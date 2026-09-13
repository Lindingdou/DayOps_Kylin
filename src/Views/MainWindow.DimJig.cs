using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 标注橡皮筋（jig）：线性/对齐/连续/半径/直径/角度/坐标标注取点过程中，按「已取点 + 光标」把整条标注
/// 现推出来画到预览通道，尺寸线随光标滑、数字随光标变（同 AutoCAD DIMLINEAR 拖尺寸线位置的手感；
/// 原版 AlignedDimensionJigAdapter / RadialDimensionJigAdapter 也是这么做的），落地前看到的就是落地后的样子。
/// 另有「标注样式」设置面板（<see cref="DimStyleWindow"/>）：无参的 标注样式 命令 / 功能区键 打开。
/// </summary>
public partial class MainWindow
{
    private bool _dimJigShown;   // 上一帧画过标注橡皮筋: 命令结束后下一次光标移动把预览通道擦干净

    /// <summary>有标注命令在取点(需要橡皮筋跟随光标)。</summary>
    private bool DimJigActive => _dimActive || (_dimRadActive && _dimRadCircle != null) || _dimAngActive || _coordLabelActive;

    /// <summary>光标移动时刷新标注橡皮筋(命令刚结束的那一次也刷, 把残留擦掉)。</summary>
    private void DimJigOnPointerMoved()
    {
        bool active = DimJigActive;
        if (active || _dimJigShown) RefreshScenePreview();
        _dimJigShown = active;
    }

    /// <summary>标注文字高(样式未定固定字高时随视图比例)——与落地时同一口径。</summary>
    private double DimJigHeight() => System.Math.Max(SnapTolWorld(_lastPointer) * 2.5, 1e-3);

    /// <summary>橡皮筋虚线的画/空长度: 约 7 屏幕像素。</summary>
    private double[] DimRubberDash() { double d = System.Math.Max(SnapTolWorld(_lastPointer) * 0.6, 1e-6); return new[] { d, d }; }

    /// <summary>预览通道出口(AppendScenePreview 里调)：把"已取点 + 光标"推出的标注镶嵌进去。</summary>
    private void AppendDimPreview(List<float> list)
    {
        if (!DimJigActive || _cursorWorld is not { } c) return;
        var ents = BuildDimPreview(c.x, c.y);
        if (ents == null) return;
        // 文字自真字体起字形本体走面通道, 线通道什么都不出; 预览通道只有线, 所以一律走 TessellatePick(文字=轮廓笔画)
        foreach (var e in ents) e.TessellatePick(list);
    }

    /// <summary>按当前标注命令的阶段构造预览实体(与落地时用同一套 DimTools, 所见即所得)；没到能画的阶段返回 null。</summary>
    private List<SceneEntity>? BuildDimPreview(double cx, double cy)
    {
        double h = DimJigHeight();
        SceneEntity Rubber(double x0, double y0) => new LineEntity { X0 = x0, Y0 = y0, X1 = cx, Y1 = cy, Dash = DimRubberDash(), Cr = 1f, Cg = 1f, Cb = 1f };

        if (_dimActive)
        {
            if (_dimP1 is not { } p1) return null;
            if (_dimContinue)
            {
                // 连续标注: (上条第二点, 光标) 为测点, 尺寸线沿用上条的偏移点
                var off = _lastDimOffsetPt ?? (cx, cy);
                return _dimAligned
                    ? DimTools.BuildLinear(p1.x, p1.y, cx, cy, off.x, off.y, h, _dimStyle)
                    : DimTools.BuildLinearAxis(p1.x, p1.y, cx, cy, off.x, off.y, h, _dimStyle);
            }
            if (_dimP2 is not { } p2) return new List<SceneEntity> { Rubber(p1.x, p1.y) };   // 第二个界线原点: 只拉橡皮筋
            // 尺寸线位置: 整条标注随光标滑
            return _dimAligned
                ? DimTools.BuildLinear(p1.x, p1.y, p2.x, p2.y, cx, cy, h, _dimStyle)
                : DimTools.BuildLinearAxis(p1.x, p1.y, p2.x, p2.y, cx, cy, h, _dimStyle);
        }
        if (_dimRadActive && _dimRadCircle is { } rc)
        {
            return _dimDiameter
                ? DimTools.BuildDiameter(rc.cx, rc.cy, rc.r, cx - rc.cx, cy - rc.cy, h, _dimStyle)
                : DimTools.BuildRadial(rc.cx, rc.cy, rc.r, cx - rc.cx, cy - rc.cy, h, _dimStyle);
        }
        if (_dimAngActive)
        {
            if (_angVertex is not { } v) return null;
            if (_angP1 is not { } q1) return new List<SceneEntity> { Rubber(v.x, v.y) };
            double arcR = System.Math.Max(System.Math.Sqrt((q1.x - v.x) * (q1.x - v.x) + (q1.y - v.y) * (q1.y - v.y)) * 0.5, h * 3);
            var ents = DimTools.BuildAngular(v.x, v.y, q1.x, q1.y, cx, cy, arcR, h, _dimStyle);
            ents.Add(Rubber(v.x, v.y));
            return ents;
        }
        if (_coordLabelActive)
            return DimTools.BuildCoordLabel(cx, cy, h * 6, h * 6, h, _dimStyle);
        return null;
    }

