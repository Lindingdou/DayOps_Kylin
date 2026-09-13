using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 逐月配置表控件（移植原 <c>MonthlyTargetGrid</c>）—— 绑的是 <see cref="MonthlyTargetStore.Current"/>，
/// <b>与「短期生产计划编制 ③b」是同一份数据，不是第二套</b>。它是编制的输入，不是结果：改了要重新编制才生效，不自动重编。
/// </summary>
internal sealed class MonthlyTargetGrid : UserControl
{
    /// <summary>表被人改过时触发（宿主窗口据此提示"要重新编制"）。</summary>
    public event Action? Edited;

    private readonly DataGrid _grid;
    private readonly TextBlock _cap = RoadUi.Hint("", 12), _hint = RoadUi.Hint("改完这张表要重新「一键编制」才会生效 —— 它是编制的输入，不是编制的结果。", 11.5);

    public MonthlyTargetGrid()
    {
        _grid = PlanUi.EditableTable(new (string, string, double, bool)[]
        {
            ("期次", "PeriodKey", 84, true), ("采出(万t)", "CoalWanT", 92, false), ("剥离(万m³)", "StripWanM3", 104, false), ("剥采比", "Ratio", 74, true),
            ("作业日", "Workdays", 72, false), ("剥离能力(万m³)", "StripCapWanM3", 130, false), ("车队能力(万t·km)", "FleetCapWanTKm", 140, false),
            ("来源", "SourceText", 84, true), ("覆盖列", "OverriddenText", 140, true), ("备注", "Note", 200, false),
        }, double.NaN);
        _grid.Columns.Insert(7, new DataGridCheckBoxColumn { Header = "内排", Binding = new Avalonia.Data.Binding("InternalDumpEnabled") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(60) });
        _grid.Columns.Insert(8, new DataGridCheckBoxColumn { Header = "检修", Binding = new Avalonia.Data.Binding("IsMaintenance") { Mode = Avalonia.Data.BindingMode.TwoWay }, Width = new DataGridLength(60) });
        PlanUi.FitHeaders(_grid);
        _grid.SelectionMode = DataGridSelectionMode.Extended;
        _grid.CellEditEnded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { try { _cap.Text = MonthlyTargetStore.Current.Caption; } catch { } Edited?.Invoke(); });

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var b1 = RoadUi.Btn("重置为派生值", OnResetToDerived, 104); b1.Margin = new Thickness(8, 0, 0, 0);
        ToolTip.SetTip(b1, "选中了行就只重置选中的行，没选就整表重置 —— 这是唯一能冲掉人工值的动作");
        var b2 = RoadUi.Btn("按当前基础约束派生", OnRebuild, 132);
        DockPanel.SetDock(b1, Avalonia.Controls.Dock.Right); DockPanel.SetDock(b2, Avalonia.Controls.Dock.Right);
        top.Children.Add(b1); top.Children.Add(b2);
        _cap.VerticalAlignment = VerticalAlignment.Center; _cap.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; top.Children.Add(_cap);

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top); root.Children.Add(top);
        _hint.Margin = new Thickness(0, 6, 0, 0); DockPanel.SetDock(_hint, Avalonia.Controls.Dock.Bottom); root.Children.Add(_hint);
        root.Children.Add(_grid);
        Content = root;
        AttachedToVisualTree += (_, _) => Reload();
    }

    /// <summary>重新绑一次（宿主打开/切方案时调）。</summary>
    public void Reload()
    {
        try
        {
            var t = MonthlyTargetStore.Current;
            _grid.ItemsSource = null; _grid.ItemsSource = t.Rows;
            _cap.Text = t.Caption;
        }
        catch (Exception ex) { _grid.ItemsSource = null; _cap.Text = "◆ 取不到逐月配置表：" + ex.Message; }
    }

    /// <summary>对账一行（逐月之和 vs 年目标）。宿主要显示就调它，不在这儿自己再算一遍。</summary>
    public string ReconcileText()
    {
        try { return MonthlyTargetStore.Current.Reconcile().Report(); }
        catch (Exception ex) { return "◆ 对账没做成：" + ex.Message; }
    }

    private void OnRebuild()
    {
        try
        {
            var t = MonthlyTargetStore.Rebuild();
            _grid.ItemsSource = null; _grid.ItemsSource = t.Rows;
            _cap.Text = t.Caption;
            _hint.Text = $"已按当前基础约束重新派生：{t.Rows.Count} 个月" + (t.ManualRowCount > 0 ? $"；**保住了 {t.ManualRowCount} 行的人工覆盖**（只更新了它们的派生值）" : "") + "。改完要重新「一键编制」才生效。";
            Edited?.Invoke();
        }
        catch (Exception ex) { _hint.Text = "◆ 派生失败：" + ex.Message; }
    }

    private void OnResetToDerived()
    {
        try
        {
            var t = MonthlyTargetStore.Current;
            var sel = _grid.SelectedItems.OfType<MonthlyTargetRow>().ToList();
            int n = sel.Count > 0 ? t.ResetToDerived(sel) : t.ResetToDerived();
            Reload();
            _hint.Text = n == 0 ? "选中的行本来就没有人工覆盖，没什么可重置的。"
                : $"已把 {n} 行重置为派生值（{(sel.Count > 0 ? "选中行" : "全表")}）—— 这是唯一能冲掉人工值的动作。改完要重新「一键编制」才生效。";
            Edited?.Invoke();
        }
        catch (Exception ex) { _hint.Text = "◆ 重置失败：" + ex.Message; }
    }
}
