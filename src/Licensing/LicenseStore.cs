using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PitMine3D.Kylin.Licensing;

/// <summary>落盘的运行状态（试用起算、时间高水位、已登记的激活码）。逐字同原版。</summary>
internal sealed class LicenseState
{
    public DateTime FirstSeenUtc { get; set; }
    public DateTime HighWaterUtc { get; set; }
    public string? ActivationCode { get; set; }
    public long RunCount { get; set; }
}

/// <summary>
/// 状态的加密冗余存储（移植原 <c>PitMineApp.Licensing.LicenseStore</c>）。
///
/// **加解密与合并口径逐字照搬**：AES-GCM（密钥由本机指纹派生）、"PM3S" 魔数 + 12 字节 nonce +
/// 16 字节 tag、极简 <c>k=v</c> 文本序列化、读时**取并集里最老实的那一份**
/// （首次运行取最早、高水位取最大、激活码取任一份登记过的）—— 这样删掉或改旧其中一份拿不到任何好处。
/// 容错口径也照旧：三处全不存在 = 首次运行（重装/换机不能拦）；有能解开的就用并补写其余；
/// 都在却一个都解不开 = 疑似被改过（<see cref="AllCorrupt"/>）。
///
/// **必须适配的是"三处副本放哪"**。原版是 注册表 HKCU + ProgramData + LocalAppData；
/// 麒麟上没有注册表，按原设计意图（三处分属不同权限域、不在同一批清理工具视野里）改为四处候选：
///   · <c>LocalApplicationData</c>（麒麟 <c>~/.local/share</c>）
///   · <c>ApplicationData</c>（麒麟 <c>~/.config</c>）—— 顶掉注册表那一份的位置
///   · <c>~/.cache</c>（麒麟）/ 用户临时目录 —— 清理工具最先扫的那类，故只当第三份冗余
///   · <c>CommonApplicationData</c>（麒麟 <c>/usr/share</c>，普通用户多半写不进去）
/// 写失败一律静默跳过（同原版：ProgramData 无写权限、只读介质都属正常）。
///
/// **隐藏方式按平台走**：Windows 用 <c>FileAttributes.Hidden</c>；麒麟上文件名前缀点号
/// （<c>.rt.bin</c>）才是"隐藏"，给 Linux 文件设 Hidden 属性是无效动作。
/// </summary>
internal static class LicenseStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PM3S");
    private const int NonceLen = 12, TagLen = 16;

    /// <summary>上次 <see cref="Read"/> 时"副本都在、但一个都解不开"——疑似被改过。</summary>
    public static bool AllCorrupt { get; private set; }

    /// <summary>麒麟上文件名带前导点才算隐藏；Windows 走文件属性，名字不必带点。</summary>
    private static string FileName => OperatingSystem.IsWindows() ? "rt.bin" : ".rt.bin";

    private static string? Under(Environment.SpecialFolder folder)
    {
        try
        {
            string root = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "DayOps", "PitMine3D", FileName);
        }
        catch { return null; }
    }

    /// <summary>第三处：麒麟用 <c>~/.cache</c>，Windows 用用户临时目录。</summary>
    private static string? CacheCopy()
    {
        try
        {
            string root = OperatingSystem.IsWindows()
                ? Path.GetTempPath()
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "DayOps", "PitMine3D", FileName);
        }
        catch { return null; }
    }

    /// <summary>全部副本路径（不存在的目录不算错，读写各自静默跳过）。</summary>
    private static IEnumerable<string> Copies()
    {
        foreach (var p in new[]
                 {
                     Under(Environment.SpecialFolder.LocalApplicationData),
                     Under(Environment.SpecialFolder.ApplicationData),
                     CacheCopy(),
                     Under(Environment.SpecialFolder.CommonApplicationData),
                 })
            if (p != null) yield return p;
    }

    // ── 读 ─────────────────────────────────────────────────────────────────────

    public static LicenseState? Read()
    {
        int present = 0, decoded = 0;
        var states = new List<LicenseState>();

        foreach (string path in Copies())
        {
            byte[]? blob = ReadFile(path);
            if (blob is null) continue;
            present++;
            var s = Decrypt(blob);
            if (s is null) continue;
            decoded++;
            states.Add(s);
        }

        AllCorrupt = present > 0 && decoded == 0;
        if (states.Count == 0) return null;

        // 合并取极值，而不是"取某一份" —— 这样删掉或改旧其中一份拿不到任何好处。
        var merged = new LicenseState { FirstSeenUtc = DateTime.MaxValue, HighWaterUtc = DateTime.MinValue };
        foreach (var s in states)
        {
            if (s.FirstSeenUtc != default && s.FirstSeenUtc < merged.FirstSeenUtc) merged.FirstSeenUtc = s.FirstSeenUtc;
            if (s.HighWaterUtc > merged.HighWaterUtc) merged.HighWaterUtc = s.HighWaterUtc;
            if (merged.ActivationCode is null && !string.IsNullOrWhiteSpace(s.ActivationCode)) merged.ActivationCode = s.ActivationCode;
            if (s.RunCount > merged.RunCount) merged.RunCount = s.RunCount;
        }
        if (merged.FirstSeenUtc == DateTime.MaxValue) merged.FirstSeenUtc = default;
        if (merged.HighWaterUtc == DateTime.MinValue) merged.HighWaterUtc = default;
        return merged;
    }

    // ── 写 ─────────────────────────────────────────────────────────────────────

    /// <summary>各处都写；任一处失败静默跳过（无写权限、只读介质都属正常）。</summary>
    public static void Write(LicenseState state)
    {
        byte[] blob;
        try { blob = Encrypt(state); }
        catch { return; }
        foreach (string path in Copies()) WriteFile(path, blob);
    }

    private static byte[]? ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    private static void WriteFile(string path, byte[] blob)
    {
        try
        {
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            if (OperatingSystem.IsWindows() && File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);   // 隐藏属性会挡住覆写
            File.WriteAllBytes(path, blob);
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Hidden);
            // 麒麟侧不设属性: 文件名已带前导点; 给 Linux 文件设 Hidden 是无效动作
        }
        catch { }
    }

    // ── 加解密（逐字同原版；格式变了会让已装机器的状态整体作废）─────────────────

    private static byte[] StoreKey()
        => SHA256.HashData(Concat(MachineCode.Fingerprint, Encoding.UTF8.GetBytes("DayOps.store.v1")));

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] Encrypt(LicenseState s)
    {
        byte[] plain = Encoding.UTF8.GetBytes(Serialize(s));
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLen];

        using (var gcm = new AesGcm(StoreKey(), TagLen))
            gcm.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[Magic.Length + NonceLen + TagLen + cipher.Length];
        Buffer.BlockCopy(Magic, 0, blob, 0, Magic.Length);
        Buffer.BlockCopy(nonce, 0, blob, Magic.Length, NonceLen);
        Buffer.BlockCopy(tag, 0, blob, Magic.Length + NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, blob, Magic.Length + NonceLen + TagLen, cipher.Length);
        return blob;
    }

    private static LicenseState? Decrypt(byte[] blob)
    {
        try
        {
            if (blob.Length < Magic.Length + NonceLen + TagLen) return null;
            for (int i = 0; i < Magic.Length; i++)
                if (blob[i] != Magic[i]) return null;

            var nonce = new byte[NonceLen];
            var tag = new byte[TagLen];
            var cipher = new byte[blob.Length - Magic.Length - NonceLen - TagLen];
            Buffer.BlockCopy(blob, Magic.Length, nonce, 0, NonceLen);
            Buffer.BlockCopy(blob, Magic.Length + NonceLen, tag, 0, TagLen);
            Buffer.BlockCopy(blob, Magic.Length + NonceLen + TagLen, cipher, 0, cipher.Length);

            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(StoreKey(), TagLen))
                gcm.Decrypt(nonce, cipher, tag, plain);      // 认证失败会抛，落到 catch

            return Deserialize(Encoding.UTF8.GetString(plain));
        }
        catch { return null; }
    }

    // ── 极简文本序列化（字段就这几个，不值得为它拖一套 JSON 上下文进来）──────────

    private static string Serialize(LicenseState s)
    {
        var sb = new StringBuilder();
        sb.Append("f=").Append(s.FirstSeenUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("h=").Append(s.HighWaterUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("n=").Append(s.RunCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrWhiteSpace(s.ActivationCode))
            sb.Append("k=").Append(s.ActivationCode!.Replace("\r", "").Replace("\n", "")).Append('\n');
        return sb.ToString();
    }

    private static LicenseState Deserialize(string text)
    {
        var s = new LicenseState();
        foreach (string line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line[..eq], v = line[(eq + 1)..].Trim();
            switch (k)
            {
                case "f": if (long.TryParse(v, out long f) && f >= 0) s.FirstSeenUtc = new DateTime(f, DateTimeKind.Utc); break;
                case "h": if (long.TryParse(v, out long h) && h >= 0) s.HighWaterUtc = new DateTime(h, DateTimeKind.Utc); break;
                case "n": if (long.TryParse(v, out long n)) s.RunCount = n; break;
                case "k": s.ActivationCode = v; break;
            }
        }
        return s;
    }

    // ── 单测用：把加解密与合并这两条纯逻辑暴露出来 ────────────────────────────
    // 真跑 Read/Write 会去动用户目录里的真状态文件（试用期就被测试改掉了），故只测纯逻辑。

    internal static byte[] EncryptForTest(LicenseState s) => Encrypt(s);
    internal static LicenseState? DecryptForTest(byte[] blob) => Decrypt(blob);
    internal static string SerializeForTest(LicenseState s) => Serialize(s);
    internal static LicenseState DeserializeForTest(string t) => Deserialize(t);

    /// <summary>合并多份副本（同 <see cref="Read"/> 里那段），单测用。</summary>
    internal static LicenseState MergeForTest(IEnumerable<LicenseState> states)
    {
        var merged = new LicenseState { FirstSeenUtc = DateTime.MaxValue, HighWaterUtc = DateTime.MinValue };
        foreach (var s in states)
        {
            if (s.FirstSeenUtc != default && s.FirstSeenUtc < merged.FirstSeenUtc) merged.FirstSeenUtc = s.FirstSeenUtc;
            if (s.HighWaterUtc > merged.HighWaterUtc) merged.HighWaterUtc = s.HighWaterUtc;
            if (merged.ActivationCode is null && !string.IsNullOrWhiteSpace(s.ActivationCode)) merged.ActivationCode = s.ActivationCode;
            if (s.RunCount > merged.RunCount) merged.RunCount = s.RunCount;
        }
        if (merged.FirstSeenUtc == DateTime.MaxValue) merged.FirstSeenUtc = default;
        if (merged.HighWaterUtc == DateTime.MinValue) merged.HighWaterUtc = default;
        return merged;
    }
}
