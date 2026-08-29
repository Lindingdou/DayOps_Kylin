using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;

namespace PitMine3D.Kylin;

internal static class Program
{
    // Avalonia 初始化必须在任何 SynchronizationContext 之前，勿在 Main 里 new 任何 Avalonia 对象。
    [STAThread]
    public static void Main(string[] args)
    {
        // 把 Avalonia 日志（含 OpenGL 初始化告警）导到 stderr，便于在麒麟/WSL 上诊断
        Trace.Listeners.Add(new ConsoleTraceListener(true));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 平台自动探测：Windows 走 ANGLE/WGL，麒麟(Linux) 走 EGL/GLX —— 同一份代码。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia 默认把 llvmpipe 软件渲染器拉黑（嫌慢）。WSLg 及无独显/驱动未就绪的
            // 信创整机会退到 llvmpipe —— 放行它，否则 OpenGlControlBase 拿不到 GLX 上下文、视口空白。
            .With(new X11PlatformOptions { GlxRendererBlacklist = Array.Empty<string>() })
            .WithInterFont()
            .LogToTrace(LogEventLevel.Warning);
}
