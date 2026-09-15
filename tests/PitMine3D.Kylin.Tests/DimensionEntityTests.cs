using System;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 标注实体（§三二七）：一条标注是**一个对象**。这组测试盯的是"成为一等对象"带来的那几件事 ——
/// 测量值算出来而不是存下来、整体变换后仍是一条标注、逐实体覆盖能存能读、三个可见性开关真起作用。
/// </summary>
public class DimensionEntityTests
{
    private static DimensionEntity Aligned(double len = 100)
        => new() { Kind = DimensionEntity.DimKind.Aligned, X1 = 0, Y1 = 0, X2 = len, Y2 = 0, OffX = 0, OffY = 20, BaseHeight = 2 };

    private static DimensionEntity Radial(double r = 50)
        => new() { Kind = DimensionEntity.DimKind.Radial, Cx = 10, Cy = 10, Radius = r, DirX = 1, DirY = 0, BaseHeight = 2 };

    // ── 测量值是算出来的 ──────────────────────────────────────
    [Fact]
    public void 测量值随定义点走_不是存下来的()
    {
        var d = Aligned(100);
        Assert.Equal(100, d.Measurement, 9);
        d.X2 = 250;                       // 挪一下测点
        Assert.Equal(250, d.Measurement, 9);   // 数字立刻跟着变 —— 这正是标注该有的样子
        Assert.Contains("250", d.DisplayText(d.EffectiveStyle()));
    }

    [Fact]
    public void 半径标注量的是半径且带R前缀()
    {
        var d = Radial(50);
        Assert.Equal(50, d.Measurement, 9);
        Assert.StartsWith("R", d.DisplayText(d.EffectiveStyle()));
    }

    [Fact]
    public void 文字内容覆盖压过测量值()
    {
        var d = Aligned(100);
        d.TextOverride = "≈100 (估)";
        Assert.Equal("≈100 (估)", d.DisplayText(d.EffectiveStyle()));
        Assert.Equal(100, d.Measurement, 9);   // 覆盖只改显示, 不改测量
    }

    [Fact]
    public void 小数位覆盖影响显示()
    {
        var d = Aligned(100.456);
        d.DecimalPlaces = 0;
        Assert.Equal("100", d.DisplayText(d.EffectiveStyle()));
        d.DecimalPlaces = 2;
        Assert.Equal("100.46", d.DisplayText(d.EffectiveStyle()));
    }

    // ── 部件与可见性开关 ──────────────────────────────────────
    [Fact]
    public void 对齐标注出两条界线_一条尺寸线_四个箭头()
    {
        var p = Aligned().Build();
        Assert.Equal(2, p.ExtLines.Count);
        Assert.Single(p.DimLines);
        Assert.Equal(4, p.Arrows.Count);
        Assert.NotNull(p.Text);
    }

    [Fact]
    public void 半径标注没有界线_一条径向线_两个箭头()
    {
        var p = Radial().Build();
        Assert.Empty(p.ExtLines);
        Assert.Single(p.DimLines);
        Assert.Equal(2, p.Arrows.Count);
    }

    [Fact]
    public void 三个可见性开关各管各的()
    {
        var d = Aligned();
        Assert.Equal(8, d.VisibleParts().Count);        // 2 界线 + 1 尺寸线 + 4 箭头 + 1 文字

        d.ExtLine1Visible = false;
        Assert.Equal(7, d.VisibleParts().Count);

        d.ExtLine2Visible = false;
        Assert.Equal(6, d.VisibleParts().Count);

        d.DimLineVisible = false;                        // 尺寸线关掉时箭头跟着走 —— 光留箭头没有意义
        Assert.Single(d.VisibleParts());                 // 只剩文字
        Assert.IsType<TextEntity>(d.VisibleParts()[0]);
    }

    [Fact]
    public void 两测点重合时只出文字不画退化线()
    {
        var d = new DimensionEntity { X1 = 5, Y1 = 5, X2 = 5, Y2 = 5, OffX = 5, OffY = 8, BaseHeight = 2 };
        var p = d.Build();
        Assert.Empty(p.ExtLines);
        Assert.Empty(p.DimLines);
        Assert.Empty(p.Arrows);
        Assert.NotNull(p.Text);
    }

