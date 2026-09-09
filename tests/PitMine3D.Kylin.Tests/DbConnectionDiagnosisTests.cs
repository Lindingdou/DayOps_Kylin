using System;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 连库失败的诊断分类。
///
/// 每条用例的异常文本都取自**真机上实际遇到过的报错**(openGauss 6.0.0-lite + Npgsql 8),
/// 不是编的 —— 这样这些断言才真的能守住"用户看到的是人话"。
/// </summary>
public class DbConnectionDiagnosisTests
{
    private static DbConnectionDiagnosis.Result D(string msg)
        => DbConnectionDiagnosis.Diagnose(new InvalidOperationException(msg));

    [Fact]
    public void 没配连接信息_引导去配置()
    {
        var r = D("选了 openGauss 但没有可用的连接信息。请在「数据库连接」里填写主机/库名/账号, 或设置 PITMINE_DB_CONN。");
        Assert.Equal("还没有配置数据库连接", r.Title);
        Assert.True(r.Actionable);
    }

    [Fact]
    public void 连不上服务器_引导去配置()
    {
        var r = D("Failed to connect to 192.168.114.131:5432");
        Assert.Equal("连不上数据库服务器", r.Title);
        Assert.True(r.Actionable);
    }

    [Fact]
    public void 真实的连不上是内层异常_必须顺着异常链看()
    {
        // 实测踩过的坑: Npgsql 连不上时最外层只有 "Exception while connecting",
        // 真正的原因(连接超时/拒绝)在 InnerException 里。只看最外层就会归到"未知错误",
        // 用户拿到一句没用的兜底话 —— 第一版就是这样, 指向不存在的地址却显示"数据库连接失败"。
        var inner = new System.Net.Sockets.SocketException(10060);   // 连接超时
        var ex = new InvalidOperationException("Exception while connecting", inner);

        var r = DbConnectionDiagnosis.Diagnose(ex);

        Assert.Equal("连不上数据库服务器", r.Title);
        Assert.Contains("Exception while connecting", r.Raw);   // 整条链都要留在详细信息里
    }

    [Fact]
    public void 密码错_引导去配置且提示改过认证方式要重设密码()
    {
        var r = D("28P01: password authentication failed for user \"pitmine\"");
        Assert.Equal("账号或密码不对", r.Title);
        Assert.True(r.Actionable);
        // 这一句是真机踩过的坑: 改完 password_encryption_type 不重设密码, 症状就是"密码没错却认证失败"
        Assert.Contains("重设", r.Detail);
    }

    [Fact]
    public void 库名不存在_引导去配置()
    {
        var r = D("3D000: database \"pitmine\" does not exist");
        Assert.Equal("库名不存在", r.Title);
        Assert.True(r.Actionable);
    }

    [Fact]
    public void 库没初始化_不该引导用户去改连接()
    {
        // 这一类改连接没用, 得部署方去建库。误导用户反复改设置比不提示更糟。
        var r = D("连上了 opengauss 库, 但读不到迁移登记表 _schema_migration —— 这个库还没初始化过。");
        Assert.Equal("数据库还没初始化", r.Title);
        Assert.False(r.Actionable);
    }

    [Fact]
    public void schema落后_同样不是改连接能解决的()
    {
        var r = D("数据库 schema 落后于本程序: 缺 3 个迁移 (V050_x, V051_y, V052_z)。");
        Assert.False(r.Actionable);
    }

    [Fact]
    public void 认不出的错保留原文_不猜()
    {
        var r = D("某个没见过的底层错误 XYZ");
        Assert.Equal("数据库连接失败", r.Title);
        Assert.Contains("XYZ", r.Raw);   // 原文进 Raw(详细信息), 不进给用户看的正文
        Assert.True(r.Actionable);
    }

    [Theory]
    [InlineData("Failed to connect to 192.168.114.131:5432")]
    [InlineData("28P01: password authentication failed for user \"pitmine\"")]
    [InlineData("3D000: database \"pitmine\" does not exist")]
    [InlineData("0A000: DISCARD statement is not yet supported.")]
    [InlineData("某个没见过的底层错误 SomeInternalError")]
    public void 给用户看的正文里不出现英文报错原文(string raw)
    {
        var r = DbConnectionDiagnosis.Diagnose(new InvalidOperationException(raw));

        // 现场人员看不懂 "28P01: password authentication failed", 混进提示只会造成困惑。
        // 英文原文只允许出现在 Raw(窗口里折叠的"详细信息")。
        foreach (var frag in new[] { "Failed to connect", "28P01", "3D000", "0A000",
                                     "authentication failed", "does not exist", "SomeInternalError" })
        {
            Assert.DoesNotContain(frag, r.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(frag, r.Detail, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(raw, r.Raw);   // 但原文必须完整留在 Raw 里, 否则远程支持没法排查
    }

    [Fact]
    public void 多行异常不会把换行带进标题()
    {
        var r = D("Failed to connect to 10.0.0.5:5432\nDETAIL: something\nHINT: else");
        Assert.DoesNotContain("\n", r.Title);
    }
}
