using System;
using System.Security.Cryptography;
using System.Text;

namespace PitMine3D.Kylin.Licensing
{
    /// <summary>激活码解析结果。<see cref="Valid"/> 为 false 时看 <see cref="Error"/>。</summary>
    internal sealed class LicenseInfo
    {
        public bool Valid { get; init; }
        public string Error { get; init; } = "";
        /// <summary>true = 永久授权；false 时看 <see cref="ExpiryUtc"/>。</summary>
        public bool Perpetual { get; init; }
        /// <summary>授权到期时刻（UTC）。永久授权时为 <see cref="DateTime.MaxValue"/>。</summary>
        public DateTime ExpiryUtc { get; init; }
        /// <summary>版本/席位等扩展位，当前恒为 0。</summary>
        public byte Flags { get; init; }
    }

    /// <summary>
    /// 激活码的编码/签发/验签。<b>本文件同时被主程序和注册机（Tools\LicenseKeygen）编译</b>
    /// ——注册机是 &lt;Compile Include&gt; 链过去的同一份源码，不是抄一遍的镜像实现，
    /// 所以两侧格式不可能漂移（见 [[mirror-impl-cross-check]] 的教训）。
    ///
    /// 体制：ECDSA P-256 离线签名。私钥只在你手上（注册机的 signing-key.private.txt），
    /// 公钥编译进主程序。没有私钥就伪造不出激活码——这一点与"内嵌对称密钥"式的
    /// 序列号有本质区别：对称密钥一旦被反编译捞走，全网通用注册机立刻出现。
    ///
    /// 传输格式（70 字节 → Base32 112 字符 → 每 7 字符一段共 16 段）：
    ///   [0]      版本号 = 1
    ///   [1..2]   到期日（自 2020-01-01 起的天数，uint16 小端；0 = 永久）
    ///   [3]      标志位（保留）
    ///   [4..67]  ECDSA P-256 签名（r||s，各 32 字节）
    ///   [68..69] 校验和（SHA256 前 2 字节）——只为把"抄错了"和"不是这台机器"两种
    ///            报错分开，安全性不依赖它。
    /// 被签名的消息 = "PMD3-LIC-V1" ‖ 机器码 10 字节 ‖ [0..3]
    ///            —— 机器码参与签名但不进传输，所以激活码离开这台机器立刻失效。
    /// </summary>
    internal static class LicenseKey
    {
        public const byte FormatVersion = 1;
        private const int SignatureBytes = 64;   // P-256 的 r||s
        private const int BlobBytes = 4 + SignatureBytes + 2;
        private static readonly byte[] SignPrefix = Encoding.ASCII.GetBytes("PMD3-LIC-V1");

        /// <summary>到期日字段的纪元。改它会让所有已签发的激活码整体平移，不要动。</summary>
        public static readonly DateTime Epoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>机器码的原始字节数（10 字节 = 80 bit = 16 个 Base32 字符）。</summary>
        public const int MachineIdBytes = 10;

        // ── 签发方公钥（SubjectPublicKeyInfo，Base64）───────────────────────────────
        // 换密钥对时：跑 LicenseKeygen genkey，把它打印的公钥整串替换到这里，
        // 私钥存好。注意换了公钥，此前签发的所有激活码全部作废。
        public const string PublicKeyBase64 =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwXMqi8wXXIq+IqRcohecCB0Mdz8ux" +
            "ToqLf+RGOO/faC4CagRxuDpHYf3kjKwhlR3IM90siW34WgJYNmRuS6f5Q==";

        // ── 编码 ───────────────────────────────────────────────────────────────────

        /// <summary>把 16 字符机器码文本解回 10 字节；格式不对返回 null。</summary>
        public static byte[]? ParseMachineCode(string? machineCode)
        {
            var raw = Base32.Decode(machineCode);
            return raw is { Length: MachineIdBytes } ? raw : null;
        }

        /// <summary>到期时刻 → 天数字段（向上取整到整天，宁可多给几小时也不少给）。</summary>
        public static ushort DaysFromExpiry(DateTime expiryUtc)
        {
            double d = Math.Ceiling((expiryUtc - Epoch).TotalDays);
            if (d <= 0) return 1;                       // 0 被占用为"永久"，不能当成已过期
            return (ushort)Math.Min(d, ushort.MaxValue);
        }

        /// <summary>天数字段 → 到期时刻（UTC）。</summary>
        public static DateTime ExpiryFromDays(ushort days) => Epoch.AddDays(days);

        // ── 签发（注册机用；主程序不带私钥，走不到这条路）───────────────────────

