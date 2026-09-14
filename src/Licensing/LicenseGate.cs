using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace PitMine3D.Kylin.Licensing;

/// <summary>
/// 启动闸 + 运行期心跳（移植原 <c>PitMineApp.Licensing.LicenseGate</c>）。
/// 授权的"拦"只发生在这两处，其余代码一律不感知授权 —— 这条边界照搬，不要往别处铺。
///
/// **失效时一律放行**（原版注释原话：授权模块自身故障不该把软件锁死）：
/// `Evaluate` 抛异常 → 当没拦；心跳抛异常 → 当没到期。宁可少拦一次，也不能让授权模块变成打不开软件的理由。
///
/// 与原版的差异（WPF → Avalonia 的必然改法）：
///   · 原版启动闸是**模态** `MessageBox` + `ShowDialog`，可以在 `MainWindow` 之前同步问完；
///     Avalonia 没有"没有主窗口时同步弹模态"这条路，故改为：**主窗口先建但不显示**，
///     把注册窗挂在它上面模态问；用户没激活就退出。
///   · 心跳定时器换成 Avalonia 的 <see cref="DispatcherTimer"/>。
/// </summary>
internal static class LicenseGate
{
    private static DispatcherTimer? _timer;
    private static bool _handling;

    /// <summary>
    /// 启动时检查。返回 false 表示"已到期且用户没有完成注册"，调用方必须终止启动。
    /// 未到期但快到期时把提醒文字交给 <paramref name="warn"/>（不拦启动）。
    ///
    /// 是 async 的：到期时要弹注册窗**模态**等用户办完，而 Avalonia 的模态只能 await ——
    /// 不能像 WPF 那样在同步流程里 `ShowDialog()` 就地阻塞（硬凑嵌套消息泵是给自己埋雷）。
    /// </summary>
    public static async System.Threading.Tasks.Task<bool> CheckAtStartupAsync(Window owner, Action<string>? warn = null)
    {
        LicenseStatusInfo info;
        try { info = LicenseService.Evaluate(); }
        catch { return true; }        // 授权模块自身故障不该把软件锁死

        if (info.Blocked) return await PromptRegistrationAsync(owner);

        if (info.Status == LicenseStatus.Trial && info.DaysLeft <= TrialPolicy.WarnDays)
            warn?.Invoke($"试用期剩余 {info.DaysLeft} 天（至 {info.ExpiryText}），到期后需注册才能继续使用。"
                       + "可在「注册」中查看本机机器码并办理。");
        return true;
    }

    /// <summary>主窗口就绪后启动心跳：一直开着不关的实例也要能到期。</summary>
    public static void StartHeartbeat(Func<Window?> owner)
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(TrialPolicy.HeartbeatInterval, DispatcherPriority.Background,
                                     (_, _) => _ = OnHeartbeatAsync(owner));
        _timer.Start();
    }

    private static async System.Threading.Tasks.Task OnHeartbeatAsync(Func<Window?> owner)
    {
        if (_handling) return;
        bool justExpired;
        try { justExpired = LicenseService.Heartbeat(); }
        catch { return; }
        if (!justExpired) return;      // 只在"刚跨过到期点"那一次拦

        _handling = true;
        try
        {
            var w = owner();
            if (w == null) return;     // 没有可挂的窗就不拦(极少见: 主窗已关但心跳还没停)
            if (!await PromptRegistrationAsync(w))
                (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch { /* 拦人这条路出错就当没拦 */ }
        finally { _handling = false; }
    }

    /// <summary>模态弹注册窗，等它关掉；返回 true = 已完成注册可以继续。</summary>
    private static async System.Threading.Tasks.Task<bool> PromptRegistrationAsync(Window owner)
    {
        try
        {
            await new Views.RegisterWindow().ShowDialog(owner);
            return !LicenseService.Current.Blocked;
        }
        catch { return true; }   // 连注册窗都开不出来时放行, 别把人锁在门外
    }
}
