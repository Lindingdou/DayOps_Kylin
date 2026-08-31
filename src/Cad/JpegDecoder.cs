using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 基线(Baseline, SOF0) JPEG 解码器 —— 供 GeoTIFF JPEG(Compression=7)正射影像解码。公开 ITU-T T.81 规范,
/// 非依赖 native/外部包(System.Drawing 仅 Windows)。支持 DQT/DHT/SOF0/DRI/SOS + huffman + IDCT + YCbCr→RGB
/// + 色度上采样。进度式(渐进 SOF2)/算术编码 不支持(返错)。纯逻辑, 可对 PIL 生成的 JPEG 独立验。
/// </summary>
public static class JpegDecoder
{
    private static readonly int[] ZigZag =
    {
        0,1,8,16,9,2,3,10,17,24,32,25,18,11,4,5,12,19,26,33,40,48,41,34,27,20,13,6,7,14,21,28,
        35,42,49,56,57,50,43,36,29,22,15,23,30,37,44,51,58,59,52,45,38,31,39,46,53,60,61,54,47,55,62,63,
    };

    private sealed class Huff
    {
        public readonly Dictionary<int, byte> Map = new();   // key = (len<<16)|code
        public int MaxLen;
    }
    private sealed class Comp { public int Id, H, V, Tq, Td, Ta, PrevDc; }

    public sealed class Result
    {
        public bool Success; public string Error = "";
        public int Width, Height;
        public byte[] Rgb = Array.Empty<byte>();   // width*height*3
    }

    /// <summary>解码完整基线 JPEG 字节流(SOI..EOI) → RGB。</summary>
    public static Result Decode(byte[] data)
    {
        var r = new Result();
        try { DecodeInner(data, r); }
        catch (Exception ex) { r.Success = false; r.Error = ex.Message; }
        return r;
    }

