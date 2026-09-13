using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Views.GeoDb;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「采场/排土场圈定」窗口（移植原 <c>ShortTermMineableAreaWindow</c>）：采场/排土场区域管理。
/// 自动识别 + 手动圈画 → 存数据库（mineable_region）→ 按类着色 overlay；选中高亮；「笔刷编辑」涂抹增删边界（涂空白处扩、涂区域内擦），替代夹点。无三量统计。
/// </summary>
internal sealed class MineableAreaWindow : Window
{
    private readonly IPlanEntityHost _host;
    private readonly ObservableCollection<RegionRow> _rows = new();
    private readonly List<double> _drawPts = new();   // 圈画累积的扁平 xyz
    private bool _drawing;
    private bool _editing;                            // 区域 overlay 笔刷编辑中
    private long _brushTargetId = -1;                 // 笔刷正在编辑的区域 id（单选区）
    private int _brushPx = 16;                        // 笔刷半径(px)，面板 +/- 调
    private bool _dbReady;

    private readonly DataGrid _regionList;
    private readonly ComboBox _categoryBox;
    private readonly TextBox _regionNameBox = new() { Margin = new Thickness(0, 0, 0, 4), Watermark = "选中区域后改名" };
    private readonly TextBlock _drawHint = RoadUi.Hint("", 11.5), _status = RoadUi.Hint("", 12);
    private readonly Button _autoIdBtn, _clearIdBtn, _drawBtn, _finishBtn, _cancelDrawBtn, _editBtn, _commitEditBtn, _cancelEditBtn;
    private readonly Modeling.ColorSwatchPicker _colorPicker = new();
    private bool _colorSyncing;

    private const string DefaultHint =
        "圈画：先在右侧选好【类别】（采场 / 外排 / 内排 / 两个工作帮），再点「圈画新区域」到视口逐点左键点出边界顶点，右键结束并闭合成区域入库（普通多段线，不平滑）。放弃本次点「取消圈画」。" +
        "类别会跟着区域一路传到「两期填挖方」——采场默认挖填都算、排土场默认只算填方。" +
        "两个【工作帮】是范围闸门（本期在哪儿干），给「采矿模型」「排土条带」用；工作帮往前推了就选中它用笔刷改边界，不必每期新建一条。" +
        "工作帮只与**自己的母范围**重叠时不做扣除（剥采工作帮↔采场、排土工作帮↔排土场）；剥采工作帮压到排土场上、排土工作帮压到采场上仍会照常裁开。";

    /// <summary>类别下拉索引 → 数据库 category 值。⚠ mineable 必须留在末位（认不出就回落不分类）。</summary>
    private static readonly string[] CategoryKeys =
    {
        MineableRegion.CatPit, MineableRegion.CatExternalDump, MineableRegion.CatInternalDump,
        MineableRegion.CatPitWorkingSlope, MineableRegion.CatDumpWorkingSlope,
        MineableRegion.CatMineable,        // ⚠ 必须留在末位（回落值取 Length-1）
    };

    private string SelectedCategoryKey
    {
        get { int i = _categoryBox?.SelectedIndex ?? -1; return i >= 0 && i < CategoryKeys.Length ? CategoryKeys[i] : MineableRegion.CatMineable; }
    }

    public MineableAreaWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "采场/排土场圈定 — 识别 / 笔刷编辑 / 颜色";
        PlanUi.Place(this, 860, 660);
        MinWidth = 700; MinHeight = 480;

        _regionList = PlanUi.Table(new (string, string, double)[] { ("色", "ColorText", 44), ("名称", "Name", 160), ("类别", "CategoryText", 96), ("顶点", "PointCount", 64), ("显示", "VisibleText", 64) }, multi: false);
        _regionList.Columns[0] = new DataGridTemplateColumn
        {
            Header = "色", Width = new DataGridLength(48),
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RegionRow>((r, _) => new Border { Width = 20, Height = 14, CornerRadius = new CornerRadius(3), Background = r?.ColorBrush, BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }),
        };
        _regionList.Columns[1].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        _regionList.ItemsSource = _rows;
        _regionList.SelectionChanged += (_, _) => OnRegionSelected();
        _regionList.DoubleTapped += (_, _) => { if (Selected != null) OnEditColorAsync(); };

        _categoryBox = PlanUi.Combo(CategoryKeys.Select(CategoryZh), 0, double.NaN);
        _categoryBox.Margin = new Thickness(0, 0, 0, 4);
        _categoryBox.HorizontalAlignment = HorizontalAlignment.Stretch;

