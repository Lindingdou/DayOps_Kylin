// 派车单的 PDF 版式 —— 对应原 DispatchOrderWindow.OnPrint（WPF PrintDialog 横向 + 按纸面缩放，打印范围 = KPI 抬头 + 车次表）。
// Avalonia 没有打印对话框，出同版式 PDF：抬头 / KPI 格 / 按卡车分组的十五列表（组头 = 车号（司机）· N 车次）/ 展开说明。
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PitMine3D.Kylin.Views.TaskLib;

internal static class DispatchOrderPdf
{
    public static void Save(string path, string title, string subTitle, IReadOnlyList<(string title, string value, string unit)> kpi,
                            IReadOnlyList<DispatchOrderWindow.Row> rows, string note)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontFamily("Noto Sans CJK SC", "WenQuanYi Zen Hei", "Microsoft YaHei", "SimSun", Fonts.Calibri).FontSize(8));

                page.Content().Column(col =>
                {
                    col.Item().Text(title).FontSize(16).Bold().FontColor("#111111");
                    col.Item().PaddingBottom(6).Text(subTitle).FontSize(9).FontColor("#444444");

                    // KPI 抬头：一行格子，与窗口同序同文案
                    col.Item().PaddingBottom(8).Row(r =>
                    {
                        foreach (var (t, v, u) in kpi)
                            r.RelativeItem().Border(0.5f).BorderColor("#CCCCCC").Background("#F5F7FA").Padding(4).Column(c =>
                            {
                                c.Item().Text(t).FontSize(7.5f).FontColor("#555555");
                                c.Item().Text(x => { x.Span(v).FontSize(12).Bold().FontColor("#111111"); x.Span(" " + u).FontSize(7).FontColor("#666666"); });
                            });
                    });

                    string[] heads = { "司机", "铲", "趟次", "作业面", "物料", "卸点", "预计装车", "预计卸车", "实装", "实卸", "延误 min", "载重 t", "运距 km", "循环 min", "状态" };
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(48); c.ConstantColumn(46); c.ConstantColumn(30); c.RelativeColumn(1.4f); c.ConstantColumn(44);
                            c.RelativeColumn(1.4f); c.ConstantColumn(46); c.ConstantColumn(46); c.ConstantColumn(38); c.ConstantColumn(38);
                            c.ConstantColumn(44); c.ConstantColumn(42); c.ConstantColumn(44); c.ConstantColumn(44); c.ConstantColumn(72);
                        });
                        t.Header(h =>
                        {
                            foreach (var s in heads)
                                h.Cell().Background("#F1F1F1").Border(0.5f).BorderColor("#CCCCCC").Padding(2).AlignCenter().Text(s).SemiBold();
                        });
                        // 按卡车分组：组头一行跨全表（司机拿到手先找自己的名字）
                        foreach (var g in rows.GroupBy(r => r.TruckHead).OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase))
                        {
                            t.Cell().ColumnSpan((uint)heads.Length).Background("#E8EDF5").Border(0.5f).BorderColor("#CCCCCC").Padding(3)
                                .Text(x => { x.Span(g.Key).Bold().FontSize(9); x.Span($"　{g.Count()} 车次").FontSize(8).FontColor("#555555"); });
                            foreach (var r in g)
                            {
                                IContainer C() => t.Cell().Border(0.5f).BorderColor("#CCCCCC").Padding(2);
                                C().Text(r.Driver); C().Text(r.Shovel); C().AlignCenter().Text(r.Trip.ToString()); C().Text(r.Zone); C().Text(r.Material);
                                C().Text(r.Sink); C().AlignCenter().Text(r.LoadAt); C().AlignCenter().Text(r.DumpAt); C().AlignCenter().Text(r.ActLoadAt); C().AlignCenter().Text(r.ActDumpAt);
                                C().AlignCenter().Text(r.Delay); C().AlignRight().Text(r.PayloadT); C().AlignRight().Text(r.Haul); C().AlignRight().Text(r.Cycle); C().Text(r.State);
                            }
                        }
                    });

                    if (note.Length > 0)
                        col.Item().PaddingTop(8).Text(note).FontSize(8).FontColor("#444444");
                });
            });
        }).GeneratePdf(path);
    }
}
