using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 测量橡皮筋（jig）：DIST 两点测距 / MANG 三点测角 / AREA 连续点取区域过程中，按「已取点 + 光标」把牵引线与实时读数画到
/// 预览通道 —— 忠实原版内核 MeasureDistanceJigAdapter / MeasureAngleJigAdapter(getAuxiliaryPreview + getAnnotations)：
///   · DIST：第一点 → 光标 一条牵引线，中点处黄色 HUD「距离: x.xxx」；
///   · MANG：顶点 → 光标（第一条边）；有第一条边后 顶点→a 实线 + 顶点→光标 牵引线，顶点右上青色 HUD「角度: x.xx°」；
///   · AREA：已取边界实线 + 最后一条/闭合回第一点的牵引虚线，光标旁黄色 HUD「面积: x.xxx m² · 周长: x.xxx m」。
/// 测量不落任何实体(原版 createPreview/createEntity 都返回 nullptr)，只在状态栏/信息栏报结果；
/// 点击取点与命令行键入坐标共用 <see cref="MeasureFeedPoint"/>。
/// </summary>
public partial class MainWindow
{
    private AreaState? _area;
    private bool _measureJigShown;   // 上一帧画过测量橡皮筋: 命令结束后下一次光标移动把预览通道擦干净

    /// <summary>是否有测量命令正在等待取点（包括尚未取到第一点的面积 jig）。</summary>
    private bool MeasureCommandActive => _measure != null || _angle != null || _area != null;

    /// <summary>新命令接管交互时取消未完成的面积 jig。</summary>
    private void CancelAreaMeasure()
    {
        if (_area == null) return;
        _area = null;
        HideDragTip();
        RefreshScenePreview();
    }

    /// <summary>有测量命令在取点且已有锚点(需要橡皮筋跟随光标)。</summary>
    private bool MeasureJigActive => _measure?.HasFirst == true || _angle?.HasVertex == true || _area?.HasPoints == true;

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
        if (_area is { HasPoints: true } area)
        {
            var points = area.Points;
            if (points.Count > 1)
                new PolylineEntity
                {
                    Points = new List<(double x, double y)>(points),
                    Cr = 1f, Cg = 1f, Cb = 1f,
                }.Tessellate(list);

            var last = points[^1];
            new LineEntity { X0 = last.x, Y0 = last.y, X1 = c.x, Y1 = c.y, Dash = dash, Cr = 1f, Cg = 1f, Cb = 1f }.Tessellate(list);
            if (points.Count > 1)
            {
                var first = points[0];
                new LineEntity { X0 = c.x, Y0 = c.y, X1 = first.x, Y1 = first.y, Dash = dash, Cr = 1f, Cg = 1f, Cb = 1f }.Tessellate(list);
            }

            var candidate = new List<(double x, double y)>(points) { c };
            double areaValue = GeomMeasure.Area(candidate);
            double perimeter = GeomMeasure.Perimeter(candidate, true);
            double off = PixelsToWorld(_lastPointer, 20);
            new TextEntity
            {
                X = c.x + off, Y = c.y + off, Height = h,
                Text = $"面积: {areaValue:0.###} m² · 周长: {perimeter:0.###} m",
                Cr = 1f, Cg = 1f, Cb = 0.2f,
            }.TessellatePick(list);
        }
    }

    /// <summary>光标浮标里的实时读数(距离/角度/面积)，接在步骤提示后面。</summary>
    private string? MeasureJigHint((double x, double y) c)
    {
        if (_measure is { First: { } p1 })
        {
            double dx = c.x - p1.x, dy = c.y - p1.y;
            return $"距离 {System.Math.Sqrt(dx * dx + dy * dy):0.###}";
        }
        if (_angle is { Vertex: { } v, FirstRay: { } a })
            return $"角度 {AngleMath.AngleDeg(v.x, v.y, a.x, a.y, c.x, c.y):0.##}°";
        if (_area is { HasPoints: true } area)
        {
            var points = new List<(double x, double y)>(area.Points) { c };
            return $"面积 {GeomMeasure.Area(points):0.###} m² · 周长 {GeomMeasure.Perimeter(points, true):0.###} m";
        }
        return null;
    }

    /// <summary>测量命令当前步骤的提示(同原版 defineSteps 的三/两句)。</summary>
    private string MeasurePrompt()
    {
        if (_measure != null) return _measure.HasFirst ? "测距：指定第二点" : "测距：指定第一点";
        if (_angle != null) return !_angle.HasVertex ? "测角：指定角度顶点" : !_angle.HasFirstRay ? "测角：指定第一条边端点" : "测角：指定第二条边端点";
        if (_area != null) return _area.HasPoints ? $"面积：指定下一点（已取 {_area.Points.Count} 点，回车/右键结束）" : "面积：指定第一点";
        return "";
    }

    /// <summary>
    /// 测量取点(视口左键 / 命令行坐标共用)：DIST 两点即报距离，MANG 三点即报夹角；
    /// 报完把量过的线留在高亮通道(黄)供核对，橡皮筋与浮标随即收起。
    /// </summary>
    private void MeasureFeedPoint(double x, double y)
    {
        _lastInputPoint = (x, y);
        if (_area != null)
        {
            _area.AddPoint(x, y);
            var points = _area.Points;
            double area = GeomMeasure.Area(points);
            double perimeter = GeomMeasure.Perimeter(points, true);
            StatusMsg.Text = $"面积测量：已取 {points.Count} 点 · 当前面积 = {area:0.###} m² · 周长 = {perimeter:0.###} m（回车/右键完成）";
            RefreshScenePreview();
            SyncPrompt();
            return;
        }
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

    /// <summary>结束面积 jig：闭合当前点列并报告面积/周长，不创建实体。</summary>
    private void FinishAreaMeasure()
    {
        if (_area == null) return;
        var result = _area.Finish();
        if (result == null)
        {
            _area = null;
            HideDragTip();
            RefreshScenePreview();
            SyncPrompt();
            StatusMsg.Text = "面积测量：区域至少需要 3 个点，已取消";
            return;
        }

        StatusMsg.Text = $"面积 = {result.Value.Area:0.###} m² · 周长(闭合) = {result.Value.Perimeter:0.###} m · {result.Value.Points.Count} 个点";
        ShowMeasuredArea(result.Value.Points);
        _area = null;
        HideDragTip();
        RefreshScenePreview();
        SyncPrompt();
    }

    private void ShowMeasuredArea(IReadOnlyList<(double x, double y)> points)
    {
        var g = new List<float>();
        new PolylineEntity
        {
            Points = new List<(double x, double y)>(points),
            Closed = true,
        }.Tessellate(g);
        Viewport.SetHighlight(g.ToArray());
    }

    /// <summary>量过的线以高亮色留在屏幕上(下一次选中/清选即被替换)。走镶嵌以减渲染原点——大坐标图纸直接塞世界坐标会整体跑飞。</summary>
    private void ShowMeasuredLines(params LineEntity[] lines)
    {
        var g = new List<float>();
        foreach (var l in lines) l.Tessellate(g);
        Viewport.SetHighlight(g.ToArray());
    }
}
