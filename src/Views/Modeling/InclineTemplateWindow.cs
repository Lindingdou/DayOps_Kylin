using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>「驱动量」参数面板要主窗做的事（Kylin 无实体 handle / 能力接口：以场景实体引用代替）。</summary>
internal sealed class InclineTemplateHandlers
{
    /// <summary>当前选中的三角网（跳过 exclude 里已被本窗拾取的）。</summary>
    public Func<IReadOnlySet<SceneEntity>, MeshEntity?>? FirstSelectedMesh;
    /// <summary>当前选中的工作线（跳过 exclude）→ 投影几何 + 实体 + 判据说明。</summary>
    public Func<IReadOnlySet<SceneEntity>, (WorkLineSamples? geo, PolylineEntity? line, string how)>? FirstSelectedWorkLine;
    /// <summary>按活实体重读工作线几何（用户加载后可能又调过箭头）。</summary>
    public Func<PolylineEntity, WorkLineSamples?>? RefreshWorkLine;
    /// <summary>当前选中的多段线（边界线；跳过 exclude）。</summary>
    public Func<IReadOnlySet<SceneEntity>, PolylineEntity?>? FirstSelectedPolyline;
    public Action<SceneEntity, bool>? SetVisible;
    public Action<IReadOnlyList<SceneEntity>>? DeleteEntities;
    public Action<string, bool>? SetLayerEntitiesVisible;
    public Func<string, int>? RemoveLayerEntities;
    public Func<IReadOnlyList<string>>? RegionNames;
    /// <summary>地质库虚拟钻孔面：(层名, 层序, 顶板几何, 底板几何)；库不可用返回空。</summary>
    public Func<IReadOnlyList<(string Seam, int Order, double[] RoofV, int[] RoofT, double[] FloorV, int[] FloorT)>>? GeoDbSeams;
    public Action<InclineTemplateInput, InclineTemplateWindow>? Confirmed;
    public Action<string, bool>? Echo;
}

/// <summary>
/// 「驱动量」（量驱动斜面模板）参数面板（忠实原 <c>InclineTemplateDialog</c>）：逐层煤（顶/底板面 + 属性/类别 + 容重 + 台阶高/采全高 + 2–3 月增量）
/// + 块体 + 现状面 + 工作线 + 端帮边界/控制线 + 可采范围 + α + 年产量 + 回采煤量 + 达标容差 + 煤判据 + 工作帮台阶参数 + 斜面约束块体。
/// 非模态；「确认」把采集到的 <see cref="InclineTemplateInput"/> 交给 Runner（主窗），求解后回调 <see cref="ShowResults"/> 就地显示采出煤量表，
/// 「推进调整」逐线滑块联动保总量、实时重画斜面。
/// </summary>
internal sealed class InclineTemplateWindow : Window
{
    internal sealed class SeamVM
    {
        public string Name = "";
        public MeshEntity? RoofMesh, FloorMesh;                 // 视口点选（一旦点选就顶掉库里那份）
        public double[]? RoofVerts, FloorVerts; public int[]? RoofTris, FloorTris;   // 地质库装的几何
        public string Attribute = "", Category = "";
        public double Density = 1.35, BenchH, IncrementWt;
        public bool FullHeight = true;
        public string RoofLabel => RoofMesh != null ? "✓ 顶板" : RoofVerts != null ? "✓ 顶板(库)" : "选顶板";
        public string FloorLabel => FloorMesh != null ? "✓ 底板" : FloorVerts != null ? "✓ 底板(库)" : "选底板";
        // 控件
        public TextBox TxtName = null!, TxtDensity = null!, TxtBenchH = null!, TxtInc = null!;
        public Button BtnRoof = null!, BtnFloor = null!;
        public ComboBox CmbAttr = null!, CmbCat = null!;
        public CheckBox ChkFull = null!;
    }

    private readonly InclineTemplateHandlers _h;
    internal readonly List<SeamVM> Seams = new();
    private readonly List<string> _attrNames = new();
    private BlockModelMeta? _activeBlock;
    private MeshEntity? _currentSurface;
    private readonly List<(WorkLineSamples geo, PolylineEntity line)> _workLines = new();
    private readonly HashSet<SceneEntity> _hidden = new();
    private readonly List<(PolylineEntity Ent, int Line, bool Created)> _boundaries = new();

