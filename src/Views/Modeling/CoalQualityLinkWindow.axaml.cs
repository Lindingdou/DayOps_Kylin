using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 煤质属性联动（忠实原 CoalQualityLinkDialog）：选煤层属性列 → 每个不同值映射到煤层（自动匹配煤层码）→ 勾指标/样品/方法 →
/// 「预览」看每煤层煤块数与可用钻孔点，「运行」用 <see cref="BlockCoalQualityLink.Estimate"/> 写列。样本来自库 coal_sample。
/// </summary>
public partial class CoalQualityLinkWindow : Window
{
    public sealed class MapRow
    {
        public double Value { get; set; }
        public string ValueText { get; set; } = "";
        public int Count { get; set; }
        public List<string> SeamOptions { get; set; } = new();
        public string Seam { get; set; } = BlockCoalQualityLink.Skip;
    }

    private readonly ModelingContext _ctx;
    private readonly BlockModelMeta _model;
    private readonly List<string> _seamOptions = new() { BlockCoalQualityLink.Skip };
    private List<CoalSample>? _samples;

    public CoalQualityLinkWindow() { _ctx = null!; _model = null!; InitializeComponent(); }

    public CoalQualityLinkWindow(ModelingContext ctx, BlockModelMeta model)
    {
        _ctx = ctx; _model = model;
        InitializeComponent();
        modelInfo.Text = $"活动块体：{_model.Name}   （{_model.BlockCount:N0} 块）";
        try
        {
            _samples = Samples();
            _seamOptions.AddRange(_samples.Select(s => s.SeamCode).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s.Length).ThenBy(s => s));
        }
        catch { }
        attrCombo.ItemsSource = _model.PropertySchema.Select(c => c.Name).ToList();
        if (!string.IsNullOrEmpty(_model.CoalAttribute) && _model.PropertySchema.Any(c => c.Name == _model.CoalAttribute)) attrCombo.SelectedItem = _model.CoalAttribute;
        else
        {
            var cat = _model.PropertySchema.FirstOrDefault(c => c.IsCategorical);
            if (cat != null) attrCombo.SelectedItem = cat.Name; else if (_model.PropertySchema.Count > 0) attrCombo.SelectedIndex = 0;
        }
        methodCombo.ItemsSource = BlockCoalQualityLink.Methods;
        methodCombo.SelectedIndex = 1;   // 默认 IDW（块体 cell 多、OK 逐格慢）
    }

    private List<CoalSample> Samples()
    {
        if (_samples != null) return _samples;
        var conn = _ctx.Conn();
        _samples = conn != null ? GeoDataQueries.GetCoalSamples(conn) : new List<CoalSample>();
        return _samples;
    }

    private void OnAttrChanged(object? sender, SelectionChangedEventArgs e) => BuildMap();

    private void BuildMap()
    {
        var attr = attrCombo.SelectedItem?.ToString();
        var rows = new ObservableCollection<MapRow>();
        if (!string.IsNullOrEmpty(attr))
        {
            foreach (var (val, count, label) in BlockCoalQualityLink.DistinctValues(_model, attr))
            {
                string vtext = Math.Abs(val - Math.Round(val)) < 1e-9 ? ((long)Math.Round(val)).ToString(CultureInfo.InvariantCulture) : val.ToString("F2", CultureInfo.InvariantCulture);
                string auto = _seamOptions.FirstOrDefault(s => s != BlockCoalQualityLink.Skip && s == vtext)
                    ?? (string.IsNullOrEmpty(label) ? null : _seamOptions.Where(s => s != BlockCoalQualityLink.Skip).OrderByDescending(s => s.Length).FirstOrDefault(s => label!.Contains(s)))
                    ?? BlockCoalQualityLink.Skip;
                rows.Add(new MapRow { Value = val, ValueText = string.IsNullOrEmpty(label) ? vtext : $"{vtext} · {label}", Count = count, SeamOptions = _seamOptions, Seam = auto });
            }
        }
        mapList.ItemsSource = rows;
        statusText.Text = rows.Count == 0 ? "该列无数据" : $"{rows.Count} 个不同值";
    }

    private List<BlockCoalQualityLink.SeamMapping> GatherMapping()
        => (mapList.ItemsSource as IEnumerable<MapRow>)?.Where(r => r.Seam != BlockCoalQualityLink.Skip && !string.IsNullOrEmpty(r.Seam))
            .Select(r => new BlockCoalQualityLink.SeamMapping(r.Value, r.Seam)).ToList() ?? new List<BlockCoalQualityLink.SeamMapping>();

    private async void OnPreview(object? sender, RoutedEventArgs e)
    {
        var attr = attrCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(attr)) { await BlockMsgBox.WarnAsync(this, "提示", "请先选择煤层属性列。"); return; }
        var mapping = GatherMapping();
        if (mapping.Count == 0) { await BlockMsgBox.WarnAsync(this, "提示", "请至少把一个属性值映射到煤层。"); return; }
        try
        {
            var cells = BlockCoalQualityLink.PreviewCells(_model, attr, mapping);
            if (cells.Count == 0) { await BlockMsgBox.InfoAsync(this, "预览", "没有匹配到任何煤块。"); return; }
            var samples = Samples();
            var sb = new System.Text.StringBuilder("预检（每个煤层：煤块数 / 可用钻孔点）：\n\n");
            int noPts = 0;
            foreach (var (seam, n) in cells)
            {
                int pts = samples.Count(s => string.Equals(s.SeamCode, seam, StringComparison.OrdinalIgnoreCase) && s.Z.HasValue);
                if (pts == 0) noPts++;
                sb.AppendLine($"  {seam} 煤：{n:N0} 煤块 / {pts} 钻孔点" + (pts == 0 ? "   ⚠ 无点，将不估" : ""));
            }
            sb.Append($"\n合计煤块 {cells.Sum(c => c.Cells):N0}。");
            if (noPts > 0) sb.Append($"（有 {noPts} 个煤层没有钻孔点，这些煤块运行后不会被赋值）");
            await BlockMsgBox.InfoAsync(this, "预览", sb.ToString());
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "错误", $"预览失败：{ex.Message}"); }
    }

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        var attr = attrCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(attr)) { await BlockMsgBox.WarnAsync(this, "提示", "请先选择煤层属性列。"); return; }
        var mapping = GatherMapping();
        if (mapping.Count == 0) { await BlockMsgBox.WarnAsync(this, "提示", "请至少把一个属性值映射到煤层。"); return; }
        var indicators = new List<string>();
        if (cbAd.IsChecked == true) indicators.Add("ad");
        if (cbSt.IsChecked == true) indicators.Add("std");
        if (cbQnet.IsChecked == true) indicators.Add("qnet");
        if (cbVdaf.IsChecked == true) indicators.Add("vdaf");
        if (cbG.IsChecked == true) indicators.Add("caking");
        if (indicators.Count == 0) { await BlockMsgBox.WarnAsync(this, "提示", "请至少选择一个指标。"); return; }
        var method = methodCombo.SelectedItem?.ToString() ?? "IDW";
        bool useClean = rbClean.IsChecked == true;
        try
        {
            statusText.Text = "估值中…";
            var samples = Samples();
            var res = await System.Threading.Tasks.Task.Run(() => BlockCoalQualityLink.Estimate(_model, attr, mapping, indicators, useClean, method, samples));
            _model.CoalAttribute = attr;
            statusText.Text = res.Ok ? "完成" : "未写入";
            if (res.Ok) { _model.ActiveColormapAttribute = res.Columns[0].Column; _model.ColormapRange = null; BlockModelStore.RefreshDisplay(_ctx, _model); }
            if (res.Ok) await BlockMsgBox.InfoAsync(this, "煤质属性联动", res.Message); else await BlockMsgBox.WarnAsync(this, "提示", res.Message);
        }
        catch (Exception ex) { statusText.Text = ""; await BlockMsgBox.WarnAsync(this, "错误", $"运行失败：{ex.Message}"); }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
