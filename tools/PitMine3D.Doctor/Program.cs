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

    private static string _logPath = "";
    private static bool _logWarned;

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }   // 终端非 UTF-8 时不致中断
        _logPath = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), "pitmine3d-doctor.log");

        foreach (var a in args)
            if (a is "-h" or "--help")
            {
                Console.WriteLine("用法: pitmine3d-doctor [--dir=<主程序安装目录, 默认 /opt/pitmine3d>] [--no-gl]");
                Console.WriteLine("      --no-gl  跳过 OpenGL 探测(驱动有问题时, 建上下文可能让进程直接崩掉)");
                Console.WriteLine("报告: ~/pitmine3d-doctor.log");
                return 0;
            }

        W("PitMine3D 麒麟环境体检器  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        W("(独立程序, 不改动也不需要主程序; 把本报告回传即可定位闪退原因)");
        W("报告写往: " + _logPath);
        // 先落一次盘: 后面哪一节把进程搞崩了(图形那节会真建 GL 上下文, 坏驱动可能直接 SIGSEGV),
        // 从报告断在哪一节也能看出问题出在哪 —— 此前只在最后写一次, 中途崩掉就什么都没有。
        Flush();

        string scanDir = "/opt/pitmine3d";
        foreach (var a in args) if (a.StartsWith("--dir=")) scanDir = a.Substring(6);
        bool noGl = Array.IndexOf(args, "--no-gl") >= 0;

        // 逐节容错: 一节抛异常不连累后面(此前 Main 无兜底, 任一节抛异常整个程序就静默退出了)
        Section("系统", System);
        Section("必需动态库", Libraries);
        Section("依赖全量扫描", () => ScanElfDeps(scanDir));
        Section("ICU", Icu);
        Section("显示会话", Session);
        if (noGl) { Head("图形"); Warn("OpenGL 探测", "已按 --no-gl 跳过"); Flush(); }
        else Section("图形", Graphics);
        Section("字体", Fonts);
        Section("结论", Verdict);

        Flush();
        Console.WriteLine($"\n报告已保存: {_logPath}");
        try { Console.Out.Flush(); } catch { }
        return _fail > 0 ? 1 : 0;
    }

    /// <summary>跑一节：异常不外泄(记成[注意]继续往下)，跑完即时把报告写盘。</summary>
    private static void Section(string name, Action body)
    {
        try { body(); }
        catch (Exception ex) { Warn(name, "这一节检查失败: " + ex.GetType().Name + " " + ex.Message); }
        Flush();
    }

    /// <summary>把已有内容写盘(每节一次)。写不了就说一声，不中断体检。</summary>
    private static void Flush()
    {
        try { Console.Out.Flush(); } catch { }
        try { File.WriteAllText(_logPath, string.Join(Environment.NewLine, Lines), new UTF8Encoding(false)); }
        catch (Exception ex)
        {
            if (!_logWarned) { _logWarned = true; Console.WriteLine($"(报告写不了 {_logPath}: {ex.Message})"); }
        }
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

    // ─────────────── ELF 依赖全量扫描(一次性把"真实需要什么"列全) ───────────────
    // 直接读发布产物里每个 .so / 可执行文件的 DT_NEEDED, 汇总成权威依赖表,
    // 再按 "包内自带 / 系统已有 / 缺失" 三分 —— 不靠手写清单, 换版本也不会漏。
    private static void ScanElfDeps(string dir)
    {
        Head($"依赖全量扫描 (解析 {dir} 内 ELF 的 DT_NEEDED)");
        if (!Directory.Exists(dir)) { W($"  (目录不存在, 跳过: {dir}) —— 未安装主程序时可用 --dir=<发布目录> 指定"); return; }

        var files = new List<string>();
        try
        {
            files.AddRange(Directory.GetFiles(dir, "*.so", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(dir, "*.so.*", SearchOption.AllDirectories));
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
                if (!f.EndsWith(".dll") && !f.EndsWith(".json") && !f.EndsWith(".pdb") && !files.Contains(f)) files.Add(f);
        }
        catch (Exception ex) { Warn("扫描目录", ex.Message); return; }

        var needed = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);   // soname → 谁需要它
        var provided = new HashSet<string>(StringComparer.Ordinal);                        // 包内自带的 soname
        int elfCount = 0;
        foreach (var f in files)
        {
            var (sonames, self) = ElfNeeded(f);
            if (sonames == null) continue;   // 非 ELF
            elfCount++;
            if (!string.IsNullOrEmpty(self)) provided.Add(self!);
            provided.Add(Path.GetFileName(f));
            foreach (var s in sonames)
            {
                if (!needed.TryGetValue(s, out var users)) needed[s] = users = new List<string>();
                if (users.Count < 3) users.Add(Path.GetFileName(f));
            }
        }
        W($"  扫描 ELF 文件 {elfCount} 个, 汇总外部依赖 {needed.Count} 项");

        bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var missing = new List<string>();
        foreach (var (so, users) in needed)
        {
            if (provided.Contains(so)) { continue; }                       // 包内自带, 不需要系统提供
            string who = string.Join(", ", users) + (users.Count >= 3 ? " …" : "");
            bool core = so.StartsWith("libc.so") || so.StartsWith("libm.so") || so.StartsWith("libdl.so")
                     || so.StartsWith("libpthread.so") || so.StartsWith("librt.so") || so.StartsWith("ld-linux")
                     || so.StartsWith("libgcc_s.so");
            bool optional = so.StartsWith("liblttng") || so.StartsWith("libnuma") || so.StartsWith("libgssapi");
            if (NativeLibrary.TryLoad(so, out var h)) { NativeLibrary.Free(h); Ok(so, (core ? "系统基础库, 已有" : "系统已有") + " ← " + who); }
            else if (File.Exists(Path.Combine(dir, so)) || File.Exists(Path.Combine(dir, "runtime-libs", so))) Ok(so, "包内自带");
            else if (!isLinux) W($"  [ 跳过 ] {so} — 本机非 Linux, 无法判断 ← {who}");
            else if (optional) Warn(so, "可选(缺了也能跑, 只少了诊断/追踪能力) ← " + who);
            else { missing.Add(so); Bad(so, (core ? "系统基础库缺失(极罕见, 说明系统不完整)" : "系统缺失") + " ← 需要它的: " + who); }
        }
        if (missing.Count == 0) W("  → 所有外部依赖均可满足");
        else
        {
            W("  → 缺失清单(按此装包即可):");
            W("     " + string.Join(" ", missing));
            W("     Debian/麒麟: sudo apt-get install -y " + string.Join(" ", GuessPackages(missing)));
        }
    }

    /// <summary>soname → Debian 包名(常见映射; 猜不到就原样列出让用户 apt-file 查)。</summary>
    private static IEnumerable<string> GuessPackages(IEnumerable<string> sonames)
    {
        var map = new Dictionary<string, string>
        {
            ["libstdc++.so.6"] = "libstdc++6", ["libgcc_s.so.1"] = "libgcc-s1",
            ["libX11.so.6"] = "libx11-6", ["libXext.so.6"] = "libxext6", ["libXi.so.6"] = "libxi6",
            ["libXrandr.so.2"] = "libxrandr2", ["libXcursor.so.1"] = "libxcursor1", ["libXrender.so.1"] = "libxrender1",
            ["libXfixes.so.3"] = "libxfixes3", ["libXinerama.so.1"] = "libxinerama1", ["libxcb.so.1"] = "libxcb1",
            ["libICE.so.6"] = "libice6", ["libSM.so.6"] = "libsm6",
            ["libfontconfig.so.1"] = "libfontconfig1", ["libfreetype.so.6"] = "libfreetype6",
            ["libGL.so.1"] = "libgl1", ["libEGL.so.1"] = "libegl1", ["libGLX.so.0"] = "libglx0",
            ["libssl.so.1.1"] = "libssl1.1", ["libcrypto.so.1.1"] = "libssl1.1",
            ["libssl.so.3"] = "libssl3", ["libcrypto.so.3"] = "libssl3",
            ["libz.so.1"] = "zlib1g", ["libexpat.so.1"] = "libexpat1", ["libpng16.so.16"] = "libpng16-16",
            ["libuuid.so.1"] = "uuid-runtime", ["libdbus-1.so.3"] = "libdbus-1-3",
            ["libc.so.6"] = "libc6", ["libm.so.6"] = "libc6", ["libdl.so.2"] = "libc6",
            ["libpthread.so.0"] = "libc6", ["librt.so.1"] = "libc6", ["ld-linux-x86-64.so.2"] = "libc6",
            ["ld-linux-aarch64.so.1"] = "libc6", ["liblttng-ust.so.0"] = "liblttng-ust0",
        };
        var outp = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in sonames) outp.Add(map.TryGetValue(s, out var p) ? p : $"<查: apt-file search {s}>");
        return outp;
    }

    /// <summary>读 ELF64 的 DT_NEEDED 与 DT_SONAME。非 ELF/32 位返回 (null, null)。</summary>
    private static (List<string>? needed, string? soname) ElfNeeded(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 64) return (null, null);
            var ident = br.ReadBytes(16);
            if (ident[0] != 0x7F || ident[1] != 'E' || ident[2] != 'L' || ident[3] != 'F') return (null, null);
            if (ident[4] != 2) return (null, null);            // 只处理 64 位(x64/arm64/loongarch64 皆是)
            fs.Position = 32;                                   // e_phoff
            long phoff = br.ReadInt64();
            fs.Position = 54;                                   // e_phentsize
            int phentsize = br.ReadUInt16();
            int phnum = br.ReadUInt16();

            var loads = new List<(ulong vaddr, ulong off, ulong filesz)>();
            (ulong off, ulong size) dyn = (0, 0);
            for (int i = 0; i < phnum; i++)
            {
                fs.Position = phoff + (long)i * phentsize;
                uint ptype = br.ReadUInt32();
                br.ReadUInt32();                                 // p_flags
                ulong poff = br.ReadUInt64(), pvaddr = br.ReadUInt64();
                br.ReadUInt64();                                 // p_paddr
                ulong pfilesz = br.ReadUInt64();
                if (ptype == 1) loads.Add((pvaddr, poff, pfilesz));      // PT_LOAD
                else if (ptype == 2) dyn = (poff, pfilesz);              // PT_DYNAMIC
            }
            if (dyn.size == 0) return (new List<string>(), null);

            ulong VaddrToOff(ulong va)
            {
                foreach (var (v, o, sz) in loads)
                    if (va >= v && va < v + sz) return va - v + o;
                return va;   // 兜底: 有些文件 vaddr==offset
            }

            var neededOffs = new List<ulong>();
            ulong strtabVa = 0, sonameOff = 0;
            for (ulong p = dyn.off; p + 16 <= dyn.off + dyn.size; p += 16)
            {
                fs.Position = (long)p;
                long tag = br.ReadInt64();
                ulong val = br.ReadUInt64();
                if (tag == 0) break;                       // DT_NULL
                if (tag == 1) neededOffs.Add(val);         // DT_NEEDED
                else if (tag == 5) strtabVa = val;         // DT_STRTAB
                else if (tag == 14) sonameOff = val;       // DT_SONAME
            }
            if (strtabVa == 0) return (new List<string>(), null);
            ulong strOff = VaddrToOff(strtabVa);

            string ReadStr(ulong rel)
            {
                fs.Position = (long)(strOff + rel);
                var sb = new StringBuilder();
                for (int i = 0; i < 512; i++) { int c = fs.ReadByte(); if (c <= 0) break; sb.Append((char)c); }
                return sb.ToString();
            }
            var list = new List<string>();
            foreach (var o in neededOffs) { var s = ReadStr(o); if (s.Length > 0) list.Add(s); }
            string? self = sonameOff != 0 ? ReadStr(sonameOff) : null;
            return (list, self);
        }
        catch { return (null, null); }
    }

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
