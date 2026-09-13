using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>清单一行：图纸中的一个候选面/线（原 <c>SurfaceRow</c>）。</summary>
public sealed class SurfaceRow
{
    public long Handle { get; set; }
    public string Kind { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
}

/// <summary>
/// 面/线选择对话框（原 <c>SurfaceSelectionDialog</c>，境界优化的「面与界线」选源）。两种模式：
///   ① 在视口拾取实体；② 从清单选择（枚举图纸中的三角网/多段线）。选定后回调 onChosen(handle)。
/// 非模态（Show）打开，避免禁用主窗口而无法在视口拾取。
/// </summary>
internal sealed class SurfaceSelectionDialog : Window
{
    private readonly IPlanEntityHost _host;
    private readonly int _wantType;
    private readonly Action<long> _onChosen;
    private bool _done;
    private readonly DataGrid _grid;
    private readonly TextBlock _status = RoadUi.Hint("");
    private readonly TextBlock _count = RoadUi.Hint("");

    public SurfaceSelectionDialog(IPlanEntityHost host, string title, int wantType, Action<long> onChosen)
    {
        _host = host; _wantType = wantType; _onChosen = onChosen;
        Title = title;
        PlanUi.Place(this, 640, 460);
        ShowInTaskbar = false;

        _grid = RoadUi.Table(new (string, string, double)[] { ("handle", "Handle", 90), ("类型", "Kind", 110), ("图层", "Layer", 120), ("名称", "Name", 150), ("尺寸 (AABB)", "Size", 150) }, multi: false);
        _grid.DoubleTapped += (_, _) => { if (_grid.SelectedItem is SurfaceRow r) Choose(r.Handle); };

        var tool = new DockPanel { Margin = new Thickness(12, 8, 12, 4) };
        var bPick = RoadUi.Btn("① 在视口拾取", async () => await PickViewportAsync(), 110, bold: true);
        var bRefresh = RoadUi.Btn("刷新清单", LoadList, 80);
        DockPanel.SetDock(bPick, Avalonia.Controls.Dock.Left); DockPanel.SetDock(bRefresh, Avalonia.Controls.Dock.Left);
        tool.Children.Add(bPick); tool.Children.Add(bRefresh);
        _count.VerticalAlignment = VerticalAlignment.Center; tool.Children.Add(_count);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(0) };
        Grid.SetRow(tool, 0); body.Children.Add(tool);
        var gb = new Border { Margin = new Thickness(12, 0, 12, 4), Child = _grid };
        Grid.SetRow(gb, 1); body.Children.Add(gb);

        var foot = new DockPanel();
        var right = RoadUi.Foot(RoadUi.Btn("确定", () =>
        {
            if (_grid.SelectedItem is SurfaceRow r) Choose(r.Handle);
            else _status.Text = "请在列表中选择一项，或用「① 在视口拾取」。";
        }, 80), RoadUi.Btn("取消", Close, 70));
        right.Margin = new Thickness(0);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right);
        foot.Children.Add(right);
        _status.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_status);

        Content = PlanUi.Shell(PlanUi.Header(title, "两种方式：① 在视口拾取实体；② 从下表（图纸中的面/线）选择。"), body, PlanUi.Footer(foot));
        LoadList();
    }

    private void LoadList()
    {
        var rows = new List<SurfaceRow>();
        try
        {
            foreach (var (h, layer, name) in _host.ListEntities(_wantType))
            {
                string size = "";
                if (_host.TryGetEntityAabb(h, out var mn, out var mx) && mn.Length == 3 && mx.Length == 3)
                    size = $"{(mx[0] - mn[0]):0} × {(mx[1] - mn[1]):0} × {(mx[2] - mn[2]):0} m";
                rows.Add(new SurfaceRow { Handle = h, Kind = KindName(_wantType), Layer = layer, Name = name, Size = size });
            }
        }
        catch (Exception ex) { _status.Text = $"枚举失败：{ex.Message}"; }
        _grid.ItemsSource = rows;
        _count.Text = $"共 {rows.Count} 个";
        if (rows.Count == 0) _status.Text = "图纸中没有此类实体——可用「① 在视口拾取」直接选。";
    }

    public static string KindName(int t) => t switch
    {
        PlanEntityType.TriangleMesh => "三角网（面）",
        PlanEntityType.Polyline => "多段线（线）",
        _ => "实体"
    };

    private async System.Threading.Tasks.Task PickViewportAsync()
    {
        _status.Text = "请在视口点选一个对象，Esc 取消…";
        try
        {
            var h = await _host.PickInViewportAsync(_wantType, $"{Title}：在视口点选{KindName(_wantType)}（Esc 取消）");
            if (h is null or 0) { _status.Text = "已取消拾取。"; Activate(); return; }
            Choose(h.Value);
        }
        catch (Exception ex) { _status.Text = $"拾取失败：{ex.Message}"; }
    }

    private void Choose(long handle)
    {
        if (_done) return;
        _done = true;
        try { _onChosen(handle); }
        finally { Close(); }
    }
}

