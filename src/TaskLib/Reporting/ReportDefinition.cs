// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportDefinition.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 明细带展开维（一行 = 该维一个成员的聚合）。
/// Destination(去向) 与 Material(物料) 是本轮补齐的两个采剥报表基本维——
/// 没有去向维就出不了「某排土场今日受排量」，没有物料维就出不了「煤/岩分列」。
/// </summary>
public enum GroupDim { Panel, Equipment, Process, Shift, Destination, Material }

/// <summary>列绑定：展开维的键（如"采区名"）或某个指标。</summary>
public enum ColBind { Dimension, Indicator }

public enum CellAlign { Left, Center, Right }

/// <summary>报表周期口径。</summary>
public enum PeriodKind { Day, Week, Month }

/// <summary>报表种类：报表(结构化表格) / 报告(叙述文档) / 矩阵(源×汇交叉表)。</summary>
public enum ReportKind { Tabular, Narrative, Matrix }

/// <summary>报告的一个叙述章节：标题 + 正文模板（正文含 {指标Id} 与 {@mine/@scope/@period/@date} 占位符）。</summary>
public sealed class NarrativeSection
{
    public string Heading { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>一列定义：表头 + 绑定（维键/指标）+ 样式 + 是否进合计行。</summary>
public sealed class ReportColumn
{
    public string Header { get; set; } = "";
    public double Width { get; set; } = 90;
    public CellAlign Align { get; set; } = CellAlign.Right;
    public ColBind Bind { get; set; } = ColBind.Indicator;
    public string IndicatorId { get; set; } = "";   // Bind==Indicator 时有效
    public bool Total { get; set; } = false;         // 合计行是否对本列汇总（走该指标对全体的聚合）
}

/// <summary>图表带（按某维对某指标出柱状；给 <see cref="IndicatorId2"/> 则出"计划vs实绩"双色对比柱）。</summary>
public sealed class ChartSpec
{
    public string Title { get; set; } = "";
    public string IndicatorId { get; set; } = "";
    public string IndicatorId2 { get; set; } = "";   // 选填：第二序列（对比图，如 计划 vs 实绩）
    public GroupDim By { get; set; } = GroupDim.Panel;
}

/// <summary>
/// 交叉表带（O-D 矩阵）—— 行=一个维、列=另一个维、单元格=指标值。
/// 露天矿最经典的一张表是「采—排流向矩阵」：行=作业面(源)、列=去向(汇)、格=该 O-D 的量与运距，
/// 它直接回答"从哪采剥、排弃到哪"这个问题本身。分带结构（一行一维）撑不起两维交叉，故单列一种带。
/// </summary>
public sealed class MatrixSpec
{
    public string Title { get; set; } = "";
    public GroupDim RowDim { get; set; } = GroupDim.Panel;
    public GroupDim ColDim { get; set; } = GroupDim.Destination;
    /// <summary>主指标（单元格大数字）。</summary>
    public string IndicatorId { get; set; } = "";
    /// <summary>副指标（单元格括号里的小字，如运距）。留空则只显示主指标。</summary>
    public string IndicatorId2 { get; set; } = "";
    /// <summary>是否输出行合计列 / 列合计行。</summary>
    public bool ShowTotals { get; set; } = true;
    /// <summary>是否隐藏全空的行/列（O-D 矩阵通常很稀疏）。</summary>
    public bool HideEmpty { get; set; } = true;
    /// <summary>
    /// 只统计采装侧事实。O-D 矩阵的「源」必须是采装面——排土面是同一批料的汇侧作业，
    /// 计进来会让同一方岩在矩阵里出现两次（一次「采装面→内排场」，一次「内排场→内排场」）。
    /// </summary>
    public bool SourceSideOnly { get; set; } = true;
}

/// <summary>
/// 模板层 —— 一份「分带报表定义」（系统内建、纯数据、可 JSON 持久化，不含任何代码/委托）。
/// 结构 = 报表头(标题+期间+单位) · 页眉(列标题) · 明细带(按 <see cref="DetailGroup"/> 逐行展开)
///        · 交叉表带(可选 <see cref="Matrix"/>) · 报表尾(合计+结论) · 图表带。
/// 生成 = 引擎按此定义取数、把明细带按维展开、渲染成实际报表（屏幕 + PDF/HTML/Word/打印）。
/// </summary>
public sealed class ReportDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新建报表";
    public string Level { get; set; } = "矿";       // 公司 / 矿 / 采区 / 班组（多层级）
    public string Title { get; set; } = "生产报表"; // 报表头大标题
    public bool BuiltIn { get; set; } = false;       // 内置模板（只读，只能克隆后改）

    public ReportKind Kind { get; set; } = ReportKind.Tabular;

    /// <summary>顶部 KPI 指标卡（大数字+红黄绿），按指标 Id 列出。空 = 不显示卡片行。</summary>
    public List<string> KpiIndicators { get; set; } = new();

    public GroupDim DetailGroup { get; set; } = GroupDim.Panel;
    public List<ReportColumn> Columns { get; set; } = new();
    public bool ShowTotalRow { get; set; } = true;
    public string TotalRowLabel { get; set; } = "合计";
    public List<ChartSpec> Charts { get; set; } = new();

    /// <summary>交叉表带（null = 不出交叉表）。Kind=Matrix 时通常只出它、不出明细带。</summary>
    public MatrixSpec? Matrix { get; set; }

    /// <summary>是否在报表尾生成规则评语（叙述结论）。</summary>
    public bool ShowConclusion { get; set; } = true;

    /// <summary>报表尾的口径脚注（写清指标口径与估算假设，避免"数看着对、口径其实不同"）。</summary>
    public List<string> Footnotes { get; set; } = new();

    // ── 报告(Narrative)专用 ──
    /// <summary>报告叙述章节（Kind=Narrative 时生效）。</summary>
    public List<NarrativeSection> NarrativeSections { get; set; } = new();
    /// <summary>签批栏文字（如 "编制：＿＿＿  审核：＿＿＿  批准：＿＿＿"）。</summary>
    public string SignatureLine { get; set; } = "";

    public ReportDefinition Clone(string? newName = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = newName ?? Name + "（副本）",
        Level = Level,
        Title = Title,
        BuiltIn = false,
        Kind = Kind,
        KpiIndicators = new List<string>(KpiIndicators),
        DetailGroup = DetailGroup,
        Columns = Columns.ConvertAll(c => new ReportColumn
        {
            Header = c.Header, Width = c.Width, Align = c.Align,
            Bind = c.Bind, IndicatorId = c.IndicatorId, Total = c.Total
        }),
        ShowTotalRow = ShowTotalRow,
        TotalRowLabel = TotalRowLabel,
        Charts = Charts.ConvertAll(c => new ChartSpec { Title = c.Title, IndicatorId = c.IndicatorId, IndicatorId2 = c.IndicatorId2, By = c.By }),
        Matrix = Matrix == null ? null : new MatrixSpec
        {
            Title = Matrix.Title, RowDim = Matrix.RowDim, ColDim = Matrix.ColDim,
            IndicatorId = Matrix.IndicatorId, IndicatorId2 = Matrix.IndicatorId2,
            ShowTotals = Matrix.ShowTotals, HideEmpty = Matrix.HideEmpty,
        },
        ShowConclusion = ShowConclusion,
        Footnotes = new List<string>(Footnotes),
        NarrativeSections = NarrativeSections.ConvertAll(s => new NarrativeSection { Heading = s.Heading, Body = s.Body }),
        SignatureLine = SignatureLine,
    };

    public static string DimLabel(GroupDim d) => d switch
    {
        GroupDim.Panel => "作业面",
        GroupDim.Equipment => "设备",
        GroupDim.Process => "工序",
        GroupDim.Shift => "班次",
        GroupDim.Destination => "去向",
        GroupDim.Material => "物料",
        _ => "维度",
    };
}
