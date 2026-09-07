using System;
using System.Diagnostics;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Logging;
using Avalonia.OpenGL;

namespace PitMine3D.Kylin;

internal static class Program
{
    // Avalonia 初始化必须在任何 SynchronizationContext 之前，勿在 Main 里 new 任何 Avalonia 对象。
    [STAThread]
    public static void Main(string[] args)
    {
        // 把 Avalonia 日志（含 OpenGL 初始化告警）导到 stderr，便于在麒麟/WSL 上诊断
        Trace.Listeners.Add(new ConsoleTraceListener(true));
        CrashLog.Install();
        CrashLog.WriteStartupBanner();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            CrashLog.Write("EXIT", "正常退出");
        }
        catch (Exception ex)
        {
            // 启动期异常(平台后端/GL/字体等)在目标机上表现为"双击即闪退"——落盘后给出可读提示再退出。
            CrashLog.Write("FATAL", ex.ToString());
            Console.Error.WriteLine($"启动失败。诊断日志: {CrashLog.Path}");
            Environment.ExitCode = 1;
        }
    }

    // 平台自动探测：Windows 走 ANGLE/WGL，麒麟(Linux) 走 EGL/GLX —— 同一份代码。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia 默认把 llvmpipe 软件渲染器拉黑（嫌慢）。WSLg 及无独显/驱动未就绪的
            // 信创整机会退到 llvmpipe —— 放行它，否则 OpenGlControlBase 拿不到 GLX 上下文、视口空白。
            // GlProfiles: 默认只协商到 桌面 GL 3.0 / GLES 2.0；国产 GPU 驱动常止步 GL 2.1，
            // 那种机器上三个桌面档全失败、EGL 又不一定有 → 视口起不来。故补一档 GL 2.1(兼容配置)。
            // 渲染器本身只用 2.0 核心调用(VBO/着色器/属性指针)，VAO 缺失自动空转，着色器按版本挑方言。
            .With(new X11PlatformOptions
            {
                GlxRendererBlacklist = Array.Empty<string>(),
                GlProfiles = new List<GlVersion>
                {
                    new(GlProfileType.OpenGL, 4, 0),
                    new(GlProfileType.OpenGL, 3, 2),
                    new(GlProfileType.OpenGL, 3, 0),
                    new(GlProfileType.OpenGLES, 3, 2),
                    new(GlProfileType.OpenGLES, 3, 0),
                    new(GlProfileType.OpenGL, 2, 1, true),   // ← 老驱动兜底(兼容配置)
                    new(GlProfileType.OpenGLES, 2, 0),
                }
            })
            .WithInterFont()
            .LogToTrace(LogEventLevel.Warning);
}
