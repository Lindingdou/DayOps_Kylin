using System;
using System.Collections.Generic;
using System.IO;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>GeoTIFF 正射采样(GeoTiffSampler)回归 —— 地理配准纯数学 + 合成/真实无压缩 RGB 采样。</summary>
public class GeoTiffSamplerTests
{
    [Fact]
    public void GeoTransform_pixel_world_roundtrip()
    {
        var g = new GeoTransform(2.0, 2.0, 0, 0, 100, 200);   // 像素(0,0)=世界(100,200), 每像素 2 单位
        Assert.Equal((100.0, 200.0), g.PixelToWorld(0, 0));
        Assert.Equal((110.0, 190.0), g.PixelToWorld(5, 5));   // 世界 Y 随行减
        var (col, row) = g.WorldToPixel(110, 190);
        Assert.Equal(5, col, 6); Assert.Equal(5, row, 6);
    }

    // 合成 2×2 无压缩 RGB GeoTIFF: (0,0)红 (1,0)绿 (0,1)蓝 (1,1)白; 像素(0,0)=世界(100,200), 每像素1
    private static byte[] MakeGeoTiff()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(new byte[] { (byte)'I', (byte)'I' }); w.Write((ushort)42); w.Write((uint)8);   // 头, IFD@8
        // 10 个标签
        void Entry(ushort tag, ushort type, uint count, uint val)
        { w.Write(tag); w.Write(type); w.Write(count); w.Write(val); }
        const uint pixOff = 134, scaleOff = 146, tieOff = 170;
        w.Write((ushort)10);                       // 标签数
        Entry(256, 3, 1, 2);                       // Width=2
        Entry(257, 3, 1, 2);                       // Height=2
        Entry(259, 3, 1, 1);                       // Compression=none
        Entry(273, 4, 1, pixOff);                  // StripOffsets
        Entry(277, 3, 1, 3);                       // SamplesPerPixel=3
        Entry(278, 3, 1, 2);                       // RowsPerStrip=2
        Entry(279, 4, 1, 12);                      // StripByteCounts
        Entry(284, 3, 1, 1);                       // Planar=chunky
        Entry(33550, 12, 3, scaleOff);             // ModelPixelScale
        Entry(33922, 12, 6, tieOff);               // ModelTiepoint
        w.Write((uint)0);                          // next IFD = 0
        // 像素数据 @134 (chunky RGB)
        w.Write(new byte[] { 255, 0, 0, 0, 255, 0, /*row0: 红 绿*/ 0, 0, 255, 255, 255, 255 /*row1: 蓝 白*/ });
        // PixelScale @146 (sx,sy,sz)=(1,1,0)
        w.Write(1.0); w.Write(1.0); w.Write(0.0);
        // Tiepoint @170 (i,j,k,X,Y,Z)=(0,0,0,100,200,0)
        w.Write(0.0); w.Write(0.0); w.Write(0.0); w.Write(100.0); w.Write(200.0); w.Write(0.0);
        return ms.ToArray();
    }

    [Fact]
    public void Synthetic_geotiff_samples_known_pixels()
    {
        var g = new GeoTiffSampler();
        g.Init(new MemoryStream(MakeGeoTiff()));
        Assert.True(g.Success, g.Error);
        Assert.Equal(2, g.Width); Assert.Equal(2, g.Height); Assert.Equal(3, g.Samples);
        Assert.Equal((byte)255, g.SampleRgb(100, 200)!.Value.r);   // 像素(0,0)=红
        Assert.Equal((byte)0, g.SampleRgb(100, 200)!.Value.g);
        var green = g.SampleRgb(101, 200)!.Value; Assert.Equal((byte)255, green.g); Assert.Equal((byte)0, green.r);  // (1,0)绿
        var blue = g.SampleRgb(100, 199)!.Value; Assert.Equal((byte)255, blue.b);   // (0,1)蓝
        var white = g.SampleRgb(101, 199)!.Value; Assert.Equal((byte)255, white.r); Assert.Equal((byte)255, white.b);  // (1,1)白
    }

    [Fact]
    public void Sample_outside_returns_null()
    {
        var g = new GeoTiffSampler();
        g.Init(new MemoryStream(MakeGeoTiff()));
        Assert.Null(g.SampleRgb(100, 201));    // row=-1
        Assert.Null(g.SampleRgb(103, 200));    // col=3(>1)
    }

    [Fact]
    public void Bounds_from_geo_referencing()
    {
        var g = new GeoTiffSampler();
        g.Init(new MemoryStream(MakeGeoTiff()));
        Assert.Equal(100, g.MinX, 6); Assert.Equal(102, g.MaxX, 6);   // 2 像素宽 ×1
        Assert.Equal(198, g.MinY, 6); Assert.Equal(200, g.MaxY, 6);   // 顶 200, 底 200-2
    }

    // 4×4 压缩 GeoTIFF: PIL 压缩的 [红绿蓝白]×4 条带 + 地理配准(像素(0,0)=世界(100,200), 每像素1)
    private static byte[] MakeCompressedGeoTiff(ushort compression, byte[] strip)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(new byte[] { (byte)'I', (byte)'I' }); w.Write((ushort)42); w.Write((uint)8);
        void Entry(ushort tag, ushort type, uint count, uint val) { w.Write(tag); w.Write(type); w.Write(count); w.Write(val); }
        uint stripOff = 134, scaleOff = (uint)(134 + strip.Length), tieOff = scaleOff + 24;
        w.Write((ushort)10);
        Entry(256, 3, 1, 4); Entry(257, 3, 1, 4);   // 4×4
        Entry(259, 3, 1, compression);
        Entry(273, 4, 1, stripOff);
        Entry(277, 3, 1, 3); Entry(278, 3, 1, 4);     // Samples=3, RowsPerStrip=4
        Entry(279, 4, 1, (uint)strip.Length);         // StripByteCounts
        Entry(284, 3, 1, 1);
        Entry(33550, 12, 3, scaleOff); Entry(33922, 12, 6, tieOff);
        w.Write((uint)0);
        w.Write(strip);
        w.Write(1.0); w.Write(1.0); w.Write(0.0);
        w.Write(0.0); w.Write(0.0); w.Write(0.0); w.Write(100.0); w.Write(200.0); w.Write(0.0);
        return ms.ToArray();
    }

    // PIL 生成的 [红绿蓝白]×4 各压缩条带
    public static IEnumerable<object[]> CompressedStrips() => new[]
    {
        new object[] { (ushort)5, new byte[] { 128,63,192,16,56,20,17,255,7,130,128,33,48,136,60,14,21,14,134,66,226,16,136,8 } },   // LZW
        new object[] { (ushort)32773, new byte[] { 0,255,254,0,0,255,254,0,253,255,0,255,254,0,0,255,254,0,253,255,0,255,254,0,0,255,254,0,253,255,0,255,254,0,0,255,254,0,253,255 } },   // PackBits
        new object[] { (ushort)8, new byte[] { 120,156,251,207,192,192,240,31,132,65,128,8,54,0,38,38,23,233 } },   // Deflate
    };

    [Theory]
    [MemberData(nameof(CompressedStrips))]
    public void Compressed_geotiff_decodes_and_samples(ushort compression, byte[] strip)
    {
        var g = new GeoTiffSampler();
        g.Init(new MemoryStream(MakeCompressedGeoTiff(compression, strip)));
        Assert.True(g.Success, g.Error);
        Assert.Equal(4, g.Width);
        // 每行像素 = 红绿蓝白(col 0..3); 行不变
        Assert.Equal((byte)255, g.SampleRgb(100, 200)!.Value.r);   // (0,0)红
        var green = g.SampleRgb(101, 200)!.Value; Assert.Equal((byte)255, green.g); Assert.Equal((byte)0, green.r);   // (1,0)绿
        var blue = g.SampleRgb(102, 199)!.Value; Assert.Equal((byte)255, blue.b);   // (2,1)蓝
        var white = g.SampleRgb(103, 197)!.Value; Assert.Equal((byte)255, white.r); Assert.Equal((byte)255, white.b);   // (3,3)白
    }

    [Fact]
    public void Non_tiff_rejected()
    {
        var g = new GeoTiffSampler();
        g.Init(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.False(g.Success);
    }

    [Fact]
    public void Real_geotiff_sample_parses()
    {
        string path = @"C:\Users\cFore\Desktop\2026年6月测试文件\dlt05.tif";
        if (!File.Exists(path)) return;   // 无样本机器跳过
        using var g = GeoTiffSampler.Load(path);
        Assert.True(g.Success, g.Error);
        Assert.True(g.Width > 0 && g.Height > 0);
        Assert.True(g.MaxX > g.MinX && g.MaxY > g.MinY);
        // 影像中心应采到有效像素
        var mid = g.SampleRgb((g.MinX + g.MaxX) / 2, (g.MinY + g.MaxY) / 2);
        Assert.NotNull(mid);
        // 远在影像外返回 null
        Assert.Null(g.SampleRgb(g.MaxX + 1e6, g.MaxY + 1e6));
    }
}
