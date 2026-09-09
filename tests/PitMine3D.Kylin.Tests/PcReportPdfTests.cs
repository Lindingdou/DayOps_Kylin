using System.Collections.Generic;
using System.Text;
using PitMine3D.Kylin.Views.PointCloud;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 点云结果窗的 PDF 导出（对应原版 VolumeResultWindow「导出 PDF」）：能渲染出真 PDF 字节。
/// 导出按钮本身要点窗口才走得到，这里把可测的那段（文档渲染）单独钉住。
/// </summary>
public class PcReportPdfTests
{
    private static readonly List<(string k, string v)> Rows = new()
    {
        ("挖方 / 填方", "12,345.6 / 7,890.1 m³"),
        ("净值（填 − 挖）", "-4,455.5 m³"),
        ("重叠区", "36,000 m²"),
    };

    [Fact]
    public void Render_produces_pdf_bytes()
    {
        byte[] pdf = PcReportPdf.Render("两期点云算量 · 结果", "挖方（红）= 第二期低于第一期。", Rows);
        Assert.True(pdf.Length > 1000, $"PDF 太小: {pdf.Length} 字节");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Render_with_histogram_is_larger_than_without()
    {
        var hist = new double[] { 1, 4, 9, 16, 9, 4, 1 };
        byte[] plain = PcReportPdf.Render("位移监测 C2C · 统计", null, Rows);
        byte[] withHist = PcReportPdf.Render("位移监测 C2C · 统计", null, Rows, hist, -1.5, 2.5, "位移分布直方图");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(withHist, 0, 4));
        Assert.True(withHist.Length > plain.Length, "带直方图的 PDF 应当更大（多画了 7 根柱子）");
    }
}
