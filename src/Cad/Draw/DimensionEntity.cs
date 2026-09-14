using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 标注实体 —— 一条标注是**一个对象**，不是一堆散线散字（§三二七）。
///
/// 此前 Kylin 的 <see cref="DimTools"/> 是"建完即炸开"：`Build*` 返回一串 <see cref="LineEntity"/> +
/// <see cref="TextEntity"/> 直接入场景。于是标注选不中一整条、挪不了、更没法回头改箭头大小或文字内容，
/// 原版那扇**逐条标注的特性编辑器**（`DimensionStyleWindow`）也就无处落地 —— §三二二 登记的就是这件事。
///
/// 本类只存**定义参数**，几何每次镶嵌时现推：
///   · 测量值是**算出来的**，不是存下来的 —— 挪一下定义点，尺寸数字立刻跟着变，这正是标注该有的样子；
///   · 逐实体的样式覆盖（箭头大小 / 界线偏移 / 界线延伸 / 文字高度·偏移·内容·位置 / 三处颜色 /
///     三个可见性开关）一一对应原版编辑器里的那几格。
///
/// **只做 对齐(Aligned) 与 半径(Radial) 两种**：原版的标注对象就是 <c>AcDbAlignedDimension</c> 与
/// <c>AcDbRadialDimension</c>（它自己的编辑器 typeLabel 也只认这两个）。线性/连续/角度/直径等命令
/// 继续按老路出散实体，见本文件末尾的登记。
/// </summary>
public sealed class DimensionEntity : SceneEntity
{
    /// <summary>标注种类。对应原版的 AcDbAlignedDimension / AcDbRadialDimension。</summary>
    public enum DimKind { Aligned = 0, Radial = 1 }

    public DimKind Kind = DimKind.Aligned;

    // ── 几何定义点 ──────────────────────────────────────────
    /// <summary>对齐标注：第一测点。</summary>
    public double X1, Y1;
    /// <summary>对齐标注：第二测点。</summary>
    public double X2, Y2;
    /// <summary>对齐标注：尺寸线通过的点（决定偏移量与方向）。</summary>
    public double OffX, OffY;

    /// <summary>半径标注：圆心。</summary>
    public double Cx, Cy;
    /// <summary>半径标注：半径。</summary>
    public double Radius;
    /// <summary>半径标注：引出方向（不必是单位向量；零向量按 +X 处理）。</summary>
    public double DirX = 1, DirY;

    /// <summary>建标注时的参考字高（样式里 TextHeight=0 时用它）。</summary>
    public double BaseHeight = 2.0;

    // ── 逐实体样式覆盖（null = 跟随样式）──────────────────────
    /// <summary>箭头大小（世界单位）。原版「箭头大小」。</summary>
    public double? ArrowSize;
    /// <summary>界线偏移：测点与延伸线起点的间隙。原版「界线偏移」。</summary>
    public double? ExtLineOffset;
    /// <summary>界线延伸：延伸线越过尺寸线的长度。原版「界线延伸」。</summary>
    public double? ExtLineExtension;
    /// <summary>文字高度。原版「文字高度」。</summary>
    public double? TextHeight;
    /// <summary>文字偏移（离尺寸线）。原版「文字偏移」。</summary>
    public double? TextOffset;

    /// <summary>文字内容覆盖；null/空 = 用测量值。原版「文字内容」。</summary>
    public string? TextOverride;
    /// <summary>文字位置覆盖（世界坐标，文字左下角）；null = 按默认摆放。原版「文字位置 X/Y」。</summary>
    public double? TextPosX, TextPosY;

    /// <summary>尺寸线颜色 / 界线颜色 / 文字颜色；null = 用实体自身颜色。原版那三格。</summary>
    public (float r, float g, float b)? DimLineColor, ExtLineColor, TextColor;

    /// <summary>三个可见性开关。原版「界线 1 可见 / 界线 2 可见 / 尺寸线可见」。</summary>
    public bool ExtLine1Visible = true, ExtLine2Visible = true, DimLineVisible = true;

    /// <summary>小数位（同 <see cref="DimStyle.DecimalPlaces"/>，逐实体覆盖）。</summary>
    public int? DecimalPlaces;

