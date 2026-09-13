// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/WordReportRenderer.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 渲染器 —— 把渲染树 <see cref="ReportDocument"/> 输出成 Word(.docx)（OpenXML SDK，运行期由 ClosedXML 传递引入）。
/// 结构 = 标题 + 期间/单位 + 叙述章节 + KPI 附表 + 结论 + 签批栏。主要给"报告(Narrative)"用。
/// </summary>
public static class WordReportRenderer
{
    public static void Write(ReportDocument doc, string path)
    {
        using var wd = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = wd.AddMainDocumentPart();
        main.Document = new Document();
        var body = main.Document.AppendChild(new Body());

        body.AppendChild(Para(doc.Title, bold: true, size: 34, center: true, color: "1E3A5F"));
        if (!string.IsNullOrEmpty(doc.Subtitle))
            body.AppendChild(Para(doc.Subtitle, size: 21, center: true, color: "6C757D"));
        foreach (var m in doc.MetaLines)
            body.AppendChild(Para(m, size: 18, center: true, color: "6C757D"));

        if (doc.KpiCards.Count > 0)
        {
            body.AppendChild(BuildKpiTable(doc));
            body.AppendChild(new Paragraph());   // 两表之间须隔一段
        }

        foreach (var nb in doc.Narrative)
        {
            if (!string.IsNullOrEmpty(nb.Heading))
                body.AppendChild(Para(nb.Heading, bold: true, size: 26, color: "1E3A5F"));
            body.AppendChild(Para(nb.Body, size: 21));
        }

        if (doc.Columns.Count > 0)
            body.AppendChild(BuildTable(doc));

        // 交叉表（采—排流向矩阵）：表头 = 行维＼列维 + 各列键，行 = 行键 + 各 O-D 单元格
        if (doc.Matrix is { Rows.Count: > 0 } mx)
        {
            body.AppendChild(new Paragraph());
            body.AppendChild(Para(mx.Title, bold: true, size: 24, color: "1E3A5F"));
            if (!string.IsNullOrEmpty(mx.Legend)) body.AppendChild(Para(mx.Legend, size: 16, color: "6C757D"));
            body.AppendChild(BuildMatrixTable(mx));
        }

        if (!string.IsNullOrEmpty(doc.Conclusion))
        {
            body.AppendChild(Para("分析结论", bold: true, size: 24, color: "1E3A5F"));
            body.AppendChild(Para(doc.Conclusion, size: 21));
        }

        if (doc.Footnotes.Count > 0)
        {
            body.AppendChild(Para("口径说明", bold: true, size: 18, color: "6C757D"));
            foreach (var n in doc.Footnotes) body.AppendChild(Para("· " + n, size: 16, color: "6C757D"));
        }

        if (!string.IsNullOrEmpty(doc.Signatures))
            body.AppendChild(Para(doc.Signatures, size: 21, right: true));

        main.Document.Save();
    }

    private static Paragraph Para(string text, bool bold = false, int size = 21, bool center = false, bool right = false, string? color = null)
    {
        var rp = new RunProperties();
        if (bold) rp.AppendChild(new Bold());
        rp.AppendChild(new RunFonts { Ascii = "Microsoft YaHei", HighAnsi = "Microsoft YaHei", EastAsia = "Microsoft YaHei" });
        rp.AppendChild(new FontSize { Val = size.ToString() });
        if (color != null) rp.AppendChild(new Color { Val = color });

        var run = new Run(new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve });
        run.PrependChild(rp);

        var para = new Paragraph(run);
        if (center || right)
        {
            var pp = new ParagraphProperties();
            pp.AppendChild(new Justification { Val = center ? JustificationValues.Center : JustificationValues.Right });
            para.PrependChild(pp);
        }
        return para;
    }

    private static Table BuildKpiTable(ReportDocument doc)
    {
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "E2E8F0" },
                new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "E2E8F0" },
                new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "E2E8F0" },
                new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "E2E8F0" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "E2E8F0" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "E2E8F0" }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        var tr = new TableRow();
        foreach (var k in doc.KpiCards)
        {
            var cell = new TableCell();
            cell.AppendChild(new TableCellProperties(new DocumentFormat.OpenXml.Wordprocessing.Shading { Val = ShadingPatternValues.Clear, Fill = "F8FAFC" }));
            cell.AppendChild(Para(k.Label, size: 16, color: "6C757D"));
            cell.AppendChild(Para(k.ValueText + (string.IsNullOrEmpty(k.Unit) ? "" : " " + k.Unit), bold: true, size: 26, color: StatusHex(k.Status)));
            cell.AppendChild(Para(string.IsNullOrEmpty(k.SubText) ? "" : k.SubText, size: 14, color: "9AA5B1"));
            tr.AppendChild(cell);
        }
        table.AppendChild(tr);
        return table;
    }

    private static string StatusHex(ReportStatus s) => s switch
    {
        ReportStatus.Ok => "0F6E56",
        ReportStatus.Warn => "B7791F",
        ReportStatus.Bad => "C0392B",
        _ => "1F2937",
    };

    private static Table BuildTable(ReportDocument doc)
    {
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "DEE2E6" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "DEE2E6" }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        var header = new TableRow();
        foreach (var c in doc.Columns)
            header.AppendChild(Cell(c.Header, bold: true, shade: "E9EEF4", align: c.Align));
        table.AppendChild(header);

        foreach (var row in doc.Rows)
        {
            var tr = new TableRow();
            for (int i = 0; i < doc.Columns.Count; i++)
                tr.AppendChild(Cell(i < row.Cells.Count ? row.Cells[i].Text : "", align: doc.Columns[i].Align));
            table.AppendChild(tr);
        }

        if (doc.TotalRow != null)
        {
            var tr = new TableRow();
            for (int i = 0; i < doc.Columns.Count; i++)
                tr.AppendChild(Cell(i < doc.TotalRow.Cells.Count ? doc.TotalRow.Cells[i].Text : "", bold: true, shade: "F1F5FA", align: doc.Columns[i].Align));
            table.AppendChild(tr);
        }
        return table;
    }

    private static Table BuildMatrixTable(MatrixTableView mx)
    {
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "9DB2C6" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "DEE2E6" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "DEE2E6" }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        var header = new TableRow();
        header.AppendChild(Cell(mx.RowHeader, bold: true, shade: "E9EEF4"));
        foreach (var c in mx.ColumnKeys)
            header.AppendChild(Cell(c, bold: true, shade: "E9EEF4", align: CellAlign.Right));
        table.AppendChild(header);

        foreach (var row in mx.Rows)
        {
            var tr = new TableRow();
            string? shade = row.Emphasize ? "EAF0F6" : null;
            tr.AppendChild(Cell(row.Key, bold: row.Emphasize, shade: shade));
            for (int i = 0; i < mx.ColumnKeys.Count; i++)
                tr.AppendChild(Cell(i < row.Cells.Count ? row.Cells[i].Text : "", bold: row.Emphasize, shade: shade, align: CellAlign.Right));
            table.AppendChild(tr);
        }
        return table;
    }

    private static TableCell Cell(string text, bool bold = false, string? shade = null, CellAlign align = CellAlign.Left)
    {
        var props = new TableCellProperties(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
        if (shade != null) props.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Shading { Val = ShadingPatternValues.Clear, Fill = shade });

        var cell = new TableCell();
        cell.AppendChild(props);
        cell.AppendChild(Para(text, bold: bold, size: 18, right: align == CellAlign.Right, center: align == CellAlign.Center));
        return cell;
    }
}
