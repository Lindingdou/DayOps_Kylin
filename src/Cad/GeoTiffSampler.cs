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
    private int _rowsPerStrip, _compression = 1, _predictor = 1;
    private long[] _stripOffsets = Array.Empty<long>();
    private long[] _stripByteCounts = Array.Empty<long>();
    private byte[] _jpegTables = Array.Empty<byte>();               // JPEG(347): 共享 DQT/DHT 表
    private readonly Dictionary<int, byte[]> _stripCache = new();   // 压缩: 解码后条带缓存
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
        long stripCntTagVal = 0, stripCntCnt = 0; int stripCntType = 0;
        double[] pixScale = Array.Empty<double>(); double[] tiePt = Array.Empty<double>();
        for (int i = 0; i < n; i++)
        {
            int tag = U16(), type = U16(); long cnt = U32(); uint val = U32();
            switch (tag)
            {
                case 256: Width = (int)val; break;
                case 257: Height = (int)val; break;
                case 259: compression = (int)val; break;
                case 277: Samples = (int)val; break;
                case 278: rps = (int)val; break;
                case 279: stripCntTagVal = val; stripCntCnt = cnt; stripCntType = type; break;
                case 284: planar = (int)val; break;
                case 317: _predictor = (int)val; break;
                case 347: _jpegTables = ReadRawBytes(val, cnt); break;   // JPEGTables
                case 273: stripOffTagVal = val; stripOffCnt = cnt; stripOffType = type; break;
                case 33550: pixScale = ReadDoubles(val, cnt); break;
                case 33922: tiePt = ReadDoubles(val, cnt); break;
            }
        }
        if (compression != 1 && compression != 5 && compression != 8 && compression != 32946 && compression != 32773 && compression != 7)
        { Error = $"暂不支持压缩(Compression={compression}); 支持 无压缩/LZW/Deflate/PackBits/JPEG"; return; }
        if (planar != 1) { Error = "暂不支持 planar 排列"; return; }
        if (Width <= 0 || Height <= 0) { Error = "无效尺寸"; return; }
        if (Samples <= 0) Samples = 3;
        _compression = compression;
        _rowsPerStrip = rps == int.MaxValue ? Height : rps;
        _stripOffsets = ReadLongs(stripOffTagVal, stripOffCnt, stripOffType);
        _stripByteCounts = ReadLongs(stripCntTagVal, stripCntCnt, stripCntType);
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
        long inStrip = ((long)rowInStrip * Width + col) * Samples;
        byte[] px;
        if (_compression != 1)                       // 压缩: 解码整条带(缓存)后取像素
        {
            var decoded = DecodeStrip(strip);
            if (decoded == null || inStrip + Samples > decoded.Length) return null;
            px = new byte[Samples];
            Array.Copy(decoded, inStrip, px, 0, Samples);
        }
        else                                         // 无压缩: 直接 seek+read
        {
            long off = _stripOffsets[strip] + inStrip;
            if (off + Samples > _s.Length) return null;
            _s.Seek(off, SeekOrigin.Begin);
            px = _br.ReadBytes(Samples);
            if (px.Length < Samples) return null;
        }
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

    private byte[]? DecodeStrip(int strip)
    {
        if (_stripCache.TryGetValue(strip, out var cached)) return cached;
        if (strip >= _stripByteCounts.Length || strip >= _stripOffsets.Length) return null;
        long off = _stripOffsets[strip], cnt = _stripByteCounts[strip];
        if (cnt <= 0 || off + cnt > _s.Length) return null;
        _s.Seek(off, SeekOrigin.Begin);
        var comp = _br.ReadBytes((int)cnt);
        int rowsInStrip = Math.Min(_rowsPerStrip, Height - strip * _rowsPerStrip);
        int expected = rowsInStrip * Width * Samples;
        byte[] decoded = _compression switch
        {
            5 => TiffLzw.Decode(comp, expected),
            8 or 32946 => TiffLzw.InflateZlib(comp, expected),
            32773 => TiffLzw.PackBitsDecode(comp, expected),
            7 => DecodeJpeg(comp),
            _ => Array.Empty<byte>(),
        };
        if (_compression != 7 && _predictor == 2) TiffLzw.UndoHorizontalPredictor(decoded, Width, rowsInStrip, Samples);
        _stripCache[strip] = decoded;
        return decoded;
    }

    // JPEG(Compression=7): 拼 JPEGTables(去尾 EOI) + 条带(去头 SOI) 成完整 JPEG → 解码 RGB
    private byte[] DecodeJpeg(byte[] strip)
    {
        byte[] full;
        if (_jpegTables.Length > 4 && strip.Length > 2)
        {
            int tlen = _jpegTables.Length - 2;                 // 去 JPEGTables 尾 EOI(FFD9)
            full = new byte[tlen + strip.Length - 2];
            Array.Copy(_jpegTables, 0, full, 0, tlen);
            Array.Copy(strip, 2, full, tlen, strip.Length - 2);   // 去条带头 SOI(FFD8)
        }
        else full = strip;
        var res = JpegDecoder.Decode(full);
        return res.Success ? res.Rgb : Array.Empty<byte>();
    }

    private byte[] ReadRawBytes(uint offset, long count)
    {
        if (count <= 0 || offset + count > _s.Length) return Array.Empty<byte>();
        long save = _s.Position; _s.Seek(offset, SeekOrigin.Begin);
        var b = _br.ReadBytes((int)count); _s.Seek(save, SeekOrigin.Begin);
        return b;
    }

    public void Dispose() { _br?.Dispose(); _s?.Dispose(); }
}
