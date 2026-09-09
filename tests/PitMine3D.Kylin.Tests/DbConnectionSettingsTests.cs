using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 连接设置的纯逻辑。界面本身不做 UI 自动化(本仓库的惯例是逻辑落纯函数单测),
/// 但连接串拼装错了是"填得对却连不上"这种最难查的故障, 必须测。
/// </summary>
public class DbConnectionSettingsTests
{
    [Fact]
    public void 拼连接串_常规字段()
    {
        var s = new DbConnectionSettings
        {
            Kind = "opengauss", Host = "192.168.114.131", Port = 5432,
            Database = "pitmine", Username = "pitmine", Password = "Pitmine@2026",
        };
        Assert.Equal("Host=192.168.114.131;Port=5432;Database=pitmine;Username=pitmine;Password=Pitmine@2026",
                     s.BuildConnectionString());
    }

    [Fact]
    public void 拼连接串_密码含分号必须加引号否则连接串被截断()
    {
        // 这是真会咬人的一条: 密码里一个分号就把后面的键值全吃掉, 且报的错跟密码无关。
        var s = new DbConnectionSettings { Host = "h", Port = 1, Database = "d", Username = "u", Password = "a;b" };
        Assert.Contains("Password=\"a;b\"", s.BuildConnectionString());
    }

    [Fact]
    public void 拼连接串_密码含双引号要翻倍()
    {
        var s = new DbConnectionSettings { Host = "h", Port = 1, Database = "d", Username = "u", Password = "a\"b" };
        Assert.Contains("Password=\"a\"\"b\"", s.BuildConnectionString());
    }

    [Fact]
    public void 空字段不进连接串()
    {
        var s = new DbConnectionSettings { Host = "h", Port = 5432, Database = "d", Username = "", Password = "" };
        string cs = s.BuildConnectionString();
        Assert.DoesNotContain("Username", cs);
        Assert.DoesNotContain("Password", cs);
    }

    [Fact]
    public void 解析配置文本_忽略注释与空行与看不懂的行()
    {
        var s = DbConnectionSettings.Parse(
            "# 注释\n\nkind=opengauss\nhost=10.0.0.5\nport=5433\n这行看不懂\ndatabase=pm\nusername=u\nsavepassword=1\npassword=p\n");
        Assert.Equal("opengauss", s.Kind);
        Assert.Equal("10.0.0.5", s.Host);
        Assert.Equal(5433, s.Port);
        Assert.Equal("pm", s.Database);
        Assert.True(s.SavePassword);
        Assert.Equal("p", s.Password);
    }

    [Fact]
    public void 带BOM的配置文件首行也要能读出来()
    {
        // 真踩过: PowerShell 的 Out-File -Encoding utf8、记事本另存, 写出来都带 UTF-8 BOM。
        // 不处理的话第一行的键会变成 "﻿kind" 而匹配不上 —— 首行配置静默失效,
        // 而文件看上去完全正常, 极难发现。
        var s = DbConnectionSettings.Parse("﻿kind=opengauss\nhost=10.0.0.7\n");
        Assert.Equal("opengauss", s.Kind);
        Assert.Equal("10.0.0.7", s.Host);
    }

    [Fact]
    public void 端口写成非数字时退回默认而不是崩()
    {
        var s = DbConnectionSettings.Parse("kind=opengauss\nport=abc\n");
        Assert.Equal(5432, s.Port);
    }

    [Fact]
    public void 不保存密码时序列化里不含密码()
    {
        var s = new DbConnectionSettings
        {
            Kind = "opengauss", Host = "h", Database = "d", Username = "u",
            Password = "secret", SavePassword = false,
        };
        Assert.DoesNotContain("secret", s.Serialize());
    }

    [Fact]
    public void 序列化再解析应还原()
    {
        var a = new DbConnectionSettings
        {
            Kind = "opengauss", Host = "h", Port = 5433, Database = "d",
            Username = "u", Password = "p", SavePassword = true,
        };
        var b = DbConnectionSettings.Parse(a.Serialize());
        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.Host, b.Host);
        Assert.Equal(a.Port, b.Port);
        Assert.Equal(a.Database, b.Database);
        Assert.Equal(a.Username, b.Username);
        Assert.Equal(a.Password, b.Password);
    }

    [Fact]
    public void 默认就是openGauss且不存在本地库这一档()
    {
        // 产品已移除 SQLite: 没有"退回本机文件"的档位。一库多客户端时静默退回本地
        // 会让每个人各写各的还以为在共享库上, 所以这里刻意锁死。
        Assert.Equal("opengauss", new DbConnectionSettings().Kind);
        Assert.True(new DbConnectionSettings().IsRemote);
    }
}
