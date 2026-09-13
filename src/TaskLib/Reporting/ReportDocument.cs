// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportDocument.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 渲染层输入 —— 引擎把「模板定义 + 数据」解析后的**渲染树**（已算好值、已定色，与渲染器无关）。
/// WPF 屏幕预览 / QuestPDF / HTML / 打印 都消费这同一棵树。
/// </summary>
public sealed class ReportDocument
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public List<string> MetaLines { get; set; } = new();   // 期间/单位/生成时间

    public List<KpiCard> KpiCards { get; set; } = new();   // 顶部指标卡片行
    public List<ColumnView> Columns { get; set; } = new();
    public List<RowView> Rows { get; set; } = new();
    public RowView? TotalRow { get; set; }

    /// <summary>交叉表带（O-D 流向矩阵等）。null = 本报表无交叉表。</summary>
    public MatrixTableView? Matrix { get; set; }

    public List<ChartView> Charts { get; set; } = new();
    public string Conclusion { get; set; } = "";

    /// <summary>口径脚注（指标口径、估算假设、数据缺口说明）。渲染在结论之后、签批之前。</summary>
    public List<string> Footnotes { get; set; } = new();

    // ── 报告(Narrative)：叙述章节 + 签批栏 ──
    public List<NarrativeBlock> Narrative { get; set; } = new();
    public string Signatures { get; set; } = "";
}

/// <summary>
/// 交叉表渲染视图（行维 × 列维）。为让四个渲染器都能无脑画，行/列的合计**已经并进
/// <see cref="ColumnKeys"/> 与 <see cref="Rows"/>**：表头 = [RowHeader] + ColumnKeys，
/// 每行 = [Key] + Cells，两者一一对齐。
/// </summary>
public sealed class MatrixTableView
{
    public string Title { get; set; } = "";
    /// <summary>左上角单元格文字（行维名，如"作业面＼去向"）。</summary>
    public string RowHeader { get; set; } = "";
    /// <summary>单元格口径说明（如"上行=排弃占容 万m³，下行=运距 km"）。</summary>
    public string Legend { get; set; } = "";
    public List<string> ColumnKeys { get; set; } = new();
    public List<MatrixRowView> Rows { get; set; } = new();
}

public sealed class MatrixRowView
{
    public string Key { get; set; } = "";
    public bool Emphasize { get; set; }
    public List<CellView> Cells { get; set; } = new();
}

/// <summary>报告的一段叙述（标题 + 已填充占位符的正文）。</summary>
public sealed class NarrativeBlock
{
    public string Heading { get; set; } = "";
    public string Body { get; set; } = "";
}

public sealed class ColumnView
{
    public string Header { get; set; } = "";
    public double Width { get; set; } = 90;
    public CellAlign Align { get; set; } = CellAlign.Right;
}

public sealed class RowView
{
    public List<CellView> Cells { get; set; } = new();
    public bool Emphasize { get; set; }   // 合计行等
}

public sealed class CellView
{
    public string Text { get; set; } = "";
    public CellAlign Align { get; set; } = CellAlign.Right;
    public ReportStatus Status { get; set; } = ReportStatus.None;
    public bool Bold { get; set; }
}

public sealed class ChartBar
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public double Value2 { get; set; } = double.NaN;   // 第二序列（对比图）；NaN = 单序列
}

public sealed class ChartView
{
    public string Title { get; set; } = "";
    public string Unit { get; set; } = "";
    public string SeriesA { get; set; } = "";   // 对比图序列名（如"计划"）
    public string SeriesB { get; set; } = "";   // 对比图第二序列名（如"实绩"）；空 = 单序列
    public List<ChartBar> Bars { get; set; } = new();   // 用类而非具名元组，便于 JSON 归档序列化
}

/// <summary>顶部 KPI 指标卡（大数字 + 单位 + 红黄绿 + 目标副文案）。</summary>
public sealed class KpiCard
{
    public string Label { get; set; } = "";
    public string ValueText { get; set; } = "";
    public string Unit { get; set; } = "";
    public ReportStatus Status { get; set; } = ReportStatus.None;
    public string SubText { get; set; } = "";
}

/// <summary>
/// 运行期上下文 —— 一键生成只有三个自由变量（模板 × 周期 × 范围），且都能从环境推断。
/// </summary>
public sealed class GenerationContext
{
    public string Mine { get; set; } = "";
    public string ScopeLabel { get; set; } = "全矿";
    public string? PanelFilter { get; set; }        // 非空 = 只统计该作业面（源侧范围）
    /// <summary>非空 = 只统计运往该去向的量（汇侧范围，如"只看北排土场今天收了多少"）。
    /// 匹配 <see cref="ProductionFact.DestinationKey"/>，即去向名（无名时用 Id、未定去向为"未指定去向"）。</summary>
    public string? DestinationFilter { get; set; }
    /// <summary>非空 = 只统计该物料（匹配 <see cref="ProductionFact.MaterialKey"/> 物料名，如"煤"/"硬岩"/"表土"）。</summary>
    public string? MaterialFilter { get; set; }
    public PeriodKind Period { get; set; } = PeriodKind.Day;
    public string PeriodLabel { get; set; } = "";
    public DateTime AsOf { get; set; } = DateTime.Now;

    /// <summary>
    /// 统计区间起（含）。日报=当天、周报=本周一、月报=当月 1 日；由
    /// <see cref="FactSource.RangeOf"/> 按周期口径推出。
    /// <para>
    /// 引擎按它过滤事实——这是「日/周/月出三份不同的数」的关键：
    /// 在此之前三个按钮共用同一天的数据，周报月报只是换了个标题。
    /// </para>
    /// </summary>
    public DateTime From { get; set; }

    /// <summary>统计区间止（含）。缺省与 <see cref="From"/> 同为作业日。</summary>
    public DateTime To { get; set; }

    /// <summary>取数说明（哪几天有量、量从哪来、哪几天只有工时）。由取数层填，进报表脚注。</summary>
    public string SourceNote { get; set; } = "";

    /// <summary>区间是否有效（未设置时引擎不做日期过滤，保持旧行为）。</summary>
    public bool HasRange => From != default && To != default;

    public static string PeriodName(PeriodKind p) => p switch
    {
        PeriodKind.Week => "周报", PeriodKind.Month => "月报", _ => "日报"
    };
}
