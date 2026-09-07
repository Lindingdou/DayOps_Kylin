using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 现状见煤点的图面标记(逐字移植原 MeshEditLib.ModelUpdate.CurrentStatePointMarker): 粗十字 + 粗竖杆 + 引线牵出的两行文字(高程 / 煤层·层位)。
///
/// 「粗」不能靠线宽 —— 线没有逐实体线宽, 细线在花花的底图上看不见。所以十字和竖杆都用【有真实宽度的三角面条】画:
/// 十字 = 地面上两条斜向粗条, 竖杆 = 两片互相垂直的竖直粗条(任何视角都有一面朝着观察者)。
/// 「显眼」靠两件事: ① 亮色(顶板=亮青 #00E5FF / 底板=橙红 #FF3D00); ② 深色描边 —— 同形状先画一层更宽的近黑色垫底,
/// 亮色叠在上面。垫底与亮层错开: 地面十字靠 Z 差开, 竖杆靠"亮层沿 X/Y、垫底沿对角线"错开, 不 z-fight。
/// 原输出 PMBI(三角网 + 点 + 带引线文字) → 此处输出场景实体(三角网 MeshEntity 三张 + 可捕捉 PointEntity + 引线 + 文字)。
/// </summary>
public static class CurrentStatePointMarker
{
    public const string Layer = "现状见煤点标注";

    /// <summary>一个待标注的见煤点。</summary>
    public sealed class Item
    {
        public double X, Y, Z;
        public string? SeamName;
        public string? Horizon;
    }

    private static bool IsFloor(string? horizon) => horizon == "底板";

    // 亮色: 顶板=亮青、底板=橙红; 垫底=近黑
    public static readonly (byte r, byte g, byte b) RoofRgb = (0x00, 0xE5, 0xFF);
    public static readonly (byte r, byte g, byte b) FloorRgb = (0xFF, 0x3D, 0x00);
    public static readonly (byte r, byte g, byte b) HaloRgb = (0x0D, 0x0D, 0x0D);

    /// <summary>三张标记网的顶点/三角(垫底 / 顶板亮层 / 底板亮层), 世界坐标。</summary>
    public sealed class MarkerMeshes
    {
        public List<double> HaloV = new(); public List<int> HaloT = new();
        public List<double> RoofV = new(); public List<int> RoofT = new();
        public List<double> FloorV = new(); public List<int> FloorT = new();
        public double Arm, Pole, Off, Size;
    }

    /// <param name="size">标记基准尺寸(m): 文字字高 = size, 十字臂长 ≈ 0.75×size, 竖杆高 ≈ 1.6×size。</param>
    public static MarkerMeshes BuildMeshes(IReadOnlyList<Item> items, double size)
    {
        double s = Math.Max(0.1, size);
        double arm = s * 0.75;          // 十字臂长
        double pole = s * 1.6;          // 竖杆高
        double wBright = s * 0.075;     // 亮层半宽
        double wHalo = s * 0.145;       // 垫底半宽(更宽 → 形成描边)
        double zHalo = s * 0.02, zBright = s * 0.05;
        double off = s * 1.4;           // 文字锚点相对落点的平面偏移(引线斜着牵出去)

        var m = new MarkerMeshes { Arm = arm, Pole = pole, Off = off, Size = s };
        foreach (var it in items)
        {
            bool floor = IsFloor(it.Horizon);
            var bv = floor ? m.FloorV : m.RoofV;
            var bt = floor ? m.FloorT : m.RoofT;
            double x = it.X, y = it.Y, z = it.Z;

            // ① 地面粗十字(斜向 ×): 垫底更宽、稍低; 亮层更窄、稍高 → Z 差开, 不 z-fight
            AddBarXY(m.HaloV, m.HaloT, x - arm, y - arm, x + arm, y + arm, z + zHalo, wHalo);
            AddBarXY(m.HaloV, m.HaloT, x - arm, y + arm, x + arm, y - arm, z + zHalo, wHalo);
            AddBarXY(bv, bt, x - arm, y - arm, x + arm, y + arm, z + zBright, wBright);
            AddBarXY(bv, bt, x - arm, y + arm, x + arm, y - arm, z + zBright, wBright);

            // ② 竖杆: 亮层沿 X/Y 两片, 垫底沿两条对角线且更宽更高 → 不共面, 任何视角都是一根粗杆
            AddPoleDiag(m.HaloV, m.HaloT, x, y, z, z + pole * 1.06, wHalo);
            AddPoleAxis(bv, bt, x, y, z, z + pole, wBright);
        }
        return m;
    }

