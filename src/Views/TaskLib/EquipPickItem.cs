// 忠实移植自原 PitMine3D Modules/TaskLib/Features/EquipPickItem.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using DbEq = PitMine3D.Kylin.Data.Entities.Equipment;

namespace PitMine3D.Kylin.Views.TaskLib;

// ─────────────────────────────────────────────────────────────────────────────
//  设备下拉里的一项 —— 让人一眼看出「这是台什么」。
//
//  ══ 原来是什么样 ══
//  下拉里只有一串编号：`1746`、`1751`、`3012`…… 光看编号分不出是电铲还是水车，
//  而检修档期、归属覆盖这些地方**挑错一台的代价是整条链**：
//  给水车排了检修档期，装箱时扣的是别人的时窗。
//
//  ══ 图标为什么用色块+汉字，不用图片 ══
//  ① 不依赖任何资源文件 —— 下拉在多个窗口复用，缺一张图就是空白方块；
//  ② 缩放/主题都不会糊；
//  ③ **颜色不是唯一信息**：色块里写着「铲/车/钻/推/平/洒」，
//     色盲或黑白截图下照样分得清 —— 只靠颜色编码的界面等于对一部分人不可用。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>设备下拉的一项。</summary>
public sealed class EquipPickItem
{
    /// <summary>设备编号 —— <b>写回台账用的就是它</b>，显示怎么变都不影响这个值。</summary>
    public string Id { get; init; } = "";

    /// <summary>类别缩写，画在色块里（铲/车/钻/装/推/平/洒/其）。</summary>
    public string Badge { get; init; } = "其";
    /// <summary>色块颜色（#RRGGBB）。</summary>
    public string BadgeColor { get; init; } = "#FF78909C";

    /// <summary>类别中文名（电铲 / 矿用卡车 …）。</summary>
    public string Category { get; init; } = "";
    public string Model { get; init; } = "";
    public string Status { get; init; } = "";

    /// <summary>不在用时整项压暗 —— 报废/待报废的设备排不了工，挑到就是白排。</summary>
    public bool InUse { get; init; } = true;

