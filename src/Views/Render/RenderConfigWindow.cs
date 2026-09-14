using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Render;

/// <summary>
/// 渲染配置（忠实原 <c>PitMineApp.Render.Views.RenderConfigDialog</c>，非模态、改即生效、无「确定」）：
/// 着色模式 / 文字字体 / 等高线间距 / 属性分级区间 / 色带 —— 分「着色·材质·贴图·透明度」四页，页签同原版。
///
/// 与原版口径一致处（这是本窗体存在的意义，不是摆设）：
///   · <b>选中即逐对象</b>：视口里选中三角网后改档/改色带，只套到选中对象（原 PitMine_SetEntityFaceRender
///     的逐面覆盖）；未选中才落全局。线框/隐藏填充是视口填充方式，恒全局，同原版。
///   · <b>属性分级区间</b>：勾「自动」= 每张网按自身高程铺满色带；手填则用绝对高程。写死 0–100 会把
///     Z≈千米的地形整片钳到色带一端（原版注释点名的坑），所以「↻ 按当前选择重新填充」就摆在旁边。
///
/// 材质/贴图/透明度三页在本移植里是**说明页**：托管渲染管线是 P3_C3（位置+顶点色）单程序，
/// 没有纹理采样器、没有 alpha 通道、没有 PBR 光照，这三页的参数下发下去也无处生效 —— 与其做几个
/// 拖了没反应的滑杆，不如把原版有什么、这里为什么没有、能用什么替代讲清楚。
/// </summary>
internal sealed class RenderConfigWindow : Window
{
    private readonly Func<List<MeshEntity>> _selected;
    private readonly Func<List<MeshEntity>> _allMeshes;
    private readonly Action _refresh;
    private readonly Action<string> _echo;
    /// <summary>换视口贴图：给路径(null=清除)，返回 null 表示成功、非 null 是要显示的失败原因。</summary>
    private readonly Func<string?, string?> _setTexture;
    /// <summary>改实体前开一个撤销组（材质/透明度改的是实体特性，得能撤）。</summary>
    private readonly Action _beginChange;
    /// <summary>「选操作面」：回主窗走标准的「选择对象」流程，返回选中的三角网数（0 = 取消/没选到）。</summary>
    private readonly Func<System.Threading.Tasks.Task<int>> _pickMeshes;

