using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// 实体取色器（AutoCAD 式下拉）—— 忠实原版 <c>Controls/EntityColorPicker.xaml</c>：
/// 关闭态 = 当前色块 + 文字 + ▾；下拉 = 随层 + ACI 索引色板(9 列 × 3 行) + 「更多颜色…（自定义 RGB）」。
/// 功能区「特性 - 颜色」与「填充 - 颜色」两处共用同一个控件(原版也是同一个 EntityColorPicker 摆两处)。
///
/// 原版还有「随块(ByBlock)」一项：Kylin 场景实体只存 RGB、没有块的概念，那一项无处落地，故只留「随层」。
///
/// ⚠ 别改回 MenuFlyout + Opening 里赋 ItemsSource 的老写法：Avalonia 11.2 的 MenuFlyout
/// 在 Opening 里改 ItemsSource, 呈现器已建好、拿不到新项 —— 下拉照样"打开"(IsOpen=True)但里面空无一物,
/// 于是屏幕上什么也看不见, 表现就是"点颜色没反应"。
/// </summary>
public sealed class EntityColorPicker : UserControl
{
    private readonly ToggleButton _toggle = new();
    private readonly Border _swatch;
    private readonly TextBlock _text;
    private readonly Popup _popup;
    private readonly Border _byLayerSwatch;

    private (float r, float g, float b)? _value;                       // null = 随层
    private (float r, float g, float b) _layerColor = (1, 1, 1);       // 「随层」显示成什么色

    /// <summary>用户在下拉里选定了颜色（null = 随层）。回填（<see cref="SetValue"/>）不触发。</summary>
    public event EventHandler<(float r, float g, float b)?>? ColorCommitted;

    /// <summary>当前值；null = 随层。</summary>
    public (float r, float g, float b)? SelectedRgb => _value;

    public EntityColorPicker()
    {
        _swatch = new Border
        {
            Width = 18, Height = 14, Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1), BorderBrush = Brush.Parse("#8A8F97"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text = new TextBlock { Text = "随层", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

        var arrow = new TextBlock { Text = "▾", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 12 };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(arrow, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(_swatch, Avalonia.Controls.Dock.Left);
        dock.Children.Add(arrow);
        dock.Children.Add(_swatch);
        dock.Children.Add(_text);

        _toggle.Height = 22;
        _toggle.MinHeight = 22;
        _toggle.Padding = new Thickness(4, 0);
        _toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
        _toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _toggle.VerticalContentAlignment = VerticalAlignment.Center;
        _toggle.Content = dock;
        _toggle.IsCheckedChanged += (_, _) => _popup.IsOpen = _toggle.IsChecked == true;

        _byLayerSwatch = new Border
        {
            Width = 16, Height = 12, Margin = new Thickness(0, 0, 6, 0),
            BorderThickness = new Thickness(1), BorderBrush = Brush.Parse("#8A8F97"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _popup = new Popup
        {
            PlacementTarget = _toggle,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = BuildDropdown(),
        };
        _popup.Closed += (_, _) => _toggle.IsChecked = false;

        Content = new Panel { Children = { _toggle, _popup } };
        RefreshDisplay();
    }

    private Control BuildDropdown()
    {
        var byLayer = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 2), Margin = new Thickness(0, 0, 0, 6),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { _byLayerSwatch, new TextBlock { Text = "随层", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 } },
            },
        };
        ToolTip.SetTip(byLayer, "跟随所在图层的颜色（AutoCAD 的 ByLayer）");
        byLayer.Click += (_, _) => Commit(null);

        var grid = new UniformGrid { Columns = 9 };
        foreach (int aci in AcadColorTable.SwatchIndices)
        {
            var (r, g, b) = AcadColorTable.Rgb255(aci);
            var cell = new Button
            {
                Width = 20, Height = 18, Margin = new Thickness(1), Padding = new Thickness(0), MinWidth = 0,
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderThickness = new Thickness(1), BorderBrush = Brush.Parse("#8A8F97"),
                Tag = aci,
            };
            ToolTip.SetTip(cell, $"索引 {aci}（#{r:X2}{g:X2}{b:X2}）");
            cell.Click += (s, _) =>
            {
                if (s is Button bt && bt.Tag is int i) Commit(AcadColorTable.RgbF(i));
            };
            grid.Children.Add(cell);
        }

        var more = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 3),
            Content = new TextBlock { Text = "更多颜色…（自定义 RGB）", FontSize = 12 },
        };
        more.Click += async (_, _) =>
        {
            var cur = _value ?? _layerColor;
            _popup.IsOpen = false;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var picked = await ColorPickDialog.PickAsync(owner, Color.FromRgb(
                (byte)Math.Round(Math.Clamp(cur.r, 0, 1) * 255),
                (byte)Math.Round(Math.Clamp(cur.g, 0, 1) * 255),
                (byte)Math.Round(Math.Clamp(cur.b, 0, 1) * 255)));
            if (picked is { } c) Commit((c.R / 255f, c.G / 255f, c.B / 255f));
        };

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#C8CDD4"),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 4 12 #33000000"),
            Padding = new Thickness(8),
            MinWidth = 220,
            Child = new StackPanel
            {
                Children =
                {
                    byLayer,
                    new TextBlock { Text = "索引颜色", FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) },
                    grid,
                    new Separator { Margin = new Thickness(0, 6) },
                    more,
                },
            },
        };
    }

    private void Commit((float r, float g, float b)? rgb)
    {
        _value = rgb;
        _popup.IsOpen = false;
        _toggle.IsChecked = false;
        RefreshDisplay();
        ColorCommitted?.Invoke(this, rgb);
    }

    /// <summary>回填显示（选中变化 / 命令行改色时用），不触发 <see cref="ColorCommitted"/>。</summary>
    public void SetValue((float r, float g, float b)? rgb, (float r, float g, float b) layerColor)
    {
        _value = rgb;
        _layerColor = layerColor;
        RefreshDisplay();
    }

    /// <summary>打开下拉（自检用；等价于点一下按钮）。</summary>
    public void OpenDropdown() => _toggle.IsChecked = true;

    /// <summary>自检：等价于在下拉里点某个索引色块（aci &lt; 1 视作「随层」）。</summary>
    public void PickForSelftest(int aci) => Commit(aci < 1 ? null : AcadColorTable.RgbF(aci));

    /// <summary>下拉是否展开（自检核对用）。</summary>
    public bool IsDropdownOpen => _popup.IsOpen;

    private void RefreshDisplay()
    {
        var c = _value ?? _layerColor;
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Round(Math.Clamp(c.r, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(c.g, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(c.b, 0, 1) * 255)));
        _swatch.Background = brush;
        _text.Text = _value is { } v ? AciPalette.DisplayName(v.r, v.g, v.b) : "随层";
        _byLayerSwatch.Background = new SolidColorBrush(Color.FromRgb(
            (byte)Math.Round(Math.Clamp(_layerColor.r, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(_layerColor.g, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(_layerColor.b, 0, 1) * 255)));
    }
}
