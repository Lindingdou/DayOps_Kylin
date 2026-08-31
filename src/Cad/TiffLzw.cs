using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// TIFF 变体 LZW 解压 —— 忠实 TIFF 6.0 规范的 LZW(区别于 GIF): 9→12 位变长码 + **EarlyChange**(码宽提前一码增)
/// + ClearCode(256)/EOI(257)。供 GeoTIFF LZW 压缩正射影像解码。纯逻辑, 可对 PIL 生成的参照强验。
/// </summary>
public static class TiffLzw
{
    private const int ClearCode = 256, EoiCode = 257;

    /// <summary>解压 TIFF-LZW 字节流 → 原始字节。</summary>
    public static byte[] Decode(byte[] input, int expectedLength = 0)
    {
        var output = new List<byte>(expectedLength > 0 ? expectedLength : input.Length * 3);
        if (input == null || input.Length == 0) return Array.Empty<byte>();

        int bitPos = 0, totalBits = input.Length * 8, codeWidth = 9;
        var dict = new List<byte[]>(4096);
        void ResetDict()
        {
            dict.Clear();
            for (int i = 0; i < 256; i++) dict.Add(new[] { (byte)i });
            dict.Add(Array.Empty<byte>());   // 256 ClearCode 占位
            dict.Add(Array.Empty<byte>());   // 257 EOI 占位
            codeWidth = 9;
        }
        int ReadCode()
        {
            if (bitPos + codeWidth > totalBits) return EoiCode;
            int code = 0;
            for (int i = 0; i < codeWidth; i++)
            {
                int bit = (input[bitPos >> 3] >> (7 - (bitPos & 7))) & 1;
                code = (code << 1) | bit; bitPos++;
            }
            return code;
        }

        ResetDict();
        byte[]? old = null;   // TIFF-LZW 流以 ClearCode 开头, old 由首个 ClearCode 后的字面码确立
        while (true)
        {
            int cur = ReadCode();
            if (cur == EoiCode) break;
            if (cur == ClearCode)
            {
                ResetDict();
                cur = ReadCode();
                if (cur == EoiCode) break;
                old = dict[cur];
                output.AddRange(old);
                continue;
            }
            if (old == null) { old = dict[cur]; output.AddRange(old); continue; }   // 容错: 无前导 Clear
            byte[] entry;
            if (cur < dict.Count) entry = dict[cur];
            else { entry = new byte[old.Length + 1]; Array.Copy(old, entry, old.Length); entry[old.Length] = old[0]; }   // KwKwK
            output.AddRange(entry);
            var added = new byte[old.Length + 1];
            Array.Copy(old, added, old.Length); added[old.Length] = entry[0];
            dict.Add(added);
            old = entry;
            if (dict.Count == (1 << codeWidth) - 1 && codeWidth < 12) codeWidth++;   // EarlyChange: 提前一码增宽
        }
        return output.ToArray();
    }

    /// <summary>PackBits(TIFF Compression=32773) RLE 解码: n≥0 复制 n+1 字节字面; n∈[-127,-1] 重复下一字节 1−n 次; n=−128 跳过。</summary>
    public static byte[] PackBitsDecode(byte[] input, int expectedLength = 0)
    {
        var o = new List<byte>(expectedLength > 0 ? expectedLength : (input?.Length ?? 0) * 2);
        if (input == null) return Array.Empty<byte>();
        int i = 0;
        while (i < input.Length)
        {
            sbyte n = (sbyte)input[i++];
            if (n >= 0) { int cnt = n + 1; for (int k = 0; k < cnt && i < input.Length; k++) o.Add(input[i++]); }
            else if (n != -128 && i < input.Length) { int cnt = 1 - n; byte b = input[i++]; for (int k = 0; k < cnt; k++) o.Add(b); }
        }
        return o.ToArray();
    }

    /// <summary>Deflate/zlib(TIFF Compression=8 Adobe Deflate) 解码, 复用内置 ZLibStream(zlib 头)。</summary>
    public static byte[] InflateZlib(byte[] input, int expectedLength = 0)
    {
        if (input == null || input.Length == 0) return Array.Empty<byte>();
        using var ms = new System.IO.MemoryStream(input);
        using var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var o = new System.IO.MemoryStream(expectedLength > 0 ? expectedLength : input.Length * 3);
        z.CopyTo(o);
        return o.ToArray();
    }

    /// <summary>撤销水平差分预测器(TIFF Predictor=2): 每行逐样本累加前一样本。samplesPerPixel 用于跨通道。</summary>
    public static void UndoHorizontalPredictor(byte[] data, int width, int height, int samplesPerPixel)
    {
        int rowBytes = width * samplesPerPixel;
        for (int row = 0; row < height; row++)
        {
            int baseOff = row * rowBytes;
            if (baseOff + rowBytes > data.Length) break;
            for (int i = samplesPerPixel; i < rowBytes; i++)
                data[baseOff + i] = (byte)(data[baseOff + i] + data[baseOff + i - samplesPerPixel]);
        }
    }
}
