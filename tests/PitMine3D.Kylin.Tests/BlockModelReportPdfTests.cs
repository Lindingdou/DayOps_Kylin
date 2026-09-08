using System.Text;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>「输出报告」的 PDF 导出（原 BlockReportGenerator.WritePdfFile，同版本 QuestPDF）。</summary>
public class BlockModelReportPdfTests
{
    private static BlockModelMeta Model()
    {
        var m = BlockModelMeta.CreateRegular("T", 0, 0, 0, 2, 2, 2, 3, 2, 2);
        m.Description = "PDF 冒烟";
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "grade", Unit = "%", Description = "品位" });
        var g = m.EnsureAttr("grade");
        for (int i = 0; i < g.Length; i++) g[i] = i;
        m.ActiveColormapAttribute = "grade";
        m.DeletedIds.Add(0);
        return m;
    }

    [Fact]
    public void Renders_a_real_pdf_document()
    {
        var report = BlockModelReport.Compute(Model());
        var bytes = BlockModelReportPdf.Render(report);
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        // 末尾应有 EOF 标记（PDF 结构完整）
        string tail = Encoding.ASCII.GetString(bytes, bytes.Length - 32, 32);
        Assert.Contains("%%EOF", tail);
    }

    [Fact]
    public void Renders_model_without_attributes()
    {
        var m = BlockModelMeta.CreateRegular("空属性", 0, 0, 0, 1, 1, 1, 2, 2, 2);
        var bytes = BlockModelReportPdf.Render(BlockModelReport.Compute(m));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }
}
