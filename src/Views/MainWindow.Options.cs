using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「选项」对话框（HOME → 文件 · 选项，对应原版 <c>Options/Views/OptionsDialog</c>：显示 / 选择集 两页）+
/// 主题与视口配色的读取、套用。
///
/// 原版这几项大多"只存配置、待 T7 渲染管线接入"；这里全部接通并**选中即预览**：
///   显示   —— 界面主题(浅/深) · 2D/3D 视口背景色 · 十字光标颜色/尺寸 · 显示网格 · 显示 Gizmo(夹点) · 功能区缩放
///   选择集 —— 对象捕捉 + 拾取框尺寸(像素) · 夹点尺寸(像素) · 未选/选中夹点颜色
/// 取消 = 把预览过的项全部还原；确定 = 落 config.json(键名沿用原版 Options.*)。
///
/// 主题切换 = 改 <see cref="Application.RequestedThemeVariant"/>：FluentTheme 标准控件自动跟随，
/// 我们自己写死过的界面配色已全部改为 {DynamicResource Theme.*}(Styles/Theme.axaml 里浅/深各一套)。
/// 视口背景/光标色进 <see cref="Controls.CadGlViewport"/>(格网明暗随背景自动推；浅底时近白几何自动压深)。
/// </summary>
public partial class MainWindow
{
    private const string KeyTheme       = "Options.Theme";                 // "Light" / "Dark"
    private const string KeyBg2D        = "Options.Display.Bg2D";          // "#RRGGBB"
    private const string KeyBg3D        = "Options.Display.Bg3D";
    private const string KeyCursorColor = "Options.Display.CursorColor";
    private const string KeyGizmoOn     = "Options.Display.GizmoVisible";  // bool 三轴变换手柄(MainWindow.Gizmo.cs)
    private const string KeyGripsOn     = "Options.Selection.GripsVisible"; // bool 夹点显示(夹点开关)
    private const string KeyGripSize    = "Options.Selection.GripSize";    // int 整边长 4..20 px
    private const string KeyGripUnsel   = "Options.Selection.GripUnsel";   // "#RRGGBB"
    private const string KeyGripSel     = "Options.Selection.GripSel";

    private (float r, float g, float b) _bg2D = Controls.CadGlViewport.DefaultBackground;
    private (float r, float g, float b) _bg3D = Controls.CadGlViewport.DefaultBackground;
    private (float r, float g, float b) _cursorRgb = (1f, 1f, 1f);

    private static bool IsDarkTheme => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>启动时套用视口配色 / 夹点样式 / Gizmo 开关(主题在 App 里更早就切好了)。</summary>
    private void LoadDisplaySettings()
    {
        try
        {
            var themeBg = ThemeDefaultBackground(IsDarkTheme);   // 没单独配过底色 → 跟主题
            _bg2D = ParseRgb(Cfg.Get<string>(KeyBg2D, null)) ?? themeBg;
            _bg3D = ParseRgb(Cfg.Get<string>(KeyBg3D, null)) ?? themeBg;
            _cursorRgb = ParseRgb(Cfg.Get<string>(KeyCursorColor, null)) ?? (1f, 1f, 1f);
            int gs = Cfg.Get<int>(KeyGripSize, (int)(GripGlyph.DefaultHalfSizePx * 2));
            GripGlyph.HalfSizePx = Math.Clamp(gs, 4, 20) / 2.0;
            GripGlyph.ColdColor = ParseRgb(Cfg.Get<string>(KeyGripUnsel, null)) ?? GripGlyph.DefaultCold;
            GripGlyph.HotColor = ParseRgb(Cfg.Get<string>(KeyGripSel, null)) ?? GripGlyph.DefaultHot;
            bool grips = Cfg.Get<bool>(KeyGripsOn, _gripsOn);
            if (grips != _gripsOn) { _gripsOn = grips; HighlightSelection(); }
            bool gizmo = Cfg.Get<bool>(KeyGizmoOn, _gizmoOn);
            if (gizmo != _gizmoOn) { _gizmoOn = gizmo; HighlightSelection(); }
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("配置", $"套用显示配置失败: {ex.Message}"); }
        ApplyViewportColors();
    }