    // ── 逐实体样式覆盖 ────────────────────────────────────────
    [Fact]
    public void 箭头大小覆盖是绝对长度()
    {
        var d = Aligned();
        d.TextHeight = 4;
        d.ArrowSize = 4;                                  // 绝对长度, 不是比值
        var st = d.EffectiveStyle();
        Assert.Equal(4.0, st.TextHeight, 9);
        Assert.Equal(1.0, st.ArrowRatio, 9);              // 4 / 4 = 1
    }

    [Fact]
    public void 未覆盖时跟随样式()
    {
        var d = Aligned();
        var baseStyle = new DimStyle { TextHeight = 3, ArrowRatio = 0.7, ExtLineOffsetRatio = 0.9 };
        var st = d.EffectiveStyle(baseStyle);
        Assert.Equal(3.0, st.TextHeight, 9);
        Assert.Equal(0.7, st.ArrowRatio, 9);
        Assert.Equal(0.9, st.ExtLineOffsetRatio, 9);
    }

    [Fact]
    public void 清覆盖回到默认()
    {
        var d = Aligned();
        d.ArrowSize = 9; d.TextOverride = "x"; d.DimLineVisible = false; d.TextColor = (1, 0, 0);
        d.ClearOverrides();
        Assert.Null(d.ArrowSize);
        Assert.Null(d.TextOverride);
        Assert.Null(d.TextColor);
        Assert.True(d.DimLineVisible);
    }

    [Fact]
    public void 三处颜色各上各的()
    {
        var d = Aligned();
        d.DimLineColor = (1, 0, 0); d.ExtLineColor = (0, 1, 0); d.TextColor = (0, 0, 1);
        var p = d.Build();
        Assert.Equal(1f, p.DimLines[0].Cr);
        Assert.Equal(1f, p.ExtLines[0].Cg);
        Assert.Equal(1f, p.Text!.Cb);
    }

    [Fact]
    public void 文字位置覆盖生效()
    {
        var d = Aligned();
        double defX = d.Build().Text!.X;
        d.TextPosX = 999; d.TextPosY = 888;
        Assert.Equal(999, d.Build().Text!.X, 9);
        Assert.Equal(888, d.Build().Text!.Y, 9);
        Assert.NotEqual(999, defX);
    }

    // ── 变换后仍是一条标注 ────────────────────────────────────
    [Fact]
    public void 平移后仍是标注且测量值不变()
    {
        var d = Aligned(100);
        var moved = (DimensionEntity)d.Apply(new Affine2(1, 0, 0, 1, 30, -10));
        Assert.Equal(100, moved.Measurement, 9);
        Assert.Equal(30, moved.X1, 9);
        Assert.Equal(-10, moved.Y1, 9);
        Assert.Equal(130, moved.X2, 9);
    }

    [Fact]
    public void 缩放后测量值跟着变_箭头等长度覆盖也跟着缩()
    {
        var d = Aligned(100);
        d.ArrowSize = 4; d.TextHeight = 2; d.ExtLineOffset = 1;
        var big = (DimensionEntity)d.Apply(new Affine2(3, 0, 0, 3, 0, 0));
        Assert.Equal(300, big.Measurement, 9);   // 放大 3 倍, 尺寸数字就该是 3 倍
        Assert.Equal(12, big.ArrowSize!.Value, 9);
        Assert.Equal(6, big.TextHeight!.Value, 9);
        Assert.Equal(3, big.ExtLineOffset!.Value, 9);
    }

    [Fact]
    public void 旋转后方向跟着转()
    {
        var d = Radial(50);
        d.DirX = 1; d.DirY = 0;
        // 逆时针 90°: (1,0) → (0,1)
        var rot = (DimensionEntity)d.Apply(new Affine2(0, 1, -1, 0, 0, 0));
        Assert.Equal(0, rot.DirX, 6);
        Assert.Equal(1, rot.DirY, 6);
        Assert.Equal(50, rot.Measurement, 6);   // 旋转不改半径
    }

    [Fact]
    public void 变换保住图层与颜色()
    {
        var d = Aligned();
        d.LayerName = "标注层"; d.Cr = 0.1f; d.Cg = 0.2f; d.Cb = 0.3f;
        var e = d.Apply(new Affine2(1, 0, 0, 1, 5, 5));
        Assert.Equal("标注层", e.LayerName);
        Assert.Equal(0.2f, e.Cg);
    }

    [Fact]
    public void 整体平移把每个定义点都带走()
    {
        var d = Aligned();
        d.TextPosX = 10; d.TextPosY = 10;
        d.Translate(7, 3);
        Assert.Equal(7, d.X1, 9);
        Assert.Equal(107, d.X2, 9);
        Assert.Equal(23, d.OffY, 9);
        Assert.Equal(17, d.TextPosX!.Value, 9);
    }