    // 控件
    private readonly StackPanel _seamHost = new();
    private readonly ComboBox _cmbActiveBlock = new() { MinWidth = 168 };
    private readonly CheckBox _chkUnifyBenchH = new() { Content = "统一煤台阶高(m):", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _txtUnifyBenchH = new() { Width = 56, Text = "15", Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBox _txtBerm = new() { Width = 56, Text = "80" };
    private readonly TextBlock _txtCurrentSurface = new() { Text = "未选", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = Brush.Parse("#5B6B80") };
    private readonly TextBlock _txtWorkLine = new() { Text = "未加载", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = Brush.Parse("#5B6B80") };
    private readonly TextBlock _txtBoundary = new() { Text = "无", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Brush.Parse("#5B6B80") };
    private readonly ComboBox _cmbRegion = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
    private readonly TextBox _txtAlpha = new() { Text = "15", Width = 52 };
    private readonly TextBox _txtAnnual = new() { Text = "2000", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _txtRecovery = new() { Text = "400", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _txtThreshold = new() { Text = "5", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _cmbCoalMode = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 150, ItemsSource = new[] { "自动识别煤", "属性 ≥ 阈值为煤", "全部算煤" }, SelectedIndex = 0 };
    private readonly TextBox _txtCoalThreshold = new() { Text = "0", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _txtBenchH = new() { Text = "15", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _txtFinalAngle = new() { Text = "10", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _txtCoalFace = new() { Text = "65", Width = 52 };
    private readonly TextBox _txtRockFace = new() { Text = "65", Width = 52 };
    private readonly CheckBox _chkJoinEndWall = new() { Content = "端帮预判（只算不落地）", IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _chkConstrain = new() { Content = "跑完自动约束块体", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _cmbFrontMode = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 180, ItemsSource = new[] { "Stage1 统一前界 d", "Stage2 逐层前界 d_seam" }, SelectedIndex = 0 };
    private readonly Border _resultsPanel = new() { IsVisible = false, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(4), Background = Brush.Parse("#F4F6F9"), BorderBrush = Brush.Parse("#D5DBE3"), BorderThickness = new Thickness(1) };
    private readonly TextBlock _txtResultSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontSize = 12 };
    private readonly DataGrid _gridResult = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserSortColumns = false, MaxHeight = 220 };
    private readonly ToggleButton _btnAdjustMode = new() { Content = "推进调整", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3) };
    private readonly StackPanel _adjustHost = new() { IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    /// <summary>确认回调（非模态：OK 时把采集到的输入交给 Runner）。</summary>
    public Action<InclineTemplateInput>? OnConfirmed;
    /// <summary>逐线推进量变化回调（联动重解后的各线推进量）；Runner 据此实时重画斜面模板。</summary>
    public Action<double[]>? OnAdvanceAdjusted;
    /// <summary>「添加控制线」回调（Runner 实现）。</summary>
    public Action? OnAddControlLineRequested;
    /// <summary>「生成采区台阶面」回调（Runner 实现）。</summary>
    public Action<BenchTemplateParams>? OnGenerateBenchTemplate;

    private static TextBlock Head(string t, string? tip = null) { var tb = new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#5B6B80"), TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }; if (tip != null) ToolTip.SetTip(tb, tip); return tb; }
    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, TextTrimming = TextTrimming.None };
    private static Button Btn(string t, Action a, double minW = 0, string? tip = null) { var b = new Button { Content = t, MinWidth = minW, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 3) }; b.Click += (_, _) => a(); if (tip != null) ToolTip.SetTip(b, tip); return b; }
    private static Border Group(string header, Control body)
    {
        var st = new StackPanel();
        st.Children.Add(new TextBlock { Text = header, FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        st.Children.Add(body);
        return new Border { Child = st, Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 10), BorderBrush = Brush.Parse("#D5DBE3"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    }
    private static bool TryD(TextBox tb, out double v) => double.TryParse(tb.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    internal InclineTemplateWindow(InclineTemplateHandlers h)
    {
        _h = h;
        Title = "驱动量 · 量驱动斜面模板";
        Width = 1000; Height = 760;
        MinWidth = 760;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowFit.ClampToScreen(this);

        var root = new DockPanel();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 6, 12, 10) };
        footer.Children.Add(Btn("生成采区台阶面", OnGenerateBenchClick, 120, "用本次驱动量解出的前界+煤层顶底板，沿煤层自下而上生成采区台阶线（煤台阶+岩台阶，碰上层底板/现状面尖灭）。需先「确认」跑过一次"));
        footer.Children.Add(Btn("确认", OnOkClick, 88));
        footer.Children.Add(Btn("关闭", Close, 88));
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(12, 10, 12, 4) };
        body.Children.Add(new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5B6B80"), Margin = new Thickness(0, 0, 0, 8), Text = "多条同水平工作线 → 各建贯穿煤层的工作帮斜面，量(吨)反算位置。Stage1 逼近年产量，Stage2 各层多加 2–3 月回采。" });
        body.Children.Add(Group("煤层（逐层）", BuildSeamGroup()));
        body.Children.Add(Group("全局参数", BuildGlobalGroup()));
        body.Children.Add(new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5B6B80"), Margin = new Thickness(0, 0, 0, 10), Text = "工作线：同水平多条（平行/扇形），逐条选中后点「加载工作线」加入；基准标高取工作线自身 Z。确认 → 预处理 → 构面+交线 → Stage1 年产量逼近 → Stage2 各层多加 2–3 月（各层增量之和≈回采煤量）逐线微调。" });
        body.Children.Add(BuildResultsPanel());
        _scroll.Content = body;
        root.Children.Add(_scroll);
        Content = root;

        RefreshBlockNames();
        RefreshRegions();
        if (!AutoLoadSeamsFromGeoDb()) AddSeam(new SeamVM { Name = "煤1" });
        Closed += (_, _) => RestoreAllHidden();
    }

    // ── 煤层组 ──
    private static readonly double[] ColW = { 92, 96, 96, 124, 124, 62, 122, 92, 30 };
    private static Grid RowGrid()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var w in ColW) g.ColumnDefinitions.Add(new ColumnDefinition(w, GridUnitType.Pixel));
        return g;
    }
    private static T Col<T>(Grid g, int c, T ctl) where T : Control { Grid.SetColumn(ctl, c); ctl.Margin = new Thickness(0, 0, 6, 0); g.Children.Add(ctl); return ctl; }

    private Control BuildSeamGroup()
    {
        var st = new StackPanel();
        var hd = RowGrid();
        Col(hd, 0, Head("层名", "不想要的煤层直接删掉这一行（右侧「✕」）或不添加即可"));
        Col(hd, 1, Head("顶板面")); Col(hd, 2, Head("底板面")); Col(hd, 3, Head("属性")); Col(hd, 4, Head("类别")); Col(hd, 5, Head("容重"));
        Col(hd, 6, Head("台阶高·采全高", "本层煤台阶高度(m)；勾「全高」=采全高：不限台阶高，从底板交线一坡到顶板交线（整层一个台阶），坡顶=顶板交线，再向上循环返岩台阶"));
        Col(hd, 7, Head("增量(万t·S2)"));
        st.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new StackPanel { Children = { hd, _seamHost } } });

        var row1 = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row1.Children.Add(Btn("＋ 添加层", () => AddSeam(new SeamVM { Name = $"煤{Seams.Count + 1}" }), 84));
        row1.Children.Add(Lbl("　块体:"));
        ToolTip.SetTip(_cmbActiveBlock, "已加载的块体（各层共用；属性、类别都从它取）");
        _cmbActiveBlock.SelectionChanged += (_, _) => OnActiveBlockChanged();
        _cmbActiveBlock.Margin = new Thickness(0, 0, 8, 0);
        row1.Children.Add(_cmbActiveBlock);
        row1.Children.Add(Btn("加载块体…", () => _ = OnLoadBlockAsync(), 92, "从文件预加载块体（.pmb/.blk），不渲染显示；加载后自动设为当前块体"));
        row1.Children.Add(Btn("刷新", RefreshBlockNames, 56, "重新读取已加载的块体模型名"));
        st.Children.Add(row1);

        var row2 = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        ToolTip.SetTip(_chkUnifyBenchH, "勾选后全部煤层的「台阶高」联动为同一值（逐行锁定）；不勾=逐层独立配置。勾了「全高」（采全高）的层不受统一值影响");
        _chkUnifyBenchH.IsCheckedChanged += (_, _) => OnUnifyBenchHChanged();
        ToolTip.SetTip(_txtUnifyBenchH, "统一煤台阶高度(m)：勾选左侧后写入全部层并联动");
        _txtUnifyBenchH.TextChanged += (_, _) => { if (_chkUnifyBenchH.IsChecked == true) PushUnifiedBenchH(); };
        row2.Children.Add(_chkUnifyBenchH); row2.Children.Add(_txtUnifyBenchH);
        row2.Children.Add(new TextBlock { Text = "统一最小工作平盘(m):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 6, 0), FontSize = 12 });
        ToolTip.SetTip(_txtBerm, "全部煤/岩台阶统一：每级平盘不小于此（设备作业需求）；与工作帮坡角反算平盘取更缓者");
        row2.Children.Add(_txtBerm);
        st.Children.Add(row2);
        return st;
    }

    private void AddSeam(SeamVM vm)
    {
        if (_chkUnifyBenchH.IsChecked == true && TryD(_txtUnifyBenchH, out double uh) && uh >= 0) vm.BenchH = uh;
        if (string.IsNullOrEmpty(vm.Attribute) && _attrNames.Count > 0) vm.Attribute = _attrNames[0];
        Seams.Add(vm);
        var g = RowGrid();
        vm.TxtName = Col(g, 0, new TextBox { Text = vm.Name });
        vm.TxtName.TextChanged += (_, _) => vm.Name = vm.TxtName.Text ?? "";
        vm.BtnRoof = Col(g, 1, new Button { Content = vm.RoofLabel, Padding = new Thickness(6, 3), HorizontalContentAlignment = HorizontalAlignment.Center });
        ToolTip.SetTip(vm.BtnRoof, "在视图中选中顶板面三角网后点此，指给本层顶板（✓=已选）");
        vm.BtnRoof.Click += (_, _) => PickRoof(vm);
        vm.BtnFloor = Col(g, 2, new Button { Content = vm.FloorLabel, Padding = new Thickness(6, 3), HorizontalContentAlignment = HorizontalAlignment.Center });
        ToolTip.SetTip(vm.BtnFloor, "在视图中选中底板面三角网后点此，指给本层底板（✓=已选）");
        vm.BtnFloor.Click += (_, _) => PickFloor(vm);
        vm.CmbAttr = Col(g, 3, new ComboBox { ItemsSource = _attrNames.ToArray(), SelectedItem = vm.Attribute, HorizontalAlignment = HorizontalAlignment.Stretch });
        ToolTip.SetTip(vm.CmbAttr, "用于计算的属性列（来自上方块体）");
        vm.CmbAttr.SelectionChanged += (_, _) => { vm.Attribute = vm.CmbAttr.SelectedItem as string ?? ""; RefreshCategories(vm); };
        vm.CmbCat = Col(g, 4, new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch });
        ToolTip.SetTip(vm.CmbCat, "该属性里对应本层煤的「类别」（选属性后自动列出）");
        vm.CmbCat.SelectionChanged += (_, _) => vm.Category = vm.CmbCat.SelectedItem as string ?? "";
        vm.TxtDensity = Col(g, 5, new TextBox { Text = vm.Density.ToString("0.##", CultureInfo.InvariantCulture) });
        ToolTip.SetTip(vm.TxtDensity, "容重 t/m³（带煤默认 1.35）");
        vm.TxtDensity.TextChanged += (_, _) => { if (TryD(vm.TxtDensity, out double d)) vm.Density = d; };
        var bh = new StackPanel { Orientation = Orientation.Horizontal };
        vm.TxtBenchH = new TextBox { Width = 44, Text = vm.BenchH > 0 ? vm.BenchH.ToString("0.#", CultureInfo.InvariantCulture) : "", IsEnabled = !vm.FullHeight && _chkUnifyBenchH.IsChecked != true };
        ToolTip.SetTip(vm.TxtBenchH, "本层煤台阶高度(m)：小于煤厚时层内自下而上分多级（勾「全高」或下方「统一台阶高」后锁定）");
        vm.TxtBenchH.TextChanged += (_, _) => { if (TryD(vm.TxtBenchH, out double v)) vm.BenchH = v; };
        vm.ChkFull = new CheckBox { Content = "全高", IsChecked = vm.FullHeight, FontSize = 11, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(vm.ChkFull, "采全高：不限台阶高，从底板交线一坡到顶板交线（整层一个台阶，坡顶=顶板交线），再向上循环返岩台阶");
        vm.ChkFull.IsCheckedChanged += (_, _) => { vm.FullHeight = vm.ChkFull.IsChecked == true; vm.TxtBenchH.IsEnabled = !vm.FullHeight && _chkUnifyBenchH.IsChecked != true; };
        bh.Children.Add(vm.TxtBenchH); bh.Children.Add(vm.ChkFull);
        Col(g, 6, bh);
        vm.TxtInc = Col(g, 7, new TextBox { Text = vm.IncrementWt.ToString("0.#", CultureInfo.InvariantCulture) });
        ToolTip.SetTip(vm.TxtInc, "Stage2：该层 2–3 月回采增量目标（万t）");
        vm.TxtInc.TextChanged += (_, _) => { if (TryD(vm.TxtInc, out double v)) vm.IncrementWt = v; };
        var del = Col(g, 8, new Button { Content = "✕", Padding = new Thickness(4, 0), FontSize = 11 });
        ToolTip.SetTip(del, "删除本层");
        del.Click += (_, _) => { ShowEnt(vm.RoofMesh); ShowEnt(vm.FloorMesh); Seams.Remove(vm); _seamHost.Children.Remove(g); };
        _seamHost.Children.Add(g);
        RefreshCategories(vm);
    }

    private void OnUnifyBenchHChanged()
    {
        bool on = _chkUnifyBenchH.IsChecked == true;
        foreach (var vm in Seams) vm.TxtBenchH.IsEnabled = !on && !vm.FullHeight;
        if (on) PushUnifiedBenchH();
    }
    private void PushUnifiedBenchH()
    {
        if (!TryD(_txtUnifyBenchH, out double v) || v < 0) return;
        foreach (var vm in Seams) { vm.BenchH = v; vm.TxtBenchH.Text = v.ToString("0.#", CultureInfo.InvariantCulture); }
    }

    private void RefreshBlockNames()
    {
        var names = BlockModelStore.Models.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        string? keep = _cmbActiveBlock.SelectedItem as string;
        _cmbActiveBlock.ItemsSource = names;
        if (keep != null && names.Contains(keep)) _cmbActiveBlock.SelectedItem = keep;
        else if (names.Count > 0) _cmbActiveBlock.SelectedIndex = BlockModelStore.Active != null ? Math.Max(0, names.IndexOf(BlockModelStore.Active.Name)) : 0;
        else OnActiveBlockChanged();
    }

    private void OnActiveBlockChanged()
    {
        string name = _cmbActiveBlock.SelectedItem as string ?? "";
        _activeBlock = BlockModelStore.Models.FirstOrDefault(m => m.Name == name);
        _attrNames.Clear();
        if (_activeBlock != null)
        {
            foreach (var c in _activeBlock.PropertySchema) if (!string.IsNullOrEmpty(c.Name) && !_attrNames.Contains(c.Name)) _attrNames.Add(c.Name);
            foreach (var k in _activeBlock.Attrs.Keys) if (!_attrNames.Contains(k)) _attrNames.Add(k);
        }
        foreach (var vm in Seams)
        {
            string a = (!string.IsNullOrEmpty(vm.Attribute) && _attrNames.Contains(vm.Attribute)) ? vm.Attribute : (_attrNames.Count > 0 ? _attrNames[0] : "");
            vm.CmbAttr.ItemsSource = _attrNames.ToArray();
            vm.CmbAttr.SelectedItem = a; vm.Attribute = a;
            RefreshCategories(vm);
        }
    }

    /// <summary>按某行选中的属性填「类别」下拉：有名表用名，没有就列数据里实际出现过的类别码（原 AttributeCategories 口径）。</summary>
    private void RefreshCategories(SeamVM vm)
    {
        var names = new List<string>();
        if (_activeBlock != null && !string.IsNullOrEmpty(vm.Attribute))
        {
            var col = _activeBlock.FindColumn(vm.Attribute);
            var set = new SortedSet<int>();
            var labels = col?.CategoryLabels;
            if (labels is { Count: > 0 }) for (int i = 0; i < labels.Count && set.Count < 256; i++) if (!string.IsNullOrEmpty(labels[i])) set.Add(i);
            foreach (var (v, _, _) in BlockCoalQualityLink.DistinctValues(_activeBlock, vm.Attribute))
            { int code = (int)Math.Round(v); if (Math.Abs(v - code) < 1e-9 && code >= 0 && set.Count < 256) set.Add(code); }
            foreach (int code in set)
            {
                string label = labels != null && code < labels.Count ? labels[code] ?? "" : "";
                names.Add(label.Length > 0 ? label : code.ToString(CultureInfo.InvariantCulture));
            }
        }
        if (vm.CmbCat == null) return;
        vm.CmbCat.ItemsSource = names.ToArray();
        if (!string.IsNullOrEmpty(vm.Category) && !names.Contains(vm.Category)) vm.Category = "";
        if (string.IsNullOrEmpty(vm.Category) && names.Count > 0) vm.Category = names[0];
        vm.CmbCat.SelectedItem = vm.Category.Length > 0 ? vm.Category : null;
    }

    private void RefreshRegions()
    {
        var names = new List<string> { "(自动·可采范围)" };
        try { if (_h.RegionNames != null) names.AddRange(_h.RegionNames().Where(n => !string.IsNullOrEmpty(n))); } catch { }
        _cmbRegion.ItemsSource = names;
        _cmbRegion.SelectedIndex = 0;
    }

    /// <summary>打开面板即从地质库装好各煤层顶底板（虚拟钻孔 virtual_drill_surface），按 seam_order 自下而上。装不上返回 false。</summary>
    private bool AutoLoadSeamsFromGeoDb()
    {
        try
        {
            var rows = _h.GeoDbSeams?.Invoke();
            if (rows == null || rows.Count == 0) return false;
            int ok = 0;
            foreach (var r in rows.OrderBy(r => r.Order))
            {
                if (r.RoofV.Length < 9 || r.FloorV.Length < 9) continue;
                AddSeam(new SeamVM { Name = r.Seam, RoofVerts = r.RoofV, RoofTris = r.RoofT, FloorVerts = r.FloorV, FloorTris = r.FloorT });
                ok++;
            }
            return ok > 0;
        }
        catch { return false; }
    }

    // ── 拾取（隐藏 / 恢复）──
    private void HideEnt(SceneEntity? e) { if (e == null) return; _h.SetVisible?.Invoke(e, false); _hidden.Add(e); }
    private void ShowEnt(SceneEntity? e) { if (e == null) return; _h.SetVisible?.Invoke(e, true); _hidden.Remove(e); }
    private void RestoreAllHidden() { foreach (var e in _hidden.ToList()) _h.SetVisible?.Invoke(e, true); _hidden.Clear(); }
    /// <summary>对话框已拾取（并隐藏）的实体：顶/底板面、现状面、工作线。</summary>
    public IReadOnlyCollection<SceneEntity> PickedEntities => _hidden;
    public IReadOnlyList<PolylineEntity> WorkLineEntities => _workLines.Select(w => w.line).ToList();

    private void PickRoof(SeamVM vm)
    {
        var m = _h.FirstSelectedMesh?.Invoke(_hidden);
        if (m == null) { Warn("请先在视图中选中顶板面三角网（一张未被本对话框用过的面），再点「选顶板」。"); return; }
        if (vm.RoofMesh != null && vm.RoofMesh != m) ShowEnt(vm.RoofMesh);
        vm.RoofMesh = m; vm.RoofVerts = null; vm.RoofTris = null; HideEnt(m);
        vm.BtnRoof.Content = vm.RoofLabel;
    }
    private void PickFloor(SeamVM vm)
    {
        var m = _h.FirstSelectedMesh?.Invoke(_hidden);
        if (m == null) { Warn("请先在视图中选中底板面三角网（一张未被本对话框用过的面），再点「选底板」。"); return; }
        if (vm.FloorMesh != null && vm.FloorMesh != m) ShowEnt(vm.FloorMesh);
        vm.FloorMesh = m; vm.FloorVerts = null; vm.FloorTris = null; HideEnt(m);
        vm.BtnFloor.Content = vm.FloorLabel;
    }
    private void PickCurrentSurface()
    {
        var m = _h.FirstSelectedMesh?.Invoke(_hidden);
        if (m == null) { Warn("请先在视图中选中现状面三角网（一张未被本对话框用过的面），再点「选现状面」。"); return; }
        if (_currentSurface != null && _currentSurface != m) ShowEnt(_currentSurface);
        _currentSurface = m; HideEnt(m);
        _txtCurrentSurface.Text = $"✓ 已选（{m.Name}）";
    }
    private void LoadWorkLine()
    {
        var r = _h.FirstSelectedWorkLine?.Invoke(_hidden) ?? (null, null, "");
        if (r.line == null) { Warn("请先在视图中选中 1 条【未加载过的】工作线，再点「加载工作线」。"); return; }
        if (r.geo == null || !r.geo.Success) { Warn("选中的不是工作线（" + (r.geo?.Error ?? "无法提取推进方向") + "）。"); return; }
        _workLines.Add((r.geo, r.line)); HideEnt(r.line);
        _txtWorkLine.Text = $"✓ 已加载 {_workLines.Count} 条（{r.how}）";
    }
    private void ClearWorkLines()
    {
        foreach (var (_, l) in _workLines) ShowEnt(l);
        _workLines.Clear(); _txtWorkLine.Text = "未加载";
    }
    /// <summary>确认时按活实体重读工作线几何（用户加载后常又在视口调推进方向）。全部重读成功才整体替换。</summary>
    private void RefreshWorkLineGeometries()
    {
        if (_workLines.Count == 0 || _h.RefreshWorkLine == null) return;
        var fresh = new List<(WorkLineSamples, PolylineEntity)>();
        foreach (var (_, l) in _workLines) { var g = _h.RefreshWorkLine(l); if (g is { Success: true }) fresh.Add((g, l)); }
        if (fresh.Count == _workLines.Count) { _workLines.Clear(); _workLines.AddRange(fresh); }
    }

    private void EstimateAlpha()
    {
        if (_currentSurface == null) { Warn("请先「选现状面」（在视图中选中现状面三角网后点选）。"); return; }
        if (_workLines.Count == 0) { Warn("请先「加载工作线」（至少 1 条，坡角沿其推进方向估算）。"); return; }
        RefreshWorkLineGeometries();
        var (v, t) = _currentSurface.Flatten();
        var surf = TinSampler.TryBuild(v, t);
        if (surf == null) { Warn("现状面不是有效三角网。"); return; }
        var r = WorkingSlopeEstimator.Estimate(surf, _workLines.Select(w => w.geo).ToList());
        if (!r.Ok) { Warn("估算失败：" + r.Note); return; }
        _txtAlpha.Text = r.AngleDeg.ToString("0.#", CultureInfo.InvariantCulture);
        _ = BlockMsgBox.InfoAsync(this, "现状面估算工作帮坡角", r.Note + "\n\n已填入「工作帮坡角 α」。这是【大致】估算（现状面沿推进方向的整体倾角），可按需微调。");
    }

    // ── 端帮边界线 / 控制线 ──
    public IReadOnlyList<(PolylineEntity Ent, int Line)> BoundaryBindings => _boundaries.Select(b => (b.Ent, b.Line)).ToList();
    public void RegisterBoundary(PolylineEntity e, int line, bool created)
    {
        if (_boundaries.Any(b => ReferenceEquals(b.Ent, e))) return;
        _boundaries.Add((e, line, created));
        _txtBoundary.Text = $"✓ {_boundaries.Count} 条";
    }
    private void AddControlLine()
    {
        if (OnAddControlLineRequested == null) { Warn("请先「确认」跑一次驱动量（需要工作线与前界），再添加控制线。"); return; }
        try { OnAddControlLineRequested(); } catch (Exception ex) { Warn("添加控制线异常：" + ex.Message); }
    }
    private void LoadBoundary()
    {
        var excl = new HashSet<SceneEntity>(_hidden); foreach (var b in _boundaries) excl.Add(b.Ent);
        var pl = _h.FirstSelectedPolyline?.Invoke(excl);
        if (pl == null) { Warn("请先在视图中选中 1 条【未加载过的】端帮裁剪边界线（多段线），再点「加载边界线」。"); return; }
        if (pl.Points.Count < 2) { Warn("边界线须为至少 2 个顶点的多段线。"); return; }
        RegisterBoundary(pl, -1, created: false);
    }
    private void ClearBoundaries()
    {
        var created = _boundaries.Where(b => b.Created).Select(b => (SceneEntity)b.Ent).ToList();
        if (created.Count > 0) { try { _h.DeleteEntities?.Invoke(created); } catch { } }
        _boundaries.Clear(); _txtBoundary.Text = "无";
    }

    // ── 全局参数组 ──
    private Control BuildGlobalGroup()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*") };
        int row = 0;
        void Row(Control a, Control b, Control? c = null, Control? d = null, int spanB = 1)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            a.Margin = new Thickness(0, 0, 8, 8); Grid.SetRow(a, row); Grid.SetColumn(a, 0); g.Children.Add(a);
            b.Margin = new Thickness(0, 0, 18, 8); Grid.SetRow(b, row); Grid.SetColumn(b, 1); Grid.SetColumnSpan(b, spanB); g.Children.Add(b);
            if (c != null) { c.Margin = new Thickness(0, 0, 8, 8); Grid.SetRow(c, row); Grid.SetColumn(c, 2); g.Children.Add(c); }
            if (d != null) { d.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(d, row); Grid.SetColumn(d, 3); g.Children.Add(d); }
            row++;
        }
        void Section(string t)
        {
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var tb = new TextBlock { Text = t, FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, 0); Grid.SetColumnSpan(tb, 4); g.Children.Add(tb); row++;
        }

        var surfRow = new StackPanel { Orientation = Orientation.Horizontal };
        surfRow.Children.Add(Btn("选现状面", PickCurrentSurface, 92, "在视图中选中现状面三角网后点此"));
        surfRow.Children.Add(_txtCurrentSurface);
        Row(Lbl("现状面:"), surfRow, spanB: 3);

        var wlRow = new WrapPanel();
        wlRow.Children.Add(Btn("加载工作线", LoadWorkLine, 92, "在视图中选中 1 条工作线后点此加载；可重复加载多条（基准标高取工作线自身 Z）"));
        wlRow.Children.Add(Btn("清空", ClearWorkLines, 58));
        wlRow.Children.Add(_txtWorkLine);
        wlRow.Children.Add(new TextBlock { Text = "端帮边界:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0), FontSize = 12 });
        wlRow.Children.Add(Btn("添加控制线", AddControlLine, 80, "为工作线端部生成一条蓝色控制线（沿推进方向过端点，绑定该工作线）。视口夹点编辑：拖端点=绕另一端旋转/伸缩，整体可平移。生成台阶时端帮线与模板台阶线都严格按它裁剪。逐次点击依次补齐 各工作线×两端"));
        wlRow.Children.Add(Btn("加载边界线", LoadBoundary, 84, "可选：在视图中选中 1 条多段线作为端帮的对齐裁剪位置（全局，约束全部工作线）——裁剪口精确落在这条线上"));
        wlRow.Children.Add(Btn("清空", ClearBoundaries, 46, "清空全部边界/控制线登记；本功能生成的蓝色控制线实体一并删除（手动加载的原实体不动）"));
        wlRow.Children.Add(_txtBoundary);
        Row(Lbl("工作线:"), wlRow, spanB: 3);

        ToolTip.SetTip(_cmbRegion, "斜面横向裁到该可采范围；「自动·可采范围」=系统里全部【采场】+【未分类】的区域（排土场与工作帮不算）");
        Row(Lbl("可采范围:"), _cmbRegion, spanB: 3);

        var alphaRow = new StackPanel { Orientation = Orientation.Horizontal };
        ToolTip.SetTip(_txtAlpha, "最大工作帮坡角：斜面沿前进方向起坡的整体倾角（「确认」时由岩台阶高/岩坡角/平盘反算并回显）");
        alphaRow.Children.Add(_txtAlpha);
        var est = Btn("现状面估算", EstimateAlpha, 76, "用现状面沿工作线推进方向采样、拟合出大致的最大工作帮坡角并填入左侧（需先「选现状面」+「加载工作线」）"); est.Margin = new Thickness(6, 0, 0, 0);
        alphaRow.Children.Add(est);
        ToolTip.SetTip(_txtAnnual, "Stage1 全局目标：所有线同步推进逼近此年产量");
        Row(Lbl("工作帮坡角 α(°):"), alphaRow, Lbl("年产量(万t · S1):"), _txtAnnual);
        ToolTip.SetTip(_txtRecovery, "Stage2 回采煤量总量（≈2–3 月年产量）；各层增量之和应≈此值");
        ToolTip.SetTip(_txtThreshold, "产能/回采达成量的容差带：达成量在目标 ±此% 内视为达标，否则回显⚠超差");
        Row(Lbl("回采煤量(万t · S2):"), _txtRecovery, Lbl("达标容差(±%):"), _txtThreshold);
        ToolTip.SetTip(_cmbCoalMode, "如何从「属性」列判定煤（只数煤）");
        ToolTip.SetTip(_txtCoalThreshold, "煤判据=「属性 ≥ 阈值」时生效");
        Row(Lbl("煤判据:"), _cmbCoalMode, Lbl("煤阈值:"), _txtCoalThreshold);

        Section("工作帮台阶参数（创建工程位置 · 采区台阶生成）");
        ToolTip.SetTip(_txtBenchH, "层间岩台阶逐级向上的台阶高（碰上层煤底板尖灭）；煤台阶高在上方「煤层」表逐层配/统一联动，最小工作平盘也在那里统一配");
        ToolTip.SetTip(_txtFinalAngle, "工作帮总坡角：台阶 toe 落在此坡度包络线上（≈10°）");
        Row(Lbl("岩台阶高度(m):"), _txtBenchH, Lbl("工作帮坡角(°):"), _txtFinalAngle);
        var faceRow = new StackPanel { Orientation = Orientation.Horizontal };
        ToolTip.SetTip(_txtCoalFace, "煤台阶坡面角"); ToolTip.SetTip(_txtRockFace, "岩台阶坡面角");
        faceRow.Children.Add(_txtCoalFace); faceRow.Children.Add(new TextBlock { Text = "/", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) }); faceRow.Children.Add(_txtRockFace);
        ToolTip.SetTip(_chkJoinEndWall, "只在命令行回报「有多少条现状台阶线跨了模板边界、能衔接几处」，一条线都不入图、一个实体都不改。\n真正的裁剪与替换走「创建工程位置」→④预览替换⑤执行替换（那儿有平面预览、逐图层确认、逐条记账）。");
        Row(Lbl("煤/岩坡角(°):"), faceRow, _chkJoinEndWall, new Panel());

        Section("斜面约束块体（跑完按前界切块体 · 可撤销）");
        ToolTip.SetTip(_chkConstrain, "确认后按 Stage1/2 前界把斜面当约束面切块体（挖除已采楔形）；勾掉后重跑=撤销上次约束");
        ToolTip.SetTip(_cmbFrontMode, "统一=整幅一张斜面同一前界 d；逐层=各层按各自 d_seam 切，层间阶梯");
        Row(_chkConstrain, new Panel(), Lbl("前界口径:"), _cmbFrontMode);
        return g;
    }

    // ── 结果区 ──
    private sealed class ResultRow
    {
        public string Seam { get; set; } = "";
        public string Stage1Wt { get; set; } = "";
        public string Stage2Wt { get; set; } = "";
        public string TotalWt { get; set; } = "";
        public string DSeam { get; set; } = "";
    }
    private readonly List<ResultRow> _resultRows = new();
    private string _resultSummary = "";

    private Control BuildResultsPanel()
    {
        var st = new StackPanel();
        st.Children.Add(new TextBlock { Text = "采出煤量", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        st.Children.Add(_txtResultSummary);
        static TextBlock H(string t) => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };
        DataGridTextColumn C(string hd, string p, double star) => new() { Header = H(hd), Binding = new Binding(p), Width = new DataGridLength(star, DataGridLengthUnitType.Star) };
        _gridResult.Columns.Add(C("煤层", nameof(ResultRow.Seam), 1.4));
        _gridResult.Columns.Add(C("阶段1·年产量(万t)", nameof(ResultRow.Stage1Wt), 1.4));
        _gridResult.Columns.Add(C("阶段2·增量(万t)", nameof(ResultRow.Stage2Wt), 1.4));
        _gridResult.Columns.Add(C("合计(万t)", nameof(ResultRow.TotalWt), 1.2));
        _gridResult.Columns.Add(C("前界d_seam(m)", nameof(ResultRow.DSeam), 1.3));
        st.Children.Add(new Border { BorderBrush = Brush.Parse("#D5DBE3"), BorderThickness = new Thickness(1), Child = _gridResult, Margin = new Thickness(0, 0, 0, 8) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        ToolTip.SetTip(_btnAdjustMode, "进入后隐藏斜面、显示工作线；下方逐条工作线滑块拖动前进/后退，其余工作线联动、保采出总量不变，模板实时刷新");
        _btnAdjustMode.IsCheckedChanged += (_, _) => { if (_btnAdjustMode.IsChecked == true) OnAdjustModeOn(); else OnAdjustModeOff(); };
        bar.Children.Add(_btnAdjustMode);
        bar.Children.Add(Btn("导出 CSV", () => _ = ExportResultCsvAsync(), 88));
        st.Children.Add(bar);
        st.Children.Add(_adjustHost);
        _resultsPanel.Child = st;
        return _resultsPanel;
    }

    /// <summary>由 Runner 求解后回调：把采出煤量填入本窗下方结果区（逐层×阶段 + 汇总），并展开。</summary>
    public void ShowResults(Stage1Result s1, Stage2Result? s2, InclineTemplateInput input, double advance)
    {
        try
        {
            _resultRows.Clear();
            double totS1 = 0, totS2 = 0;
            var ci = CultureInfo.InvariantCulture;
            if (s2 != null && s2.Success && s2.Seams.Count > 0)
            {
                foreach (var ss in s2.Seams)
                {
                    totS1 += ss.Stage1Wt; totS2 += ss.AchievedWt;
                    _resultRows.Add(new ResultRow { Seam = ss.Name, Stage1Wt = ss.Stage1Wt.ToString("0.0", ci), Stage2Wt = ss.AchievedWt.ToString("0.0", ci) + (ss.WithinTol ? "" : " ⚠"), TotalWt = ss.TotalWt.ToString("0.0", ci), DSeam = ss.Stage2Advance.ToString("0.#", ci) });
                }
            }
            else
            {
                totS1 = s1.TotalCoalWt;
                _resultRows.Add(new ResultRow { Seam = "（全层合计）", Stage1Wt = s1.TotalCoalWt.ToString("0.0", ci), Stage2Wt = "—", TotalWt = s1.TotalCoalWt.ToString("0.0", ci), DSeam = advance.ToString("0.#", ci) });
            }
            _resultRows.Add(new ResultRow { Seam = "合计", Stage1Wt = totS1.ToString("0.0", ci), Stage2Wt = totS2.ToString("0.0", ci), TotalWt = (totS1 + totS2).ToString("0.0", ci), DSeam = "" });

            double annual = input?.AnnualProductionWt ?? 0, recov = input?.RecoveryTotalWt ?? 0;
            double tol = s1.TolPct > 0 ? s1.TolPct : 5.0;
            double devS2 = recov > 1e-9 ? (totS2 - recov) / recov * 100.0 : 0;
            bool s2Ok = recov <= 1e-9 || Math.Abs(devS2) <= tol + 1e-6;
            _resultSummary =
                $"推进 d={advance:0.#}m · 切片 Δ={s1.SliceWidth:0.#}m · 煤 {s1.CoalCellCount:N0} cell · 容差±{tol:0.#}%\n"
                + $"阶段1 年产量：目标 {annual:0.0} → 达成 {s1.TotalCoalWt:0.0} 万t（偏差 {s1.DeviationPct:+0.0;-0.0}%）{(s1.WithinTol ? " ✓" : $" ⚠超差(上限 {s1.MaxCoalWt:0.0})")}\n"
                + $"阶段2 回采增量：目标 {recov:0.0} → 达成 {totS2:0.0} 万t（偏差 {devS2:+0.0;-0.0}%）{(s2Ok ? " ✓" : " ⚠超差")}　·　采出合计 {s1.TotalCoalWt + totS2:0.0} 万t"
                + (s2 != null && s2.Success ? "\n" + s2.TotalCheckText : "");
            _txtResultSummary.Text = _resultSummary;
            _gridResult.ItemsSource = null;
            _gridResult.ItemsSource = _resultRows.ToList();
            _resultsPanel.IsVisible = true;
            Dispatcher.UIThread.Post(() => { try { _resultsPanel.BringIntoView(); } catch { } }, DispatcherPriority.Loaded);
        }
        catch { }
    }
    internal string ResultSummary => _resultSummary;
    internal int ResultRowCount => _resultRows.Count;

    // ── 推进调整 ──
    private CoalProfile? _adjustProf;
    private double _adjustTarget, _adjustTolPct = 5.0;
    private double[] _adjustPerLineD = Array.Empty<double>();
    private Slider[] _adjustSliders = Array.Empty<Slider>();
    private TextBlock[] _adjustLabels = Array.Empty<TextBlock>();
    private TextBlock? _adjustTotalLabel;
    private bool _adjustSuppress, _dragging;
    private DispatcherTimer? _redrawTimer;

    public void SetupAdjust(CoalProfile prof, double targetWt, double[] initialPerLineD, double tolPct = 5.0)
    {
        _adjustProf = prof; _adjustTarget = targetWt;
        _adjustTolPct = tolPct > 0 ? tolPct : 5.0;
        _adjustPerLineD = (double[])initialPerLineD.Clone();
        BuildAdjustPanel();
    }
    internal double[] AdjustPerLineD => _adjustPerLineD;

    private void BuildAdjustPanel()
    {
        _adjustHost.Children.Clear();
        _adjustSliders = Array.Empty<Slider>(); _adjustLabels = Array.Empty<TextBlock>();
        if (_adjustProf == null || _adjustProf.LineCount == 0) return;
        int L = _adjustProf.LineCount;
        double dz = _adjustProf.SliceWidth > 1e-9 ? _adjustProf.SliceWidth : 1.0;
        _adjustSliders = new Slider[L]; _adjustLabels = new TextBlock[L];
        _adjustTotalLabel = new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _adjustHost.Children.Add(_adjustTotalLabel);
        for (int l = 0; l < L; l++)
        {
            double maxD = Math.Max(dz, InclineVolumeEngine.MaxAdvance(_adjustProf, l, dz));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = $"工作线{l + 1}", Width = 58, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var sl = new Slider { Minimum = 0, Maximum = maxD, Width = 300, VerticalAlignment = VerticalAlignment.Center, Value = Math.Min(_adjustPerLineD.Length > l ? _adjustPerLineD[l] : 0, maxD) };
            int li = l;
            sl.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) OnAdjustSliderChanged(li); };
            sl.AddHandler(PointerPressedEvent, (_, _) => _dragging = true, RoutingStrategies.Tunnel, true);
            sl.AddHandler(PointerReleasedEvent, (_, _) => { _dragging = false; FlushAdjustRedrawNow(); }, RoutingStrategies.Tunnel, true);
            row.Children.Add(sl);
            var lab = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 150, FontSize = 12 };
            row.Children.Add(lab);
            _adjustSliders[l] = sl; _adjustLabels[l] = lab;
            _adjustHost.Children.Add(row);
        }
        UpdateAdjustReadouts();
    }

    private void OnAdjustSliderChanged(int line)
    {
        if (_adjustSuppress || _adjustProf == null || line >= _adjustSliders.Length) return;
        double newD = _adjustSliders[line].Value;
        double cap = _adjustTarget * (1 + _adjustTolPct / 100.0);
        if (_adjustTarget > 1e-9 && _adjustProf.CoalAtLine(line, newD) > cap)
        {
            double lo = 0, hi = newD;
            for (int it = 0; it < 50; it++) { double mid = 0.5 * (lo + hi); if (_adjustProf.CoalAtLine(line, mid) > cap) hi = mid; else lo = mid; }
            newD = lo;
            _adjustSuppress = true; _adjustSliders[line].Value = newD; _adjustSuppress = false;
        }
        _adjustPerLineD = InclineVolumeEngine.RelinkKeepTotal(_adjustProf, _adjustTarget, line, newD);
        _adjustSuppress = true;
        for (int l = 0; l < _adjustSliders.Length; l++) if (l != line && l < _adjustPerLineD.Length) _adjustSliders[l].Value = Math.Min(_adjustPerLineD[l], _adjustSliders[l].Maximum);
        _adjustSuppress = false;
        UpdateAdjustReadouts();
        if (_dragging) return;
        ScheduleAdjustRedraw();
    }
    /// <summary>自检：直接设某线推进量（等价拖动滑块）。</summary>
    internal void SelftestAdjust(int line, double d) { if (line < _adjustSliders.Length) { _adjustSliders[line].Value = d; FlushAdjustRedrawNow(); } }

    private void FlushAdjustRedrawNow() { _redrawTimer?.Stop(); try { OnAdvanceAdjusted?.Invoke(_adjustPerLineD); } catch { } }
    private void ScheduleAdjustRedraw()
    {
        if (_redrawTimer == null)
        {
            _redrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _redrawTimer.Tick += (_, _) => { _redrawTimer!.Stop(); try { OnAdvanceAdjusted?.Invoke(_adjustPerLineD); } catch { } };
        }
        _redrawTimer.Stop(); _redrawTimer.Start();
    }
    private void UpdateAdjustReadouts()
    {
        if (_adjustProf == null) return;
        double total = 0;
        for (int l = 0; l < _adjustLabels.Length && l < _adjustPerLineD.Length; l++)
        {
            double coal = _adjustProf.CoalAtLine(l, _adjustPerLineD[l]); total += coal;
            _adjustLabels[l].Text = $"d={_adjustPerLineD[l]:0.#}m　煤={coal:0.0}万t";
        }
        if (_adjustTotalLabel != null)
            _adjustTotalLabel.Text = $"总采出 {total:0.0} 万t / 目标 {_adjustTarget:0.0} 万t（拖一条其余联动保总量；上限 {_adjustTarget * (1 + _adjustTolPct / 100.0):0.0} 万t=目标+{_adjustTolPct:0.#}%）";
    }
    private void OnAdjustModeOn()
    {
        if (_adjustProf == null) { Warn("请先「确认」运行一次，再进入推进调整。"); _btnAdjustMode.IsChecked = false; return; }
        _h.SetLayerEntitiesVisible?.Invoke(InclineLayers.Surface, false);
        _h.SetLayerEntitiesVisible?.Invoke(InclineLayers.Stage2, false);
        foreach (var (_, l) in _workLines) _h.SetVisible?.Invoke(l, true);
        _adjustHost.IsVisible = true;
        Dispatcher.UIThread.Post(() => { try { _adjustHost.BringIntoView(); } catch { } }, DispatcherPriority.Loaded);
    }
    private void OnAdjustModeOff()
    {
        if (_redrawTimer is { IsEnabled: true }) { _redrawTimer.Stop(); try { OnAdvanceAdjusted?.Invoke(_adjustPerLineD); } catch { } }
        _h.SetLayerEntitiesVisible?.Invoke(InclineLayers.Surface, true);
        _h.RemoveLayerEntities?.Invoke(InclineLayers.Stage2);
        foreach (var (_, l) in _workLines) _h.SetVisible?.Invoke(l, false);
        _adjustHost.IsVisible = false;
    }
    internal void SetAdjustMode(bool on) => _btnAdjustMode.IsChecked = on;

    private async Task ExportResultCsvAsync()
    {
        if (_resultRows.Count == 0) { Warn("还没有可导出的结果，请先「确认」运行。"); return; }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出采出煤量 CSV", SuggestedFileName = "量驱动采出煤量.csv", DefaultExtension = "csv", FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } } });
            if (file == null) return;
            var sb = new System.Text.StringBuilder();
            foreach (var line in _resultSummary.Split('\n')) sb.Append("# ").AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("煤层,阶段1·年产量(万t),阶段2·增量(万t),合计(万t),前界d_seam(m)");
            foreach (var r in _resultRows) sb.Append(Q(r.Seam)).Append(',').Append(Q(r.Stage1Wt)).Append(',').Append(Q(r.Stage2Wt)).Append(',').Append(Q(r.TotalWt)).Append(',').AppendLine(Q(r.DSeam));
            System.IO.File.WriteAllText(file.Path.LocalPath, sb.ToString(), new System.Text.UTF8Encoding(true));
            await BlockMsgBox.InfoAsync(this, "导出 CSV", "已导出：\n" + file.Path.LocalPath);
        }
        catch (Exception ex) { Warn("导出失败：" + ex.Message); }
    }
    private static string Q(string s) => (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ── 加载块体（预加载、不显示）──
    private async Task OnLoadBlockAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "加载块体（预加载，不显示）", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("块体模型 (*.pmb;*.blk)") { Patterns = new[] { "*.pmb", "*.blk" } } } });
            if (files == null || files.Count == 0) return;
            string path = files[0].Path.LocalPath;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            BlockModelMeta meta;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (ext == ".blk")
            {
                var r = await Task.Run(() => BlkImportService.Load(path));
                if (!r.Success) { Warn("读取块体失败：" + r.Error); return; }
                meta = await Task.Run(() => BlockModelMeta.FromLeaves(BlockModelStore.UniqueName(baseName), r.Blocks, r.AllAttrs.Count > 0 ? r.AllAttrs : null, r.Ox, r.Oy, r.Oz, r.Bx, r.By, r.Bz, r.Nx, r.Ny, r.Nz, r.MaxSub, r.VarCellCount));
                ImportBlockModelWindow.ApplyBlkMetadata(meta, r);
            }
            else if (ext == ".pmb")
            {
                var r = await Task.Run(() => PmbImportService.Load(path));
                if (!r.Success) { Warn("读取块体失败：" + r.Error); return; }
                if (!string.IsNullOrWhiteSpace(r.ModelName)) baseName = r.ModelName;
                meta = await Task.Run(() => BlockModelMeta.FromBlocks(BlockModelStore.UniqueName(baseName), r.Blocks, r.AllAttrs.Count > 0 ? r.AllAttrs : null));
                ImportBlockModelWindow.ApplyPmbMetadata(meta, r);
            }
            else { Warn("不支持的块体格式（仅 .pmb / .blk）。"); return; }
            if (meta.Blocks.Count == 0) { Warn("读取块体失败（空内容）。"); return; }
            meta.IsVisible = false;                    // 预加载 = 只载数据、不渲染
            BlockModelStore.Models.Add(meta);
            RefreshBlockNames();
            _cmbActiveBlock.SelectedItem = meta.Name;
            await BlockMsgBox.InfoAsync(this, "加载块体", $"已预加载块体「{meta.Name}」（{meta.RealBlockCount:N0} 块，不显示）。\n它已设为当前块体；请在各层选「属性」「类别」。");
        }
        catch (Exception ex) { Warn("加载块体异常：" + ex.Message); }
    }

