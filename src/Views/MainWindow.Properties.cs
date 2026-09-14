using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// Ribbon「特性」组的 颜色 / 线宽 / 线型 三栏，以及它与右侧特性面板的双向联动 ——
/// 忠实原版 MainWindow「特性」RibbonGroupBox + SyncPropertyRibbonFromBag / ClearPropertyRibbon：
///   · 有选中 → 三栏写入选中实体（原版 ApplyPropertyToSelection 逐个 SetEntityProperty）
///   · 无选中 → 作为「下次新建的默认值」（原版此时只记日志，Kylin 沿用既有的默认值语义）
///   · 选中变化 / 面板改值 → 回填三栏显示（用抑制标志断环，同原版 _suppressPropertyComboEvents）
/// </summary>
public partial class MainWindow
{
    private bool _suppressPropRibbon;                       // 回填显示时置位，避免把回填当成用户操作再写回实体
    private (float r, float g, float b)? _defaultColor;      // 新建实体默认色；null = 随层(取当前图层色)
    private short _defaultLineWeight = -1;                   // 新建实体默认线宽；-1 = 随层

    /// <summary>构造时调用：填线宽下拉、复位三栏显示。</summary>
    private void InitPropertyRibbon()
    {
        if (LineWeightBox == null) return;
        _suppressPropRibbon = true;
        try
        {
            LineWeightBox.Items.Clear();
            foreach (short lw in LineWeightUtil.Choices)
                LineWeightBox.Items.Add(new ComboBoxItem { Content = LineWeightUtil.Display(lw), Tag = lw });
            LineWeightBox.SelectedIndex = 0;                 // 随层
            if (EntityColorPick != null) EntityColorPick.ColorCommitted += (_, rgb) => ApplyEntityColor(rgb);
            ShowRibbonColor(null);
        }
        finally { _suppressPropRibbon = false; }
    }

    // ── 颜色 ────────────────────────────────────────────────────────────────
    // 取色器本体见 Views/Controls/EntityColorPicker.cs（照原版 EntityColorPicker：
    // 随层 + ACI 索引色板 + 更多颜色…）。这里只管"选了色之后写到哪儿"。

    /// <summary>把颜色写入选中实体；无选中则记为新建默认色。rgb=null 表示「随层」。</summary>
    private void ApplyEntityColor((float r, float g, float b)? rgb)
    {
        if (_suppressPropRibbon) return;
        if (_selected.Count == 0)
        {
            _defaultColor = rgb;
            ShowRibbonColor(rgb);
            StatusMsg.Text = rgb == null
                ? "颜色：随层（未选中实体，作为新建默认；选中实体后再选即写入实体）"
                : $"颜色：{AciPalette.DisplayName(rgb.Value.r, rgb.Value.g, rgb.Value.b)}（未选中实体，作为新建默认）";
            return;
        }
        int ok = 0, locked = 0;
        BeginChange();
        foreach (var ent in _selected)
        {
            if (IsLayerLocked(ent)) { locked++; continue; }   // 锁定层不写入(同原版: 引擎拒绝则不改显示)
            var c = rgb ?? LayerColorOf(ent);
            ent.Cr = c.r; ent.Cg = c.g; ent.Cb = c.b;
            ok++;
        }
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"颜色：已写入 {ok}/{_selected.Count} 个实体" + (locked > 0 ? $"（{locked} 个在锁定图层，跳过）" : "");
    }

    /// <summary>命令行「颜色 &lt;值&gt;」：值可为 #RRGGBB / R,G,B / ACI 号 / 色名 / 随层。</summary>
    private void ColorCmd(string arg)
    {
        string t = (arg ?? "").Trim();
        if (t.Length == 0)
        {
            StatusMsg.Text = "颜色：用法「颜色 #RRGGBB」「颜色 R,G,B」「颜色 红」「颜色 随层」（有选中即写入选中实体）";
            return;
        }
        if (t is "随层" or "ByLayer" or "bylayer" or "BYLAYER") { ApplyEntityColor(null); return; }
        if (!AciPalette.TryParse(t, out float r, out float g, out float b))
        { StatusMsg.Text = $"颜色：无法识别「{t}」（用 #RRGGBB / R,G,B / ACI 号 / 色名 / 随层）"; return; }
        ApplyEntityColor((r, g, b));
    }

    // ── 线宽 ────────────────────────────────────────────────────────────────

