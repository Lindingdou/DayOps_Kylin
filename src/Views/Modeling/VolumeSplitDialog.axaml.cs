using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 两期三角网算量参数面板（原 MeshEditLib.Dialogs.VolumeSplitDialog）：体积/渲染格网、最小高差、去噪半径、最小台阶高 +
/// 填方/挖方任意 RGB（滑块 + Hex + 预览）。确定后进入两步拾取（视口点选第一期 → 第二期三角网），
/// 由 <see cref="CutFillSolids"/>(核 <see cref="SurfaceVolume.CutFill"/>) 生成各连通块的填方/挖方水密实体入场景；
/// 结果(挖/填/净/重叠面积 + 各封闭体表)显示在窗内，可开关高差着色格、导出 CSV。参数非法只提示不崩。
/// </summary>
public partial class VolumeSplitDialog : Window
{
    private bool _suppress;
    private readonly ModelingContext? _ctx;
    private CutFillSolids.Result? _last;
    private MeshEntity? _meshA, _meshB;
    private readonly List<SceneEntity> _cellEnts = new();
    private readonly List<MeshEntity> _bodyEnts = new();

    public double GridCell { get; private set; } = 2.0;
    public double RenderCell { get; private set; } = 6.0;
    public double MinDz { get; private set; } = 1.0;
    public int OpenRadius { get; private set; } = 1;
    public double MinBenchH { get; private set; } = 3.0;
    public Color FillColor { get; private set; } = Color.FromRgb(0, 120, 255);
    public Color CutColor { get; private set; } = Color.FromRgb(255, 60, 0);

    public sealed class BodyRow
    {
        public string Kind { get; set; } = "";
        public int Index { get; set; }
        public int Cells { get; set; }
        public double AreaM2 { get; set; }
        public double VolumeM3 { get; set; }
        public double MaxDz { get; set; }
        public CutFillSolids.Body Body { get; set; } = null!;
    }

    public VolumeSplitDialog()
    {
        InitializeComponent();
        _suppress = true;
        sliderFillR.Value = 0; sliderFillG.Value = 120; sliderFillB.Value = 255;
        sliderCutR.Value = 255; sliderCutG.Value = 60; sliderCutB.Value = 0;
        _suppress = false;
        UpdateFill();
        UpdateCut();
    }

    public VolumeSplitDialog(ModelingContext ctx) : this() { _ctx = ctx; }

    private void OnFillSliderChanged(object? sender, RangeBaseValueChangedEventArgs e) { if (!_suppress) UpdateFill(); }
    private void OnCutSliderChanged(object? sender, RangeBaseValueChangedEventArgs e) { if (!_suppress) UpdateCut(); }

    private void UpdateFill()
    {
        if (sliderFillR == null) return;
        byte r = (byte)sliderFillR.Value, g = (byte)sliderFillG.Value, b = (byte)sliderFillB.Value;
        FillColor = Color.FromRgb(r, g, b);
        if (fillPreview != null) fillPreview.Background = new SolidColorBrush(FillColor);
        if (fillRVal != null) fillRVal.Text = r.ToString(CultureInfo.InvariantCulture);
        if (fillGVal != null) fillGVal.Text = g.ToString(CultureInfo.InvariantCulture);
        if (fillBVal != null) fillBVal.Text = b.ToString(CultureInfo.InvariantCulture);
        _suppress = true;
        if (txtFillHex != null) txtFillHex.Text = $"#{r:X2}{g:X2}{b:X2}";
        _suppress = false;
    }

