using System;

namespace PitMine3D.Kylin.Licensing
{
    internal enum LicenseStatus
    {
        /// <summary>试用期内，可正常使用。</summary>
        Trial,
        /// <summary>已注册（永久或按期授权且未到期）。</summary>
        Licensed,
        /// <summary>已到期，必须注册才能继续使用。</summary>
        Expired,
    }

    /// <summary>一次评估的结论。全部字段都是快照，不要缓存跨会话。</summary>
    internal sealed class LicenseStatusInfo
    {
        public LicenseStatus Status { get; init; }
        /// <summary>true = 永久授权，<see cref="ExpiryUtc"/>/<see cref="DaysLeft"/> 无意义。</summary>
        public bool Perpetual { get; init; }
        public DateTime ExpiryUtc { get; init; }
        /// <summary>剩余天数（向上取整）；已到期为 0。</summary>
        public int DaysLeft { get; init; }
        /// <summary>检测到系统时钟被往回拨过。</summary>
        public bool RollbackDetected { get; init; }
        /// <summary>到期/异常原因，直接显示给用户。</summary>
        public string Reason { get; init; } = "";

        public bool Blocked => Status == LicenseStatus.Expired;

        /// <summary>到期日的本地时间文本；永久授权返回"永久"。</summary>
        public string ExpiryText => Perpetual ? "永久" : ExpiryUtc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// 授权状态的唯一入口。启动闸（<c>App.OnStartup</c>）、运行期心跳、注册窗都只跟它打交道。
    ///
    /// 判定链：本地状态 → 时间基准（<see cref="TimeGuard"/>，防回拨）→ 激活码验签
    /// → 试用固定到期日。每次评估都会把有效时间并进高水位并落盘，所以"运行过一次"
    /// 这件事本身就把时间钉住了：之后再把系统时间拨回去也退不回来。
    /// </summary>
    internal static class LicenseService
    {
        private static LicenseState? _state;
        private static LicenseStatusInfo? _current;

        /// <summary>本机机器码（展示给用户抄给供应方）。</summary>
        public static string MachineCodeText => MachineCode.Text;

        /// <summary>最近一次评估结果；尚未评估过则先评估。</summary>
        public static LicenseStatusInfo Current => _current ??= Evaluate();

        /// <summary>
        /// 重新评估并落盘。启动时调一次，之后每 <see cref="TrialPolicy.HeartbeatInterval"/> 调一次。
        /// 任何异常都退化为"按试用处理"而不是崩溃——授权模块不该成为打不开软件的理由。
        /// </summary>
        public static LicenseStatusInfo Evaluate()
        {
            try
            {
                _state = LicenseStore.Read();
                TimeGuard.Initialize(_state);

                _state ??= new LicenseState { FirstSeenUtc = TimeGuard.EffectiveNowUtc };
                if (_state.FirstSeenUtc == default) _state.FirstSeenUtc = TimeGuard.EffectiveNowUtc;

                TimeGuard.Advance(_state);
                _state.RunCount++;
                LicenseStore.Write(_state);

                return _current = Judge(_state);
            }
            catch (Exception ex)
            {
                // 评估本身出错时不拦人：按"试用中"放行，把原因写进 Reason 供注册窗显示。
                return _current = new LicenseStatusInfo
                {
                    Status = LicenseStatus.Trial,
                    ExpiryUtc = TrialPolicy.TrialExpiryUtc,
                    DaysLeft = DaysBetween(DateTime.UtcNow, TrialPolicy.TrialExpiryUtc),
                    Reason = "授权状态评估异常：" + ex.Message,
                };
            }
        }

        /// <summary>
        /// 运行期心跳：只推进高水位并复检是否已到期，不重读存储。
        /// 返回 true 表示"刚刚跨过到期点"，调用方应当拦下来。
        /// </summary>
        public static bool Heartbeat()
        {
            try
            {
                if (_state is null) { Evaluate(); return Current.Blocked; }

                TimeGuard.Advance(_state);
                LicenseStore.Write(_state);

                bool wasBlocked = _current?.Blocked ?? false;
                _current = Judge(_state);
                return _current.Blocked && !wasBlocked;
            }
            catch { return false; }
        }