    // ── 存档往返 ──────────────────────────────────────────────
    [Fact]
    public void 存档往返_对齐标注定义参数全保住()
    {
        var scene = new Scene();
        var d = Aligned(123.5);
        d.ArrowSize = 3; d.ExtLineOffset = 1.5; d.ExtLineExtension = 2.5;
        d.TextHeight = 4; d.TextOffset = 1.2; d.TextOverride = "自定义";
        d.TextPosX = 11; d.TextPosY = 22; d.DecimalPlaces = 3;
        d.ExtLine2Visible = false; d.DimLineColor = (1, 0, 0); d.TextColor = (0, 0, 1);
        scene.Add(d);

        var back = (DimensionEntity)SceneIO.Load(SceneIO.Save(scene)).Entities.Single();
        Assert.Equal(DimensionEntity.DimKind.Aligned, back.Kind);
        Assert.Equal(123.5, back.Measurement, 6);
        Assert.Equal(3, back.ArrowSize!.Value, 9);
        Assert.Equal(1.5, back.ExtLineOffset!.Value, 9);
        Assert.Equal(2.5, back.ExtLineExtension!.Value, 9);
        Assert.Equal(4, back.TextHeight!.Value, 9);
        Assert.Equal(1.2, back.TextOffset!.Value, 9);
        Assert.Equal("自定义", back.TextOverride);
        Assert.Equal(11, back.TextPosX!.Value, 9);
        Assert.Equal(3, back.DecimalPlaces!.Value);
        Assert.False(back.ExtLine2Visible);
        Assert.True(back.ExtLine1Visible);
        Assert.Equal((1f, 0f, 0f), back.DimLineColor!.Value);
        Assert.Equal((0f, 0f, 1f), back.TextColor!.Value);
        Assert.Null(back.ExtLineColor);       // 没覆盖的就该读回 null, 不是黑色
    }

    [Fact]
    public void 存档往返_半径标注()
    {
        var scene = new Scene();
        scene.Add(Radial(42));
        var back = (DimensionEntity)SceneIO.Load(SceneIO.Save(scene)).Entities.Single();
        Assert.Equal(DimensionEntity.DimKind.Radial, back.Kind);
        Assert.Equal(42, back.Radius, 9);
        Assert.Equal(10, back.Cx, 9);
    }

    [Fact]
    public void 存档往返_没覆盖的一概读回null()
    {
        var scene = new Scene();
        scene.Add(Aligned());
        var back = (DimensionEntity)SceneIO.Load(SceneIO.Save(scene)).Entities.Single();
        Assert.Null(back.ArrowSize);
        Assert.Null(back.ExtLineOffset);
        Assert.Null(back.TextHeight);
        Assert.Null(back.TextOverride);
        Assert.Null(back.TextPosX);
        Assert.Null(back.DecimalPlaces);
        Assert.True(back.DimLineVisible);
    }

    // ── 场景集成 ──────────────────────────────────────────────
    [Fact]
    public void 能上屏且包围盒罩得住()
    {
        var d = Aligned();
        var buf = new System.Collections.Generic.List<float>();
        d.Tessellate(buf);
        Assert.NotEmpty(buf);

        var b = d.PreviewAabb();
        Assert.NotNull(b);
        Assert.True(b!.Value.minX <= 0 && b.Value.maxX >= 100);
        Assert.True(b.Value.maxY >= 20);   // 尺寸线偏到 y=20
    }

    [Fact]
    public void 夹点给到定义点()
    {
        var g = Aligned().GripPoints();
        Assert.Contains(g, p => Math.Abs(p.x) < 1e-9 && Math.Abs(p.y) < 1e-9);       // 测点1
        Assert.Contains(g, p => Math.Abs(p.x - 100) < 1e-9 && Math.Abs(p.y) < 1e-9);  // 测点2

        var gr = Radial().GripPoints();
        Assert.Contains(gr, p => Math.Abs(p.x - 10) < 1e-9 && Math.Abs(p.y - 10) < 1e-9);   // 圆心
    }

    [Fact]
    public void 对齐标注向通用夹点系统公开高度节点()
    {
        var g = Aligned().Grips();

        Assert.Equal(4, g.Count);
        Assert.Equal((0d, 0d), g[0]);
        Assert.Equal((100d, 0d), g[1]);
        Assert.Equal((50d, 20d), g[2]);       // 尺寸线中点：拖它调高低
    }

