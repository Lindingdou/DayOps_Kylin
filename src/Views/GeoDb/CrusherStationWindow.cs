using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「破碎站位置设置」（移植原 <c>RoadLib.Views.CrusherStationWindow</c>）——
/// 在图上定破碎站的位置与通过能力，随工程持久化，供寻径 / 运距 / 运量驱动布线。
///
/// 这个窗口的前身是「装卸点设置」，管四类点（采剥点 / 破碎站 / 排土场 / 储矿场）。
/// 现场令窄化成只管破碎站：排土场 / 储矿场归「去向台账」，采剥点跟着工作面走、不该靠在图上手点。
/// <b>故本窗口只对破碎站有处置权</b>，别的类别既不显示也不许被它的保存动作碰到
/// —— 落库走 <see cref="CrusherStore.SaveScoped"/> 的增量 CRUD，绝不能退回"清表重插"。
///
/// 非模态（必须）：窗内「拾取位置」要去视口取点，模态对话框会吞掉视口点击。
///
/// 视口取点走主窗的道路取点通路（<c>RoadPickOnePointAsync</c>：X/Y 光标落点 + Z 取地形三角网），与原版 (x,y,z) 契约一致。
/// </summary>
internal sealed class CrusherStationWindow : Window
{
    /// <summary>一行 = 一座破碎站。INotifyPropertyChanged 让拾取回填的坐标即时反映到表格。</summary>
    internal sealed class CrusherRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        private string _id = "";
        /// <summary>破碎站名称，同时用作路网节点 Id 与库里的 <c>name</c>（唯一，窗内校验）。</summary>
        public string Id { get => _id; set { _id = value ?? ""; On(nameof(Id)); } }

        private double _x, _y, _z, _tph;
        public double X { get => _x; set { _x = value; On(nameof(X)); } }
        public double Y { get => _y; set { _y = value; On(nameof(Y)); } }
        public double Z { get => _z; set { _z = value; On(nameof(Z)); } }

