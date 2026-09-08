using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PitMine3D.Kylin;

/// <summary>
/// 崩溃/启动日志。麒麟等目标机上"双击就闪退"最难的是拿不到原因 ——
/// 这里把未处理异常与启动环境写到用户数据目录的 crash.log(同时打到 stderr)，用户只需回传一个文件。
/// </summary>
public static class CrashLog
{
    private static string? _path;

    /// <summary>日志文件路径(用户数据目录; 建不了则临时目录)。</summary>
    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            try
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string dir = System.IO.Path.Combine(baseDir, "PitMine3D.Kylin");
                    Directory.CreateDirectory(dir);
                    return _path = System.IO.Path.Combine(dir, "crash.log");
                }
            }
            catch { }
            return _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pitmine3d-crash.log");
        }
    }

    /// <summary>
    /// 断点追踪：PITMINE_TRACE=1 时每步落一条并立即刷盘。
    /// 原生崩溃(SIGSEGV)不会走托管异常钩子，crash.log 里不会有堆栈；
    /// 靠「最后一条 TRACE 停在哪」来定位崩在哪一步。默认关闭，不影响正常运行。
    /// </summary>
    public static readonly bool TraceOn =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PITMINE_TRACE"));

    public static void Trace(string step)
    {
        if (!TraceOn) return;
        try { File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] [TRACE] {step}{Environment.NewLine}", Encoding.UTF8); }
        catch { }
    }

    /// <summary>
    /// 标记"硬件 OpenGL 不可靠"，启动器下次启动即自动改用软件渲染。
    /// 用于已知会崩的驱动：与启动器 .gl-crashed 同一个文件，两条路径(崩溃退出码 / 驱动黑名单)共用。
    /// </summary>
    public static void MarkGlUnsafe(string reason)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path) ?? "";
            if (dir.Length == 0) return;
            File.WriteAllText(System.IO.Path.Combine(dir, ".gl-crashed"), reason + Environment.NewLine, Encoding.UTF8);
            Write("GL", $"已标记硬件 OpenGL 不可靠({reason}); 下次启动自动改用软件渲染");
        }
        catch { }
    }

    public static void Write(string tag, string text)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{tag}] {text}";
        Console.Error.WriteLine(line);
        try { File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    /// <summary>启动横幅：进程/系统/运行时/关键环境变量 —— 判断"是否满足系统要求"靠它。</summary>
    public static void WriteStartupBanner()
    {
        var sb = new StringBuilder();
        sb.AppendLine("──────── PitMine3D 麒麟版启动 ────────");
        // 版本戳: 排查时第一件事就是确认"跑的到底是哪一版"(收到过旧版日志, 白查半天)
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            string ver = asm?.GetName().Version?.ToString() ?? "?";
            string built = "?";
            var loc = asm?.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc)) built = File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm");
            else
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0 && File.Exists(exe)) built = File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
            }
            sb.AppendLine($"  程序版本: {ver}   构建时间: {built}");
        }
        catch { }
        sb.AppendLine($"  版本目录: {AppContext.BaseDirectory}");
        sb.AppendLine($"  OS      : {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"  运行时  : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine($"  区域    : Invariant={AppContext.TryGetSwitch("System.Globalization.Invariant", out var inv) && inv}");
        foreach (var k in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XDG_SESSION_TYPE", "LD_LIBRARY_PATH",
                                  "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "LIBGL_ALWAYS_SOFTWARE" })
            sb.AppendLine($"  {k,-38}= {Environment.GetEnvironmentVariable(k) ?? "(未设)"}");
        Write("START", sb.ToString().TrimEnd());
    }

    /// <summary>挂全局异常钩子；任何未处理异常都落盘后再让进程按原样结束。</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("FATAL", (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString() ?? "(未知异常)");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        { Write("TASK", e.Exception.ToString()); e.SetObserved(); };
    }
}