    private void UpdateCut()
    {
        if (sliderCutR == null) return;
        byte r = (byte)sliderCutR.Value, g = (byte)sliderCutG.Value, b = (byte)sliderCutB.Value;
        CutColor = Color.FromRgb(r, g, b);
        if (cutPreview != null) cutPreview.Background = new SolidColorBrush(CutColor);
        if (cutRVal != null) cutRVal.Text = r.ToString(CultureInfo.InvariantCulture);
        if (cutGVal != null) cutGVal.Text = g.ToString(CultureInfo.InvariantCulture);
        if (cutBVal != null) cutBVal.Text = b.ToString(CultureInfo.InvariantCulture);
        _suppress = true;
        if (txtCutHex != null) txtCutHex.Text = $"#{r:X2}{g:X2}{b:X2}";
        _suppress = false;
    }

    private void OnFillHexLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (TryParseHex(txtFillHex.Text, out byte r, out byte g, out byte b))
        {
            _suppress = true;
            sliderFillR.Value = r; sliderFillG.Value = g; sliderFillB.Value = b;
            _suppress = false;
        }
        UpdateFill();   // 输入非法 → 回写当前滑块值（不崩）
    }

    private void OnCutHexLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (TryParseHex(txtCutHex.Text, out byte r, out byte g, out byte b))
        {
            _suppress = true;
            sliderCutR.Value = r; sliderCutG.Value = g; sliderCutB.Value = b;
            _suppress = false;
        }
        UpdateCut();
    }

    /// <summary>解析 #RRGGBB / RRGGBB。失败返回 false（绝不抛）。</summary>
    public static bool TryParseHex(string? s, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        try
        {
            r = Convert.ToByte(s.Substring(0, 2), 16);
            g = Convert.ToByte(s.Substring(2, 2), 16);
            b = Convert.ToByte(s.Substring(4, 2), 16);
            return true;
        }
        catch { return false; }
    }

    /// <summary>参数校验(忠实原 OnOkClick 的 5 项检查); 失败返回提示文案。</summary>
    public static string? Validate(string? gridCell, string? renderCell, string? minDz, string? openRadius, string? minBenchH,
        out double gc, out double rc, out double dz, out int orad, out double bh)
    {
        gc = rc = dz = bh = 0; orad = 0;
        if (!double.TryParse(gridCell?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out gc) || gc <= 0)
            return "请输入有效的格网分辨率（> 0 的数字，单位 m）。";
        if (!double.TryParse(renderCell?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rc) || rc <= 0)
            return "请输入有效的渲染格网（> 0 的数字，单位 m；≤ 体积格网时不解耦）。";
        if (!double.TryParse(minDz?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dz) || dz < 0)
            return "请输入有效的最小高差（≥ 0 的数字，单位 m）。";
        if (!int.TryParse(openRadius?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out orad) || orad < 0)
            return "请输入有效的去噪半径（≥ 0 的整数，单位：格）。";
        if (!double.TryParse(minBenchH?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out bh) || bh < 0)
            return "请输入有效的最小台阶高（≥ 0 的数字，单位 m；0 = 不过滤）。";
        return null;
    }

    private async void OnOkClick(object? sender, RoutedEventArgs e)
    {
        string? err = Validate(txtGridCell.Text, txtRenderCell.Text, txtMinDz.Text, txtOpenRadius.Text, txtMinBenchH.Text,
            out double gc, out double rc, out double dz, out int orad, out double bh);
        if (err != null) { ShowResult("参数错误：" + err); return; }
        GridCell = gc; RenderCell = rc; MinDz = dz; OpenRadius = orad; MinBenchH = bh;
        UpdateFill(); UpdateCut();
        if (_ctx == null) { Close(true); return; }

        // 两步拾取：第一期 → 第二期
        btnOk.IsEnabled = false;
        try
        {
            var a = await _ctx.PickEntityAsync("两期三角网算量：点选 ① 第一期(原始/较早) 三角网（Esc 取消）", en => en is MeshEntity) as MeshEntity;
            if (a == null) { _ctx.Status("两期三角网算量：已取消(未选到第一期三角网)"); return; }
            var b = await _ctx.PickEntityAsync("两期三角网算量：点选 ② 第二期(现状/较新) 三角网（Esc 取消）", en => en is MeshEntity && !ReferenceEquals(en, a)) as MeshEntity;
            if (b == null) { _ctx.Status("两期三角网算量：已取消(未选到第二期三角网)"); return; }
            _meshA = a; _meshB = b;
            _ctx.Status($"两期三角网算量：「{a.Name}」→「{b.Name}」计算中…");
            var (va, ta) = a.Flatten(); var (vb, tb) = b.Flatten();
            var o = new CutFillSolids.Options { GridCell = gc, RenderCell = rc, MinDz = dz, OpenRadius = orad, MinBenchH = bh };
            var res = await Task.Run(() => CutFillSolids.Compute(va, ta, vb, tb, o));
            if (!string.IsNullOrEmpty(res.Error)) { ShowResult("两期三角网算量：" + res.Error); _ctx.Status("两期三角网算量：" + res.Error); return; }
            ApplyResult(res);
        }
        catch (Exception ex) { ShowResult($"两期三角网算量异常：{ex.Message}"); _ctx.Status($"两期三角网算量异常：{ex.Message}"); }
        finally { btnOk.IsEnabled = true; }
    }

    private void ApplyResult(CutFillSolids.Result res)
    {
        if (_ctx == null) return;
        // 清旧体/旧格
        if (_bodyEnts.Count > 0) { _ctx.RemoveEntities(_bodyEnts.Cast<SceneEntity>().ToList()); _bodyEnts.Clear(); }
        RemoveCells();
        _last = res;
        var made = new List<SceneEntity>();
        foreach (var body in res.Bodies)
        {
            if (body.Tris.Count == 0) continue;
            var c = body.IsFill ? FillColor : CutColor;
            var me = new MeshEntity((body.IsFill ? "填方体" : "挖方体") + body.Index, body.Verts, body.Tris)
            { LayerName = body.IsFill ? "填方" : "挖方", Cr = c.R / 255f, Cg = c.G / 255f, Cb = c.B / 255f };
            _ctx.AddMesh(me, false);
            _bodyEnts.Add(me); made.Add(me);
        }
        if (made.Count > 0) _ctx.Select(made);
        var rows = res.Bodies.Select(b => new BodyRow { Kind = b.IsFill ? "填方" : "挖方", Index = b.Index, Cells = b.Cells, AreaM2 = b.AreaM2, VolumeM3 = b.VolumeM3, MaxDz = b.MaxDz, Body = b }).ToList();
        bodyGrid.ItemsSource = rows;
        var raw = res.Raw;
        string msg = $"「{_meshA?.Name}」→「{_meshB?.Name}」：挖方 {res.CutM3:0.#} m³ · 填方 {res.FillM3:0.#} m³ · 净 {res.NetM3:+0.#;-0.#} m³（去噪/台阶过滤后）\n" +
                     $"原始格级：挖 {raw.CutM3:0.#} / 填 {raw.FillM3:0.#} m³ · 重叠 {raw.OverlapAreaM2:0.#} m²（格距 {raw.CellSize:0.##}, {raw.ValidCells} 格）· dz {raw.MinDz:0.##}~{raw.MaxDz:0.##}\n" +
                     $"封闭体 {res.Bodies.Count} 个（填 {res.Bodies.Count(b => b.IsFill)} / 挖 {res.Bodies.Count(b => !b.IsFill)}）· 去噪去格 {res.DroppedNoise} · 台阶过滤丢块 {res.DroppedLowBench}";
        ShowResult(msg);
        _ctx.Status($"两期三角网算量：挖方 {res.CutM3:0.#} m³ · 填方 {res.FillM3:0.#} m³ · 净 {res.NetM3:+0.#;-0.#} m³ · {res.Bodies.Count} 个封闭体已入场景（图层「填方」/「挖方」）");
        if (chkCells.IsChecked == true) AddCells();
    }

    private void ShowResult(string text) { resultPanel.IsVisible = true; txtResult.Text = text; }

    private void OnBodySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_ctx == null || bodyGrid.SelectedItem is not BodyRow row) return;
        int i = _last?.Bodies.IndexOf(row.Body) ?? -1;
        if (i >= 0 && i < _bodyEnts.Count) _ctx.Select(new SceneEntity[] { _bodyEnts[i] });
    }

    private void OnCellsToggled(object? sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        if (chkCells.IsChecked == true) AddCells(); else RemoveCells();
    }

    private void AddCells()
    {
        if (_ctx == null || _last == null || _last.Dz == null) return;
        RemoveCells();
        double maxAbs = 1e-9;
        foreach (var b in _last.Bodies) maxAbs = Math.Max(maxAbs, b.MaxDz);
        double cell = _last.Cell;
        foreach (var body in _last.Bodies)
        {
            var c = body.IsFill ? FillColor : CutColor;
            foreach (var (i, j) in body.CellIdx)
            {
                double t = 0.35 + 0.65 * Math.Min(1, Math.Abs(_last.Dz[i, j]) / maxAbs);
                double cx = _last.X0 + (i + 0.5) * cell, cy = _last.Y0 + (j + 0.5) * cell;
                _cellEnts.Add(new RectEntity
                {
                    X0 = cx - cell / 2, Y0 = cy - cell / 2, X1 = cx + cell / 2, Y1 = cy + cell / 2,
                    Cr = (float)(c.R / 255f * t), Cg = (float)(c.G / 255f * t), Cb = (float)(c.B / 255f * t),
                });
            }
        }
        if (_cellEnts.Count > 0) _ctx.AddEntities(_cellEnts, "两期算量·高差格", null);
    }

    private void RemoveCells()
    {
        if (_ctx == null || _cellEnts.Count == 0) return;
        _ctx.RemoveEntities(_cellEnts);
        _cellEnts.Clear();
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null || _last == null) return;
        var name = await _ctx.SaveTextAsync("导出两期算量 (CSV)", "两期三角网算量.csv", ToCsv(_last, _meshA?.Name ?? "", _meshB?.Name ?? ""));
        if (name != null) _ctx.Status($"两期三角网算量：已导出 {name}");
    }

    /// <summary>结果 → CSV：汇总行 + 各封闭体行。</summary>
    public static string ToCsv(CutFillSolids.Result res, string nameA, string nameB)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("项目,值");
        sb.AppendLine($"第一期,{nameA}");
        sb.AppendLine($"第二期,{nameB}");
        sb.AppendLine($"挖方 m³(过滤后),{res.CutM3.ToString("0.###", ic)}");
        sb.AppendLine($"填方 m³(过滤后),{res.FillM3.ToString("0.###", ic)}");
        sb.AppendLine($"净 m³,{res.NetM3.ToString("0.###", ic)}");
        sb.AppendLine($"原始挖方 m³,{res.Raw.CutM3.ToString("0.###", ic)}");
        sb.AppendLine($"原始填方 m³,{res.Raw.FillM3.ToString("0.###", ic)}");
        sb.AppendLine($"重叠面积 m²,{res.Raw.OverlapAreaM2.ToString("0.###", ic)}");
        sb.AppendLine($"格距 m,{res.Raw.CellSize.ToString("0.###", ic)}");
        sb.AppendLine();
        sb.AppendLine("类型,序号,格数,面积 m²,体积 m³,最大高差 m,平均高差 m");
        foreach (var b in res.Bodies)
            sb.AppendLine($"{(b.IsFill ? "填方" : "挖方")},{b.Index},{b.Cells},{b.AreaM2.ToString("0.###", ic)},{b.VolumeM3.ToString("0.###", ic)},{b.MaxDz.ToString("0.###", ic)},{b.MeanDz.ToString("0.###", ic)}");
        return sb.ToString();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
