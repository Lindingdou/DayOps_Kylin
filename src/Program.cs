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
        NativeCrashHandler.Install(CrashLog.Path);   // 段错误时把原生调用栈写进日志(不依赖 gdb)
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
                // 麒麟(UKUI)没有全局菜单服务 com.canonical.AppMenu.Registrar, Avalonia 默认会去 DBus
                // 导出应用菜单并抛 ServiceUnknown, 由终结器线程重抛(crash.log 里的 [TASK] 那条)。
                // 我们的菜单本来就画在窗口内, 不需要全局菜单 —— 直接关掉这条 DBus 通路。
                UseDBusMenu = false,
                // 麒麟上的 fcitx 与 Avalonia 期望的 DBus 接口对不上(SetCapacity/DestroyIC 方法不存在),
                // 出错后销毁输入上下文时进程直接段错误 —— 实测崩溃紧跟在这串 [IME] Error 之后。
                // 视口/命令行都不需要中文输入法候选框, 关掉这条通路。要打开: PITMINE_IME=1
                EnableIme = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PITMINE_IME")),
                GlProfiles = BuildGlProfiles(),
            })
            .WithInterFont()
            .LogToTrace(LogEventLevel.Warning);

    /// <summary>
    /// 请求的 GL 版本档位。默认从高到低试；PITMINE_GL_PROFILE 可把指定档位顶到最前，
    /// 用来在有问题的驱动上快速二分（实测格兰菲 Arise1020 + Mesa 25.0 在 GL 4.0 下首帧段错误）。
    /// 取值: 4.0 / 3.2 / 3.0 / 2.1 / es3.2 / es3.0 / es2.0
    /// </summary>
    private static List<GlVersion> BuildGlProfiles()
    {
        var list = new List<GlVersion>
        {
            new(GlProfileType.OpenGL, 4, 0),
            new(GlProfileType.OpenGL, 3, 2),
            new(GlProfileType.OpenGL, 3, 0),
            new(GlProfileType.OpenGLES, 3, 2),
            new(GlProfileType.OpenGLES, 3, 0),
            new(GlProfileType.OpenGL, 2, 1, true),   // ← 老驱动兜底(兼容配置)
            new(GlProfileType.OpenGLES, 2, 0),
        };
        string? want = Environment.GetEnvironmentVariable("PITMINE_GL_PROFILE");
        if (string.IsNullOrWhiteSpace(want)) return list;

        want = want.Trim().ToLowerInvariant();
        bool es = want.StartsWith("es");
        string num = es ? want.Substring(2) : want;
        var parts = num.Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out int maj) && int.TryParse(parts[1], out int min))
        {
            var type = es ? GlProfileType.OpenGLES : GlProfileType.OpenGL;
            var pick = new GlVersion(type, maj, min, !es && maj == 2);
            list.RemoveAll(v => v.Type == type && v.Major == maj && v.Minor == min);
            list.Insert(0, pick);
            CrashLog.Write("GL", $"按 PITMINE_GL_PROFILE={want} 优先请求 {type} {maj}.{min}");
        }
        else CrashLog.Write("GL", $"PITMINE_GL_PROFILE={want} 解析不了, 用默认档位表");
        return list;
    }
}