    /// <summary>光标浮标里的实时读数(距离/半径/角度/坐标), 接在步骤提示后面。</summary>
    private string? DimJigHint((double x, double y) c)
    {
        string F(double v) => v.ToString(_dimStyle.NumberFormat, CultureInfo.InvariantCulture);
        if (_dimActive && _dimP1 is { } p1)
        {
            var q = (_dimContinue || _dimP2 == null) ? c : _dimP2!.Value;
            double dx = q.x - p1.x, dy = q.y - p1.y;
            if (_dimAligned || _dimP2 == null || _dimContinue) return $"距离 {F(System.Math.Sqrt(dx * dx + dy * dy))}";
            double mx = (p1.x + q.x) / 2, my = (p1.y + q.y) / 2;
            bool horizontal = System.Math.Abs(c.y - my) >= System.Math.Abs(c.x - mx);   // 同 BuildLinearAxis 的判法
            return horizontal ? $"ΔX {F(System.Math.Abs(dx))}" : $"ΔY {F(System.Math.Abs(dy))}";
        }
        if (_dimRadActive && _dimRadCircle is { } rc) return _dimDiameter ? $"Ø{F(2 * rc.r)}" : $"R{F(rc.r)}";
        if (_dimAngActive && _angVertex is { } v && _angP1 is { } q1)
        {
            double a1 = System.Math.Atan2(q1.y - v.y, q1.x - v.x), a2 = System.Math.Atan2(c.y - v.y, c.x - v.x);
            double deg = System.Math.Abs(a2 - a1) * 180 / System.Math.PI;
            if (deg > 180) deg = 360 - deg;
            return $"角度 {deg:0.#}°";
        }
        if (_coordLabelActive) return $"X={F(c.x)} Y={F(c.y)}";
        return null;
    }

    /// <summary>「标注样式」设置面板：编辑文档级 DIM 变量(字高/小数位/箭头/端刻度/界线/文字偏移), 确定后影响新建标注并记到用户目录。</summary>
    private async Task OpenDimStyleWindowAsync()
    {
        var w = new DimStyleWindow(_dimStyle);
        if (System.Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 })
        {
            w.Show(this);   // 自检: 非模态打开好截图, 不阻塞脚本
            StatusMsg.Text = "标注样式：设置面板已打开";
            return;
        }
        bool ok = await w.ShowDialog<bool>(this);
        if (!ok) { StatusMsg.Text = "标注样式：未改动"; return; }
        DimStyleStore.Save(_dimStyle);
        StatusMsg.Text = $"标注样式已更新：{DimStyleStore.Describe(_dimStyle)}（影响新建标注）";
    }
}
