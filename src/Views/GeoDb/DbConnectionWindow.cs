using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 数据库连接设置窗口。代码构建、无 XAML（与本目录其它窗口一致）。
///
/// 存在的理由: 产品只连 openGauss(SQLite 已从交付物移除), 而局域网里"一个库 + 多客户端"时
/// 连接串不能靠给每台机器设环境变量下发。
/// 这里让用户直接填, 并且**必须能先测再存** —— 填错了当场知道, 而不是下次启动打不开。
/// </summary>
internal static class DbConnectionWindow
{
    /// <summary>打开设置窗口。返回 true 表示用户保存了新设置(调用方据此提示重启生效)。</summary>
    public static async Task<bool> ShowAsync(Window owner)
    {
        var cfg = DbConnectionSettings.LoadEffective();

        var win = new Window
        {
            Title = "数据库连接",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#F7F8FA")),
        };


        var host = new TextBox { Width = 320, Text = cfg.Host, Watermark = "192.168.114.131" };
        var port = new TextBox { Width = 320, Text = cfg.Port.ToString() };
        var db = new TextBox { Width = 320, Text = cfg.Database };
        var user = new TextBox { Width = 320, Text = cfg.Username };
        var pwd = new TextBox { Width = 320, Text = cfg.Password, PasswordChar = '●' };
        var savePwd = new CheckBox { Content = "保存密码（以明文存放在本机配置文件中）", IsChecked = cfg.SavePassword };

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
        };

        var grid = new StackPanel { Spacing = 8, Margin = new Thickness(16, 14, 16, 4) };
        // 只有 openGauss 一种 —— 产品里没有 SQLite, 所以不给"选哪种库"的下拉。
        var remoteRows = new StackPanel { Spacing = 8 };
        remoteRows.Children.Add(Row("主机", host));
        remoteRows.Children.Add(Row("端口", port));
        remoteRows.Children.Add(Row("库名", db));
        remoteRows.Children.Add(Row("用户", user));
        remoteRows.Children.Add(Row("密码", pwd));
        remoteRows.Children.Add(savePwd);
        grid.Children.Add(remoteRows);
        grid.Children.Add(status);

        DbConnectionSettings Collect() => new()
        {
            Kind = "opengauss",
            Host = host.Text ?? "",
            Port = int.TryParse(port.Text, out int p) ? p : 5432,
            Database = db.Text ?? "",
            Username = user.Text ?? "",
            Password = pwd.Text ?? "",
            SavePassword = savePwd.IsChecked == true,
        };

        var test = new Button { Content = "测试连接", MinWidth = 88 };
        var ok = new Button { Content = "保存", MinWidth = 72, IsDefault = true };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };

        test.Click += async (_, _) =>
        {
            var s = Collect();
            test.IsEnabled = false;
            status.Text = "正在连接…";
            var (okConn, msg) = await TestAsync(s);
            status.Text = (okConn ? "✅ " : "❌ ") + msg;
            status.Foreground = new SolidColorBrush(Color.Parse(okConn ? "#1A7F37" : "#C62828"));
            test.IsEnabled = true;
        };

        ok.Click += (_, _) => { Collect().SaveUser(); win.Close(true); };
        cancel.Click += (_, _) => win.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 10, 16, 14),
        };
        buttons.Children.Add(test);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(buttons);
        win.Content = root;

        return await win.ShowDialog<bool?>(owner) == true;
    }

    private static Control Row(string label, Control input)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        sp.Children.Add(new TextBlock
        {
            Text = label, Width = 56,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        sp.Children.Add(input);
        return sp;
    }

    /// <summary>
    /// 真去连一次并读服务器版本。**不只是 Open()** —— 有些错误(权限、库不存在)要发一条语句才暴露。
    /// 8 秒超时: 填错 IP 时 TCP 默认要等很久, 用户会以为程序卡死。
    /// </summary>
    private static async Task<(bool ok, string msg)> TestAsync(DbConnectionSettings s)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string cs = s.BuildConnectionString() + ";Timeout=8;No Reset On Close=true";
                using var conn = new Npgsql.NpgsqlConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT version()";
                string ver = cmd.ExecuteScalar()?.ToString() ?? "";
                // 顺带看看这个库建过没有 —— 连得上但没建库是最常见的下一个坑
                cmd.CommandText = "SELECT COUNT(*) FROM _schema_migration";
                long applied;
                try { applied = Convert.ToInt64(cmd.ExecuteScalar()); }
                catch { return (true, "连接成功，但这个库还没初始化过（缺迁移登记表），需由部署方先建库。"); }
                return (true, $"连接成功。{Trim(ver)}，已应用 {applied} 个迁移。");
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return (false, ex.Message.Split('\n')[0].Trim());
        }
    }

    private static string Trim(string ver)
    {
        int i = ver.IndexOf(" build", StringComparison.Ordinal);
        return (i > 0 ? ver.Substring(0, i) : ver).Trim().TrimStart('(');
    }
}
