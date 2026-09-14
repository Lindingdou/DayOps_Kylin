using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 格网导出的三个选项（§三二三，对应原版「导出选项」组）：导出法线 / 翻转 Y/Z 轴 / 缩放因子。
/// 口径按原版：**缩放只乘位置、不乘法线**；翻转 Y/Z 位置与法线**都**翻
/// （只翻位置的话法线整体指错，导到 Y-up 的浏览器里全是背光面）。
/// </summary>
public class MeshExportOptionsTests
{
    // 一张躺在 z=2 平面上的方片，法线朝 +Z
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) FlatQuad()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 2), (4, 0, 2), (4, 3, 2), (0, 3, 2) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };   // 逆时针 → 法线 +Z
        return (v, t);
    }

    private static double[] Nums(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    // ── 逐顶点法线 ──────────────────────────────────────────
    [Fact]
    public void 顶点法线_平面网全是单位正Z()
    {
        var (v, t) = FlatQuad();
        var n = MeshExport.VertexNormals(v, t);
        Assert.Equal(v.Count, n.Count);
        foreach (var q in n)
        {
            Assert.Equal(0, q.x, 9);
            Assert.Equal(0, q.y, 9);
            Assert.Equal(1, q.z, 9);
        }
    }

    [Fact]
    public void 顶点法线_孤立点给正Z而不是NaN()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0), (9, 9, 9) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2) };
        var n = MeshExport.VertexNormals(v, t);
        Assert.False(double.IsNaN(n[3].x) || double.IsNaN(n[3].y) || double.IsNaN(n[3].z));
        Assert.Equal(1, n[3].z, 9);
    }

    // ── 导出法线 ────────────────────────────────────────────
    [Fact]
    public void OBJ_不开法线时没有vn且面是三个裸索引()
    {
        var (v, t) = FlatQuad();
        var s = MeshExport.ToObj(v, t);
        Assert.DoesNotContain("vn ", s);
        Assert.Contains("f 1 2 3", s);
        Assert.DoesNotContain("//", s);
    }

    [Fact]
    public void OBJ_开法线时出vn且面写成双斜杠()
    {
        var (v, t) = FlatQuad();
        var s = MeshExport.ToObj(v, t, new MeshExport.Options { Normals = true });
        var vn = s.Split('\n').Where(l => l.StartsWith("vn ")).ToList();
        Assert.Equal(v.Count, vn.Count);                  // 逐顶点法线, 与顶点一一对应
        Assert.Contains("f 1//1 2//2 3//3", s);
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, Nums(vn[0]).Select(x => Math.Round(x, 9)).ToArray());
    }

    [Fact]
    public void PLY_开法线时头里多三条属性且每行多三个数()
    {
        var (v, t) = FlatQuad();
        var off = MeshExport.ToPly(v, t);
        var on = MeshExport.ToPly(v, t, new MeshExport.Options { Normals = true });
        Assert.DoesNotContain("property float nx", off);
        Assert.Contains("property float nx", on);
        Assert.Contains("property float ny", on);
        Assert.Contains("property float nz", on);

        string firstVert = on.Split('\n').SkipWhile(l => !l.StartsWith("end_header")).Skip(1).First();
        Assert.Equal(6, firstVert.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // ── 缩放因子 ────────────────────────────────────────────
    [Fact]
    public void 缩放只乘位置不乘法线()
    {
        var (v, t) = FlatQuad();
        var s = MeshExport.ToObj(v, t, new MeshExport.Options { Normals = true, Scale = 10 });
        var lines = s.Split('\n');
        var firstV = Nums(lines.First(l => l.StartsWith("v ")));
        var firstN = Nums(lines.First(l => l.StartsWith("vn ")));
        Assert.Equal(new[] { 0.0, 0.0, 20.0 }, firstV);              // (0,0,2) × 10
        Assert.Equal(1.0, Math.Round(firstN[2], 9));                 // 法线仍是单位, 没被乘成 10
        Assert.Equal(1.0, Math.Sqrt(firstN.Sum(x => x * x)), 9);
    }

    [Fact]
    public void 缩放对PLY和OFF同样生效()
    {
        var (v, t) = FlatQuad();
        var o = new MeshExport.Options { Scale = 0.5 };
        string plyVert = MeshExport.ToPly(v, t, o).Split('\n').SkipWhile(l => !l.StartsWith("end_header")).Skip(1).First();
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, plyVert.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray());

        string offVert = MeshExport.ToOff(v, t, o).Split('\n')[2];   // OFF / 计数行 / 第一个顶点
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, offVert.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray());
    }

    // ── 翻转 Y/Z ────────────────────────────────────────────
    [Fact]
    public void 翻转YZ_位置的Y与Z对调()
    {
        var (v, t) = FlatQuad();
        var s = MeshExport.ToObj(v, t, new MeshExport.Options { FlipYZ = true });
        var verts = s.Split('\n').Where(l => l.StartsWith("v ")).Select(Nums).ToList();
        // 原 (4,3,2) → 翻成 (4,2,3)
        Assert.Contains(verts, q => q[0] == 4 && q[1] == 2 && q[2] == 3);
    }

    [Fact]
    public void 翻转YZ_法线跟着翻而不是留在原轴()
    {
        // 只翻位置不翻法线是最容易漏的一步: 网翻到 Y-up 了, 法线还指着旧的 +Z, 到查看器里整片背光
        var (v, t) = FlatQuad();
        var s = MeshExport.ToObj(v, t, new MeshExport.Options { Normals = true, FlipYZ = true });
        var n = Nums(s.Split('\n').First(l => l.StartsWith("vn ")));
        Assert.Equal(0, Math.Round(n[0], 9));
        Assert.Equal(1, Math.Round(n[1], 9));   // 原本的 +Z 现在落在 Y 上
        Assert.Equal(0, Math.Round(n[2], 9));
    }

    [Fact]
    public void 翻转YZ_倒绕向后三种格式的法线指同一侧()
    {
        // 翻 Y/Z 是反射(行列式 -1)，会把绕向翻过来。倒一次面的顶点顺序后，几何法线才与
        // 逐顶点法线(轴换过的)指同一侧 —— 不倒的话 OBJ 说 +Y、STL 说 -Y，同一份网自己跟自己打架。
        var (v, t) = FlatQuad();
        var o = new MeshExport.Options { Normals = true, FlipYZ = true };

        var vn = Nums(MeshExport.ToObj(v, t, o).Split('\n').First(l => l.StartsWith("vn ")));
        var facet = MeshExport.ToStlAscii(v, t, o).Split('\n').First(l => l.TrimStart().StartsWith("facet normal"));
        var fn = facet.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(2)
                      .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();

        Assert.Equal(1.0, Math.Sqrt(fn.Sum(x => x * x)), 6);
        Assert.Equal(1, Math.Round(vn[1], 6));    // 原 +Z 落到 +Y
        Assert.Equal(1, Math.Round(fn[1], 6));    // STL 的面法向同侧，不是 -Y
        Assert.Equal(0, Math.Round(fn[2], 6));
    }

    [Fact]
    public void 翻转YZ_面的顶点顺序倒过来()
    {
        var (v, t) = FlatQuad();
        Assert.Contains("f 1 2 3", MeshExport.ToObj(v, t));                                            // 不翻: 原序
        Assert.Contains("f 1 3 2", MeshExport.ToObj(v, t, new MeshExport.Options { FlipYZ = true }));   // 翻: 倒序
        Assert.Contains("3 0 2 1", MeshExport.ToPly(v, t, new MeshExport.Options { FlipYZ = true }));
        Assert.Contains("3 0 2 1", MeshExport.ToOff(v, t, new MeshExport.Options { FlipYZ = true }));
    }

    // ── 分发 ────────────────────────────────────────────────
    [Fact]
    public void 按扩展名分发_四档都认且选项透传()
    {
        var (v, t) = FlatQuad();
        var o = new MeshExport.Options { Normals = true };
        Assert.StartsWith("# PitMine3D.Kylin OBJ", MeshExport.ByExtension("obj", v, t, o));
        Assert.StartsWith("ply", MeshExport.ByExtension(".ply", v, t, o));
        Assert.StartsWith("solid", MeshExport.ByExtension("STL", v, t, o));
        Assert.StartsWith("OFF", MeshExport.ByExtension(".off", v, t, o));
        Assert.Contains("property float nx", MeshExport.ByExtension("ply", v, t, o));   // 选项确实传下去了
        Assert.StartsWith("# PitMine3D.Kylin OBJ", MeshExport.ByExtension("不认识的", v, t, o));   // 未知回落 OBJ
    }

    [Fact]
    public void 默认选项与旧调用等价()
    {
        // 老调用点(不传 opt)必须与"全默认选项"逐字一致 —— 补选项不该悄悄改变既有导出结果
        var (v, t) = FlatQuad();
        Assert.Equal(MeshExport.ToObj(v, t), MeshExport.ToObj(v, t, new MeshExport.Options()));
        Assert.Equal(MeshExport.ToPly(v, t), MeshExport.ToPly(v, t, new MeshExport.Options()));
        Assert.Equal(MeshExport.ToStlAscii(v, t), MeshExport.ToStlAscii(v, t, new MeshExport.Options()));
    }
}
