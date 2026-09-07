using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Views.GeoDb;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>拾取见煤点后弹出(原 MeshEditLib.ModelUpdate.PickHorizonDialog): 确认哪层煤 + 顶板/底板 + 备注。煤层来自预置的「煤层结构」。
/// ShowDialog&lt;bool&gt; true=确定。</summary>
public partial class PickHorizonDialog : Window
{
    public string? SeamCode { get; private set; }
    public string? SeamName { get; private set; }
    public string Horizon { get; private set; } = "顶板";
    public string? Remark { get; private set; }

    /// <summary>XAML 编译器/设计器用。</summary>
    public PickHorizonDialog() { InitializeComponent(); }

    /// <param name="x">拾取点世界坐标; NaN = 尚未解算(点「确定」后才对曲面取高程)。</param>
    public PickHorizonDialog(SeamStructureConfig config, double x, double y, double z, string? lastSeamCode, string lastHorizon)
    {
        InitializeComponent();
        ptText.Text = double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)
            ? "拾取点：点「确定」后解算曲面坐标"
            : $"拾取点：X {x.ToString("F2", CultureInfo.InvariantCulture)}  "
            + $"Y {y.ToString("F2", CultureInfo.InvariantCulture)}  "
            + $"Z {z.ToString("F2", CultureInfo.InvariantCulture)}";

        seamCombo.ItemsSource = config.Seams;
        if (config.Seams.Count > 0)
        {
            int idx = 0;
            if (!string.IsNullOrEmpty(lastSeamCode))
                for (int i = 0; i < config.Seams.Count; i++)
                    if (config.Seams[i].Code == lastSeamCode) { idx = i; break; }
            seamCombo.SelectedIndex = idx;
        }
        if (lastHorizon == "底板") floorRadio.IsChecked = true; else roofRadio.IsChecked = true;
    }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        if (seamCombo.SelectedItem is not SeamStructureConfig.SeamItem it)
        {
            await BoreholeMsgBox.InfoAsync(this, "提示", "请先选择煤层（可先在窗口「煤层结构…」里预置煤层）。");
            return;
        }
        SeamCode = it.Code;
        SeamName = it.Name;
        Horizon = floorRadio.IsChecked == true ? "底板" : "顶板";
        Remark = string.IsNullOrWhiteSpace(remarkBox.Text) ? null : remarkBox.Text!.Trim();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
