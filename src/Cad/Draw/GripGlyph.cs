using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 夹点方块的画法（AutoCAD 样式）—— 纯几何，可单测。
///
/// AutoCAD 的夹点是**实心**小方块，三种状态只靠颜色区分：
/// 冷（未选）蓝 · 暖（光标悬停）绿 · 热（已选中）红；三态同尺寸，外加一圈压深的细描边。
///
/// 为什么实心是用一组水平扫描线画的、而不是走三角通道：视口里高亮面是"开深度 + 多边形偏移"
/// 画的（为了压住三角网表面），而夹点要的是**永远浮在最上层**，那条路会被几何挡住。
/// 线通道是关深度画的，正合适；方块只有十来像素，十几条扫描线就填实了。
/// </summary>
public static class GripGlyph
{
    /// <summary>
    /// 填充扫描线条数。方块换算到屏幕上十几到二十几个设备像素（还要乘 DPI 缩放），
    /// 条数少了会露出横条纹（14 条实测就是一格一格的），这里取足够密的值 ——
    /// 夹点总共几百上千个，多几条线的开销可以忽略。
    /// </summary>
    public const int FillLines = 36;

    /// <summary>
    /// 方块半边长(屏幕像素, DIP)。固定像素、与视图无关 —— 同原版 Viewport.cpp 的夹点是在屏幕空间叠加层上画的定尺寸方块。
    /// 早先拿"捕捉容差 × 0.45"当尺寸: 容差调到 60px 方块就 54px 见方, 是"节点太大"的来源之一, 故脱钩成常量。
    /// 世界半边长 = 此值 × 该夹点位置的"每像素世界长度"(3D 透视按各自深度算, 见 Camera.WorldPerPixelAt)。
    /// </summary>
    /// 可在「选项 · 选择集 · 夹点尺寸(像素)」改(同原版 Options.Selection.GripSize, 存的是整边长 4~20, 这里是半边)。
    public static double HalfSizePx { get; set; } = DefaultHalfSizePx;
    public const double DefaultHalfSizePx = 5.0;

    /// <summary>描边相对填充色的压深系数。</summary>
    public const float EdgeShade = 0.45f;

    // 默认值必须声明在可配属性之前：静态初始化器按文本顺序跑, 反过来写 ColdColor 会拿到还没赋值的 (0,0,0)
    public static readonly (float r, float g, float b) DefaultCold = (0.10f, 0.45f, 0.95f);
    public static readonly (float r, float g, float b) DefaultHot = (0.95f, 0.20f, 0.15f);
    /// <summary>冷(未选)夹点色，默认蓝；「选项 · 选择集 · 未选夹点颜色」可改(原版 Options.Selection.GripUnsel)。</summary>
    public static (float r, float g, float b) ColdColor { get; set; } = DefaultCold;
    /// <summary>热(已选)夹点色，默认红；「选项 · 选择集 · 选中夹点颜色」可改(原版 Options.Selection.GripSel)。</summary>
    public static (float r, float g, float b) HotColor { get; set; } = DefaultHot;
    /// <summary>暖(悬停)绿：原版无此项配置, 固定。</summary>
    public static readonly (float r, float g, float b) WarmColor = (0.15f, 0.85f, 0.25f);

    /// <summary>AutoCAD 夹点配色：热(已选)红 / 暖(悬停)绿 / 冷(未选)蓝(冷/热可配)。</summary>
    public static (float r, float g, float b) Color(bool selected, bool hovered)
        => selected ? HotColor
         : hovered ? WarmColor
         : ColdColor;

    /// <summary>
    /// 把一个夹点方块追加到线段缓冲（交错 P3_C3，每 2 顶点一段）。
    /// </summary>
    /// <param name="o">目标缓冲。</param>
    /// <param name="cx">中心 X（世界坐标）。</param>
    /// <param name="cy">中心 Y。</param>
    /// <param name="h">半边长（世界单位，由屏幕像素换算）。</param>
    /// <param name="z">
    /// 方块所在高程。必须传图元自己的 Z（<see cref="SceneEntity.GripZ"/>）——
    /// 一律画在 0 上的话，图元一有标高，三维视图里方块就飘离节点。
    /// </param>
    public static void Append(List<float> o, double cx, double cy, double h, float r, float g, float b, double z = 0)
    {
        // 减渲染局部原点再转 float —— 与图元自身的镶嵌同一口径。少减这一下，方块就会整体
        // 平移一个原点的量、离节点老远（踩过：看着像"夹点位置不对"，其实是没跟上 RenderOrigin）。
        double ox = RenderOrigin.X, oy = RenderOrigin.Y;
        float fz = (float)z;
        void Seg(double x0, double y0, double x1, double y1, float cr, float cg, float cb)
        {
            o.Add((float)(x0 - ox)); o.Add((float)(y0 - oy)); o.Add(fz); o.Add(cr); o.Add(cg); o.Add(cb);
            o.Add((float)(x1 - ox)); o.Add((float)(y1 - oy)); o.Add(fz); o.Add(cr); o.Add(cg); o.Add(cb);
        }

        for (int i = 0; i <= FillLines; i++)                     // 填充：自下而上的水平扫描线
        {
            double y = cy - h + 2 * h * i / FillLines;
            Seg(cx - h, y, cx + h, y, r, g, b);
        }

        float dr = r * EdgeShade, dg = g * EdgeShade, db = b * EdgeShade;   // 描边：同色压深，浅色图元上也咬得住边
        Seg(cx - h, cy - h, cx + h, cy - h, dr, dg, db);
        Seg(cx + h, cy - h, cx + h, cy + h, dr, dg, db);
        Seg(cx + h, cy + h, cx - h, cy + h, dr, dg, db);
        Seg(cx - h, cy + h, cx - h, cy - h, dr, dg, db);
    }

    /// <summary>一个夹点方块占多少段线（供缓冲预估与回归断言）。</summary>
    public const int SegmentsPerGrip = FillLines + 1 + 4;
}
