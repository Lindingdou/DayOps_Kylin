using System;
using System.IO;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 3DMine 2011 .3dm 二进制网格导入回归（忠实移植 TdmReader）。
/// 样本(4-2底面.3dm ~1.2MB, 煤层底板 TIN)在仓库外, skip-if-absent 本机端到端验证；
/// 大二进制夹具不入库, CI 自动跳。实测: 43018 三角 · 65213 边 · UTM 坐标域。
/// </summary>
public class TdmImportTests
{
    private static readonly string[] Candidates =
    {
        @"C:\Users\cFore\Desktop\2026年6月测试文件\平朔数据\4-2底面.3dm",
    };

    private static string? FindFixture()
    {
        foreach (var p in Candidates) if (File.Exists(p)) return p;
        return null;
    }

    [Fact]
    public void Real_3dm_parses_triangle_mesh_faithfully()
    {
        string? fx = FindFixture();
        if (fx == null) return;   // 无样本机器(CI)跳过

        var r = TdmImportService.Load(fx);
        Assert.True(r.Success, $"3DMine .3dm 应解析成功：{r.Error}");

        Assert.True(r.EntityCount > 1000, $"煤层底板 TIN 应有大量三角(实测 43018)，实 {r.EntityCount}");
        Assert.True(r.SegmentCount > 1000, $"应有大量边(实测 65213)，实 {r.SegmentCount}");
        // 每边 2 顶点 × 6 float(P3_C3)
        Assert.Equal(r.SegmentCount * 12, r.LineVertices.Length);
        Assert.True(r.LayerOrder.Count >= 1);

        // 三角网 Euler 合理性：去重边数应 < 3×三角数（每三角 3 边，内部边共享）
        Assert.True(r.SegmentCount < r.EntityCount * 3, "去重边数应 < 3×三角数(内部边共享)");
        Assert.True(r.SegmentCount > r.EntityCount, "边数应 > 三角数(TIN 拓扑)");

        // bounds 为真实 UTM 坐标(非退化、非天文数字)
        double xmin = r.Bounds[0], ymin = r.Bounds[1], xmax = r.Bounds[2], ymax = r.Bounds[3];
        Assert.True(xmax > xmin && ymax > ymin, "bbox 非退化");
        Assert.InRange(xmin, 1e4, 1e7);
        Assert.InRange(ymin, 1e5, 1e7);

        // 保留 Z：线框顶点含非零 Z（3D 曲面，非平铺投影）
        bool anyZ = false;
        for (int i = 2; i < r.LineVertices.Length && !anyZ; i += 6) if (Math.Abs(r.LineVertices[i]) > 1e-6) anyZ = true;
        Assert.True(anyZ, "3D 曲面应保留非零 Z");
    }

    private static readonly string[] SolidCandidates =
    {
        @"C:\Users\cFore\Desktop\2026年6月测试文件\平朔数据\三维地质模型\9煤底.3dm",
    };

    private static string? FindSolidFixture()
    {
        foreach (var p in SolidCandidates) if (File.Exists(p)) return p;
        return null;
    }

    [Fact]
    public void Solid_text_3dm_parses_faithfully()
    {
        string? fx = FindSolidFixture();
        if (fx == null) return;   // 无样本机器跳过

        var r = TdmImportService.Load(fx);
        Assert.True(r.Success, $"3DMine Solid 文本应解析成功：{r.Error}");
        Assert.True(r.EntityCount > 100, $"实体煤层模型应有大量三角，实 {r.EntityCount}");
        Assert.True(r.SegmentCount > 100);
        Assert.Equal(r.SegmentCount * 12, r.LineVertices.Length);
        // 每三角≤3 去重边（该 solid 为三角汤=顶点不按索引共享 → 恰 3×；index-shared 网格则更少）
        Assert.True(r.SegmentCount <= r.EntityCount * 3, "去重边 ≤ 3×三角");
        Assert.True(r.SegmentCount >= r.EntityCount, "边数 ≥ 三角数");
        double xmin = r.Bounds[0], ymin = r.Bounds[1], xmax = r.Bounds[2], ymax = r.Bounds[3];
        Assert.True(xmax > xmin && ymax > ymin, "bbox 非退化");
        // 保留非零 Z
        bool anyZ = false;
        for (int i = 2; i < r.LineVertices.Length && !anyZ; i += 6) if (Math.Abs(r.LineVertices[i]) > 1e-6) anyZ = true;
        Assert.True(anyZ, "实体模型应保留非零 Z");
    }

    [Fact]
    public void Non_3dmine_3dm_errors_without_crash()
    {
        // Rhino .3dm 或非 3DMine 文件 → 报错不崩(测试 dll 自身当坏输入)
        var r = TdmImportService.Load(typeof(TdmImportTests).Assembly.Location);
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void IsBinary3dm_detects_magic()
    {
        Assert.False(TdmImportService.IsBinary3dm(new byte[] { 1, 2, 3 }));                       // 太短
        Assert.False(TdmImportService.IsBinary3dm(new byte[32]));                                 // 全 0
        var good = new byte[32];
        good[0] = 15;
        System.Text.Encoding.ASCII.GetBytes("3DMine_2011_Bin").CopyTo(good, 1);
        Assert.True(TdmImportService.IsBinary3dm(good));
    }
}