/// <summary>分段分帮表的一行（一条直线段）（原 <c>WallSegRow</c>）。</summary>
public sealed class WallSegRow
{
    public int Index { get; set; }
    public double AzimuthDeg { get; set; }
    public double LengthM { get; set; }
    public string SideName { get; set; } = "";
    public double BetaDeg { get; set; } = 40;
    public double InsetExtraM { get; set; }
    public string AzimuthText => AzimuthDeg.ToString("F0");
    public string LengthText => LengthM.ToString("N0");
}

/// <summary>
/// 按地表界多段线的各直线段交互指定最终帮坡角 β（各帮不同）（原 <c>WallSegmentDialog</c>）。
/// 模态：确定后 <see cref="ToSegments"/> 给出 SegmentBeta 列表（Index 与多段线边对齐）。
/// </summary>
internal sealed class WallSegmentDialog : Window
{
    public ObservableCollection<WallSegRow> Rows { get; } = new();
    public bool Accepted { get; private set; }
    private readonly IPlanEntityHost _host;
    private readonly long _handle;
    private readonly DataGrid _grid;
    private readonly TextBlock _status = RoadUi.Hint("");

    public WallSegmentDialog(IPlanEntityHost host, long polylineHandle, IEnumerable<SegmentBeta> existing, IReadOnlyList<WallAngle> walls)
    {
        _host = host; _handle = polylineHandle;
        Title = "境界线段调整（帮坡角 + 收缩）";
        PlanUi.Place(this, 720, 520);
        ShowInTaskbar = false;

        _grid = PlanUi.EditableTable(new (string, string, double, bool)[]
        {
            ("段#", "Index", 50, true), ("方位(°)", "AzimuthText", 70, true), ("长度(m)", "LengthText", 80, true),
            ("帮别", "SideName", 0, false), ("最终帮坡角 β(°)", "BetaDeg", 120, false), ("额外收缩(m)", "InsetExtraM", 100, false),
        }, height: double.NaN);
        _grid.ItemsSource = Rows;

        var tool = new DockPanel { Margin = new Thickness(12, 8, 12, 4) };
        var b1 = RoadUi.Btn("按方位预填 β", () =>
        {
            foreach (var r in Rows) r.BetaDeg = Math.Round(PitSchemeEnvelope.BetaForAzimuth(walls, r.AzimuthDeg), 1);
            var src = Rows.ToList(); Rows.Clear(); foreach (var r in src) Rows.Add(r);
            _status.Text = "已按方位默认（端帮陡/工作帮缓需手动区分，或在③帮别命名东/西/南/北）。";
        }, 110);
        var b2 = RoadUi.Btn("在视口高亮该线", () =>
        {
            if (_handle == 0) { _status.Text = "选集能力不可用。"; return; }
            try { _host.SelectByHandle(_handle); _status.Text = "已在视口高亮该地表界线。"; }
            catch (Exception ex) { _status.Text = $"高亮失败：{ex.Message}"; }
        }, 120);
        DockPanel.SetDock(b1, Avalonia.Controls.Dock.Left); DockPanel.SetDock(b2, Avalonia.Controls.Dock.Left);
        tool.Children.Add(b1); tool.Children.Add(b2);
        tool.Children.Add(new Border());

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(tool, 0); body.Children.Add(tool);
        var gb = new Border { Margin = new Thickness(12, 0, 12, 4), Child = _grid };
        Grid.SetRow(gb, 1); body.Children.Add(gb);

        var foot = new DockPanel();
        var right = RoadUi.Foot(RoadUi.Btn("确定", () => { Accepted = true; Close(); }, 80), RoadUi.Btn("取消", () => { Accepted = false; Close(); }, 70));
        right.Margin = new Thickness(0);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right);
        foot.Children.Add(right);
        _status.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_status);

        Content = PlanUi.Shell(PlanUi.Header("境界线段调整（帮坡角 + 收缩）",
            "读地表界多段线各直线段，逐段指定 帮别 / 最终帮坡角 β / 额外向内收缩；或填整体收缩，在境界范围内手调边界形态。"),
            body, PlanUi.Footer(foot));
        Load(existing?.ToList() ?? new List<SegmentBeta>(), walls);
    }

    private void Load(List<SegmentBeta> existing, IReadOnlyList<WallAngle> walls)
    {
        if (!_host.TryGetPolylineWorldVertices(_handle, out var xyz, out bool closed) || xyz == null || xyz.Length < 6)
        {
            _status.Text = "读不到地表界多段线（请在④确认已选闭合多段线）";
            return;
        }
        int nv = xyz.Length / 3;
        int nEdges = closed ? nv : nv - 1;
        double cx = 0, cy = 0;
        for (int i = 0; i < nv; i++) { cx += xyz[i * 3]; cy += xyz[i * 3 + 1]; }
        cx /= nv; cy /= nv;

        for (int i = 0; i < nEdges; i++)
        {
            int j = (i + 1) % nv;
            double x0 = xyz[i * 3], y0 = xyz[i * 3 + 1], x1 = xyz[j * 3], y1 = xyz[j * 3 + 1];
            double ex = x1 - x0, ey = y1 - y0;
            double len = Math.Sqrt(ex * ex + ey * ey);
            double mx = (x0 + x1) / 2 - cx, my = (y0 + y1) / 2 - cy;
            double nx = ey, ny = -ex;
            if (nx * mx + ny * my < 0) { nx = -nx; ny = -ny; }
            double az = (Math.Atan2(ny, nx) * 180.0 / Math.PI + 360.0) % 360.0;

            double beta = (i < existing.Count && existing[i].BetaDeg > 1)
                ? existing[i].BetaDeg : PitSchemeEnvelope.BetaForAzimuth(walls, az);
            string side = (i < existing.Count && !string.IsNullOrWhiteSpace(existing[i].SideName))
                ? existing[i].SideName : DirName(az);

            Rows.Add(new WallSegRow
            {
                Index = i, AzimuthDeg = Math.Round(az, 1), LengthM = Math.Round(len, 1),
                SideName = side, BetaDeg = Math.Round(beta, 1),
                InsetExtraM = i < existing.Count ? existing[i].InsetExtraM : 0,
            });
        }
        _status.Text = $"共 {Rows.Count} 段（{(closed ? "闭合" : "开口")}）。可编辑「帮别 / 最终帮坡角 β / 额外收缩(m)」，或填上方整体收缩。";
    }

    public List<SegmentBeta> ToSegments()
        => Rows.Select(r => new SegmentBeta { Index = r.Index, SideName = r.SideName, BetaDeg = r.BetaDeg, InsetExtraM = r.InsetExtraM }).ToList();

    public static string DirName(double az)
        => (az >= 315 || az < 45) ? "东帮" : (az < 135) ? "北帮" : (az < 225) ? "西帮" : "南帮";
}
