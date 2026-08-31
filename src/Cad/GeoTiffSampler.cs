using System;
using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 正射影像(GeoTIFF)像素↔世界坐标映射 —— ModelPixelScale(33550) + ModelTiepoint(33922) 定像素格网到世界。
/// 纯数学、可单测(不碰文件)。像素 (col,row) 原点在左上, 行向下 → 世界 Y 随行减。
/// </summary>
public readonly struct GeoTransform
{
    public readonly double ScaleX, ScaleY, TiePixelCol, TiePixelRow, TieWorldX, TieWorldY;
    public GeoTransform(double sx, double sy, double ti, double tj, double tx, double ty)
    { ScaleX = sx; ScaleY = sy; TiePixelCol = ti; TiePixelRow = tj; TieWorldX = tx; TieWorldY = ty; }

    public (double x, double y) PixelToWorld(double col, double row) =>
        (TieWorldX + (col - TiePixelCol) * ScaleX, TieWorldY - (row - TiePixelRow) * ScaleY);

    public (double col, double row) WorldToPixel(double x, double y) =>
        (TiePixelCol + (x - TieWorldX) / ScaleX, TiePixelRow + (TieWorldY - y) / ScaleY);
}

/// <summary>
/// GeoTIFF 正射影像采样 —— 忠实原 PointCloudLib「真实色(正射影像着色)」: 读 TIFF 头(无压缩 RGB) + 地理配准,
/// 在世界 (x,y) 采像素色, 供点云真实色着色。曾误记 native, 实则 TIFF 公开 + 无压缩样本→可做可验(同 LAS 判据)。
/// 仅支持 无压缩(Compression=1)、chunky、8bit; 其它(LZW/JPEG/planar)返错记录。IDisposable(持流)。
/// </summary>
public sealed class GeoTiffSampler : IDisposable
{
    private Stream _s = Stream.Null;
    private BinaryReader _br = null!;
    private bool _le;
    public int Width, Height, Samples;
    private int _rowsPerStrip;
    private long[] _stripOffsets = Array.Empty<long>();
    private GeoTransform _geo;
    public double MinX, MaxX, MinY, MaxY;
    public bool Success; public string Error = "";

    public static GeoTiffSampler Load(string path)
    {
        var g = new GeoTiffSampler();
        try { g.Init(File.OpenRead(path)); }
        catch (Exception ex) { g.Error = ex.Message; g.Dispose(); }
        return g;
    }

    /// <summary>供单测: 从内存流初始化。</summary>
    public void Init(Stream s)
    {
        _s = s; _br = new BinaryReader(s);
        var bo = _br.ReadBytes(2);
        _le = bo.Length == 2 && bo[0] == (byte)'I';
        if (!(_le || (bo.Length == 2 && bo[0] == (byte)'M'))) { Error = "非 TIFF"; return; }
        _br.ReadUInt16();                       // 42
        uint ifd = U32();
        _s.Seek(ifd, SeekOrigin.Begin);
        int n = U16();
        int compression = 1, planar = 1, rps = int.MaxValue;
        long stripOffTagVal = 0, stripOffCnt = 0; int stripOffType = 0;
        double[] pixScale = Array.Empty<double>(); double[] tiePt = Array.Empty<double>();
        for (int i = 0; i < n; i++)
        {
            int tag = U16(), type = U16(); long cnt = U32(); long valPos = _s.Position; uint val = U32();
            switch (tag)
            {
                case 256: Width = (int)val; break;
                case 257: Height = (int)val; break;
                case 259: compression = (int)val; break;
                case 277: Samples = (int)val; break;
                case 278: rps = (int)val; break;
                case 284: planar = (int)val; break;
                case 273: stripOffTagVal = val; stripOffCnt = cnt; stripOffType = type; break;
                case 33550: pixScale = ReadDoubles(val, cnt); break;
                case 33922: tiePt = ReadDoubles(val, cnt); break;
            }
        }
        if (compression != 1) { Error = $"暂不支持压缩(Compression={compression}); 仅无压缩"; return; }
        if (planar != 1) { Error = "暂不支持 planar 排列"; return; }
        if (Width <= 0 || Height <= 0) { Error = "无效尺寸"; return; }
        if (Samples <= 0) Samples = 3;
        _rowsPerStrip = rps == int.MaxValue ? Height : rps;
        _stripOffsets = ReadLongs(stripOffTagVal, stripOffCnt, stripOffType);
        if (_stripOffsets.Length == 0) { Error = "缺 StripOffsets"; return; }
        if (pixScale.Length < 2 || tiePt.Length < 5) { Error = "缺地理配准(PixelScale/Tiepoint)"; return; }
        _geo = new GeoTransform(pixScale[0], pixScale[1], tiePt[0], tiePt[1], tiePt[3], tiePt[4]);
        var (x0, y0) = _geo.PixelToWorld(0, 0);
        var (x1, y1) = _geo.PixelToWorld(Width, Height);
        MinX = Math.Min(x0, x1); MaxX = Math.Max(x0, x1);
        MinY = Math.Min(y0, y1); MaxY = Math.Max(y0, y1);
        Success = true;
    }

    /// <summary>在世界 (x,y) 采像素 RGB; 落影像外返回 null。</summary>
    public (byte r, byte g, byte b)? SampleRgb(double x, double y)
    {
        if (!Success) return null;
        var (colF, rowF) = _geo.WorldToPixel(x, y);
        int col = (int)Math.Floor(colF), row = (int)Math.Floor(rowF);
        if (col < 0 || row < 0 || col >= Width || row >= Height) return null;
        int strip = row / _rowsPerStrip;
        if (strip >= _stripOffsets.Length) return null;
        int rowInStrip = row - strip * _rowsPerStrip;
        long off = _stripOffsets[strip] + ((long)rowInStrip * Width + col) * Samples;
        if (off + Samples > _s.Length) return null;
        _s.Seek(off, SeekOrigin.Begin);
        var px = _br.ReadBytes(Samples);
        if (px.Length < Samples) return null;
        return Samples >= 3 ? (px[0], px[1], px[2]) : (px[0], px[0], px[0]);   // 灰度→RGB
    }

    private ushort U16() { var b = _br.ReadBytes(2); return _le ? (ushort)(b[0] | b[1] << 8) : (ushort)(b[1] | b[0] << 8); }
    private uint U32() { var b = _br.ReadBytes(4); return _le ? (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24) : (uint)(b[3] | b[2] << 8 | b[1] << 16 | b[0] << 24); }

    private double[] ReadDoubles(uint offset, long count)
    {
        long save = _s.Position; _s.Seek(offset, SeekOrigin.Begin);
        var r = new double[count];
        for (int i = 0; i < count; i++)
        {
            var b = _br.ReadBytes(8);
            if (!_le) Array.Reverse(b);
            r[i] = BitConverter.ToDouble(b, 0);
        }
        _s.Seek(save, SeekOrigin.Begin); return r;
    }

    private long[] ReadLongs(long offsetOrVal, long count, int type)
    {
        if (count <= 1) return new[] { offsetOrVal };          // 单值内联
        long save = _s.Position; _s.Seek(offsetOrVal, SeekOrigin.Begin);
        var r = new long[count];
        for (int i = 0; i < count; i++) r[i] = type == 3 ? U16() : U32();   // SHORT or LONG
        _s.Seek(save, SeekOrigin.Begin); return r;
    }

    public void Dispose() { _br?.Dispose(); _s?.Dispose(); }
}
