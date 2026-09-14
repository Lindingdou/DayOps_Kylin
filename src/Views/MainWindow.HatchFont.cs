using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「开始 · 注释」组的 填充图案 / 填充颜色 / 文字字体 —— 对齐原版填充组的三件套:
///   · 图案: 命名图案库(<see cref="HatchPatternLibrary"/>, SOLID/ANSI31…/砖墙/蜂窝/土壤/草地) + 用户定义
///   · 颜色: 下一次填充用的颜色(随层 / 标准色板 / 自定义 RGB), 同原版 hatchColorPicker
///   · 字体: 全局切换文字显示字体, 列本机已装字体(常用中文置顶), 同原版 comboTextFont
/// 选中的图案/颜色/字体随配置持久化(关闭即落盘)。
/// </summary>
public partial class MainWindow
{
    private const string KeyHatchPattern = "Options.Hatch.Pattern";   // 图案名(如 ANSI31)
    private const string KeyHatchColor   = "Options.Hatch.Color";     // "#RRGGBB"; 空/缺省 = 随层
    private const string KeyTextFont     = "Options.Display.TextFont";// 字体族名; 空 = 原始(自动探测)

    private HatchPatternLibrary.Pattern _hatchPattern = HatchPatternLibrary.ByName("ANSI31")!;
    private (float r, float g, float b)? _hatchColor;   // null = 随层(取当前图层色)
    private bool _suppressHatchFontRibbon;              // 回填显示时不当成用户操作
    private bool _textFontListLoaded;                   // 系统字体懒加载(不拖慢启动)
    private List<(string Display, string Family)> _textFontItems = new();

    /// <summary>建「注释」组的 图案/颜色/字体 三个控件的初值(启动期调, 系统字体等首次展开再扫)。</summary>
    private void InitHatchFontRibbon()
    {
        _suppressHatchFontRibbon = true;
        try
        {
            if (HatchPatternBox != null)
            {
                HatchPatternBox.ItemsSource = HatchPatternLibrary.All.Select(p => p.Display).ToList();
                string want = Cfg.Get<string>(KeyHatchPattern, "ANSI31") ?? "ANSI31";
                var pat = HatchPatternLibrary.ByName(want) ?? HatchPatternLibrary.ByName("ANSI31")!;
                _hatchPattern = pat;
                HatchPatternBox.SelectedIndex = Math.Max(0, Array.IndexOf(HatchPatternLibrary.All, pat));
            }

            string hex = Cfg.Get<string>(KeyHatchColor, "") ?? "";
            _hatchColor = AciPalette.TryParse(hex, out float r, out float g, out float b) ? (r, g, b) : null;
            if (HatchColorPick != null) HatchColorPick.ColorCommitted += (_, rgb) => SetHatchColor(rgb);
            SyncHatchColorSwatch();

            // 字体: 先只放当前项(启动不扫字体); 展开下拉时才全量列。
            string font = Cfg.Get<string>(KeyTextFont, "") ?? "";
            if (TextFontBox != null)
            {
                _textFontItems = new List<(string, string)> { (SystemFontList.OriginalLabel, "") };
                if (font.Length > 0) _textFontItems.Add((font, font));
                TextFontBox.ItemsSource = _textFontItems.Select(x => x.Display).ToList();
                TextFontBox.SelectedIndex = font.Length > 0 ? 1 : 0;
            }
            if (font.Length > 0 && !GlyphFontHost.TryUseFamily(font))
                PitMine3D.Kylin.CrashLog.Write("字体", $"上次选的字体「{font}」本机没装, 回到自动探测");
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("功能区", $"注释组初始化失败: {ex.Message}"); }
        finally { _suppressHatchFontRibbon = false; }
    }

    // ── 图案 ────────────────────────────────────────────────────────

