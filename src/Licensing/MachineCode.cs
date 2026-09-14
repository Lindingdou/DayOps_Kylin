using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PitMine3D.Kylin.Licensing;

/// <summary>
/// 本机机器码（移植原 <c>PitMineApp.Licensing.MachineCode</c>）。用户在注册窗看到的 16 位码就是它，
/// 激活码按它绑定。
///
/// **口径与原版一致的部分（不能动）**：盐值、三项取材拼接成一条竖线分隔的串、SHA256、取前 10 字节。
/// 只要三项取材的**内容**相同，算出来的机器码就与原版逐位相同。
///
/// **必须适配的部分**：原版三项取材是 Windows 专有的
/// （注册表 <c>MachineGuid</c> / <c>kernel32!GetVolumeInformation</c> 卷序列号 / <c>PROCESSOR_IDENTIFIER</c>）。
/// 麒麟(Linux)上这三样都不存在，故按**同等语义**各取一项 Linux 对应物：
///   1) <c>/etc/machine-id</c>（装系统时生成、重装才变）—— 与 MachineGuid 同语义，是最直接的对应物；
///   2) 根文件系统的 UUID（格式化才变）—— 与卷序列号同语义；
///   3) <c>/proc/cpuinfo</c> 的 model name（换 CPU 才变）—— 与 PROCESSOR_IDENTIFIER 同语义。
/// 三项都不含主机名，故用户改机器名不会让激活码失效（同原版的取舍）。
///
/// **登记的差异**：同一台物理机在 Windows 与麒麟上算出的机器码**不同** —— 取材本来就是两套。
/// 这与原版"重装系统/换主板要重新签发"是同一条规则的延伸：换了操作系统就要重新签发，
/// 不是缺陷。跨平台开发机上先在 Windows 调试、再到麒麟部署时，两边各需一份激活码。
/// </summary>
internal static class MachineCode
{
    /// <summary>盐值逐字照搬原版 —— 改它等于把所有已签发的激活码作废。</summary>
    private const string Salt = "DayOps.PitMine3D.MachineId.v1";

    private static string? _cachedText;
    private static byte[]? _cachedId;
    private static byte[]? _cachedFingerprint;

    /// <summary>展示用机器码，形如 <c>ABCD-EFGH-JKMN-PQRS</c>。</summary>
    public static string Text => _cachedText ??= Base32.Group(Base32.Encode(Id), 4);

    /// <summary>机器码的 10 字节原始值（参与激活码签名）。</summary>
    public static byte[] Id => _cachedId ??= Derive();

    /// <summary>完整 32 字节机器指纹。仅用于派生本地状态文件的密钥 —— 状态文件因此拷不到别的机器上继续用。</summary>
    public static byte[] Fingerprint
    {
        get { _ = Id; return _cachedFingerprint ?? Array.Empty<byte>(); }
    }

    /// <summary>三项取材的原始文本（诊断/单测用；不含盐值）。</summary>
    internal static (string guid, string volume, string cpu) Material()
        => (ReadMachineGuid(), ReadVolumeSerial(), ReadProcessorId());

    private static byte[] Derive()
    {
        var material = new StringBuilder();
        material.Append(Salt).Append('|');
        material.Append(ReadMachineGuid()).Append('|');
        material.Append(ReadVolumeSerial()).Append('|');
        material.Append(ReadProcessorId());

        byte[] full = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        _cachedFingerprint = full;

        var id = new byte[LicenseKey.MachineIdBytes];
        Buffer.BlockCopy(full, 0, id, 0, id.Length);
        return id;
    }

    // ── 三项取材 ─────────────────────────────────────────────────────────────
    // 任一项失败都退到**固定占位串**（原版的取舍）：宁可指纹弱一点，也不能让同一台机器
    // 每次算出不同的机器码 —— 那会让已签发的激活码莫名其妙失效。

    private static string ReadMachineGuid()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindowsMachineGuid();
            // 麒麟/Linux: systemd 的 machine-id; 老系统在 dbus 那份
            foreach (var p in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                if (!File.Exists(p)) continue;
                string v = File.ReadAllText(p).Trim();
                if (v.Length > 0) return v;
            }
        }
        catch { }
        return "no-machine-guid";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ReadWindowsMachineGuid()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? "no-machine-guid";
        }
        catch { return "no-machine-guid"; }
    }

    private static string ReadVolumeSerial()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                if (GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0))
                    return serial.ToString("X8");
                return "no-volume-serial";
            }
            // Linux: 根分区的 UUID。/dev/disk/by-uuid 下是 UUID → 设备 的符号链接,
            // 找出指向根设备的那一条即可; 不装 blkid、不需要 root。
            string? rootDev = ResolveRootDevice();
            if (rootDev != null && Directory.Exists("/dev/disk/by-uuid"))
            {
                foreach (var link in Directory.GetFiles("/dev/disk/by-uuid"))
                {
                    string? target = TryResolveLink(link);
                    if (target != null && string.Equals(target, rootDev, StringComparison.Ordinal))
                        return Path.GetFileName(link);
                }
            }
        }
        catch { }
        return "no-volume-serial";
    }

    /// <summary>由 /proc/mounts 找出挂在 "/" 上的设备节点（解到真实路径）。</summary>
    private static string? ResolveRootDevice()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var f = line.Split(' ');
                if (f.Length >= 2 && f[1] == "/" && f[0].StartsWith("/dev/"))
                    return TryResolveLink(f[0]) ?? f[0];
            }
        }
        catch { }
        return null;
    }

    private static string? TryResolveLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget != null ? Path.GetFullPath(info.ResolveLinkTarget(true)?.FullName ?? path) : path;
        }
        catch { return null; }
    }

    private static string ReadProcessorId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "no-cpu-id";

            if (File.Exists("/proc/cpuinfo"))
            {
                // 取 model name（x86）或 Hardware/Processor（部分 ARM/飞腾内核用这两个键）
                foreach (var key in new[] { "model name", "Hardware", "Processor", "cpu model" })
                {
                    var line = File.ReadLines("/proc/cpuinfo")
                                   .FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase));
                    int i = line?.IndexOf(':') ?? -1;
                    if (i >= 0)
                    {
                        string v = line!.Substring(i + 1).Trim();
                        if (v.Length > 0) return v;
                    }
                }
            }
        }
        catch { }
        return "no-cpu-id";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);
}