    /// <summary>把背景/光标色推给所有已建视口(多标签各一份；新标签在 EnsureHost 里继承)。</summary>
    private void ApplyViewportColors()
    {
        foreach (var d in _docs)
            if (d.Vp != null) { d.Vp.Background2D = _bg2D; d.Vp.Background3D = _bg3D; d.Vp.CursorColor = _cursorRgb; }
    }

    /// <summary>
    /// 切浅/深主题并记配置。整个应用(含已开的独立窗口)即时生效。
    /// 视口底色若还是"另一主题的默认色"就跟着换成本主题默认(浅=深灰 / 深=原版海军蓝)——用户自己改过的底色不动。
    /// </summary>
    private void ApplyTheme(bool dark, bool persist)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var from = ThemeDefaultBackground(!dark); var to = ThemeDefaultBackground(dark);
        bool changed = false;
        if (_bg2D.Equals(from)) { _bg2D = to; changed = true; }
        if (_bg3D.Equals(from)) { _bg3D = to; changed = true; }
        if (changed) ApplyViewportColors();
        if (persist) Cfg.Set(KeyTheme, dark ? "Dark" : "Light");
    }

    /// <summary>某主题配套的视口默认底色。</summary>
    private static (float r, float g, float b) ThemeDefaultBackground(bool dark)
        => dark ? Controls.CadGlViewport.DarkThemeBackground : Controls.CadGlViewport.DefaultBackground;

    private void SaveDisplaySettings()
    {
        Cfg.Set(KeyBg2D, ToHex(_bg2D));
        Cfg.Set(KeyBg3D, ToHex(_bg3D));
        Cfg.Set(KeyCursorColor, ToHex(_cursorRgb));
        Cfg.Set(KeyGizmoOn, _gizmoOn);
        Cfg.Set(KeyGripsOn, _gripsOn);
        Cfg.Set(KeyGripSize, (int)Math.Round(GripGlyph.HalfSizePx * 2));
        Cfg.Set(KeyGripUnsel, ToHex(GripGlyph.ColdColor));
        Cfg.Set(KeyGripSel, ToHex(GripGlyph.HotColor));
    }

    // ── 颜色互转 ──
    private static (float r, float g, float b)? ParseRgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { var c = Color.Parse(hex.Trim()); return (c.R / 255f, c.G / 255f, c.B / 255f); }
        catch { return null; }
    }
    private static string ToHex((float r, float g, float b) c) => $"#{B(c.r):X2}{B(c.g):X2}{B(c.b):X2}";
    private static byte B(float v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
    private static Color ToColor((float r, float g, float b) c) => Color.FromRgb(B(c.r), B(c.g), B(c.b));
    private static (float r, float g, float b) FromColor(Color c) => (c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>把控件的画刷属性绑到主题资源(切主题即时换色；代码里建的控件不能写死 Brush.Parse)。</summary>
    internal static void ThemeBind(StyledElement c, AvaloniaProperty prop, string key)
        => c.Bind(prop, c.GetResourceObservable(key));

    /// <summary>对话框里的小字说明：随主题走的灰色。</summary>
    private static TextBlock Hint(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        ThemeBind(tb, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return tb;
    }

    private static TextBlock Label(string text, double width = 140)
        => new() { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params Control[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var i in items) sp.Children.Add(i);
        return sp;
    }

    /// <summary>分组(同原版 GroupBox)：标题 + 缩进的内容, 底部一道分隔线。</summary>
    private static Control Group(string header, params Control[] rows)
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(8, 4, 0, 0) };
        foreach (var r in rows) body.Children.Add(r);
        var title = new TextBlock { Text = header, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var b = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 10), Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel { Children = { title, body } },
        };
        ThemeBind(b, Border.BorderBrushProperty, "Theme.Panel.Border");
        return b;
    }

    /// <summary>色块 + 「…」取色 + 十六进制值(同原版 brd/btnPick/lblHex 三件套)。onChange 选中即预览；set 供"一键套用"回写。</summary>
    private (Control row, Action<(float r, float g, float b)> set) ColorRow(string label, (float r, float g, float b) initial,
        Action<(float r, float g, float b)> onChange)
    {
        var cur = initial;
        var swatch = new Border
        {
            Width = 36, Height = 22, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(ToColor(cur)), VerticalAlignment = VerticalAlignment.Center,
        };
        ThemeBind(swatch, Border.BorderBrushProperty, "Theme.Input.Border");
        var hex = new TextBlock { Text = ToHex(cur), FontFamily = new FontFamily("Consolas,monospace"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };
        var pick = new Button { Content = "…", Width = 28, Height = 22, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        void Set((float r, float g, float b) c)
        {
            cur = c;
            swatch.Background = new SolidColorBrush(ToColor(c));
            hex.Text = ToHex(c);
        }
        pick.Click += async (_, _) =>
        {
            var c = await Controls.ColorPickDialog.PickAsync(this, ToColor(cur));
            if (c == null) return;
            Set(FromColor(c.Value));
            onChange(cur);
        };
        return (Row(Label(label), swatch, pick, hex), Set);
    }

    /// <summary>「选项」对话框：显示 / 选择集 两页(同原版), 选中即预览, 取消还原, 确定落盘。</summary>
    private void ShowOptions()
    {
        // ── 快照(取消时还原) ──
        bool themeBefore = IsDarkTheme;
        var bg2Before = _bg2D; var bg3Before = _bg3D; var curBefore = _cursorRgb;
        double cursorPctBefore = _cursorSizePct;
        bool gridBefore = _gridOn, gripsBefore = _gripsOn, gizmoBefore = _gizmoOn;
        double gripHalfBefore = GripGlyph.HalfSizePx;
        var coldBefore = GripGlyph.ColdColor; var hotBefore = GripGlyph.HotColor;
        double ribbonBefore = _ribbonScaleSetting;

        // ═════════ 显示 ═════════
        var (bg2Row, bg2Set) = ColorRow("2D 视口背景色", _bg2D, c => { _bg2D = c; ApplyViewportColors(); });
        var (bg3Row, bg3Set) = ColorRow("3D 视口背景色", _bg3D, c => { _bg3D = c; ApplyViewportColors(); });
        var presetBg = new ComboBox { Width = 200, PlaceholderText = "常用配色…", ItemsSource = new[] { "深灰（浅色主题默认）", "海军蓝（深色主题默认）", "黑", "白", "米黄" } };
        presetBg.SelectionChanged += (_, _) =>
        {
            var c = presetBg.SelectedIndex switch
            {
                1 => Controls.CadGlViewport.DarkThemeBackground, 2 => (0f, 0f, 0f), 3 => (1f, 1f, 1f), 4 => (0.98f, 0.96f, 0.90f),
                _ => Controls.CadGlViewport.DefaultBackground,
            };
            _bg2D = _bg3D = c; ApplyViewportColors();
            bg2Set(c); bg3Set(c);
        };
        var theme = new ComboBox { Width = 200, ItemsSource = new[] { "浅色", "深色" }, SelectedIndex = themeBefore ? 1 : 0 };
        theme.SelectionChanged += (_, _) =>
        {
            ApplyTheme(theme.SelectedIndex == 1, persist: false);
            bg2Set(_bg2D); bg3Set(_bg3D);   // 底色可能随主题换了默认, 色块同步
        };
        var themeGroup = Group("主题", Row(Label("界面主题"), theme),
            Hint("浅/深整套切换：功能区、停靠面板、信息栏、状态栏与各功能窗口同时生效；绘图区颜色另由下面的视口背景决定。"));

        var bgGroup = Group("视口背景", bg2Row, bg3Row, Row(Label("一键套用"), presetBg),
            Hint("格网明暗随背景自动调整；背景为浅色时，近白色的图元自动按深色显示（同 AutoCAD 白底黑线）。切换界面主题时，仍是默认色的底色会跟着换成该主题的默认。"));

        var (curRow, _) = ColorRow("十字光标颜色", _cursorRgb, c => { _cursorRgb = c; ApplyViewportColors(); });
        var curSlider = new Slider { Minimum = 2, Maximum = 100, Value = _cursorSizePct, Width = 220, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var curVal = new TextBlock { Text = $"{_cursorSizePct:0}", MinWidth = 28, VerticalAlignment = VerticalAlignment.Center };
        curSlider.ValueChanged += (_, _) => { SetCursorSize(curSlider.Value); curVal.Text = $"{_cursorSizePct:0}"; };
        var grid = new CheckBox { Content = "显示网格", IsChecked = _gridOn };
        grid.IsCheckedChanged += (_, _) => SetGrid(grid.IsChecked == true);
        var grips = new CheckBox { Content = "启用夹点显示", IsChecked = _gripsOn };
        grips.IsCheckedChanged += (_, _) => { if (_gripsOn != (grips.IsChecked == true)) ToggleGrips(); };
        var gizmo = new CheckBox { Content = "显示 Gizmo（三轴变换手柄）", IsChecked = _gizmoOn };
        gizmo.IsCheckedChanged += (_, _) => { if (_gizmoOn != (gizmo.IsChecked == true)) ToggleGizmo(); };
        var cursorGroup = Group("光标 / Gizmo", curRow, Row(Label("十字光标尺寸 (%)"), curSlider, curVal),
            Hint("100 % = 满屏十字（默认）；调小即成短十字，中心拾取框大小不变。拖动即可预览。"),
            Row(Label("网格 / 夹点"), grid, grips, gizmo));

        var scaleBox = new ComboBox { Width = 220, ItemsSource = RibbonScalePresets.Select(x => x.label).ToList() };
        int curIdx = 0;
        for (int i = 0; i < RibbonScalePresets.Length; i++)
            if (Math.Abs(RibbonScalePresets[i].targetWidth - _ribbonScaleSetting) < 1e-6) { curIdx = i; break; }
        scaleBox.SelectedIndex = curIdx;
        scaleBox.SelectionChanged += (_, _) => { _ribbonScaleSetting = RibbonScalePresets[Math.Max(0, scaleBox.SelectedIndex)].targetWidth; FitRibbons(); };
        var scaleGroup = Group("功能区", Row(Label("功能区缩放（屏幕适配）"), scaleBox),
            Hint("功能区按 1:1 需要约 2300px 宽，1080p 放不下故默认缩放。选固定分辨率按该宽度缩放，适合投屏或多屏切换；选「自动」则跟随当前窗口宽度。"));

        // 页面布置：面板排布/比例/当前页/钉住在退出时自动记录, 下次启动照原样; 这里只给一个"恢复默认"
        var resetLayout = new Button { Content = "恢复默认页面布置", MinWidth = 150 };
        var layoutState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = _dockLayoutRestored ? "当前：上次退出时记录的排布" : "当前：默认排布" };
        ThemeBind(layoutState, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        resetLayout.Click += (_, _) =>
        {
            ResetDockLayout();
            layoutState.Text = "已清除记录，下次启动按默认排布";
            StatusMsg.Text = "页面布置：已恢复默认（下次启动生效）";
        };
        var layoutGroup = Group("页面布置", Row(resetLayout, layoutState),
            Hint("停靠面板的位置、大小、当前页与钉住状态在退出时自动记录（dock-layout.json），下次打开自动恢复；面板被关掉后想找回，点这里恢复默认。"));

        var displayPage = new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(12), Children = { themeGroup, bgGroup, cursorGroup, scaleGroup, layoutGroup } },
        };

        // ═════════ 选择集 ═════════
        var snap = new CheckBox { Content = "启用对象捕捉", IsChecked = SnapToggle.IsChecked == true };
        var tol = new Slider { Minimum = 3, Maximum = 20, Value = Math.Clamp(_snapTolPx, 3, 20), Width = 220, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var tolVal = new TextBlock { Text = $"{_snapTolPx:0}", MinWidth = 28, VerticalAlignment = VerticalAlignment.Center };
        tol.ValueChanged += (_, _) => tolVal.Text = $"{tol.Value:0}";
        var pickGroup = Group("拾取", snap, Row(Label("拾取框尺寸 (像素)"), tol, tolVal),
            Hint("对象捕捉与点选线/点的命中容差（屏幕像素）。"));

        var gripSize = new Slider { Minimum = 4, Maximum = 20, Value = Math.Round(GripGlyph.HalfSizePx * 2), Width = 220, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var gripVal = new TextBlock { Text = $"{GripGlyph.HalfSizePx * 2:0}", MinWidth = 28, VerticalAlignment = VerticalAlignment.Center };
        gripSize.ValueChanged += (_, _) => { GripGlyph.HalfSizePx = gripSize.Value / 2.0; gripVal.Text = $"{gripSize.Value:0}"; HighlightSelection(); };
        var (coldRow, _) = ColorRow("未选夹点颜色", GripGlyph.ColdColor, c => { GripGlyph.ColdColor = c; HighlightSelection(); });
        var (hotRow, _) = ColorRow("选中夹点颜色", GripGlyph.HotColor, c => { GripGlyph.HotColor = c; HighlightSelection(); });
        var gripGroup = Group("夹点 (Grip)", Row(Label("夹点尺寸 (像素)"), gripSize, gripVal), coldRow, hotRow,
            Hint("AutoCAD 三态：未选蓝 / 悬停绿 / 选中红；悬停色固定。先选中一个图元再调，可即时看到效果。"));

        var selectPage = new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(12), Children = { pickGroup, gripGroup } },
        };

        // ═════════ 窗体 ═════════
        var tabs = new TabControl
        {
            TabStripPlacement = Avalonia.Controls.Dock.Left, Margin = new Thickness(8),
            ItemsSource = new[]
            {
                new TabItem { Header = "显示", Content = displayPage, Padding = new Thickness(12, 6) },
                new TabItem { Header = "选择集", Content = selectPage, Padding = new Thickness(12, 6) },
            },
        };
        var ok = new Button { Content = "确定", MinWidth = 90, IsDefault = true };
        var apply = new Button { Content = "应用", MinWidth = 90 };
        var cancel = new Button { Content = "取消", MinWidth = 90, IsCancel = true };
        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 8),
            Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, apply, cancel } },
        };
        ThemeBind(footer, Border.BorderBrushProperty, "Theme.Panel.Border");
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
        var root = new DockPanel { Children = { footer, tabs } };   // 页脚 Dock 到底, 内容滚动 —— 缩放≠1 时「确定」不会被顶出屏幕
        var win = new Window
        {
            Title = "选项", Width = 780, Height = 680, MinWidth = 640, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true,
            Content = root,
        };
        ThemeBind(win, Window.BackgroundProperty, "Theme.Window.Background");

        void Commit()
        {
            SnapToggle.IsChecked = snap.IsChecked == true;
            _snapTolPx = tol.Value;
            _ribbonScaleSetting = RibbonScalePresets[Math.Max(0, scaleBox.SelectedIndex)].targetWidth;
            SaveRibbonScaleSetting();
            SaveCursorSizeSetting();
            SaveDisplaySettings();
            Cfg.Set(KeyTheme, theme.SelectedIndex == 1 ? "Dark" : "Light");
            Cfg.Set(KeySnapTolPx, _snapTolPx);
            Cfg.Set(KeySnapOn, SnapToggle.IsChecked == true);
            Cfg.Set(KeyGridOn, _gridOn);
            FitRibbons();
            StatusMsg.Text = $"选项已应用（主题 {(theme.SelectedIndex == 1 ? "深色" : "浅色")} · 视口背景 2D {ToHex(_bg2D)} / 3D {ToHex(_bg3D)}"
                           + $" · 十字光标 {ToHex(_cursorRgb)} {_cursorSizePct:0}% · 网格 {(_gridOn ? "开" : "关")} · 夹点 {(_gripsOn ? "开" : "关")} {GripGlyph.HalfSizePx * 2:0}px"
                           + $" · 捕捉 {(SnapToggle.IsChecked == true ? "开" : "关")} 拾取框 {_snapTolPx:0}px）";
        }
        void Revert()
        {
            ApplyTheme(themeBefore, persist: false);
            _bg2D = bg2Before; _bg3D = bg3Before; _cursorRgb = curBefore; ApplyViewportColors();
            SetCursorSize(cursorPctBefore);
            if (_gridOn != gridBefore) SetGrid(gridBefore);
            if (_gripsOn != gripsBefore) ToggleGrips();
            if (_gizmoOn != gizmoBefore) ToggleGizmo();
            GripGlyph.HalfSizePx = gripHalfBefore; GripGlyph.ColdColor = coldBefore; GripGlyph.HotColor = hotBefore; HighlightSelection();
            _ribbonScaleSetting = ribbonBefore; FitRibbons();
        }
        bool committed = false;
        apply.Click += (_, _) => { Commit(); committed = true; };
        ok.Click += (_, _) => { Commit(); committed = true; win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Closed += (_, _) => { if (!committed) Revert(); };
        win.Show(this);
    }
}
