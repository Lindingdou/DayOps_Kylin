using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 层位展点的煤层选择对话框: 勾选要展绘的煤层(带色块) + 顶板/底板。确定后由调用方读取
/// <see cref="SelectedSeams"/> / <see cref="IncludeRoof"/> / <see cref="IncludeFloor"/>; ShowDialog&lt;bool&gt; true=确定。
/// 整条「展绘层位数据」流程见 <see cref="DrawHorizonPointsAsync"/>。
/// </summary>
public partial class HorizonPointDialog : Window
{
    public sealed class SeamItem : INotifyPropertyChanged
    {
        public string Code { get; set; } = "";
        public string Label { get; set; } = "";
        public IBrush ColorBrush { get; set; } = Brushes.Gray;
        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<SeamItem> _items = new();

    /// <summary>确定后: 用户勾选的煤层编号。</summary>
    public HashSet<string> SelectedSeams { get; } = new();
    public bool IncludeRoof { get; private set; } = true;
    public bool IncludeFloor { get; private set; } = true;

    /// <summary>XAML 编译器/设计器用。</summary>
    public HorizonPointDialog() { InitializeComponent(); }

    public HorizonPointDialog(GeoDbContext ctx) : this(GeoDbViews.HorizonAvailableSeams(ctx.Conn)) { }

    public HorizonPointDialog(IEnumerable<string> availableSeams)
    {
        InitializeComponent();
        foreach (var code in availableSeams)
        {
            var (r, g, b) = GeoDbViews.HorizonSeamColor(code);
            _items.Add(new SeamItem { Code = code, Label = $"{code} 号煤", IsChecked = true, ColorBrush = new SolidColorBrush(Color.FromRgb(r, g, b)) });
        }
        seamList.ItemsSource = _items;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSelectAll(object? sender, RoutedEventArgs e) { foreach (var i in _items) i.IsChecked = true; }
    private void OnSelectNone(object? sender, RoutedEventArgs e) { foreach (var i in _items) i.IsChecked = false; }

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        SelectedSeams.Clear();
        foreach (var i in _items.Where(i => i.IsChecked)) SelectedSeams.Add(i.Code);
        IncludeRoof = roofBox.IsChecked == true;
        IncludeFloor = floorBox.IsChecked == true;
        if (SelectedSeams.Count == 0) { await BoreholeMsgBox.InfoAsync(this, "层位展点", "请至少勾选一个煤层。"); return; }
        if (!IncludeRoof && !IncludeFloor) { await BoreholeMsgBox.InfoAsync(this, "层位展点", "请至少勾选 顶板 或 底板。"); return; }
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>
    /// 「展绘层位数据」完整流程(原插件 Ribbon 命令): 取可用煤层 → 选煤层/顶底板对话框 → HorizonPointBuilder 提取高程点 →
    /// 分(煤层 × 顶/底)图层入场景 → 状态栏回报 图层数/点数/跳过数。
    /// </summary>
    public static async Task DrawHorizonPointsAsync(GeoDbContext ctx)
    {
        List<string> seams;
        try { seams = GeoDbViews.HorizonAvailableSeams(ctx.Conn); }
        catch (Exception ex) { ctx.Status($"展绘层位数据：读取失败 {ex.Message}"); return; }
        if (seams.Count == 0) { ctx.Status("展绘层位数据：库中无见煤成果/见煤点（缺底板标高）"); return; }

        var dlg = new HorizonPointDialog(seams);
        bool ok = await dlg.ShowDialog<bool>(ctx.Owner);
        if (!ok) { ctx.Status("展绘层位数据：已取消"); return; }

        var r = GeoDbViews.BuildHorizonPoints(ctx.Conn, dlg.SelectedSeams, dlg.IncludeRoof, dlg.IncludeFloor);
        if (r.SeamLayers == 0) { ctx.Status($"展绘层位数据：没有可展绘的层位点（跳过 {r.Skipped} 条，缺底板标高或孔位）"); return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var lay in r.Layers)
        {
            ctx.RemoveLayerEntities(lay.Name);
            foreach (var (x, y, _) in lay.Pts) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        }
        var last = r.Layers[^1];
        foreach (var lay in r.Layers)
            ctx.AddToScene(GeoDbViews.HorizonLayerEntities(lay), lay.Name, ReferenceEquals(lay, last) ? new[] { minX, minY, maxX, maxY } : null);
        string tail = r.Skipped > 0 ? $"（跳过 {r.Skipped} 条：缺底板标高或孔位）" : "";
        ctx.Status($"展绘层位数据：{r.Seams.Count} 煤层 → {r.SeamLayers} 个图层，底板 {r.FloorPoints} 点 + 顶板 {r.RoofPoints} 点{tail}");
    }
}
