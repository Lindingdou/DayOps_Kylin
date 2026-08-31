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

    // PIL TIFF-JPEG(16×16 渐变)提取: JPEGTables(347) + 条带(273) + 期望像素
    private static readonly byte[] JT = { 255,216,255,219,0,67,0,8,6,6,7,6,5,8,7,7,7,9,9,8,10,12,20,13,12,11,11,12,25,18,19,15,20,29,26,31,30,29,26,28,28,32,36,46,39,32,34,44,35,28,28,40,55,41,44,48,49,52,52,52,31,39,57,61,56,50,60,46,51,52,50,255,196,0,31,0,0,1,5,1,1,1,1,1,1,0,0,0,0,0,0,0,0,1,2,3,4,5,6,7,8,9,10,11,255,196,0,181,16,0,2,1,3,3,2,4,3,5,5,4,4,0,0,1,125,1,2,3,0,4,17,5,18,33,49,65,6,19,81,97,7,34,113,20,50,129,145,161,8,35,66,177,193,21,82,209,240,36,51,98,114,130,9,10,22,23,24,25,26,37,38,39,40,41,42,52,53,54,55,56,57,58,67,68,69,70,71,72,73,74,83,84,85,86,87,88,89,90,99,100,101,102,103,104,105,106,115,116,117,118,119,120,121,122,131,132,133,134,135,136,137,138,146,147,148,149,150,151,152,153,154,162,163,164,165,166,167,168,169,170,178,179,180,181,182,183,184,185,186,194,195,196,197,198,199,200,201,202,210,211,212,213,214,215,216,217,218,225,226,227,228,229,230,231,232,233,234,241,242,243,244,245,246,247,248,249,250,255,217 };
    private static readonly byte[] JSP = { 255,216,255,192,0,17,8,0,16,0,16,3,82,17,0,71,17,0,66,17,0,255,218,0,12,3,82,0,71,0,66,0,0,63,0,243,127,7,127,203,58,243,127,248,67,191,233,159,233,85,235,232,15,7,127,203,58,63,225,14,255,0,166,127,165,21,243,255,0,131,191,229,157,125,1,255,0,8,119,253,51,253,40,175,160,60,29,255,0,44,232,255,0,132,59,254,153,254,148,87,255,217 };
    private static readonly byte[] JExp = { 0,0,120,14,0,120,33,0,120,49,0,120,63,0,120,79,0,120,98,0,120,112,0,120,128,0,120,142,0,120,161,0,120,177,0,120,191,0,120,207,0,120,226,0,120,240,0,120,0,16,120,14,16,120,33,16,120,49,16,120,63,16,120,79,16,120,98,16,120,112,16,120,128,16,120,142,16,120,161,16,120,177,16,120,191,16,120,207,16,120,226,16,120,240,16,120,0,32,120,14,32,120,33,32,120,49,32,120,63,32,120,79,32,120,98,32,120,112,32,120,128,32,120,142,32,120,161,32,120,177,32,120,191,32,120,207,32,120,226,32,120,240,32,120,0,47,120,14,47,120,33,47,120,49,47,120,63,47,120,79,47,120,98,47,120,112,47,120,128,47,120,142,47,120,161,47,120,177,47,120,191,47,120,207,47,120,226,47,120,240,47,120,0,65,120,14,65,120,33,65,120,49,65,120,63,65,120,79,65,120,98,65,120,112,65,120,128,65,120,142,65,120,161,65,120,177,65,120,191,65,120,207,65,120,226,65,120,240,65,120,0,80,120,14,80,120,33,80,120,49,80,120,63,80,120,79,80,120,98,80,120,112,80,120,128,80,120,142,80,120,161,80,120,177,80,120,191,80,120,207,80,120,226,80,120,240,80,120,0,96,120,14,96,120,33,96,120,49,96,120,63,96,120,79,96,120,98,96,120,112,96,120,128,96,120,142,96,120,161,96,120,177,96,120,191,96,120,207,96,120,226,96,120,240,96,120,0,112,120,14,112,120,33,112,120,49,112,120,63,112,120,79,112,120,98,112,120,112,112,120,128,112,120,142,112,120,161,112,120,177,112,120,191,112,120,207,112,120,226,112,120,240,112,120,0,128,120,14,128,120,33,128,120,49,128,120,63,128,120,79,128,120,98,128,120,112,128,120,128,128,120,142,128,120,161,128,120,177,128,120,191,128,120,207,128,120,226,128,120,240,128,120,0,144,120,14,144,120,33,144,120,49,144,120,63,144,120,79,144,120,98,144,120,112,144,120,128,144,120,142,144,120,161,144,120,177,144,120,191,144,120,207,144,120,226,144,120,240,144,120,0,160,120,14,160,120,33,160,120,49,160,120,63,160,120,79,160,120,98,160,120,112,160,120,128,160,120,142,160,120,161,160,120,177,160,120,191,160,120,207,160,120,226,160,120,240,160,120,0,175,120,14,175,120,33,175,120,49,175,120,63,175,120,79,175,120,98,175,120,112,175,120,128,175,120,142,175,120,161,175,120,177,175,120,191,175,120,207,175,120,226,175,120,240,175,120,0,193,120,14,193,120,33,193,120,49,193,120,63,193,120,79,193,120,98,193,120,112,193,120,128,193,120,142,193,120,161,193,120,177,193,120,191,193,120,207,193,120,226,193,120,240,193,120,0,208,120,14,208,120,33,208,120,49,208,120,63,208,120,79,208,120,98,208,120,112,208,120,128,208,120,142,208,120,161,208,120,177,208,120,191,208,120,207,208,120,226,208,120,240,208,120,0,224,120,14,224,120,33,224,120,49,224,120,63,224,120,79,224,120,98,224,120,112,224,120,128,224,120,142,224,120,161,224,120,177,224,120,191,224,120,207,224,120,226,224,120,240,224,120,0,240,120,14,240,120,33,240,120,49,240,120,63,240,120,79,240,120,98,240,120,112,240,120,128,240,120,142,240,120,161,240,120,177,240,120,191,240,120,207,240,120,226,240,120,240,240,120 };

    private static byte[] MakeJpegGeoTiff()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(new byte[] { (byte)'I', (byte)'I' }); w.Write((ushort)42); w.Write((uint)8);
        void Entry(ushort tag, ushort type, uint count, uint val) { w.Write(tag); w.Write(type); w.Write(count); w.Write(val); }
        uint stripOff = 146, jtOff = (uint)(146 + JSP.Length), scaleOff = jtOff + (uint)JT.Length, tieOff = scaleOff + 24;
        w.Write((ushort)11);
        Entry(256, 3, 1, 16); Entry(257, 3, 1, 16);
        Entry(259, 3, 1, 7);                          // Compression=JPEG
        Entry(273, 4, 1, stripOff);
        Entry(277, 3, 1, 3); Entry(278, 3, 1, 16);
        Entry(279, 4, 1, (uint)JSP.Length);
        Entry(284, 3, 1, 1);
        Entry(347, 7, (uint)JT.Length, jtOff);        // JPEGTables
        Entry(33550, 12, 3, scaleOff); Entry(33922, 12, 6, tieOff);
        w.Write((uint)0);
        w.Write(JSP); w.Write(JT);
        w.Write(1.0); w.Write(1.0); w.Write(0.0);
        w.Write(0.0); w.Write(0.0); w.Write(0.0); w.Write(100.0); w.Write(200.0); w.Write(0.0);
        return ms.ToArray();
    }

    [Fact]
    public void Jpeg_geotiff_decodes_and_samples()
    {
        var g = new GeoTiffSampler();
        g.Init(new MemoryStream(MakeJpegGeoTiff()));
        Assert.True(g.Success, g.Error);
        Assert.Equal(16, g.Width);
        int maxDiff = 0;
        for (int row = 0; row < 16; row++)
            for (int col = 0; col < 16; col++)
            {
                var rgb = g.SampleRgb(100 + col, 200 - row);   // 像素(col,row)
                Assert.NotNull(rgb);
                int e = (row * 16 + col) * 3;
                maxDiff = Math.Max(maxDiff, Math.Abs(rgb!.Value.r - JExp[e]));
                maxDiff = Math.Max(maxDiff, Math.Abs(rgb.Value.g - JExp[e + 1]));
                maxDiff = Math.Max(maxDiff, Math.Abs(rgb.Value.b - JExp[e + 2]));
            }
        Assert.True(maxDiff <= 5, $"JPEG GeoTIFF 最大像素差 {maxDiff} 应≤5");
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