    private void OnHatchPatternChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressHatchFontRibbon || HatchPatternBox == null) return;
        int i = HatchPatternBox.SelectedIndex;
        if (i < 0 || i >= HatchPatternLibrary.All.Length) return;
        _hatchPattern = HatchPatternLibrary.All[i];
        Cfg.Set(KeyHatchPattern, _hatchPattern.Name);
        int changed = ApplyHatchToSelection(pattern: true);
        if (changed > 0)
        { StatusMsg.Text = $"编辑填充：{changed} 个填充 → {_hatchPattern.Display}"; return; }
        StatusMsg.Text = _hatchPattern.IsUserDefined
            ? $"填充图案: {_hatchPattern.Display} —— 用「角度」「比例(间距)」两栏的值; 十字交叉见 填充 ▾ 菜单"
            : $"填充图案: {_hatchPattern.Display}（比例空 = 按边界大小自动取）";
    }

    // ── 颜色 ────────────────────────────────────────────────────────

    /// <summary>命令行「填充颜色 <#RRGGBB|R,G,B|色名|ACI|随层>」。</summary>
    private void HatchColorCmd(string arg)
    {
        string t = (arg ?? "").Trim();
        if (t.Length == 0)
        {
            StatusMsg.Text = $"填充颜色：当前 {HatchColorLabel()}（用法：填充颜色 #RRGGBB / R,G,B / 红 / 1 / 随层）";
            return;
        }
        if (t is "随层" or "ByLayer" or "bylayer") { SetHatchColor(null); return; }
        if (AciPalette.TryParse(t, out float r, out float g, out float b)) SetHatchColor((r, g, b));
        else StatusMsg.Text = $"填充颜色：无法识别「{t}」（用 #RRGGBB / R,G,B / 色名 / ACI 索引 / 随层）";
    }

    private void SetHatchColor((float r, float g, float b)? rgb)
    {
        _hatchColor = rgb;
        Cfg.Set(KeyHatchColor, rgb is { } c ? AciPalette.Hex(c.r, c.g, c.b) : "");
        SyncHatchColorSwatch();
        int changed = ApplyHatchToSelection(color: true);
        StatusMsg.Text = changed > 0
            ? $"编辑填充：{changed} 个填充 → 颜色 {HatchColorLabel()}"
            : $"填充颜色: {HatchColorLabel()}（下一次填充生效）";
    }

    private string HatchColorLabel()
        => _hatchColor is { } c ? AciPalette.DisplayName(c.r, c.g, c.b) : "随层";

    /// <summary>回填填充取色器的显示；不触发写入。</summary>
    private void SyncHatchColorSwatch()
    {
        var l = _layers.Current;
        HatchColorPick?.SetValue(_hatchColor, (l.Cr, l.Cg, l.Cb));
    }

    // ── 字体 ────────────────────────────────────────────────────────

    private void OnTextFontDropDownOpened(object? sender, EventArgs e)
    {
        if (_textFontListLoaded || TextFontBox == null) return;
        _suppressHatchFontRibbon = true;
        try
        {
            string prev = TextFontBox.SelectedIndex >= 0 && TextFontBox.SelectedIndex < _textFontItems.Count
                ? _textFontItems[TextFontBox.SelectedIndex].Family : "";
            _textFontItems = SystemFontList.FromSystem();
            TextFontBox.ItemsSource = _textFontItems.Select(x => x.Display).ToList();
            int idx = _textFontItems.FindIndex(x => string.Equals(x.Family, prev, StringComparison.OrdinalIgnoreCase));
            TextFontBox.SelectedIndex = idx >= 0 ? idx : 0;
            _textFontListLoaded = true;
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("字体", $"填充字体下拉失败: {ex.Message}"); }
        finally { _suppressHatchFontRibbon = false; }
    }

    private void OnTextFontChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressHatchFontRibbon || TextFontBox == null) return;
        int i = TextFontBox.SelectedIndex;
        if (i < 0 || i >= _textFontItems.Count) return;
        var (display, family) = _textFontItems[i];
        ApplyTextFont(family, display);
    }

    /// <summary>换视口文字字体(全局覆盖, 立即重绘)。family 为空 = 回自动探测。装不上如实回显, 不悄悄换别的。</summary>
    private void ApplyTextFont(string family, string display)
    {
        bool ok = GlyphFontHost.TryUseFamily(family);
        Cfg.Set(KeyTextFont, ok ? family : "");
        RefreshScene();
        StatusMsg.Text = family.Length == 0
            ? $"文字字体 → {SystemFontList.OriginalLabel}（当前 {GlyphFontHost.ActiveFamily ?? "默认"}）"
            : ok ? $"文字字体 → {display}（当前 {GlyphFontHost.ActiveFamily ?? display}）"
                 : $"文字字体：本机未装「{display}」，仍用 {GlyphFontHost.ActiveFamily ?? "默认"}";
    }

    /// <summary>命令行「字体 <族名>」/「字体」列出可用字体。</summary>
    private void TextFontCmd(string arg)
    {
        string t = (arg ?? "").Trim();
        if (t.Length == 0)
        {
            var items = SystemFontList.FromSystem();
            LogCommand($"可用字体 {items.Count - 1} 种（前 20）：" + string.Join(" · ", items.Skip(1).Take(20).Select(x => x.Display)));
            StatusMsg.Text = $"字体：当前 {GlyphFontHost.ActiveFamily ?? "默认"}（用法：字体 <族名>，或用 开始→注释→字体 下拉）";
            return;
        }
        if (t is "原始" or "自动") { ApplyTextFont("", SystemFontList.OriginalLabel); SyncTextFontBox(""); return; }
        // 允许输入中文名(下拉里显示的那个), 回查真实族名
        var all = SystemFontList.FromSystem();
        var hit = all.FirstOrDefault(x => string.Equals(x.Display, t, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(x.Family, t, StringComparison.OrdinalIgnoreCase));
        string fam = hit.Family ?? t;
        if (string.IsNullOrEmpty(fam)) fam = t;
        ApplyTextFont(fam, hit.Display ?? t);
        SyncTextFontBox(fam);
    }

    private void SyncTextFontBox(string family)
    {
        if (TextFontBox == null) return;
        _suppressHatchFontRibbon = true;
        try
        {
            if (!_textFontListLoaded)
            {
                _textFontItems = SystemFontList.FromSystem();
                TextFontBox.ItemsSource = _textFontItems.Select(x => x.Display).ToList();
                _textFontListLoaded = true;
            }
            int idx = _textFontItems.FindIndex(x => string.Equals(x.Family, family, StringComparison.OrdinalIgnoreCase));
            TextFontBox.SelectedIndex = idx >= 0 ? idx : 0;
        }
        finally { _suppressHatchFontRibbon = false; }
    }

    // ── 填充落地 ─────────────────────────────────────────────────────

    /// <summary>自检用：直设图案(不点下拉)并对选中边界填一次 —— 截图核对各图案的形状。</summary>
    private void SelftestHatch(string[] args)
    {
        if (args.Length == 0) { StatusMsg.Text = "自检填充：用法 @填充 <图案名> [比例] [角度]"; return; }
        var pat = HatchPatternLibrary.ByName(args[0]);
        if (pat == null) { StatusMsg.Text = $"自检填充：没有图案「{args[0]}」"; return; }
        _hatchPattern = pat;
        if (HatchPatternBox != null)
        {
            _suppressHatchFontRibbon = true;
            HatchPatternBox.SelectedIndex = Math.Max(0, Array.IndexOf(HatchPatternLibrary.All, pat));
            _suppressHatchFontRibbon = false;
        }
        double scale = args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sc) ? sc : 0;
        double ang = args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double an) ? an : 0;

        // 已经选中填充对象 → 就地改它(同在功能区换图案那条路), 而不是再铺一块
        var sel = SelectedHatches();
        if (sel.Count > 0)
        {
            BeginChange();
            foreach (var h in sel)
            {
                h.PatternName = pat.Name; h.Scale = scale; h.Angle = ang; h.Cross = _hatchCross;
                h.Invalidate();
            }
            RefreshScene(); HighlightSelection();
            StatusMsg.Text = $"编辑填充：{sel.Count} 个填充 → {sel[0].Describe()} · {sel[0].Lines().Count} 段";
            return;
        }

        var bnd = PickHatchBoundary(out double elev);
        if (bnd == null) { StatusMsg.Text = "自检填充：请先选中闭合边界(可用 @线示例 圈)"; return; }
        HatchFillBoundary(bnd, ang, scale, elev);
    }

    /// <summary>从选中实体里取一条闭合边界(闭合多段线/矩形/正多边形); 顺带带出它的标高, 填充跟着贴在同一高程上。</summary>
    private List<(double x, double y)>? PickHatchBoundary(out double elevation)
    {
        elevation = 0;
        foreach (var e in _selected)
        {
            if (e is PolylineEntity p && p.Closed && p.Points.Count >= 3)
            { elevation = p.Elevation; return new List<(double, double)>(p.Points); }
            if (e is RectEntity r)
            { elevation = r.Elevation; return new List<(double, double)> { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1) }; }
            if (e is PolygonEntity pg && pg.Sides >= 3)
            {
                elevation = pg.Elevation;
                var bnd = new List<(double, double)>();
                for (int i = 0; i < pg.Sides; i++)
                {
                    double a = pg.Rotation + 2 * Math.PI * i / pg.Sides;
                    bnd.Add((pg.Cx + pg.Radius * Math.Cos(a), pg.Cy + pg.Radius * Math.Sin(a)));
                }
                return bnd;
            }
        }
        return null;
    }

    /// <summary>边界包围盒对角线 —— 自动比例/自动间距都按它折算(约 24 条线)。</summary>
    private static double BoundaryDiagonal(IReadOnlyList<(double x, double y)> bnd)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var v in bnd) { if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x; if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y; }
        return Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
    }

    /// <summary>
    /// 按当前图案/颜色在边界上放**一个填充对象**(HatchEntity): 存边界+图案+比例+角度+颜色,
    /// 图案线由它自己算。于是整块可选中/移动/删除, 也可随时改图案参数(「编辑填充」或特性面板)。
    /// arg = 比例(命名图案: 缩放倍数) 或 间距(用户定义: 世界单位); ≤0 = 自动。
    /// </summary>
    private void HatchFillBoundary(List<(double x, double y)> bnd, double angleDeg, double arg, double elevation)
    {
        if (BoundaryDiagonal(bnd) < 1e-9) { StatusMsg.Text = "图案填充：边界过小"; return; }
        var hatch = new HatchEntity
        {
            Boundary = new List<(double, double)>(bnd),
            PatternName = _hatchPattern.Name,
            Scale = arg > 0 ? arg : 0,      // 0 = 自动(按边界大小折算)
            Angle = angleDeg,
            Cross = _hatchCross,
            Elevation = elevation,
        };
        hatch.AnchorOriginToBoundary();   // 图案锚在这块填充自己身上(移动/复制后不错位)
        int n = hatch.Lines().Count;
        if (n == 0 && !hatch.IsSolid)
        { StatusMsg.Text = $"图案填充：未生成线（{hatch.Describe()}）—— 比例给大了或边界过小"; return; }

        BeginChange();
        AssignLayer(hatch);
        if (_hatchColor is { } c) { hatch.Cr = c.r; hatch.Cg = c.g; hatch.Cb = c.b; }
        _scene.Add(hatch);
        SelectEntities(new SceneEntity[] { hatch });   // 建完即选中: 接着改图案/比例就作用在它身上(同原版)
        RefreshScene();
        string capped = n >= HatchPatternLibrary.MaxSegments ? "（已达线段上限, 请把比例调大）" : "";
        StatusMsg.Text = hatch.IsSolid
            ? $"图案填充：1 个填充对象 · {hatch.Describe()} · 颜色 {HatchColorLabel()}（可整体选中/移动/改参数）"
            : $"图案填充：1 个填充对象 · {hatch.Describe()} · {n} 段 · 颜色 {HatchColorLabel()}{capped}";
    }

    /// <summary>选中的填充对象(可能多个)。</summary>
    private List<HatchEntity> SelectedHatches() => _selected.OfType<HatchEntity>().ToList();

    /// <summary>
    /// 把功能区当前的 图案/比例/角度/颜色 写进选中的填充对象并就地重算 —— 同原版
    /// 「下一次填充用的图案(也用于修改选中的填充)」。返回改了几个。
    /// </summary>
    private int ApplyHatchToSelection(bool pattern = false, bool color = false, bool scale = false, bool angle = false)
    {
        var hs = SelectedHatches();
        if (hs.Count == 0) return 0;
        BeginChange();
        foreach (var h in hs)
        {
            if (pattern) { h.PatternName = _hatchPattern.Name; h.Cross = _hatchCross; }
            if (scale) h.Scale = ParseHatchArg();
            if (angle) h.Angle = ParseHatchAngle();
            if (color && _hatchColor is { } c) { h.Cr = c.r; h.Cg = c.g; h.Cb = c.b; }
            if (color && _hatchColor == null) { h.Cr = _layers.Current.Cr; h.Cg = _layers.Current.Cg; h.Cb = _layers.Current.Cb; }
            h.Invalidate();
        }
        RefreshScene();
        HighlightSelection();
        return hs.Count;
    }

    /// <summary>比例栏的值(空/非法 = 0 表示自动)。</summary>
    private double ParseHatchArg()
        => double.TryParse((HatchScaleBox?.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 0;

    /// <summary>角度栏的值(空/非法 = 0)。</summary>
    private double ParseHatchAngle()
        => double.TryParse((HatchAngleBox?.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>比例/角度栏改完(回车或失焦) → 写进选中的填充。</summary>
    private void OnHatchScaleCommitted(object? sender, RoutedEventArgs e) => CommitHatchScale();

    private void OnHatchAngleCommitted(object? sender, RoutedEventArgs e) => CommitHatchAngle();

    private void CommitHatchScale()
    {
        int n = ApplyHatchToSelection(scale: true);
        if (n > 0) StatusMsg.Text = $"编辑填充：{n} 个填充已按新比例重算（{SelectedHatches()[0].Describe()}）";
    }

    private void CommitHatchAngle()
    {
        int n = ApplyHatchToSelection(angle: true);
        if (n > 0) StatusMsg.Text = $"编辑填充：{n} 个填充已按新角度重算（{SelectedHatches()[0].Describe()}）";
    }

    private void OnHatchParamKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        if (ReferenceEquals(sender, HatchScaleBox)) CommitHatchScale(); else CommitHatchAngle();
        e.Handled = true;
    }

    // 「编辑填充」命令/双击填充 → 对话框(带预览), 见 MainWindow.HatchEdit.cs / HatchEditWindow.cs；
    // 功能区四项(图案/比例/角度/颜色)改一项即写进选中填充的即时路径(ApplyHatchToSelection)仍保留。

    /// <summary>选中一个填充时, 功能区四项回填成它的参数(同原版 SyncHatchRibbonFrom…); 只刷显示不回写。</summary>
    private void SyncHatchRibbonFromSelection()
    {
        if (HatchPatternBox == null) return;
        var hs = SelectedHatches();
        if (hs.Count != 1) return;
        var h = hs[0];
        _suppressHatchFontRibbon = true;
        try
        {
            var pat = HatchPatternLibrary.ByName(h.PatternName);
            if (pat != null)
            {
                _hatchPattern = pat;
                HatchPatternBox.SelectedIndex = Math.Max(0, Array.IndexOf(HatchPatternLibrary.All, pat));
            }
            _hatchCross = h.Cross;
            if (HatchScaleBox != null) HatchScaleBox.Text = h.Scale > 1e-12 ? h.Scale.ToString("0.###", CultureInfo.InvariantCulture) : "";
            if (HatchAngleBox != null) HatchAngleBox.Text = h.Angle.ToString("0.###", CultureInfo.InvariantCulture);
            _hatchColor = (h.Cr, h.Cg, h.Cb);
            SyncHatchColorSwatch();
        }
        finally { _suppressHatchFontRibbon = false; }
    }
}
