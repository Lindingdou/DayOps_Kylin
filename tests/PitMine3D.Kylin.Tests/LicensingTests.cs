using System;
using System.Security.Cryptography;
using System.Text;
using PitMine3D.Kylin.Licensing;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 授权（§三二八）：Base32 编解码、激活码格式与验签、机器码取材。
///
/// 这里**不测**"某串固定激活码能不能过" —— 签发用的私钥在供应方手里，测试里没有。
/// 改为**自带一对临时密钥**跑签发→验签往返：格式(70 字节布局 / 被签消息 / 校验和)只要有一处漂了，
/// 往返就过不去；这正是要守住的东西。真实公钥另测"用别的密钥签的码验不过"。
/// </summary>
public class LicensingTests
{
    // ── Base32（Crockford 变体）──────────────────────────────
    [Fact]
    public void Base32_往返()
    {
        var rnd = new Random(1234);
        for (int len = 1; len <= 40; len++)
        {
            var data = new byte[len];
            rnd.NextBytes(data);
            string s = Base32.Encode(data);
            var back = Base32.Decode(s);
            Assert.NotNull(back);
            // 末尾按 5bit 补零可能多解出 1 字节, 前 len 字节必须逐位相同
            Assert.True(back!.Length >= len);
            for (int i = 0; i < len; i++) Assert.Equal(data[i], back[i]);
        }
    }

