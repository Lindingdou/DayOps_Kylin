using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Licensing;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 授权状态的落盘与时间基准（§三二九）。
///
/// **刻意不跑 `LicenseStore.Read/Write` 与 `LicenseService.Evaluate`**：它们动的是**本机真实的**
/// 授权状态文件（`~/.local/share`、`~/.config` …），测试一跑就把开发机的试用期状态改了，
/// 还会互相干扰。只测纯逻辑层：加解密 / 序列化 / 多副本合并 / 时间基准推进。
///
/// `TimeGuard` 的三个字段是**进程级静态**，故本类归入串行集合 —— 教训见 [[test-flake-shared-static]]，
/// 上一节刚因为"写侧串行了、读侧没进集合"吃过亏。
/// </summary>
[Collection("LicenseTimeGuard")]
public class LicenseStoreTests
{
    private static LicenseState Sample() => new()
    {
        FirstSeenUtc = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc),
        HighWaterUtc = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc),
        ActivationCode = "ABCDEFG-HJKMNPQ",
        RunCount = 42,
    };

    // ── 加解密 ──────────────────────────────────────────────
    [Fact]
    public void 加解密往返()
    {
        var s = Sample();
        var back = LicenseStore.DecryptForTest(LicenseStore.EncryptForTest(s));
        Assert.NotNull(back);
        Assert.Equal(s.FirstSeenUtc, back!.FirstSeenUtc);
        Assert.Equal(s.HighWaterUtc, back.HighWaterUtc);
        Assert.Equal(s.ActivationCode, back.ActivationCode);
        Assert.Equal(s.RunCount, back.RunCount);
    }

    [Fact]
    public void 改一个字节就解不开()
    {
        // AES-GCM 带认证标签: 文本编辑器改一个 bit 就该解不开, 而不是解出一份被篡改的状态
        var blob = LicenseStore.EncryptForTest(Sample());
        for (int i = 4; i < blob.Length; i += Math.Max(1, blob.Length / 6))
        {
            var t = (byte[])blob.Clone();
            t[i] ^= 0x01;
            Assert.Null(LicenseStore.DecryptForTest(t));
        }
    }

    [Fact]
    public void 魔数不对或长度不足都返回null()
    {
        var blob = LicenseStore.EncryptForTest(Sample());
        var bad = (byte[])blob.Clone();
        bad[0] ^= 0xFF;                                   // 魔数被改
        Assert.Null(LicenseStore.DecryptForTest(bad));
        Assert.Null(LicenseStore.DecryptForTest(new byte[8]));     // 太短
        Assert.Null(LicenseStore.DecryptForTest(Array.Empty<byte>()));
    }

    [Fact]
    public void 密文不是明文_看不出激活码()
    {
        var blob = LicenseStore.EncryptForTest(Sample());
        string ascii = System.Text.Encoding.ASCII.GetString(blob);
        Assert.DoesNotContain("ABCDEFG", ascii);
        Assert.DoesNotContain("k=", ascii);
    }

    [Fact]
    public void 两次加密的密文不同_nonce每次新取()
    {
        var s = Sample();
        Assert.NotEqual(LicenseStore.EncryptForTest(s), LicenseStore.EncryptForTest(s));
    }

    // ── 序列化 ──────────────────────────────────────────────
    [Fact]
    public void 序列化往返_没有激活码时不写那一行()
    {
        var s = new LicenseState { FirstSeenUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), RunCount = 7 };
        string text = LicenseStore.SerializeForTest(s);
        Assert.DoesNotContain("k=", text);
        var back = LicenseStore.DeserializeForTest(text);
        Assert.Equal(s.FirstSeenUtc, back.FirstSeenUtc);
        Assert.Equal(7, back.RunCount);
        Assert.Null(back.ActivationCode);
    }

    [Fact]
    public void 反序列化_乱文本不抛只给默认值()
    {
        var s = LicenseStore.DeserializeForTest("这不是k=v\n=没有键\nf=不是数字\n");
        Assert.Equal(default, s.FirstSeenUtc);
        Assert.Equal(0, s.RunCount);
    }

    [Fact]
    public void 序列化_激活码里的换行被剥掉()
    {
        // 换行会把 k=v 那行截断, 读回来就少半截激活码
        var s = new LicenseState { ActivationCode = "AAAA\r\nBBBB" };
        var back = LicenseStore.DeserializeForTest(LicenseStore.SerializeForTest(s));
        Assert.Equal("AAAABBBB", back.ActivationCode);
    }

    // ── 多副本合并 ──────────────────────────────────────────
    [Fact]
    public void 合并_首次运行取最早_高水位取最大()
    {
        // 删旧一份、改早一份都拿不到好处: 这正是三处冗余的意义
        var a = new LicenseState { FirstSeenUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), HighWaterUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), RunCount = 3 };
        var b = new LicenseState { FirstSeenUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), HighWaterUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), RunCount = 9 };
        var m = LicenseStore.MergeForTest(new[] { a, b });
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), m.FirstSeenUtc);   // 最早
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), m.HighWaterUtc);   // 最大
        Assert.Equal(9, m.RunCount);                                                         // 最大
    }

    [Fact]
    public void 合并_任一份登记过激活码就算登记过()
    {
        var a = new LicenseState { RunCount = 1 };
        var b = new LicenseState { ActivationCode = "CODE-HERE" };
        Assert.Equal("CODE-HERE", LicenseStore.MergeForTest(new[] { a, b }).ActivationCode);
        Assert.Equal("CODE-HERE", LicenseStore.MergeForTest(new[] { b, a }).ActivationCode);   // 顺序无关
    }

    [Fact]
    public void 合并_全空时回默认而不是极值哨兵()
    {
        var m = LicenseStore.MergeForTest(new[] { new LicenseState(), new LicenseState() });
        Assert.Equal(default, m.FirstSeenUtc);   // 不能留 DateTime.MaxValue
        Assert.Equal(default, m.HighWaterUtc);   // 不能留 DateTime.MinValue
        Assert.Null(m.ActivationCode);
    }

    [Fact]
    public void 合并_空集合不抛()
    {
        var m = LicenseStore.MergeForTest(Array.Empty<LicenseState>());
        Assert.Equal(default, m.FirstSeenUtc);
    }

    // ── 时间基准 ────────────────────────────────────────────
    [Fact]
    public void 时间基准_取系统时钟与高水位的较大者()
    {
        try
        {
            var future = DateTime.UtcNow.AddDays(2);
            TimeGuard.Initialize(new LicenseState { HighWaterUtc = future });
            Assert.True(TimeGuard.EffectiveNowUtc >= future, "高水位比系统时钟晚时, 有效时间该跟高水位走");
        }
        finally { TimeGuard.Initialize(null); }   // 复位, 免得影响同集合的后续用例
    }

    [Fact]
    public void 时间基准_回拨超过容忍量才算回拨()
    {
        try
        {
            // 高水位比现在晚很多 = 系统时钟被往回拨过
            TimeGuard.Initialize(new LicenseState { HighWaterUtc = DateTime.UtcNow.AddDays(30) });
            Assert.True(TimeGuard.RollbackDetected);

            // 容忍量以内(NTP 校时/跨时区带笔记本)不该当成作弊
            TimeGuard.Initialize(new LicenseState { HighWaterUtc = DateTime.UtcNow.AddHours(1) });
            Assert.False(TimeGuard.RollbackDetected);
        }
        finally { TimeGuard.Initialize(null); }
    }

    [Fact]
    public void 时间基准_高水位只增不减()
    {
        try
        {
            var s = new LicenseState { HighWaterUtc = DateTime.UtcNow.AddDays(5) };
            TimeGuard.Initialize(s);
            var before = s.HighWaterUtc;
            TimeGuard.Advance(s);
            Assert.True(s.HighWaterUtc >= before, "Advance 把高水位改小了");

            // 拿一份更旧的状态推进, 也不能把会话内的有效时间拉回去
            var older = new LicenseState { HighWaterUtc = DateTime.UtcNow.AddYears(-1) };
            var now = TimeGuard.EffectiveNowUtc;
            TimeGuard.Advance(older);
            Assert.True(TimeGuard.EffectiveNowUtc >= now);
        }
        finally { TimeGuard.Initialize(null); }
    }

    [Fact]
    public void 时间基准_无状态时不早于系统时钟()
    {
        TimeGuard.Initialize(null);
        Assert.False(TimeGuard.RollbackDetected);
        Assert.True(TimeGuard.EffectiveNowUtc >= DateTime.UtcNow.AddMinutes(-1));
    }

    // ── 口径常量 ────────────────────────────────────────────
    [Fact]
    public void 试用口径常量成形()
    {
        Assert.True(TrialPolicy.TrialExpiryUtc > TrialPolicy.SaneMinUtc);
        Assert.True(TrialPolicy.TrialExpiryUtc < TrialPolicy.SaneMaxUtc);
        Assert.True(TrialPolicy.RollbackTolerance > TimeSpan.Zero);
        Assert.True(TrialPolicy.HeartbeatInterval > TimeSpan.Zero);
        Assert.True(TrialPolicy.WarnDays > 0);
        Assert.NotEmpty(TrialPolicy.ProductName);
    }
}
