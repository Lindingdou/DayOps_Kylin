using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PitMine3D.Doctor;

/// <summary>
/// PitMine3D 麒麟环境体检器 —— 独立单文件小程序, 免安装、不依赖主程序。
/// 目的: 主程序在目标机"闪退"时, 先把「哪一项不满足」查清楚再动代码。
/// 检查: 系统/内核/glibc · 必需动态库 · ICU · 显示会话 · GLX/EGL 实际能拿到的 OpenGL 版本与 GLSL 版本 · 字体。
/// 结论按主程序的实际要求给判定(要 GL≥2.1 + 着色器; 2.1 走兼容方言)。
/// </summary>
internal static class Program
{
    private static readonly List<string> Lines = new();
    private static int _fail, _warn;

    private static void W(string s) { Console.WriteLine(s); Lines.Add(s); }
    private static void Head(string s) { W(""); W("── " + s + " " + new string('─', Math.Max(0, 58 - s.Length))); }
    private static void Ok(string item, string detail = "") { W($"  [ 正常 ] {item}{(detail.Length > 0 ? " — " + detail : "")}"); }
    private static void Warn(string item, string detail) { _warn++; W($"  [ 注意 ] {item} — {detail}"); }
    private static void Bad(string item, string detail) { _fail++; W($"  [ 缺失 ] {item} — {detail}"); }

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }   // 终端非 UTF-8 时不致中断
        W("PitMine3D 麒麟环境体检器  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        W("(独立程序, 不改动也不需要主程序; 把本报告回传即可定位闪退原因)");

        System();
        Libraries();
        Icu();
        Session();
        Graphics();
        Fonts();
        Verdict();

        string log = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), "pitmine3d-doctor.log");
        try { File.WriteAllText(log, string.Join(Environment.NewLine, Lines), new UTF8Encoding(false)); Console.WriteLine($"\n报告已保存: {log}"); }
        catch (Exception ex) { Console.WriteLine($"\n(报告保存失败: {ex.Message})"); }
        return _fail > 0 ? 1 : 0;
    }

    // ─────────────────────────── 系统 ───────────────────────────
    private static void System()
    {
        Head("系统");
        W($"  OS        : {RuntimeInformation.OSDescription}");
        W($"  架构      : {RuntimeInformation.OSArchitecture} (本程序进程 {RuntimeInformation.ProcessArchitecture})");
        W($"  运行时    : {RuntimeInformation.FrameworkDescription}");
        foreach (var (file, label) in new[] { ("/etc/os-release", "发行版"), ("/etc/kylin-build", "麒麟构建") })
            if (File.Exists(file))
                foreach (var l in File.ReadAllLines(file))
                    if (l.StartsWith("PRETTY_NAME=") || l.StartsWith("VERSION=") || label == "麒麟构建")
                        W($"  {label}    : {l.Replace("PRETTY_NAME=", "").Trim('"')}");

        // glibc: .NET 8 要求 ≥ 2.23
        try
        {
            var ver = Marshal.PtrToStringAnsi(gnu_get_libc_version());
            if (ver != null && Version.TryParse(ver.Contains('.') ? ver : ver + ".0", out var v))
            {
                if (v >= new Version(2, 23)) Ok($"glibc {ver}", ".NET 8 要求 ≥ 2.23");
                else Bad($"glibc {ver}", ".NET 8 要求 ≥ 2.23 —— 主程序无法运行");
            }
            else W($"  glibc     : {ver ?? "(取不到)"}");
        }
        catch (Exception ex) { Warn("glibc 版本", "查询失败: " + ex.Message); }
    }

    [DllImport("libc", EntryPoint = "gnu_get_libc_version")]
    private static extern IntPtr gnu_get_libc_version();

    // ────────────────────────── 动态库 ──────────────────────────
    private static void Libraries()
    {
        Head("必需动态库 (主程序运行时会加载)");
        // (soname, 说明, 缺了是否致命)
        var libs = new (string so, string why, bool fatal)[]
        {
            ("libstdc++.so.6",   "C++ 运行时(Skia/内核库依赖)",       true),
            ("libX11.so.6",      "X11 窗口(Avalonia 显示后端)",        true),
            ("libXext.so.6",     "X11 扩展",                           false),
            ("libXi.so.6",       "X11 输入",                           false),
            ("libXrandr.so.2",   "X11 分辨率/多屏",                    false),
            ("libXcursor.so.1",  "X11 光标",                           false),
            ("libXrender.so.1",  "X11 渲染扩展",                       false),
            ("libICE.so.6",      "会话管理",                           false),
            ("libSM.so.6",       "会话管理",                           false),
            ("libfontconfig.so.1","字体查找(Skia 文字渲染)",           true),
            ("libfreetype.so.6", "字形光栅化",                         true),
            ("libGL.so.1",       "OpenGL(桌面 GLX 路径)",              false),
            ("libEGL.so.1",      "EGL(GLES 路径, 二者有其一即可)",     false),
        };
        bool gl = false, egl = false;
        foreach (var (so, why, fatal) in libs)
        {
            if (NativeLibrary.TryLoad(so, out var h))
            {
                Ok(so, why);
                if (so.StartsWith("libGL.")) gl = true;
                if (so.StartsWith("libEGL.")) egl = true;
                NativeLibrary.Free(h);
            }
            else if (fatal) Bad(so, why + " —— 主程序会加载失败");
            else Warn(so, why + " (缺失可能导致个别功能异常)");
        }
        if (!gl && !egl) Bad("OpenGL", "libGL 与 libEGL 都没有 —— 三维视口无法初始化(主程序旧版会直接闪退)");
    }

    // ─────────────────────────── ICU ────────────────────────────
    private static void Icu()
    {
        Head("ICU (.NET 全球化依赖)");
        string? found = null;
        for (int v = 80; v >= 50 && found == null; v--)
            if (NativeLibrary.TryLoad($"libicuuc.so.{v}", out var h)) { found = $"libicuuc.so.{v}"; NativeLibrary.Free(h); }
        if (found == null && NativeLibrary.TryLoad("libicuuc.so", out var h2)) { found = "libicuuc.so"; NativeLibrary.Free(h2); }

        if (found != null) Ok("系统 ICU", found);
        else
        {
            Warn("系统 ICU", "系统里没有 libicuuc —— 主程序会改用随包携带的 ICU;"
                           + " 若安装包内也没有, 会退到 Invariant 模式(只影响区域格式, 程序仍可用)");
            string bundled = "/opt/pitmine3d/runtime-libs";
            if (Directory.Exists(bundled))
            {
                var f = Directory.GetFiles(bundled, "libicuuc.so.*");
                if (f.Length > 0) Ok("随包 ICU", Path.GetFileName(f[0]));
                else Warn("随包 ICU", $"{bundled} 里没有 libicuuc.so.*");
            }
            else W($"  (未安装主程序或安装路径不同: 没找到 {bundled})");
        }
    }

    // ────────────────────────── 显示会话 ─────────────────────────
    private static void Session()
    {
        Head("显示会话");
        string? disp = Environment.GetEnvironmentVariable("DISPLAY");
        string? way = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        string? type = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        W($"  DISPLAY={disp ?? "(未设)"}  WAYLAND_DISPLAY={way ?? "(未设)"}  XDG_SESSION_TYPE={type ?? "(未设)"}");
        if (string.IsNullOrEmpty(disp) && string.IsNullOrEmpty(way))
            Bad("显示会话", "DISPLAY 与 WAYLAND_DISPLAY 都为空 —— 图形程序无法启动(是否在 ssh/纯字符终端里运行?)");
        else if (string.IsNullOrEmpty(disp) && !string.IsNullOrEmpty(way))
            Warn("显示会话", "只有 Wayland。主程序走 X11 后端, 需要 XWayland; 若无则起不来");
        else Ok("显示会话", $"X11 DISPLAY={disp}");
    }

    // ─────────────────────── OpenGL 实测 ────────────────────────
    private const int GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION_ = 0x1F02, GL_SHADING = 0x8B8C;

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(IntPtr d);
    [DllImport("libX11.so.6")] private static extern IntPtr XRootWindow(IntPtr d, int scr);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr d);
    [DllImport("libGL.so.1")] private static extern bool glXQueryVersion(IntPtr dpy, out int maj, out int min);
    [DllImport("libGL.so.1")] private static extern IntPtr glXChooseVisual(IntPtr dpy, int screen, int[] attribs);
    [DllImport("libGL.so.1")] private static extern IntPtr glXCreateContext(IntPtr dpy, IntPtr vis, IntPtr share, bool direct);
    [DllImport("libGL.so.1")] private static extern bool glXMakeCurrent(IntPtr dpy, IntPtr drawable, IntPtr ctx);
    [DllImport("libGL.so.1")] private static extern void glXDestroyContext(IntPtr dpy, IntPtr ctx);
    [DllImport("libGL.so.1")] private static extern IntPtr glGetString(int name);
    [DllImport("libGL.so.1")] private static extern IntPtr glXQueryServerString(IntPtr dpy, int screen, int name);
    [DllImport("libGL.so.1")] private static extern bool glXIsDirect(IntPtr dpy, IntPtr ctx);

    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport("libEGL.so.1")] private static extern bool eglInitialize(IntPtr dpy, out int maj, out int min);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglQueryString(IntPtr dpy, int name);

    private static string GlVersionText = "", GlslText = "", GlRendererText = "";

    private static void Graphics()
    {
        Head("OpenGL 实测 (决定三维视口能否启动)");
        GlVersionText = GlslText = GlRendererText = "";

        // ① GLX(桌面 OpenGL) —— 主程序优先走这条
        try
        {
            IntPtr dpy = XOpenDisplay(IntPtr.Zero);
            if (dpy == IntPtr.Zero) { Bad("GLX", "打不开 X Display(DISPLAY 无效或无权限)"); }
            else
            {
                if (glXQueryVersion(dpy, out int gmaj, out int gmin)) Ok("GLX 版本", $"{gmaj}.{gmin}");
                else Warn("GLX", "glXQueryVersion 失败");

                // 双缓冲 RGBA + 深度缓冲(主程序需要深度测试)
                int[] attrs = { 4 /*GLX_RGBA*/, 5 /*GLX_DOUBLEBUFFER*/, 12 /*GLX_DEPTH_SIZE*/, 16,
                                8 /*GLX_RED_SIZE*/, 8, 9 /*GREEN*/, 8, 10 /*BLUE*/, 8, 0 /*None*/ };
                int screen = XDefaultScreen(dpy);
                IntPtr vis = glXChooseVisual(dpy, screen, attrs);
                if (vis == IntPtr.Zero) Bad("GLX 可视配置", "找不到 RGBA+深度16 的配置 —— 三维视口无法创建");
                else
                {
                    IntPtr ctx = glXCreateContext(dpy, vis, IntPtr.Zero, true);
                    if (ctx == IntPtr.Zero) Bad("GLX 上下文", "glXCreateContext 失败 —— 驱动不可用");
                    else
                    {
                        IntPtr root = XRootWindow(dpy, screen);
                        if (glXMakeCurrent(dpy, root, ctx))
                        {
                            GlRendererText = Str(glGetString(GL_RENDERER));
                            GlVersionText = Str(glGetString(GL_VERSION_));
                            GlslText = Str(glGetString(GL_SHADING));
                            W($"  厂商      : {Str(glGetString(GL_VENDOR))}");
                            W($"  渲染器    : {GlRendererText}");
                            W($"  GL 版本   : {GlVersionText}");
                            W($"  GLSL 版本 : {GlslText}");
                            W($"  直接渲染  : {(glXIsDirect(dpy, ctx) ? "是(硬件加速)" : "否(间接/软件)")}");
                            glXMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero);
                        }
                        else Warn("GLX MakeCurrent", "无法在根窗口上激活上下文(不一定影响真实窗口)");
                        glXDestroyContext(dpy, ctx);
                    }
                }
                XCloseDisplay(dpy);
            }
        }
        catch (DllNotFoundException) { Bad("GLX", "没有 libGL.so.1 —— 桌面 OpenGL 路径不可用"); }
        catch (Exception ex) { Warn("GLX 检测", ex.Message); }

        // ② EGL/GLES —— GLX 不行时主程序的备选
        try
        {
            IntPtr edpy = eglGetDisplay(IntPtr.Zero);
            if (edpy != IntPtr.Zero && eglInitialize(edpy, out int emaj, out int emin))
            {
                Ok("EGL", $"{emaj}.{emin}  厂商={Str(eglQueryString(edpy, 0x3053))}  可用 API={Str(eglQueryString(edpy, 0x308D))}");
            }
            else Warn("EGL", "初始化失败(若 GLX 正常可忽略)");
        }
        catch (DllNotFoundException) { Warn("EGL", "没有 libEGL.so.1(若 GLX 正常可忽略)"); }
        catch (Exception ex) { Warn("EGL 检测", ex.Message); }
    }

    private static string Str(IntPtr p) => p == IntPtr.Zero ? "(空)" : Marshal.PtrToStringAnsi(p) ?? "(空)";

    // ─────────────────────────── 字体 ───────────────────────────
    private static void Fonts()
    {
        Head("中文字体");
        string[] dirs = { "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/root", ".fonts") };
        int n = 0;
        foreach (var d in dirs)
            if (Directory.Exists(d))
                try { n += Directory.GetFiles(d, "*.tt*", SearchOption.AllDirectories).Length
                          + Directory.GetFiles(d, "*.otf", SearchOption.AllDirectories).Length; } catch { }
        if (n > 0) Ok("字体文件", $"共 {n} 个(界面自带 Inter 字体, 中文靠系统字体)");
        else Warn("字体文件", "系统字体目录里没找到字体 —— 中文可能显示为方框");
    }

    // ─────────────────────────── 结论 ───────────────────────────
    private static void Verdict()
    {
        Head("结论");
        // GL 版本 → 主程序能力档位
        double glv = ParseLeadingVersion(GlVersionText);
        if (glv <= 0)
            W("  · OpenGL: 没测到可用上下文 —— 三维视口起不来。可试软件渲染: LIBGL_ALWAYS_SOFTWARE=1 pitmine3d");
        else if (glv >= 3.3)
            W($"  · OpenGL {glv:0.0} —— 满足主程序最佳档(GLSL 330), 三维视口应正常");
        else if (glv >= 3.0)
            W($"  · OpenGL {glv:0.0} —— 走 GLSL 130 档(0.1.2 起支持); 更早版本的包会因着色器 330 编译失败而闪退");
        else if (glv >= 2.1)
            W($"  · OpenGL {glv:0.0} —— 只能走 GLSL 110 兼容档 + 无 VAO(0.1.2 起支持); 0.1.1 及更早会闪退");
        else
            W($"  · OpenGL {glv:0.0} —— 低于 2.1, 主程序三维视口无法工作, 只能软件渲染");

        if (GlRendererText.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
            GlRendererText.Contains("softpipe", StringComparison.OrdinalIgnoreCase))
            W("  · 当前是软件渲染(llvmpipe): 能跑但大模型会卡; 装显卡驱动可提速");

        W($"  · 缺失项 {_fail} 个, 注意项 {_warn} 个");
        W(_fail == 0
            ? "  · 未发现致命缺失。若主程序仍闪退, 请把 ~/.local/share/PitMine3D.Kylin/crash.log 一并回传。"
            : "  · 上面标 [缺失] 的就是不满足的地方, 先补这些。");
    }

    private static double ParseLeadingVersion(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var sb = new StringBuilder();
        foreach (char c in s.Trim()) { if (char.IsDigit(c) || c == '.') sb.Append(c); else break; }
        var parts = sb.ToString().Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b)
            ? a + b / 10.0 : 0;
    }
}
