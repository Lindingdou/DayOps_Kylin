using System;
using Avalonia.Threading;

namespace PitMine3D.Kylin.Views.Startup;

/// <summary>
/// 启动页生命周期管理（对应原 PitMineApp.Startup.StartupSplash）。
///
/// 与原版的差别只有一处、且是被迫的：原版 WPF 把启动页放在**独立 STA 线程**上跑，
/// 好让主线程同步加载模块时进度条照转。Avalonia 的窗口必须建在同一个 UI 线程上，
/// 起不了第二个 UI 线程，所以这里改成「每次 Report 之后把消息泵跑一轮」
/// （<see cref="Dispatcher.RunJobs()"/>）—— 主线程忙着建主窗口时，进度照样一格格往前走。
///
/// 任何一步失败都不许影响主程序启动：整条通路裹 try/catch，出问题就当没有启动页。
/// </summary>
public sealed class StartupSplash
{
    private SplashWindow? _window;
    private bool _closed;

    /// <summary>显示启动页（失败静默降级为"无启动页"）。</summary>
    public void Show()
    {
        try
        {
            _window = new SplashWindow();
            _window.Show();
            Pump(260);   // 首帧: 让渲染线程把这块卡片真画出来(只 RunJobs 不给时间, 窗口会是一片空白)
        }
        catch (Exception ex)
        {
            _window = null;
            PitMine3D.Kylin.CrashLog.Write("启动页", "显示失败(不影响启动): " + ex.Message);
        }
    }

    /// <summary>更新进度并让界面立刻重画一轮。</summary>
    public void Report(double fraction, string? status = null, string? module = null)
    {
        if (_window == null || _closed) return;
        try
        {
            _window.SetProgress(fraction, status, module);
            Pump(35);    // 每步给渲染线程一点时间, 进度条/文案才跟得上
        }
        catch { /* 进度失败不影响启动 */ }
    }

    /// <summary>关闭启动页（幂等）。</summary>
    public void Close()
    {
        if (_window == null || _closed) return;
        // PITMINE_SPLASH_HOLD=<毫秒>：多留一会儿再关 —— 交付截图/演示用，缺省不生效。
        if (int.TryParse(Environment.GetEnvironmentVariable("PITMINE_SPLASH_HOLD"), out int hold) && hold > 0)
            Pump(Math.Min(hold, 30000));
        _closed = true;
        try { _window.Close(); } catch { }
        _window = null;
    }

    /// <summary>
    /// 把排队的 UI 作业跑一轮，并留一点时间给渲染线程真正出帧。
    /// 只 RunJobs 不留时间的话：布局/合成提交是做了，可渲染线程还没画完就被主线程接着占住，
    /// 启动页就是一块白板 —— 主线程忙着建主窗口，它没有别的机会画。
    /// </summary>
    private static void Pump(int ms)
    {
        try
        {
            var until = DateTime.UtcNow.AddMilliseconds(Math.Max(ms, 0));
            do
            {
                Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(8);
            }
            while (DateTime.UtcNow < until);
        }
        catch { }
    }
}