    private static void DecodeInner(byte[] d, Result r)
    {
        var qt = new int[4][];
        var huffDc = new Huff[4]; var huffAc = new Huff[4];
        var comps = new List<Comp>();
        int restartInterval = 0;
        int adobeTransform = -1;                       // -1=无 APP14; 0=RGB/CMYK; 1=YCbCr; 2=YCCK
        int p = 0;
        if (d.Length < 2 || d[0] != 0xFF || d[1] != 0xD8) { r.Error = "非 JPEG(缺 SOI)"; return; }
        p = 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            int marker = d[p + 1]; p += 2;
            if (marker == 0xD9) break;                         // EOI
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;   // 无长度段
            int len = (d[p] << 8) | d[p + 1]; int seg = p + 2, segEnd = p + len; p += len;
            switch (marker)
            {
                case 0xDB:                                     // DQT
                    while (seg < segEnd)
                    {
                        int pq = d[seg] >> 4, tq = d[seg] & 0xF; seg++;
                        var t = new int[64];
                        for (int i = 0; i < 64; i++) { t[i] = pq == 0 ? d[seg++] : ((d[seg] << 8) | d[seg + 1]); if (pq != 0) seg += 2; }
                        qt[tq] = t;
                    }
                    break;
                case 0xC4:                                     // DHT
                    while (seg < segEnd)
                    {
                        int tc = d[seg] >> 4, th = d[seg] & 0xF; seg++;
                        var counts = new int[17]; int total = 0;
                        for (int i = 1; i <= 16; i++) { counts[i] = d[seg++]; total += counts[i]; }
                        var h = new Huff(); int code = 0;
                        for (int L = 1; L <= 16; L++)
                        {
                            for (int i = 0; i < counts[L]; i++) { h.Map[(L << 16) | code] = d[seg++]; code++; }
                            code <<= 1; if (counts[L] > 0) h.MaxLen = L;
                        }
                        (tc == 0 ? huffDc : huffAc)[th] = h;
                    }
                    break;
                case 0xC0:                                     // SOF0 baseline
                    r.Height = (d[seg + 1] << 8) | d[seg + 2];
                    r.Width = (d[seg + 3] << 8) | d[seg + 4];
                    int nc = d[seg + 5]; seg += 6;
                    for (int i = 0; i < nc; i++)
                    { comps.Add(new Comp { Id = d[seg], H = d[seg + 1] >> 4, V = d[seg + 1] & 0xF, Tq = d[seg + 2] }); seg += 3; }
                    break;
                case 0xC2: r.Error = "不支持渐进式 JPEG(SOF2)"; return;
                case 0xEE:                                 // APP14 Adobe: 末字节 = 色彩变换标志
                    if (segEnd - seg >= 12 && d[seg] == (byte)'A' && d[seg + 1] == (byte)'d') adobeTransform = d[segEnd - 1];
                    break;
                case 0xDD: restartInterval = (d[seg] << 8) | d[seg + 1]; break;   // DRI
                case 0xDA:                                     // SOS
                    int ns = d[seg]; seg++;
                    for (int i = 0; i < ns; i++)
                    {
                        int cid = d[seg], td = d[seg + 1] >> 4, ta = d[seg + 1] & 0xF; seg += 2;
                        var c = comps.Find(x => x.Id == cid); if (c != null) { c.Td = td; c.Ta = ta; }
                    }
                    seg += 3;                                  // Ss, Se, Ah/Al
                    bool ycbcr = adobeTransform == 1 ||
                        (adobeTransform < 0 && comps.Count == 3 && comps[0].Id == 1 && comps[1].Id == 2 && comps[2].Id == 3);
                    DecodeScan(d, seg, comps, qt, huffDc, huffAc, restartInterval, ycbcr, r);
                    return;
            }
        }
        r.Error = "未见 SOS 扫描";
    }

    private static void DecodeScan(byte[] d, int start, List<Comp> comps, int[][] qt,
        Huff[] huffDc, Huff[] huffAc, int restartInterval, bool ycbcr, Result r)
    {
        int hMax = 1, vMax = 1;
        foreach (var c in comps) { hMax = Math.Max(hMax, c.H); vMax = Math.Max(vMax, c.V); }
        int mcuW = 8 * hMax, mcuH = 8 * vMax;
        int mcusX = (r.Width + mcuW - 1) / mcuW, mcusY = (r.Height + mcuH - 1) / mcuH;
        // 每分量一张全分辨率(按其采样)平面
        var planes = new float[comps.Count][];
        var pw = new int[comps.Count]; var ph = new int[comps.Count];
        for (int i = 0; i < comps.Count; i++)
        { pw[i] = mcusX * comps[i].H * 8; ph[i] = mcusY * comps[i].V * 8; planes[i] = new float[pw[i] * ph[i]]; }

        var br = new BitReader(d, start);
        foreach (var c in comps) c.PrevDc = 0;
        int restartCnt = 0;
        var block = new float[64];
        for (int my = 0; my < mcusY; my++)
            for (int mx = 0; mx < mcusX; mx++)
            {
                if (restartInterval > 0 && restartCnt == restartInterval)
                { br.Restart(); foreach (var c in comps) c.PrevDc = 0; restartCnt = 0; }
                for (int ci = 0; ci < comps.Count; ci++)
                {
                    var c = comps[ci];
                    for (int by = 0; by < c.V; by++)
                        for (int bx = 0; bx < c.H; bx++)
                        {
                            DecodeBlock(br, c, qt[c.Tq], huffDc[c.Td], huffAc[c.Ta], block);
                            int px0 = (mx * c.H + bx) * 8, py0 = (my * c.V + by) * 8;
                            for (int y = 0; y < 8; y++)
                                for (int x = 0; x < 8; x++)
                                    planes[ci][(py0 + y) * pw[ci] + px0 + x] = block[y * 8 + x];
                        }
                }
                restartCnt++;
            }

        // YCbCr(或灰度) → RGB, 色度按采样倍数最近邻上采样
        r.Rgb = new byte[r.Width * r.Height * 3];
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                float c0 = Sample(planes[0], pw[0], ph[0], comps[0], hMax, vMax, x, y);
                float c1 = 128, c2 = 128;
                if (comps.Count >= 3)
                {
                    c1 = Sample(planes[1], pw[1], ph[1], comps[1], hMax, vMax, x, y);
                    c2 = Sample(planes[2], pw[2], ph[2], comps[2], hMax, vMax, x, y);
                }
                float R, G, B;
                if (comps.Count >= 3 && ycbcr)               // YCbCr → RGB
                { R = c0 + 1.402f * (c2 - 128); G = c0 - 0.344136f * (c1 - 128) - 0.714136f * (c2 - 128); B = c0 + 1.772f * (c1 - 128); }
                else if (comps.Count >= 3)                   // 已是 RGB(TIFF-JPEG 常见)
                { R = c0; G = c1; B = c2; }
                else { R = G = B = c0; }                     // 灰度
                int o = (y * r.Width + x) * 3;
                r.Rgb[o] = Clamp(R); r.Rgb[o + 1] = Clamp(G); r.Rgb[o + 2] = Clamp(B);
            }
        r.Success = true;
    }

    private static float Sample(float[] plane, int pw, int ph, Comp c, int hMax, int vMax, int x, int y)
    {
        if (c.H == hMax && c.V == vMax)                 // 全分辨率(如 Y 或 4:4:4)直接取
            return plane[Math.Min(y, ph - 1) * pw + Math.Min(x, pw - 1)];
        // 色度: 双线性上采样(中心对齐), 逼近 libjpeg fancy upsampling
        float fcx = (x + 0.5f) * c.H / hMax - 0.5f, fcy = (y + 0.5f) * c.V / vMax - 0.5f;
        int ix = (int)Math.Floor(fcx), iy = (int)Math.Floor(fcy);
        float tx = fcx - ix, ty = fcy - iy;
        int x0 = Clamp(ix, pw), x1 = Clamp(ix + 1, pw), y0 = Clamp(iy, ph), y1 = Clamp(iy + 1, ph);
        float p00 = plane[y0 * pw + x0], p10 = plane[y0 * pw + x1], p01 = plane[y1 * pw + x0], p11 = plane[y1 * pw + x1];
        return (1 - tx) * (1 - ty) * p00 + tx * (1 - ty) * p10 + (1 - tx) * ty * p01 + tx * ty * p11;
    }
    private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

    private static void DecodeBlock(BitReader br, Comp c, int[] quant, Huff dc, Huff ac, float[] outBlock)
    {
        var coef = new int[64];
        int t = br.DecodeHuff(dc);
        int diff = t == 0 ? 0 : br.Receive(t);
        c.PrevDc += diff; coef[0] = c.PrevDc * quant[0];
        int k = 1;
        while (k < 64)
        {
            int rs = br.DecodeHuff(ac);
            int run = rs >> 4, size = rs & 0xF;
            if (size == 0) { if (run != 15) break; k += 16; continue; }   // EOB 或 ZRL
            k += run; if (k >= 64) break;
            coef[ZigZag[k]] = br.Receive(size) * quant[k];
            k++;
        }
        Idct8x8(coef, outBlock);
    }

    // IDCT 余弦表：static readonly 初始化器由 CLR 类型初始化锁保证只建一次且线程安全。
    // (曾为惰性 if(_cos==null){...}, 并发解码时 B 线程见非 null 但仍零填充的数组 → IDCT 错, 已修。)
    private static readonly float[] _cos = BuildCosTable();
    private static float[] BuildCosTable()
    {
        var c = new float[64];
        for (int k = 0; k < 8; k++)
            for (int f = 0; f < 8; f++)
                c[k * 8 + f] = (float)((f == 0 ? 1.0 / Math.Sqrt(2) : 1.0) * Math.Cos((2 * k + 1) * f * Math.PI / 16.0));
        return c;
    }

    private static void Idct8x8(int[] F, float[] outBlock)
    {
        var tmp = new float[64];
        for (int v = 0; v < 8; v++)                 // 行 IDCT(沿 u)
            for (int x = 0; x < 8; x++)
            { float s = 0; for (int u = 0; u < 8; u++) s += _cos[x * 8 + u] * F[v * 8 + u]; tmp[v * 8 + x] = s; }
        for (int x = 0; x < 8; x++)                 // 列 IDCT(沿 v)
            for (int y = 0; y < 8; y++)
            { float s = 0; for (int v = 0; v < 8; v++) s += _cos[y * 8 + v] * tmp[v * 8 + x]; outBlock[y * 8 + x] = s / 4f + 128f; }
    }

    private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    private sealed class BitReader
    {
        private readonly byte[] _d; private int _pos; private int _bits, _cnt;
        public BitReader(byte[] d, int pos) { _d = d; _pos = pos; }
        public void Restart() { _cnt = 0; while (_pos + 1 < _d.Length && !(_d[_pos] == 0xFF && _d[_pos + 1] >= 0xD0 && _d[_pos + 1] <= 0xD7)) _pos++; if (_pos + 1 < _d.Length) _pos += 2; }
        private int Bit()
        {
            if (_cnt == 0)
            {
                if (_pos >= _d.Length) return 0;
                int b = _d[_pos++];
                if (b == 0xFF) { int n = _pos < _d.Length ? _d[_pos] : 0; if (n == 0) _pos++; else if (n >= 0xD0 && n <= 0xD7) { } else { /* 标记 */ } }
                _bits = b; _cnt = 8;
            }
            _cnt--; return (_bits >> _cnt) & 1;
        }
        public int DecodeHuff(Huff h)
        {
            int code = 0;
            for (int L = 1; L <= 16; L++)
            { code = (code << 1) | Bit(); if (h.Map.TryGetValue((L << 16) | code, out var sym)) return sym; }
            return 0;
        }
        public int Receive(int s)
        {
            int v = 0; for (int i = 0; i < s; i++) v = (v << 1) | Bit();
            if (v < (1 << (s - 1))) v += (-1 << s) + 1;   // 符号扩展
            return v;
        }
    }
}
