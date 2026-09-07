using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>采样段新增/编辑对话框(忠实原 CoalSampleEditDialog: 30 字段分 4 个 Tab)。ShowDialog&lt;bool&gt; 返回 true 表示保存, 结果在 <see cref="Result"/>。</summary>
public partial class CoalSampleEditDialog : Window
{
    private readonly CoalSampleRow _model;
    private readonly bool _isNew;
    private readonly IReadOnlyList<CoalBoreholeRow> _allHoles;
    private readonly IReadOnlyList<CoalClassRefRow> _allCoalTypes;
    private readonly IReadOnlyList<CoalTypeInference.ClassRange> _ranges;

    public CoalSampleRow Result => _model;

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public CoalSampleEditDialog()
    {
        InitializeComponent();
        _model = new CoalSampleRow(); _allHoles = new List<CoalBoreholeRow>(); _allCoalTypes = new List<CoalClassRefRow>(); _ranges = new List<CoalTypeInference.ClassRange>();
    }

    public CoalSampleEditDialog(GeoDbContext ctx, CoalSampleRow? source = null)
    {
        InitializeComponent();
        _allHoles = CoalBoreholes(ctx.Conn);
        _allCoalTypes = CoalClassRefs(ctx.Conn);
        _ranges = GeoDataQueries.GetCoalClassificationRanges(ctx.Conn);

        foreach (var h in _allHoles.OrderBy(h => h.HoleId)) cbHole.Items.Add(new ComboBoxItem { Content = h.HoleId, Tag = h.Id });
        foreach (var s in CoalSeamDefs(ctx.Conn)) cbSeam.Items.Add(new ComboBoxItem { Content = s.Code, Tag = s.Code });
        cbCoalType.Items.Add(new ComboBoxItem { Content = "(未标注)", Tag = null });
        foreach (var c in _allCoalTypes) cbCoalType.Items.Add(new ComboBoxItem { Content = $"{c.Code} - {c.NameCn}", Tag = c.Code });

        _isNew = source is null;
        _model = source is null ? new CoalSampleRow() : source.Clone();
        titleText.Text = _isNew ? "新增采样段" : $"编辑采样段 #{_model.Id}";
        Title = titleText.Text;
        FillForm();
    }

    private void FillForm()
    {
        SelectByTag(cbHole, _model.BoreholeId);
        SelectByTag(cbSeam, _model.SeamCode);
        tbFrom.Text = _model.DepthFrom?.ToString("F2") ?? "";
        tbTo.Text = _model.DepthTo?.ToString("F2") ?? "";
        tbThickness.Text = _model.SampleThickness?.ToString("F2") ?? "";
        tbRemark.Text = _model.Remark ?? "";

        tbMadR.Text = _model.MadRaw?.ToString("F2") ?? ""; tbMadC.Text = _model.MadClean?.ToString("F2") ?? "";
        tbAdR.Text = _model.AdRaw?.ToString("F2") ?? ""; tbAdC.Text = _model.AdClean?.ToString("F2") ?? "";
        tbVdafR.Text = _model.VdafRaw?.ToString("F2") ?? ""; tbVdafC.Text = _model.VdafClean?.ToString("F2") ?? "";
        tbFcdR.Text = _model.FcdRaw?.ToString("F2") ?? ""; tbFcdC.Text = _model.FcdClean?.ToString("F2") ?? "";
        tbSR.Text = _model.StdRaw?.ToString("F2") ?? ""; tbSC.Text = _model.StdClean?.ToString("F2") ?? "";
        tbQgr.Text = _model.QgrD?.ToString("F2") ?? ""; tbQnet.Text = _model.QnetAd?.ToString("F2") ?? "";
        tbX.Text = _model.PlasticXMm?.ToString("F1") ?? ""; tbY.Text = _model.PlasticYMm?.ToString("F1") ?? "";
        tbG.Text = _model.CakingG?.ToString("F1") ?? "";
        tbCharR.Text = _model.CharResidueRaw?.ToString() ?? ""; tbCharC.Text = _model.CharResidueClean?.ToString() ?? "";
        tbYield.Text = _model.CleanCoalYield?.ToString("F2") ?? "";
        SelectByTag(cbCoalType, _model.CoalType);
    }

