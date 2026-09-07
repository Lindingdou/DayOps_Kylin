using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 数据导入导出中心(忠实原 DataImportCenterWindow + DataImportCenter.cs): 左 数据类型列表(设备台账/月度产能/
/// 月度可用率(KPI)/生产班次记录/故障记录), 右 详情(模板列·必填红·说明·重复策略) + 下载模板 / 导入文件 / 导出全部。
/// 原 Excel(.xlsx) 在 Kylin 无 Excel 库 → 统一 CSV(同列头, 带 BOM)。
/// </summary>
public partial class DataImportCenterWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly List<EqImportSpec> _specs = EqImportSpecs();

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public DataImportCenterWindow() { _ctx = null!; InitializeComponent(); }

    public DataImportCenterWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        typesList.ItemsSource = _specs;
        if (_specs.Count > 0) typesList.SelectedIndex = 0;
    }

    private EqImportSpec? Current => typesList.SelectedItem as EqImportSpec;

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var spec = Current;
        if (spec == null) return;
        detailName.Text = spec.Name;
        detailDesc.Text = spec.Description;
        colPanel.Children.Clear();
        var reqSet = new HashSet<string>(spec.Required);
        foreach (var h in spec.Headers)
        {
            bool req = reqSet.Contains(h);
            colPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 3), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.Parse(req ? "#FDE8E8" : "#EEF2F6")),
                BorderBrush = new SolidColorBrush(Color.Parse(req ? "#C0392B" : "#CBD5E1")), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = req ? h + " *" : h, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse(req ? "#C0392B" : "#334155")) },
            });
        }
        notesText.Text = spec.Notes.Length > 0 ? "说明:" + string.Join("  ", spec.Notes) : "";
        statusText.Text = $"已选:{spec.Name}({spec.Headers.Length} 列,必填 {spec.Required.Length} 列)";
    }

    private async void OnTemplateClick(object? sender, RoutedEventArgs e)
    {
        var spec = Current; if (spec == null) return;
        try
        {
            var path = await _ctx.SaveTextAsync("下载导入模板(CSV)", $"{spec.Name}_导入模板.csv", EqTemplateCsv(spec));
            if (path == null) return;
            resultText.Text = $"✔ 已生成「{spec.Name}」模板:\n{path}\n\n请按表头填写(红色为必填),删除示例行后即可导入。";
            statusText.Text = "模板已生成";
        }
        catch (Exception ex) { await Fail("生成模板失败", ex); }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var spec = Current; if (spec == null) return;
        var path = await _ctx.OpenFileAsync($"导入{spec.Name}(CSV)", new[] { "*.csv", "*.txt" });
        if (path == null) return;
        bool overwrite = policyOverwrite.IsChecked == true;
        try
        {
            var outcome = EqImportCsv(_ctx.Conn, spec, File.ReadAllText(path), overwrite);
            resultText.Text = $"【{spec.Name}】\n{outcome.ToSummary()}\n\n提示:相关窗口需重新打开或点「重新加载」以显示新数据。";
            statusText.Text = $"导入完成:新增 {outcome.Inserted} / 更新 {outcome.Updated} / 跳过 {outcome.Skipped}";
        }
        catch (Exception ex) { await Fail("导入失败", ex); }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var spec = Current; if (spec == null) return;
        try
        {
            var csv = EqExportCsv(_ctx.Conn, spec);
            var path = await _ctx.SaveTextAsync("导出全部(CSV)", $"{spec.Name}_{DateTime.Now:yyyyMMdd}.csv", csv);
            if (path == null) return;
            resultText.Text = $"✔ 已导出「{spec.Name}」当前全部记录:\n{path}\n\n该文件与模板同列,可修改后再导入(闭环)。";
            statusText.Text = "导出完成";
        }
        catch (Exception ex) { await Fail("导出失败", ex); }
    }

    private async System.Threading.Tasks.Task Fail(string title, Exception ex)
    {
        resultText.Text = $"✘ {title}:{ex.Message}";
        statusText.Text = title;
        await EquipmentMessageBox.Info(this, ex.Message, title);
    }
}
