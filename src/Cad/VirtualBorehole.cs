using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 虚拟钻孔 —— 忠实原 GeoDataBase「虚拟钻孔」核(VirtualDrillEngine.Drill / TinZSampler): 在任意 (x,y)
/// 对各煤层顶/底板三角网做**竖直求交**得顶/底板高程, 合成一根钻孔柱(逐层 顶板/底板/厚度)。
/// 复用 <see cref="TinSampler"/>(竖直求交)。曾误记"缺块模型"——实则只需煤层面 TIN(可由层位展点建), 已具备。纯逻辑、可单测。
/// </summary>
public static class VirtualBorehole
{
    public readonly record struct SeamHit(string SeamCode, double RoofZ, double FloorZ, double Thickness);

    public readonly record struct Seam(
        string Code,
        IReadOnlyList<(double x, double y, double z)> RoofPoints,
        IReadOnlyList<(double x, double y, double z)> FloorPoints);

    /// <summary>在 (qx,qy) 钻穿各煤层面：顶/底板各竖直求交, 两者都命中才算见煤。返回自顶向下排序的层。</summary>
    public static List<SeamHit> Drill(double qx, double qy, IReadOnlyList<Seam> seams)
    {
        var hits = new List<SeamHit>();
        if (seams == null) return hits;
        foreach (var s in seams)
        {
            var rz = TinSampler.SampleZ(s.RoofPoints, qx, qy);
            if (rz == null) continue;
            var fz = TinSampler.SampleZ(s.FloorPoints, qx, qy);
            if (fz == null) continue;
            hits.Add(new SeamHit(s.Code, rz.Value, fz.Value, rz.Value - fz.Value));
        }
        hits.Sort((a, b) => b.RoofZ.CompareTo(a.RoofZ));   // 顶板高者在前(自顶向下)
        return hits;
    }

    /// <summary>层位展点(SeamCode,IsRoof,x,y,z) → 分煤层顶/底板点集 → Seam 列表(供 Drill)。</summary>
    public static List<Seam> SeamsFromHorizonPoints(
        IEnumerable<(string seamCode, bool isRoof, double x, double y, double z)> pts)
    {
        var roof = new Dictionary<string, List<(double x, double y, double z)>>();
        var floor = new Dictionary<string, List<(double x, double y, double z)>>();
        foreach (var p in pts)
        {
            var map = p.isRoof ? roof : floor;
            if (!map.TryGetValue(p.seamCode, out var lst)) map[p.seamCode] = lst = new List<(double, double, double)>();
            lst.Add((p.x, p.y, p.z));
        }
        var seams = new List<Seam>();
        foreach (var kv in roof)
            if (floor.TryGetValue(kv.Key, out var fl))
                seams.Add(new Seam(kv.Key, kv.Value, fl));
        return seams;
    }

    /// <summary>合成钻孔柱 → CSV(seam,roof_z,floor_z,thickness)。</summary>
    public static string ToCsv(IReadOnlyList<SeamHit> hits)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("seam,roof_z,floor_z,thickness\n");
        foreach (var h in hits)
            sb.Append(h.SeamCode).Append(',').Append(h.RoofZ.ToString("R", inv)).Append(',')
              .Append(h.FloorZ.ToString("R", inv)).Append(',').Append(h.Thickness.ToString("R", inv)).Append('\n');
        return sb.ToString();
    }
}
