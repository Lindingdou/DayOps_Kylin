using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>新增设备的小对话框(忠实原 EquipmentAddDialog): 只问编号 / 分类 / 型号 三项。ShowDialog&lt;bool&gt; 返回 true 时 Result 有值。</summary>
public partial class EquipmentAddDialog : Window
{
    public GeoDbViews.EquipmentLedgerItem? Result { get; private set; }

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentAddDialog() { InitializeComponent(); }

    public EquipmentAddDialog(GeoDbContext ctx)
    {
        InitializeComponent();
        Opened += (_, _) => txtId.Focus();
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var id = txtId.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(id))
        {
            await EquipmentMessageBox.Info(this, "请输入设备编号。");
            txtId.Focus();
            return;
        }
        var categoryTag = (cmbCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Other";
        Result = new GeoDbViews.EquipmentLedgerItem { Id = id, Category = GeoDbViews.EqParseCategory(categoryTag), Model = txtModel.Text?.Trim() ?? "", Status = "在用" };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