        _autoIdBtn = SideBtn("自动识别采场/排土场", () => _ = OnAutoIdentifyAsync(), "读图中现状线（优先视口选中的），按线的几何关系（高程 + 空间相邻/嵌套 → 凹凸极性）圈出采场/外排/内排，入库后可逐块改名/显隐/删除/笔刷微调");
        _clearIdBtn = SideBtn("清空识别结果", () => _ = OnClearIdentifiedAsync(), "清掉自动识别出的采场/外排/内排；手动圈画的可采区域保留");
        _drawBtn = SideBtn("圈画新区域", OnDrawRegion);
        _finishBtn = SideBtn("完成圈画", OnFinishDraw); _finishBtn.IsEnabled = false;
        _cancelDrawBtn = SideBtn("取消圈画", OnCancelDraw); _cancelDrawBtn.IsEnabled = false;
        _editBtn = SideBtn("笔刷编辑（开）", OnBrushEdit, "对选中的一个区域当选区涂改：Alt+拖=并入(扩)，普通拖=移出(缩)；边界实时更新，完成后写库");
        _commitEditBtn = SideBtn("完成", () => _host.EndRegionBrushEdit(true)); _commitEditBtn.IsEnabled = false;
        _cancelEditBtn = SideBtn("取消", () => _host.EndRegionBrushEdit(false)); _cancelEditBtn.IsEnabled = false;

        Content = BuildLayout();
        _drawHint.Text = DefaultHint;
        _colorPicker.SelectedColorChanged += (_, c) => { if (!_colorSyncing) ApplyColor(c); };