    // ── 确认 / 生成台阶 ──
    internal InclineTemplateInput? BuildInput(out string? error)
    {
        error = null;
        if (Seams.Count == 0) { error = "请至少添加一层煤。"; return null; }
        if (!TryD(_txtAnnual, out double annual) || annual <= 0) { error = "年产量须为正数。"; return null; }
        double recovery = (TryD(_txtRecovery, out double rc) && rc >= 0) ? rc : 0;
        double thr = (TryD(_txtThreshold, out double t) && t > 0) ? t : 5.0;
        if (_currentSurface == null) { error = "请指定现状面（在视图中选中后点「选现状面」）。"; return null; }
        if (_workLines.Count == 0) { error = "请至少加载 1 条工作线（视图中选中后点「加载工作线」）。"; return null; }
        RefreshWorkLineGeometries();

        double coalThr = TryD(_txtCoalThreshold, out double ctv) ? ctv : 0;
        int coalMode = _cmbCoalMode.SelectedIndex < 0 ? 0 : _cmbCoalMode.SelectedIndex;
        double benchH = (TryD(_txtBenchH, out double bh) && bh > 0) ? bh : 15;
        double berm = (TryD(_txtBerm, out double bw) && bw >= 0) ? bw : 80;
        double finalA = (TryD(_txtFinalAngle, out double fa) && fa > 0 && fa < 90) ? fa : 10;
        double coalF = (TryD(_txtCoalFace, out double cf) && cf > 0 && cf < 90) ? cf : 65;
        double rockF = (TryD(_txtRockFace, out double rf) && rf > 0 && rf < 90) ? rf : 65;
        // 工作帮坡角由台阶几何反算：α = atan(benchH / (benchH/tan岩坡角 + berm))，量/卡量/台阶同一 α
        double alphaBench = Math.Atan(benchH / (benchH / Math.Tan(rockF * Math.PI / 180.0) + Math.Max(0.0, berm))) * 180.0 / Math.PI;
        alphaBench = Math.Max(1.0, Math.Min(89.0, alphaBench));
        _txtAlpha.Text = alphaBench.ToString("0.##", CultureInfo.InvariantCulture);
        string regSel = _cmbRegion.SelectedItem as string ?? "";
        string regName = regSel.StartsWith("(自动", StringComparison.Ordinal) ? "" : regSel;

        var (cv, ct) = _currentSurface.Flatten();
        var input = new InclineTemplateInput
        {
            CurrentSurfaceVerts = cv, CurrentSurfaceTris = ct, CurrentSurfaceName = _currentSurface.Name,
            RegionName = regName, BlockModelName = _activeBlock?.Name ?? "",
            SlopeAngleDeg = alphaBench, AnnualProductionWt = annual, RecoveryTotalWt = recovery, ApproachThresholdPct = thr,
            CoalMode = coalMode, CoalThreshold = coalThr,
            BenchHeight = benchH, MinBermWidth = berm, FinalSlopeAngleDeg = finalA, CoalFaceDeg = coalF, RockFaceDeg = rockF,
            ConstrainBlockModel = _chkConstrain.IsChecked == true,
            ConstraintFrontMode = _cmbFrontMode.SelectedIndex < 0 ? 0 : _cmbFrontMode.SelectedIndex,
        };
        foreach (var (geo, _) in _workLines) input.WorkLines.Add(geo);
        foreach (var vm in Seams)
        {
            double[]? rv = vm.RoofVerts; int[]? rt = vm.RoofTris; string rs = rv != null ? "地质库" : "";
            if (vm.RoofMesh != null) { var (v, tt) = vm.RoofMesh.Flatten(); rv = v; rt = tt; rs = vm.RoofMesh.Name; }
            double[]? fv = vm.FloorVerts; int[]? ft = vm.FloorTris; string fs = fv != null ? "地质库" : "";
            if (vm.FloorMesh != null) { var (v, tt) = vm.FloorMesh.Flatten(); fv = v; ft = tt; fs = vm.FloorMesh.Name; }
            input.Seams.Add(new InclineSeamInput
            {
                Name = string.IsNullOrWhiteSpace(vm.Name) ? $"煤{input.Seams.Count + 1}" : vm.Name,
                RoofVerts = rv, RoofTris = rt, FloorVerts = fv, FloorTris = ft, RoofSource = rs, FloorSource = fs,
                Attribute = vm.Attribute ?? "", Category = vm.Category ?? "",
                Density = vm.Density > 0 ? vm.Density : 1.35, IncrementWt = vm.IncrementWt,
                BenchHeight = (vm.FullHeight || vm.BenchH <= 0) ? 0 : vm.BenchH,
            });
        }
        return input;
    }

