// 忠实移植自原 PitMine3D Modules/TaskLib/Features/ShiftSelector.cs（逐行对应；WPF ComboBox → Avalonia ComboBox）
using Avalonia.Controls;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 班次下拉框的统一装配。八个窗口共用一套：取值域来自当日班制（<see cref="ShiftScope"/>），
/// <b>默认落在当前班</b>——不是「全部」，也不是写死的「中班」。
///
/// <para>
/// 原先每个窗口的 XAML 里各写一遍 <c>&lt;ComboBoxItem&gt;早班&lt;/ComboBoxItem&gt;</c>，
/// 代码里再 <c>SelectedIndex = 0/1</c> 拍一个默认值。班制一改，下拉框还是老三样，
/// 而任务的 <c>Shift</c> 来自盘子——筛选结果恒空，界面却看不出任何异样。
/// </para>
/// </summary>
internal static class ShiftSelector
{
    /// <summary>
    /// 用当日真实班制填一个班次下拉框，并选中当前班；返回选中的班次名。
    /// </summary>
    /// <param name="includeAll">
    /// 首项是否给「全部」。只有确有整天批量操作的窗口才给（任务下达 / 派车单 / 实绩录入）；
    /// 任务书、派工这类<b>本身就是一班一份</b>的单据不给——给了就会印出一张跨班的任务书。
    /// 即便给了，默认仍落当前班。
    /// </param>
    public static string Bind(ComboBox combo, bool includeAll)
    {
        combo.Items.Clear();
        if (includeAll) combo.Items.Add(new ComboBoxItem { Content = ShiftScope.All });
        foreach (var name in ShiftScope.Names) combo.Items.Add(new ComboBoxItem { Content = name });
        if (combo.Items.Count == 0) return "";

        string current = ShiftScope.Current;
        int idx = -1;
        for (int i = 0; i < combo.Items.Count; i++)
            if (TextOf(combo.Items[i]) == current) { idx = i; break; }

        // 当前班认不出来（班制没排满全天等）：宁可停在第一个真实班次，也不要停在「全部」——
        // 默认「全部」正是这轮要改掉的东西。
        combo.SelectedIndex = idx >= 0 ? idx : (includeAll && combo.Items.Count > 1 ? 1 : 0);
        return Selected(combo);
    }

    /// <summary>选中的班次名；「全部」返回空串（＝不筛）。各窗口的筛选一律用它，别再各判各的。</summary>
    public static string Filter(ComboBox combo)
    {
        string s = Selected(combo);
        return s == ShiftScope.All ? "" : s;
    }

    /// <summary>选中项的原文（「全部」原样返回）。</summary>
    public static string Selected(ComboBox combo) => TextOf(combo.SelectedItem);

    private static string TextOf(object? item)
        => (item as ComboBoxItem)?.Content?.ToString() ?? item?.ToString() ?? "";
}
