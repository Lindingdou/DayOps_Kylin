using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 绘制场景的内部格式读写（JSON）。用扁平 DTO 表示多态实体，避免多态 JSON 复杂度。
/// 纯逻辑、可单测（Save→Load round-trip）。对应 Home 文件的新建/打开/保存。
///
/// 两种顶层格式：
///   · 旧：实体数组 `[ {T,N,...}, ... ]`（Save 输出，兼容既有 .pmx）；
///   · 新：文档对象 `{ "L":[图层], "Cur":当前层, "E":[实体] }`（SaveDoc 输出，含图层表状态）。
/// Load/LoadDoc 均按首字符 `[`/`{` 自动识别，二者都能读两种格式。
/// </summary>
public static class SceneIO
{
    private sealed class Dto
    {
        public string T { get; set; } = "";
        public double[] N { get; set; } = Array.Empty<double>();
        public List<double[]>? P { get; set; }
        public bool Closed { get; set; }
        public float[] C { get; set; } = { 0.86f, 0.9f, 0.6f };
        public string L { get; set; } = "0";   // 图层名
        public string? S { get; set; }          // 文字内容
        // ── 属性保真(缺省省略 → 旧 .pmx 兼容; 缺字段回退默认) ──
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double[]? D { get; set; }   // 线型虚线样式(null=实线)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public short? W { get; set; }       // 线宽(null=ByLayer -1)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public short? Tr { get; set; }      // 透明度(null=随层 -1)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool H { get; set; }         // 隐藏(false=可见)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double? Wf { get; set; }     // 文字字宽比(null=1)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Ob { get; set; }      // 文字倾斜角(0)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Ha { get; set; }         // 文字水平对齐(0=左)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Va { get; set; }         // 文字垂直对齐(0=基线)
    }

    private sealed class LayerDto
    {
        public string N { get; set; } = "0";               // 图层名
        public float[] C { get; set; } = { 0.86f, 0.9f, 0.6f };
        public bool V { get; set; } = true;                // Visible
        public bool F { get; set; }                        // Frozen
        public bool K { get; set; }                        // Locked
    }

    private sealed class Doc
    {
        public List<LayerDto> L { get; set; } = new();
        public string Cur { get; set; } = "0";
        public List<Dto> E { get; set; } = new();
    }

    /// <summary>一层的持久化状态（LoadDoc 回传，供 LayerTable 恢复）。</summary>
    public readonly record struct LayerState(string Name, float Cr, float Cg, float Cb, bool Visible, bool Frozen, bool Locked);

    /// <summary>LoadDoc 结果：场景 + 图层表状态 + 当前层名（旧格式时图层列表为空、当前层 "0"）。</summary>
    public sealed class LoadedDoc
    {
        public Scene Scene { get; init; } = new();
        public List<LayerState> Layers { get; init; } = new();
        public string Current { get; init; } = "0";
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // ── 实体 ⇄ DTO ────────────────────────────────────────────────
    private static List<Dto> ToDtos(Scene scene)
    {
        var list = new List<Dto>();
        foreach (var e in scene.Entities)
        {
            Dto? d = e switch
            {
                LineEntity l => new Dto { T = "line", N = new[] { l.X0, l.Y0, l.X1, l.Y1 } },
                CircleEntity ci => new Dto { T = "circle", N = new[] { ci.Cx, ci.Cy, ci.Radius } },
                RectEntity r => new Dto { T = "rect", N = new[] { r.X0, r.Y0, r.X1, r.Y1 } },
                PointEntity p => new Dto { T = "point", N = new[] { p.X, p.Y } },
                ArcEntity a => new Dto { T = "arc", N = new[] { a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3 } },
                PolygonEntity pg => new Dto { T = "polygon", N = new[] { pg.Cx, pg.Cy, pg.Radius, pg.Rotation, pg.Sides } },
                TextEntity tx => new Dto { T = "text", N = new[] { tx.X, tx.Y, tx.Height, tx.Rotation }, S = tx.Text },
                PolylineEntity pl => PolyDto(pl),
                _ => null
            };
            if (d == null) continue;
            d.C = new[] { e.Cr, e.Cg, e.Cb };
            d.L = e.LayerName;
            d.D = e.Dash;                                              // 线型
            d.W = e.LineWeight == -1 ? (short?)null : e.LineWeight;    // 线宽(ByLayer 省略)
            d.Tr = e.Transparency == -1 ? (short?)null : e.Transparency;   // 透明度(随层省略)
            d.H = !e.Visible;                                          // 隐藏
            if (e is TextEntity txe)                                   // 文字格式
            {
                if (txe.WidthFactor != 1) d.Wf = txe.WidthFactor;
                d.Ob = txe.ObliqueAngle; d.Ha = txe.HAlign; d.Va = txe.VAlign;
            }
            list.Add(d);
        }
        return list;
    }