    private void OnOkClick()
    {
        var input = BuildInput(out string? err);
        if (input == null) { Warn(err ?? "输入无效。"); return; }
        try { OnConfirmed?.Invoke(input); _h.Confirmed?.Invoke(input, this); }
        catch (Exception ex) { Warn("驱动量 异常：" + ex.GetType().Name + ": " + ex.Message); }
        // 不 Close()：本窗保持打开，采出煤量表就地显示在下方结果区；可继续调参数重「确认」刷新。
    }

    internal BenchTemplateParams ReadBenchParams() => new()
    {
        RockBenchH = (TryD(_txtBenchH, out double bh) && bh > 0) ? bh : 15,
        MinBerm = (TryD(_txtBerm, out double bw) && bw >= 0) ? bw : 80,
        WorkAngleDeg = (TryD(_txtFinalAngle, out double fa) && fa > 0 && fa < 90) ? fa : 10,
        CoalFaceDeg = (TryD(_txtCoalFace, out double cf) && cf > 0 && cf < 90) ? cf : 65,
        RockFaceDeg = (TryD(_txtRockFace, out double rf) && rf > 0 && rf < 90) ? rf : 65,
        CoalBenchHBySeam = Seams.Select(v => v.FullHeight ? -1.0 : (v.BenchH > 0 ? v.BenchH : 0.0)).ToArray(),
        JoinEndWall = _chkJoinEndWall.IsChecked == true,
    };

