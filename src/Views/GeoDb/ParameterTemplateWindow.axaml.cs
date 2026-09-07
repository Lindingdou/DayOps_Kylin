using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// ② 参数模板库窗体(忠实原 GeoDataBase.ParameterTemplateWindow)。
/// 左:模板列表;右:模板下所有参数按"系统/环节"分组的推荐值表格。
/// 编辑后点"保存所有改动"批量 UPSERT 到 template_param_value。
/// </summary>
public partial class ParameterTemplateWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<ProcTemplate> _templates = new();
    private ProcTemplate? _selectedTpl;

    /// <summary>当前编辑中的"参数 → 值"行,提交时 upsert。</summary>
    private readonly Dictionary<long, ParamValueRow> _editableRows = new();

    /// <summary>仅供 XAML 编译器/设计器; 运行时用 <see cref="ParameterTemplateWindow(GeoDbContext)"/>。</summary>
    public ParameterTemplateWindow() { _ctx = null!; InitializeComponent(); }

    public ParameterTemplateWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        templateList.ItemsSource = _templates;
        Opened += (_, _) => LoadTemplates();
    }

    private void LoadTemplates()
    {
        _templates.Clear();
        try
        {
            foreach (var t in ProcTemplates(_ctx.Conn, activeOnly: false)) _templates.Add(t);
            statusText.Text = $"加载 {_templates.Count} 个模板";
        }
        catch (Exception ex) { statusText.Text = "加载失败:" + ex.Message; }
    }

    private void OnTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedTpl = templateList.SelectedItem as ProcTemplate;
        if (_selectedTpl == null) { paramGroupsPanel.Children.Clear(); return; }
        RenderTemplateDetail(_selectedTpl);
    }

    private void RenderTemplateDetail(ProcTemplate tpl)
    {
        tplHeaderText.Text = $"{tpl.Name}  ·  {tpl.Version}";
        tplInfoText.Text = $"物料 {tpl.ApplicableMaterial ?? "通用"} · 硬度 {tpl.ApplicableHardness ?? "—"} · 状态 {tpl.Status}";

        paramGroupsPanel.Children.Clear();
        _editableRows.Clear();

        // 加载该模板已有的值
        var existing = ProcValuesByTemplate(_ctx.Conn, tpl.TemplateId).ToDictionary(v => v.ParamId);

        // 按系统/环节分组列出所有参数,每个参数一行(原: 全部显示)
        foreach (var sys in ProcessSystems(_ctx.Conn).OrderBy(s => s.DisplayOrder))
        {
            var phases = ProcessPhasesBySystem(_ctx.Conn, sys.SystemId);
            if (phases.Count == 0) continue;

            paramGroupsPanel.Children.Add(new TextBlock
            {
                Text = $"📦 {sys.Name}", FontWeight = FontWeight.Bold, FontSize = 13,
                Foreground = Brushes.SteelBlue, Margin = new Thickness(0, 16, 0, 6)
            });

            foreach (var ph in phases)
            {
                var ps = ProcessParamsByPhase(_ctx.Conn, ph.PhaseId);
                if (ps.Count == 0) continue;

                paramGroupsPanel.Children.Add(new TextBlock
                {
                    Text = $"⚙ {ph.Name}", FontWeight = FontWeight.SemiBold, FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)), Margin = new Thickness(8, 8, 0, 4)
                });

                var grid = new Grid { Margin = new Thickness(16, 0, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                int row = 0;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddHeader(grid, row, 0, "参数");
                AddHeader(grid, row, 1, "单位");
                AddHeader(grid, row, 2, "标准范围");
                AddHeader(grid, row, 3, "本模板值");
                AddHeader(grid, row, 4, "本模板范围");
                AddHeader(grid, row, 5, "备注");
                row++;

                foreach (var p in ps)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var rowObj = new ParamValueRow { ParamId = p.ParamId };
                    _editableRows[p.ParamId] = rowObj;

                    var nameTb = new TextBlock { Text = p.Name, Margin = new Thickness(0, 4, 8, 4), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    ToolTip.SetTip(nameTb, p.Code);
                    Place(grid, nameTb, row, 0);

                    var unitTb = new TextBlock { Text = string.IsNullOrEmpty(p.Unit) ? "—" : p.Unit, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    Place(grid, unitTb, row, 1);

                    var stdTb = new TextBlock
                    {
                        Text = $"{p.StandardMin?.ToString("F1") ?? "—"} ~ {p.StandardMax?.ToString("F1") ?? "—"} (默 {p.StandardDefault?.ToString("F1") ?? "—"})",
                        FontSize = 11, Foreground = Brushes.DarkSlateBlue, Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    };
                    Place(grid, stdTb, row, 2);

                    // 本模板推荐值(可编辑)
                    var recBox = new TextBox { Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 2, 4, 2), FontWeight = FontWeight.Bold, Background = Brushes.LightYellow, MinHeight = 26 };
                    if (existing.TryGetValue(p.ParamId, out var v))
                    {
                        recBox.Text = v.RecommendedValue?.ToString("F2") ?? v.TextValue ?? "";
                        rowObj.MinValue = v.MinValue;
                        rowObj.MaxValue = v.MaxValue;
                        rowObj.Notes = v.Notes;
                        rowObj.Id = v.Id;
                    }
                    rowObj.RecommendedText = recBox.Text ?? "";
                    recBox.TextChanged += (_, _) => rowObj.RecommendedText = recBox.Text ?? "";
                    Place(grid, recBox, row, 3);

                    // 本模板范围 min~max
                    var rangeBox = new TextBox
                    {
                        Text = rowObj.MinValue.HasValue || rowObj.MaxValue.HasValue
                            ? $"{rowObj.MinValue?.ToString("F1") ?? ""}~{rowObj.MaxValue?.ToString("F1") ?? ""}" : "",
                        Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 2, 4, 2), FontSize = 11, MinHeight = 26
                    };
                    ToolTip.SetTip(rangeBox, "格式: 下限~上限,如 200~250");
                    rowObj.RangeText = rangeBox.Text ?? "";
                    rangeBox.TextChanged += (_, _) => rowObj.RangeText = rangeBox.Text ?? "";
                    Place(grid, rangeBox, row, 4);

                    // 备注
                    var noteBox = new TextBox { Text = rowObj.Notes ?? "", Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 2, 0, 2), FontSize = 11, Width = 220, MinHeight = 26 };
                    noteBox.TextChanged += (_, _) => rowObj.Notes = noteBox.Text;
                    Place(grid, noteBox, row, 5);

                    row++;
                }
                paramGroupsPanel.Children.Add(grid);
            }
        }
    }

    private static void Place(Grid grid, Control c, int row, int col)
    {
        Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c);
    }

    private static void AddHeader(Grid grid, int row, int col, string text)
    {
        var tb = new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 4, 4) };
        Place(grid, tb, row, col);
    }

    // ─── 模板增删改 ──────────────────────────────────────────────────

    private async void OnNewTemplate(object? sender, RoutedEventArgs e)
    {
        var code = await ProcessDialogs.PromptTextAsync(this, "新建模板", "请输入编码(如 'hard_rock_v1.0'):");
        if (string.IsNullOrWhiteSpace(code)) return;
        var name = await ProcessDialogs.PromptTextAsync(this, "新建模板", "请输入显示名(如 '硬岩区标准 v1.0'):");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var t = new ProcTemplate { Code = code.Trim(), Name = name.Trim(), Version = "v1.0", IsCurrent = true, Status = "active" };
            t.TemplateId = ProcInsertTemplate(_ctx.Conn, t);
            LoadTemplates();
            templateList.SelectedItem = _templates.FirstOrDefault(x => x.TemplateId == t.TemplateId);
            statusText.Text = $"已新建模板 {t.Name}";
        }
        catch (Exception ex) { statusText.Text = "新建失败:" + ex.Message; }
    }

    private void OnCloneTemplate(object? sender, RoutedEventArgs e)
    {
        if (_selectedTpl == null) { statusText.Text = "请先选中要复制的模板"; return; }
        try
        {
            var src = _selectedTpl;
            var newTpl = new ProcTemplate
            {
                Code = src.Code + "_copy_" + DateTime.Now.ToString("HHmmss"),
                Name = src.Name + " (副本)",
                Description = src.Description,
                ApplicableMaterial = src.ApplicableMaterial,
                ApplicableHardness = src.ApplicableHardness,
                Version = ProcBumpVersion(src.Version),
                IsCurrent = false,
                Status = "draft"
            };
            newTpl.TemplateId = ProcInsertTemplate(_ctx.Conn, newTpl);
            ProcCloneTemplateValues(_ctx.Conn, src.TemplateId, newTpl.TemplateId);
            LoadTemplates();
            templateList.SelectedItem = _templates.FirstOrDefault(x => x.TemplateId == newTpl.TemplateId);
            statusText.Text = $"已复制为 {newTpl.Name}";
        }
        catch (Exception ex) { statusText.Text = "复制失败:" + ex.Message; }
    }

    private async void OnArchiveTemplate(object? sender, RoutedEventArgs e)
    {
        if (_selectedTpl == null) return;
        if (!await ProcessDialogs.ConfirmAsync(this, "确认", $"归档 「{_selectedTpl.Name}」?归档后不会在主列表显示。")) return;
        try
        {
            ProcArchiveTemplate(_ctx.Conn, _selectedTpl.TemplateId);
            LoadTemplates();
            statusText.Text = "已归档";
        }
        catch (Exception ex) { statusText.Text = "归档失败:" + ex.Message; }
    }

    private async void OnDeleteTemplate(object? sender, RoutedEventArgs e)
    {
        if (_selectedTpl == null) return;
        if (!await ProcessDialogs.ConfirmAsync(this, "确认", $"彻底删除「{_selectedTpl.Name}」?所有参数值一并删除。")) return;
        try
        {
            ProcDeleteTemplate(_ctx.Conn, _selectedTpl.TemplateId);
            LoadTemplates();
            paramGroupsPanel.Children.Clear();
            statusText.Text = "已删除";
        }
        catch (Exception ex) { statusText.Text = "删除失败:" + ex.Message; }
    }

    // ─── 保存 / 查引用 ───────────────────────────────────────────────

    private void OnSaveAll(object? sender, RoutedEventArgs e)
    {
        if (_selectedTpl == null) { statusText.Text = "请先选中模板"; return; }
        try
        {
            int upserted = 0, skipped = 0;
            foreach (var (paramId, row) in _editableRows)
            {
                var entity = ProcBuildTemplateValue(_selectedTpl.TemplateId, paramId, row.RecommendedText, row.RangeText, row.Notes);
                if (entity == null) { skipped++; continue; }
                ProcUpsertTemplateValue(_ctx.Conn, entity);
                upserted++;
            }
            statusInline.Text = $"✅ 保存 {upserted} 项,跳过空 {skipped} 项";
            statusText.Text = "保存成功";
        }
        catch (Exception ex)
        {
            statusInline.Text = "保存失败:" + ex.Message;
            statusText.Text = "保存失败:" + ex.Message;
        }
    }

    private async void OnViewUsage(object? sender, RoutedEventArgs e)
    {
        if (_selectedTpl == null) return;
        try
        {
            var usage = ProcTemplateUsage(_ctx.Conn, _selectedTpl.TemplateId);
            if (usage.Count == 0)
            {
                await ProcessDialogs.InfoAsync(this, "引用统计", "当前没有平盘引用此模板。");
                return;
            }
            var lines = usage.Select(u => $"  • 平盘 {u.LocationCode} / 环节 {u.PhaseName}");
            await ProcessDialogs.InfoAsync(this, $"模板「{_selectedTpl.Name}」的引用",
                "以下平盘+环节正在使用此模板:\n\n" + string.Join("\n", lines));
        }
        catch (Exception ex) { statusText.Text = "查询失败:" + ex.Message; }
    }

    private sealed class ParamValueRow
    {
        public long Id { get; set; }
        public long ParamId { get; set; }
        public string RecommendedText { get; set; } = "";
        public string RangeText { get; set; } = "";
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public string? Notes { get; set; }
    }
}
