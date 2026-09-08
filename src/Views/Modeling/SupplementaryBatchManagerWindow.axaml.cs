using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Data.Common;
using PitMine3D.Kylin.Views.GeoDb;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>补勘写实数据管理(原 MeshEditLib.ModelUpdate.SupplementaryBatchManagerWindow): 按「批次」(系统唯一时间标签)列出;
/// 双击名称改名; 删除批次连带删该批孔+层位。ShowDialog 关闭后调用方重载批次。</summary>
public partial class SupplementaryBatchManagerWindow : Window
{
    private readonly DbConnection? _conn;
    private ObservableCollection<SupBatchRow> _rows = new();

    /// <summary>XAML 编译器/设计器用。</summary>
    public SupplementaryBatchManagerWindow() { InitializeComponent(); }

    public SupplementaryBatchManagerWindow(DbConnection? conn)
    {
        InitializeComponent();
        _conn = conn;
        Opened += (_, _) => Reload();
    }

    private void Reload()
    {
        if (_conn == null) { statusText.Text = "数据库不可用"; return; }
        try { _rows = new ObservableCollection<SupBatchRow>(SupAllBatches(_conn)); }
        catch (Exception ex) { statusText.Text = $"加载失败：{ex.Message}"; return; }
        grid.ItemsSource = _rows;
        int holes = _rows.Sum(b => b.HoleCount);
        statusText.Text = $"共 {_rows.Count} 个批次，{holes} 个补勘孔";
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Reload();

    private void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.DataContext is not SupBatchRow b || _conn == null) return;
        // 绑定在本事件返回后才提交, 延迟到提交完成再读 b.Name 落库。
        Dispatcher.UIThread.Post(() =>
        {
            try { SupRenameBatch(_conn, b.Id, b.Name ?? ""); statusText.Text = $"已改名：{b.Name}"; }
            catch (Exception ex) { statusText.Text = $"改名失败：{ex.Message}"; }
        }, DispatcherPriority.Background);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not SupBatchRow b) { statusText.Text = "请先选择要删除的批次"; return; }
        if (_conn == null) { statusText.Text = "数据库不可用"; return; }
        if (!await BoreholeMsgBox.ConfirmAsync(this, "删除批次",
                $"确认删除批次「{b.Name}」（{b.Label}）？\n\n将连带删除该批 {b.HoleCount} 个补勘钻孔及其全部煤层顶底板记录，且不可撤销。")) return;
        try
        {
            int n = SupDeleteBatch(_conn, b.Id);
            Reload();
            statusText.Text = $"已删除批次「{b.Name}」（连带 {n} 个孔）";
        }
        catch (Exception ex) { statusText.Text = $"删除失败：{ex.Message}"; }
    }
}
