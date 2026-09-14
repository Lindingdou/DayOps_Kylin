using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「渲染配置」的着色管线：坡度/坡向色带口径、属性分级色带映射、等高线几何、逐对象独立着色、
/// 平面-平滑之别，以及改完参数缓存要失效。
/// 色带与打光的数值口径逐档对着原 <c>Kernel/xllAcGi/shader/Lit.hlsl</c>（mode 11/12/14/10）写死，
/// 改这些常数前先回去看那份 shader —— 它才是原版屏幕上真正显示的东西。
/// </summary>
[Collection("MeshRenderMode")]
public class RenderConfigShadingTests
{
    /// <summary>一张 z 从 0 爬到 10 的斜面（两个三角）。</summary>
    private static MeshEntity Ramp10()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 10), (0, 10, 10) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return new MeshEntity("m", v, t) { Cr = 1f, Cg = 0.5f, Cb = 0.25f };
    }

    /// <summary>跑一段用例，跑完把所有全局着色状态放回去（这些是 static，串了别的用例就是偶发失败）。</summary>
    private static void WithShadeState(Action body)
    {
        var saved = (MeshEntity.RenderMode, MeshEntity.ShadeMode, MeshEntity.SmoothShading, MeshEntity.ContourSpacing,
                     MeshEntity.AttrAutoRange, MeshEntity.AttrMin, MeshEntity.AttrMax,
                     MeshEntity.AttrColormap, MeshEntity.AttrReverse);
        try { body(); }
        finally
        {
            (MeshEntity.RenderMode, MeshEntity.ShadeMode, MeshEntity.SmoothShading, MeshEntity.ContourSpacing,
             MeshEntity.AttrAutoRange, MeshEntity.AttrMin, MeshEntity.AttrMax,
             MeshEntity.AttrColormap, MeshEntity.AttrReverse) = saved;
            MeshEntity.BumpShade();
        }
    }

    // ── 坡度色带：0°蓝 → 22.5°绿 → 45°黄 → 67.5°橙 → 90°红（Lit.hlsl mode 11）──
    [Theory]
    [InlineData(0.0, 0.10f, 0.30f, 1.00f)]
    [InlineData(22.5, 0.10f, 0.85f, 0.20f)]
    [InlineData(45.0, 1.00f, 0.95f, 0.10f)]
    [InlineData(67.5, 1.00f, 0.55f, 0.10f)]
    [InlineData(90.0, 0.90f, 0.10f, 0.10f)]
    public void SlopeRamp_AnchorsMatchShader(double deg, float r, float g, float b)
    {
        var c = MeshEntity.SlopeRamp(deg);
        Assert.Equal(r, c.r, 3); Assert.Equal(g, c.g, 3); Assert.Equal(b, c.b, 3);
    }

    /// <summary>旧色带 0~70° 就到顶，70° 与 85° 的陡帮同一个红；五档色带必须分得开。</summary>
    [Fact]
    public void SlopeRamp_SteepFacesStillSeparable()
    {
        var a = MeshEntity.SlopeRamp(70);
        var b = MeshEntity.SlopeRamp(85);
        Assert.True(Math.Abs(a.g - b.g) > 0.05f, $"70°/85° 分不开: {a} vs {b}");
    }

    // ── 坡向色带：色环 + 平坦淡入灰（Lit.hlsl mode 12）──
    [Fact]
    public void AspectColor_FlatFadesToGray_AndOppositeFacingsDiffer()
    {
        var flat = MeshEntity.AspectColor(0, 0, 1);
        Assert.Equal(0.70f, flat.r, 2); Assert.Equal(0.70f, flat.g, 2); Assert.Equal(0.70f, flat.b, 2);

        var east = MeshEntity.AspectColor(1, 0, 0);
        var west = MeshEntity.AspectColor(-1, 0, 0);
        Assert.True(Math.Abs(east.r - west.r) + Math.Abs(east.g - west.g) + Math.Abs(east.b - west.b) > 0.3f);
    }

    // ── 属性分级：Z → 区间 → 色带（Lit.hlsl mode 14）──
    [Fact]
    public void AttributeColor_MapsRange_ClampsOutside_AndReverses()
    {
        var stops = Colormap.Viridis;
        var lo = MeshEntity.AttributeColor(100, 100, 200, stops, false);
        var hi = MeshEntity.AttributeColor(200, 100, 200, stops, false);
        Assert.Equal(stops[0].r / 255f, lo.r, 3);
        Assert.Equal(stops[^1].r / 255f, hi.r, 3);

        // 区间外夹到端点（原版 saturate）
        Assert.Equal(lo, MeshEntity.AttributeColor(-999, 100, 200, stops, false));
        Assert.Equal(hi, MeshEntity.AttributeColor(9999, 100, 200, stops, false));

        // 反转：低值取到色带右端
        var loRev = MeshEntity.AttributeColor(100, 100, 200, stops, true);
        Assert.Equal(hi, loRev);
    }

    /// <summary>区间退化（全网同高）不能出 NaN —— shader 侧是 max(range, 0.001)，这里同口径。</summary>
    [Fact]
    public void AttributeColor_DegenerateRange_NoNaN()
    {
        var c = MeshEntity.AttributeColor(50, 50, 50, Colormap.Jet, false);
        Assert.False(float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b));
    }

    // ── 等高线档：面照常打光，边线通道多出整数倍等高距的等值线 ──
    [Fact]
    public void Contour_EmitsIsoLinesAtSpacingMultiples()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Contour;
            MeshEntity.ContourSpacing = 5;
            MeshEntity.BumpShade();

            var m = Ramp10();
            var e = new List<float>(); m.Tessellate(e);
            Assert.True(e.Count > 0, "等高线档在纯着色面模式下也必须出线, 否则等高线永远看不见");
            Assert.Equal(0, e.Count % 12);                       // 每段两点 × 6 float
            for (int i = 0; i < e.Count; i += 6)
            {
                double z = e[i + 2];
                Assert.Equal(0.0, z % 5.0, 3);                   // 落在 0/5/10 层位上
                Assert.Equal(0.08f, e[i + 3], 3);                // 深色等值线(同 shader contourColor)
            }

            // 等高距减半 → 层位变密：缓存必须跟着 ShadeEpoch 失效, 否则"设了不生效"
            int coarse = e.Count;
            MeshEntity.ContourSpacing = 2.5; MeshEntity.BumpShade();
            var e2 = new List<float>(); m.Tessellate(e2);
            Assert.True(e2.Count > coarse, $"等高距减半后线该变多: {coarse} → {e2.Count}");
        });
    }

    /// <summary>面+线框叠等高线时，网格边线仍要在（等高线是加出来的，不是顶掉的）。</summary>
    [Fact]
    public void Contour_KeepsWireframeEdgesInShadedWireframe()
    {
        WithShadeState(() =>
        {
            var m = Ramp10();
            MeshEntity.RenderMode = MeshEntity.DisplayMode.ShadedWireframe;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Entity; MeshEntity.BumpShade();
            var plain = new List<float>(); m.Tessellate(plain);

            MeshEntity.ShadeMode = MeshEntity.FaceShade.Contour; MeshEntity.ContourSpacing = 5; MeshEntity.BumpShade();
            var withContour = new List<float>(); m.Tessellate(withContour);
            Assert.True(withContour.Count > plain.Count);
        });
    }

    // ── 逐对象独立着色：选中的那张换档，别的不动 ──
    [Fact]
    public void FaceRenderOverride_OnlyAffectsThatMesh()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Entity; MeshEntity.BumpShade();

            var a = Ramp10(); var b = Ramp10();
            var baseline = new List<float>(); a.TessellateFaces(baseline);

            b.FaceRender = new MeshEntity.FaceRenderOverride(
                MeshEntity.FaceShade.Slope, true, 0, 0, Colormap.Viridis, false);
            var over = new List<float>(); b.TessellateFaces(over);
            Assert.Equal(baseline.Count, over.Count);
            Assert.False(baseline.SequenceEqual(over), "逐对象覆盖没生效");

            // 未覆盖的那张仍跟随全局
            var again = new List<float>(); a.TessellateFaces(again);
            Assert.True(baseline.SequenceEqual(again));

            // 清除覆盖 → 回到与全局一致
            b.FaceRender = null;
            var cleared = new List<float>(); b.TessellateFaces(cleared);
            Assert.True(baseline.SequenceEqual(cleared));
        });
    }

    /// <summary>属性分级的区间改了要立刻反映到颜色上（缓存键带 ShadeEpoch 的意义）。</summary>
    [Fact]
    public void AttributeShade_RangeChangeInvalidatesCache()
    {
        WithShadeState(() =>
        {
            var m = Ramp10();
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Attribute;
            MeshEntity.AttrColormap = Colormap.Viridis; MeshEntity.AttrReverse = false;
            MeshEntity.AttrAutoRange = false; MeshEntity.AttrMin = 0; MeshEntity.AttrMax = 10;
            MeshEntity.BumpShade();
            var narrow = new List<float>(); m.TessellateFaces(narrow);

            MeshEntity.AttrMin = -1000; MeshEntity.AttrMax = 1000; MeshEntity.BumpShade();
            var wide = new List<float>(); m.TessellateFaces(wide);
            Assert.False(narrow.SequenceEqual(wide), "改完色带区间还拿旧缓存 —— 用户看到的就是「设了不生效」");

            // 反转色带同样要即时生效
            MeshEntity.AttrMin = 0; MeshEntity.AttrMax = 10; MeshEntity.AttrReverse = true; MeshEntity.BumpShade();
            var reversed = new List<float>(); m.TessellateFaces(reversed);
            Assert.False(narrow.SequenceEqual(reversed));
        });
    }

    /// <summary>自动区间 = 每张网按自身高程铺满色带：两张高程不同的网，各自都要铺到色带两端。</summary>
    [Fact]
    public void AttributeShade_AutoRange_IsPerMesh()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Attribute;
            MeshEntity.AttrColormap = Colormap.Grayscale; MeshEntity.AttrReverse = false;
            MeshEntity.AttrAutoRange = true; MeshEntity.BumpShade();

            var low = Ramp10();                       // z 0..10
            var high = Ramp10(); high.Elevation = 1000;   // 同一张网抬到 1000 m

            static (float lo, float hi) GrayRange(MeshEntity m)
            {
                var o = new List<float>(); m.TessellateFaces(o);
                float lo = 1f, hi = 0f;
                for (int i = 0; i < o.Count; i += 6) { lo = Math.Min(lo, o[i + 3]); hi = Math.Max(hi, o[i + 3]); }
                return (lo, hi);
            }
            var a = GrayRange(low); var b = GrayRange(high);
            Assert.True(a.hi - a.lo > 0.3f, "自动区间下单张网该铺满色带");
            Assert.Equal(a.lo, b.lo, 2);   // 抬高 1000 m 后仍各自铺满, 不是整片钳到一端
            Assert.Equal(a.hi, b.hi, 2);
        });
    }

    /// <summary>平面着色恒用面法线，平滑着色才认顶点法线 —— 原版 Flat(0)/Smooth(1) 的唯一分别。</summary>
    [Fact]
    public void SmoothShading_TogglesVertexNormalLighting()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Entity;

            var m = Ramp10();
            m.VertNormals = new List<(double x, double y, double z)>
            { (0, 0, 1), (1, 0, 0), (0, 1, 0), (0, 0, -1) };

            MeshEntity.SmoothShading = false; MeshEntity.BumpShade();
            var flat = new List<float>(); m.TessellateFaces(flat);
            // 面法线：同一个三角三个顶点亮度相同
            Assert.Equal(flat[3], flat[9], 4);

            MeshEntity.SmoothShading = true; MeshEntity.BumpShade();
            var smooth = new List<float>(); m.TessellateFaces(smooth);
            Assert.False(flat.SequenceEqual(smooth), "平滑着色没认顶点法线");
        });
    }
}