        /// <summary>
        /// 用私钥为指定机器码签发激活码。<paramref name="expiryDays"/> 传 0 表示永久授权。
        /// </summary>
        public static string Issue(ECDsa privateKey, byte[] machineId, ushort expiryDays, byte flags = 0)
        {
            if (machineId is not { Length: MachineIdBytes })
                throw new ArgumentException($"机器码必须是 {MachineIdBytes} 字节", nameof(machineId));

            var blob = new byte[BlobBytes];
            blob[0] = FormatVersion;
            blob[1] = (byte)(expiryDays & 0xFF);
            blob[2] = (byte)(expiryDays >> 8);
            blob[3] = flags;

            byte[] sig = privateKey.SignData(BuildSignedMessage(machineId, blob), HashAlgorithmName.SHA256);
            if (sig.Length != SignatureBytes)
                throw new CryptographicException($"签名长度异常：{sig.Length}，期望 {SignatureBytes}");
            Buffer.BlockCopy(sig, 0, blob, 4, SignatureBytes);

            WriteChecksum(blob);
            return Base32.Group(Base32.Encode(blob), 7);
        }

        // ── 验签（主程序用）─────────────────────────────────────────────────────

        /// <summary>校验激活码：格式 → 校验和 → 签名（含机器码绑定）。任何一步不过都不抛异常。</summary>
        public static LicenseInfo Verify(string? activationCode, byte[]? machineId)
            => VerifyCore(activationCode, machineId, null);

        /// <summary>
        /// 同 <see cref="Verify"/>，但用调用方给的密钥验签而不是编译进来的供应方公钥。
        /// **只为单测**：签发私钥在供应方手里，测试里没有，故自带一对临时密钥跑签发→验签往返 ——
        /// 格式(70 字节布局 / 被签消息 / 校验和)只要漂了一处，往返就过不去。产品路径一律走 <see cref="Verify"/>。
        /// </summary>
        internal static LicenseInfo VerifyWith(ECDsa key, string? activationCode, byte[]? machineId)
            => VerifyCore(activationCode, machineId, key);

        private static LicenseInfo VerifyCore(string? activationCode, byte[]? machineId, ECDsa? overrideKey)
        {
            if (machineId is not { Length: MachineIdBytes })
                return Fail("无法读取本机机器码");

            var blob = Base32.Decode(activationCode);
            if (blob is null || blob.Length < BlobBytes)
                return Fail("激活码不完整或含非法字符，请核对后重新粘贴");
            if (blob.Length > BlobBytes)
                Array.Resize(ref blob, BlobBytes);      // Base32 末尾补零位可能多解出 1 字节

            if (blob[0] != FormatVersion)
                return Fail($"激活码格式版本 {blob[0]} 不受支持，请向供应方索取新版激活码");

            if (!ChecksumOk(blob))
                return Fail("激活码校验失败，多半是抄写或粘贴时有遗漏、串行，请重新复制完整的激活码");

            var sig = new byte[SignatureBytes];
            Buffer.BlockCopy(blob, 4, sig, 0, SignatureBytes);

            bool ok;
            try
            {
                if (overrideKey != null)
                {
                    ok = overrideKey.VerifyData(BuildSignedMessage(machineId, blob), sig, HashAlgorithmName.SHA256);
                }
                else
                {
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
                    ok = ecdsa.VerifyData(BuildSignedMessage(machineId, blob), sig, HashAlgorithmName.SHA256);
                }
            }
            catch (Exception ex)
            {
                return Fail("激活码校验出错：" + ex.Message);
            }

            if (!ok)
                return Fail("此激活码与本机机器码不匹配。请确认申请时提供的机器码与当前显示的一致");

            ushort days = (ushort)(blob[1] | (blob[2] << 8));
            return new LicenseInfo
            {
                Valid = true,
                Perpetual = days == 0,
                ExpiryUtc = days == 0 ? DateTime.MaxValue : ExpiryFromDays(days),
                Flags = blob[3],
            };
        }

        // ── 内部 ───────────────────────────────────────────────────────────────────

        private static byte[] BuildSignedMessage(byte[] machineId, byte[] blob)
        {
            var msg = new byte[SignPrefix.Length + MachineIdBytes + 4];
            Buffer.BlockCopy(SignPrefix, 0, msg, 0, SignPrefix.Length);
            Buffer.BlockCopy(machineId, 0, msg, SignPrefix.Length, MachineIdBytes);
            Buffer.BlockCopy(blob, 0, msg, SignPrefix.Length + MachineIdBytes, 4);
            return msg;
        }

        private static void WriteChecksum(byte[] blob)
        {
            byte[] h = SHA256.HashData(new ReadOnlySpan<byte>(blob, 0, BlobBytes - 2));
            blob[BlobBytes - 2] = h[0];
            blob[BlobBytes - 1] = h[1];
        }

        private static bool ChecksumOk(byte[] blob)
        {
            byte[] h = SHA256.HashData(new ReadOnlySpan<byte>(blob, 0, BlobBytes - 2));
            return blob[BlobBytes - 2] == h[0] && blob[BlobBytes - 1] == h[1];
        }

        private static LicenseInfo Fail(string why) => new() { Valid = false, Error = why };
    }
}