    private static void FromDtos(List<Dto>? list, Scene scene)
    {
        if (list == null) return;
        foreach (var d in list)
        {
            SceneEntity? e = d.T switch
            {
                "line" when d.N.Length >= 4 => new LineEntity { X0 = d.N[0], Y0 = d.N[1], X1 = d.N[2], Y1 = d.N[3] },
                "circle" when d.N.Length >= 3 => new CircleEntity { Cx = d.N[0], Cy = d.N[1], Radius = d.N[2] },
                "rect" when d.N.Length >= 4 => new RectEntity { X0 = d.N[0], Y0 = d.N[1], X1 = d.N[2], Y1 = d.N[3] },
                "point" when d.N.Length >= 2 => new PointEntity { X = d.N[0], Y = d.N[1] },
                "arc" when d.N.Length >= 6 => new ArcEntity { X1 = d.N[0], Y1 = d.N[1], X2 = d.N[2], Y2 = d.N[3], X3 = d.N[4], Y3 = d.N[5] },
                "polygon" when d.N.Length >= 5 => new PolygonEntity { Cx = d.N[0], Cy = d.N[1], Radius = d.N[2], Rotation = d.N[3], Sides = (int)d.N[4] },
                "text" when d.N.Length >= 3 => new TextEntity { X = d.N[0], Y = d.N[1], Height = d.N[2], Rotation = d.N.Length >= 4 ? d.N[3] : 0, Text = d.S ?? "" },
                "poly" => BuildPoly(d),
                _ => null
            };
            if (e == null) continue;
            if (d.C is { Length: >= 3 }) { e.Cr = d.C[0]; e.Cg = d.C[1]; e.Cb = d.C[2]; }
            e.LayerName = string.IsNullOrEmpty(d.L) ? "0" : d.L;
            e.Dash = d.D;                              // 线型
            e.LineWeight = d.W ?? -1;                  // 线宽(缺=ByLayer)
            e.Transparency = d.Tr ?? -1;              // 透明度(缺=随层)
            e.Visible = !d.H;                          // 隐藏
            if (e is TextEntity txe)                   // 文字格式
            {
                txe.WidthFactor = d.Wf ?? 1; txe.ObliqueAngle = d.Ob; txe.HAlign = d.Ha; txe.VAlign = d.Va;
            }
            scene.Add(e);
        }
    }

    // ── 旧格式：实体数组（保持既有 .pmx 兼容） ──────────────────────
    public static string Save(Scene scene) => JsonSerializer.Serialize(ToDtos(scene), Opts);

    /// <summary>读场景（两种格式都认）。</summary>
    public static Scene Load(string json)
    {
        var scene = new Scene();
        if (string.IsNullOrWhiteSpace(json)) return scene;
        if (json.TrimStart().StartsWith("{"))
            FromDtos(JsonSerializer.Deserialize<Doc>(json)?.E, scene);   // 新格式：取实体段
        else
            FromDtos(JsonSerializer.Deserialize<List<Dto>>(json), scene);
        return scene;
    }

    // ── 新格式：文档对象（含图层表状态） ────────────────────────────
    public static string SaveDoc(Scene scene, IReadOnlyList<Layer> layers, string current)
    {
        var doc = new Doc { Cur = current, E = ToDtos(scene) };
        foreach (var l in layers)
            doc.L.Add(new LayerDto { N = l.Name, C = new[] { l.Cr, l.Cg, l.Cb }, V = l.Visible, F = l.Frozen, K = l.Locked });
        return JsonSerializer.Serialize(doc, Opts);
    }

    /// <summary>读文档（新格式带图层；旧数组格式时图层列表为空，交调用方回退按实体重建）。</summary>
    public static LoadedDoc LoadDoc(string json)
    {
        var scene = new Scene();
        if (string.IsNullOrWhiteSpace(json)) return new LoadedDoc { Scene = scene };
        if (!json.TrimStart().StartsWith("{"))
        {
            FromDtos(JsonSerializer.Deserialize<List<Dto>>(json), scene);
            return new LoadedDoc { Scene = scene };
        }
        var doc = JsonSerializer.Deserialize<Doc>(json) ?? new Doc();
        FromDtos(doc.E, scene);
        var layers = new List<LayerState>();
        foreach (var l in doc.L)
        {
            float r = l.C.Length >= 3 ? l.C[0] : 0.86f, g = l.C.Length >= 3 ? l.C[1] : 0.9f, b = l.C.Length >= 3 ? l.C[2] : 0.6f;
            layers.Add(new LayerState(l.N, r, g, b, l.V, l.F, l.K));
        }
        return new LoadedDoc { Scene = scene, Layers = layers, Current = string.IsNullOrEmpty(doc.Cur) ? "0" : doc.Cur };
    }

    private static Dto PolyDto(PolylineEntity pl)
    {
        var pts = new List<double[]>();
        foreach (var pt in pl.Points) pts.Add(new[] { pt.x, pt.y });
        return new Dto { T = "poly", P = pts, Closed = pl.Closed };
    }

    private static PolylineEntity BuildPoly(Dto d)
    {
        var pl = new PolylineEntity { Closed = d.Closed };
        if (d.P != null)
            foreach (var p in d.P)
                if (p.Length >= 2) pl.Points.Add((p[0], p[1]));
        return pl;
    }
}
