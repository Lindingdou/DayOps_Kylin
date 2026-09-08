using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PitMine3D.Kylin;

/// <summary>
/// 原生崩溃(段错误等)现场记录 —— 不依赖 gdb，客户机上也能拿到调用栈。
///
/// 背景：SIGSEGV/SIGBUS 这类是**信号**不是托管异常，进程被内核直接打死，
/// AppDomain.UnhandledException 根本不会触发，所以 crash.log 里只会看到崩溃前的最后一行日志。
/// 这里用 libc 的 signal() 挂上处理函数，崩溃瞬间用 backtrace()/backtrace_symbols_fd()
/// 把原生调用栈直接写进日志文件描述符 —— 那份栈能指名崩在哪个 .so 的哪个函数。
///
/// 信号处理函数里不能做托管分配、不能加锁（否则可能死锁在崩溃现场），
/// 故：日志 fd 事先打开、缓冲区事先分配、只调用 write/backtrace 这类异步信号安全的函数。
/// 写完恢复默认处理并重新触发同一信号，让系统照常产生 core dump、退出码照常是 128+信号号。
///
/// 关掉：PITMINE_NO_SIGHANDLER=1
/// </summary>
public static class NativeCrashHandler
{
    private const int SIGILL = 4, SIGABRT = 6, SIGBUS = 7, SIGFPE = 8, SIGSEGV = 11;
    private const int O_WRONLY = 0x001, O_CREAT = 0x040, O_APPEND = 0x400;

    [DllImport("libc", SetLastError = true)] private static extern IntPtr signal(int signum, IntPtr handler);
    [DllImport("libc", SetLastError = true)] private static extern int raise(int sig);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr write(int fd, IntPtr buf, IntPtr count);
    [DllImport("libc", SetLastError = true)] private static extern int backtrace(IntPtr buffer, int size);
    [DllImport("libc", SetLastError = true)] private static extern void backtrace_symbols_fd(IntPtr buffer, int size, int fd);

    private static int _fd = -1;
    private static IntPtr _frames;        // backtrace 用的地址数组(事先分配)
    private static IntPtr _banner;        // 崩溃横幅(事先编码好, 处理函数里只 write)
    private static int _bannerLen;
    private static bool _installed;

    /// <summary>挂上信号处理。非 Linux 或显式关闭时不做任何事。</summary>
    public static void Install(string logPath)
    {
        if (_installed) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PITMINE_NO_SIGHANDLER"))) return;

        try
        {
            _fd = open(logPath, O_WRONLY | O_CREAT | O_APPEND, 0x1A4 /* 0644 */);
            if (_fd < 0) return;

            _frames = Marshal.AllocHGlobal(IntPtr.Size * 64);
            var text = Encoding.UTF8.GetBytes(
                "\n===== 原生崩溃(信号) 调用栈 =====\n" +
                "（下面是崩溃瞬间的原生栈；最上面几行就是崩溃点。函数名后带 .so 的即出问题的库）\n");
            _bannerLen = text.Length;
            _banner = Marshal.AllocHGlobal(_bannerLen);
            Marshal.Copy(text, 0, _banner, _bannerLen);

            unsafe
            {
                IntPtr h = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&OnFatalSignal;
                foreach (int sig in new[] { SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGABRT })
                    signal(sig, h);
            }
            _installed = true;
            CrashLog.Write("SIG", "已挂原生崩溃处理: 段错误时会把原生调用栈写进本日志(无需 gdb)");
        }
        catch (Exception ex) { CrashLog.Write("SIG", "挂原生崩溃处理失败(不影响运行): " + ex.Message); }
    }

    /// <summary>
    /// 信号处理函数。只用异步信号安全的调用：write / backtrace / backtrace_symbols_fd。
    /// 不分配托管内存、不取锁、不碰 Console。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void OnFatalSignal(int sig)
    {
        if (_fd >= 0)
        {
            write(_fd, _banner, (IntPtr)_bannerLen);
            // 信号号：用固定字节写出，避免格式化分配
            byte* num = stackalloc byte[24];
            int n = 0;
            num[n++] = (byte)'s'; num[n++] = (byte)'i'; num[n++] = (byte)'g'; num[n++] = (byte)'=';
            int v = sig;
            if (v >= 10) { num[n++] = (byte)('0' + v / 10); v %= 10; }
            num[n++] = (byte)('0' + v);
            num[n++] = (byte)'\n';
            write(_fd, (IntPtr)num, (IntPtr)n);

            int depth = backtrace(_frames, 64);
            if (depth > 0) backtrace_symbols_fd(_frames, depth, _fd);
        }
        // 恢复默认处理并重新触发：core dump 照常产生, 退出码仍是 128+信号号
        signal(sig, IntPtr.Zero /* SIG_DFL */);
        raise(sig);
    }
}