        LoadRegions();
        Closed += (_, _) => OnWindowClosed();
    }

    private static Button SideBtn(string text, Action a, string? tip = null)
    {
        var b = RoadUi.Btn(text, a, 0);
        b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Center; b.Margin = new Thickness(0, 0, 0, 4);
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }

    private Control BuildLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = PlanUi.Header("采场/排土场圈定", "自动识别采场/排土场 · 圈画 · 笔刷涂抹编辑 · 按类着色（颜色可改）", Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x1E, 0x40, 0xAF));
        Grid.SetRow(header, 0); root.Children.Add(header);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,236"), Margin = new Thickness(12, 10, 12, 10) };
        var left = new DockPanel();
        var lt = RoadUi.Text("区域列表", 12, bold: true); DockPanel.SetDock(lt, Avalonia.Controls.Dock.Top); left.Children.Add(lt);
        _drawHint.Margin = new Thickness(2, 8, 0, 0); DockPanel.SetDock(_drawHint, Avalonia.Controls.Dock.Bottom); left.Children.Add(_drawHint);
        left.Children.Add(_regionList);
        Grid.SetColumn(left, 0); body.Children.Add(left);

        var side = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        side.Children.Add(RoadUi.Hint("识别", 12));
        side.Children.Add(_autoIdBtn); side.Children.Add(_clearIdBtn);
        side.Children.Add(RoadUi.Hint("圈画 / 区域", 12));
        side.Children.Add(RoadUi.Hint("类别（新圈画的区域按此入库）", 11));
        side.Children.Add(_categoryBox);
        var applyCat = SideBtn("应用类别", OnApplyCategory, "把下拉里的类别写到选中区域（类别决定「两期填挖方」默认填挖口径）"); side.Children.Add(applyCat);
        side.Children.Add(_drawBtn);
        side.Children.Add(Pair(_finishBtn, _cancelDrawBtn));
        side.Children.Add(_regionNameBox);
        side.Children.Add(Pair(SideBtn("应用名称", OnRenameRegion), SideBtn("显示/隐藏", OnToggleVisible)));
        var del = SideBtn("删除区域", () => _ = OnDeleteRegionAsync()); del.Margin = new Thickness(0, 0, 0, 10); side.Children.Add(del);
        side.Children.Add(RoadUi.Hint("颜色", 12));
        _colorPicker.Margin = new Thickness(0, 0, 0, 4); side.Children.Add(_colorPicker);
        side.Children.Add(Pair(SideBtn("改颜色…", () => OnEditColorAsync(), "为选中区域选色：选色即写库并刷新 overlay（双击列表行同）"), SideBtn("恢复类别色", OnResetColor, "清掉自定义颜色，回到该类别默认色")));
        var legend = new StackPanel();
        legend.Children.Add(RoadUi.Hint("类别默认色", 11));
        foreach (var k in CategoryKeys) legend.Children.Add(LegendRow(CategoryColor(k), CategoryZh(k)));
        side.Children.Add(PlanUi.InfoBox(legend, new Thickness(0, 0, 0, 10)));
        side.Children.Add(RoadUi.Hint("笔刷编辑", 12));
        side.Children.Add(_editBtn);
        side.Children.Add(Pair(SideBtn("笔刷 －", () => SetBrush(_brushPx - 6)), SideBtn("笔刷 ＋", () => SetBrush(_brushPx + 6))));
        side.Children.Add(Pair(_commitEditBtn, _cancelEditBtn));
        side.Children.Add(RoadUi.Hint("Alt+拖=增选 · 普通拖=减选", 11));
        var sv = new ScrollViewer { Content = side, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetColumn(sv, 1); body.Children.Add(sv);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var foot = new DockPanel();
        var close = RoadUi.Btn("关闭", Close, 70); DockPanel.SetDock(close, Avalonia.Controls.Dock.Right); foot.Children.Add(close);
        _status.VerticalAlignment = VerticalAlignment.Center; _status.Margin = new Thickness(2, 0, 8, 0); foot.Children.Add(_status);
        var footB = PlanUi.Footer(foot);
        Grid.SetRow(footB, 2); root.Children.Add(footB);
        return root;
    }

    private static Control Pair(Control a, Control b)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        if (a is Control ca) { ca.Margin = new Thickness(0, 0, 3, 4); Grid.SetColumn(ca, 0); g.Children.Add(ca); }
        if (b is Control cb) { cb.Margin = new Thickness(0, 0, 0, 4); Grid.SetColumn(cb, 1); g.Children.Add(cb); }
        return g;
    }

    private static Control LegendRow(uint rgb, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1) };
        sp.Children.Add(new Border { Width = 16, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(ToColor(rgb)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
        var t = RoadUi.Hint(text, 11); t.Margin = new Thickness(6, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; sp.Children.Add(t);
        return sp;
    }

    // ════════════════════ 可采区域边界 ════════════════════

    private void LoadRegions()
    {
        _rows.Clear();
        _dbReady = _host.Db != null;
        if (!_dbReady)
        {
            _drawHint.Text = "（GeoDataBase 未就绪，可采区域边界不可用 —— 请先连接数据库）";
            _drawBtn.IsEnabled = false;
            return;
        }
        foreach (var r in MineableRegionRepo.All(_host.Db))
            _rows.Add(new RegionRow(r) { PointCount = ParsePoints(r.PointsJson).Length / 3 });
        RefreshOverlay();
    }

    private RegionRow? Selected => _regionList.SelectedItem as RegionRow;

    private void OnRegionSelected()
    {
        if (Selected is { } row)
        {
            _regionNameBox.Text = row.Entity.Name;
            // 下拉回填成选中区域的现类别：既是"看当前是什么"，也是改判的起点。
            int i = Array.IndexOf(CategoryKeys, row.Entity.Category ?? MineableRegion.CatMineable);
            _categoryBox.SelectedIndex = i >= 0 ? i : CategoryKeys.Length - 1;
            _colorSyncing = true; _colorPicker.SelectedColor = ToColor(EffectiveColor(row.Entity)); _colorSyncing = false;
        }
        if (!_editing) RefreshOverlay();   // 选中区域 → 边界高亮
    }

    /// <summary>「应用类别」：把下拉里的类别写到选中区域。类别一路传到「两期填挖方」决定默认填挖口径，手工圈画的区域也必须能标。</summary>
    private void OnApplyCategory()
    {
        if (!_dbReady) { _status.Text = "GeoDataBase 未就绪。"; return; }
        if (_editing) { _status.Text = "请先结束笔刷编辑再改类别。"; return; }
        if (Selected is not { } row) { _status.Text = "请先在列表里选中一个区域，再点「应用类别」。"; return; }
        string key = SelectedCategoryKey;
        if (string.Equals(row.Entity.Category, key, StringComparison.Ordinal)) { _status.Text = $"「{row.Entity.Name}」已经是{CategoryZh(key)}，未改动。"; return; }
        row.Entity.Category = key;
        MineableRegionRepo.Update(_host.Db, row.Entity);
        LoadRegions();
        _status.Text = $"「{row.Entity.Name}」已改判为{CategoryZh(key)}——「两期填挖方」会按此定默认填挖口径。";
    }

    private void OnDrawRegion()
    {
        if (!_dbReady) return;
        _drawPts.Clear();
        // 右键 / Esc → 宿主退出取点并回调 onCancel：在此把累积点闭合成区域（右键 = 结束并成区域）。
        bool ok = _host.BeginScreenPointPick((x, y, z) => OnPointPicked(x, y, z), OnDrawFinishedByViewport);
        if (!ok) { _status.Text = "当前宿主不支持视口取点（非交互宿主）"; return; }
        _drawing = true;
        _drawBtn.IsEnabled = false; _finishBtn.IsEnabled = true; _cancelDrawBtn.IsEnabled = true;
        _drawHint.Text = "正在圈画：视口左键逐点点出边界顶点；右键结束并成区域。（窗口内「取消圈画」放弃本次）";
    }

    private void OnPointPicked(double x, double y, double z)
    {
        _drawPts.Add(x); _drawPts.Add(y); _drawPts.Add(z);
        _drawHint.Text = $"已取 {_drawPts.Count / 3} 个点…（至少 3 个点可成区域）";
        PreviewDraw();
    }

    /// <summary>「完成圈画」按钮：主动退出取点 → 把累积点闭合成区域。</summary>
    private void OnFinishDraw()
    {
        if (!_drawing) return;
        EndDrawSession();
        _ = CommitDrawnRegionAsync();
    }

    /// <summary>视口右键 / Esc：宿主已退出取点 → 把累积点闭合成区域。</summary>
    private void OnDrawFinishedByViewport()
    {
        if (!_drawing) return;
        _drawing = false;
        _drawBtn.IsEnabled = true; _finishBtn.IsEnabled = false; _cancelDrawBtn.IsEnabled = false;
        _host.ClearScreenPickMarkers();
        _drawHint.Text = DefaultHint;
        _ = CommitDrawnRegionAsync();
    }

    /// <summary>用累积的 _drawPts 闭合成一个可采区域入库；点数不足则放弃。</summary>
    private async Task CommitDrawnRegionAsync()
    {
        if (_drawPts.Count < 9)   // < 3 个点
        {
            _status.Text = "至少需要 3 个点才能成区域，已放弃本次圈画";
            _drawPts.Clear(); RefreshOverlay(); return;
        }
        if (!_dbReady) { _drawPts.Clear(); return; }
        var flat = _drawPts.ToArray();
        _drawPts.Clear();

        // 类别取右侧下拉：手工圈的区域也带采场/排土场身份入库，名字跟着类别走（采场1 / 外排土场1…）。
        string category = SelectedCategoryKey;
        string name = NextRegionName(category);
        var (finalRing, overlapped) = await ResolveSpatialUniquenessAsync(flat, -1, name, category);
        if (finalRing == null || finalRing.Length < 9) { _status.Text = $"「{name}」与已有区域完全重叠且选择以已有为主 → 未新增。"; RefreshOverlay(); return; }

        var entity = new MineableRegion { Name = name, Category = category, PointsJson = SerializePoints(finalRing), Visible = 1 };
        MineableRegionRepo.Insert(_host.Db, entity, out string err);
        if (err.Length > 0) { _status.Text = "入库失败：" + err; RefreshOverlay(); return; }
        LoadRegions();
        if (!overlapped && MineableRegion.IsWorkingSlope(category))
            _status.Text = $"已新增{CategoryZh(category)}「{name}」（{finalRing.Length / 3} 点）——与自己的母范围重叠不做扣除；压到另一门类（{(MineableRegion.IsPitGate(category) ? "排土场" : "采场")}）上仍会照常裁开。";
        else if (!overlapped) _status.Text = $"已新增{CategoryZh(category)}「{name}」（{finalRing.Length / 3} 点）——「两期填挖方」的区域清单里现在就能选到它。";
        _host.Echo($"采场/排土场圈定：已新增{CategoryZh(category)}「{name}」（{finalRing.Length / 3} 点）", false);
    }

    private void OnCancelDraw()
    {
        if (!_drawing) return;
        EndDrawSession();
        _drawPts.Clear();
        RefreshOverlay();
        _status.Text = "已取消圈画";
    }

    private void EndDrawSession()
    {
        _drawing = false;
        _drawBtn.IsEnabled = true; _finishBtn.IsEnabled = false; _cancelDrawBtn.IsEnabled = false;
        _host.EndScreenPointPick();
        _host.ClearScreenPickMarkers();
        _drawHint.Text = DefaultHint;
    }

    private void OnRenameRegion()
    {
        if (Selected is not { } row || !_dbReady) return;
        string name = (_regionNameBox.Text ?? "").Trim();
        if (name.Length == 0) { _status.Text = "名称不能为空"; return; }
        row.Entity.Name = name;
        MineableRegionRepo.Update(_host.Db, row.Entity);
        LoadRegions();
        _status.Text = $"已重命名为「{name}」";
    }

    private void OnToggleVisible()
    {
        if (Selected is not { } row || !_dbReady) return;
        row.Entity.Visible = row.Entity.Visible != 0 ? 0 : 1;
        MineableRegionRepo.Update(_host.Db, row.Entity);
        LoadRegions();
        _status.Text = $"「{row.Entity.Name}」{(row.Entity.Visible != 0 ? "已显示" : "已隐藏")}";
    }

    private async Task OnDeleteRegionAsync()
    {
        if (Selected is not { } row || !_dbReady) return;
        if (!await CoalMsgBox.ConfirmAsync(this, "采场/排土场圈定", $"删除可采区域「{row.Entity.Name}」？")) return;
        MineableRegionRepo.Delete(_host.Db, row.Entity.Id);
        LoadRegions();
        _status.Text = $"已删除「{row.Entity.Name}」";
    }

    // ════════════════════ 颜色（按类着色 + 用户自定义 overlay 颜色，存数据库）════════════════════

    /// <summary>「改颜色…」/ 双击列表行：打开色板；选色即写库并刷新 overlay。</summary>
    private void OnEditColorAsync()
    {
        if (!_dbReady) { _status.Text = "GeoDataBase 未就绪。"; return; }
        if (_editing) { _status.Text = "请先结束笔刷编辑再改颜色。"; return; }
        if (Selected is not { } row) { _status.Text = "请先在列表里选中一个区域，再改颜色。"; return; }
        _colorSyncing = true; _colorPicker.SelectedColor = ToColor(EffectiveColor(row.Entity)); _colorSyncing = false;
        _status.Text = $"在「颜色」色板里为「{row.Entity.Name}」选色（或直接改 Hex）→ 选色即写库。";
    }

    private void ApplyColor(Color c)
    {
        if (!_dbReady || _editing || Selected is not { } row) return;
        string hex = $"{c.R:X2}{c.G:X2}{c.B:X2}";
        if (string.Equals(row.Entity.Color, hex, StringComparison.OrdinalIgnoreCase)) return;
        row.Entity.Color = hex;
        MineableRegionRepo.Update(_host.Db, row.Entity);
        var keep = row.Entity.Id;
        LoadRegions();
        _regionList.SelectedItem = _rows.FirstOrDefault(r => r.Entity.Id == keep);
        _status.Text = $"「{row.Entity.Name}」颜色已改为 #{hex}。";
    }

    /// <summary>「恢复类别色」：清掉自定义颜色，回到该类别默认色。</summary>
    private void OnResetColor()
    {
        if (!_dbReady) return;
        if (Selected is not { } row) { _status.Text = "请先选中一个区域。"; return; }
        if (string.IsNullOrEmpty(row.Entity.Color)) { _status.Text = "该区域已是类别默认色。"; return; }
        row.Entity.Color = null;
        MineableRegionRepo.Update(_host.Db, row.Entity);
        var keep = row.Entity.Id;
        LoadRegions();
        _regionList.SelectedItem = _rows.FirstOrDefault(r => r.Entity.Id == keep);
        _status.Text = $"「{row.Entity.Name}」已恢复为类别默认色。";
    }

    /// <summary>区域生效色：有自定义色用自定义，否则按类别默认色。</summary>
    private static uint EffectiveColor(MineableRegion e) => TryParseHexColor(e.Color, out uint rgb) ? rgb : CategoryColor(e.Category);

    private static bool TryParseHexColor(string? hex, out uint rgb)
    {
        rgb = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb) && hex.Length is 6 or 8;
    }

    private static Color ToColor(uint rgb) => Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    // ════════════════════ 笔刷编辑（涂抹增删边界 → 完成记录，替代夹点）════════════════════

    /// <summary>开关：对【选中的一个区域】当选区涂改——Alt+拖=并入(扩)，普通拖=移出(缩)；笔刷圆圈跟随、边界实时更新。</summary>
    private void OnBrushEdit()
    {
        if (_editing) return;
        if (!_dbReady) { _status.Text = "GeoDataBase 未就绪。"; return; }
        if (Selected is not { } sel) { _status.Text = "请先在列表里选中一个区域，再开笔刷（笔刷只改选中的那个区域）。"; return; }
        var target = ParsePoints(sel.Entity.PointsJson);
        if (target.Length < 9) { _status.Text = "选中区域无有效边界。"; return; }
        uint targetColor = EffectiveColor(sel.Entity);

        var ctx = new List<double[]>(); var ctxC = new List<uint>();   // 其它区域作背景
        foreach (var row in _rows)
        {
            if (row == sel || row.Entity.Visible == 0) continue;
            var f = ParsePoints(row.Entity.PointsJson);
            if (f.Length >= 9) { ctx.Add(f); ctxC.Add(EffectiveColor(row.Entity)); }
        }
        bool ok = _host.BeginRegionBrushEdit(target, targetColor, ctx, ctxC, OnBrushCommitted, OnBrushCancelled);
        if (!ok) { _status.Text = "当前宿主不支持选区笔刷。"; return; }

        _brushTargetId = sel.Entity.Id;
        _editing = true;
        _editBtn.IsEnabled = false; _commitEditBtn.IsEnabled = true; _cancelEditBtn.IsEnabled = true;
        _drawBtn.IsEnabled = false; _autoIdBtn.IsEnabled = false; _clearIdBtn.IsEnabled = false;
        _status.Text = $"笔刷编辑「{sel.Entity.Name}」：视口左键涂改——Alt+拖=并入(扩)，普通拖=移出(缩)；「笔刷±」调大小。完成/取消(或视口 Esc)。";
    }

    /// <summary>宿主回调：选区涂抹完成 → 回写选中区域；选区擦空则删除该区域；与其它区域重叠时提示「以哪个为主」并裁剪，保证空间唯一。</summary>
    private void OnBrushCommitted(double[] editedRing) => _ = OnBrushCommittedAsync(editedRing);

    private async Task OnBrushCommittedAsync(double[] editedRing)
    {
        if (_dbReady && _brushTargetId >= 0)
        {
            var row = _rows.FirstOrDefault(r => r.Entity.Id == _brushTargetId);
            if (row != null)
            {
                if (editedRing == null || editedRing.Length < 9)
                { MineableRegionRepo.Delete(_host.Db, row.Entity.Id); _status.Text = "选区已擦空 → 该区域已删除。"; }
                else
                {
                    var (finalRing, overlapped) = await ResolveSpatialUniquenessAsync(editedRing, row.Entity.Id, row.Entity.Name, row.Entity.Category);
                    if (finalRing == null || finalRing.Length < 9)
                    { MineableRegionRepo.Delete(_host.Db, row.Entity.Id); _status.Text = $"「{row.Entity.Name}」被已有区域完全覆盖 → 已删除。"; }
                    else
                    {
                        row.Entity.PointsJson = SerializePoints(finalRing);
                        MineableRegionRepo.Update(_host.Db, row.Entity);
                        if (!overlapped) _status.Text = "已记录选区编辑。";
                    }
                }
            }
        }
        EndEditMode();
        LoadRegions();
    }

    /// <summary>
    /// 保证空间唯一：把 selfRing 与其它区域比对，若有「成片」重叠则提示「以哪个为主」，裁剪非主区域并即时写库。
    /// 返回最终的 self 边界（可能被裁小）；null=self 被完全覆盖。★ 豁免只有一对：工作帮 ↔ 它自己的母范围；跨门类照常扣除。
    /// </summary>
    private async Task<(double[]? ring, bool overlapped)> ResolveSpatialUniquenessAsync(double[] selfRing, long selfId, string selfName, string? selfCategory)
    {
        if (!_dbReady) return (selfRing, false);
        var overlaps = new List<RegionRow>();
        foreach (var other in _rows)
        {
            if (other.Entity.Id == selfId) continue;   // 与全部区域(含隐藏的)逐个比对，保证空间唯一
            if (MineableRegion.IsGateOverItsParent(selfCategory, other.Entity.Category)) continue;   // 工作帮落在自己的母范围里 = 子区域，不扣除
            var op = ParsePoints(other.Entity.PointsJson);
            if (op.Length >= 9 && RegionGeometry.Overlaps(selfRing, op)) overlaps.Add(other);
        }
        if (overlaps.Count == 0) return (selfRing, false);

        string others = string.Join("、", overlaps.Select(o => $"「{o.Entity.Name}」"));
        string msg = overlaps.Count == 1
            ? $"「{selfName}」与{others}空间重叠。\n\n以哪个为主？（主区域保留完整形状，另一区域裁掉重叠部分）\n\n【以本区域为主】= 以「{selfName}」为主\n【以已有为主】= 以{others}为主\n【保留重叠】= 不裁剪"
            : $"「{selfName}」与 {overlaps.Count} 个已有区域（{others}）空间重叠。\n\n以哪个为主？\n\n【以本区域为主】= 以「{selfName}」为主，其余裁掉重叠\n【以已有为主】= 以已有区域为主，裁掉「{selfName}」的重叠\n【保留重叠】= 不裁剪";
        var ans = await CoalMsgBox.AskAsync(this, "区域重叠——保证空间唯一", msg, "以本区域为主", "以已有为主", "保留重叠");

        if (ans == null || ans == "保留重叠") { _status.Text = "已保留重叠（未裁剪）。"; return (selfRing, true); }
        if (ans == "以本区域为主")   // self 为主：裁掉其它区域与 self 的重叠
        {
            int clipped = 0, removed = 0;
            foreach (var o in overlaps)
            {
                var res = RegionGeometry.SubtractKeepLargest(ParsePoints(o.Entity.PointsJson), selfRing);
                if (res.Length < 9) { MineableRegionRepo.Delete(_host.Db, o.Entity.Id); removed++; }
                else { o.Entity.PointsJson = SerializePoints(res); MineableRegionRepo.Update(_host.Db, o.Entity); clipped++; }
            }
            _status.Text = $"以「{selfName}」为主：裁剪 {clipped} 个、移除 {removed} 个重叠区域。";
            return (selfRing, true);
        }
        // 以已有区域为主，裁掉 self 与各重叠区域的部分
        var cur = selfRing;
        foreach (var o in overlaps)
        {
            cur = RegionGeometry.SubtractKeepLargest(cur, ParsePoints(o.Entity.PointsJson));
            if (cur.Length < 9) return (null, true);   // self 被完全覆盖
        }
        _status.Text = $"以已有区域为主：已裁掉「{selfName}」与 {overlaps.Count} 个区域的重叠。";
        return (cur, true);
    }

    private void OnBrushCancelled()
    {
        EndEditMode();
        RefreshOverlay();
        _status.Text = "已退出笔刷编辑（未改动）。";
    }

    private void SetBrush(int px)
    {
        int v = _host.SetRegionBrushRadiusPx(px);
        if (v > 0) { _brushPx = v; _status.Text = $"笔刷半径 ≈ {v} px（Alt+拖=并入，普通拖=移出）"; }
    }

    private void EndEditMode()
    {
        _editing = false;
        _brushTargetId = -1;
        _editBtn.IsEnabled = true; _commitEditBtn.IsEnabled = false; _cancelEditBtn.IsEnabled = false;
        _drawBtn.IsEnabled = _dbReady; _autoIdBtn.IsEnabled = true; _clearIdBtn.IsEnabled = true;
    }

    /// <summary>把全部"显示中"的区域推到单色 overlay（圈画中时附带预览当前折线）。</summary>
    private void RefreshOverlay()
    {
        var (rings, colors) = CollectVisible();
        _host.ShowMineableAreaOverlay(rings, colors);
    }

    private void PreviewDraw()
    {
        var (rings, colors) = CollectVisible();
        if (_drawPts.Count >= 6) { rings.Add(_drawPts.ToArray()); colors.Add(0x26D973u); }   // 进行中(≥2 点)也画出来(绿)
        _host.ShowMineableAreaOverlay(rings, colors);
    }

    /// <summary>显示中的区域环 + 按类别配色（采橙/外蓝/内青/可采绿）；选中的区域高亮（亮黄）。</summary>
    private (List<double[]> rings, List<uint> colors) CollectVisible()
    {
        var sel = Selected;
        var rings = new List<double[]>(); var colors = new List<uint>();
        foreach (var row in _rows)
        {
            if (row.Entity.Visible == 0) continue;
            var flat = ParsePoints(row.Entity.PointsJson);
            if (flat.Length >= 9) { rings.Add(flat); colors.Add(row == sel ? 0xFFE000u : EffectiveColor(row.Entity)); }
        }
        return (rings, colors);
    }

    private static uint CategoryColor(string? category) => category switch
    {
        MineableRegion.CatPit => 0xD85A30u,              // 采场 橙
        MineableRegion.CatExternalDump => 0x2E6FCFu,     // 外排土场 蓝
        MineableRegion.CatInternalDump => 0x1D9E75u,     // 内排土场 青
        MineableRegion.CatPitWorkingSlope => 0xF2B14Au,  // 剥采工作帮 琥珀（采场橙的亮版）
        MineableRegion.CatDumpWorkingSlope => 0x6FB7F5u, // 排土工作帮 亮蓝（排土蓝的亮版）
        _ => 0x26D973u,                                  // 可采区域 绿
    };

    private void OnWindowClosed()
    {
        if (_drawing) { try { _host.EndScreenPointPick(); } catch { } }
        if (_editing) { try { _host.EndRegionBrushEdit(false); } catch { } }
        try { _host.ClearMineableAreaOverlay(); } catch { }
    }

    // ════════════════════ 自动识别采场 / 排土场（件一）════════════════════

    /// <summary>读现状线 → LandformClassifier 圈定 → 入库 → 走同一编辑/保存闭环。</summary>
    private async Task OnAutoIdentifyAsync()
    {
        if (!_dbReady) { _status.Text = "GeoDataBase 未就绪，无法入库。"; return; }
        if (_editing) { _status.Text = "请先结束「编辑区域」再自动识别。"; return; }

        // 自动识别前先清空列表里已有区域：保证结果干净、不与旧区域叠加相交。含手动圈画的也会清掉，故有内容时二次确认。
        if (_rows.Count > 0)
        {
            if (!SelftestAutoConfirm && !await CoalMsgBox.ConfirmAsync(this, "自动识别采场/排土场", $"自动识别会先清空当前 {_rows.Count} 个区域（含手动圈画的），再重新识别。是否继续？")) return;
            foreach (var id in _rows.Select(r => r.Entity.Id).ToList()) MineableRegionRepo.Delete(_host.Db, id);
            LoadRegions();
        }

        // 直接用现状线（不分图层）：优先视口选中的；没选中就用图中全部多段线。按线的几何关系识别，不依赖图层名。
        var lines = new List<double[]>();
        ReadSelectedLines(lines);
        bool fromSelection = lines.Count > 0;
        if (lines.Count == 0) ReadAllLines(lines);
        if (lines.Count == 0) { _status.Text = "图中没有现状线可用（请先有台阶线，或在视口选中要识别的线）。"; return; }

        var r = LandformClassifier.Classify(lines);
        if (!r.Ok) { _status.Text = r.Message; return; }

        int n = 0;
        var used = new HashSet<string>(_rows.Select(rw => rw.Entity.Name));
        foreach (var reg in r.Regions)
        {
            string name = NextNameFor(reg.Category, used);
            used.Add(name);
            var entity = new MineableRegion { Name = name, Category = reg.Category, PointsJson = SerializePoints(reg.PolygonXyz), Visible = 1 };
            MineableRegionRepo.Insert(_host.Db, entity, out _);
            n++;
        }
        LoadRegions();
        _status.Text = $"{r.Message}（用{(fromSelection ? "选中" : "全部")}现状线）已入库 {n} 块——可逐块改名/显隐/删除/视口微调后保存。";
        _host.Echo("采场/排土场圈定：" + _status.Text, false);
    }

    /// <summary>清空之前自动识别出的区域（采场/外排/内排）；手动圈画的可采区域保留。</summary>
    private async Task OnClearIdentifiedAsync()
    {
        if (!_dbReady) { _status.Text = "GeoDataBase 未就绪。"; return; }
        if (_editing) { _status.Text = "请先结束「编辑区域」再清空。"; return; }
        var ids = _rows.Where(r => r.Entity.Category is "pit" or "external_dump" or "internal_dump").Select(r => r.Entity.Id).ToList();
        if (ids.Count == 0) { _status.Text = "没有可清空的识别结果（采场/外排/内排）。"; return; }
        if (!await CoalMsgBox.ConfirmAsync(this, "清空识别结果", $"清空 {ids.Count} 个自动识别区域（采场/外排/内排）？\n手动圈画的可采区域会保留。")) return;
        foreach (var id in ids) MineableRegionRepo.Delete(_host.Db, id);
        LoadRegions();
        _status.Text = $"已清空 {ids.Count} 个识别区域（手动圈画的保留）。";
    }

    /// <summary>读视口选中的多段线作现状线（不依赖图层；按线的几何关系识别，不分顶/底）。</summary>
    private void ReadSelectedLines(List<double[]> outList)
    {
        foreach (var h in _host.SelectedHandles())
            if (_host.TryGetPolylineWorldVertices(h, out var xyz, out _) && xyz != null && xyz.Length >= 6) outList.Add(xyz);
    }

    /// <summary>读图中全部多段线作现状线（未选中时的默认；非台阶线靠面积/极性自然被滤掉）。</summary>
    private void ReadAllLines(List<double[]> outList)
    {
        foreach (var (h, _, _) in _host.ListEntities(PlanEntityType.Polyline))
            if (_host.TryGetPolylineWorldVertices(h, out var xyz, out _) && xyz != null && xyz.Length >= 6) outList.Add(xyz);
    }

    /// <summary>类别 → 中文名（只转发 <see cref="MineableRegion.DisplayName"/>）。</summary>
    private static string CategoryZh(string? category) => MineableRegion.DisplayName(category);

    private static string NextNameFor(string category, HashSet<string> used)
    {
        string prefix = CategoryZh(category);
        for (int i = 1; ; i++) { string nm = $"{prefix}{i}"; if (!used.Contains(nm)) return nm; }
    }

    private string NextRegionName(string category) => NextNameFor(category, new HashSet<string>(_rows.Select(r => r.Entity.Name)));

    private static string SerializePoints(double[] flat) => System.Text.Json.JsonSerializer.Serialize(flat);

    private static double[] ParsePoints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<double>();
        try { return System.Text.Json.JsonSerializer.Deserialize<double[]>(json) ?? Array.Empty<double>(); }
        catch { return Array.Empty<double>(); }
    }

    /// <summary>自检直通：按当前下拉类别用给定环直接入库（不走视口取点）。</summary>
    internal async Task SelftestAddRegionAsync(double[] ring) { _drawPts.Clear(); _drawPts.AddRange(ring); await CommitDrawnRegionAsync(); }
    internal int SelftestRowCount => _rows.Count;
    internal string SelftestStatus => _status.Text ?? "";
    internal void SelftestSelect(int index) { if (index >= 0 && index < _rows.Count) _regionList.SelectedIndex = index; }
    internal void SelftestBrush() => OnBrushEdit();
    internal bool SelftestAutoConfirm;
    internal Task SelftestAutoIdentifyAsync() => OnAutoIdentifyAsync();

    /// <summary>列表行视图模型（包数据库实体 + 派生显示列）。</summary>
    public sealed class RegionRow
    {
        public MineableRegion Entity { get; }
        public RegionRow(MineableRegion e) { Entity = e; ColorBrush = new SolidColorBrush(ToColor(EffectiveColor(e))); }
        public string Name => Entity.Name;
        public string CategoryText => CategoryZh(Entity.Category);
        public int PointCount { get; set; }
        public string VisibleText => Entity.Visible != 0 ? "显示" : "隐藏";
        public string ColorText => "■";
        public IBrush ColorBrush { get; }
    }
}
