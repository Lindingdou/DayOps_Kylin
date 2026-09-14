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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double[]? V { get; set; }    // 三角网顶点 x,y,z 扁平
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int[]? I { get; set; }       // 三角网三角索引 a,b,c 扁平
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double? E { get; set; }    // 实体标高(null=0)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float[]? Pc { get; set; }    // 点云逐点色 r,g,b 扁平(null=全份基色)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float[]? Mc { get; set; }    // 三角网逐顶点色(着色结果; 原版「随工程持久化」)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float[]? Mr { get; set; }    // 三角网逐顶点真实色(建面时自点云带来)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double?[]? Dv { get; set; }   // 标注的逐实体覆盖(null=未覆盖; NaN 不是合法 JSON, 故用可空)
        // ── 只出现在撤销快照(Snapshot)里、落盘存档(Save/SaveDoc)永远不写 ──
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long[]? K { get; set; }       // 点云重数据 key [点, 逐点色, 真实色, 法向](0=无)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? Src { get; set; }     // 点云源文件路径
    }

    private sealed class LayerDto
    {
        public string N { get; set; } = "0";               // 图层名
        public float[] C { get; set; } = { 0.86f, 0.9f, 0.6f };
        public bool V { get; set; } = true;                // Visible
        public bool F { get; set; }                        // Frozen
        public bool K { get; set; }                        // Locked
        // 图层特性管理器新增四列; 旧档缺这些字段时按默认值(默认线宽/不透明/打印/无说明)读回
        // Lw 不加 WhenWritingDefault: 那条按"类型默认 0"省略, 而 0 是合法线宽(0.00mm), 省了读回会变成 -3
        public short Lw { get; set; } = -3;                                                                  // 线宽(-3=默认)
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public short Tr { get; set; }        // 透明度(0=不透明)
        public bool P { get; set; } = true;                                                                  // Plottable
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? D { get; set; }       // 说明
    }

    private sealed class Doc
    {
        public List<LayerDto> L { get; set; } = new();
        public string Cur { get; set; } = "0";
        public List<Dto> E { get; set; } = new();
    }

    /// <summary>一层的持久化状态（LoadDoc 回传，供 LayerTable 恢复）。</summary>
    public readonly record struct LayerState(string Name, float Cr, float Cg, float Cb, bool Visible, bool Frozen, bool Locked,
                                            short LineWeight = -3, short Transparency = 0, bool Plottable = true, string? Description = null);

    /// <summary>LoadDoc 结果：场景 + 图层表状态 + 当前层名（旧格式时图层列表为空、当前层 "0"）。</summary>
    public sealed class LoadedDoc
    {
        public Scene Scene { get; init; } = new();
        public List<LayerState> Layers { get; init; } = new();
        public string Current { get; init; } = "0";
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // ── 实体 ⇄ DTO ────────────────────────────────────────────────
    // heavy != null 是撤销快照模式：点云列表不逐点写进 DTO，只登记到仓里记 key（见 Snapshot）。存档永远传 null。
    private static List<Dto> ToDtos(Scene scene, SnapshotHeavyStore? heavy = null)
    {
        var list = new List<Dto>();
        foreach (var e in scene.Entities)
        {
            Dto? d = e switch
            {
                LineEntity l => new Dto { T = "line", N = new[] { l.X0, l.Y0, l.X1, l.Y1 } },
                CircleEntity ci => new Dto { T = "circle", N = new[] { ci.Cx, ci.Cy, ci.Radius } },
                RectEntity r => new Dto { T = "rect", N = new[] { r.X0, r.Y0, r.X1, r.Y1 } },
                PointEntity p => new Dto { T = "point", N = new[] { p.X, p.Y, p.Size, p.Style } },   // 含点大小/样式
                ArcEntity a => new Dto { T = "arc", N = new[] { a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3 } },
                PolygonEntity pg => new Dto { T = "polygon", N = new[] { pg.Cx, pg.Cy, pg.Radius, pg.Rotation, pg.Sides } },
                TextEntity tx => new Dto { T = "text", N = new[] { tx.X, tx.Y, tx.Height, tx.Rotation }, S = tx.Text },
                PolylineEntity pl => PolyDto(pl),
                MeshEntity me => MeshDto(me),
                PointCloudEntity pc => heavy != null ? CloudRefDto(pc, heavy) : CloudDto(pc),
                HatchEntity ha => HatchDto(ha),
                DimensionEntity dm => DimDto(dm),
                _ => null
            };
            if (d == null) continue;
            d.C = new[] { e.Cr, e.Cg, e.Cb };
            d.L = e.LayerName;
            d.D = e.Dash;                                              // 线型
            d.W = e.LineWeight == -1 ? (short?)null : e.LineWeight;    // 线宽(ByLayer 省略)
            d.Tr = e.Transparency == -1 ? (short?)null : e.Transparency;   // 透明度(随层省略)
            d.H = !e.Visible;                                          // 隐藏
            // 标高必须存：ZAt = Zs[i] + Elevation，只存 Zs 的话三维线一存一读就掉基准，
            // 平面线(等高线/台阶线)更是整条塌回 0 —— 撤销/重做同走这条路，所以撤销也会把标高抹掉。
            d.E = e.Elevation != 0 ? e.Elevation : (double?)null;      // 标高(0 省略)
            if (e is TextEntity txe)                                   // 文字格式
            {
                if (txe.WidthFactor != 1) d.Wf = txe.WidthFactor;
                d.Ob = txe.ObliqueAngle; d.Ha = txe.HAlign; d.Va = txe.VAlign;
            }
            list.Add(d);
        }
        return list;
    }

    private static void FromDtos(List<Dto>? list, Scene scene, SnapshotHeavyStore? heavy = null)
    {
        if (list == null) return;
        foreach (var d in list)
        {
            SceneEntity? e = d.T switch
            {
                "cloud" when d.K != null && heavy != null => BuildCloudRef(d, heavy),   // 撤销快照：列表按 key 取回，不逐点重建
                "line" when d.N.Length >= 4 => new LineEntity { X0 = d.N[0], Y0 = d.N[1], X1 = d.N[2], Y1 = d.N[3] },
                "circle" when d.N.Length >= 3 => new CircleEntity { Cx = d.N[0], Cy = d.N[1], Radius = d.N[2] },
                "rect" when d.N.Length >= 4 => new RectEntity { X0 = d.N[0], Y0 = d.N[1], X1 = d.N[2], Y1 = d.N[3] },
                "point" when d.N.Length >= 2 => new PointEntity { X = d.N[0], Y = d.N[1], Size = d.N.Length >= 3 ? d.N[2] : 0.5, Style = d.N.Length >= 4 ? (int)d.N[3] : 2 },
                "arc" when d.N.Length >= 6 => new ArcEntity { X1 = d.N[0], Y1 = d.N[1], X2 = d.N[2], Y2 = d.N[3], X3 = d.N[4], Y3 = d.N[5] },
                "polygon" when d.N.Length >= 5 => new PolygonEntity { Cx = d.N[0], Cy = d.N[1], Radius = d.N[2], Rotation = d.N[3], Sides = (int)d.N[4] },
                "text" when d.N.Length >= 3 => new TextEntity { X = d.N[0], Y = d.N[1], Height = d.N[2], Rotation = d.N.Length >= 4 ? d.N[3] : 0, Text = d.S ?? "" },
                "poly" => BuildPoly(d),
                "mesh" when d.V != null && d.I != null => BuildMesh(d),
                "cloud" when d.V != null => BuildCloud(d),
                "hatch" when d.P != null => BuildHatch(d),
                "dim" when d.N.Length >= 12 => BuildDim(d),   // 12 = 几何最少位数(旗标缺省按可见)
                _ => null
            };
            if (e == null) continue;
            if (d.C is { Length: >= 3 }) { e.Cr = d.C[0]; e.Cg = d.C[1]; e.Cb = d.C[2]; }
            e.LayerName = string.IsNullOrEmpty(d.L) ? "0" : d.L;
            e.Dash = d.D;                              // 线型
            e.LineWeight = d.W ?? -1;                  // 线宽(缺=ByLayer)
            e.Transparency = d.Tr ?? -1;              // 透明度(缺=随层)
            e.Visible = !d.H;                          // 隐藏
            // 只在字段存在时覆盖：老档案里三角网的标高存在 N[0]，BuildMesh 已经读过了，别用 0 顶掉
            if (d.E.HasValue) e.Elevation = d.E.Value;  // 标高
            if (e is TextEntity txe)                   // 文字格式
            {
                txe.WidthFactor = d.Wf ?? 1; txe.ObliqueAngle = d.Ob; txe.HAlign = d.Ha; txe.VAlign = d.Va;
            }
            scene.Add(e);
        }
    }

    // ── 撤销快照：实体数组，但点云列表按引用进重数据仓（不落盘） ────────
    /// <summary>
    /// 撤销/重做用的场景快照。与 <see cref="Save"/> 同一份 DTO，唯独点云的 点/逐点色/真实色/法向 四个列表
    /// 不逐点写 JSON，而是按引用登记到 <paramref name="heavy"/> 里、DTO 只记 key（列表创建后从不原地改，引用即内容）。
    /// 200 万点的点云：Save 是上百 MB 字符串，这里是几百字节。顺带真实色与法向缓存也随撤销保住了
    /// （旧快照不存它们，撤销一次真实色就丢）。
    /// </summary>
    public static UndoManager.Snapshot Snapshot(Scene scene, SnapshotHeavyStore heavy)
    {
        var dtos = ToDtos(scene, heavy);
        var keys = new List<long>();
        foreach (var d in dtos) if (d.K != null) foreach (var k in d.K) if (k != 0) keys.Add(k);
        return new UndoManager.Snapshot(JsonSerializer.Serialize(dtos, Opts), keys.ToArray());
    }

    /// <summary>从撤销快照重建场景（点云列表从 <paramref name="heavy"/> 取回，与快照时是同一批对象）。</summary>
    public static Scene Restore(UndoManager.Snapshot snapshot, SnapshotHeavyStore heavy)
    {
        var scene = new Scene();
        if (string.IsNullOrWhiteSpace(snapshot.Json)) return scene;
        FromDtos(JsonSerializer.Deserialize<List<Dto>>(snapshot.Json), scene, heavy);
        return scene;
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
            doc.L.Add(new LayerDto { N = l.Name, C = new[] { l.Cr, l.Cg, l.Cb }, V = l.Visible, F = l.Frozen, K = l.Locked,
                                     Lw = l.LineWeight, Tr = l.Transparency, P = l.Plottable, D = string.IsNullOrEmpty(l.Description) ? null : l.Description });
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
            layers.Add(new LayerState(l.N, r, g, b, l.V, l.F, l.K, l.Lw, l.Tr, l.P, l.D));
        }
        return new LoadedDoc { Scene = scene, Layers = layers, Current = string.IsNullOrEmpty(doc.Cur) ? "0" : doc.Cur };
    }

    // 填充: 存「边界 + 图案名 + 比例/角度/十字」, 图案线是读回来重算的(它就是个参数化对象, 不存几千根线)
    // 标注(§三二七): 只存**定义参数**, 几何读回后现推 —— 存线段的话, 改了字高/箭头再打开还是老样子。
    // N = 几何与基准(恒有); Dv = 逐实体覆盖(可空, null 即未覆盖 —— NaN 不是合法 JSON, 不能拿它当哨兵);
    // S = 文字内容覆盖。旧档没有 dim 这一型, 不存在兼容问题。
    private static Dto DimDto(DimensionEntity m) => new()
    {
        T = "dim",
        N = new[]
        {
            (double)(int)m.Kind, m.X1, m.Y1, m.X2, m.Y2, m.OffX, m.OffY,
            m.Cx, m.Cy, m.Radius, m.DirX, m.DirY, m.BaseHeight,
            m.ExtLine1Visible ? 1 : 0, m.ExtLine2Visible ? 1 : 0, m.DimLineVisible ? 1 : 0,
        },
        S = m.TextOverride,
        Dv = new double?[]
        {
            m.ArrowSize, m.ExtLineOffset, m.ExtLineExtension, m.TextHeight, m.TextOffset,
            m.TextPosX, m.TextPosY, m.DecimalPlaces,
            m.DimLineColor?.r, m.DimLineColor?.g, m.DimLineColor?.b,
            m.ExtLineColor?.r, m.ExtLineColor?.g, m.ExtLineColor?.b,
            m.TextColor?.r, m.TextColor?.g, m.TextColor?.b,
        },
    };

    private static (float r, float g, float b)? ColFrom(double?[]? v, int i)
        => v != null && v.Length > i + 2 && v[i] is { } r && v[i + 1] is { } g && v[i + 2] is { } b
            ? ((float)r, (float)g, (float)b) : null;

    private static SceneEntity BuildDim(Dto d)
    {
        var n = d.N;
        var v = d.Dv;
        double? Opt(int i) => v != null && v.Length > i ? v[i] : null;
        bool Flag(int i, bool dflt) => i < n.Length ? n[i] != 0 : dflt;
        var m = new DimensionEntity
        {
            Kind = (DimensionEntity.DimKind)(int)n[0],
            X1 = n[1], Y1 = n[2], X2 = n[3], Y2 = n[4], OffX = n[5], OffY = n[6],
            Cx = n[7], Cy = n[8], Radius = n[9], DirX = n[10], DirY = n[11],
            BaseHeight = n.Length > 12 ? n[12] : 2.0,
            ExtLine1Visible = Flag(13, true), ExtLine2Visible = Flag(14, true), DimLineVisible = Flag(15, true),
            ArrowSize = Opt(0), ExtLineOffset = Opt(1), ExtLineExtension = Opt(2),
            TextHeight = Opt(3), TextOffset = Opt(4), TextPosX = Opt(5), TextPosY = Opt(6),
            TextOverride = string.IsNullOrEmpty(d.S) ? null : d.S,
            DimLineColor = ColFrom(v, 8), ExtLineColor = ColFrom(v, 11), TextColor = ColFrom(v, 14),
        };
        if (Opt(7) is { } dp) m.DecimalPlaces = (int)dp;
        return m;
    }

    private static Dto HatchDto(HatchEntity h) => new()
    {
        T = "hatch",
        S = h.PatternName,
        N = new[] { h.Scale, h.Angle, h.Cross ? 1.0 : 0.0, h.OriginX, h.OriginY },
        P = h.Boundary.ConvertAll(p => new[] { p.x, p.y }),
        Closed = true,
    };

    private static SceneEntity BuildHatch(Dto d)
    {
        var h = new HatchEntity
        {
            PatternName = string.IsNullOrWhiteSpace(d.S) ? "ANSI31" : d.S!,
            Scale = d.N.Length > 0 ? d.N[0] : 0,
            Angle = d.N.Length > 1 ? d.N[1] : 0,
            Cross = d.N.Length > 2 && d.N[2] != 0,
            OriginX = d.N.Length > 3 ? d.N[3] : 0,
            OriginY = d.N.Length > 4 ? d.N[4] : 0,
        };
        if (d.P != null)
            foreach (var p in d.P)
                if (p.Length >= 2) h.Boundary.Add((p[0], p[1]));
        return h;
    }

    private static Dto PolyDto(PolylineEntity pl)
    {
        var pts = new List<double[]>();
        bool z3 = pl.Has3D;
        for (int i = 0; i < pl.Points.Count; i++)
            pts.Add(z3 ? new[] { pl.Points[i].x, pl.Points[i].y, pl.Zs![i] } : new[] { pl.Points[i].x, pl.Points[i].y });
        return new Dto { T = "poly", P = pts, Closed = pl.Closed };
    }

    private static PolylineEntity BuildPoly(Dto d)
    {
        var pl = new PolylineEntity { Closed = d.Closed };
        if (d.P != null)
        {
            bool all3 = d.P.Count > 0;
            foreach (var p in d.P) { if (p.Length >= 2) pl.Points.Add((p[0], p[1])); if (p.Length < 3) all3 = false; }
            if (all3) { pl.Zs = new List<double>(); foreach (var p in d.P) if (p.Length >= 3) pl.Zs.Add(p[2]); }
        }
        return pl;
    }

    // ── 三角网 DTO(顶点/索引扁平数组, 名称存 S) ─────────────────────────
    private static Dto MeshDto(MeshEntity me)
    {
        var v = new double[me.Verts.Count * 3];
        for (int i = 0; i < me.Verts.Count; i++) { v[i * 3] = me.Verts[i].x; v[i * 3 + 1] = me.Verts[i].y; v[i * 3 + 2] = me.Verts[i].z; }
        var idx = new int[me.Tris.Count * 3];
        for (int i = 0; i < me.Tris.Count; i++) { idx[i * 3] = me.Tris[i].a; idx[i * 3 + 1] = me.Tris[i].b; idx[i * 3 + 2] = me.Tris[i].c; }
        return new Dto
        {
            T = "mesh", V = v, I = idx, S = me.Name, N = new[] { me.Elevation },
            Mc = FlatColors(me.VertColors, me.Verts.Count),
            Mr = FlatColors(me.RgbColors, me.Verts.Count),
        };
    }

    /// <summary>逐顶点色 → 扁平 r,g,b 数组；个数对不上或为空时返回 null（不存）。</summary>
    private static float[]? FlatColors(List<(float r, float g, float b)>? cols, int n)
    {
        if (cols == null || cols.Count != n || n == 0) return null;
        var a = new float[n * 3];
        for (int i = 0; i < n; i++) { a[i * 3] = cols[i].r; a[i * 3 + 1] = cols[i].g; a[i * 3 + 2] = cols[i].b; }
        return a;
    }

    /// <summary>扁平 r,g,b → 逐顶点色；长度不足返回 null。</summary>
    private static List<(float r, float g, float b)>? UnflatColors(float[]? a, int n)
    {
        if (a == null || n == 0 || a.Length < n * 3) return null;
        var l = new List<(float, float, float)>(n);
        for (int i = 0; i < n; i++) l.Add((a[i * 3], a[i * 3 + 1], a[i * 3 + 2]));
        return l;
    }

    // ── 点云 DTO(点 x,y,z 扁平存 V; 逐点色扁平存 Pc; 名称存 S) ────────────
    // 法向缓存(Normals)不存档: 它是可再算的派生缓存(重开工程按需重估), 存下来会让工程文件翻倍。
    private static Dto CloudDto(PointCloudEntity pc)
    {
        var v = new double[pc.Pts.Count * 3];
        for (int i = 0; i < pc.Pts.Count; i++) { v[i * 3] = pc.Pts[i].x; v[i * 3 + 1] = pc.Pts[i].y; v[i * 3 + 2] = pc.Pts[i].z; }
        float[]? cols = null;
        if (pc.HasColors)
        {
            cols = new float[pc.Pts.Count * 3];
            for (int i = 0; i < pc.Pts.Count; i++) { var c = pc.Colors![i]; cols[i * 3] = c.r; cols[i * 3 + 1] = c.g; cols[i * 3 + 2] = c.b; }
        }
        return new Dto { T = "cloud", V = v, Pc = cols, S = pc.Name, N = new[] { pc.Elevation, pc.PointPixels } };
    }

    private static PointCloudEntity BuildCloud(Dto d)
    {
        var pc = new PointCloudEntity { Name = string.IsNullOrEmpty(d.S) ? "点云" : d.S! };
        var v = d.V!;
        for (int i = 0; i + 2 < v.Length; i += 3) pc.Pts.Add((v[i], v[i + 1], v[i + 2]));
        if (d.Pc != null && d.Pc.Length >= pc.Pts.Count * 3)
        {
            pc.Colors = new List<(float, float, float)>(pc.Pts.Count);
            for (int i = 0; i < pc.Pts.Count; i++) pc.Colors.Add((d.Pc[i * 3], d.Pc[i * 3 + 1], d.Pc[i * 3 + 2]));
        }
        if (d.N.Length >= 1) pc.Elevation = d.N[0];
        if (d.N.Length >= 2 && d.N[1] > 0) pc.PointPixels = (float)d.N[1];
        return pc;
    }

    // ── 点云撤销快照 DTO：四个列表只记重数据仓 key(K=[点,逐点色,真实色,法向], 0=无)，名称/标高/点径/源路径照常 ──
    private static Dto CloudRefDto(PointCloudEntity pc, SnapshotHeavyStore heavy) => new()
    {
        T = "cloud", S = pc.Name, Src = pc.Source, N = new[] { pc.Elevation, pc.PointPixels, (double)pc.SourceTotalPoints },
        K = new[]
        {
            heavy.Put(pc.Pts),
            pc.Colors != null ? heavy.Put(pc.Colors) : 0,
            pc.RgbColors != null ? heavy.Put(pc.RgbColors) : 0,
            pc.Normals != null ? heavy.Put(pc.Normals) : 0,
        },
    };

    /// <summary>按 key 从仓里取回列表拼成点云；点列表已被清扫(不该发生)则退回一份空点云，别让整次撤销炸掉。</summary>
    private static PointCloudEntity BuildCloudRef(Dto d, SnapshotHeavyStore heavy)
    {
        var k = d.K!;
        var pc = new PointCloudEntity { Name = string.IsNullOrEmpty(d.S) ? "点云" : d.S!, Source = d.Src };
        if (k.Length >= 1) pc.Pts = heavy.Get<List<(double x, double y, double z)>>(k[0]) ?? new();
        if (k.Length >= 2 && k[1] != 0) pc.Colors = heavy.Get<List<(float r, float g, float b)>>(k[1]);
        if (k.Length >= 3 && k[2] != 0) pc.RgbColors = heavy.Get<List<(float r, float g, float b)>>(k[2]);
        if (k.Length >= 4 && k[3] != 0) pc.Normals = heavy.Get<List<(double x, double y, double z)>>(k[3]);
        if (d.N.Length >= 1) pc.Elevation = d.N[0];
        if (d.N.Length >= 2 && d.N[1] > 0) pc.PointPixels = (float)d.N[1];
        if (d.N.Length >= 3) pc.SourceTotalPoints = (long)d.N[2];   // 抽样封顶时的源文件全量点数(撤销回来仍能回源文件全量算)
        return pc;
    }

    private static MeshEntity BuildMesh(Dto d)
    {
        var me = new MeshEntity { Name = string.IsNullOrEmpty(d.S) ? "三角网" : d.S! };
        var v = d.V!; var idx = d.I!;
        for (int i = 0; i + 2 < v.Length; i += 3) me.Verts.Add((v[i], v[i + 1], v[i + 2]));
        for (int i = 0; i + 2 < idx.Length; i += 3) me.Tris.Add((idx[i], idx[i + 1], idx[i + 2]));
        if (d.N.Length >= 1) me.Elevation = d.N[0];
        me.VertColors = UnflatColors(d.Mc, me.Verts.Count);
        me.RgbColors = UnflatColors(d.Mr, me.Verts.Count);
        return me;
    }
}