    private static void SelectByTag(ComboBox cb, object? tag)
    {
        for (int i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is ComboBoxItem item && Equals(item.Tag, tag)) { cb.SelectedIndex = i; return; }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    private void OnInferCoalTypeClick(object? sender, RoutedEventArgs e)
    {
        var resolved = CoalTypeInference.ResolveCoalType(ParseD(tbVdafR.Text), ParseD(tbG.Text), ParseD(tbY.Text), _ranges);
        if (resolved is null) { inferResult.Text = "无法反推（数据不足或越界）"; return; }
        SelectByTag(cbCoalType, resolved);
        var name = _allCoalTypes.FirstOrDefault(c => c.Code == resolved)?.NameCn;
        inferResult.Text = $"反推: {resolved} ({name})";
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (cbHole.SelectedItem is not ComboBoxItem hi || hi.Tag is not long bhid) { await CoalMsgBox.ShowAsync(this, "提示", "请选孔号"); return; }
        if (cbSeam.SelectedItem is not ComboBoxItem si || si.Tag is not string seam) { await CoalMsgBox.ShowAsync(this, "提示", "请选煤层"); return; }
        if (!double.TryParse(tbFrom.Text, out var df)) { await CoalMsgBox.ShowAsync(this, "提示", "采样起深必填且为数字"); return; }
        if (!double.TryParse(tbTo.Text, out var dt)) { await CoalMsgBox.ShowAsync(this, "提示", "采样止深必填且为数字"); return; }
        if (dt <= df) { await CoalMsgBox.ShowAsync(this, "提示", "止深必须 > 起深"); return; }

        _model.BoreholeId = bhid;
        _model.SeamCode = seam;
        _model.DepthFrom = df;
        _model.DepthTo = dt;
        _model.SampleThickness = double.TryParse(tbThickness.Text, out var th) ? th : (dt - df);
        _model.Remark = string.IsNullOrWhiteSpace(tbRemark.Text) ? null : tbRemark.Text;

        _model.MadRaw = ParseD(tbMadR.Text); _model.MadClean = ParseD(tbMadC.Text);
        _model.AdRaw = ParseD(tbAdR.Text); _model.AdClean = ParseD(tbAdC.Text);
        _model.VdafRaw = ParseD(tbVdafR.Text); _model.VdafClean = ParseD(tbVdafC.Text);
        _model.FcdRaw = ParseD(tbFcdR.Text); _model.FcdClean = ParseD(tbFcdC.Text);
        _model.StdRaw = ParseD(tbSR.Text); _model.StdClean = ParseD(tbSC.Text);
        _model.QgrD = ParseD(tbQgr.Text); _model.QnetAd = ParseD(tbQnet.Text);
        _model.PlasticXMm = ParseD(tbX.Text); _model.PlasticYMm = ParseD(tbY.Text);
        _model.CakingG = ParseD(tbG.Text);
        _model.CharResidueRaw = ParseI(tbCharR.Text);
        _model.CharResidueClean = ParseI(tbCharC.Text);
        _model.CleanCoalYield = ParseD(tbYield.Text);
        _model.CoalType = (cbCoalType.SelectedItem as ComboBoxItem)?.Tag as string;

        // 计算 z_sample（如果孔有 z_collar）
        var hole = _allHoles.FirstOrDefault(h => h.Id == bhid);
        if (hole?.ZCollar is not null) _model.ZSample = hole.ZCollar.Value - (df + dt) / 2;

        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private static double? ParseD(string? s) => double.TryParse(s, out var v) ? v : null;
    private static int? ParseI(string? s) => int.TryParse(s, out var v) ? v : null;
}
