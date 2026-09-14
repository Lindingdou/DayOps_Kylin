using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Transport;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「延拓触发设置」（移植原 <c>RoadLib.Views.ExtendParamsDialog</c>「延拓参数中心」）——
/// 单一入口集中维护延拓相关全部参数的持久默认：
/// 触发条件（推进步距/时间步 + 临时道路回收）+ 演化判定阈值 + 推进方向先验。
///
/// 校验判据在 <see cref="ExtendTriggerSettings.Validate"/>（纯函数），对话框只负责铺界面 ——
/// 与 §三三九 同一条：判据只此一份，脱 GUI 可验收。
/// </summary>
internal sealed class ExtendParamsWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Action<string> _echo;

    // A 触发条件
    private readonly RadioButton _rbAdvance = new() { Content = "推进步距", GroupName = "TriggerMode", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _rbTime = new() { Content = "时间步", GroupName = "TriggerMode" };
    private readonly TextBox _tbAdvanceStep = NumBox(), _tbTimePeriods = NumBox();
    private readonly CheckBox _cbAutoReclaim = new() { Content = "采空后自动回收临时道路", Margin = new Thickness(0, 6, 0, 0) };
    // B 演化判定阈值
    private readonly TextBox _tbStep = NumBox(), _tbTol = NumBox(), _tbAngle = NumBox(),
                             _tbNewCover = NumBox(), _tbGoneCover = NumBox(), _tbGrow = NumBox(), _tbShift = NumBox();
    // C 推进方向先验
    private readonly CheckBox _cbUseDir = new() { Content = "启用", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly TextBox _tbAzimuth = NumBox();

    private readonly TextBlock _err = new()
    { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#DC2626") };

    private static TextBox NumBox() => new()
    { Width = 78, HorizontalContentAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 3), Margin = new Thickness(0, 2) };

    internal ExtendParamsWindow(Action<string> echo)
    {
        _echo = echo;
        Title = "延拓触发设置";
        // 尺寸照原版 420×560 —— 逻辑像素在缩放≠1 的机器上会放大，别自行加高。
        // 原版是 ResizeMode.NoResize；这里放开可缩放：Avalonia 的控件行高比 WPF 高一截，
        // 同样的行数在小屏 + 高缩放下会顶到屏幕边，锁死尺寸就等于把窗口卡死在放不下的状态。
        // 参数区已包在 ScrollViewer 里、页脚 Dock 到底，缩到多小「确定」都还在。
        Width = 420; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        _rbAdvance.IsCheckedChanged += (_, _) => SyncTriggerEnabled();
        _rbTime.IsCheckedChanged += (_, _) => SyncTriggerEnabled();
        _cbUseDir.IsCheckedChanged += (_, _) => SyncDirEnabled();

        Content = BuildLayout();
        LoadValues(ExtendTriggerSettings.Load());
    }

    private static Control Group(string header, Control body) => new Border
    {
        BorderBrush = Brush.Parse("#D0D0D0"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 8),
        Child = new StackPanel
        {
            Children =
            {
                new Border
                {
                    Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(10, 4),
                    Child = new TextBlock { Text = header, FontWeight = FontWeight.SemiBold },
                },
                new Border { Padding = new Thickness(10, 6, 10, 8), Child = body },
            },
        },
    };

    /// <summary>一行 = 左标签（自适应）+ 右数值框（定宽）。列号在这里设，别散在外面两处对不上。</summary>
    private static Control Row(string label, TextBox box)
    {
        Grid.SetColumn(box, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 1),
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                box,
            },
        };
    }

    private Control BuildLayout()
    {
        var modeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                new TextBlock { Text = "触发模式:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                _rbAdvance, _rbTime,
            },
        };

        var gbTrigger = Group("触发条件", new StackPanel
        {
            Children =
            {
                modeRow,
                Row("推进步距阈值 (m)", _tbAdvanceStep),
                Row("时间步阈值 (期)", _tbTimePeriods),
                _cbAutoReclaim,
            },
        });

        var gbEvo = Group("演化判定阈值", new StackPanel
        {
            Children =
            {
                Row("采样步长 (m)", _tbStep),
                Row("匹配容差 (m)", _tbTol),
                Row("走向夹角 (°)", _tbAngle),
                Row("新建覆盖比", _tbNewCover),
                Row("废除覆盖比", _tbGoneCover),
                Row("最小段长 (m)", _tbGrow),
                Row("移位阈值 (m)", _tbShift),
                new TextBlock
                {
                    Text = "（演化对比默认值）", Foreground = Brush.Parse("#888"), FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0),
                },
            },
        });

        var gbDir = Group("推进方向先验（可选）", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                _cbUseDir,
                new TextBlock { Text = "方位角 (°,北=0顺时针):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                _tbAzimuth,
            },
        });

        Button B(string t, Action a, bool bold = false)
        {
            var b = new Button { Content = t, MinWidth = 76, Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 8, 0) };
            if (bold) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => a();
            return b;
        }

        var ok = B("确定", Commit, bold: true);
        ok.IsDefault = true;
        var cancel = B("取消", Close);
        cancel.IsCancel = true;
        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
            Children = { B("恢复默认", () => LoadValues(new ExtendTriggerSettings())), cancel, ok },
        };

        // ★ 「确定/取消」必须钉在底部、参数区滚动 —— 不能整块塞进一个 StackPanel。
        // 原版 WPF 的 420×560 装得下是因为 WPF 控件行高更紧；Avalonia 同样的行数要高出一截，
        // 一路堆下去会把页脚顶到屏幕外，用户连"确定"都点不到（见 §三三九 同类坑）。
        // Dock 到底就与缩放、行高、字体大小全都无关了。
        var foot2 = new Border { Padding = new Thickness(12, 6, 12, 10), Child = foot };
        var errBox = new Border { Padding = new Thickness(12, 0), Child = _err };

        var root = new DockPanel();
        DockPanel.SetDock(foot2, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(errBox, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(foot2);
        root.Children.Add(errBox);
        root.Children.Add(new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12, 12, 12, 0),
                Children = { gbTrigger, gbEvo, gbDir },
            },
        });
        return root;
    }

    /// <summary>把设置值填进控件（初值 / 恢复默认共用）。</summary>
    private void LoadValues(ExtendTriggerSettings s)
    {
        _rbAdvance.IsChecked = s.TriggerMode == ExtendTriggerSettings.Mode.AdvanceStep;
        _rbTime.IsChecked = s.TriggerMode == ExtendTriggerSettings.Mode.TimeStep;
        _tbAdvanceStep.Text = s.AdvanceStepM.ToString("0.##", Inv);
        _tbTimePeriods.Text = s.TimeStepPeriods.ToString(Inv);
        _cbAutoReclaim.IsChecked = s.AutoReclaimTemporary;

        _tbStep.Text = s.SampleStepM.ToString("0.##", Inv);
        _tbTol.Text = s.MatchToleranceM.ToString("0.##", Inv);
        _tbAngle.Text = s.MatchAngleDeg.ToString("0.##", Inv);
        _tbNewCover.Text = s.NewCoverFrac.ToString("0.###", Inv);
        _tbGoneCover.Text = s.GoneCoverFrac.ToString("0.###", Inv);
        _tbGrow.Text = s.GrowMinLenM.ToString("0.##", Inv);
        _tbShift.Text = s.ShiftThresholdM.ToString("0.##", Inv);

        _cbUseDir.IsChecked = s.UseAdvanceDir;
        _tbAzimuth.Text = s.AdvanceAzimuthDeg.ToString("0.##", Inv);

        _err.Text = ""; _err.IsVisible = false;
        SyncTriggerEnabled();
        SyncDirEnabled();
    }

    /// <summary>按选中触发模式启用对应数值框。</summary>
    private void SyncTriggerEnabled()
    {
        bool adv = _rbAdvance.IsChecked == true;
        _tbAdvanceStep.IsEnabled = adv;
        _tbTimePeriods.IsEnabled = !adv;
    }

    /// <summary>勾选「启用」才允许填方位角。</summary>
    private void SyncDirEnabled() => _tbAzimuth.IsEnabled = _cbUseDir.IsChecked == true;

    /// <summary>解析失败给 NaN，交由 <see cref="ExtendTriggerSettings.Validate"/> 统一拦。</summary>
    private static double ParseOrNaN(TextBox tb)
        => double.TryParse(tb.Text, NumberStyles.Any, Inv, out double v) ? v : double.NaN;

    internal ExtendTriggerSettings ReadUi()
    {
        double periods = ParseOrNaN(_tbTimePeriods);
        return new ExtendTriggerSettings
        {
            TriggerMode = _rbTime.IsChecked == true ? ExtendTriggerSettings.Mode.TimeStep : ExtendTriggerSettings.Mode.AdvanceStep,
            AdvanceStepM = ParseOrNaN(_tbAdvanceStep),
            // NaN 转 int 是未定义值，会把"没填"变成一个看着像数的垃圾；留 0 让 Validate 拦下来
            TimeStepPeriods = double.IsNaN(periods) ? 0 : (int)Math.Round(periods),
            AutoReclaimTemporary = _cbAutoReclaim.IsChecked == true,
            SampleStepM = ParseOrNaN(_tbStep),
            MatchToleranceM = ParseOrNaN(_tbTol),
            MatchAngleDeg = ParseOrNaN(_tbAngle),
            NewCoverFrac = ParseOrNaN(_tbNewCover),
            GoneCoverFrac = ParseOrNaN(_tbGoneCover),
            GrowMinLenM = ParseOrNaN(_tbGrow),
            ShiftThresholdM = ParseOrNaN(_tbShift),
            UseAdvanceDir = _cbUseDir.IsChecked == true,
            AdvanceAzimuthDeg = ParseOrNaN(_tbAzimuth),
        };
    }

    private void Commit()
    {
        var s = ReadUi();
        var errs = s.Validate();
        if (errs.Count > 0)
        {
            _err.Text = "✖ " + string.Join("\n✖ ", errs);
            _err.IsVisible = true;
            return;
        }
        if (!s.TrySave(null, out string? err))
        {
            _err.Text = "保存失败：" + (err ?? "未知原因");
            _err.IsVisible = true;
            return;
        }
        _echo("✓ 延拓参数已保存（" + s.Caption + "）");
        Close();
    }

    // ── 自检钩子用 ──
    internal string ErrText => _err.Text ?? "";
    internal bool AdvanceBoxEnabled => _tbAdvanceStep.IsEnabled;
    internal bool AzimuthBoxEnabled => _tbAzimuth.IsEnabled;
}
