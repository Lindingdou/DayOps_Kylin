using System;

namespace PitMine3D.Kylin.Licensing
{
    /// <summary>
    /// 试用/授权的口径常量。<b>发版前要改的就这一个文件。</b>
    /// </summary>
    internal static class TrialPolicy
    {
        // ══════════════════════════════════════════════════════════════════════════
        //  试用到期日 —— 固定日历日，与首次运行无关：同一个安装包发出去的所有拷贝
        //  都在这一天到期。换新一批试用期就改这里重新出包。
        //  下面这个值 = 北京时间 2026-10-15 23:59:59（UTC 差 8 小时）。
        //  在此之前不需要注册，全部功能照常使用。
        // ══════════════════════════════════════════════════════════════════════════
        public static readonly DateTime TrialExpiryUtc =
            new DateTime(2026, 10, 15, 15, 59, 59, DateTimeKind.Utc);

        /// <summary>剩余不足这么多天时，每次启动弹一次提醒（不拦启动）。</summary>
        public const int WarnDays = 15;

        /// <summary>
        /// 时钟回拨的容忍量。低于这个幅度不提示——跨时区带笔记本、NTP 校时都会造成
        /// 几十分钟级的负跳变，不该当成作弊。注意：不管提不提示，判定一律走
        /// <see cref="TimeGuard.EffectiveNowUtc"/>，回拨本身不会换来额外时间。
        /// </summary>
        public static readonly TimeSpan RollbackTolerance = TimeSpan.FromHours(6);

        /// <summary>运行期复检间隔：只开着不关也会到期，不能靠"启动时查一次"。</summary>
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(10);

        /// <summary>合理时间的上下界。越界的时间戳一律不采信（防坏文件时间戳误伤）。</summary>
        public static readonly DateTime SaneMinUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static readonly DateTime SaneMaxUtc = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public const string ProductName = "DayOps 露天矿三维设计与计划系统";

        /// <summary>联系方式，注册窗里显示给用户，让他把机器码报过来。</summary>
        public const string VendorContact = "请联系软件供应方索取激活码";
    }
}
