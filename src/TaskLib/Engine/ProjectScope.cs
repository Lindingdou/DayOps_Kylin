// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/ProjectScope.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using PitMine3D.Kylin.Platform;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

/// <summary>
/// 项目/期次上下文在 TaskLib 侧的落点 —— 窗口与静态引擎拿不到 <c>IPluginContext</c>，
/// 由 <c>TaskLibPlugin.Initialize</c> 一次性把宿主的 <see cref="IProjectContext"/> 注进来
/// （与 <c>Simulation.SimHost.Inject</c> 同一套做法）。
///
/// <para>
/// <b>降级纪律</b>：宿主没注册上下文（单测、第三方宿主、注册早于本模块加载失败）时，
/// 全部回落到样例盘子的种子常量——**绝不因为上下文缺席就崩，也绝不静默换一套口径**。
/// 是否接通看 <see cref="Connected"/>，UI 的来源文案要如实写出来。
/// </para>
///
/// <para>
/// <b>为什么作业日要跟着上下文走</b>：落盘目录按日期键分文件（<c>actuals/{日期}_{班次}.json</c>）、
/// 班次日历与爆破事件按日期查库、月度对账按计划期取月计划——三处口径必须同源，
/// 否则会出现「报表在查 6 月、实绩写进 8 月」这种对不上账的事。
/// </para>
/// </summary>
public static class ProjectScope
{
    private static IProjectContext? _ctx;

    /// <summary>上下文变化（换矿/换作业日/换计划期/换文档）——盘子缓存据此失效。</summary>
    public static event Action? Changed;

    /// <summary>由 TaskLibPlugin 注入；传 null 表示宿主未提供（保持样例缺省）。</summary>
    public static void Inject(IProjectContext? ctx)
    {
        if (ReferenceEquals(_ctx, ctx)) return;

        if (_ctx != null)
        {
            try { _ctx.Changed -= OnContextChanged; } catch { }
        }
        _ctx = ctx;
        if (_ctx != null)
        {
            try { _ctx.Changed += OnContextChanged; } catch { }
        }
        OnContextChanged(null, EventArgs.Empty);
    }

    private static void OnContextChanged(object? sender, EventArgs e)
    {
        try { Changed?.Invoke(); } catch { /* 订阅方抛错不影响上下文本身 */ }
    }

    /// <summary>宿主是否提供了项目上下文（false = 全部走样例种子）。</summary>
    public static bool Connected => _ctx != null;

    /// <summary>
    /// 矿名。<b>没接工程上下文时明说没接</b>，不拿「示例露天矿」顶 ——
    /// 那个名字会一路印到任务书抬头和报表页眉上。
    /// </summary>
    public static string MineName => _ctx?.MineName is { Length: > 0 } m ? m : "（未接工程）";

    /// <summary>当前作业日。未接上下文时用样例日（保证样例盘子的日期与任务 Id 自洽）。</summary>
    public static DateTime WorkDate => _ctx?.WorkDate ?? SeedDate;

    public static string DateLabel => _ctx?.DateLabel is { Length: > 0 } d
        ? d
        : ProjectPeriodFormat.DateLabel(SeedDate);

    public static int PlanYear => _ctx?.PlanYear ?? SeedDate.Year;
    public static int PlanMonth => _ctx?.PlanMonth ?? SeedDate.Month;

    /// <summary>任务 Id 前缀「D0804」——同一天的任务共用它，重排会再加后缀 R。</summary>
    public static string IdPrefix => "D" + WorkDate.ToString("MMdd");

    /// <summary>
    /// 「此刻」几点（0..24）。作业日就是今天时取真实时钟，否则取样例时刻——
    /// 排的是过去或未来某一天时，用真实时钟会把整天都判成「已过」，甘特上的现在线也没有意义。
    /// </summary>
    public static double NowHour
    {
        get
        {
            if (WorkDate != DateTime.Today) return SampleTaskBoard.SeedNowHour;
            double h = DateTime.Now.TimeOfDay.TotalHours;
            return h < 0 ? 0 : h > 24 ? 24 : Math.Round(h, 2);
        }
    }

    /// <summary>当前数据库文件（UI 显示「这盘数据存在哪」用；未接通为空串）。</summary>
    public static string DatabasePath => _ctx?.DatabasePath ?? "";

    /// <summary>关联图形文档；未打开为 null。</summary>
    public static string? DocumentPath => _ctx?.DocumentPath;

    /// <summary>样例种子日期（样例盘子的自洽基准，见 <see cref="SampleTaskBoard.SeedDateLabel"/>）。</summary>
    private static DateTime SeedDate
        => DateTime.TryParse(ProjectPeriodFormat.DateKey(SampleTaskBoard.SeedDateLabel), out var d)
            ? d.Date
            : DateTime.Today;

    /// <summary>一句来源文案：上下文接没接通、当前是哪个矿哪一天。</summary>
    public static string Caption => Connected
        ? $"{MineName} · {DateLabel} · 计划期 {ProjectPeriodFormat.MonthLabel(PlanYear, PlanMonth)}"
        : $"{MineName} · {DateLabel}（项目上下文未接线，按样例期次）";
}
