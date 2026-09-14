using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>逐三角面拾取(删除三角面的悬停/点选核)：2D 取最高层、3D 取深度最前、桶查询与线性扫一致。</summary>
public class MeshFacePickTests
{
    private static MeshEntity Mesh(List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) => new("m", v, t);

    // 两层叠着的正方形(各两三角): 底层 z=0, 顶层 z=10, 同一 XY 投影
    private static MeshEntity TwoLayers()
    {
        var v = new List<(double x, double y, double z)>
        {
            (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0),
            (0, 0, 10), (10, 0, 10), (10, 10, 10), (0, 10, 10),
        };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3), (4, 5, 6), (4, 6, 7) };
        return Mesh(v, t);
    }

    [Fact]
    public void FindAtXY_PicksTopLayer_AndCorrectHalf()
    {
        var pk = new MeshFacePick(TwoLayers());
        Assert.Equal(2, pk.FindAtXY(7, 3));   // 下三角(对角线之下) 的顶层
        Assert.Equal(3, pk.FindAtXY(3, 7));   // 上三角 的顶层
        Assert.Equal(-1, pk.FindAtXY(11, 5)); // 网外
        Assert.Equal(-1, pk.FindAtXY(-1, -1));
    }

    [Fact]
    public void FindAtXY_ElevationDoesNotChangeXYHit()
    {
        var m = TwoLayers(); m.Elevation = 500;
        var pk = new MeshFacePick(m);
        Assert.Equal(2, pk.FindAtXY(7, 3));
        var c = pk.Centroid(2)!.Value;
        Assert.Equal(510, c.z, 9);   // 形心含 Elevation
    }

    [Fact]
    public void FindAtScreen_PicksNearest_AndReprojectsOnlyWhenStampChanges()
    {
        var pk = new MeshFacePick(TwoLayers());
        int calls = 0;
        // 正交"相机": 屏幕 = (x, -y)·10, 深度 = -z(越高越近)
        (double sx, double sy, double depth)? Proj(double x, double y, double z) { calls++; return (x * 10, -y * 10, -z); }
        int hit = pk.FindAtScreen(70, -30, "v1", Proj);
        Assert.Equal(2, hit);
        Assert.Equal(8, calls);   // 8 个顶点各投一次
        Assert.Equal(3, pk.FindAtScreen(30, -70, "v1", Proj));
        Assert.Equal(8, calls);   // 视图戳没变: 不重投
        // 换个相机(深度反过来: 底层在前), 视图戳变了才重投
        (double sx, double sy, double depth)? Proj2(double x, double y, double z) { calls++; return (x * 10, -y * 10, z); }
        Assert.Equal(0, pk.FindAtScreen(70, -30, "v2", Proj2));
        Assert.Equal(16, calls);
        Assert.Equal(-1, pk.FindAtScreen(150, -30, "v2", Proj2));
    }

    [Fact]
    public void FindAtScreen_VerticesBehindCamera_AreSkipped()
    {
        var pk = new MeshFacePick(TwoLayers());
        // 顶层投不出来(相机后方) → 只剩底层可命中
        (double sx, double sy, double depth)? Proj(double x, double y, double z) => z > 5 ? null : (x, y, z);
        Assert.Equal(0, pk.FindAtScreen(7, 3, "s", Proj));
    }

    [Fact]
    public void FacesInRect_WindowNeedsAllVertices_CrossingTouches()
    {
        var pk = new MeshFacePick(TwoLayers());
        // 整个正方形框住: 窗口选到全部 4 面(两层)
        Assert.Equal(new[] { 0, 1, 2, 3 }, pk.FacesInRect(-1, -1, 11, 11, crossing: false));
        // 只框住右下角一块: 窗口一个都不全含 → 空; 交叉 → 碰到的下三角(两层各一)
        Assert.Empty(pk.FacesInRect(6, 1, 9, 4, crossing: false));
        Assert.Equal(new[] { 0, 2 }, pk.FacesInRect(6, 1, 9, 4, crossing: true));
        // 框跨过对角线: 交叉两个三角都碰到
        Assert.Equal(new[] { 0, 1, 2, 3 }, pk.FacesInRect(4, 4, 6, 6, crossing: true));
        // 框在网外
        Assert.Empty(pk.FacesInRect(20, 20, 30, 30, crossing: true));
        // 角点顺序无关(右→左给的框)
        Assert.Equal(new[] { 0, 1, 2, 3 }, pk.FacesInRect(11, 11, -1, -1, crossing: false));
    }

    [Fact]
    public void FacesInScreenRect_UsesProjection()
    {
        var pk = new MeshFacePick(TwoLayers());
        (double sx, double sy, double depth)? Proj(double x, double y, double z) => (x * 10, -y * 10, -z);
        // 屏幕框对应世界 x∈[-1,11], y∈[-1,11]
        Assert.Equal(new[] { 0, 1, 2, 3 }, pk.FacesInScreenRect(-10, 10, 110, -110, false, "s", Proj));
        Assert.Equal(new[] { 0, 2 }, pk.FacesInScreenRect(60, -10, 90, -40, true, "s", Proj));
    }

    [Fact]
    public void Area_IsTrue3DArea()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (3, 0, 0), (0, 0, 4) };
        var pk = new MeshFacePick(Mesh(v, new List<(int a, int b, int c)> { (0, 1, 2) }));
        Assert.Equal(6, pk.Area(0), 9);   // 3-4 直角边, 竖着的三角
        Assert.Equal(0, pk.Area(5));
    }

    [Fact]
    public void Buckets_AgreeWithLinearScan_OnRandomTerrain()
    {
        // 40×40 格网地形, 桶查询结果须与逐三角线性扫(取最高 z)一致
        var rnd = new Random(7);
        int n = 40;
        var v = new List<(double x, double y, double z)>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) v.Add((i * 2.5, j * 2.5, rnd.NextDouble() * 20));
        var t = new List<(int a, int b, int c)>();
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
            t.Add((a, b, d)); t.Add((a, d, c));
        }
        var m = Mesh(v, t); var pk = new MeshFacePick(m);
        for (int k = 0; k < 300; k++)
        {
            double x = rnd.NextDouble() * 110 - 5, y = rnd.NextDouble() * 110 - 5;
            int expect = -1; double bestZ = double.NegativeInfinity;
            for (int ti = 0; ti < t.Count; ti++)
            {
                var (a, b, c) = t[ti]; var p = v[a]; var q = v[b]; var r = v[c];
                double det = (q.y - r.y) * (p.x - r.x) + (r.x - q.x) * (p.y - r.y);
                double w0 = ((q.y - r.y) * (x - r.x) + (r.x - q.x) * (y - r.y)) / det;
                double w1 = ((r.y - p.y) * (x - r.x) + (p.x - r.x) * (y - r.y)) / det;
                double w2 = 1 - w0 - w1;
                if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
                double z = w0 * p.z + w1 * q.z + w2 * r.z;
                if (z > bestZ) { bestZ = z; expect = ti; }
            }
            Assert.Equal(expect, pk.FindAtXY(x, y));
        }
    }
}
