// 忠实移植自原 PitMine3D Platform/PitMine.Platform/IProjectContext.cs（逐行对应；仅命名空间适配）
using System;

namespace PitMine3D.Kylin.Platform
{
    /// <summary>
    /// 项目 / 期次上下文 —— 全系统「现在在给哪个矿、哪一期干活」的唯一答案。
    ///
    /// <para>
    /// 在它出现之前，每个模块各自假设自己的时空坐标：日常生产组织写死「2026-06-17 示例露天矿」，
    /// 计划编制只认会话内存里的方案，报表按窗口自己拼的日期取数。三处各说各话，
    /// 换一个矿或换一期设计时没有任何一处会跟着变。
    /// </para>
    ///
    /// <para>
    /// 宿主是唯一同时看得见「数据库 + 图形文档 + 用户设置」的地方，故实现方在宿主，
    /// 经 宿主服务注册表 注册；模块取不到时**必须优雅降级**（用各自的样例缺省），
    /// 绝不因为上下文没接线就崩——这与 IPlanSimulationService 是同一条纪律。
    /// </para>
    ///
    /// <para>
    /// 期次分两层，故意分开：<see cref="WorkDate"/> 是「今天在排哪一天的班」（日常生产组织用），
    /// <see cref="PlanYear"/>/<see cref="PlanMonth"/> 是「在编哪个月的计划」（计划编制用）。
    /// 八月里编九月的计划是常态，两者不能合成一个。
    /// </para>
    /// </summary>
    public interface IProjectContext
    {
        /// <summary>当前矿名（报表抬头、任务书抬头、事实宽表的 Mine 维都取它）。</summary>
        string MineName { get; }

        /// <summary>当前作业日（日常生产组织裂解装箱、实绩录入、日报的日期口径）。</summary>
        DateTime WorkDate { get; }

        /// <summary>当前计划年（计划编制/月度对账口径；缺省跟随 <see cref="WorkDate"/>）。</summary>
        int PlanYear { get; }

        /// <summary>当前计划月 1-12（缺省跟随 <see cref="WorkDate"/>）。</summary>
        int PlanMonth { get; }

        /// <summary>作业日标签「2026-08-04 周二」——落盘文件名与单据抬头统一走它。</summary>
        string DateLabel { get; }

        /// <summary>计划期标签「2026年8月」。</summary>
        string PeriodLabel { get; }

        /// <summary>当前数据库文件路径（取自 SqlLib；未接通时为空串）。</summary>
        string DatabasePath { get; }

        /// <summary>当前关联的图形文档路径；未打开文档时为 null。</summary>
        string? DocumentPath { get; }

        /// <summary>矿 / 作业日 / 计划期 / 文档 任一变化后触发——各模块据此让自己的缓存失效。</summary>
        event EventHandler? Changed;

        void SetMine(string mineName);

        /// <summary>切作业日。</summary>
        void SetWorkDate(DateTime date);

        /// <summary>作业日回到今天（并让计划期跟随）。</summary>
        void ResetWorkDateToToday();

        /// <summary>切计划期（与作业日解耦：八月编九月计划）。</summary>
        void SetPlanMonth(int year, int month);

        /// <summary>宿主在打开/关闭图形文档时调用。</summary>
        void SetDocument(string? path);
    }

    /// <summary>
    /// 期次文案的唯一格式化口径。跨模块统一走它，免得一处写「2026-08-04 周二」、
    /// 另一处写「2026/8/4」，落盘文件名与单据抬头就此对不上。
    /// </summary>
    public static class ProjectPeriodFormat
    {
        private static readonly string[] WeekNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        /// <summary>「2026-08-04 周二」。第一个空格前的片段即日期键（落盘目录按它分文件）。</summary>
        public static string DateLabel(DateTime d) => $"{d:yyyy-MM-dd} {WeekNames[(int)d.DayOfWeek]}";

        /// <summary>「2026年8月」。</summary>
        public static string MonthLabel(int year, int month) => $"{year}年{month}月";

        /// <summary>日期键「2026-08-04」（<see cref="DateLabel"/> 的首片段）。</summary>
        public static string DateKey(string? dateLabel)
        {
            var s = (dateLabel ?? "").Trim();
            int i = s.IndexOf(' ');
            return i > 0 ? s.Substring(0, i) : s;
        }
    }
}
