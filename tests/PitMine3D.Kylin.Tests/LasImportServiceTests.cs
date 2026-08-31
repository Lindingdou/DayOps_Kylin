using System;
using System.IO;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>LAS 点云导入(LasImportService)回归 —— 公开 LAS 1.2 规范, 合成样本验解码 + 真实样本 skip-if-absent。</summary>
public class LasImportServiceTests
{
    // 构造最小合法 LAS 1.2(point format 0, recLen 20)
    private static byte[] MakeLas(params (double x, double y, double z)[] pts)
    {
        double sx = 0.01, sy = 0.01, sz = 0.01, ox = 1000, oy = 2000, oz = 0;
        ushort recLen = 20; uint offsetToPoints = 227;
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[offsetToPoints]);                       // 头占位
        void At(long o, Action w) { ms.Seek(o, SeekOrigin.Begin); w(); }
        At(0, () => bw.Write(new[] { (byte)'L', (byte)'A', (byte)'S', (byte)'F' }));
        At(24, () => { bw.Write((byte)1); bw.Write((byte)2); });  // 版本 1.2
        At(96, () => bw.Write(offsetToPoints));
        At(104, () => { bw.Write((byte)0); bw.Write(recLen); });  // 格式 0, 记录长 20
        At(107, () => bw.Write((uint)pts.Length));
        At(131, () => { bw.Write(sx); bw.Write(sy); bw.Write(sz); bw.Write(ox); bw.Write(oy); bw.Write(oz); });
        double maxX = double.MinValue, minX = double.MaxValue, maxY = double.MinValue, minY = double.MaxValue, maxZ = double.MinValue, minZ = double.MaxValue;
        foreach (var p in pts) { maxX = Math.Max(maxX, p.x); minX = Math.Min(minX, p.x); maxY = Math.Max(maxY, p.y); minY = Math.Min(minY, p.y); maxZ = Math.Max(maxZ, p.z); minZ = Math.Min(minZ, p.z); }
        At(179, () => { bw.Write(maxX); bw.Write(minX); bw.Write(maxY); bw.Write(minY); bw.Write(maxZ); bw.Write(minZ); });
        ms.Seek(offsetToPoints, SeekOrigin.Begin);
        foreach (var p in pts)
        {
            bw.Write((int)Math.Round((p.x - ox) / sx));
            bw.Write((int)Math.Round((p.y - oy) / sy));
            bw.Write((int)Math.Round((p.z - oz) / sz));
            bw.Write(new byte[recLen - 12]);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Synthetic_las_decodes_points_with_scale_offset()
    {
        var bytes = MakeLas((1000.50, 2000.50, 5.00), (1001.00, 2002.00, 10.00), (999.00, 1998.00, -3.00));
        var r = LasImportService.Read(new MemoryStream(bytes));
        Assert.True(r.Success, r.Error);
        Assert.Equal((byte)1, r.VersionMajor);
        Assert.Equal((byte)2, r.VersionMinor);
        Assert.Equal(3, r.PointCount);
        Assert.Equal(3, r.Points.Count);
        // scale+offset 还原坐标
        Assert.Equal(1000.50, r.Points[0].x, 6);
        Assert.Equal(2000.50, r.Points[0].y, 6);
        Assert.Equal(5.00, r.Points[0].z, 6);
        Assert.Equal(-3.00, r.Points[2].z, 6);
        // 头包围盒
        Assert.Equal(1001.00, r.MaxX, 6);
        Assert.Equal(999.00, r.MinX, 6);
    }

    [Fact]
    public void Decimation_caps_point_count()
    {
        var pts = new (double, double, double)[50];
        for (int i = 0; i < 50; i++) pts[i] = (1000 + i, 2000 + i, i);
        var r = LasImportService.Read(new MemoryStream(MakeLas(pts)), maxPoints: 10);
        Assert.Equal(50, r.PointCount);              // 头声明 50
        Assert.True(r.Points.Count <= 10);           // 抽稀到 ≤10
        Assert.True(r.Points.Count >= 5);            // 但确有读入
    }

    [Fact]
    public void Non_las_rejected()
    {
        var junk = new byte[300]; junk[0] = (byte)'X';
        var r = LasImportService.Read(new MemoryStream(junk));
        Assert.False(r.Success);
        Assert.Contains("LAS", r.Error);
    }

    [Fact]
    public void Too_small_rejected()
    {
        Assert.False(LasImportService.Read(new MemoryStream(new byte[10])).Success);
    }

    [Fact]
    public void Real_las_sample_parses()
    {
        string[] cands =
        {
            @"C:\Users\cFore\coder\PitMine3D\测试实验\dlt_test.las",
            @"C:\Users\cFore\coder\AlgoCore\RoadLib\atb.las",
        };
        string? path = null;
        foreach (var c in cands) if (File.Exists(c)) { path = c; break; }
        if (path == null) return;   // 无样本机器(CI)跳过——大二进制夹具不入库
        var r = LasImportService.Load(path!, maxPoints: 20000);
        Assert.True(r.Success, r.Error);
        Assert.Equal((byte)1, r.VersionMajor);
        Assert.True(r.PointCount > 0);
        Assert.NotEmpty(r.Points);
        // 抽稀点应落在头包围盒内(容差, 防 scale/offset 读错成天文坐标)
        foreach (var p in r.Points)
        {
            Assert.InRange(p.x, r.MinX - 1, r.MaxX + 1);
            Assert.InRange(p.y, r.MinY - 1, r.MaxY + 1);
            Assert.InRange(p.z, r.MinZ - 1, r.MaxZ + 1);
        }
    }

    // 构造 LAS 1.2 point format 2(带 RGB, recLen 26, RGB 在记录偏移 20)
    private static byte[] MakeLasRgb(params (double x, double y, double z, ushort r, ushort g, ushort b)[] pts)
    {
        double sx = 0.01, sy = 0.01, sz = 0.01, ox = 1000, oy = 2000, oz = 0;
        ushort recLen = 26; uint offsetToPoints = 227;
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        bw.Write(new byte[offsetToPoints]);
        void At(long o, Action w) { ms.Seek(o, SeekOrigin.Begin); w(); }
        At(0, () => bw.Write(new[] { (byte)'L', (byte)'A', (byte)'S', (byte)'F' }));
        At(24, () => { bw.Write((byte)1); bw.Write((byte)2); });
        At(96, () => bw.Write(offsetToPoints));
        At(104, () => { bw.Write((byte)2); bw.Write(recLen); });   // 格式 2, recLen 26
        At(107, () => bw.Write((uint)pts.Length));
        At(131, () => { bw.Write(sx); bw.Write(sy); bw.Write(sz); bw.Write(ox); bw.Write(oy); bw.Write(oz); });
        At(179, () => { bw.Write(3000.0); bw.Write(900.0); bw.Write(3000.0); bw.Write(1900.0); bw.Write(100.0); bw.Write(-10.0); });
        ms.Seek(offsetToPoints, SeekOrigin.Begin);
        foreach (var p in pts)
        {
            bw.Write((int)Math.Round((p.x - ox) / sx));
            bw.Write((int)Math.Round((p.y - oy) / sy));
            bw.Write((int)Math.Round((p.z - oz) / sz));
            bw.Write((ushort)5000);                                // intensity (偏移 12)
            bw.Write(new byte[6]);                                 // flags..pointsource (偏移 14..19)
            bw.Write(p.r); bw.Write(p.g); bw.Write(p.b);           // RGB 在偏移 20
        }
        return ms.ToArray();
    }

    [Fact]
    public void Format2_reads_real_rgb_colors()
    {
        // 全红(65535,0,0) + 全绿(0,65535,0)
        var bytes = MakeLasRgb((1000.5, 2000.5, 5.0, 65535, 0, 0), (1001.0, 2002.0, 10.0, 0, 65535, 0));
        var r = LasImportService.Read(new MemoryStream(bytes));
        Assert.True(r.Success, r.Error);
        Assert.Equal((byte)2, r.PointFormat);
        Assert.Equal(2, r.Points.Count);
        Assert.NotNull(r.Colors);
        Assert.Equal(2, r.Colors!.Count);                          // Colors 与 Points 同长
        Assert.Equal(1f, r.Colors[0].r, 3); Assert.Equal(0f, r.Colors[0].g, 3);   // 全红 → r=1
        Assert.Equal(1f, r.Colors[1].g, 3); Assert.Equal(0f, r.Colors[1].r, 3);   // 全绿 → g=1
        // 强度(偏移12)也读到, 与 Points 同长
        Assert.Equal(2, r.Intensity.Count);
        Assert.Equal(5000f, r.Intensity[0], 1);
    }

    [Fact]
    public void Format0_has_no_colors()
    {
        var r = LasImportService.Read(new MemoryStream(MakeLas((1000.5, 2000.5, 5.0))));
        Assert.True(r.Success, r.Error);
        Assert.Null(r.Colors);                                     // 格式 0 无 RGB
    }
}
