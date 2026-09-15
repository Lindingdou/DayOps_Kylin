using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// Ribbon「特性」组补齐(颜色/线宽栏 + 测量六项) 与 特性面板非单选内容的纯逻辑。
/// 对齐原版 MainWindow「特性」RibbonGroupBox / MultiSelectionProperties / DocumentProperties。
/// </summary>
public class PropertiesRibbonTests
{
    // ── 颜色: ACI 色板 ─────────────────────────────────────────────────────
    [Fact]
    public void AciPalette_ParsesHexRgbIndexAndName()
    {
        Assert.True(AciPalette.TryParse("#FF0000", out float r, out float g, out float b));
        Assert.Equal((1f, 0f, 0f), (r, g, b));
        Assert.True(AciPalette.TryParse("0,255,0", out r, out g, out b));
        Assert.Equal((0f, 1f, 0f), (r, g, b));
        Assert.True(AciPalette.TryParse("5", out r, out g, out b));      // ACI 5 = 蓝
        Assert.Equal((0f, 0f, 1f), (r, g, b));
        Assert.True(AciPalette.TryParse("黄", out r, out g, out b));
        Assert.Equal((1f, 1f, 0f), (r, g, b));
        Assert.False(AciPalette.TryParse("紫外线", out _, out _, out _));
        Assert.False(AciPalette.TryParse("", out _, out _, out _));
    }

    [Fact]
    public void AciPalette_DisplayName_UsesPaletteNameElseHex()
    {
        Assert.Equal("红", AciPalette.DisplayName(1, 0, 0));
        Assert.Equal("#123456", AciPalette.DisplayName(0x12 / 255f, 0x34 / 255f, 0x56 / 255f));
        Assert.Equal("#FFFFFF", AciPalette.Hex(1, 1, 1));
    }

    // ── 线宽下拉候选 ───────────────────────────────────────────────────────
    [Fact]
    public void LineWeightChoices_LeadWithByLayerDefaultByBlock_ThenStandardSteps()
    {
        var c = LineWeightUtil.Choices;
        Assert.Equal(new short[] { -1, -3, -2 }, c.Take(3).ToArray());
        Assert.Equal("随层", LineWeightUtil.Display(c[0]));
        Assert.Equal("默认", LineWeightUtil.Display(c[1]));
        Assert.Equal("随块", LineWeightUtil.Display(c[2]));
        Assert.Contains<short>(25, c);                                   // 0.25 mm 标准档
        Assert.Equal("0.25 mm", LineWeightUtil.Display(25));
        Assert.Equal(c.Length, c.Distinct().Count());                    // 无重复项
    }