    /// <summary>整批标记 → 场景实体(图层 <see cref="Layer"/>): 先垫底网, 再顶板/底板亮网, 每点一个可捕捉点 + 引线 + 两行文字。</summary>
    public static List<SceneEntity> Build(IReadOnlyList<Item> items, double size)
    {
        var ents = new List<SceneEntity>();
        if (items.Count == 0) return ents;
        var m = BuildMeshes(items, size);
        double s = m.Size;
        AddMesh(ents, "见煤点标注·垫底", m.HaloV, m.HaloT, HaloRgb);
        AddMesh(ents, "见煤点标注·顶板", m.RoofV, m.RoofT, RoofRgb);
        AddMesh(ents, "见煤点标注·底板", m.FloorV, m.FloorT, FloorRgb);

        foreach (var it in items)
        {
            bool floor = IsFloor(it.Horizon);
            var (r, g, b) = floor ? FloorRgb : RoofRgb;
            float cr = r / 255f, cg = g / 255f, cb = b / 255f;
            double x = it.X, y = it.Y, z = it.Z;

            ents.Add(new PointEntity { X = x, Y = y, Elevation = z, Size = s * 0.3, Style = 0, LayerName = Layer, Cr = cr, Cg = cg, Cb = cb });   // 可捕捉的点

            // 文字牵到旁边、引线连回落点(不压住落点本身)
            double tx = x + m.Off, ty = y + m.Off, tz = z + m.Pole;
            string label = floor ? "底板" : "顶板";
            if (!string.IsNullOrWhiteSpace(it.SeamName)) label = it.SeamName + "·" + label;
            ents.Add(new PolylineEntity { Points = { (x, y), (tx, ty) }, Zs = new List<double> { z, tz }, LayerName = Layer, Cr = cr, Cg = cg, Cb = cb });   // 引线
            ents.Add(new TextEntity { X = tx, Y = ty, Height = s * 0.8, HAlign = 0, VAlign = 2, Text = label, Elevation = tz, LayerName = Layer, Cr = cr, Cg = cg, Cb = cb });
            ents.Add(new TextEntity
            {
                X = tx, Y = ty, Height = s, HAlign = 0, VAlign = 2, Elevation = tz + s * 1.1,
                Text = "Z=" + z.ToString("F2", CultureInfo.InvariantCulture), LayerName = Layer, Cr = cr, Cg = cg, Cb = cb,
            });
        }
        return ents;
    }

    private static void AddMesh(List<SceneEntity> ents, string name, List<double> v, List<int> t, (byte r, byte g, byte b) rgb)
    {
        if (t.Count == 0) return;
        var verts = new List<(double x, double y, double z)>(v.Count / 3);
        for (int i = 0; i + 2 < v.Count; i += 3) verts.Add((v[i], v[i + 1], v[i + 2]));
        var tris = new List<(int a, int b, int c)>(t.Count / 3);
        for (int i = 0; i + 2 < t.Count; i += 3) tris.Add((t[i], t[i + 1], t[i + 2]));
        ents.Add(new MeshEntity(name, verts, tris) { LayerName = Layer, Cr = rgb.r / 255f, Cg = rgb.g / 255f, Cb = rgb.b / 255f });
    }

    /// <summary>水平面内的粗条: 沿 (x0,y0)→(x1,y1), 总宽 2×halfW。</summary>
    private static void AddBarXY(List<double> v, List<int> t,
        double x0, double y0, double x1, double y1, double z, double halfW)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return;
        double nx = -dy / len * halfW, ny = dx / len * halfW;
        Quad(v, t, x0 + nx, y0 + ny, z, x1 + nx, y1 + ny, z, x1 - nx, y1 - ny, z, x0 - nx, y0 - ny, z);
    }

    /// <summary>竖杆(亮层): 两片竖直面, 分别沿 X、Y 轴。</summary>
    private static void AddPoleAxis(List<double> v, List<int> t, double x, double y, double z0, double z1, double halfW)
    {
        VQuad(v, t, x - halfW, y, x + halfW, y, z0, z1);
        VQuad(v, t, x, y - halfW, x, y + halfW, z0, z1);
    }

    /// <summary>竖杆(垫底): 两片竖直面沿两条对角线 —— 与亮层不共面, 避免 z-fight。</summary>
    private static void AddPoleDiag(List<double> v, List<int> t, double x, double y, double z0, double z1, double halfW)
    {
        double d = halfW * 0.7071;
        VQuad(v, t, x - d, y - d, x + d, y + d, z0, z1);
        VQuad(v, t, x - d, y + d, x + d, y - d, z0, z1);
    }

    private static void VQuad(List<double> v, List<int> t,
        double ax, double ay, double bx, double by, double z0, double z1)
        => Quad(v, t, ax, ay, z0, bx, by, z0, bx, by, z1, ax, ay, z1);

    private static void Quad(List<double> v, List<int> t,
        double x0, double y0, double z0, double x1, double y1, double z1,
        double x2, double y2, double z2, double x3, double y3, double z3)
    {
        int b = v.Count / 3;
        v.Add(x0); v.Add(y0); v.Add(z0);
        v.Add(x1); v.Add(y1); v.Add(z1);
        v.Add(x2); v.Add(y2); v.Add(z2);
        v.Add(x3); v.Add(y3); v.Add(z3);
        t.Add(b); t.Add(b + 1); t.Add(b + 2);
        t.Add(b); t.Add(b + 2); t.Add(b + 3);
    }
}
