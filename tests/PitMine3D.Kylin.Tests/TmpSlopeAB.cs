using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using Xunit.Abstractions;
namespace PitMine3D.Kylin.Tests;
public class TmpSlopeAB
{
    private readonly ITestOutputHelper _o;
    public TmpSlopeAB(ITestOutputHelper o) { _o = o; }
    [Fact]
    public void AB_against_native_dump()
    {
        string las = @"C:\Users\cFore\coder\PitMine3D\测试实验\dlt_test.las";
        var r = LasImportService.Load(las, int.MaxValue);
        Assert.True(r.Success, r.Error);
        _o.WriteLine($"points {r.Points.Count} (header {r.PointCount})");
        var st = new SlopeLineExtractor.Stats();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = SlopeLineExtractor.Extract(r.Points, new SlopeLineExtractor.Options { CellSize = 1.0 }, out var crest, out var toe, st, out string err);
        Assert.True(ok, err);
        _o.WriteLine($"C# : DEM {st.DemW}x{st.DemH} crest {crest.Count} lines {st.CrestLenM:0} m | toe {toe.Count} lines {st.ToeLenM:0} m | {sw.Elapsed.TotalSeconds:0.0}s refine {st.RefinedCrestVertices}/{st.RefinedToeVertices}");
        string dir = @"C:\Users\cFore\AppData\Local\Temp\claude\C--Users-cFore-coder-DayOps-Kylin\68ff84ae-4945-43f5-a15c-a1df10c05a70\scratchpad";
        void Dump(string p, List<SlopeLineExtractor.Polyline3> v)
        {
            using var w = new StreamWriter(p);
            foreach (var l in v) { for (int i = 0; i < l.Count; i++) w.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:0.00} {1:0.00} {2:0.00}", l.Xs[i], l.Ys[i], l.Zs[i])); w.WriteLine(); }
        }
        Dump(Path.Combine(dir, "cs_crest.txt"), crest); Dump(Path.Combine(dir, "cs_toe.txt"), toe);
        // 与 native dump 对拍：读 dump_crest.txt，统计条数/总长；再看顶点集合的最近距离分布
        List<List<(double x, double y, double z)>> ReadDump(string p)
        {
            var res = new List<List<(double, double, double)>>(); var cur = new List<(double, double, double)>();
            foreach (var line in File.ReadLines(p))
            {
                if (line.Trim().Length == 0) { if (cur.Count > 0) res.Add(cur); cur = new(); continue; }
                var t = line.Split(' '); cur.Add((double.Parse(t[0], CultureInfo.InvariantCulture), double.Parse(t[1], CultureInfo.InvariantCulture), double.Parse(t[2], CultureInfo.InvariantCulture)));
            }
            if (cur.Count > 0) res.Add(cur); return res;
        }
        double Len(List<(double x, double y, double z)> l) { double s = 0; for (int i = 1; i < l.Count; i++) s += Math.Sqrt(Math.Pow(l[i].x - l[i - 1].x, 2) + Math.Pow(l[i].y - l[i - 1].y, 2)); return s; }
        var nc = ReadDump(@"C:\Users\cFore\coder\PitMine3D\测试实验\dump_crest.txt"); var nt = ReadDump(@"C:\Users\cFore\coder\PitMine3D\测试实验\dump_toe.txt");
        _o.WriteLine($"native: crest {nc.Count} lines {nc.Sum(Len):0} m | toe {nt.Count} lines {nt.Sum(Len):0} m");
        // 顶点最近距离：C# 顶点到 native 顶点集合(同类)的距离中位数/90 分位（粗看是不是同一批线）
        double[] NearestStats(List<SlopeLineExtractor.Polyline3> a, List<List<(double x, double y, double z)>> b)
        {
            var grid = new Dictionary<(int, int), List<(double x, double y)>>();
            foreach (var l in b) foreach (var p in l) { var k = ((int)(p.x / 20), (int)(p.y / 20)); if (!grid.TryGetValue(k, out var g)) grid[k] = g = new(); g.Add((p.x, p.y)); }
            var d = new List<double>();
            foreach (var l in a) for (int i = 0; i < l.Count; i++)
            {
                double best = 1e9; int cx = (int)(l.Xs[i] / 20), cy = (int)(l.Ys[i] / 20);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (grid.TryGetValue((cx + dx, cy + dy), out var g)) foreach (var q in g) { double dd = Math.Sqrt(Math.Pow(q.x - l.Xs[i], 2) + Math.Pow(q.y - l.Ys[i], 2)); if (dd < best) best = dd; }
                d.Add(best);
            }
            d.Sort(); return new[] { d[d.Count / 2], d[(int)(d.Count * 0.9)], d.Count(v => v < 3) * 100.0 / d.Count };
        }
        var sc = NearestStats(crest, nc); var stt = NearestStats(toe, nt);
        _o.WriteLine($"crest vertex→native: p50 {sc[0]:0.00} m p90 {sc[1]:0.00} m within3m {sc[2]:0}% | toe: p50 {stt[0]:0.00} p90 {stt[1]:0.00} within3m {stt[2]:0}%");
    }
}
