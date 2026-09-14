using System;
using System.IO;
using PitMine3D.Kylin;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>用户配置(config.json)：读写往返 / 关闭落盘 / 坏文件不挡启动。</summary>
public class UserSettingsTests : IDisposable
{
    private readonly string _dir;

    public UserSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pm_cfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string ConfigFile => Path.Combine(_dir, "config.json");

    [Fact]
    public void 写入后重开仍在_关闭时落盘()
    {
        using (var s = new UserSettings(_dir))
        {
            s.Set("FileSystem.WorkingDirectories", new[] { @"D:\图纸", "/data/图纸" });
            s.Set("Options.Display.CursorSize", 42.5);
            s.Set("Options.Draft.Ortho", true);
            s.Set("Options.Snap.ExtraMask", 9);
            s.Flush();                                  // 同关闭时的同步落盘
        }
        Assert.True(File.Exists(ConfigFile));

        var again = new UserSettings(_dir);             // 下次启动
        Assert.Equal(new[] { @"D:\图纸", "/data/图纸" }, again.Get<string[]>("FileSystem.WorkingDirectories", null!));
        Assert.Equal(42.5, again.Get<double>("Options.Display.CursorSize", 0));
        Assert.True(again.Get<bool>("Options.Draft.Ortho", false));
        Assert.Equal(9, again.Get<int>("Options.Snap.ExtraMask", 0));
    }

    [Fact]
    public void 没写过的键回默认值()
    {
        var s = new UserSettings(_dir);
        Assert.Equal(100.0, s.Get<double>("Options.Display.CursorSize", 100.0));
        Assert.Null(s.Get<string[]?>("FileSystem.WorkingDirectories", null));
        Assert.False(s.ContainsKey("Options.Draft.Ortho"));
    }

    [Fact]
    public void 删除某项后不再回读()
    {
        var s = new UserSettings(_dir);
        s.Set("k", 1);
        Assert.True(s.ContainsKey("k"));
        Assert.True(s.Remove("k"));
        Assert.False(s.Remove("k"));
        Assert.Equal(-1, s.Get<int>("k", -1));
    }

    [Fact]
    public void 配置文件损坏时按空配置启动_不抛()
    {
        File.WriteAllText(ConfigFile, "{ 这不是 json");
        var s = new UserSettings(_dir);                 // 不抛 = 程序照常起
        Assert.Equal(7, s.Get<int>("任意键", 7));

        s.Set("k", "v");                                // 仍可正常写, 坏文件被覆盖
        s.Flush();
        Assert.Equal("v", new UserSettings(_dir).Get<string>("k", ""));
    }

    [Fact]
    public void 类型对不上时回默认值不抛()
    {
        var s = new UserSettings(_dir);
        s.Set("k", "文本");
        Assert.Equal(-1, s.Get<int>("k", -1));
    }
}
