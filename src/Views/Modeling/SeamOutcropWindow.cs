using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.SeamOutcrop;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「煤层露头着色」（移植原 <c>MineAssLib.SeamOutcrop.SeamOutcropDialog</c>）——
/// 露头 = 现状面高程夹在【底板 ≤ 现状 ≤ 顶板】的区域，逐顶点判定后把这些顶点染成该层煤色。
///
/// 判定与统计全在 <see cref="SeamOutcropRunner"/> / <see cref="SeamOutcropEngine"/>（纯函数），
/// 本窗只负责选面、配色、回显。
///
/// <b>层序即优先级</b>：先配的层先认领顶点。煤层空间上本不该重叠，真重叠了说明顶/底板选错了 ——
/// 逐层的"实际染上多少"如实回显，让人自己看出哪层被吃掉了。
/// </summary>
internal sealed class SeamOutcropWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>默认配色（多于此数就循环取）。</summary>
    private static readonly uint[] Palette =
        { 0xD9480F, 0x2B8A3E, 0x1971C2, 0x862E9C, 0xE8590C, 0x0C8599, 0x5F3DC4, 0xC2255C };

    /// <summary>表格一行 = 一层煤。</summary>
    internal sealed class Row
    {
        public string Name { get; set; } = "";
        public string Roof { get; set; } = "";
        public string Floor { get; set; } = "";
        public string Hex { get; set; } = "#D9480F";
        /// <summary>上一次运行的结果（回显用）。</summary>
        public string Result { get; set; } = "";
    }

    private readonly ObservableCollection<Row> _rows = new();
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
        SelectionMode = DataGridSelectionMode.Single,
    };

    private readonly ComboBox _cmbTerrain = new() { MinWidth = 220 };
    private readonly ComboBox _cmbRegion = new() { MinWidth = 220 };
    private readonly TextBox _tbEps = new() { Width = 80, Padding = new Thickness(6, 3) };
    private readonly CheckBox _chkRefine = new()
    {
        Content = "沿交线重剖分", IsChecked = true, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 0, 0),
    };
    private readonly TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private readonly Func<List<string>> _meshNames;
    private readonly Func<List<string>> _regionNames;
    private readonly Func<SeamOutcropRequest, string> _apply;
    private readonly Action<string> _echo;

    internal SeamOutcropWindow(Func<List<string>> meshNames, Func<List<string>> regionNames,
                               Func<SeamOutcropRequest, string> apply, Action<string> echo)
    {
        _meshNames = meshNames; _regionNames = regionNames; _apply = apply; _echo = echo;

        Title = "煤层露头着色";
        Width = 860; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildColumns();
        _grid.ItemsSource = _rows;
        Content = BuildLayout();
        Reload();
    }

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    private void BuildColumns()
    {
        var meshes = new List<string>();   // 每次 Reload 重填, 下拉共用这一份

        DataGridTextColumn T(string h, string path, double w, bool ro = false) => new()
        {
            Header = Head(h), Width = new DataGridLength(w), IsReadOnly = ro,
            Binding = new Binding(path) { Mode = ro ? BindingMode.OneWay : BindingMode.TwoWay },
        };

        _grid.Columns.Add(T("煤层名", nameof(Row.Name), 120));
        _grid.Columns.Add(MeshCol("顶板面", nameof(Row.Roof)));
        _grid.Columns.Add(MeshCol("底板面", nameof(Row.Floor)));
        _grid.Columns.Add(T("颜色", nameof(Row.Hex), 100));
        // 色块预览：Hex 打错了(解析不出)显示成透明, 一眼看得出来
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            // 56 装不下两个汉字的表头(截图核对出来的: 显示成「色」), 给到 76
            Header = Head("色块"), Width = new DataGridLength(76), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<Row>((r, _) => new Border
            {
                Width = 32, Height = 14, Margin = new Thickness(6, 0),
                BorderBrush = Brush.Parse("#888"), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Background = BrushOf(r?.Hex),
            }, supportsRecycling: false),
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = Head("上次结果"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true,
            Binding = new Binding(nameof(Row.Result)),
        });

        DataGridTemplateColumn MeshCol(string header, string path) => new()
        {
            Header = Head(header), Width = new DataGridLength(180),
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var tb = new TextBlock { Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
                tb.Bind(TextBlock.TextProperty, new Binding(path));
                return tb;
            }, supportsRecycling: true),
            CellEditingTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var cb = new ComboBox { ItemsSource = _meshNames(), HorizontalAlignment = HorizontalAlignment.Stretch };
                cb.Bind(ComboBox.SelectedItemProperty, new Binding(path) { Mode = BindingMode.TwoWay });
                return cb;
            }, supportsRecycling: false),
        };
    }

    private static IBrush BrushOf(string? hex)
    {
        uint? v = SeamSpec.ParseHex(hex);
        if (v == null) return Brushes.Transparent;
        var (r, g, b) = SeamSpec.Unpack(v.Value);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private Control BuildLayout()
    {
        Button B(string t, Action a, bool bold = false)
        {
            var b = new Button { Content = t, Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 8, 0) };
            if (bold) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => a();
            return b;
        }

        var top = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 6),
            Children =
            {
                // 一行放不下就换行, 不要挤出窗口 —— 定宽横排在缩放/字体变一变就会把末尾那个控件切掉
                // (截图核对出来的: 「沿交线重剖分」被切成「沿交」)。
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6),
                    Children =
                    {
                        new TextBlock { Text = "现状面", VerticalAlignment = VerticalAlignment.Center, Width = 64 },
                        _cmbTerrain,
                        new TextBlock { Text = "可采范围", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) },
                        _cmbRegion,
                        new TextBlock { Text = "容差 m", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) },
                        _tbEps,
                        _chkRefine,
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { B("+煤层", AddRow), B("删除选中", DeleteSelected), B("刷新面列表", Reload) },
                },
            },
        };

        var run = B("着色", Run, bold: true);
        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 6, 12, 10),
            Children = { run, B("关闭", Close) },
        };

        var bottom = new StackPanel
        {
            Children = { new Border { Margin = new Thickness(12, 0), Child = _msg }, foot },
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Margin = new Thickness(12, 0), Child = _grid });
        return root;
    }

    internal void Reload()
    {
        var meshes = _meshNames();
        string keepT = _cmbTerrain.SelectedItem as string ?? "";
        _cmbTerrain.ItemsSource = meshes;
        if (meshes.Contains(keepT)) _cmbTerrain.SelectedItem = keepT;
        else if (meshes.Count > 0) _cmbTerrain.SelectedIndex = 0;

        // 「（不裁）」排头一位：范围是可选项，默认不裁
        var regions = new List<string> { "（不裁）" };
        regions.AddRange(_regionNames());
        string keepR = _cmbRegion.SelectedItem as string ?? "";
        _cmbRegion.ItemsSource = regions;
        _cmbRegion.SelectedItem = regions.Contains(keepR) ? keepR : regions[0];

        if (string.IsNullOrWhiteSpace(_tbEps.Text)) _tbEps.Text = "0.05";

        SetMsg(meshes.Count == 0
            ? "场景里没有三角网 —— 先做出现状面与各层顶/底板（如「2.5D TIN」「层面建模」）再来。"
            : $"场景里有 {meshes.Count} 张三角网。用「+煤层」逐层指定顶/底板与颜色；层序即优先级，先配的层先认领。"
              + "\n勾「沿交线重剖分」= 切开现状网逐面着色（边界即交线，窄露头带不漏，另出露头面积），"
              + "生成一张新面；不勾 = 只按原有节点染色（边界锯齿≈一个三角边长，窄带可能整片漏染），就地改色。", null);
    }

    private void SetMsg(string text, string? color)
    {
        _msg.Text = text;
        _msg.Foreground = Brush.Parse(color ?? "#555");
    }

    private void AddRow()
    {
        int n = _rows.Count;
        _rows.Add(new Row
        {
            Name = $"{n + 2}煤",
            Hex = "#" + Palette[n % Palette.Length].ToString("X6", Inv),
        });
        _grid.SelectedIndex = _rows.Count - 1;
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedItem is Row r) _rows.Remove(r);
        else SetMsg("请先选中要删除的那一行。", "#D97706");
    }

    private void Run()
    {
        _grid.CommitEdit();

        string terrain = _cmbTerrain.SelectedItem as string ?? "";
        if (terrain.Length == 0) { SetMsg("请先选现状面。", "#DC2626"); return; }
        if (_rows.Count == 0) { SetMsg("请先用「+煤层」加至少一层煤。", "#DC2626"); return; }

        if (!double.TryParse(_tbEps.Text, NumberStyles.Float, Inv, out double eps) || eps < 0)
        { SetMsg("容差需为 ≥0 的数。", "#DC2626"); return; }

        var seams = new List<SeamSpec>();
        foreach (var r in _rows)
        {
            if (string.IsNullOrWhiteSpace(r.Roof) || string.IsNullOrWhiteSpace(r.Floor))
            { SetMsg($"「{r.Name}」还没选顶板或底板 —— 只有一张面判不出「夹在中间」。", "#DC2626"); return; }
            if (string.Equals(r.Roof, r.Floor, StringComparison.Ordinal))
            { SetMsg($"「{r.Name}」的顶板与底板选成了同一张面。", "#DC2626"); return; }
            uint? rgb = SeamSpec.ParseHex(r.Hex);
            if (rgb == null) { SetMsg($"「{r.Name}」的颜色 {r.Hex} 认不出，需形如 #RRGGBB。", "#DC2626"); return; }

            seams.Add(new SeamSpec { Name = r.Name, RoofName = r.Roof, FloorName = r.Floor, PackedRgb = rgb.Value });
        }

        string region = _cmbRegion.SelectedItem as string ?? "";
        var req = new SeamOutcropRequest
        {
            TerrainName = terrain,
            RegionName = region == "（不裁）" ? "" : region,
            Seams = seams,
            SnapEps = eps,
            RefineOnIntersection = _chkRefine.IsChecked == true,
        };

        string msg = _apply(req);
        SetMsg(msg, msg.Contains('✖') ? "#DC2626" : "#16A34A");
        _echo(msg);
    }

    /// <summary>重剖分那条路的逐层结果（三角数 + 两种面积）回填。</summary>
    internal void ShowRefineStats(IReadOnlyList<int> tris, IReadOnlyList<double> area3D, IReadOnlyList<double> areaXY)
    {
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Result = i < tris.Count
                ? (tris[i] == 0
                    ? "未切出露头面"
                    : $"{tris[i]} 面 · 三维 {area3D[i]:N0} m² · 投影 {areaXY[i]:N0} m²")
                : "";
        RebindGrid();
    }

    /// <summary>把逐层统计回填到表格的"上次结果"列。</summary>
    internal void ShowStats(SeamOutcropReport rep)
    {
        for (int i = 0; i < _rows.Count && i < rep.Layers.Count; i++)
        {
            var s = rep.Layers[i];
            _rows[i].Result = s.Marked == 0
                ? (s.NoData > 0 ? $"未染色（{s.NoData} 个顶点顶/底板没盖到）" : "未染色")
                : $"染 {s.Marked} 点"
                  + (s.NoData > 0 ? $" · 判不了 {s.NoData}" : "")
                  + (s.UncoveredTriangles > 0 ? $" · 疑漏 {s.UncoveredTriangles} 面" : "");
        }
        RebindGrid();
    }

    /// <summary>只读列不是 INotify 的，重挂一次数据源才刷新得出来。</summary>
    private void RebindGrid()
    {
        _grid.ItemsSource = null;
        _grid.ItemsSource = _rows;
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal string MsgText => _msg.Text ?? "";
}
