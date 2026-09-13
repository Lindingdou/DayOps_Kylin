using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Modeling;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 【LT7 的配套入口】加载 / 激活块体模型（移植原 <c>LongTermBlockPickerWindow</c>）。
///
/// 中长远的量只能来自块体（BM1），所以"没有块体"必须是一个**能就地解决**的状态，而不是一句"请先去别的 tab 激活块体"。
/// 这个小窗就干三件事：列出已加载的块体、设为活动、导入新的。顺带对每个块体标出**有没有煤属性列** ——
/// 没有煤属性的块体激活了也排不了产，而这件事在块体浏览器里是看不出来的（那边只管块体本身）。
/// </summary>
internal sealed class LongTermBlockPickerWindow : Window
{
    private readonly ListBox _list = new() { Height = 200 };
    private readonly TextBlock _status = RoadUi.Text("", 12.5);
    private readonly Action<Window> _openImport;

    private sealed class Row
    {
        public BlockModelMeta Model = null!;
        public bool HasCoal;
        public override string ToString()
            => $"{(Model.IsActive ? "● " : "○ ")}{Model.Name}　{Model.Nx}×{Model.Ny}×{Model.Nz}　单元 {Model.Sx:0.#}×{Model.Sy:0.#}×{Model.Sz:0.#}m"
             + (HasCoal ? "　✓有煤属性" : "　✗没有煤属性列（排不了产）");
    }

    public LongTermBlockPickerWindow(Action<Window> openImport)
    {
        _openImport = openImport;
        Title = "加载块体模型 — 中长远的量源";
        Width = 680; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(RoadUi.Text("中长远进度计划的逐年采出/剥离量**只能来自块体**（cell 求和），几何只出归属、不出方量。选一个带煤属性的块体设为活动，排产才跑得起来。", 13));
        root.Children.Add(new Border { Height = 8 });
        root.Children.Add(_list);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Spacing = 10 };
        var bActivate = RoadUi.Btn("设为活动", Activate_, 90, primary: true); bActivate.IsDefault = true;
        btns.Children.Add(bActivate);
        btns.Children.Add(RoadUi.Btn("导入块体…", Import, 100));
        btns.Children.Add(RoadUi.Btn("刷新", Reload, 70));
        btns.Children.Add(RoadUi.Btn("关闭", Close, 70));
        root.Children.Add(btns);
        _status.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_status);
        Content = root;
        Reload();
    }

    private void Reload()
    {
        var rows = new System.Collections.Generic.List<Row>();
        try
        {
            foreach (var m in BlockModelStore.Models)
                rows.Add(new Row { Model = m, HasCoal = BlockModelCoal.TryGetClassifier(m, out _, out _) });
        }
        catch (Exception ex) { _status.Text = "读块体清单失败：" + ex.Message; return; }
        _list.ItemsSource = rows;
        if (rows.Count == 0)
        {
            _status.Text = "还没有加载任何块体模型。点「导入块体…」从外部文件载入，或去【块体】tab 用「创建块体」建一个。";
            return;
        }
        var cur = rows.FirstOrDefault(r => r.Model.IsActive);
        _list.SelectedItem = cur ?? rows[0];
        _status.Text = cur == null
            ? "有块体但一个都没激活 —— 选中一个点「设为活动」。"
            : (cur.HasCoal ? $"当前活动：{cur.Model.Name}（有煤属性，可排产）"
                           : $"当前活动：{cur.Model.Name} —— **没有煤属性列**，排产会被拦住。换一个或先给它赋煤属性。");
    }

    private void Activate_()
    {
        if (_list.SelectedItem is not Row r) { _status.Text = "先选一个块体。"; return; }
        try { BlockModelStore.Active = r.Model; }
        catch (Exception ex) { _status.Text = "激活失败：" + ex.Message; return; }
        Reload();
    }

    private void Import()
    {
        try { _openImport(this); }
        catch (Exception ex) { _status.Text = "打开导入对话框失败：" + ex.Message; return; }
        Reload();
    }

    /// <summary>在 <paramref name="owner"/> 上弹出（模态）。返回关闭后是否已有可用量源。</summary>
    public static async Task<bool> ShowForAsync(Window? owner, Action<Window> openImport)
    {
        var w = new LongTermBlockPickerWindow(openImport);
        if (owner != null) await w.ShowDialog(owner); else w.Show();
        return LongTermBlockSource.HasActiveBlockModel(BlockModelStore.Active ?? BlockModelStore.PickDefault(), out _);
    }
}
