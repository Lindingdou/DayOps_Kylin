// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/RouteReachSweepTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  命中率对【吸附半径 × 候选扇出】的响应曲线
//
//  为什么值得单独跑：路网碎成 127 块之后，"提高命中率"有两条完全不同的路 ——
//    ① 调吸附/候选（模拟这边的参数，立刻见效，但天花板由路网决定）
//    ② 重建路网、把断头接上（那才是真修，但要回另一个模块）
//  只有把曲线打出来，才知道 ① 还剩多少空间、什么时候必须走 ②。
//
//  ⚠ 依赖本机的台账 + 库。缺任一自动跳过。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class RouteReachSweepTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public RouteReachSweepTests(ITestOutputHelper o) => _out = o;

    // ★ SimRoadGraph 是**进程级静态**：本台架改的每一项都要原样还回去。
    //   来源文案也算状态 —— 第一版还成了"台架还原"，把 J5b（判缺省来源说得出口）弄红了。
    //   程序集是串行跑的，所以污染一定会传染给后面的判据。
    private readonly double _savedRadius = SimRoadGraph.SnapRadiusM;
    private readonly string _savedSource = SimRoadGraph.SnapRadiusSource;
    private readonly int _savedFanout = SimRoadGraph.CandidateFanout;

    public void Dispose()
    {
        SimRoadGraph.AttachForTest(null);
        SimRoadGraph.UseSnapRadius(_savedRadius, _savedSource);
        SimRoadGraph.CandidateFanout = _savedFanout;
    }

    private static string DbPath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var p = System.IO.Path.Combine(dir, "Data", "pmgeo.db");
            if (System.IO.File.Exists(p)) return p;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return "";
    }

    [Fact]
    public void S1_命中率随吸附半径与候选扇出的响应()
    {
        var store = new MonthlyUnitLedgerStore();
        string db = DbPath();
        if (!store.HasBase || db.Length == 0) { _out.WriteLine("跳过：本机缺台账或库"); return; }

        string json;
        using (var cn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "select graph_json from road_network order by captured_at desc limit 1";
            json = cmd.ExecuteScalar() as string ?? "";
        }
        if (json.Length == 0) { _out.WriteLine("跳过：库里没有路网存档"); return; }

        var graph = RoadGraphSerializer.FromJson(json);
        Assert.True(store.TryLoadBase(out var baseRows, out _));

        string month = store.ListMonths().Where(m => SimPeriodKey.Parse(m).Length > 0)
                            .OrderBy(m => m, StringComparer.Ordinal).LastOrDefault() ?? "";
        if (month.Length == 0 || !store.TryLoad(month, out var rows, out _)) { _out.WriteLine("跳过：没有月度台账"); return; }

        var sink = CoalSinkPoint.Load(store.Root, out _);
        var od = LedgerOdBuilder.Build(baseRows, rows, sink);
        _out.WriteLine($"期次 {month} · O-D {od.Ods.Count} 笔 · 路网 {graph.NodeCount} 节点 / {graph.EdgeCount} 边\n");
        _out.WriteLine("吸附半径 ×  扇出 →  命中 / 问路   （命中率）");

        foreach (double r in new[] { 200.0, 400.0, 800.0, 1500.0 })
            foreach (int k in new[] { 1, 6, 12 })
            {
                // 每一格都重新挂图：吸附缓存与统计都要清干净，否则上一格的结果会串味
                SimRoadGraph.AttachForTest(graph, "台架");
                SimRoadGraph.UseSnapRadius(r, "台架扫描");
                SimRoadGraph.CandidateFanout = k;

                var stage = new HaulRouteStage(dynamicSink: new RecordingDynamicOverlay());
                var res = stage.Apply(month + $"|r{r}k{k}", od.Ods);
                _out.WriteLine($"  {r,6:0} m  ×  {k,2}  →  {res.OdHit,3} / {res.OdRouted,3}   ({res.HitRate:P1})");
            }

        _out.WriteLine("\n★ 扇出=1 那一列就是「只取最近节点」的旧行为，用来看候选重试带来多少。");
    }
}
