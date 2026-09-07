using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Views.GeoDb;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>现状写实数据管理(原 MeshEditLib.ModelUpdate.CurrentStateBatchManagerWindow): 按批次列出; 双击名称改名; 删批次连带删该批点。</summary>
public partial class CurrentStateBatchManagerWindow : Window
{
    private readonly SqliteConnection? _conn;
    private ObservableCollection<CsBatchRow> _rows = new();

    /// <summary>XAML 编译器/设计器用。</summary>
    public CurrentStateBatchManagerWindow() { InitializeComponent(); }

    public CurrentStateBatchManagerWindow(SqliteConnection? conn)
    {
        InitializeComponent();
        _conn = conn;
        Opened += (_, _) => Reload();
    }

    private void Reload()
    {
        if (_conn == null) { statusText.Text = "数据库不可用"; return; }
        try { _rows = new ObservableCollection<CsBatchRow>(CsAllBatches(_conn)); }
        catch (Exception ex) { statusText.Text = $"加载失败：{ex.Message}"; return; }
        grid.ItemsSource = _rows;
        int pts = _rows.Sum(b => b.PointCount);
        statusText.Text = $"共 {_rows.Count} 个批次，{pts} 个现状点";
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Reload();

    private void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.DataContext is not CsBatchRow b || _conn == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { CsRenameBatch(_conn, b.Id, b.Name ?? ""); statusText.Text = $"已改名：{b.Name}"; }
            catch (Exception ex) { statusText.Text = $"改名失败：{ex.Message}"; }
        }, DispatcherPriority.Background);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not CsBatchRow b) { statusText.Text = "请先选择要删除的批次"; return; }
        if (_conn == null) { statusText.Text = "数据库不可用"; return; }
        if (!await BoreholeMsgBox.ConfirmAsync(this, "删除批次",
                $"确认删除批次「{b.Name}」（{b.Label}）？\n\n将连带删除该批 {b.PointCount} 个现状点，且不可撤销。")) return;
        try
        {
            int n = CsDeleteBatch(_conn, b.Id);
            Reload();
            statusText.Text = $"已删除批次「{b.Name}」（连带 {n} 个点）";
        }
        catch (Exception ex) { statusText.Text = $"删除失败：{ex.Message}"; }
    }
}
