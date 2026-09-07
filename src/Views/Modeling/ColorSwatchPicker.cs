using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 轻量色板选择控件（忠实原 BlockModelLib.Controls.ColorSwatchPicker + ColorPalettePopup）：
/// 色块预览 + Hex 输入 + 「选色...」弹 32 色调色板（8 调色板预设 + 24 标准色）。代码构建, 无 XAML。
/// </summary>
public sealed class ColorSwatchPicker : UserControl
{
    public static readonly StyledProperty<Color> SelectedColorProperty =
        AvaloniaProperty.Register<ColorSwatchPicker, Color>(nameof(SelectedColor), Colors.Gray, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public Color SelectedColor { get => GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public event EventHandler<Color>? SelectedColorChanged;

    private readonly Border _preview;
    private readonly TextBox _hex;
    private readonly Button _btn;
    private bool _syncing;

    private static readonly Color[] StandardColors =
    {
        Colors.Red, Colors.OrangeRed, Colors.Orange, Colors.Gold, Colors.Yellow, Colors.YellowGreen, Colors.Green, Colors.DarkGreen,
        Colors.Cyan, Colors.LightBlue, Colors.SteelBlue, Colors.Blue, Colors.DarkBlue, Colors.Purple, Colors.MediumOrchid, Colors.HotPink,
        Colors.White, Colors.LightGray, Colors.Silver, Colors.Gray, Colors.DimGray, Colors.DarkGray, Colors.Black, Colors.Brown,
    };

    public ColorSwatchPicker()
    {
        _preview = new Border { Width = 28, Height = 22, BorderBrush = new SolidColorBrush(Color.Parse("#A0A0A0")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Colors.Gray) };
        _hex = new TextBox { Width = 78, Margin = new Thickness(6, 0, 0, 0), FontFamily = new FontFamily("Consolas,monospace"), FontSize = 11, MinHeight = 24, Padding = new Thickness(4, 2) };
        _hex.LostFocus += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
        _btn = new Button { Content = "选色...", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 2), FontSize = 11 };
        _btn.Click += (_, _) => OpenPalette();
        Content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _preview, _hex, _btn } };
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedColorProperty)
        {
            Sync();
            SelectedColorChanged?.Invoke(this, SelectedColor);
        }
        if (change.Property == IsEnabledProperty) { _hex.IsEnabled = _btn.IsEnabled = IsEnabled; }
    }

    private void Sync()
    {
        _syncing = true;
        var c = SelectedColor;
        _preview.Background = new SolidColorBrush(c);
        _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _syncing = false;
    }

    private void CommitHex()
    {
        if (_syncing) return;
        var s = (_hex.Text ?? "").Trim().TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            SelectedColor = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        else Sync();
    }

    private void OpenPalette()
    {
        var root = new StackPanel { Orientation = Orientation.Vertical };
        var muted = new SolidColorBrush(Color.Parse("#606060"));
        root.Children.Add(new TextBlock { Text = "调色板预设", FontSize = 10, Foreground = muted, Margin = new Thickness(2, 0, 0, 4) });
        var preset = new Color[Cad.BlockDefaultPalette.Colors.Length];
        for (int i = 0; i < preset.Length; i++) { var (r, g, b) = Cad.BlockDefaultPalette.Colors[i]; preset[i] = Color.FromRgb(r, g, b); }
        var flyout = new Flyout { Placement = PlacementMode.Bottom };
        root.Children.Add(BuildSwatchRow(preset, flyout));
        root.Children.Add(new TextBlock { Text = "标准颜色", FontSize = 10, Foreground = muted, Margin = new Thickness(2, 8, 0, 4) });
        root.Children.Add(BuildSwatchRow(StandardColors, flyout));
        flyout.Content = new Border { Background = Brushes.White, Padding = new Thickness(8), Child = root };
        flyout.ShowAt(_btn);
    }

    private Control BuildSwatchRow(Color[] colors, Flyout flyout)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 8 * 26 };
        foreach (var color in colors)
        {
            var rect = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(1), Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.Parse("#909090")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(rect, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
            var captured = color;
            rect.PointerPressed += (_, _) => { SelectedColor = captured; flyout.Hide(); };
            rect.PointerEntered += (_, _) => { rect.BorderBrush = new SolidColorBrush(Color.Parse("#1E40AF")); rect.BorderThickness = new Thickness(2); };
            rect.PointerExited += (_, _) => { rect.BorderBrush = new SolidColorBrush(Color.Parse("#909090")); rect.BorderThickness = new Thickness(1); };
            wrap.Children.Add(rect);
        }
        return wrap;
    }

    public static Color ToColor((byte r, byte g, byte b) c) => Color.FromRgb(c.r, c.g, c.b);
    public static (byte r, byte g, byte b) ToTuple(Color c) => (c.R, c.G, c.B);
}
