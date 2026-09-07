using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 切面剖切对话框（忠实原 SliceDialog）：启用总开关 + 法线轴 X/Y/Z + 切方向 + 位置 Slider/TextBox（范围按活动模型 AABB）。
/// 平面 (n, d)：dot((p,1), plane) &gt; 0 一侧被切；全局态存 <see cref="BlockModelStore"/>，关窗不关剖切。
/// </summary>
public partial class SliceWindow : Window
{
    private readonly ModelingContext _ctx;
    private bool _ready;

    public SliceWindow() { _ctx = null!; InitializeComponent(); }

    public SliceWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        InitFromCurrentState();
        _ready = true;
        enableBox.IsCheckedChanged += (_, _) => { paramPanel.IsEnabled = enableBox.IsChecked == true; ApplyIfEnabled(); };
        foreach (var rb in new[] { axisX, axisY, axisZ }) rb.IsCheckedChanged += (_, e) => { if (((RadioButton)e.Source!).IsChecked == true) UpdateRangeForAxis(); };
        foreach (var rb in new[] { dirPositive, dirNegative }) rb.IsCheckedChanged += (_, _) => ApplyIfEnabled();
        posSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) OnPosSliderChanged(); };
        paramPanel.IsEnabled = enableBox.IsChecked == true;
        UpdateRangeForAxis();
    }

    private void InitFromCurrentState()
    {
        enableBox.IsChecked = BlockModelStore.ClipEnabled;
        var p = BlockModelStore.ClipPlane;
        double ax = Math.Abs(p.NX), ay = Math.Abs(p.NY), az = Math.Abs(p.NZ);
        if (ax >= ay && ax >= az) { axisX.IsChecked = true; SetDirFromSign(p.NX); }
        else if (ay >= az) { axisY.IsChecked = true; SetDirFromSign(p.NY); }
        else { axisZ.IsChecked = true; SetDirFromSign(p.NZ); }
    }

    private void SetDirFromSign(double n) { if (n >= 0) dirPositive.IsChecked = true; else dirNegative.IsChecked = true; }
    private int CurrentAxis => axisX.IsChecked == true ? 0 : axisY.IsChecked == true ? 1 : 2;
    private bool CurrentDirPositive => dirPositive.IsChecked == true;

    private void UpdateRangeForAxis()
    {
        if (!_ready) return;
        var m = BlockModelStore.PickDefault();
        double minV, maxV; string axisName;
        var b = m?.Bounds;
        switch (CurrentAxis)
        {
            case 0: minV = b?.minX ?? 0; maxV = b?.maxX ?? 1000; axisName = "X"; break;
            case 1: minV = b?.minY ?? 0; maxV = b?.maxY ?? 1000; axisName = "Y"; break;
            default: minV = b?.minZ ?? 0; maxV = b?.maxZ ?? 1000; axisName = "Z"; break;
        }
        if (maxV <= minV) maxV = minV + 1.0;
        _ready = false;
        posSlider.Minimum = minV; posSlider.Maximum = maxV; posSlider.TickFrequency = (maxV - minV) / 20.0;
        double mid = (minV + maxV) * 0.5;
        posSlider.Value = mid;
        posBox.Text = mid.ToString("0.###", CultureInfo.InvariantCulture);
        _ready = true;
        rangeLabel.Text = m == null ? "(请先创建块体模型)" : $"{axisName} ∈ [{minV:0.##}, {maxV:0.##}]  ({m.Name})";
        ApplyIfEnabled();
    }

    private void ApplyIfEnabled()
    {
        if (!_ready) return;
        if (enableBox.IsChecked != true)
        {
            if (BlockModelStore.ClipEnabled) BlockModelStore.DisableClip(_ctx);
            planeEqLabel.Text = "(剖切已关闭)";
            return;
        }
        double pos = posSlider.Value;
        double sign = CurrentDirPositive ? 1.0 : -1.0;
        double nx = 0, ny = 0, nz = 0;
        switch (CurrentAxis) { case 0: nx = sign; break; case 1: ny = sign; break; default: nz = sign; break; }
        double d = -sign * pos;
        BlockModelStore.SetClipPlane(_ctx, nx, ny, nz, d, true);
        char axis = CurrentAxis switch { 0 => 'x', 1 => 'y', _ => 'z' };
        planeEqLabel.Text = $"discard if {axis} {(CurrentDirPositive ? ">" : "<")} {pos:0.###}    (plane = {nx:0.#}, {ny:0.#}, {nz:0.#}, {d:0.###})";
        _ctx.Status($"切面剖切：{axis} {(CurrentDirPositive ? ">" : "<")} {pos:0.###} 被切掉");
    }

    private void OnPosSliderChanged()
    {
        if (!_ready) return;
        posBox.Text = posSlider.Value.ToString("0.###", CultureInfo.InvariantCulture);
        ApplyIfEnabled();
    }

    private void OnPosBoxKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) CommitPosFromBox(); }
    private void OnPosBoxLostFocus(object? sender, RoutedEventArgs e) => CommitPosFromBox();

    private void CommitPosFromBox()
    {
        if (!double.TryParse(posBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) { posBox.Text = posSlider.Value.ToString("0.###", CultureInfo.InvariantCulture); return; }
        v = Math.Max(posSlider.Minimum, Math.Min(posSlider.Maximum, v));
        posSlider.Value = v;
    }

    private void OnDisable(object? sender, RoutedEventArgs e) => enableBox.IsChecked = false;
    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
