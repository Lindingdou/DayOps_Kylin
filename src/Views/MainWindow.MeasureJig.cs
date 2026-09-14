using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 测量橡皮筋（jig）：DIST 两点测距 / MANG 三点测角 取点过程中，按「已取点 + 光标」把牵引线与实时读数画到
/// 预览通道 —— 忠实原版内核 MeasureDistanceJigAdapter / MeasureAngleJigAdapter(getAuxiliaryPreview + getAnnotations)：
///   · DIST：第一点 → 光标 一条牵引线，中点处黄色 HUD「距离: x.xxx」；
///   · MANG：顶点 → 光标（第一条边）；有第一条边后 顶点→a 实线 + 顶点→光标 牵引线，顶点右上青色 HUD「角度: x.xx°」。
/// 测量不落任何实体(原版 createPreview/createEntity 都返回 nullptr)，只在状态栏/信息栏报结果；
/// 点击取点与命令行键入坐标共用 <see cref="MeasureFeedPoint"/>。
/// </summary>
public partial class MainWindow
{
    private bool _measureJigShown;   // 上一帧画过测量橡皮筋: 命令结束后下一次光标移动把预览通道擦干净

    /// <summary>有测量命令在取点且已有锚点(需要橡皮筋跟随光标)。</summary>
    private bool MeasureJigActive => _measure?.HasFirst == true || _angle?.HasVertex == true;

    /// <summary>光标移动时刷新测量橡皮筋(命令刚结束的那一次也刷, 把残留擦掉)。</summary>
    private void MeasureJigOnPointerMoved()
    {
        bool active = MeasureJigActive;
        if (active || _measureJigShown) RefreshScenePreview();
        _measureJigShown = active;
    }

    /// <summary>预览通道出口(AppendScenePreview 里调)：牵引线 + 世界坐标处的读数(同原版 JigAnnotation 贴在几何旁)。</summary>
    private void AppendMeasurePreview(List<float> list)
    {
        if (!MeasureJigActive || _cursorWorld is not { } c) return;
        double h = System.Math.Max(PixelsToWorld(_lastPointer, 15), 1e-6);   // 读数是屏幕 HUD(原版 JigAnnotation 定屏幕字号), 不随视图比例变大
        var dash = DimRubberDash();
        if (_measure is { First: { } p1 })
        {
            new LineEntity { X0 = p1.x, Y0 = p1.y, X1 = c.x, Y1 = c.y, Dash = dash, Cr = 1f, Cg = 1f, Cb = 1f }.Tessellate(list);
            double dx = c.x - p1.x, dy = c.y - p1.y;
            // 原版 HUD 在两点中点、黄色；读数抬半个字高免得压在牵引线上
            new TextEntity
            {
                X = (p1.x + c.x) / 2, Y = (p1.y + c.y) / 2 + h * 0.5, Height = h, HAlign = 1,
                Text = $"距离: {System.Math.Sqrt(dx * dx + dy * dy):0.###}", Cr = 1f, Cg = 1f, Cb = 0.2f,
            }.TessellatePick(list);   // 预览通道只有线, 文字走轮廓笔画(同标注橡皮筋)
            return;
        }
        if (_angle is { Vertex: { } v })
        {
            if (_angle.FirstRay is { } a)
            {
                new LineEntity { X0 = v.x, Y0 = v.y, X1 = a.x, Y1 = a.y, Cr = 1f, Cg = 1f, Cb = 1f }.Tessellate(list);   // 第一条边已定: 实线
                new LineEntity { X0 = v.x, Y0 = v.y, X1 = c.x, Y1 = c.y, Dash = dash, Cr = 1f, Cg = 1f, Cb = 1f }.Tessellate(list);
                // 原版 HUD 在顶点处、屏幕偏移 (20,-20)px(右上)、青色
                double off = PixelsToWorld(_lastPointer, 20);
                new TextEntity
                {
                    X = v.x + off, Y = v.y + off, Height = h,
                    Text = $"角度: {AngleMath.AngleDeg(v.x, v.y, a.x, a.y, c.x, c.y):0.##}°", Cr = 0.2f, Cg = 1f, Cb = 1f,
                }.TessellatePick(list);
            }
            else
                new LineEntity { X0 = v.x, Y0 = v.y, X1 = c.x, Y1 = c.y, Dash = dash, Cr = 1f, Cg = 1f, Cb = 1f }.Tessellate(list);
        }
    }

    /// <summary>光标浮标里的实时读数(距离/角度), 接在步骤提示后面。</summary>
    private string? MeasureJigHint((double x, double y) c)
    {
        if (_measure is { First: { } p1 })
        {
            double dx = c.x - p1.x, dy = c.y - p1.y;
            return $"距离 {System.Math.Sqrt(dx * dx + dy * dy):0.###}";
        }
        if (_angle is { Vertex: { } v, FirstRay: { } a })
            return $"角度 {AngleMath.AngleDeg(v.x, v.y, a.x, a.y, c.x, c.y):0.##}°";
        return null;
    }

    /// <summary>测量命令当前步骤的提示(同原版 defineSteps 的三/两句)。</summary>
    private string MeasurePrompt()
    {
        if (_measure != null) return _measure.HasFirst ? "测距：指定第二点" : "测距：指定第一点";
        if (_angle != null) return !_angle.HasVertex ? "测角：指定角度顶点" : !_angle.HasFirstRay ? "测角：指定第一条边端点" : "测角：指定第二条边端点";
        return "";
    }

    /// <summary>
    /// 测量取点(视口左键 / 命令行坐标共用)：DIST 两点即报距离，MANG 三点即报夹角；
    /// 报完把量过的线留在高亮通道(黄)供核对，橡皮筋与浮标随即收起。
    /// </summary>
    private void MeasureFeedPoint(double x, double y)
    {
        _lastInputPoint = (x, y);
        if (_measure != null)
        {
            var first = _measure.First;
            var d = _measure.AddPoint(x, y);
            if (d == null) { StatusMsg.Text = MeasurePrompt(); return; }
            StatusMsg.Text = $"距离 = {d:0.###}";
            if (first is { } f) ShowMeasuredLines(new LineEntity { X0 = f.x, Y0 = f.y, X1 = x, Y1 = y });
            _measure = null;
        }
        else if (_angle != null)
        {
            var v = _angle.Vertex; var a = _angle.FirstRay;
            var deg = _angle.AddPoint(x, y);
            if (deg == null) { StatusMsg.Text = MeasurePrompt(); return; }
            StatusMsg.Text = $"角度 = {deg:0.##}°";
            if (v is { } vv && a is { } aa)
                ShowMeasuredLines(new LineEntity { X0 = aa.x, Y0 = aa.y, X1 = vv.x, Y1 = vv.y },
                                  new LineEntity { X0 = vv.x, Y0 = vv.y, X1 = x, Y1 = y });
            _angle = null;
        }
        else return;
        HideDragTip();
        RefreshScenePreview();   // 命令已结束: 立刻擦掉橡皮筋(不等下一次光标移动)
        SyncPrompt();
    }

    /// <summary>量过的线以高亮色留在屏幕上(下一次选中/清选即被替换)。走镶嵌以减渲染原点——大坐标图纸直接塞世界坐标会整体跑飞。</summary>
    private void ShowMeasuredLines(params LineEntity[] lines)
    {
        var g = new List<float>();
        foreach (var l in lines) l.Tessellate(g);
        Viewport.SetHighlight(g.ToArray());
    }
}