    private readonly ComboBox _cmbShading = new() { Width = 250 };
    private readonly ComboBox _cmbFont = new() { Width = 250 };
    private readonly ComboBox _cmbColormap = new() { Width = 230 };
    private readonly CheckBox _chkReverse = new() { Content = "反转色带 (高↔低)" };
    private readonly CheckBox _chkAutoRange = new() { Content = "自动按各网自身高程 (推荐)", IsChecked = true };
    private readonly TextBox _txtMin = new() { Width = 110, Text = "0" };
    private readonly TextBox _txtMax = new() { Width = 110, Text = "100" };
    private readonly Slider _sliContour = new() { Minimum = 0.5, Maximum = 50, Value = 5, Width = 230 };
    private readonly TextBlock _lblContour = new() { Width = 46, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _fontStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _swatch = new() { Height = 14, Width = 230, CornerRadius = new CornerRadius(2) };

    // 材质页：全局 PBR / 逐实体覆盖 / 地学预设
    private readonly Slider _sliMetallic = new() { Minimum = 0, Maximum = 1, Value = 0, Width = 200 };
    private readonly Slider _sliRoughness = new() { Minimum = 0, Maximum = 1, Value = 0.5, Width = 200 };
    private readonly Slider _sliEntMetallic = new() { Minimum = 0, Maximum = 1, Value = 0, Width = 200 };
    private readonly Slider _sliEntRoughness = new() { Minimum = 0, Maximum = 1, Value = 0.5, Width = 200 };
    private readonly TextBlock _lblMetallic = new() { Width = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _lblRoughness = new() { Width = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _lblEntMetallic = new() { Width = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _lblEntRoughness = new() { Width = 40, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _matStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _geoStatus = new() { TextWrapping = TextWrapping.Wrap };

    // 贴图页
    private readonly Slider _sliTexScale = new() { Minimum = 1, Maximum = 500, Value = 50, Width = 190 };
    private readonly TextBlock _lblTexScale = new() { Width = 46, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _texStatus = new() { Text = "未加载贴图", TextWrapping = TextWrapping.Wrap };
    private bool _hasTexture;
    private readonly TextBlock _builtinTexStatus = new() { TextWrapping = TextWrapping.Wrap };

    // 透明度页
    private readonly Slider _sliTransparency = new() { Minimum = 0, Maximum = 90, Value = 40, Width = 190, TickFrequency = 10 };
    private readonly TextBlock _lblTransparency = new() { Width = 46, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _transStatus = new() { TextWrapping = TextWrapping.Wrap };

    private readonly TabControl _tabs = new() { Margin = new Thickness(8, 6, 8, 0) };
    private readonly TextBlock _footText = new();

    /// <summary>切页签（自检截图用：脚本点不了控件，只好直接选）。0=着色 1=材质 2=贴图 3=透明度。</summary>
    public void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Items.Count) return;
        _tabs.SelectedIndex = index;
        // 窗口刚 Show 出来时模板还没套完, 这里设的选择会被之后的默认选择顶掉 —— 排到布局完成后再设一次。
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { if (index < _tabs.Items.Count) _tabs.SelectedIndex = index; },
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>构造期填控件会触发各 handler，这一位挡住它们；末尾 LoadFromScene 才放开（同原版 _loading）。</summary>
    private bool _loading = true;

    /// <summary>着色档 = 下拉项的 Tag。编号照原 Lit.hlsl 的 ShadingMode，好与原版逐项对照。</summary>
    private enum Item
    {
        Flat = 0, Smooth = 1, Wireframe = 2, Hidden = 3, Textured = 4, Pbr = 6,
        Contour = 10, Slope = 11, Aspect = 12, Attribute = 14,
        /// <summary>原版下拉没有这一档：属性分级套死地形色带的老档(Kylin 的「高程着色面」命令用的就是它)，
        /// 列出来才不会"开窗时正处于高程档、下拉却显示别的"。编号避开 shader 已占用的值(13=Section)。</summary>
        Elevation = 900,
    }

    public RenderConfigWindow(Func<List<MeshEntity>> selected, Func<List<MeshEntity>> allMeshes,
                              Action refresh, Action<string> echo,
                              Func<string?, string?> setTexture, Action beginChange,
                              Func<System.Threading.Tasks.Task<int>> pickMeshes)
    {
        _selected = selected; _allMeshes = allMeshes; _refresh = refresh; _echo = echo;
        _setTexture = setTexture; _beginChange = beginChange; _pickMeshes = pickMeshes;

        Title = "渲染配置";
        Width = 530; Height = 760;   // 一屏放得下「着色模式→等高线→属性区间→色带」四组, 不必先滚再改
        MinWidth = 470; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        var tabs = _tabs;
        tabs.Items.Add(new TabItem { Header = "着色", Content = BuildShadingTab() });
        tabs.Items.Add(new TabItem { Header = "材质", Content = BuildMaterialTab() });
        tabs.Items.Add(new TabItem { Header = "贴图", Content = BuildTextureTab() });
        tabs.Items.Add(new TabItem { Header = "透明度", Content = BuildTransparencyTab() });

        var close = new Button { Content = "关闭", MinWidth = 72, IsCancel = true };
        close.Click += (_, _) => Close();
        var pick = new Button { Content = "选操作面", MinWidth = 88, Classes = { "primary" } };
        pick.Click += async (_, _) => await PickTargetsAsync();
        ToolTip.SetTip(pick, "回视口挑要套效果的三角网：单击/框选加减选，右键或回车确定（Esc 取消）");
        var applyAll = new Button { Content = "应用效果", MinWidth = 88 };
        applyAll.Click += (_, _) => ApplyCurrentPage();
        ToolTip.SetTip(applyAll, "把当前页的设置套到选中的面（着色档 / 材质 / 贴图 / 透明度，按当前页走）");

        _footText.Classes.Add("muted"); _footText.Classes.Add("small");
        _footText.VerticalAlignment = VerticalAlignment.Center;
        _footText.Text = "所有改动实时生效（无需确定）";
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(14, 8) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btns.Children.Add(pick); btns.Children.Add(applyAll); btns.Children.Add(close);
        Grid.SetColumn(_footText, 0); Grid.SetColumn(btns, 3);
        foot.Children.Add(_footText); foot.Children.Add(btns);

        var root = new DockPanel();
        DockPanel.SetDock(foot, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(tabs);
        Content = root;

        LoadFromScene();
    }

    // ── 着色页 ───────────────────────────────────────────────────────────
    private Control BuildShadingTab()
    {
        foreach (var (name, item) in new (string, Item)[]
        {
            ("平面着色 (Flat)", Item.Flat), ("平滑着色 (Smooth)", Item.Smooth),
            ("线框 (Wireframe)", Item.Wireframe), ("隐藏填充 (Hidden)", Item.Hidden),
            ("贴图 (Textured)", Item.Textured),
            ("等高线 (Contour)", Item.Contour), ("坡度 (Slope)", Item.Slope), ("坡向 (Aspect)", Item.Aspect),
            ("高程色带 (地形)", Item.Elevation), ("属性分级 (Attribute)", Item.Attribute),
            ("PBR 物理材质", Item.Pbr),
        })
        {
            var box = new ComboBoxItem { Content = name, Tag = item };
            if (item == Item.Textured) ToolTip.SetTip(box, "三平面投影贴图；贴图文件在「贴图」页选");
            if (item == Item.Pbr) ToolTip.SetTip(box, "Cook-Torrance 物理材质；金属度/粗糙度在「材质」页调");
            _cmbShading.Items.Add(box);
        }
        _cmbShading.SelectionChanged += (_, _) => { if (!_loading) ApplyShading(); };

        var clearBtn = new Button { Content = "清除选中对象的独立着色（退回全局）", HorizontalAlignment = HorizontalAlignment.Left };
        clearBtn.Click += (_, _) => ClearObjectShading();

        var shadingPanel = Group("着色模式", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row("模式", _cmbShading),
                Note("先在视口选中三角网 → 改档/改色带即只套到该对象；未选中则全局。" +
                     "线框 / 隐藏填充 是视口填充方式，恒按全局生效（同原版）。"),
                clearBtn,
                _status.Also(t => { t.Classes.Add("muted"); t.Classes.Add("small"); }),
            },
        });

        _sliContour.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _lblContour.Text = _sliContour.Value.ToString("0.#", CultureInfo.InvariantCulture) + " m";
            if (_loading) return;
            MeshEntity.ContourSpacing = _sliContour.Value;
            Done($"等高距 {_sliContour.Value:0.#} m");
        };
        var contourPanel = Group("等高线（Contour 档）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row("等高距", new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { _sliContour, _lblContour },
                }),
                Note("面照常打光，另在 Z = 整数倍等高距处叠一层深色等值线（原版逐像素画，这里出真几何，同一条线）。"),
            },
        });

        _chkAutoRange.IsCheckedChanged += (_, _) =>
        {
            bool manual = _chkAutoRange.IsChecked != true;
            _txtMin.IsEnabled = _txtMax.IsEnabled = manual;
            if (_loading) return;
            MeshEntity.AttrAutoRange = !manual;
            ReapplyAttribute();
        };
        _txtMin.IsEnabled = _txtMax.IsEnabled = false;
        _txtMin.LostFocus += (_, _) => ApplyValueRange();
        _txtMax.LostFocus += (_, _) => ApplyValueRange();
        _txtMin.KeyDown += OnRangeKey;
        _txtMax.KeyDown += OnRangeKey;
        var fillBtn = new Button { Content = "↻ 按当前选择重新填充", HorizontalAlignment = HorizontalAlignment.Left };
        fillBtn.Click += (_, _) => { if (AutoFillValueRange()) ReapplyAttribute(); };

        var rangePanel = Group("属性分级色带区间（Attribute 档）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _chkAutoRange,
                Row("色带最小值", _txtMin),
                Row("色带最大值", _txtMax),
                fillBtn,
                Note("按 Z 高程映射到下方色带：最小值→色带左端，最大值→右端。"),
            },
        });

        foreach (var (name, stops) in Colormap.Presets)
            _cmbColormap.Items.Add(new ComboBoxItem { Content = name, Tag = stops });
        _cmbColormap.SelectionChanged += (_, _) => { UpdateSwatch(); if (!_loading) ApplyColormap(); };
        _chkReverse.IsCheckedChanged += (_, _) => { UpdateSwatch(); if (!_loading) ApplyColormap(); };

        var colormapPanel = Group("色带 Colormap（属性分级用，选中即应用）", new StackPanel
        {
            Spacing = 6,
            Children = { Row("色带", _cmbColormap), Row("", _swatch), _chkReverse },
        });

        // 列本机**已装**的字体(常用中文置顶显示中文名), 与 开始→注释→字体 用同一份表 ——
        // 写死几项的话, 麒麟上装的思源/文泉驿一个都选不到。
        foreach (var (display, family) in PitMine3D.Kylin.Views.SystemFontList.FromSystem())
            _cmbFont.Items.Add(new ComboBoxItem
            {
                Content = family.Length == 0 ? "自动（按系统可用中文字体）" : display,
                Tag = family,
            });
        _cmbFont.SelectionChanged += (_, _) => { if (!_loading) ApplyFont(); };

        var fontPanel = Group("文字字体", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Row("字体", _cmbFont),
                _fontStatus.Also(t => { t.Classes.Add("muted"); t.Classes.Add("small"); }),
                Note("原版另有「按文字样式改字体」（AutoCAD STYLE 逐样式指定）；本移植的文字样式只存宽度系数/倾斜角，" +
                     "不带字体名，按样式换字体暂无处落地。"),
            },
        });

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12), Spacing = 10,
                Children = { shadingPanel, contourPanel, rangePanel, colormapPanel, fontPanel },
            },
        };
    }

    // ── 材质页（原版「材质」：全局 PBR + 逐实体覆盖 + 地学材质预设）────────
    private Control BuildMaterialTab()
    {
        var globalPanel = Group("PBR 物理材质（着色模式 = PBR 时生效）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                SliderRow("金属度", _sliMetallic, _lblMetallic, v =>
                {
                    MeshEntity.PbrMetallic = v;
                    Done($"全局金属度 {v:0.00}");
                }),
                SliderRow("粗糙度", _sliRoughness, _lblRoughness, v =>
                {
                    MeshEntity.PbrRoughness = v;
                    Done($"全局粗糙度 {v:0.00}");
                }),
                Note("Cook-Torrance 全局材质（逐式照抄原版 shader）：金属度↑ 越像金属，粗糙度↑ 高光越散。" +
                     "没有环境贴图，环境反射用原版那套 Z-up 天空→地平→地面渐变近似。"),
            },
        });

        var applyEnt = new Button { Content = "应用到选中实体", MinWidth = 120 };
        applyEnt.Click += (_, _) => ApplyEntityMaterial();
        var clearEnt = new Button { Content = "清除(恢复全局)", MinWidth = 110 };
        clearEnt.Click += (_, _) => ClearEntityMaterial();
        var entPanel = Group("逐实体材质覆盖（选中后应用；PBR 档生效）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                SliderRow("金属度", _sliEntMetallic, _lblEntMetallic, null),
                SliderRow("粗糙度", _sliEntRoughness, _lblEntRoughness, null),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(88, 0, 0, 0),
                    Children = { applyEnt, clearEnt },
                },
                _matStatus.Also(t => { t.Classes.Add("muted"); t.Classes.Add("small"); }),
                Note("给选中三角网单独的金属度/粗糙度（漫反射色 = 实体颜色，不透明度 = 透明度页）。" +
                     "同原版：材质参数只作用于显示，不随工程存档。"),
            },
        });

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var m in GeoMaterials)
        {
            var btn = new Button
            {
                Margin = new Thickness(2), Padding = new Thickness(4, 2), MinWidth = 150,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 5,
                    Children =
                    {
                        new Border
                        {
                            Width = 16, Height = 16, CornerRadius = new CornerRadius(2),
                            Background = new SolidColorBrush(Color.FromRgb(m.R, m.G, m.B)),
                            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock { Text = m.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            ToolTip.SetTip(btn, $"{m.Name}：金属度 {m.Metallic:0.00} · 粗糙度 {m.Roughness:0.00}" +
                                (m.Transparency > 0 ? $" · 透明度 {m.Transparency * 100:0}%" : ""));
            var preset = m;
            btn.Click += (_, _) => ApplyGeoMaterial(preset);
            wrap.Children.Add(btn);
        }
        var geoPanel = Group("地学材质预设（点击应用到选中，自动切 PBR）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new ScrollViewer { MaxHeight = 220, Content = wrap },
                _geoStatus.Also(t => { t.Classes.Add("muted"); t.Classes.Add("small"); }),
                Note("为选中三角网套典型地质材质（颜色 + 金属度 + 粗糙度 + 透明度），并自动切到 PBR 档。" +
                     "金属矿物呈金属反光，岩石呈漫反射，水/冰半透。"),
            },
        });

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12), Spacing = 10,
                Children = { globalPanel, entPanel, geoPanel },
            },
        };
    }

    // ── 贴图页（原版「贴图」：三平面投影 + 平铺尺度 + 内置地学贴图）────────
    private Control BuildTextureTab()
    {
        var pick = new Button { Content = "选择贴图...", MinWidth = 110 };
        pick.Click += async (_, _) => await PickTextureAsync();
        var clear = new Button { Content = "清除贴图", MinWidth = 90 };
        clear.Click += (_, _) => SetTexture(null, "已清除贴图（贴图档退回白底光照）");

        var filePanel = Group("贴图（着色模式 = 贴图 时生效）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(88, 0, 0, 0),
                    Children = { pick, clear },
                },
                SliderRow("平铺尺度", _sliTexScale, _lblTexScale, v =>
                {
                    MeshEntity.TexScale = v;
                    Done($"贴图平铺尺度 {v:0} m");
                }),
                _texStatus.Also(t => { t.Classes.Add("muted"); t.Classes.Add("small"); }),
                Note("三平面投影贴图（世界空间，不需要 UV），适合地形/矿体。平铺尺度 = 每张贴图覆盖多少米。" +
                     "实体颜色作 tint 叠加 —— 与原版同一条口径。"),
            },
        });

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        var builtin = TextureLoader.BuiltinAvailable();
        foreach (var (path, label) in builtin)
        {
            Control thumb;
            try
            {
                thumb = new Image
                {
                    Source = new Bitmap(path), Width = 64, Height = 64,
                    Stretch = Avalonia.Media.Stretch.UniformToFill,
                };
            }
            catch { thumb = new Border { Width = 64, Height = 64, Background = Brushes.Gray }; }
            var btn = new Button
            {
                Margin = new Thickness(3), Padding = new Thickness(3),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children = { thumb, new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center } },
                },
            };
            string p2 = path, l2 = label;
            btn.Click += (_, _) => SetTexture(p2, $"已套用内置贴图「{l2}」");
            wrap.Children.Add(btn);
        }
        var builtinPanel = Group("内置地学贴图（点击缩略图直接应用）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                builtin.Count > 0
                    ? new ScrollViewer { MaxHeight = 260, Content = wrap }
                    : (Control)Note("没找到内置贴图目录（Assets/Textures/Geo）—— 用上面的「选择贴图...」指一张图片也一样。"),
                _builtinTexStatus.Also(t => { t.Classes.Add("muted"); t.Classes.Add("small"); }),
            },
        });

        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(12), Spacing = 10,
                Children = { filePanel, builtinPanel },
            },
        };
    }

    // ── 透明度页（原版「透明度」：批量套到选中面模型）──────────────────────
    private Control BuildTransparencyTab()
    {
        var apply = new Button { Content = "应用到选中实体", MinWidth = 120 };
        apply.Click += (_, _) => ApplyTransparency((short)Math.Round(_sliTransparency.Value));
        var reset = new Button { Content = "恢复不透明", MinWidth = 100 };
        reset.Click += (_, _) => ApplyTransparency(0);

        var panel = Group("面模型透明度（批量）", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                SliderRow("透明度", _sliTransparency, _lblTransparency, null),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(88, 0, 0, 0),
                    Children = { apply, reset },
                },
                _transStatus.Also(t => { t.Classes.Add("muted"); t.Classes.Add("small"); }),
                Note("先在视口选中三角网，拖滑杆后点「应用到选中实体」（0 = 不透明）。改的就是实体的「透明度」特性 ——" +
                     "特性面板能看到、随工程存档、导出保值，这里只是把它接到了渲染上。"),
            },
        });

        return new ScrollViewer
        {
            Content = new StackPanel { Margin = new Thickness(12), Spacing = 10, Children = { panel } },
        };
    }

    // ── 材质/贴图/透明度 的下发 ───────────────────────────────────────────

    /// <summary>地学材质预设 —— 数值与顺序照原 RenderConfigDialog 的 s_geoMaterials，一个不改。</summary>
    private readonly record struct GeoMat(string Name, byte R, byte G, byte B, double Metallic, double Roughness, double Transparency);

    private static readonly GeoMat[] GeoMaterials =
    {
        // ── 岩石 (电介质, metallic≈0, 漫反射为主) ──
        new("花岗岩 Granite", 190, 180, 170, 0.00, 0.75, 0),
        new("砂岩 Sandstone", 205, 170, 120, 0.00, 0.90, 0),
        new("石灰岩 Limestone", 225, 222, 210, 0.00, 0.80, 0),
        new("大理岩 Marble", 235, 235, 230, 0.00, 0.30, 0),
        new("玄武岩 Basalt", 60, 60, 65, 0.00, 0.85, 0),
        new("页岩 Shale", 92, 98, 92, 0.00, 0.70, 0),
        new("煤 Coal", 25, 25, 28, 0.00, 0.40, 0),
        // ── 金属矿物 (metallic 高, 反光) ──
        new("黄铁矿 Pyrite", 205, 175, 95, 0.95, 0.35, 0),
        new("方铅矿 Galena", 150, 150, 160, 0.90, 0.22, 0),
        new("黄铜矿 Chalcopyrite", 195, 160, 70, 0.90, 0.40, 0),
        new("磁铁矿 Magnetite", 45, 45, 55, 0.85, 0.50, 0),
        new("赤铁矿 Hematite", 125, 62, 55, 0.60, 0.45, 0),
        new("自然金 Native Gold", 255, 200, 80, 1.00, 0.20, 0),
        // ── 地表覆盖物 ──
        new("水体 Water", 60, 120, 180, 0.00, 0.05, 0.45),
        new("冰雪 Ice/Snow", 225, 240, 250, 0.00, 0.15, 0.18),
        new("土壤 Soil", 110, 80, 55, 0.00, 0.95, 0),
        new("植被 Vegetation", 80, 120, 60, 0.00, 0.85, 0),
    };

    private void ApplyEntityMaterial()
    {
        var sel = _selected();
        if (sel.Count == 0) { _matStatus.Text = "未选中三角网"; return; }
        _beginChange();
        foreach (var m in sel) { m.Metallic = _sliEntMetallic.Value; m.Roughness = _sliEntRoughness.Value; }
        _matStatus.Text = $"已把 金属度 {_sliEntMetallic.Value:0.00} · 粗糙度 {_sliEntRoughness.Value:0.00} 套到选中 {sel.Count} 张网";
        Done(_matStatus.Text);
    }

    private void ClearEntityMaterial()
    {
        var sel = _selected();
        if (sel.Count == 0) { _matStatus.Text = "未选中三角网"; return; }
        _beginChange();
        foreach (var m in sel) { m.Metallic = null; m.Roughness = null; }
        _matStatus.Text = $"已清除 {sel.Count} 张网的独立材质（退回全局）";
        Done(_matStatus.Text);
    }

    private void ApplyGeoMaterial(GeoMat g)
    {
        var sel = _selected();
        if (sel.Count == 0) { _geoStatus.Text = "未选中三角网 —— 先在视口选中要套材质的面模型"; return; }
        _beginChange();
        foreach (var m in sel)
        {
            m.Cr = g.R / 255f; m.Cg = g.G / 255f; m.Cb = g.B / 255f;
            m.Metallic = g.Metallic; m.Roughness = g.Roughness;
            m.Transparency = (short)Math.Round(g.Transparency * 100);
            m.FaceRender = new MeshEntity.FaceRenderOverride(
                MeshEntity.FaceShade.Pbr, _chkAutoRange.IsChecked == true,
                ParseOr(_txtMin, MeshEntity.AttrMin), ParseOr(_txtMax, MeshEntity.AttrMax),
                CurrentStops(), _chkReverse.IsChecked == true);
            m.Invalidate();   // 基色变了, 镶嵌缓存里的颜色得重算
        }
        if (MeshEntity.RenderMode == MeshEntity.DisplayMode.Wireframe) MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
        _geoStatus.Text = $"已把「{g.Name}」套到选中 {sel.Count} 张网（已自动切 PBR 档）";
        Done(_geoStatus.Text);
    }

    private async System.Threading.Tasks.Task PickTextureAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择贴图",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("图片") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } },
            },
        });
        if (files.Count == 0) return;
        string? path = files[0].Path.LocalPath;
        if (string.IsNullOrEmpty(path)) { _texStatus.Text = "这个位置的文件读不到本地路径"; return; }
        SetTexture(path, $"已加载贴图：{System.IO.Path.GetFileName(path)}");
    }

    /// <summary>换贴图（null = 清除）。切到贴图档才看得见，故顺带把档也切过去（同原版点缩略图即应用）。</summary>
    private void SetTexture(string? path, string okMsg)
    {
        string? err = _setTexture(path);
        if (err != null) { _texStatus.Text = err; return; }
        _hasTexture = path != null;
        _texStatus.Text = path == null ? "未加载贴图" : okMsg;
        if (path != null)
        {
            var sel = _selected();
            if (sel.Count > 0)
                foreach (var m in sel) m.FaceRender = MakeOverride(MeshEntity.FaceShade.Textured);
            else MeshEntity.ShadeMode = MeshEntity.FaceShade.Textured;
            if (MeshEntity.RenderMode == MeshEntity.DisplayMode.Wireframe) MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            SyncShadingCombo();
        }
        Done(okMsg);
    }

    private void ApplyTransparency(short t)
    {
        var sel = _selected();
        if (sel.Count == 0) { _transStatus.Text = "未选中三角网 —— 先在视口选中面模型"; return; }
        _beginChange();
        foreach (var m in sel) m.Transparency = t;
        _transStatus.Text = t <= 0
            ? $"已把选中 {sel.Count} 张网恢复不透明"
            : $"已把 透明度 {t}% 套到选中 {sel.Count} 张网";
        Done(_transStatus.Text);
    }

    /// <summary>标签 + 滑杆 + 数值，值一变就调 onChanged（构造期 _loading 挡着不下发）。</summary>
    private Control SliderRow(string label, Slider slider, TextBlock value, Action<double>? onChanged)
    {
        string fmt = slider.Maximum > 10 ? "0" : "0.00";
        value.Text = slider.Value.ToString(fmt, CultureInfo.InvariantCulture);
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            value.Text = slider.Value.ToString(fmt, CultureInfo.InvariantCulture);
            if (_loading || onChanged == null) return;
            onChanged(slider.Value);
        };
        return Row(label, new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { slider, value },
        });
    }

    // ── 下发 ─────────────────────────────────────────────────────────────
    private Item CurrentItem() => _cmbShading.SelectedItem is ComboBoxItem { Tag: Item it } ? it : Item.Flat;

    /// <summary>可走逐对象覆盖的档（同原版 IsPerFaceMode）：线框/隐藏填充是视口填充方式，只能全局。</summary>
    private static bool IsPerObject(Item it) => it is not (Item.Wireframe or Item.Hidden or Item.Textured or Item.Pbr);

    private static string ItemName(Item it) => it switch
    {
        Item.Flat => "平面着色", Item.Smooth => "平滑着色", Item.Wireframe => "线框", Item.Hidden => "隐藏填充",
        Item.Contour => "等高线", Item.Slope => "坡度", Item.Aspect => "坡向",
        Item.Elevation => "高程色带", Item.Attribute => "属性分级",
        Item.Textured => "贴图", Item.Pbr => "PBR 物理材质", _ => it.ToString(),
    };

    private static MeshEntity.FaceShade ShadeOf(Item it) => it switch
    {
        Item.Contour => MeshEntity.FaceShade.Contour,
        Item.Slope => MeshEntity.FaceShade.Slope,
        Item.Aspect => MeshEntity.FaceShade.Aspect,
        Item.Elevation => MeshEntity.FaceShade.Elevation,
        Item.Attribute => MeshEntity.FaceShade.Attribute,
        Item.Textured => MeshEntity.FaceShade.Textured,
        Item.Pbr => MeshEntity.FaceShade.Pbr,
        _ => MeshEntity.FaceShade.Entity,
    };

    private void ApplyShading()
    {
        var it = CurrentItem();
        var sel = _selected();

        // 平面/平滑之别在本移植是全局的打光开关（带真法线的曲面才认顶点法线），不随对象走。
        if (it is Item.Flat or Item.Smooth) MeshEntity.SmoothShading = it == Item.Smooth;

        if (it is Item.Wireframe or Item.Hidden)
        {
            MeshEntity.RenderMode = it == Item.Wireframe
                ? MeshEntity.DisplayMode.Wireframe
                : MeshEntity.DisplayMode.ShadedWireframe;
            Done($"「{ItemName(it)}」已全局应用（视口填充方式，不按对象）");
            return;
        }

        // 选了着色档就得看得见面：还停在纯线框会「设了没反应」。面+线框保持不动（那是用户自己叠的边线）。
        if (MeshEntity.RenderMode == MeshEntity.DisplayMode.Wireframe) MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;

        if (sel.Count > 0 && IsPerObject(it))
        {
            foreach (var m in sel) m.FaceRender = MakeOverride(ShadeOf(it));
            Done($"已把「{ItemName(it)}」套到选中 {sel.Count} 张三角网（其余仍按全局）");
            return;
        }
        MeshEntity.ShadeMode = ShadeOf(it);
        if (it == Item.Attribute && MeshEntity.AttrAutoRange) AutoFillValueRange();
        Done(sel.Count == 0
            ? $"未选中 —— 「{ItemName(it)}」已全局应用（选中对象后改档即只套到该对象）"
            : $"「{ItemName(it)}」按全局应用");
    }

    private MeshEntity.FaceRenderOverride MakeOverride(MeshEntity.FaceShade shade) => new(
        shade, _chkAutoRange.IsChecked == true,
        ParseOr(_txtMin, MeshEntity.AttrMin), ParseOr(_txtMax, MeshEntity.AttrMax),
        CurrentStops(), _chkReverse.IsChecked == true);

    private void ClearObjectShading()
    {
        var sel = _selected();
        if (sel.Count == 0) { Done("未选中三角网"); return; }
        int n = 0;
        foreach (var m in sel) if (m.FaceRender != null) { m.FaceRender = null; n++; }
        Done(n > 0 ? $"已清除 {n} 张三角网的独立着色（退回全局）" : "选中项本来就跟随全局");
    }

    /// <summary>色带/区间改了就重铺：有独立着色的选中对象逐个重下发，否则落全局（同原版 ReapplyAttribute）。</summary>
    private void ReapplyAttribute()
    {
        var sel = _selected();
        int n = 0;
        foreach (var m in sel.Where(m => m.FaceRender != null)) { m.FaceRender = MakeOverride(m.FaceRender!.Shade); n++; }
        if (n > 0) { Done($"已按当前色带/区间重铺 {n} 张三角网"); return; }
        Done(CurrentItem() == Item.Attribute ? "已按当前色带/区间重铺（全局）" : "已记下；切到「属性分级」档即生效");
    }

    private void ApplyColormap()
    {
        MeshEntity.AttrColormap = CurrentStops();
        MeshEntity.AttrReverse = _chkReverse.IsChecked == true;
        ReapplyAttribute();
    }

    private void OnRangeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyValueRange();
    }

    private void ApplyValueRange()
    {
        if (_loading || _chkAutoRange.IsChecked == true) return;
        double mn = ParseOr(_txtMin, MeshEntity.AttrMin), mx = ParseOr(_txtMax, MeshEntity.AttrMax);
        if (mx <= mn) { _status.Text = "色带最大值要大于最小值"; return; }
        MeshEntity.AttrMin = mn; MeshEntity.AttrMax = mx;
        ReapplyAttribute();
    }

    /// <summary>
    /// 按「选中（未选则全部）三角网」的世界高程填区间 —— 原版这条是为治
    /// 「写死 0–100 把 Z≈千米的地形整片钳到色带一端」。
    /// </summary>
    private bool AutoFillValueRange()
    {
        var sel = _selected();
        bool usedSel = sel.Count > 0;
        var meshes = usedSel ? sel : _allMeshes();
        if (meshes.Count == 0) { _status.Text = "场景里没有三角网可取高程"; return false; }
        double zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
        foreach (var m in meshes)
        {
            if (m.VertexCount == 0) continue;
            var b = m.Bounds;
            zmin = Math.Min(zmin, b.minZ + m.Elevation);
            zmax = Math.Max(zmax, b.maxZ + m.Elevation);
        }
        if (!double.IsFinite(zmin) || !double.IsFinite(zmax) || zmax < zmin)
        { _status.Text = "未能取得三角网高程范围"; return false; }
        if (zmax - zmin < 1e-4) zmax = zmin + 1.0;   // 同高退化区间兜底（下游按 max(range,1e-3) 再兜一次）

        bool keep = _loading; _loading = true;       // 只改文本，不回环触发下发
        _txtMin.Text = zmin.ToString("0.###", CultureInfo.InvariantCulture);
        _txtMax.Text = zmax.ToString("0.###", CultureInfo.InvariantCulture);
        _loading = keep;
        MeshEntity.AttrMin = zmin; MeshEntity.AttrMax = zmax;
        _status.Text = usedSel
            ? $"已按选中 {meshes.Count} 张网高程 [{zmin:0.#}, {zmax:0.#}] m"
            : $"未选中 —— 已按全部 {meshes.Count} 张网高程 [{zmin:0.#}, {zmax:0.#}] m";
        return true;
    }

    private void ApplyFont()
    {
        string fam = _cmbFont.SelectedItem is ComboBoxItem { Tag: string s } ? s : "";
        bool ok = GlyphFontHost.TryUseFamily(fam);
        _fontStatus.Text = ok
            ? "当前字体：" + (GlyphFontHost.ActiveFamily ?? "(默认)")
            : $"系统未装该字体，仍用：{GlyphFontHost.ActiveFamily ?? "(默认)"}";
        _refresh();
    }

    // ── 杂项 ─────────────────────────────────────────────────────────────
    private (byte r, byte g, byte b)[] CurrentStops()
        => _cmbColormap.SelectedItem is ComboBoxItem { Tag: (byte r, byte g, byte b)[] st } ? st : Colormap.Viridis;

    private void UpdateSwatch()
    {
        var stops = CurrentStops();
        bool rev = _chkReverse.IsChecked == true;
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        for (int i = 0; i < stops.Length; i++)
        {
            var c = stops[rev ? stops.Length - 1 - i : i];
            brush.GradientStops.Add(new GradientStop(
                Color.FromRgb(c.r, c.g, c.b), stops.Length == 1 ? 0 : i / (double)(stops.Length - 1)));
        }
        _swatch.Background = brush;
    }

    /// <summary>一次改动收尾：写状态行 → 推进着色版本号（清镶嵌缓存）→ 重绘 → 回显到信息栏。</summary>
    private void Done(string msg)
    {
        _status.Text = msg;
        MeshEntity.BumpShade();
        _refresh();
        _echo("渲染配置：" + msg);
    }

    private static double ParseOr(TextBox box, double fallback)
        => double.TryParse((box.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : fallback;

    /// <summary>
    /// 「选操作面」：回视口走标准的「选择对象」流程（单击/框选加减选，右键确定）。
    /// 窗体是非模态的，选完主窗还在前台，这里把本窗叫回来并把选中数写进底栏 —— 不然用户不知道选着几张。
    /// </summary>
    private async System.Threading.Tasks.Task PickTargetsAsync()
    {
        _footText.Text = "请在视口里选面：单击/框选加减选 · 右键或回车确定 · Esc 取消";
        int n = await _pickMeshes();
        try { Activate(); } catch { }
        UpdateFootCount(n);
    }

    private void UpdateFootCount(int n)
        => _footText.Text = n > 0
            ? $"已选 {n} 张面 —— 点「应用效果」把当前页的设置套上去（改动实时生效）"
            : "未选中面 —— 「应用效果」将按全局生效（着色档/等高距/色带 等全局项照常）";

    /// <summary>「应用效果」：按当前页把设置套到选中的面（页与动作一一对应，不搞"一键全套"）。</summary>
    private void ApplyCurrentPage()
    {
        switch (_tabs.SelectedIndex)
        {
            case 1: ApplyEntityMaterial(); break;
            case 2: ApplyTextureShade(); break;
            case 3: ApplyTransparency((short)Math.Round(_sliTransparency.Value)); break;
            default: ApplyShading(); break;
        }
        UpdateFootCount(_selected().Count);
    }

    /// <summary>贴图页的「应用效果」：把贴图档套到选中面（未选中则全局）；还没加载贴图就先说清楚。</summary>
    private void ApplyTextureShade()
    {
        if (!_hasTexture) { _texStatus.Text = "还没加载贴图 —— 先点「选择贴图...」或下面的内置贴图"; return; }
        var sel = _selected();
        if (sel.Count > 0)
        {
            foreach (var m in sel) m.FaceRender = MakeOverride(MeshEntity.FaceShade.Textured);
            _texStatus.Text = $"已把贴图档套到选中 {sel.Count} 张面";
        }
        else
        {
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Textured;
            _texStatus.Text = "未选中 —— 贴图档已全局应用";
        }
        if (MeshEntity.RenderMode == MeshEntity.DisplayMode.Wireframe) MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
        SyncShadingCombo();
        Done(_texStatus.Text);
    }

    /// <summary>把下拉拨到当前生效的档（贴图/材质页改了档时用；不回环触发下发）。</summary>
    private void SyncShadingCombo()
    {
        bool keep = _loading; _loading = true;
        var it = MeshEntity.ShadeMode switch
        {
            MeshEntity.FaceShade.Textured => Item.Textured,
            MeshEntity.FaceShade.Pbr => Item.Pbr,
            MeshEntity.FaceShade.Contour => Item.Contour,
            MeshEntity.FaceShade.Slope => Item.Slope,
            MeshEntity.FaceShade.Aspect => Item.Aspect,
            MeshEntity.FaceShade.Elevation => Item.Elevation,
            MeshEntity.FaceShade.Attribute => Item.Attribute,
            _ => MeshEntity.SmoothShading ? Item.Smooth : Item.Flat,
        };
        var items = _cmbShading.Items.OfType<ComboBoxItem>().ToList();
        _cmbShading.SelectedItem = items.FirstOrDefault(x => x.Tag is Item t && t == it) ?? items[0];
        _loading = keep;
    }

    /// <summary>开窗时把当前生效的设置读回控件（同原版 LoadFromEngine）。</summary>
    private void LoadFromScene()
    {
        _loading = true;
        var it = MeshEntity.RenderMode switch
        {
            MeshEntity.DisplayMode.Wireframe => Item.Wireframe,
            MeshEntity.DisplayMode.ShadedWireframe when MeshEntity.ShadeMode == MeshEntity.FaceShade.Entity => Item.Hidden,
            _ => MeshEntity.ShadeMode switch
            {
                MeshEntity.FaceShade.Contour => Item.Contour,
                MeshEntity.FaceShade.Slope => Item.Slope,
                MeshEntity.FaceShade.Aspect => Item.Aspect,
                MeshEntity.FaceShade.Elevation => Item.Elevation,
                MeshEntity.FaceShade.Attribute => Item.Attribute,
                MeshEntity.FaceShade.Textured => Item.Textured,
                MeshEntity.FaceShade.Pbr => Item.Pbr,
                _ => MeshEntity.SmoothShading ? Item.Smooth : Item.Flat,
            },
        };
        var items = _cmbShading.Items.OfType<ComboBoxItem>().ToList();
        _cmbShading.SelectedItem = items.FirstOrDefault(x => x.Tag is Item t && t == it) ?? items[0];

        _sliMetallic.Value = Math.Clamp(MeshEntity.PbrMetallic, 0, 1);
        _sliRoughness.Value = Math.Clamp(MeshEntity.PbrRoughness, 0, 1);
        _sliTexScale.Value = Math.Clamp(MeshEntity.TexScale, _sliTexScale.Minimum, _sliTexScale.Maximum);

        _cmbFont.SelectedIndex = 0;
        _fontStatus.Text = "当前字体：" + (GlyphFontHost.ActiveFamily ?? "(默认笔画字体)");

        _sliContour.Value = Math.Clamp(MeshEntity.ContourSpacing, _sliContour.Minimum, _sliContour.Maximum);
        _lblContour.Text = _sliContour.Value.ToString("0.#", CultureInfo.InvariantCulture) + " m";

        _chkAutoRange.IsChecked = MeshEntity.AttrAutoRange;
        _txtMin.IsEnabled = _txtMax.IsEnabled = !MeshEntity.AttrAutoRange;
        _txtMin.Text = MeshEntity.AttrMin.ToString("0.###", CultureInfo.InvariantCulture);
        _txtMax.Text = MeshEntity.AttrMax.ToString("0.###", CultureInfo.InvariantCulture);

        _chkReverse.IsChecked = MeshEntity.AttrReverse;
        var maps = _cmbColormap.Items.OfType<ComboBoxItem>().ToList();
        _cmbColormap.SelectedItem = maps.FirstOrDefault(x => ReferenceEquals(x.Tag, MeshEntity.AttrColormap)) ?? maps[0];
        UpdateSwatch();

        _status.Text = $"当前：{ItemName(it)} · 场景里 {_allMeshes().Count} 张三角网";
        _loading = false;
    }

    // ── 小排版件 ─────────────────────────────────────────────────────────
    private static Border Group(string header, Control body) => new()
    {
        Classes = { "panel" },
        Child = new StackPanel
        {
            Children =
            {
                new Border { Classes = { "panel-hdr" }, Child = new TextBlock { Text = header, Classes = { "panel-title" } } },
                new Border { Padding = new Thickness(10, 8), Child = body },
            },
        },
    };

    private static Control Row(string label, Control input) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children =
        {
            new TextBlock { Text = label, Width = 88, VerticalAlignment = VerticalAlignment.Center },
            input,
        },
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" },
    };
}

internal static class ControlChain
{
    /// <summary>就地改一把再返回自身（省得为一句 Classes.Add 拆个临时变量出来）。</summary>
    public static T Also<T>(this T c, Action<T> f) { f(c); return c; }
}
