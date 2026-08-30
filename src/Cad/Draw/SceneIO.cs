using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 绘制场景的内部格式读写（JSON）。用扁平 DTO 表示多态实体，避免多态 JSON 复杂度。
/// 纯逻辑、可单测（Save→Load round-trip）。对应 Home 文件的新建/打开/保存。
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
    }

    public static string Save(Scene scene)
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
            list.Add(d);
        }
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dto PolyDto(PolylineEntity pl)
    {
        var pts = new List<double[]>();
        foreach (var pt in pl.Points) pts.Add(new[] { pt.x, pt.y });
        return new Dto { T = "poly", P = pts, Closed = pl.Closed };
    }

    public static Scene Load(string json)
    {
        var scene = new Scene();
        var list = JsonSerializer.Deserialize<List<Dto>>(json);
        if (list == null) return scene;
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
            scene.Add(e);
        }
        return scene;
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
