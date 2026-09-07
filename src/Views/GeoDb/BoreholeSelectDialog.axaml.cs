using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 展绘钻孔前的选孔对话框: 逐行复选框勾选 + 顶部「全选」三态总开关 + 关键字筛选。
/// <see cref="Result"/> 为 null 表示(可展绘的)全部; 否则为勾选的钻孔子集。对话框结果 <c>ShowDialog&lt;bool&gt;</c> true=确定。
/// 整条「展绘钻孔」流程(选孔 → 生成柱状图入场景 → 状态栏回报)见 <see cref="DrawBoreholesAsync"/>。
/// </summary>
public partial class BoreholeSelectDialog : Window
{
    private readonly List<HoleVm> _all = new();
    private string _filter = "";

    /// <summary>用户确认后的结果: null = 全部可展绘孔; 非空 = 勾选的子集。</summary>
    public IReadOnlyList<GeoDbViews.BoreholeRow>? Result { get; private set; }

    /// <summary>XAML 编译器/设计器用。</summary>
    public BoreholeSelectDialog() { InitializeComponent(); }

    public BoreholeSelectDialog(GeoDbContext ctx) : this(GeoDbViews.LoadBoreholes(ctx.Conn)) { }

    public BoreholeSelectDialog(IEnumerable<GeoDbViews.BoreholeRow> holes)
    {
        InitializeComponent();
        foreach (var h in holes.OrderBy(h => h.HoleId, StringComparer.OrdinalIgnoreCase))
        {
            bool drawable = h.ZCollar is not null && h.DepthTotal is > 0;
            string label = drawable
                ? $"{h.HoleId}   {h.ZCollar!.Value:F0}m / 深{h.DepthTotal!.Value:F0}m"
                : $"{h.HoleId}   (缺高程/孔深，不可展绘)";
            _all.Add(new HoleVm(h, drawable, label, RefreshSummary));
        }
        filterBox.TextChanged += OnFilterChanged;
        ApplyFilter();
        RefreshSummary();
        Opened += (_, _) => filterBox.Focus();
    }


    private bool FilterPredicate(HoleVm vm)
        => _filter.Length == 0
           || (vm.Hole.HoleId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
           || (vm.Hole.Category?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void ApplyFilter() => holeItems.ItemsSource = _all.Where(FilterPredicate).ToList();

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = filterBox.Text?.Trim() ?? "";
        ApplyFilter();
        RefreshSummary();
    }

    private IEnumerable<HoleVm> FilteredDrawable() => _all.Where(FilterPredicate).Where(v => v.Drawable);

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        var vis = FilteredDrawable().ToList();
        bool target = vis.Any(v => !v.IsChecked);   // 只要有未勾选的就全勾上; 否则全取消
        foreach (var v in vis) v.SetCheckedSilent(target);
        RefreshSummary();
    }

    /// <summary>刷新计数 + 全选框三态(全勾=✓ / 全不勾=✗ / 部分=■)。</summary>
    private void RefreshSummary()
    {
        int checkedCount = _all.Count(v => v.IsChecked);
        int drawable = _all.Count(v => v.Drawable);
        countText.Text = $"共 {_all.Count} 孔（可展绘 {drawable}），已勾选 {checkedCount}";
        var vis = FilteredDrawable().ToList();
        int visChecked = vis.Count(v => v.IsChecked);
        selectAllBox.IsChecked = vis.Count == 0 ? false : visChecked == 0 ? false : visChecked == vis.Count ? true : null;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var sel = _all.Where(v => v.IsChecked && v.Drawable).Select(v => v.Hole).ToList();
        if (sel.Count == 0) { countText.Text = "请至少勾选一个钻孔（或用顶部「全选」）"; return; }
        int drawable = _all.Count(v => v.Drawable);
        Result = sel.Count == drawable ? null : sel;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>
    /// 「展绘钻孔」完整流程(原插件 Ribbon 命令): 选孔对话框 → BoreholeColumnBuilder 生成柱状图 → 清旧图层后入场景 →
    /// 状态栏回报 孔数/煤层段数/跳过数(照原文案)。
    /// </summary>
    public static async Task DrawBoreholesAsync(GeoDbContext ctx)
    {
        List<GeoDbViews.BoreholeRow> holes;
        try { holes = GeoDbViews.LoadBoreholes(ctx.Conn); }
        catch (Exception ex) { ctx.Status($"展绘钻孔：读取钻孔失败 {ex.Message}"); return; }
        if (holes.Count == 0) { ctx.Status("展绘钻孔：数据库中没有钻孔"); return; }

        var dlg = new BoreholeSelectDialog(holes);
        bool ok = await dlg.ShowDialog<bool>(ctx.Owner);
        if (!ok) { ctx.Status("展绘钻孔：已取消"); return; }

        var r = GeoDbViews.BuildBoreholeColumns(ctx.Conn, dlg.Result);
        if (r.HoleCount == 0) { ctx.Status($"展绘钻孔：没有可展绘的钻孔（跳过 {r.SkippedHoles} 孔，缺孔口高程或孔深）"); return; }
        ctx.RemoveLayerEntities(GeoDbViews.BoreholeColumnLayerName);
        ctx.AddToScene(r.Entities, GeoDbViews.BoreholeColumnLayerName, r.Bounds);
        ctx.RunCommand("3D");   // 柱体是竖直三维几何, 俯视只看得到圆截面 —— 同原版, 展绘后进三维视图
        string tail = (r.SkippedHoles > 0 || r.SkippedSeams > 0) ? $"（跳过 {r.SkippedHoles} 孔 / {r.SkippedSeams} 层：缺高程或厚度）" : "";
        ctx.Status($"展绘钻孔：已生成 {r.HoleCount} 孔柱状图，{r.SeamCount} 个煤层段，{r.MeshGroups} 个合并网格 → 图层「{GeoDbViews.BoreholeColumnLayerName}」{tail}");
    }

    /// <summary>行视图模型: 勾选状态 + 显示标签。</summary>
    private sealed class HoleVm : INotifyPropertyChanged
    {
        private readonly Action _onChanged;
        private bool _isChecked;

        public HoleVm(GeoDbViews.BoreholeRow hole, bool drawable, string label, Action onChanged)
        { Hole = hole; Drawable = drawable; Label = label; _onChanged = onChanged; }

        public GeoDbViews.BoreholeRow Hole { get; }
        public bool Drawable { get; }
        public string Label { get; }
        public string Tip => Drawable ? "勾选以展绘该钻孔" : "该孔缺孔口高程或孔深，无法展绘";

        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(nameof(IsChecked)); _onChanged(); }
        }

        public void SetCheckedSilent(bool value)
        { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
