using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.PointCloud;

/// <summary>
/// 点云管理面板（忠实原 PointCloudLib.Manage.PointCloudManagerWindow，非模态）：
/// 列出场景中所有点云 → 设为当前 / 重命名 / 单独显隐 / 单独移除 / 缩放到。
///
/// 为什么必须有它：所有点云算子都是非破坏式的，每跑一次就往场景里追加一份新点云
/// （地面点、去噪结果、着色副本…）。没有这个清单，它们全是匿名的 —— 分不清谁是谁，
/// 也没法单独删/单独隐藏；「当前点云」更是各算子的输入锚点，算子跑完自动切到产物上，
/// 抽稀→去噪→滤波→建面才串得下去。代码构建，无 XAML。
/// </summary>
internal sealed class PointCloudManagerWindow : Window
{
    private readonly Func<List<PointCloudEntity>> _list;
    private readonly Func<PointCloudEntity?> _getCurrent;
    private readonly Action<PointCloudEntity> _setCurrent;
    private readonly Action<PointCloudEntity> _remove;
    private readonly Action<PointCloudEntity> _zoomTo;
    private readonly Action _refreshScene;

    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _summary = new() { Foreground = Brushes.Gray, FontSize = 12 };

    public PointCloudManagerWindow(
        Func<List<PointCloudEntity>> list,
        Func<PointCloudEntity?> getCurrent,
        Action<PointCloudEntity> setCurrent,
        Action<PointCloudEntity> remove,
        Action<PointCloudEntity> zoomTo,
        Action refreshScene)
    {
        _list = list; _getCurrent = getCurrent; _setCurrent = setCurrent;
        _remove = remove; _zoomTo = zoomTo; _refreshScene = refreshScene;

        Title = "点云管理";
        Width = 880; Height = 470;
        MinWidth = 720; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        var head = new TextBlock
        {
            Text = "「当前点云」决定各点云算子的输入；算子跑完自动切到结果上，以便 抽稀 → 去噪 → 滤波 → 建面 一路串下去。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var refresh = new Button { Content = "刷新", MinWidth = 72 };
        refresh.Click += (_, _) => Rebuild();
        var close = new Button { Content = "关闭", MinWidth = 72, IsCancel = true };
        close.Click += (_, _) => Close();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        bar.Children.Add(refresh); bar.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(14, 12) };
        DockPanel.SetDock(head, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(_summary, Avalonia.Controls.Dock.Bottom);
        _summary.Margin = new Thickness(0, 8, 0, 8);
        root.Children.Add(head);
        root.Children.Add(bar);
        root.Children.Add(_summary);
        root.Children.Add(new ScrollViewer { Content = _rows });
        Content = root;
        Rebuild();
    }

    /// <summary>表头与数据行共用的列宽（改一处两边都动）：标记 / 名称 / 点数 / 高程范围 / 4 个动作。</summary>
    private const string Cols = "26,*,86,132,84,60,68,60";

    private static TextBlock Cell(string text, bool right = false) => new()
    {
        Text = text, FontSize = 12, Margin = new Thickness(6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
    };

    private static Button Btn(string text) => new()
    {
        Content = text, FontSize = 11, Padding = new Thickness(6, 2),
        Margin = new Thickness(2, 3), HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>外部（算子跑完 / 加载新点云）调用：重建清单。</summary>
    public void RefreshFromOutside() => Rebuild();

    private void Rebuild()
    {
        _rows.Children.Clear();
        var clouds = _list();
        var cur = _getCurrent();
        long total = 0;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols), Margin = new Thickness(0, 0, 0, 2) };
        void H(string t, int col, bool right = false)
        {
            var tb = new TextBlock
            {
                Text = t, FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(6, 2),
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(tb, col); header.Children.Add(tb);
        }
        H("", 0); H("名称", 1); H("点数", 2, right: true); H("高程范围 (m)", 3);
        H("当前", 4); H("显隐", 5); H("缩放", 6); H("移除", 7);
        _rows.Children.Add(header);
        _rows.Children.Add(new Border { Height = 1, Background = Brush.Parse("#DCDFE4"), Margin = new Thickness(0, 0, 0, 2) });

        foreach (var pc in clouds)
        {
            total += pc.PointCount;
            bool isCur = ReferenceEquals(pc, cur);
            var b = pc.Bounds;
            bool odd = _rows.Children.Count % 2 == 1;
            var g = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(Cols),
                Height = 30,
                Background = isCur ? new SolidColorBrush(Color.Parse("#E7F1FF"))
                                   : (odd ? new SolidColorBrush(Color.Parse("#FAFBFC")) : Brushes.Transparent),
            };
            void C(Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }

            C(new TextBlock
            {
                Text = isCur ? "▶" : "", FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#0D6EFD")),
            }, 0);

            // 名称：直接可改（原版是弹「重命名」小窗；这里就地编辑，少一次弹窗，语义一样）。
            // 名字是算子链式累加出来的，常比列宽长 —— 挂 ToolTip 让鼠标一停就能看全。
            var name = new TextBox
            {
                Text = pc.Name, FontSize = 12, MinWidth = 180,
                Margin = new Thickness(4, 2), Padding = new Thickness(6, 2),
                VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(name, pc.Name);
            name.LostFocus += (_, _) =>
            {
                string t = (name.Text ?? "").Trim();
                if (t.Length > 0 && t != pc.Name) { pc.Name = t; ToolTip.SetTip(name, t); UpdateSummary(); }
            };
            C(name, 1);

            C(Cell(pc.PointCount.ToString("N0", CultureInfo.InvariantCulture), right: true), 2);
            C(Cell(pc.PointCount == 0
                ? "—"
                : $"{b.minZ.ToString("0.#", CultureInfo.InvariantCulture)} ~ {b.maxZ.ToString("0.#", CultureInfo.InvariantCulture)}"), 3);

            var setCur = Btn(isCur ? "当前" : "设为当前");
            setCur.IsEnabled = !isCur;
            setCur.Click += (_, _) => { _setCurrent(pc); Rebuild(); };
            C(setCur, 4);

            var vis = Btn(pc.Visible ? "隐藏" : "显示");
            vis.Click += (_, _) => { pc.Visible = !pc.Visible; _refreshScene(); Rebuild(); };
            C(vis, 5);

            var zoom = Btn("缩放到");
            zoom.Click += (_, _) => _zoomTo(pc);
            C(zoom, 6);

            var del = Btn("移除");
            del.Click += (_, _) => { _remove(pc); Rebuild(); };
            C(del, 7);

            _rows.Children.Add(g);
        }

        if (clouds.Count == 0)
            _rows.Children.Add(new TextBlock
            {
                Text = "场景中没有点云。用「加载点云」导入 LAS / CSV 后再回来。",
                Foreground = Brushes.Gray, Margin = new Thickness(6, 12),
            });
        UpdateSummary(clouds.Count, total);
    }

    private void UpdateSummary()
    {
        var clouds = _list();
        long total = 0;
        foreach (var c in clouds) total += c.PointCount;
        UpdateSummary(clouds.Count, total);
    }

    private void UpdateSummary(int count, long total)
        => _summary.Text = $"共 {count} 份点云 · {total:N0} 点";
}
