using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「作业区划分」—— 把场景里的闭合多段线落成 <c>mineable_region</c> 台账
/// （移植原 <c>TaskLib.Features.WorkZoneLayoutWindow</c> 的口径那部分）。
///
/// <b>为什么是这张表</b>：它是全项目共用的「作业区域」注册表 —— 三维推演读它当推进轮廓、
/// 路网中心线提取读它当裁剪范围。另起一张表就等于"画完了推演里什么也不变"。
/// Kylin 侧这张表建好之后一直**零消费者**，本窗是那条接线。
///
/// ── 本窗守住的两件事 ──
/// <b>① Z 有出处</b>：落库前逐顶点从现状面采 Z，采不到就要人填基准标高；
///    两条都不成立 <b>拒绝入库</b>（<see cref="MineableRegions.Save"/> 里挡）——
///    写 0 会被推演当成台账实测高程，层体整体摆在 0 米，而且一路不报错。
/// <b>② 关联当场可见</b>：选定 / 极性 / 几何 / 高程 / 绑定 五条全都不会报错，故逐条判给人看。
///
/// ── 与原版的范围差异（登记）──
/// 原版是"对着正射影像在画布上圈"，还带影像配准校验与设备图标布置；
/// Kylin 侧区域直接取**场景里已有的世界坐标闭合多段线**（画线本来就是 CAD 侧的事），
/// 影像未配准那个失败模式**结构上不存在**；设备图标布置原版自己也说"没有任何下游消费方"，同样不移。
/// </summary>
internal sealed class WorkZoneWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal sealed class Row
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Points { get; set; } = "";
        public string Area { get; set; } = "";
        public string Z { get; set; } = "";
        public string Active { get; set; } = "";
        public string Usable { get; set; } = "";
        public IBrush Brush { get; set; } = Brushes.Black;
    }

    private readonly Func<DbConnection?> _conn;
    private readonly Func<SinkRegistry?> _sinks;
    private readonly Func<List<string>> _polylineLayers;
    private readonly Func<string, List<(double x, double y)>> _ringOf;
    private readonly Func<List<string>> _meshNames;
    private readonly Func<string, IReadOnlyList<(double x, double y)>, double[]?> _sampleZ;
    private readonly Action<string> _echo;

    private readonly ObservableCollection<Row> _rows = new();
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserSortColumns = false,
    };

    private readonly ComboBox _cmbLayer = new() { MinWidth = 180 };
    private readonly ComboBox _cmbCat = new() { MinWidth = 130 };
    private readonly ComboBox _cmbTerrain = new() { MinWidth = 170 };
    private readonly TextBox _tbName = new() { Width = 160, Padding = new Thickness(6, 3) };
    private readonly TextBox _tbDatumZ = new() { Width = 90, Padding = new Thickness(6, 3) };
    private readonly CheckBox _chkActive = new() { Content = "本期选定", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _diag = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#444") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };

    private static readonly (string Code, string Zh)[] Cats =
    {
        (MineableRegions.CatMineable, "可采区域"),
        (MineableRegions.CatPit, "采场"),
        (MineableRegions.CatExternalDump, "外排土场"),
        (MineableRegions.CatInternalDump, "内排土场"),
    };

    internal WorkZoneWindow(Func<DbConnection?> conn, Func<SinkRegistry?> sinks,
                            Func<List<string>> polylineLayers,
                            Func<string, List<(double x, double y)>> ringOf,
                            Func<List<string>> meshNames,
                            Func<string, IReadOnlyList<(double x, double y)>, double[]?> sampleZ,
                            Action<string> echo)
    {
        _conn = conn; _sinks = sinks; _polylineLayers = polylineLayers; _ringOf = ringOf;
        _meshNames = meshNames; _sampleZ = sampleZ; _echo = echo;

        Title = "作业区划分";
        Width = 1020; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        BuildColumns();
        _grid.ItemsSource = _rows;
        _grid.SelectionChanged += (_, _) => ShowDiagnosis();
        Content = BuildLayout();

        _cmbCat.ItemsSource = Cats.Select(c => c.Zh).ToList();
        _cmbCat.SelectedIndex = 0;
        Reload();
    }

    private static TextBlock Head(string t)
        => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    private void BuildColumns()
    {
        DataGridTextColumn C(string h, string path, double w) => new()
        { Header = Head(h), Width = new DataGridLength(w), Binding = new Binding(path) };

        _grid.Columns.Add(C("名称", nameof(Row.Name), 150));
        _grid.Columns.Add(C("类别", nameof(Row.Category), 110));
        _grid.Columns.Add(C("顶点", nameof(Row.Points), 70));
        _grid.Columns.Add(C("面积 万m²", nameof(Row.Area), 110));
        _grid.Columns.Add(C("高程出处", nameof(Row.Z), 150));
        _grid.Columns.Add(C("选定", nameof(Row.Active), 70));
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Head("能进推演"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var tb = new TextBlock { Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
                tb.Bind(TextBlock.TextProperty, new Binding(nameof(Row.Usable)));
                tb.Bind(TextBlock.ForegroundProperty, new Binding(nameof(Row.Brush)));
                return tb;
            }, supportsRecycling: true),
        });
    }

    private Control BuildLayout()
    {
        Button B(string t, Action a, bool bold = false)
        {
            var b = new Button { Content = t, Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 8, 0) };
            if (bold) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => a();
            return b;
        }

        var top = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 6),
            Children =
            {
                new WrapPanel
                {
                    Margin = new Thickness(0, 0, 0, 4),
                    Children =
                    {
                        new TextBlock { Text = "闭合多段线图层", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) },
                        _cmbLayer,
                        new TextBlock { Text = "名称", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 4) },
                        _tbName,
                        new TextBlock { Text = "类别", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 4) },
                        _cmbCat,
                        _chkActive,
                    },
                },
                new WrapPanel
                {
                    Margin = new Thickness(0, 0, 0, 4),
                    Children =
                    {
                        new TextBlock { Text = "Z 来源：现状面", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) },
                        _cmbTerrain,
                        new TextBlock { Text = "或基准标高 m", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 4) },
                        _tbDatumZ,
                        B("从选中图层新建区域", CreateFromLayer, bold: true),
                        B("刷新", Reload),
                    },
                },
            },
        };

        var bottom = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "关联诊断（五条全都不会报错，故逐条判出来）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6, 12, 2) },
                new Border { Height = 118, Margin = new Thickness(12, 0), Child = new ScrollViewer { Content = _diag } },
            },
        };
        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(12, 6, 12, 10),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { B("切换选定", ToggleActive), B("删除选中", DeleteSelected), B("关闭", Close) },
            },
        };
        var statusBar = new Border { Padding = new Thickness(12, 2), Child = _status };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Margin = new Thickness(12, 0), Child = _grid });
        return root;
    }

    internal void Reload()
    {
        var layers = _polylineLayers();
        string keepL = _cmbLayer.SelectedItem as string ?? "";
        _cmbLayer.ItemsSource = layers;
        if (layers.Contains(keepL)) _cmbLayer.SelectedItem = keepL;
        else if (layers.Count > 0) _cmbLayer.SelectedIndex = 0;

        var meshes = new List<string> { "（不采样）" };
        meshes.AddRange(_meshNames());
        string keepM = _cmbTerrain.SelectedItem as string ?? "";
        _cmbTerrain.ItemsSource = meshes;
        _cmbTerrain.SelectedItem = meshes.Contains(keepM) ? keepM : meshes[0];

        var sinks = _sinks();
        _rows.Clear();
        foreach (var r in MineableRegions.List(_conn()))
        {
            var d = MineableRegions.Diagnose(r, sinks);
            _rows.Add(new Row
            {
                Id = r.Id,
                Name = r.Name,
                Category = MineableRegions.CategoryZh(r.Category),
                Points = r.PointCount.ToString(Inv),
                Area = (r.AreaM2 / 1e4).ToString("N2", Inv),
                Z = r.ZProvenance.Length > 0 ? r.ZProvenance
                    : double.IsNaN(r.AvgZ) ? "—" : $"均值 {r.AvgZ:0.##}m（无出处）",
                Active = MineableRegions.IsActive(r) ? "是" : "否",
                Usable = d.Usable ? "可以" : "不行",
                Brush = d.Usable ? Brush.Parse("#16A34A") : Brush.Parse("#D97706"),
            });
        }

        _status.Text = _rows.Count == 0
            ? "台账里还没有作业区域 —— 在场景里画一条闭合多段线，选好图层与 Z 来源后点「从选中图层新建区域」。"
            : $"台账里有 {_rows.Count} 块区域，其中 {_rows.Count(x => x.Usable == "可以")} 块能进推演。";
        ShowDiagnosis();
    }

    private void ShowDiagnosis()
    {
        if (_grid.SelectedItem is not Row row) { _diag.Text = "（选中一行看它的关联诊断）"; return; }
        var rec = MineableRegions.List(_conn()).FirstOrDefault(x => x.Id == row.Id);
        if (rec == null) { _diag.Text = ""; return; }
        _diag.Text = string.Join("\n", MineableRegions.Diagnose(rec, _sinks()).Lines);
    }

    private void CreateFromLayer()
    {
        if (_cmbLayer.SelectedItem is not string layer || layer.Length == 0)
        { SetStatus("先在场景里画一条闭合多段线。", "#D97706"); return; }

        var ring = _ringOf(layer);
        if (ring.Count < 3) { SetStatus($"图层「{layer}」里没有 ≥3 顶点的闭合多段线。", "#DC2626"); return; }

        string name = (_tbName.Text ?? "").Trim();
        if (name.Length == 0) name = layer;

        // ── Z 的两条路：现状面采样优先，其次基准标高；两条都不成立就别入库 ──
        double[]? zs = null;
        string prov = "";
        if (_cmbTerrain.SelectedItem is string mesh && mesh != "（不采样）")
        {
            zs = _sampleZ(mesh, ring);
            if (zs == null) SetStatus($"从「{mesh}」采 Z 失败 —— 改填基准标高。", "#D97706");
            else prov = $"现状面采样({mesh})";
        }
        if (zs == null && double.TryParse(_tbDatumZ.Text, NumberStyles.Float, Inv, out double datum))
        {
            zs = Enumerable.Repeat(datum, ring.Count).ToArray();
            prov = $"基准标高 {datum.ToString("0.##", Inv)}m";
        }
        if (zs == null)
        {
            SetStatus("Z 没有出处：请选一张现状面采样，或填一个基准标高。"
                    + "直接写 0 会被推演当成台账实测高程，层体整体摆在 0 米。", "#DC2626");
            return;
        }

        var rec = new RegionRecord
        {
            Name = name,
            Category = Cats[Math.Max(0, _cmbCat.SelectedIndex)].Code,
            Note = (_chkActive.IsChecked == true ? MineableRegions.ActiveTag : "")
                 + MineableRegions.ZTag + prov + ";",
            Visible = true,
        };
        for (int i = 0; i < ring.Count; i++)
        { rec.Points.Add(ring[i].x); rec.Points.Add(ring[i].y); rec.Points.Add(zs[i]); }

        string err = MineableRegions.Save(_conn(), rec, out long id);
        if (err.Length > 0) { SetStatus("入库失败：" + err, "#DC2626"); return; }

        string msg = $"已建区域「{name}」（{MineableRegions.CategoryZh(rec.Category)}）："
                   + $"{rec.PointCount} 顶点 · {rec.AreaM2 / 1e4:0.##} 万m² · {prov}";
        SetStatus(msg, "#16A34A");
        _echo(msg);
        Reload();
    }

    private void ToggleActive()
    {
        if (_grid.SelectedItem is not Row row) { SetStatus("先选中一行。", "#D97706"); return; }
        var rec = MineableRegions.List(_conn()).FirstOrDefault(x => x.Id == row.Id);
        if (rec == null) return;
        string note = rec.Note ?? "";
        rec.Note = MineableRegions.IsActive(rec)
            ? note.Replace(MineableRegions.ActiveTag, "")
            : MineableRegions.ActiveTag + note;
        string err = MineableRegions.Save(_conn(), rec, out _);
        SetStatus(err.Length > 0 ? "改不了：" + err
                                 : $"「{rec.Name}」已{(MineableRegions.IsActive(rec) ? "选定" : "取消选定")}。",
                  err.Length > 0 ? "#DC2626" : null);
        Reload();
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedItem is not Row row) { SetStatus("先选中一行。", "#D97706"); return; }
        string err = MineableRegions.Delete(_conn(), row.Id);
        if (err.Length > 0) { SetStatus("删除失败：" + err, "#DC2626"); return; }
        SetStatus($"已删除区域「{row.Name}」。", null);
        _echo($"已删除作业区域「{row.Name}」");
        Reload();
    }

    private void SetStatus(string text, string? color)
    {
        _status.Text = text;
        _status.Foreground = Brush.Parse(color ?? "#555");
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal string StatusTextValue => _status.Text ?? "";
    internal string DiagText => _diag.Text ?? "";
}