    [Fact]
    public void Base32_字母表不含易混字()
    {
        var s = Base32.Encode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        foreach (char c in Base32.Encode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }) + s)
            Assert.DoesNotContain(c, "ILOU");   // 电话/微信里转抄时最容易混的四个
    }

    [Fact]
    public void Base32_形近字自动规约()
    {
        // 用户把 1 抄成 I/l、0 抄成 O、V 抄成 U —— 都要能解回同一串
        var data = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };
        string s = Base32.Encode(data);
        string mangled = s.Replace('1', 'I').Replace('0', 'O');
        Assert.Equal(Base32.Decode(s), Base32.Decode(mangled));
    }

    [Fact]
    public void Base32_忽略分隔符与大小写()
    {
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 };
        string s = Base32.Encode(data);
        Assert.Equal(Base32.Decode(s), Base32.Decode(Base32.Group(s, 4)));
        Assert.Equal(Base32.Decode(s), Base32.Decode(s.ToLowerInvariant()));
        Assert.Equal(Base32.Decode(s), Base32.Decode(" " + s.Insert(3, "\n") + "\t"));
    }

    [Fact]
    public void Base32_非法字符返回null而不是抛()
    {
        Assert.Null(Base32.Decode("ABC$DEF"));
        Assert.Null(Base32.Decode(null));
        Assert.Null(Base32.Decode("   "));
    }

    [Fact]
    public void Base32_分组()
    {
        Assert.Equal("ABCD-EFGH-JK", Base32.Group("ABCDEFGHJK", 4));
        Assert.Equal("ABC", Base32.Group("ABC", 4));       // 不够一组不加连字符
        Assert.Equal("ABC", Base32.Group("ABC", 0));       // 组长非法即原样
    }

    // ── 激活码 ──────────────────────────────────────────────
    private static byte[] Machine(byte seed)
    {
        var m = new byte[LicenseKey.MachineIdBytes];
        for (int i = 0; i < m.Length; i++) m[i] = (byte)(seed + i);
        return m;
    }

    [Fact]
    public void 激活码_签发验签往返_限期()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var machine = Machine(7);
        ushort days = LicenseKey.DaysFromExpiry(new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        string code = LicenseKey.Issue(key, machine, days);

        var info = LicenseKey.VerifyWith(key, code, machine);
        Assert.True(info.Valid, info.Error);
        Assert.False(info.Perpetual);
        Assert.Equal(new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc), info.ExpiryUtc);
    }

    [Fact]
    public void 激活码_永久授权是0天而不是过期()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var machine = Machine(3);
        var info = LicenseKey.VerifyWith(key, LicenseKey.Issue(key, machine, 0), machine);
        Assert.True(info.Valid, info.Error);
        Assert.True(info.Perpetual);
        Assert.Equal(DateTime.MaxValue, info.ExpiryUtc);
    }

    [Fact]
    public void 激活码_绑机器_换台机器就不认()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string code = LicenseKey.Issue(key, Machine(1), 365);
        var info = LicenseKey.VerifyWith(key, code, Machine(2));   // 同一串码, 另一台机器
        Assert.False(info.Valid);
        Assert.Contains("机器码", info.Error);
    }

    [Fact]
    public void 激活码_别的密钥签的验不过()
    {
        using var mine = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var theirs = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var machine = Machine(5);
        string forged = LicenseKey.Issue(theirs, machine, 365);
        Assert.False(LicenseKey.VerifyWith(mine, forged, machine).Valid);
    }

    [Fact]
    public void 激活码_内置公钥拒绝随手编的码()
    {
        // 走真正的 Verify(用编译进来的供应方公钥): 随手编的串必须过不去
        var machine = Machine(9);
        Assert.False(LicenseKey.Verify("ABCDEFG-HJKMNPQ-RSTVWXY", machine).Valid);
        Assert.False(LicenseKey.Verify("", machine).Valid);
        Assert.False(LicenseKey.Verify(null, machine).Valid);
    }

    [Fact]
    public void 激活码_抄错一位就报校验失败而不是不匹配()
    {
        // 校验和的意义就在这里: 把"抄错了"和"不是这台机器"两种报错分开
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var machine = Machine(4);
        string code = LicenseKey.Issue(key, machine, 100);
        var chars = code.ToCharArray();
        // 改**签名段中间**的一位: 头两个字节是版本/天数, 动它会先撞上"格式版本不受支持"那条,
        // 就测不到校验和了(第一版测试正是这么写错的)
        int i = chars.Length / 2;
        while (chars[i] == '-') i++;
        chars[i] = chars[i] == '2' ? '3' : '2';
        var info = LicenseKey.VerifyWith(key, new string(chars), machine);
        Assert.False(info.Valid);
        Assert.Contains("校验", info.Error);
    }

    [Fact]
    public void 激活码_机器码不对时不抛只报错()
    {
        Assert.False(LicenseKey.Verify("ABC", null).Valid);
        Assert.False(LicenseKey.Verify("ABC", new byte[3]).Valid);   // 长度不对
    }

    [Fact]
    public void 到期日字段_向上取整且0留给永久()
    {
        // 0 被占用为"永久", 所以已过期的日子也得给到 1, 不能给 0
        Assert.Equal(1, LicenseKey.DaysFromExpiry(LicenseKey.Epoch));
        Assert.Equal(1, LicenseKey.DaysFromExpiry(LicenseKey.Epoch.AddDays(-100)));
        Assert.Equal(10, LicenseKey.DaysFromExpiry(LicenseKey.Epoch.AddDays(10)));
        // 不足一天也算一天(宁可多给几小时也不少给)
        Assert.Equal(11, LicenseKey.DaysFromExpiry(LicenseKey.Epoch.AddDays(10).AddHours(1)));
        Assert.Equal(LicenseKey.Epoch.AddDays(10), LicenseKey.ExpiryFromDays(10));
    }

    [Fact]
    public void 机器码文本_16位并按4分组()
    {
        var raw = Machine(0);
        string text = Base32.Group(Base32.Encode(raw), 4);
        Assert.Equal(16, text.Replace("-", "").Length);   // 10 字节 = 80bit = 16 个 Base32 字符
        Assert.Equal(3, text.Split('-').Length - 1 + 1 - 1);
        Assert.Equal(raw, LicenseKey.ParseMachineCode(text));   // 展示文本能解回原字节
    }

    [Fact]
    public void 机器码文本_解析非法值返回null()
    {
        Assert.Null(LicenseKey.ParseMachineCode("太短"));
        Assert.Null(LicenseKey.ParseMachineCode(""));
        Assert.Null(LicenseKey.ParseMachineCode(null));
    }

    // ── 本机机器码 ──────────────────────────────────────────
    [Fact]
    public void 本机机器码_稳定且成形()
    {
        string a = MachineCode.Text;
        string b = MachineCode.Text;
        Assert.Equal(a, b);                                  // 同一次运行内恒定
        Assert.Equal(16, a.Replace("-", "").Length);
        Assert.Equal(LicenseKey.MachineIdBytes, MachineCode.Id.Length);
        Assert.Equal(32, MachineCode.Fingerprint.Length);
        Assert.Equal(MachineCode.Id, LicenseKey.ParseMachineCode(a));
    }

    [Fact]
    public void 本机机器码_三项取材都取到了东西()
    {
        // 全退到占位串说明取材整个失效了 —— 那时机器码等于常量, 谁的激活码都能用到别人机器上
        var (guid, vol, cpu) = MachineCode.Material();
        Assert.NotEqual("", guid);
        Assert.NotEqual("", vol);
        Assert.NotEqual("", cpu);
        Assert.False(guid == "no-machine-guid" && vol == "no-volume-serial" && cpu == "no-cpu-id",
                     "三项取材全失败, 机器码退化成常量了");
    }

    [Fact]
    public void 本机机器码_不含主机名()
    {
        // 原版的取舍: 改计算机名不该让激活码失效
        var (guid, vol, cpu) = MachineCode.Material();
        string host = Environment.MachineName;
        if (host.Length >= 3)
        {
            Assert.DoesNotContain(host, guid, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(host, vol, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(host, cpu, StringComparison.OrdinalIgnoreCase);
        }
    }
}
