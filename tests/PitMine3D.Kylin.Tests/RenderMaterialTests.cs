using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「渲染配置」后三页（材质 / 贴图 / 透明度）的托管侧：谁走材质通道、法线怎么带、批怎么分。
/// 逐片元的 PBR/三平面采样在 GPU 上（GlRenderer 的材质程序，公式照抄原 Lit.hlsl），这里测的是
/// 送进 GPU 之前的那一半 —— 档位判定、原色不预乘光照、法线随顶点、按材质参数合批、不重复画。
/// </summary>
[Collection("MeshRenderMode")]
public class RenderMaterialTests
{
    private static MeshEntity Quad()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 10), (0, 10, 10) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return new MeshEntity("m", v, t) { Cr = 0.8f, Cg = 0.6f, Cb = 0.4f };
    }

    private static void WithShadeState(Action body)
    {
        var saved = (MeshEntity.RenderMode, MeshEntity.ShadeMode, MeshEntity.SmoothShading,
                     MeshEntity.PbrMetallic, MeshEntity.PbrRoughness, MeshEntity.TexScale);
        try { body(); }
        finally
        {
            (MeshEntity.RenderMode, MeshEntity.ShadeMode, MeshEntity.SmoothShading,
             MeshEntity.PbrMetallic, MeshEntity.PbrRoughness, MeshEntity.TexScale) = saved;
            MeshEntity.BumpShade();
        }
    }

    // ── 透明度：直接由既有的「透明度」特性来（-1 随层 / 0 不透明 / 1..90 百分比）──
    [Theory]
    [InlineData((short)-1, 1f)]
    [InlineData((short)0, 1f)]
    [InlineData((short)55, 0.45f)]
    [InlineData((short)90, 0.10f)]
    public void EffAlpha_FromTransparencyProperty(short t, float expected)
    {
        var m = Quad();
        m.Transparency = t;
        Assert.Equal(expected, m.EffAlpha, 3);
    }

    [Fact]
    public void UsesMaterialPass_OnlyWhenTexturedPbrOrTranslucent()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Entity;
            var m = Quad();
            Assert.False(m.UsesMaterialPass);           // 普通不透明面走便宜的 P3_C3 通道

            m.Transparency = 40;
            Assert.True(m.UsesMaterialPass);            // 半透要混合 → 材质通道
            Assert.Equal(0, m.MaterialMode);            // 但档还是"顶点色", 只叠 alpha

            m.Transparency = 0;
            m.FaceRender = new MeshEntity.FaceRenderOverride(
                MeshEntity.FaceShade.Pbr, true, 0, 0, PitMine3D.Kylin.Cad.Colormap.Viridis, false);
            Assert.True(m.UsesMaterialPass);
            Assert.Equal(6, m.MaterialMode);

            m.FaceRender = new MeshEntity.FaceRenderOverride(
                MeshEntity.FaceShade.Textured, true, 0, 0, PitMine3D.Kylin.Cad.Colormap.Viridis, false);
            Assert.Equal(4, m.MaterialMode);

            // 线框档没有面, 材质通道也不该有它
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Wireframe;
            Assert.False(m.UsesMaterialPass);
        });
    }

    /// <summary>同一张网不能两个通道各画一遍 —— 画两遍的话半透会被"叠实"。</summary>
    [Fact]
    public void MaterialMesh_IsSkippedByPlainFacePass()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Entity;
            var m = Quad();
            m.Transparency = 50;

            var plain = new List<float>(); m.TessellateFaces(plain);
            Assert.Empty(plain);

            var mat = new List<float>(); m.TessellateFacesMat(mat);
            Assert.Equal(2 * 3 * 9, mat.Count);   // 2 三角 × 3 顶点 × (位置3 + 色3 + 法线3)
        });
    }

    [Fact]
    public void MaterialStream_CarriesUnitNormals()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Pbr;
            var m = Quad();
            var o = new List<float>(); m.TessellateFacesMat(o);
            Assert.NotEmpty(o);
            for (int i = 0; i < o.Count; i += 9)
            {
                double nx = o[i + 6], ny = o[i + 7], nz = o[i + 8];
                Assert.Equal(1.0, Math.Sqrt(nx * nx + ny * ny + nz * nz), 3);
            }
        });
    }

    /// <summary>
    /// 贴图/PBR 档下顶点色必须是**原色**：光交给 shader 打，CPU 再乘一次 lambert 就是"上了两遍釉"。
    /// </summary>
    [Fact]
    public void TexturedAndPbr_EmitRawAlbedo_PlainDoesNot()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;

            MeshEntity.ShadeMode = MeshEntity.FaceShade.Pbr; MeshEntity.BumpShade();
            var pbr = new List<float>(); Quad().TessellateFacesMat(pbr);
            Assert.Equal(0.8f, pbr[3], 3); Assert.Equal(0.6f, pbr[4], 3); Assert.Equal(0.4f, pbr[5], 3);

            MeshEntity.ShadeMode = MeshEntity.FaceShade.Textured; MeshEntity.BumpShade();
            var tex = new List<float>(); Quad().TessellateFacesMat(tex);
            Assert.Equal(0.8f, tex[3], 3);

            // 只为透明走材质通道的那种: 顶点色仍是 CPU 打好光的(k<1), 不是原色
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Entity; MeshEntity.BumpShade();
            var m = Quad(); m.Transparency = 50;
            var lit = new List<float>(); m.TessellateFacesMat(lit);
            Assert.True(lit[3] < 0.8f, $"普通半透面该带光照, 实际 {lit[3]}");
        });
    }

    [Fact]
    public void EffMaterial_FallsBackToGlobals_AndClamps()
    {
        WithShadeState(() =>
        {
            MeshEntity.PbrMetallic = 0.3; MeshEntity.PbrRoughness = 0.7;
            var m = Quad();
            Assert.Equal(0.3, m.EffMetallic, 3);
            Assert.Equal(0.7, m.EffRoughness, 3);

            m.Metallic = 5; m.Roughness = 0;      // 越界值按 shader 的 clamp 口径夹住
            Assert.Equal(1.0, m.EffMetallic, 3);
            Assert.Equal(0.04, m.EffRoughness, 3);
        });
    }

    // ── 合批：材质参数是 uniform, 只有同参数的网能合到一块缓冲 ──
    [Fact]
    public void BuildMaterialFaces_GroupsByParams_OpaqueBeforeTranslucent()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Pbr;
            MeshEntity.PbrMetallic = 0.2; MeshEntity.PbrRoughness = 0.6;
            MeshEntity.BumpShade();

            var scene = new Scene();
            var a = Quad(); var b = Quad();          // 同参数 → 一批
            var c = Quad(); c.Metallic = 0.9;        // 不同金属度 → 另一批
            var d = Quad(); d.Transparency = 60;     // 半透 → 又一批
            foreach (var e in new[] { a, b, c, d }) scene.Add(e);

            var batches = scene.BuildMaterialFaces();
            Assert.Equal(3, batches.Count);
            Assert.Equal(1f, batches[0].Alpha, 3);                       // 不透明在前
            Assert.Equal(0.4f, batches[^1].Alpha, 3);                    // 半透在后
            Assert.All(batches, x => Assert.Equal(0, x.Verts.Length % 9));
            // a+b 合成一批 = 两张网的顶点数之和
            var merged = batches.First(x => Math.Abs(x.Metallic - 0.2f) < 1e-3 && x.Alpha > 0.99f);
            Assert.Equal(2 * 2 * 3 * 9, merged.Verts.Length);

            // 走了材质通道的网, 普通面通道里一个顶点都不该有
            Assert.Empty(scene.BuildFaces());
        });
    }

    /// <summary>不透明的普通网仍走普通通道，材质通道为空 —— 别让所有网都被拖去多带一条法线。</summary>
    [Fact]
    public void PlainScene_HasNoMaterialBatches()
    {
        WithShadeState(() =>
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            MeshEntity.ShadeMode = MeshEntity.FaceShade.Entity;
            MeshEntity.BumpShade();
            var scene = new Scene();
            scene.Add(Quad());
            Assert.Empty(scene.BuildMaterialFaces());
            Assert.NotEmpty(scene.BuildFaces());
        });
    }
}