    private void OnGenerateBenchClick()
    {
        if (OnGenerateBenchTemplate == null) { Warn("请先「确认」跑一次驱动量（解出前界+煤层），再生成采区台阶面。"); return; }
        try { OnGenerateBenchTemplate(ReadBenchParams()); }
        catch (Exception ex) { Warn("生成采区台阶面异常：" + ex.Message); }
        RestoreAllHidden();   // 算完放开实体级隐藏，控制权交还图层
    }
    internal void SelftestGenerateBench() => OnGenerateBenchClick();

    // ── 自检辅助 ──
    internal void SelftestSetSeams(params string[] names)
    {
        foreach (var vm in Seams.ToList()) { ShowEnt(vm.RoofMesh); ShowEnt(vm.FloorMesh); }
        Seams.Clear(); _seamHost.Children.Clear();
        foreach (var n in names) AddSeam(new SeamVM { Name = n });
    }
    internal void SelftestSetSeamGeometry(int idx, MeshEntity roof, MeshEntity floor)
    {
        if (idx < 0 || idx >= Seams.Count) return;
        var vm = Seams[idx]; vm.RoofMesh = roof; vm.FloorMesh = floor; vm.RoofVerts = null; vm.FloorVerts = null;
        vm.BtnRoof.Content = vm.RoofLabel; vm.BtnFloor.Content = vm.FloorLabel;
    }
    internal void SelftestSetSeamRule(int idx, string attr, string category, double density, double incWt)
    {
        if (idx < 0 || idx >= Seams.Count) return;
        var vm = Seams[idx];
        vm.Attribute = attr; vm.CmbAttr.SelectedItem = attr; RefreshCategories(vm);
        vm.Category = category; vm.CmbCat.SelectedItem = category;
        vm.Density = density; vm.TxtDensity.Text = density.ToString("0.##", CultureInfo.InvariantCulture);
        vm.IncrementWt = incWt; vm.TxtInc.Text = incWt.ToString("0.#", CultureInfo.InvariantCulture);
    }
    internal void SelftestSetCurrentSurface(MeshEntity m) { _currentSurface = m; HideEnt(m); _txtCurrentSurface.Text = $"✓ 已选（{m.Name}）"; }
    internal void SelftestAddWorkLine(WorkLineSamples g, PolylineEntity l) { _workLines.Add((g, l)); HideEnt(l); _txtWorkLine.Text = $"✓ 已加载 {_workLines.Count} 条"; }
    internal void SelftestSetGlobals(double annual, double recovery, double tol, int coalMode, double coalThr, double benchH, double berm, bool constrain)
    {
        _txtAnnual.Text = annual.ToString(CultureInfo.InvariantCulture); _txtRecovery.Text = recovery.ToString(CultureInfo.InvariantCulture); _txtThreshold.Text = tol.ToString(CultureInfo.InvariantCulture);
        _cmbCoalMode.SelectedIndex = coalMode; _txtCoalThreshold.Text = coalThr.ToString(CultureInfo.InvariantCulture);
        _txtBenchH.Text = benchH.ToString(CultureInfo.InvariantCulture); _txtBerm.Text = berm.ToString(CultureInfo.InvariantCulture);
        _chkConstrain.IsChecked = constrain;
    }
    internal void SelftestSelectBlock(string name) { _cmbActiveBlock.SelectedItem = name; }
    internal void SelftestConfirm() => OnOkClick();
    internal void SelftestScrollTop() => Dispatcher.UIThread.Post(() => _scroll.ScrollToHome(), DispatcherPriority.Background);
    internal void SelftestAddControlLine() => AddControlLine();
    internal int SeamCount => Seams.Count;
    internal int WorkLineCount => _workLines.Count;
    internal int BoundaryCount => _boundaries.Count;
    internal string AlphaText => _txtAlpha.Text ?? "";

    private void Warn(string msg) { _h.Echo?.Invoke("驱动量：" + msg, true); _ = BlockMsgBox.WarnAsync(this, "驱动量 · 量驱动斜面模板", msg); }
}

/// <summary>「驱动量」产物图层名。</summary>
internal static class InclineLayers
{
    public const string Surface = "驱动量_斜面";
    public const string Stage2 = "驱动量_Stage2";
    public const string Control = "驱动量_控制线";
    public const string BenchTemplate = "创建工程位置_台阶";
}
