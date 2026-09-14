using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Licensing;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 注册/授权窗（忠实原 <c>PitMineApp.Licensing.Views.RegisterWindow</c>）：
/// 状态横幅（试用中 / 剩余 N 天 / 已注册 / 已到期，四种配色）+ 本机机器码（可复制）+
/// 激活码粘贴框 + 「立即激活」+ 时钟回拨提示。
///
/// 文案与配色照原窗：绿=已授权、橙=临近到期、红=已到期、蓝=试用中；
/// 「激活码与本机机器码绑定，换到别的计算机上无效」这句是用户最需要先看到的，位置照旧摆在机器码下方。
/// </summary>
internal sealed class RegisterWindow : Window
{
    private static readonly Color ColorLicensed = Color.FromRgb(0x2E, 0x9E, 0x5B);   // 绿
    private static readonly Color ColorWarn = Color.FromRgb(0xD9, 0x77, 0x06);       // 橙
    private static readonly Color ColorExpired = Color.FromRgb(0xC0, 0x39, 0x2B);    // 红
    private static readonly Color ColorTrial = Color.FromRgb(0x2B, 0x57, 0x9A);      // 蓝

    private readonly TextBlock _statusTitle = new() { FontSize = 16, FontWeight = FontWeight.Bold };
    private readonly TextBlock _statusDetail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Border _banner = new()
    {
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 12),
    };
    private readonly TextBlock _rollback = new()
    {
        Text = "⚠ 检测到本机系统时间被向前调整过。授权判定采用软件记录的最晚时间，回拨系统时间不会延长使用期限。",
        TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(ColorWarn),
        Margin = new Thickness(0, 0, 0, 10), IsVisible = false,
    };
    private readonly TextBox _machine = new() { IsReadOnly = true, FontFamily = new FontFamily("Consolas, monospace"), FontSize = 15 };
    private readonly TextBox _activation = new()
    {
        AcceptsReturn = true, Height = 76, TextWrapping = TextWrapping.Wrap,
        Watermark = "把供应方给的激活码整串粘贴到这里",
    };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public RegisterWindow()
    {
        Title = "注册 / 授权";
        Width = 560; Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _banner.Child = new StackPanel { Children = { _statusTitle, _statusDetail } };
        _machine.Text = LicenseService.MachineCodeText;

        var btnCopy = new Button { Content = "复制", Padding = new Thickness(14, 4), Margin = new Thickness(8, 0, 0, 0) };
        btnCopy.Click += async (_, _) =>
        {
            try
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                if (cb != null) { await cb.SetTextAsync(_machine.Text ?? ""); _result.Text = "机器码已复制到剪贴板。"; }
                else { _machine.SelectAll(); _machine.Focus(); _result.Text = "复制不可用，已为你选中机器码，请手动复制。"; }
            }
            catch (Exception ex) { _result.Text = "复制失败：" + ex.Message + "（可手动选中上面的机器码复制）"; }
        };

        var btnActivate = new Button
        {
            Content = "立即激活", Padding = new Thickness(20, 6), FontWeight = FontWeight.Bold, IsDefault = true,
        };
        btnActivate.Click += (_, _) => Activate_();
        var btnClose = new Button { Content = "关闭", Padding = new Thickness(20, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        btnClose.Click += (_, _) => Close();

        static TextBlock Head(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        static TextBlock Dim(string t) => new()
        {
            Text = t, FontSize = 12, Foreground = Brush.Parse("#666"), TextWrapping = TextWrapping.Wrap,
        };

        var machineRow = new DockPanel();
        DockPanel.SetDock(btnCopy, Avalonia.Controls.Dock.Right);
        machineRow.Children.Add(btnCopy);
        machineRow.Children.Add(_machine);

        var panel = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                _banner,
                _rollback,
                Head("本机机器码"),
                machineRow,
                Dim("把上面这串机器码发给软件供应方，取得对应的激活码后粘贴到下方，点「立即激活」。"
                  + "激活码与本机机器码绑定，换到别的计算机上无效。"),
                Head("激活码"),
                _activation,
                _result,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { btnActivate, btnClose },
                },
                Dim(TrialPolicy.VendorContact),
            },
        };
        Content = new ScrollViewer { Content = panel };

        Refresh();
        Opened += (_, _) => _activation.Focus();
    }

    /// <summary>按当前授权状态刷新横幅文字与配色（逐条同原版 Refresh）。</summary>
    internal void Refresh()
    {
        var info = LicenseService.Current;
        Color accent;
        switch (info.Status)
        {
            case LicenseStatus.Licensed:
                accent = ColorLicensed;
                _statusTitle.Text = info.Perpetual ? "已注册 · 永久授权" : "已注册";
                _statusDetail.Text = info.Perpetual
                    ? $"本机（{LicenseService.MachineCodeText}）已获永久授权，感谢使用 {TrialPolicy.ProductName}。"
                    : $"授权有效期至 {info.ExpiryText}，剩余 {info.DaysLeft} 天。到期前可用新的激活码续期。";
                break;

            case LicenseStatus.Expired:
                accent = ColorExpired;
                _statusTitle.Text = "使用期限已到";
                _statusDetail.Text = info.Reason + " 软件已停止使用，完成注册后即可继续。";
                break;

            default:    // Trial
                bool soon = info.DaysLeft <= TrialPolicy.WarnDays;
                accent = soon ? ColorWarn : ColorTrial;
                _statusTitle.Text = soon ? $"试用期剩余 {info.DaysLeft} 天" : "试用中";
                _statusDetail.Text = $"试用期至 {info.ExpiryText}（剩余 {info.DaysLeft} 天）。"
                                   + "到期后需要注册才能继续使用，请提前办理。";
                if (!string.IsNullOrEmpty(info.Reason)) _statusDetail.Text += "\n" + info.Reason;
                break;
        }
        var brush = new SolidColorBrush(accent);
        _statusTitle.Foreground = brush;
        _banner.BorderBrush = brush;
        _rollback.IsVisible = info.RollbackDetected;
    }

    private void Activate_()
    {
        string code = (_activation.Text ?? "").Trim();
        if (code.Length == 0) { _result.Text = "请先粘贴激活码。"; return; }

        bool ok = LicenseService.TryActivate(code, out string message);
        _result.Text = message;
        _result.Foreground = new SolidColorBrush(ok ? ColorLicensed : ColorExpired);
        if (ok) Refresh();
    }

    // ── 自检钩子用 ──
    internal string MachineText => _machine.Text ?? "";
    internal string StatusTitle => _statusTitle.Text ?? "";
    internal string ResultText => _result.Text ?? "";
    internal void SetActivationCode(string s) => _activation.Text = s;
    internal void DoActivate() => Activate_();
}