    private void OnLineWeightChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropRibbon) return;
        if (LineWeightBox?.SelectedItem is not ComboBoxItem it || it.Tag is not short lw) return;
        if (_selected.Count == 0)
        {
            _defaultLineWeight = lw;
            StatusMsg.Text = $"线宽：{LineWeightUtil.Display(lw)}（未选中实体，作为新建默认）";
            return;
        }
        int ok = 0, locked = 0;
        BeginChange();
        foreach (var ent in _selected)
        {
            if (IsLayerLocked(ent)) { locked++; continue; }
            ent.LineWeight = lw; ok++;
        }
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"线宽 {LineWeightUtil.Display(lw)}：已写入 {ok}/{_selected.Count} 个实体"
                         + (locked > 0 ? $"（{locked} 个在锁定图层，跳过）" : "");
    }

    // ── 回填(选中变化 / 面板改值 → 三栏显示) ─────────────────────────────────
    // 同原版 SyncPropertyRibbonFromBag / ClearPropertyRibbon：只刷显示，不回写实体。

    private void SyncPropertyRibbonFromSelection()
    {
        if (LineWeightBox == null) return;
        _suppressPropRibbon = true;
        try
        {
            if (_selected.Count == 1)
            {
                var e = _selected[0];
                ShowRibbonColor((e.Cr, e.Cg, e.Cb));
                SelectLineWeight(e.LineWeight);
                SelectLinetype(DashPattern.DisplayName(e.Dash));
            }
            else
            {
                ShowRibbonColor(_defaultColor);              // 无单选 → 复位到默认(原版复位 ByLayer/默认线宽)
                SelectLineWeight(_defaultLineWeight);
                SelectLinetype(DashPattern.DisplayName(_currentDash));
            }
        }
        finally { _suppressPropRibbon = false; }
        SyncHatchRibbonFromSelection();   // 选中填充时, 注释组的 图案/比例/角度/颜色 回填成它的参数
    }

    /// <summary>回填取色器的显示（色块 + 文字）；null = 随层。不触发写入。</summary>
    private void ShowRibbonColor((float r, float g, float b)? rgb)
    {
        var l = _layers.Current;
        EntityColorPick?.SetValue(rgb, (l.Cr, l.Cg, l.Cb));
    }

    private void SelectLineWeight(short lw)
    {
        if (LineWeightBox == null) return;
        foreach (var o in LineWeightBox.Items)
            if (o is ComboBoxItem it && it.Tag is short v && v == lw) { LineWeightBox.SelectedItem = it; return; }
        LineWeightBox.SelectedIndex = 0;
    }

    private void SelectLinetype(string name)
    {
        if (LinetypeBox == null) return;
        foreach (var o in LinetypeBox.Items)
            if (o is ComboBoxItem it && it.Content is string s && s == name) { LinetypeBox.SelectedItem = it; return; }
        LinetypeBox.SelectedIndex = 0;
    }

    // ── 共用小工具 ──────────────────────────────────────────────────────────

    private bool IsLayerLocked(SceneEntity e) => _layers.Get(e.LayerName)?.Locked == true;

    private (float r, float g, float b) LayerColorOf(SceneEntity e)
    {
        var l = _layers.Get(e.LayerName) ?? _layers.Current;
        return (l.Cr, l.Cg, l.Cb);
    }

    // ── 测量六项(忠实原版「特性」组 测量 SplitButton) ─────────────────────────

    /// <summary>测量子项分派；返回 true 表示已处理。</summary>
    private bool TryMeasureCommand(string cmd)
    {
        switch (cmd)
        {
            case "快速测量":
            case "快速测距":
                _measure = new MeasureState(); _tool = null; _angle = null;
                StatusMsg.Text = "测量 - 快速：" + MeasurePrompt() + "（两点测距，ESC 取消）";
                return true;
            case "测量半径":
            case "半径测量":
                StatusMsg.Text = MeasureOps.Radius(_selected).Text;
                return true;
            case "测量体积":
            case "体积测量":
                StatusMsg.Text = MeasureOps.Volume(_selected).Text;
                return true;
        }
        return false;
    }

    /// <summary>距离/角度/面积：先按选集算，选集不足以判定才转入点测（忠实原版双模式）。</summary>
    private void MeasureBySelection(string kind)
    {
        var r = kind switch
        {
            "距离" => MeasureOps.Distance(_selected),
            "角度" => MeasureOps.Angle(_selected),
            _ => MeasureOps.Area(_selected),
        };
        if (r.NeedJig)
        {
            if (kind == "距离") { _measure = new MeasureState(); _angle = null; }
            else { _angle = new AngleState(); _measure = null; }
            _tool = null;
            StatusMsg.Text = MeasurePrompt();   // 按步骤提示(牵引线/读数随光标, 见 MainWindow.MeasureJig.cs)
            return;
        }
        StatusMsg.Text = r.Text;
    }

    // ── 特性面板：非单选时的内容(忠实原版 MultiSelectionProperties / DocumentProperties) ──

    private List<(string cat, string label, string value)> NonSingleSelectionRows() =>
        _selected.Count > 1
            ? PropertyPanelModel.MultiSelectionRows(_selected)
            : PropertyPanelModel.DocumentRows(_currentPath == null ? null : System.IO.Path.GetFileName(_currentPath),
                                              _scene.Count, _selected.Count, _orthoOn, _snapOn, !Viewport.Is2DView);
}
