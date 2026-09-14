using System;
using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Licensing
{
    /// <summary>
    /// 防回拨的时间基准。判到期一律用 <see cref="EffectiveNowUtc"/>，任何地方都不要直接用
    /// <c>DateTime.UtcNow</c>——那正是被改系统时间就失效的那条路。
    ///
    /// 有效时间 = max(系统时钟, 自己的高水位, 系统旁证时间) 三者取最大：
    ///   · 系统时钟   —— 正常情况下就是它，另外两项都不会超过它
    ///   · 高水位     —— 本软件每次运行/每次心跳记下的"见过的最晚时刻"，只增不减，
    ///                   存在三处加密副本里（见 <see cref="LicenseStore"/>）
    ///   · 系统旁证   —— 磁盘上一批系统目录的最后写入时间。把时钟拨回去并不会把这些
    ///                   时间戳一起改回去，所以哪怕把本软件的三处副本全删了、再把
    ///                   时钟拨到去年，这一项照样能把有效时间顶回来
    ///
    /// 取最大值而不是"发现回拨就拉黑"，是刻意的：拉黑一旦误判就把正常用户砸死了，
    /// 而取最大值最坏情况只是"用户没占到便宜"。
    /// </summary>
    internal static class TimeGuard
    {
        private static DateTime _effectiveNow;
        private static DateTime _highWater;
        private static bool _rollbackSeen;

        /// <summary>本次会话的有效当前时刻（UTC）。</summary>
        public static DateTime EffectiveNowUtc
        {
            get
            {
                // 会话内随真实时钟往前走：一直开着的实例也能正常到期。
                var now = DateTime.UtcNow;
                return now > _effectiveNow ? now : _effectiveNow;
            }
        }

        /// <summary>检测到系统时钟被明显往回拨过（超过容忍量）。仅用于提示。</summary>
        public static bool RollbackDetected => _rollbackSeen;

        /// <summary>启动时调用一次：把三个来源汇总成有效时间。</summary>
        public static void Initialize(LicenseState? state)
        {
            var clock = DateTime.UtcNow;
            _highWater = state?.HighWaterUtc ?? default;

            _rollbackSeen = _highWater != default && clock < _highWater - TrialPolicy.RollbackTolerance;

            var evidence = ProbeSystemEvidenceUtc();

            _effectiveNow = clock;
            if (_highWater > _effectiveNow) _effectiveNow = _highWater;
            if (evidence > _effectiveNow) _effectiveNow = evidence;
        }

        /// <summary>把当前有效时间并进高水位，返回要落盘的新状态（只增不减）。</summary>
        public static void Advance(LicenseState state)
        {
            var now = EffectiveNowUtc;
            if (now > state.HighWaterUtc) state.HighWaterUtc = now;
            if (now > _highWater) _highWater = now;
            if (now > _effectiveNow) _effectiveNow = now;
        }

        // ── 系统旁证时间 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 采一批系统/用户目录的最后写入时间，取<b>第三大</b>的那个。
        ///
        /// 不取最大值：个别安装程序会写出年份离谱的时间戳，取最大值会被这一个坏样本
        /// 顶上天，把正常用户直接判到期。要求至少三处独立目录都晚于某个时刻，才认。
        /// 样本不足三个时退回最小值（宁可这一项失效，也不能误判）。
        /// </summary>
        private static DateTime ProbeSystemEvidenceUtc()
        {
            var samples = new List<DateTime>(64);

            void Take(string? path)
            {
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    DateTime t;
                    if (Directory.Exists(path)) t = Directory.GetLastWriteTimeUtc(path);
                    else if (File.Exists(path)) t = File.GetLastWriteTimeUtc(path);
                    else return;

                    if (t > TrialPolicy.SaneMinUtc && t < TrialPolicy.SaneMaxUtc)
                        samples.Add(t);
                }
                catch { }
            }

            void TakeChildren(string? root, int max)
            {
                if (string.IsNullOrEmpty(root)) return;
                try
                {
                    int n = 0;
                    foreach (string dir in Directory.EnumerateDirectories(root))
                    {
                        Take(dir);
                        if (++n >= max) break;
                    }
                }
                catch { }
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Take(local);
            Take(roaming);
            Take(profile);
            Take(Path.GetTempPath());
            Take(Environment.SystemDirectory);      // 麒麟上是空串, 由 Take 的空值守卫跳过
            Take(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            Take(Environment.GetFolderPath(Environment.SpecialFolder.Recent));
            TakeChildren(local, 24);
            TakeChildren(roaming, 24);

            // 麒麟/Linux 旁证：Windows 那几项在这里一半是空串(SystemDirectory/Recent)，
            // 样本不足三个整项就会失效。补几处**系统自己会写、用户不会去改**的目录 ——
            // 日志与包数据库随每次装包/开机滚动，把时钟拨回去并不会把它们的时间戳一起拨回去。
            if (!OperatingSystem.IsWindows())
            {
                foreach (var p in new[]
                         {
                             "/var/log", "/var/lib/dpkg", "/var/lib/rpm", "/var/cache",
                             "/etc", "/run", "/var/tmp", "/usr/share",
                         })
                    Take(p);
                TakeChildren("/var/log", 24);
                TakeChildren(Path.Combine(profile, ".config"), 24);
                TakeChildren(Path.Combine(profile, ".cache"), 24);
            }

            if (samples.Count < 3) return DateTime.MinValue;
            samples.Sort();
            return samples[^3];      // 第三大
        }
    }
}