        /// <summary>
        /// 登记一个激活码。成功返回 true 并刷新 <see cref="Current"/>；
        /// 失败时 <paramref name="message"/> 是可以直接弹给用户看的原因。
        /// </summary>
        public static bool TryActivate(string? activationCode, out string message)
        {
            var info = LicenseKey.Verify(activationCode, MachineCode.Id);
            if (!info.Valid)
            {
                message = info.Error;
                return false;
            }

            var now = TimeGuard.EffectiveNowUtc;
            if (!info.Perpetual && info.ExpiryUtc <= now)
            {
                message = $"该激活码的授权期已于 {info.ExpiryUtc.ToLocalTime():yyyy-MM-dd} 结束，请索取新的激活码。";
                return false;
            }

            try
            {
                _state ??= new LicenseState { FirstSeenUtc = now };
                _state.ActivationCode = (activationCode ?? "").Trim();
                TimeGuard.Advance(_state);
                LicenseStore.Write(_state);
                _current = Judge(_state);
            }
            catch (Exception ex)
            {
                message = "激活码有效，但写入本机授权信息失败：" + ex.Message;
                return false;
            }

            message = info.Perpetual
                ? "注册成功，本机已获永久授权。"
                : $"注册成功，授权有效期至 {info.ExpiryUtc.ToLocalTime():yyyy-MM-dd}。";
            return true;
        }

        // ── 判定 ───────────────────────────────────────────────────────────────────

        private static LicenseStatusInfo Judge(LicenseState state)
        {
            var now = TimeGuard.EffectiveNowUtc;
            bool rollback = TimeGuard.RollbackDetected;

            // 1) 已登记激活码 —— 验签通过才算数；换了机器（机器码变了）会验不过，
            //    此时不报错，直接退回试用口径判定。
            if (!string.IsNullOrWhiteSpace(state.ActivationCode))
            {
                var lic = LicenseKey.Verify(state.ActivationCode, MachineCode.Id);
                if (lic.Valid)
                {
                    if (lic.Perpetual)
                        return new LicenseStatusInfo
                        {
                            Status = LicenseStatus.Licensed,
                            Perpetual = true,
                            ExpiryUtc = DateTime.MaxValue,
                            DaysLeft = int.MaxValue,
                            RollbackDetected = rollback,
                        };

                    if (lic.ExpiryUtc > now)
                        return new LicenseStatusInfo
                        {
                            Status = LicenseStatus.Licensed,
                            ExpiryUtc = lic.ExpiryUtc,
                            DaysLeft = DaysBetween(now, lic.ExpiryUtc),
                            RollbackDetected = rollback,
                        };

                    return new LicenseStatusInfo
                    {
                        Status = LicenseStatus.Expired,
                        ExpiryUtc = lic.ExpiryUtc,
                        RollbackDetected = rollback,
                        Reason = $"本机授权已于 {lic.ExpiryUtc.ToLocalTime():yyyy-MM-dd} 到期。",
                    };
                }
            }

            // 2) 未注册 —— 走固定试用到期日。
            if (now > TrialPolicy.TrialExpiryUtc)
                return new LicenseStatusInfo
                {
                    Status = LicenseStatus.Expired,
                    ExpiryUtc = TrialPolicy.TrialExpiryUtc,
                    RollbackDetected = rollback,
                    // 回拨的说明不写进 Reason：注册窗有专门一行 ⚠ 提示，两处都写就重复了。
                    // 开不出注册窗时的兜底提示由 LicenseGate 自己补这句。
                    Reason = $"试用期已于 {TrialPolicy.TrialExpiryUtc.ToLocalTime():yyyy-MM-dd} 结束。",
                };

            return new LicenseStatusInfo
            {
                Status = LicenseStatus.Trial,
                ExpiryUtc = TrialPolicy.TrialExpiryUtc,
                DaysLeft = DaysBetween(now, TrialPolicy.TrialExpiryUtc),
                RollbackDetected = rollback,
            };
        }

        private static int DaysBetween(DateTime from, DateTime to)
        {
            if (to <= from) return 0;
            double d = Math.Ceiling((to - from).TotalDays);
            return d > int.MaxValue ? int.MaxValue : (int)d;
        }
    }
}