        /// <summary>通过能力 t/h（网络流的汇容量）。</summary>
        public double ThroughputTph { get => _tph; set { _tph = value; On(nameof(ThroughputTph)); } }
    }

    private readonly ObservableCollection<CrusherRow> _rows = new();
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.All,
        Margin = new Thickness(12, 12, 12, 6),
        CanUserSortColumns = false,
    };
    private readonly TextBlock _msg = new()
    { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 6), FontSize = 12 };

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;
    private readonly Func<string, Task<(double x, double y, double z)?>>? _pick;
    private bool _committed;

    /// <summary>本次窗口打开时读到的破碎站名字 —— <b>删除权限的边界</b>（见 <see cref="CrusherStore.SaveScoped"/>）。</summary>
    private readonly HashSet<string> _seededNames = new(StringComparer.Ordinal);

    internal CrusherStationWindow(Func<DbConnection?> conn, Action<string> echo,
                                  Func<string, Task<(double x, double y, double z)?>>? pick)
    {
        _conn = conn; _echo = echo; _pick = pick;

        Title = "破碎站位置设置";
        Width = 640; Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        static TextBlock Head(string t)
            => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };
        DataGridTextColumn Col(string h, string path, string? fmt, double w) => new()
        {
            Header = Head(h), Width = new DataGridLength(w),
            Binding = fmt == null
                ? new Binding(path) { Mode = BindingMode.TwoWay }
                : new Binding(path) { Mode = BindingMode.TwoWay, StringFormat = "{0:" + fmt + "}" },
        };
        _grid.Columns.Add(Col("名称", nameof(CrusherRow.Id), null, 150));
        _grid.Columns.Add(Col("X", nameof(CrusherRow.X), "F1", 115));
        _grid.Columns.Add(Col("Y", nameof(CrusherRow.Y), "F1", 115));
        _grid.Columns.Add(Col("Z", nameof(CrusherRow.Z), "F1", 95));
        _grid.Columns.Add(Col("通过能力 t/h", nameof(CrusherRow.ThroughputTph), "F0", 120));
        _grid.ItemsSource = _rows;

        Button B(string t, Action a)
        {
            var b = new Button { Content = t, MinWidth = 78, Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => a();
            return b;
        }

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 6),
            Children = { B("+破碎站", AddRow), B("拾取位置…", PickForSelected), B("删除选中", DeleteSelected) },
        };

        var ok = B("确定", Commit);
        ok.MinWidth = 84; ok.FontWeight = FontWeight.SemiBold; ok.IsDefault = true;
        var cancel = B("取消", Close);
        cancel.MinWidth = 84; cancel.IsCancel = true;
        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 10), Children = { ok, cancel },
        };

        var root = new DockPanel();
        DockPanel.SetDock(tools, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(_msg, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(foot, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(_msg);
        root.Children.Add(tools);
        root.Children.Add(_grid);
        Content = root;

        Seed();
    }

    /// <summary>播种：读库里已有的破碎站。读不到就只允许新增，并把原因说清楚。</summary>
    /// <summary>种子是不是真从库里读回来的（false = 当时没连上库）。</summary>
    internal bool SeededFromDb { get; private set; }

    /// <summary>
    /// 补种：首次点数据库功能时 <c>EnsureGeoDb</c> 必然返回 null（它另起异步连接流程，
    /// 连上后再把这条命令重跑一遍），于是构造时那次播种拿不到连接。重跑走这里补上。
    /// <b>只在"当时没连上"时才补</b> —— 否则会把用户已经改的行冲掉。
    /// </summary>
    internal void ReseedIfNotSeeded()
    {
        if (!SeededFromDb) Seed();
    }

    private void Seed()
    {
        _rows.Clear();
        _seededNames.Clear();
        SeededFromDb = false;

        var conn = _conn();
        string note;
        if (conn == null)
        {
            // 子类信息只有库里有：读不到就宁可不播种，也不能把排土场当破碎站端上来编辑后写回
            note = "⚠ 数据库未接通：读不到已有破碎站，本次只能新增，且删除不会持久化。";
        }
        else
        {
            foreach (var p in CrusherStore.LoadCrushers(conn))
            {
                _rows.Add(new CrusherRow { Id = p.Name, X = p.X, Y = p.Y, Z = p.Z, ThroughputTph = p.ThroughputTph });
                _seededNames.Add(p.Name);
            }
            SeededFromDb = true;
            note = _rows.Count > 0 ? $"台账里已有 {_rows.Count} 座破碎站。" : "台账里还没有破碎站，用「+破碎站」新增。";
        }

        SetMsg(note + "\n提示：「+破碎站」新增一行，选中后「拾取位置」到视口点位置"
             + "（右键/Esc 取消；标高取光标下的三角网，没有网面时为 0，可在表格里改）。"
             + "\n排土场 / 储矿场请用「去向台账」；采剥点跟着工作面走，不在此处手点。", null);
    }

    private void SetMsg(string text, IBrush? brush)
    {
        _msg.Text = text;
        _msg.Foreground = brush ?? Brush.Parse("#666");
    }

    private void AddRow()
    {
        int n = _rows.Count + 1;
        while (_rows.Any(r => r.Id == "C" + n)) n++;   // 避免编号撞车
        var row = new CrusherRow { Id = "C" + n, ThroughputTph = 0 };
        _rows.Add(row);
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row, null);
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedItem is CrusherRow r) _rows.Remove(r);
        else SetMsg("请先在表格里选中一行，再删除。", Brush.Parse("#D97706"));
    }

    private async void PickForSelected()
    {
        if (_grid.SelectedItem is not CrusherRow r)
        { SetMsg("请先在表格里选中一行，再拾取位置。", Brush.Parse("#D97706")); return; }
        if (_pick == null)
        { SetMsg("视口取点不可用。", Brush.Parse("#D97706")); return; }

        var p = await _pick($"破碎站「{r.Id}」：在视口中点击位置（Esc 取消）");
        if (p == null) { SetMsg($"「{r.Id}」拾取已取消。", null); return; }

        r.X = p.Value.x; r.Y = p.Value.y; r.Z = p.Value.z;   // INotifyPropertyChanged → 表格即时刷新（Z 取地形面，同原版）
        SetMsg($"「{r.Id}」位置已取到 ({r.X:F1}, {r.Y:F1}, {r.Z:F1})。", null);
        Activate();
    }

    private void Commit()
    {
        _grid.CommitEdit();   // 先提交正在编辑的单元格，再校验

        // 校验：名称非空且唯一（名称即路网节点 Id，也是库里按名匹配的键，重名会把两行并成一行）
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in _rows)
        {
            if (string.IsNullOrWhiteSpace(r.Id))
            { SetMsg("存在空名称的行，请填写或删除。", Brush.Parse("#DC2626")); return; }
            if (!ids.Add(r.Id))
            { SetMsg($"名称重复：{r.Id}。名称须唯一（它同时是路网节点编号）。", Brush.Parse("#DC2626")); return; }
        }

        var conn = _conn();
        if (conn == null)
        {
            // 原版：GeoDataBase 未就绪时不拦，降级为仅会话有效（共享设置按名并上窗口行、会话路网重灌源/汇），只是不入库。
            _echo("破碎站位置设置：工程库未连接，本次仅会话有效（未入库）");
            _committed = true;
            Saved?.Invoke(_rows.Select(r => (r.Id, r.X, r.Y, r.Z, r.ThroughputTph)).ToList(), 0, 0, 0, false);
            Close();
            return;
        }

        try
        {
            var specs = _rows.Select(r => new CrusherSpec(r.Id, r.X, r.Y, r.Z, r.ThroughputTph)).ToList();
            var res = CrusherStore.SaveScoped(conn, specs, _seededNames);
            string caption = $"破碎站位置：新增 {res.Inserted} · 更新 {res.Updated} · 删除 {res.Deleted}";
            _echo(caption);
            // 原版 CommitHandler 的第②③步（共享设置按全表重建 + 会话路网按全表重灌源/汇 + 视口旗标）由主窗接在 Saved 上做。
            _committed = true;
            Saved?.Invoke(_rows.Select(r => (r.Id, r.X, r.Y, r.Z, r.ThroughputTph)).ToList(), res.Inserted, res.Updated, res.Deleted, true);
            Close();
        }
        catch (Exception ex) { SetMsg("破碎站入库失败：" + ex.Message, Brush.Parse("#DC2626")); }
    }

    /// <summary>确定后触发：(名称,X,Y,Z,吞吐) 行 + 新增/更新/删除计数 + 是否已入库（false = 库未连接的会话级降级）。主窗据此重灌共享设置与会话路网（原版第②③步）。</summary>
    internal event Action<List<(string Id, double X, double Y, double Z, double Tph)>, int, int, int, bool>? Saved;

    /// <summary>关窗未确定 → 原版 Cancelled 回显「未修改」。</summary>
    internal event Action? Cancelled;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_committed) Cancelled?.Invoke();
    }

    // ── 自检钩子用 ──
    internal int RowCount => _rows.Count;
    internal string MsgText => _msg.Text ?? "";
}
