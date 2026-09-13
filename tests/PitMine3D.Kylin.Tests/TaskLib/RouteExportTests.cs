// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/RouteExportTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  把「每个块体走哪条线路」导出成文本，供离线出图
//
//  为什么要出图台架：界面上一层叠一层，"这条线到底属于哪个块"靠看是分不清的。
//  导出来逐块画一张，才能真正核对寻径结果 —— 这与 [[dump-strip-offline-harness]]
//  是同一条路子：合成/真算例 + 离线出图，不靠界面截图猜。
//
//  ⚠ 依赖本机台账 + 库，缺则跳过。输出到 %TEMP%\pitmine_routes\。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class RouteExportTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public RouteExportTests(ITestOutputHelper o) => _out = o;
    public void Dispose() => SimRoadGraph.AttachForTest(null);

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(new[] { dir }.Concat(parts).ToArray());
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    [Fact]
    public void E1_导出每个块体的寻径结果()
    {
        var store = new MonthlyUnitLedgerStore();
        string db = RepoFile("Data", "pmgeo.db");
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
        SimRoadGraph.AttachForTest(graph, "台架：库里最新路网");
        Assert.True(store.TryLoadBase(out var baseRows, out _));

        string month = store.ListMonths().Where(m => SimPeriodKey.Parse(m).Length > 0)
                            .OrderBy(m => m, StringComparer.Ordinal).LastOrDefault() ?? "";
        Assert.True(month.Length > 0 && store.TryLoad(month, out var rows, out _), "没有月度台账");
        store.TryLoad(month, out rows, out _);

        var sink = CoalSinkPoint.Load(store.Root, out _);
        var od = LedgerOdBuilder.Build(baseRows, rows, sink);
        var haul = new HaulRouteStage(dynamicSink: new RecordingDynamicOverlay());
        var hr = haul.Apply(month, od.Ods);

        string dir = Path.Combine(Path.GetTempPath(), "pitmine_routes");
        Directory.CreateDirectory(dir);
        var ci = CultureInfo.InvariantCulture;

        // ① 路网全部中线
        using (var w = new StreamWriter(Path.Combine(dir, "network.txt"), false, new UTF8Encoding(false)))
            foreach (var e in graph.Edges)
            {
                var cl = e.Centerline;
                if (cl == null || cl.Count < 2) continue;
                w.WriteLine(string.Join(" ", cl.Select(pt => $"{pt.X.ToString("0.##", ci)},{pt.Y.ToString("0.##", ci)}")));
            }

        // ② 每个块体：位置 + 它的路线
        var pathOf = new Dictionary<string, SimHaulPath>(StringComparer.Ordinal);
        foreach (var pp in hr.Paths) if (!pathOf.ContainsKey(pp.UnitId)) pathOf[pp.UnitId] = pp;

        int withRoute = 0, without = 0;
        using (var w = new StreamWriter(Path.Combine(dir, "blocks.txt"), false, new UTF8Encoding(false)))
        {
            w.WriteLine("# UnitId|Kind|Cx|Cy|LenM|WidM|AzDeg|HasRoute|RouteXY...");
            foreach (var r in rows)
            {
                if (r.Kind == LedgerKind.Dump) continue;
                pathOf.TryGetValue(r.UnitId, out var pth);
                var sb = new StringBuilder();
                sb.Append(r.UnitId).Append('|')
                  .Append(r.Kind == LedgerKind.Coal ? "coal" : "rock").Append('|')
                  .Append(r.Cx.ToString("0.##", ci)).Append('|').Append(r.Cy.ToString("0.##", ci)).Append('|')
                  .Append(r.LengthM.ToString("0.##", ci)).Append('|').Append(r.WidthM.ToString("0.##", ci)).Append('|')
                  .Append((r.AzimuthDeg ?? double.NaN).ToString("0.##", ci)).Append('|')
                  .Append(pth != null ? 1 : 0).Append('|');
                if (pth != null)
                {
                    withRoute++;
                    for (int i = 0; i < pth.PointCount; i++)
                        sb.Append(pth.Xyz[i * 3].ToString("0.##", ci)).Append(',')
                          .Append(pth.Xyz[i * 3 + 1].ToString("0.##", ci)).Append(' ');
                }
                else without++;
                w.WriteLine(sb.ToString());
            }
        }

        _out.WriteLine($"期次 {month}：块体 {withRoute + without}（有路线 {withRoute} · 无 {without}）");
        _out.WriteLine($"导出到 {dir}");
        Assert.True(withRoute > 0, "一个块体都没有寻径结果 —— 出图没有意义。");
    }
}
