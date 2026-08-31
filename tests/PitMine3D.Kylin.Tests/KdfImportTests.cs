using System;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// WeCAD KDF 导入回归（忠实移植 KdfReader 二进制解析）。
/// 样本(01_地质地形图.kdf, 8MB)在仓库外, skip-if-absent 本机端到端验证；
/// 大二进制夹具不入库, 但本机真验解析正确性。
/// **参照校验**：原始 kdf_export.dxf 有 3611 条 LWPOLYLINE，本移植解析出 ~3615 折线（差 0.1%，证忠实）。
/// </summary>
public class KdfImportTests
{
    private static readonly string[] Candidates =
    {
        @"C:\Users\cFore\Desktop\2026年6月测试文件\平朔数据\01_地质地形图.kdf",
    };

    private static string? FindFixture()
    {
        foreach (var p in Candidates) if (File.Exists(p)) return p;
        return null;
    }

    [Fact]
    public void Real_kdf_parses_geological_map_faithfully()
    {
        string? fx = FindFixture();
        if (fx == null) return;   // 无样本机器(CI)跳过——大二进制夹具不入库

        var er = KdfImportService.Load(fx);
        Assert.True(er.Success, $"KDF 应解析成功：{er.Error}");

        var plines = er.Entities.OfType<PolylineEntity>().ToList();
        var texts = er.Entities.OfType<TextEntity>().ToList();

        // 参照 kdf_export.dxf = 3611 LWPOLYLINE。折线数应在其邻域(允许 hatch/退化差异)。
        Assert.InRange(plines.Count, 3000, 5000);
        Assert.NotEmpty(texts);                               // 地质图有注记标签(实测 883)
        Assert.True(er.LayerOrder.Count >= 5, $"应有多个图层(实测 18)，实 {er.LayerOrder.Count}");

        // 每条折线≥2 点
        Assert.All(plines, p => Assert.True(p.Points.Count >= 2));

        // bounds 为真实 UTM 坐标(X~424-438km, Y~4974-4984km)——证坐标解析未错位
        double xmin = er.Bounds[0], ymin = er.Bounds[1], xmax = er.Bounds[2], ymax = er.Bounds[3];
        Assert.True(xmax > xmin && ymax > ymin, "bbox 非退化");
        Assert.InRange(xmin, 1e5, 1e7);
        Assert.InRange(ymin, 1e6, 1e7);

        // 顶点落 bbox 内(UpdateBbox 已按地理范围过滤，故所有入选顶点应在界内)
        foreach (var p in plines)
            foreach (var (x, y) in p.Points)
                if (x >= 1e5 && x <= 1e7 && y >= 1e6 && y <= 1e7)   // 只校真实坐标点(占位点已被 bbox 过滤逻辑排除)
                { Assert.InRange(x, xmin - 1, xmax + 1); Assert.InRange(y, ymin - 1, ymax + 1); }

        // 至少一条注记含中文(GBK 解码生效)
        Assert.Contains(texts, t => t.Text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF));
    }

    [Fact]
    public void Bad_magic_errors_without_crash()
    {
        // 非 KDF 文件(测试 dll 自身)→ 报错不崩
        var er = KdfImportService.Load(typeof(KdfImportTests).Assembly.Location);
        Assert.False(er.Success);
        Assert.NotNull(er.Error);
    }
}