    [Fact]
    public void 拖动高度节点只调整尺寸线高度()
    {
        var moved = Assert.IsType<DimensionEntity>(Aligned().MoveGrip(2, 80, 35));

        Assert.Equal(0, moved.X1, 9);
        Assert.Equal(0, moved.Y1, 9);
        Assert.Equal(100, moved.X2, 9);
        Assert.Equal(0, moved.Y2, 9);
        var dimLine = Assert.Single(moved.Build().DimLines);
        Assert.Equal(35, dimLine.Y0, 9);
        Assert.Equal(35, dimLine.Y1, 9);
    }

    // ── 特性面板 ──────────────────────────────────────────────
    [Fact]
    public void 特性面板列出原版那三组()
    {
        var rows = EntityProperties.Describe(Aligned());
        var cats = rows.Select(r => r.cat).Distinct().ToList();
        Assert.Contains("直线和箭头", cats);
        Assert.Contains("文字", cats);
        Assert.Contains("几何", cats);
        Assert.Contains(rows, r => r.label == "箭头大小");
        Assert.Contains(rows, r => r.label == "测量值");
        Assert.Contains(rows, r => r.label == "定义点1");
    }

    [Fact]
    public void 特性面板_未覆盖显随样式而不是0()
    {
        // 0 是合法值, 显 0 会让人以为已经设过了
        var rows = EntityProperties.Describe(Aligned());
        Assert.Equal("随样式", rows.First(r => r.label == "箭头大小").value);
        Assert.Equal("随实体", rows.First(r => r.label == "文字颜色").value);
        Assert.Equal("（测量值）", rows.First(r => r.label == "文字内容").value);
    }

    [Fact]
    public void 特性面板_按种类给几何格()
    {
        var alignedLabels = EntityProperties.EditableLabels(Aligned());
        Assert.Contains("定义点1", alignedLabels);
        Assert.DoesNotContain("圆心", alignedLabels);

        var radialLabels = EntityProperties.EditableLabels(Radial());
        Assert.Contains("圆心", radialLabels);
        Assert.DoesNotContain("定义点1", radialLabels);
    }

    [Fact]
    public void 特性面板_改箭头大小生效()
    {
        var e = (DimensionEntity)EntityProperties.WithEdited(Aligned(), "箭头大小", "5")!;
        Assert.Equal(5, e.ArrowSize!.Value, 9);
    }

    [Fact]
    public void 特性面板_留空即清除覆盖()
    {
        var d = Aligned();
        d.ArrowSize = 5;
        var e = (DimensionEntity)EntityProperties.WithEdited(d, "箭头大小", "")!;
        Assert.Null(e.ArrowSize);          // 回到随样式, 不是变成 0
    }

    [Fact]
    public void 特性面板_改定义点后测量值跟着变()
    {
        var e = (DimensionEntity)EntityProperties.WithEdited(Aligned(100), "定义点2", "300,0")!;
        Assert.Equal(300, e.Measurement, 6);
    }

    [Fact]
    public void 特性面板_乱填不改原值()
    {
        Assert.Null(EntityProperties.WithEdited(Aligned(), "箭头大小", "看不懂"));
        Assert.Null(EntityProperties.WithEdited(Aligned(), "小数位", "99"));      // 越界
        Assert.Null(EntityProperties.WithEdited(Radial(), "半径", "-5"));         // 负半径
        Assert.Null(EntityProperties.WithEdited(Aligned(), "定义点1", "只有一个数"));
    }

    [Fact]
    public void 特性面板_可见性开关能改()
    {
        var e = (DimensionEntity)EntityProperties.WithEdited(Aligned(), "尺寸线可见", "否")!;
        Assert.False(e.DimLineVisible);
        var e2 = (DimensionEntity)EntityProperties.WithEdited(e, "尺寸线可见", "是")!;
        Assert.True(e2.DimLineVisible);
    }

    [Fact]
    public void 特性面板_编辑不动原实体()
    {
        var d = Aligned();
        EntityProperties.WithEdited(d, "箭头大小", "5");
        Assert.Null(d.ArrowSize);   // WithEdited 出的是新实体, 原件不该被就地改
    }

    [Fact]
    public void 类型名按种类分()
    {
        Assert.Equal("对齐标注", EntityTypeName.Of(Aligned()));
        Assert.Equal("半径标注", EntityTypeName.Of(Radial()));
    }
}
