using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 道路网连通增强（焊接近失端点 + 桥接悬空断头 + T形打断 + 手动线宽容/免坡度）回归 —— 忠实移植原 RoadNetworkConnector。
/// 合成已知值：逐用例按算法推演断言 焊接数/桥接段/打断处/输出条数。
/// </summary>
public class RoadNetworkConnectorTests
{
    private static double[] Line(params (double x, double y, double z)[] pts)
    {
        var f = new double[pts.Length * 3];
        for (int i = 0; i < pts.Length; i++) { f[3 * i] = pts[i].x; f[3 * i + 1] = pts[i].y; f[3 * i + 2] = pts[i].z; }
        return f;
    }
    private static RoadConnectOptions Opt() => new RoadConnectOptions();  // SnapTol4/Connect25/Manual60/Slope14

    [Fact]
    public void Weld_near_endpoints_into_shared_node()
    {
        // A 末(100,0) 与 B 首(102,0) 相距 2 ≤ SnapTol4 → 焊到质心(101,0); 无桥接。
        var a = Line((0, 0, 0), (100, 0, 0));
        var b = Line((102, 0, 0), (200, 0, 0));
        var res = RoadNetworkConnector.Connect(new[] { a, b }, null, Opt());
        Assert.Equal(2, res.Welded);      // 两端点入同簇
        Assert.Equal(0, res.Bridged);
        Assert.Equal(0, res.Splits);
        Assert.Equal(2, res.Lines.Count);
    }

    [Fact]
    public void Bridge_endpoint_gap_within_connect_dist()
    {
        // A 末(100,0) 与 B 首(110,0) 相距 10 ∈ (SnapTol4, Connect25] → 补一段连接线。
        var a = Line((0, 0, 0), (100, 0, 0));
        var b = Line((110, 0, 0), (200, 0, 0));
        var res = RoadNetworkConnector.Connect(new[] { a, b }, null, Opt());
        Assert.Equal(0, res.Welded);
        Assert.Equal(1, res.Bridged);     // 新增一条连接段
        Assert.Equal(0, res.Splits);
        Assert.Equal(3, res.Lines.Count); // A + B + 连接段
    }

    [Fact]
    public void Gap_beyond_connect_dist_not_bridged()
    {
        // 间距 50 > Connect25 → 不连。
        var a = Line((0, 0, 0), (100, 0, 0));
        var b = Line((150, 0, 0), (200, 0, 0));
        var res = RoadNetworkConnector.Connect(new[] { a, b }, null, Opt());
        Assert.Equal(0, res.Bridged);
        Assert.Equal(2, res.Lines.Count);
    }

    [Fact]
    public void Tee_split_when_deadend_hits_line_middle()
    {
        // 支线 B 首(100,20) 落在干线 A 中部(100,0) 上方 20 ≤ Connect25 → 补连接段 + 把 A 在(100,0)打断成 T。
        var a = Line((0, 0, 0), (200, 0, 0));
        var b = Line((100, 20, 0), (100, 50, 0));
        var res = RoadNetworkConnector.Connect(new[] { a, b }, null, Opt());
        Assert.Equal(1, res.Bridged);
        Assert.Equal(1, res.Splits);      // A 打断一处
        Assert.Equal(4, res.Lines.Count); // A 两段 + B + 连接段
    }

    [Fact]
    public void Steep_bridge_rejected_for_auto_line()
    {
        // A 末(100,0,0) 与 B 首(110,0,50): 平距 10 ≤ Connect25 但坡度 atan2(50,10)≈78.7° > 14° → 自动线拒连。
        var a = Line((0, 0, 0), (100, 0, 0));
        var b = Line((110, 0, 50), (200, 0, 50));
        var res = RoadNetworkConnector.Connect(new[] { a, b }, null, Opt());
        Assert.Equal(0, res.Bridged);     // 坡度闸门拦下
        Assert.Equal(2, res.Lines.Count);
    }

    [Fact]
    public void Manual_line_bridges_wider_gap()
    {
        // A(自动)末(100,0) 与 B(手动)首(140,0) 相距 40：自动上限 25 够不着，手动上限 60 可连。
        var a = Line((0, 0, 0), (100, 0, 0));
        var bManual = Line((140, 0, 0), (200, 0, 0));
        var res = RoadNetworkConnector.Connect(new[] { a }, new[] { bManual }, Opt());
        Assert.Equal(1, res.Bridged);
        Assert.Equal(1, res.ManualMerged);
        Assert.Equal(3, res.Lines.Count); // A + B + 连接段
    }

    [Fact]
    public void Empty_input_reports_nothing()
    {
        var res = RoadNetworkConnector.Connect(new List<double[]>(), null, Opt());
        Assert.Empty(res.Lines);
        Assert.Equal(0, res.Bridged);
    }
}
