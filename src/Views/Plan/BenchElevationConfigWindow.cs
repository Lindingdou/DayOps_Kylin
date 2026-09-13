using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.GeoDb;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「标注台阶标高」样式配置对话框（大小 / 字体 / 倾斜 / 颜色 / 落平盘；移植原 <c>BenchElevationConfigWindow</c>）。模态；
/// 点「保存」回填到 <see cref="Result"/> 供调用方写入用户设置。从按钮下拉的「标注设置…」打开。
/// </summary>
internal sealed class BenchElevationConfigWindow : Window
{
    /// <summary>点「保存」后非空 = 用户确认的配置；取消则保持 null。</summary>
    public BenchElevationConfig? Result { get; private set; }

    private readonly ComboBox _fontCombo, _axisCombo, _dirCombo;
    private readonly TextBox _size = PlanUi.Box("", 70), _angle = PlanUi.Box("30", 56), _hex = PlanUi.Box("FFE000", 80);
    private readonly CheckBox _placeOnBench = new() { Content = "文字落到平盘中央（坡顶线↔坡底线夹出的平盘；缺对向线则标在线上）", IsChecked = true, Margin = new Thickness(0, 4, 0, 8) };
    private readonly RadioButton _colorAuto = new() { Content = "按区域类别自动配色（采场橙 / 排土场蓝 / 达标平盘品红 / 未知黄）", GroupName = "colorMode", IsChecked = true };
    private readonly RadioButton _colorFixed = new() { Content = "统一颜色", GroupName = "colorMode" };
    private readonly Border _preview = new() { Width = 26, Height = 20, Margin = new Thickness(8, 0, 0, 0), CornerRadius = new CornerRadius(3), BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), BorderThickness = new Thickness(1) };

    public BenchElevationConfigWindow(BenchElevationConfig? cfg)
    {
        cfg ??= new BenchElevationConfig();
        Title = "标注台阶标高 — 标注设置";
        Width = 470; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        _fontCombo = PlanUi.Combo(BenchElevationAnnotator.Fonts.Select(f => f.Display), 0, 170);
        int fi = Array.FindIndex(BenchElevationAnnotator.Fonts, f => string.Equals(f.Alias, cfg.FontName ?? "", StringComparison.OrdinalIgnoreCase));
        _fontCombo.SelectedIndex = fi < 0 ? 0 : fi;
        _axisCombo = PlanUi.Combo(new[] { "正立(不倾斜)", "绕 X 轴", "绕 Y 轴", "绕 Z 轴" }, Math.Clamp(cfg.TiltAxis, 0, 3), 110);
        _dirCombo = PlanUi.Combo(new[] { "逆时针", "顺时针" }, cfg.TiltDeg < 0 ? 1 : 0, 90);
        _size.Text = cfg.SizeMeters > 0 ? cfg.SizeMeters.ToString("0.##", CultureInfo.CurrentCulture) : "";
        _placeOnBench.IsChecked = cfg.PlaceOnBenchCenter;
        _angle.Text = Math.Abs(cfg.TiltDeg).ToString("0.##", CultureInfo.CurrentCulture);
        _colorAuto.IsChecked = cfg.AutoColorByCategory; _colorFixed.IsChecked = !cfg.AutoColorByCategory;
        _hex.Text = (cfg.FixedColorRgb & 0xFFFFFF).ToString("X6");
        _hex.TextChanged += (_, _) => UpdatePreview();

        var stack = new StackPanel { Margin = new Thickness(12) };
        var g1 = new StackPanel();
        var r1 = RoadUi.Row(RoadUi.Lbl("大小"), _size, RoadUi.Hint("m（留空 = 自动：按标注范围对角线 0.6% 取）")); g1.Children.Add(r1);
        var r2 = RoadUi.Row(RoadUi.Lbl("字体"), _fontCombo); r2.Margin = new Thickness(0, 6, 0, 0); g1.Children.Add(r2);
        stack.Children.Add(PlanUi.Group("符号大小 / 字体", g1, new Thickness(0, 0, 0, 8), 10));
        var g2 = new StackPanel();
        var r3 = RoadUi.Row(RoadUi.Lbl("绕轴"), _axisCombo, RoadUi.Lbl("角度"), _angle, RoadUi.Lbl("°"), _dirCombo); g2.Children.Add(r3);
        g2.Children.Add(RoadUi.Hint("正立 = 沿 Z 轴竖立；绕 X/Y 让标注前倾/侧倾，便于三维斜视查看；方向按从该轴正向看。Kylin 平面文字只把「绕 Z」当旋转角，其余记录、随导出带走。", 11.5));
        stack.Children.Add(PlanUi.Group("倾斜", g2, new Thickness(0, 0, 0, 8), 10));
        var g3 = new StackPanel();
        g3.Children.Add(_colorAuto);
        var r4 = RoadUi.Row(_colorFixed, RoadUi.Lbl("#"), _hex, _preview); r4.Margin = new Thickness(0, 4, 0, 0); g3.Children.Add(r4);
        stack.Children.Add(PlanUi.Group("颜色", g3, new Thickness(0, 0, 0, 4), 10));
        stack.Children.Add(_placeOnBench);
        var foot = new DockPanel();
        var btns = RoadUi.Foot(RoadUi.Btn("保存", () => _ = OnSaveAsync(), 76), RoadUi.Btn("取消", Close, 70)); btns.Margin = new Thickness(0);
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Right); foot.Children.Add(btns);
        var hint = RoadUi.Hint("保存后记入配置，下次标注自动套用。", 12); hint.VerticalAlignment = VerticalAlignment.Center; foot.Children.Add(hint);
        stack.Children.Add(foot);
        Content = stack;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (TryParseHex(_hex.Text, out uint rgb)) _preview.Background = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    private static bool TryParseHex(string? s, out uint rgb)
    {
        rgb = 0;
        s = s?.Trim().TrimStart('#');
        return !string.IsNullOrEmpty(s) && s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    private async Task OnSaveAsync()
    {
        var cfg = new BenchElevationConfig();
        cfg.SizeMeters = double.TryParse((_size.Text ?? "").Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out double sz) && sz > 0 ? sz : 0;
        cfg.PlaceOnBenchCenter = _placeOnBench.IsChecked == true;
        cfg.TiltAxis = Math.Clamp(_axisCombo.SelectedIndex, 0, 3);
        double deg = double.TryParse((_angle.Text ?? "").Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out double a) ? a : 0;
        deg = Math.Abs(deg); if (_dirCombo.SelectedIndex == 1) deg = -deg;
        cfg.TiltDeg = deg;
        cfg.FontName = _fontCombo.SelectedIndex >= 0 ? BenchElevationAnnotator.Fonts[_fontCombo.SelectedIndex].Alias : "";
        cfg.AutoColorByCategory = _colorAuto.IsChecked == true;
        if (!cfg.AutoColorByCategory)
        {
            if (!TryParseHex(_hex.Text, out uint rgb)) { await CoalMsgBox.ShowAsync(this, "标注设置", "颜色请填 6 位十六进制（如 FFE000）。"); return; }
            cfg.FixedColorRgb = rgb;
        }
        else cfg.FixedColorRgb = TryParseHex(_hex.Text, out uint kept) ? kept : 0xFFE000;
        Result = cfg;
        Close();
    }
}
