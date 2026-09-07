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