    // ── 测量六项 ───────────────────────────────────────────────────────────
    [Fact]
    public void Measure_Radius_ReadsCircleArcPolygon_AndWarnsOtherwise()
    {
        Assert.Contains("请先选中", MeasureOps.Radius(Array.Empty<SceneEntity>()).Text);
        var circle = new CircleEntity { Cx = 0, Cy = 0, Radius = 12.5 };
        Assert.Contains("12.5", MeasureOps.Radius(new SceneEntity[] { circle }).Text);
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0 };
        Assert.Contains("没有半径", MeasureOps.Radius(new SceneEntity[] { line }).Text);
        var poly = new PolygonEntity { Cx = 0, Cy = 0, Radius = 3, Sides = 6 };
        Assert.Contains("共 2 项", MeasureOps.Radius(new SceneEntity[] { circle, poly }).Text);
    }

    [Fact]
    public void Measure_Volume_SumsMeshes_AndIsSignIndependent()
    {
        var box = UnitBox();
        double v = Math.Abs(box.Volume());
        Assert.Equal(1.0, v, 6);                                          // 单位立方体
        Assert.Contains("1", MeasureOps.Volume(new SceneEntity[] { box }).Text);
        var two = MeasureOps.Volume(new SceneEntity[] { box, UnitBox() }).Text;
        Assert.Contains("共 2 个三角网", two);
        Assert.Contains("2", two);
        Assert.Contains("不是三角网", MeasureOps.Volume(new SceneEntity[] { new CircleEntity { Radius = 1 } }).Text);
        Assert.Contains("请先选中", MeasureOps.Volume(Array.Empty<SceneEntity>()).Text);
    }

    [Fact]
    public void Measure_Volume_RepairsInconsistentFaceWindingBeforeIntegrating()
    {
        var box = UnitBox();
        var t = box.Tris[2];
        box.Tris[2] = (t.a, t.c, t.b); // 模拟导入网格中单个面朝向反了

        var result = MeasureOps.Volume(new SceneEntity[] { box });

        Assert.Equal("测量 - 体积：1 m³", result.Text); // 修正朝向后仍应为 1 m³，而不是裸积分的错误值
    }

    [Fact]
    public void Measure_Volume_RejectsOpenMeshInsteadOfReturningFakeVolume()
    {
        var box = UnitBox();
        box.Tris.RemoveRange(2, 2); // 去掉顶面，剩下开口表面没有唯一实体体积

        var result = MeasureOps.Volume(new SceneEntity[] { box });

        Assert.Contains("未闭合", result.Text);
        Assert.Contains("开放边", result.Text);
    }

    [Fact]
    public void Measure_Volume_TranslatesLargeWorldCoordinatesBeforeIntegrating()
    {
        var box = UnitBox();
        for (int i = 0; i < box.Verts.Count; i++)
        {
            var v = box.Verts[i];
            box.Verts[i] = (v.x + 1_000_000_000, v.y + 2_000_000_000, v.z + 3_000_000_000);
        }

        var result = MeasureOps.Volume(new SceneEntity[] { box });

        Assert.Equal("测量 - 体积：1 m³", result.Text);
    }

    [Fact]
    public void Measure_Area_SingleClosedPolylineGivesPerimeterToo_MultiSums()
    {
        var pl = new PolylineEntity { Closed = true };
        pl.Points.AddRange(new[] { (0.0, 0.0), (4.0, 0.0), (4.0, 3.0), (0.0, 3.0) });
        var one = MeasureOps.Area(new SceneEntity[] { pl }).Text;
        Assert.Contains("12", one);            // 面积 4×3
        Assert.Contains("周长", one);
        var rect = new RectEntity { X0 = 0, Y0 = 0, X1 = 2, Y1 = 5 };
        var multi = MeasureOps.Area(new SceneEntity[] { pl, rect }).Text;
        Assert.Contains("共 2 项", multi);
        Assert.Contains("22", multi);          // 12 + 10
        var empty = MeasureOps.Area(Array.Empty<SceneEntity>());
        Assert.True(empty.NeedJig);
        Assert.Contains("指定第一点", empty.Text);
    }

    [Fact]
    public void Measure_DistanceAndAngle_FallBackToJigWhenSelectionInsufficient()
    {
        // 无选中 → 转入鼠标点测(原版进 DIST / MANG jig)
        Assert.True(MeasureOps.Distance(Array.Empty<SceneEntity>()).NeedJig);
        Assert.True(MeasureOps.Angle(Array.Empty<SceneEntity>()).NeedJig);

        var l1 = new LineEntity { X0 = 0, Y0 = 0, X1 = 3, Y1 = 4 };
        var single = MeasureOps.Distance(new SceneEntity[] { l1 });
        Assert.False(single.NeedJig);
        Assert.Contains("5", single.Text);                                 // 3-4-5

        var c1 = new CircleEntity { Cx = 0, Cy = 0, Radius = 1 };
        var c2 = new CircleEntity { Cx = 10, Cy = 0, Radius = 1 };
        Assert.Contains("10", MeasureOps.Distance(new SceneEntity[] { c1, c2 }).Text);

        var l2 = new LineEntity { X0 = 0, Y0 = 0, X1 = 0, Y1 = 7 };
        var ang = MeasureOps.Angle(new SceneEntity[] { l1, l2 });
        Assert.False(ang.NeedJig);
        Assert.Contains("°", ang.Text);
        // 两条实体但都不是直线 → 仍回到点测(同原版"仅支持 2 条 Line")
        Assert.True(MeasureOps.Angle(new SceneEntity[] { c1, c2 }).NeedJig);
    }

    // ── 特性面板: 非单选内容 ───────────────────────────────────────────────
    [Fact]
    public void MultiSelectionRows_TotalFirstThenPerTypeCounts()
    {
        var sel = new SceneEntity[]
        {
            new LineEntity(), new LineEntity(), new LineEntity(),
            new CircleEntity { Radius = 1 },
        };
        var rows = PropertyPanelModel.MultiSelectionRows(sel);
        Assert.Equal(("选择", "总数量", "4"), rows[0]);
        Assert.Equal(("选择", "直线", "3"), rows[1]);                      // 多的类型排前
        Assert.Equal(("选择", "圆", "1"), rows[2]);
    }

    [Fact]
    public void DocumentRows_CoverDocumentAndViewState()
    {
        var rows = PropertyPanelModel.DocumentRows("矿区.pmx", 128, 0, ortho: true, snap: false, is3D: true);
        Assert.Equal(("文档", "名称", "矿区.pmx"), rows[0]);
        Assert.Contains(("视图", "实体总数", "128"), rows);
        Assert.Contains(("视图", "选择数量", "0"), rows);
        Assert.Contains(("视图", "正交模式", "开"), rows);
        Assert.Contains(("视图", "捕捉模式", "关"), rows);
        Assert.Contains(("视图", "3D 视图", "是"), rows);
        Assert.Equal("未命名", PropertyPanelModel.DocumentRows(null, 0, 0, false, false, false)[0].value);
    }

    // ── 圆弧起止角(补齐原版属性面板那两行) ─────────────────────────────────
    [Fact]
    public void ArcProperties_ExposeStartAndEndAngle()
    {
        // 单位圆上 0° → 90° 的弧(经 45°)
        var arc = new ArcEntity { X1 = 1, Y1 = 0, X2 = Math.Sqrt(0.5), Y2 = Math.Sqrt(0.5), X3 = 0, Y3 = 1 };
        var rows = EntityProperties.Describe(arc);
        var start = rows.Single(x => x.label == "起始角度").value;
        var end = rows.Single(x => x.label == "终止角度").value;
        Assert.Equal("0°", start);
        Assert.Equal("90°", end);
        Assert.DoesNotContain("起始角度", EntityProperties.EditableLabels(arc));   // 只读派生行
    }

    private static MeshEntity UnitBox()
    {
        var m = new MeshEntity();
        m.Verts.AddRange(new (double, double, double)[]
        {
            (0,0,0), (1,0,0), (1,1,0), (0,1,0),
            (0,0,1), (1,0,1), (1,1,1), (0,1,1),
        });
        // 6 面 × 2 三角，外法线朝外
        int[][] quads =
        {
            new[]{0,3,2,1}, new[]{4,5,6,7}, new[]{0,1,5,4},
            new[]{1,2,6,5}, new[]{2,3,7,6}, new[]{3,0,4,7},
        };
        foreach (var q in quads) { m.Tris.Add((q[0], q[1], q[2])); m.Tris.Add((q[0], q[2], q[3])); }
        return m;
    }
}
