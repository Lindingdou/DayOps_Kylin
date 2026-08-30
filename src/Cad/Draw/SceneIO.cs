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
    }

    public static string Save(Scene scene)
    {
        var list = new List<Dto>();
        foreach (var e in scene.Entities)
        {
            var c = new[] { e.Cr, e.Cg, e.Cb };
            switch (e)
            {
                case LineEntity l: list.Add(new Dto { T = "line", N = new[] { l.X0, l.Y0, l.X1, l.Y1 }, C = c }); break;
                case CircleEntity ci: list.Add(new Dto { T = "circle", N = new[] { ci.Cx, ci.Cy, ci.Radius }, C = c }); break;
                case RectEntity r: list.Add(new Dto { T = "rect", N = new[] { r.X0, r.Y0, r.X1, r.Y1 }, C = c }); break;
                case PointEntity p: list.Add(new Dto { T = "point", N = new[] { p.X, p.Y }, C = c }); break;
                case ArcEntity a: list.Add(new Dto { T = "arc", N = new[] { a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3 }, C = c }); break;
                case PolylineEntity pl:
                    var pts = new List<double[]>();
                    foreach (var pt in pl.Points) pts.Add(new[] { pt.x, pt.y });
                    list.Add(new Dto { T = "poly", P = pts, Closed = pl.Closed, C = c });
                    break;
            }
        }
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
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
                "poly" => BuildPoly(d),
                _ => null
            };
            if (e == null) continue;
            if (d.C is { Length: >= 3 }) { e.Cr = d.C[0]; e.Cg = d.C[1]; e.Cb = d.C[2]; }
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