    /// <summary>编号后面那一串说明。</summary>
    public string Detail =>
        string.Join(" · ", new[] { Category, Model, InUse ? "" : Status }
                           .Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>可编辑下拉里键入匹配用的文本，也是 <c>Text</c> 回读的值。</summary>
    public override string ToString() => Id;

    // ── 工厂 ──────────────────────────────────────────────────────────

    private static (string Badge, string Color) Look(EquipmentCategory c) => c switch
    {
        EquipmentCategory.Shovel     => ("铲", "#FFD9822B"),   // 暖橙：挖装
        EquipmentCategory.Loader     => ("装", "#FFCA8A04"),
        EquipmentCategory.Truck      => ("车", "#FF2F6FEB"),   // 蓝：运输
        EquipmentCategory.Drill      => ("钻", "#FF8B5CF6"),   // 紫：穿孔
        EquipmentCategory.Dozer      => ("推", "#FF1D9E75"),   // 绿：排土
        EquipmentCategory.Grader     => ("平", "#FF0E7490"),
        EquipmentCategory.WaterTruck => ("洒", "#FF0891B2"),
        _                            => ("其", "#FF78909C"),
    };

    /// <summary>「在用」才排得了工。状态列存的是中文，枚举名是英文 —— 两种都认。</summary>
    public static bool IsInUse(string? status)
    {
        var s = (status ?? "").Trim();
        return s.Length == 0
            || s.Contains("在用")
            || s.Equals("InUse", StringComparison.OrdinalIgnoreCase)
            || s.Equals("Active", StringComparison.OrdinalIgnoreCase);
    }

    public static EquipPickItem From(DbEq e)
    {
        var cat = Enum.TryParse<EquipmentCategory>(e?.Category, true, out var c) ? c : EquipmentCategory.Other;
        var (badge, color) = Look(cat);
        return new EquipPickItem
        {
            Id = (e?.EquipmentId ?? "").Trim(),
            Badge = badge,
            BadgeColor = color,
            Category = CategoryZh(cat),
            Model = (e?.Model ?? "").Trim(),
            Status = (e?.Status ?? "").Trim(),
            InUse = IsInUse(e?.Status),
        };
    }

    /// <summary>类别中文名。与 <c>Equipment.CategoryDisplay</c> 同一份说法。</summary>
    public static string CategoryZh(EquipmentCategory c) => c switch
    {
        EquipmentCategory.Shovel => "电铲",
        EquipmentCategory.Truck => "矿用卡车",
        EquipmentCategory.Drill => "钻机",
        EquipmentCategory.Loader => "前装机",
        EquipmentCategory.Dozer => "推土机",
        EquipmentCategory.Grader => "平路机",
        EquipmentCategory.WaterTruck => "水车/水鹤",
        _ => "其他",
    };

    /// <summary>
    /// 一批设备 → 下拉项。<b>先按类别再按编号</b>：同类的排在一起，找起来快。
    /// <para><b>不在用的排在最后</b>，不是滤掉 —— 历史档期可能挂在一台已报废的设备上，
    /// 滤掉的话那条档期就永远编辑不了了。</para>
    /// </summary>
    public static List<EquipPickItem> Build(IEnumerable<DbEq>? src)
    {
        return (src ?? Array.Empty<DbEq>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.EquipmentId))
            .Select(From)
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.InUse ? 0 : 1)
            .ThenBy(x => Order(x.Badge))
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>类别显示次序：按工序链排（穿孔 → 挖装 → 运输 → 排土 → 辅助）。</summary>
    private static int Order(string badge) => badge switch
    {
        "钻" => 0, "铲" => 1, "装" => 2, "车" => 3, "推" => 4, "平" => 5, "洒" => 6, _ => 7,
    };
}

/// <summary>
/// bool → 不透明度：false 压到 0.45。
/// <para>「不在用」的设备**留在列表里但压暗** —— 滤掉的话，挂在已报废设备上的历史档期
/// 就永远编辑不了了；不压暗的话，人会把它当成能排工的设备挑走。</para>
/// </summary>
/// <summary>Avalonia 侧：不在用的设备在下拉里压暗（原 WPF DimIfFalseConverter）。</summary>
public static class EquipPickLook
{
    public static double OpacityOf(bool inUse) => inUse ? 1.0 : 0.45;

    /// <summary>下拉项模板：色块（汉字）+ 编号 + 明细，与原 XAML DataTemplate 逐项对应。</summary>
    public static Avalonia.Controls.Templates.FuncDataTemplate<EquipPickItem> ItemTemplate() => new((it, _) =>
    {
        var sp = new Avalonia.Controls.StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        if (it == null) return sp;
        double op = OpacityOf(it.InUse);
        sp.Children.Add(new Avalonia.Controls.Border
        {
            Width = 20, Height = 18, CornerRadius = new Avalonia.CornerRadius(3), Margin = new Avalonia.Thickness(0, 0, 7, 0),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(it.BadgeColor)), Opacity = op,
            Child = new Avalonia.Controls.TextBlock { Text = it.Badge, Foreground = Avalonia.Media.Brushes.White, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
        });
        var id = new Avalonia.Controls.TextBlock { Text = it.Id, FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Opacity = op };
        TaskUi.Theme(id, Avalonia.Controls.TextBlock.ForegroundProperty, "Theme.Text.Body");
        sp.Children.Add(id);
        var det = new Avalonia.Controls.TextBlock { Text = it.Detail, Margin = new Avalonia.Thickness(7, 0, 0, 0), FontSize = 11.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Opacity = op };
        TaskUi.Theme(det, Avalonia.Controls.TextBlock.ForegroundProperty, "Theme.Text.Muted");
        sp.Children.Add(det);
        return sp;
    });
}