    /// <summary>
    /// 测量值：对齐=两测点真距，半径=半径。**算出来、不存** —— 挪定义点它就跟着变。
    /// </summary>
    public double Measurement => Kind == DimKind.Radial
        ? Math.Abs(Radius)
        : Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));

    /// <summary>本实体实际生效的样式（把逐实体覆盖折进一份 <see cref="DimStyle"/>）。</summary>
    public DimStyle EffectiveStyle(DimStyle? baseStyle = null)
    {
        var b = baseStyle ?? DimStyle.Default;
        double H = TextHeight ?? (b.TextHeight > 0 ? b.TextHeight : BaseHeight);
        // 样式里几项是「相对字高的比」，而编辑器给的是绝对长度 —— 这里换算回比值，
        // 免得两套单位在下游打架（原版编辑器里填的就是绝对值）。
        return new DimStyle
        {
            TextHeight = H,
            DecimalPlaces = DecimalPlaces ?? b.DecimalPlaces,
            TickRatio = b.TickRatio,
            ArrowRatio = ArrowSize is { } a && H > 1e-9 ? a / H : b.ArrowRatio,
            ArrowWidthRatio = b.ArrowWidthRatio,
            TextOffsetRatio = TextOffset is { } t && H > 1e-9 ? t / H : b.TextOffsetRatio,
            ExtLineOffsetRatio = ExtLineOffset is { } o && H > 1e-9 ? o / H : b.ExtLineOffsetRatio,
            ExtLineExtensionRatio = ExtLineExtension is { } e && H > 1e-9 ? e / H : b.ExtLineExtensionRatio,
        };
    }

    /// <summary>镶嵌出来的各部件（供渲染与"哪根线归哪一类"的可见性开关用）。</summary>
    public sealed class Parts
    {
        /// <summary>延伸线（界线）。对齐标注两条；半径标注没有。</summary>
        public List<LineEntity> ExtLines = new();
        /// <summary>尺寸线本体（含半径标注的径向线）。</summary>
        public List<LineEntity> DimLines = new();
        /// <summary>箭头。跟尺寸线同开关 —— 尺寸线都藏了，光留两个箭头没有意义。</summary>
        public List<LineEntity> Arrows = new();
        public TextEntity? Text;
        public double Measurement;
    }

    /// <summary>默认文字串：有覆盖用覆盖，否则半径带 R 前缀、对齐给纯数字（同 <see cref="DimTools"/> 的口径）。</summary>
    public string DisplayText(DimStyle st)
    {
        if (!string.IsNullOrEmpty(TextOverride)) return TextOverride!;
        string n = Measurement.ToString(st.NumberFormat, CultureInfo.InvariantCulture);
        return Kind == DimKind.Radial ? "R" + n : n;
    }

    /// <summary>
    /// 按当前定义参数推出各部件。几何口径与 <see cref="DimTools.BuildLinear"/> /
    /// <see cref="DimTools.BuildRadial"/> 一致（那两条是既有命令在用的，不能两套画法各画各的）。
    /// </summary>
    public Parts Build(DimStyle? baseStyle = null)
    {
        var st = EffectiveStyle(baseStyle);
        double H = st.TextHeight > 0 ? st.TextHeight : BaseHeight;
        var p = new Parts { Measurement = Measurement };
        var (dr, dg, db) = DimLineColor ?? (Cr, Cg, Cb);
        var (er, eg, eb) = ExtLineColor ?? (Cr, Cg, Cb);
        var (tr, tg, tb) = TextColor ?? (Cr, Cg, Cb);

        LineEntity L(double a, double b, double c, double d, float r, float g, float bl)
            => new() { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = r, Cg = g, Cb = bl, LayerName = LayerName, Elevation = Elevation };

        if (Kind == DimKind.Radial)
        {
            double dl = Math.Sqrt(DirX * DirX + DirY * DirY);
            double ux = dl < 1e-9 ? 1 : DirX / dl, uy = dl < 1e-9 ? 0 : DirY / dl;
            double ex = Cx + ux * Radius, ey = Cy + uy * Radius;
            p.DimLines.Add(L(Cx, Cy, ex, ey, dr, dg, db));                     // 径向线

            double ah = H * st.ArrowRatio, aw = H * st.ArrowWidthRatio;
            double bx = ex - ux * ah, by = ey - uy * ah;
            double px = -uy, py = ux;
            p.Arrows.Add(L(ex, ey, bx + px * aw, by + py * aw, dr, dg, db));
            p.Arrows.Add(L(ex, ey, bx - px * aw, by - py * aw, dr, dg, db));

            string s = DisplayText(st);
            p.Text = new TextEntity
            {
                X = TextPosX ?? ex + ux * H * 0.3, Y = TextPosY ?? ey + uy * H * 0.3,
                Height = H, Text = s, Cr = tr, Cg = tg, Cb = tb, LayerName = LayerName, Elevation = Elevation,
            };
            return p;
        }

        // ── 对齐标注（同 DimTools.BuildLinear 的偏移/延伸/箭头口径）──
        double dx = X2 - X1, dy = Y2 - Y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
        {
            // 两测点重合：给不出方向，只留文字，别画出一堆退化线
            p.Text = new TextEntity
            {
                X = TextPosX ?? X1, Y = TextPosY ?? Y1, Height = H, Text = DisplayText(st),
                Cr = tr, Cg = tg, Cb = tb, LayerName = LayerName, Elevation = Elevation,
            };
            return p;
        }
        double uxx = dx / len, uyy = dy / len;
        double nx = -uyy, ny = uxx;
        double proj = (OffX - X1) * nx + (OffY - Y1) * ny;
        double sgn = proj >= 0 ? 1 : -1;
        double gap = H * st.ExtLineOffsetRatio, ext = H * st.ExtLineExtensionRatio;

        double d1x = X1 + nx * proj, d1y = Y1 + ny * proj;
        double d2x = X2 + nx * proj, d2y = Y2 + ny * proj;
        p.ExtLines.Add(L(X1 + nx * sgn * gap, Y1 + ny * sgn * gap, d1x + nx * sgn * ext, d1y + ny * sgn * ext, er, eg, eb));
        p.ExtLines.Add(L(X2 + nx * sgn * gap, Y2 + ny * sgn * gap, d2x + nx * sgn * ext, d2y + ny * sgn * ext, er, eg, eb));
        p.DimLines.Add(L(d1x, d1y, d2x, d2y, dr, dg, db));

        double ah2 = H * st.ArrowRatio, aw2 = H * st.ArrowWidthRatio;
        p.Arrows.Add(L(d1x, d1y, d1x + uxx * ah2 + nx * aw2, d1y + uyy * ah2 + ny * aw2, dr, dg, db));
        p.Arrows.Add(L(d1x, d1y, d1x + uxx * ah2 - nx * aw2, d1y + uyy * ah2 - ny * aw2, dr, dg, db));
        p.Arrows.Add(L(d2x, d2y, d2x - uxx * ah2 + nx * aw2, d2y - uyy * ah2 + ny * aw2, dr, dg, db));
        p.Arrows.Add(L(d2x, d2y, d2x - uxx * ah2 - nx * aw2, d2y - uyy * ah2 - ny * aw2, dr, dg, db));

        string txt = DisplayText(st);
        double mx = (d1x + d2x) / 2, my = (d1y + d2y) / 2;
        double tox = nx * sgn * H * st.TextOffsetRatio, toy = ny * sgn * H * st.TextOffsetRatio;
        double tw = txt.Length * H * 0.8;   // 粗略文字宽 → 居中(同 DimTools)
        p.Text = new TextEntity
        {
            X = TextPosX ?? mx + tox - tw / 2, Y = TextPosY ?? my + toy,
            Height = H, Text = txt, Cr = tr, Cg = tg, Cb = tb, LayerName = LayerName, Elevation = Elevation,
        };
        return p;
    }

    /// <summary>按三个可见性开关筛出真正要画的部件（箭头跟尺寸线同开关）。</summary>
    public List<SceneEntity> VisibleParts(DimStyle? baseStyle = null)
    {
        var p = Build(baseStyle);
        var list = new List<SceneEntity>();
        if (ExtLine1Visible && p.ExtLines.Count > 0) list.Add(p.ExtLines[0]);
        if (ExtLine2Visible && p.ExtLines.Count > 1) list.Add(p.ExtLines[1]);
        if (DimLineVisible)
        {
            list.AddRange(p.DimLines);
            list.AddRange(p.Arrows);
        }
        if (p.Text != null) list.Add(p.Text);
        return list;
    }

    public override void Tessellate(List<float> o)
    {
        foreach (var e in VisibleParts()) e.Tessellate(o);
    }

    /// <summary>拾取/框选/高亮：尺寸文字按轮廓参与（真字体下它的线段通道是空的，见 <see cref="TextEntity.TessellatePick"/>）。</summary>
    public override void TessellatePick(List<float> o)
    {
        foreach (var e in VisibleParts()) e.TessellatePick(o);
    }

    /// <summary>
    /// 尺寸文字的**实心字形**走面通道（§三一一 起文字就是填充三角，<see cref="TextEntity.Tessellate"/> 里
    /// <c>skipFilled: true</c> 只吐非填充笔画）。不转发这一条，标注就只剩几根线、那个数字根本不显示 ——
    /// 实测就是这么撞上的。
    /// </summary>
    public override void TessellateFaces(List<float> o)
    {
        foreach (var e in VisibleParts()) e.TessellateFaces(o);
    }

    /// <summary>
    /// 仿射变换：**变的是定义点**，不是画出来的线 —— 这样移动/旋转/缩放/镜像之后它仍是一条标注，
    /// 测量值也跟着变（缩放 2 倍，尺寸数字就该是 2 倍）。
    /// 长度类的逐实体覆盖（箭头大小/界线偏移/延伸/字高/文字偏移）按尺度幅度一并缩放，
    /// 否则整体放大十倍后箭头还是原来那么小，看着像没跟上。
    /// </summary>
    public override SceneEntity Apply(Affine2 m)
    {
        var (x1, y1) = m.Map(X1, Y1);
        var (x2, y2) = m.Map(X2, Y2);
        var (ox, oy) = m.Map(OffX, OffY);
        var (cx, cy) = m.Map(Cx, Cy);
        // 方向是向量: 只过线性部分(A,B,C,D), 不带平移
        double dirx = m.A * DirX + m.C * DirY, diry = m.B * DirX + m.D * DirY;
        double s = m.ScaleMag;
        double? Sc(double? v) => v is { } x ? x * s : null;
        var e = new DimensionEntity
        {
            Kind = Kind,
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, OffX = ox, OffY = oy,
            Cx = cx, Cy = cy, Radius = Radius * s, DirX = dirx, DirY = diry,
            BaseHeight = BaseHeight * s,
            ArrowSize = Sc(ArrowSize), ExtLineOffset = Sc(ExtLineOffset), ExtLineExtension = Sc(ExtLineExtension),
            TextHeight = Sc(TextHeight), TextOffset = Sc(TextOffset),
            TextOverride = TextOverride, DecimalPlaces = DecimalPlaces,
            DimLineColor = DimLineColor, ExtLineColor = ExtLineColor, TextColor = TextColor,
            ExtLine1Visible = ExtLine1Visible, ExtLine2Visible = ExtLine2Visible, DimLineVisible = DimLineVisible,
        };
        if (TextPosX is { } tpx && TextPosY is { } tpy) { var (nx2, ny2) = m.Map(tpx, tpy); e.TextPosX = nx2; e.TextPosY = ny2; }
        return Colored(e);
    }

    public override int PreviewCost() => 96;   // 几条线 + 一段文字, O(1) 常数(同基类约定)

    public override (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)? PreviewAabb()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void P(double x, double y) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
        var p = Build();
        foreach (var l in p.ExtLines) { P(l.X0, l.Y0); P(l.X1, l.Y1); }
        foreach (var l in p.DimLines) { P(l.X0, l.Y0); P(l.X1, l.Y1); }
        foreach (var l in p.Arrows) { P(l.X0, l.Y0); P(l.X1, l.Y1); }
        if (p.Text != null) { P(p.Text.X, p.Text.Y); P(p.Text.X + p.Text.Text.Length * p.Text.Height * 0.8, p.Text.Y + p.Text.Height); }
        if (minX > maxX) return null;
        return (minX, minY, Elevation, maxX, maxY, Elevation);
    }

    /// <summary>夹点：对齐=两测点 + 尺寸线通过点 + 文字位置；半径=圆心 + 圆周端点 + 文字位置。</summary>
    public List<(double x, double y)> GripPoints()
    {
        var g = new List<(double x, double y)>();
        var p = Build();
        if (Kind == DimKind.Radial)
        {
            g.Add((Cx, Cy));
            if (p.DimLines.Count > 0) g.Add((p.DimLines[0].X1, p.DimLines[0].Y1));
        }
        else
        {
            g.Add((X1, Y1));
            g.Add((X2, Y2));
            if (p.DimLines.Count > 0) g.Add(((p.DimLines[0].X0 + p.DimLines[0].X1) / 2, (p.DimLines[0].Y0 + p.DimLines[0].Y1) / 2));
        }
        if (p.Text != null) g.Add((p.Text.X, p.Text.Y));
        return g;
    }

    /// <summary>整体平移（挪一条标注就该整条走，这也是"标注是一个对象"的直接好处）。</summary>
    public void Translate(double dx, double dy)
    {
        X1 += dx; Y1 += dy; X2 += dx; Y2 += dy;
        OffX += dx; OffY += dy;
        Cx += dx; Cy += dy;
        if (TextPosX is { } tx) TextPosX = tx + dx;
        if (TextPosY is { } ty) TextPosY = ty + dy;
    }

    /// <summary>清掉一条逐实体覆盖后回到样式默认（原版编辑器里把格子清空即恢复）。</summary>
    public void ClearOverrides()
    {
        ArrowSize = ExtLineOffset = ExtLineExtension = TextHeight = TextOffset = null;
        TextOverride = null;
        TextPosX = TextPosY = null;
        DimLineColor = ExtLineColor = TextColor = null;
        DecimalPlaces = null;
        ExtLine1Visible = ExtLine2Visible = DimLineVisible = true;
    }
}
