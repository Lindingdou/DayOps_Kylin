using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 块体浏览器（忠实原 BlockModelBrowserView Dock 面板，Kylin 作独立窗）：每个模型一行可展开卡片——
/// 可见性复选 / 名称 / 块数 / 活动小点；展开后 存储模式 / 网格数 / 块尺寸 / AABB / 属性列 / 着色属性下拉 / 色带下拉 / 筛选条件 / 已删 cell，
/// 以及 设为活动 / 缩放至 / 重命名 / 删除。空态提示 + 底部状态条。
/// </summary>
public partial class BlockModelBrowserWindow : Window
{
    private readonly ModelingContext _ctx;
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#0D6EFD"));
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#DEE2E6"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush Border1 = new SolidColorBrush(Color.Parse("#DCDFE4"));

    public BlockModelBrowserWindow() { _ctx = null!; InitializeComponent(); }

    public BlockModelBrowserWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        BlockModelStore.Models.CollectionChanged += OnModelsChanged;
        BlockModelStore.ActiveChanged += OnStoreChanged;
        BlockModelStore.DisplayChanged += OnStoreChanged;
        Closed += (_, _) =>
        {
            BlockModelStore.Models.CollectionChanged -= OnModelsChanged;
            BlockModelStore.ActiveChanged -= OnStoreChanged;
            BlockModelStore.DisplayChanged -= OnStoreChanged;
        };
        Rebuild();
    }

    // 延后到下一帧重建：卡片内下拉/按钮的事件里触发的刷新，不能在其自身事件中拆掉控件
    private void OnModelsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
    private void OnStoreChanged(object? s, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        var expanded = new HashSet<Guid>();
        foreach (var child in modelsList.Children) if (child is Border b && b.Child is Expander ex && ex.IsExpanded && b.Tag is Guid id) expanded.Add(id);
        modelsList.Children.Clear();
        foreach (var m in BlockModelStore.Models) modelsList.Children.Add(BuildCard(m, expanded.Contains(m.Id)));
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        int n = BlockModelStore.Models.Count;
        countLabel.Text = $"共 {n} 个";
        emptyHint.IsVisible = n == 0;
        if (n == 0) { statusLabel.Text = "尚未创建任何块体模型"; return; }
        var act = BlockModelStore.Active;
        statusLabel.Text = act != null ? $"活动模型：{act.Name} ({act.BlockCount:N0} 块)" : $"{n} 个模型（无活动模型）";
    }

    private Control BuildCard(BlockModelMeta m, bool expanded)
    {
        var card = new Border
        {
            Margin = new Thickness(6, 4), CornerRadius = new CornerRadius(3), Background = Brushes.White, Tag = m.Id,
            BorderBrush = m.IsActive ? Blue : Border1, BorderThickness = new Thickness(m.IsActive ? 2 : 1),
        };
        // 头：可见性 + 名称/块数 + 活动小点
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 2) };
        var vis = new CheckBox { IsChecked = m.IsVisible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        ToolTip.SetTip(vis, "切换视口可见性");
        vis.IsCheckedChanged += (_, _) => BlockModelStore.SetVisibility(_ctx, m, vis.IsChecked == true);
        var names = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = m.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = m.IsActive ? FontWeight.Bold : FontWeight.Normal, Foreground = m.IsActive ? Blue : Brushes.Black });
        names.Children.Add(new TextBlock { Text = $"{m.BlockCount:N0} 块", FontSize = 10, Foreground = Muted });
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, Fill = m.IsActive ? Blue : Grey };
        Grid.SetColumn(names, 1); Grid.SetColumn(dot, 2);
        header.Children.Add(vis); header.Children.Add(names); header.Children.Add(dot);

        // 展开：详情 + 工具按钮
        var detail = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*"), Margin = new Thickness(22, 4, 8, 8) };
        int row = 0;
        void Row(string key, Control value)
        {
            detail.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = key, FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(k, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            detail.Children.Add(k); detail.Children.Add(value);
            row++;
        }
        TextBlock V(string t) => new() { Text = t, FontSize = 11, Margin = new Thickness(0, 2), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Row("存储模式", V(m.StorageMode.ToString()));
        Row("网格数", V(m.DimensionsText));
        Row("块尺寸", V(m.BlockSizeText));
        Row("AABB", V(m.BoundsText));
        Row("属性列", V($"{m.PropertySchema.Count} 列"));
        var attrCombo = new ComboBox { ItemsSource = m.ColormapChoices, SelectedItem = m.ColormapChoiceText, FontSize = 11, Height = 24, Margin = new Thickness(0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        attrCombo.SelectionChanged += (_, e) =>
        {
            if (e.RemovedItems.Count == 0) return;
            m.ColormapChoiceText = attrCombo.SelectedItem?.ToString() ?? "";
            m.ColormapRange = null;
            BlockModelStore.RefreshDisplay(_ctx, m);
        };
        Row("着色属性", attrCombo);
        var presets = Enum.GetValues<BlockColormapPreset>().Select(p => p.ToShortLabel()).ToList();
        var paletteCombo = new ComboBox { ItemsSource = presets, SelectedIndex = (int)m.DisplayStyle.DefaultColormap, FontSize = 11, Height = 24, Margin = new Thickness(0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        paletteCombo.SelectionChanged += (_, e) =>
        {
            if (e.RemovedItems.Count == 0 || paletteCombo.SelectedIndex < 0) return;
            m.DisplayStyle.DefaultColormap = (BlockColormapPreset)paletteCombo.SelectedIndex;
            BlockModelStore.RefreshDisplay(_ctx, m);
        };
        Row("色带", paletteCombo);
        Row("筛选条件", V(m.Filter?.ToString() ?? "（无筛选）"));
        Row("已删 cell", V(m.DeletedBlockCount.ToString("N0")));

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 8, 8), Spacing = 4 };
        Button Mini(string text, bool danger = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 3), FontSize = 11 };
            if (danger) b.Foreground = new SolidColorBrush(Color.Parse("#DC3545"));
            return b;
        }
        var setActive = Mini("设为活动"); setActive.Click += (_, _) => { BlockModelStore.Active = m; BlockModelStore.RefreshDisplay(_ctx, m); };
        var zoom = Mini("缩放至"); zoom.Click += (_, _) => BlockModelStore.ZoomTo(_ctx, m);
        var rename = Mini("重命名"); rename.Click += async (_, _) =>
        {
            var dlg = await PromptDialog.AskAsync(this, "重命名块体模型", new[] { new PromptDialog.Field("name", "新名称：", m.Name, Numeric: false) });
            if (dlg == null) return;
            var newName = dlg.S("name").Trim();
            if (newName.Length == 0 || newName == m.Name) return;
            var err = BlockModelStore.Rename(m, newName);
            if (err != null) await BlockMsgBox.WarnAsync(this, "重命名失败", err);
        };
        var del = Mini("删除", true); del.Click += async (_, _) =>
        {
            bool ok = await BlockMsgBox.ConfirmAsync(this, "确认删除", $"确定要删除块体模型 \"{m.Name}\" 吗？\n（{m.BlockCount:N0} 块；操作不可撤销）");
            if (!ok) return;
            var err = BlockModelStore.Remove(_ctx, m);
            if (err != null) await BlockMsgBox.WarnAsync(this, "删除失败", err);
        };
        tools.Children.Add(setActive); tools.Children.Add(zoom); tools.Children.Add(rename); tools.Children.Add(del);

        var body = new StackPanel();
        body.Children.Add(detail); body.Children.Add(tools);
        card.Child = new Expander { Header = header, Content = body, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        return card;
    }
}
