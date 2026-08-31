using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// "Block_Model_2.0" (.blk) 八叉树块体导入 —— 忠实移植原 `BlkReader` 的公开(逆向)二进制格式:
/// BinaryWriter 风格(7-bit 变长串长 + GBK), magic "Block_Model_2.0"(或 "3DMine_" 前缀) + origin + 根盒 + extent
/// + 属性 schema + blockCount × {u64 loc 位打包定位码, 属性×4B}。loc: bit0-3=层级 sub, Z=bit5..23, Y=bit24..42,
/// X=bit43..; 细格尺寸=根盒/2^maxSub。取叶块中心+首数值属性作品位, 入 grade-only 块模型。纯逻辑、可单测。
/// </summary>
public static class BlkImportService
{
    public const string Magic = "Block_Model_2.0";
    private static readonly Encoding Gbk = MakeGbk();
    private static Encoding MakeGbk()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding("GBK"); } catch { try { return Encoding.GetEncoding(936); } catch { return Encoding.Default; } }
    }

    public sealed class Result
    {
        public bool Success; public string Error = "";
        public List<BlockModel.Block> Blocks = new();
        public int BlockCount;
        public List<string> AttrNames = new();   // 全属性名(供选取哪个作品位)
        public string UsedAttr = "";              // 实际取作品位的属性名
        public Dictionary<string, double[]> AllAttrs = new();   // 全属性逐块值(供无重导切换活动属性)
    }

    public static Result Load(string path, string? selectAttr = null)
    {
        try { return Parse(File.ReadAllBytes(path), selectAttr); }
        catch (Exception ex) { return new Result { Error = ex.Message }; }
    }

    /// <summary>selectAttr 非空则取该名属性作品位(找不到回落首数值属性)。</summary>
    public static Result Parse(byte[] b, string? selectAttr = null)
    {
        var r = new Result();
        if (b == null || b.Length < 64) { r.Error = "BLK 过小"; return r; }
        int p = 0;
        string magic = ReadStr(b, ref p);
        if (!magic.EndsWith(Magic, StringComparison.Ordinal)) { r.Error = $"BLK magic 不匹配: {magic}"; return r; }
        p += 8;                                                  // a,b (2×i32) 跳
        double ox = D(b, ref p), oy = D(b, ref p), oz = D(b, ref p);   // origin
        double rootX = D(b, ref p), rootY = D(b, ref p), rootZ = D(b, ref p);   // 根盒
        double ex = D(b, ref p), ey = D(b, ref p), ez = D(b, ref p);   // extent
        p += 8;                                                  // d9 跳
        p += 1;                                                  // flag 跳
        int attrCount = I(b, ref p);
        if (attrCount < 0 || attrCount > 4096) { r.Error = $"BLK 属性数异常 {attrCount}"; return r; }
        var attrType = new int[attrCount];
        int firstNumeric = -1, selectIdx = -1;
        for (int a = 0; a < attrCount; a++)
        {
            string name = ReadStr(b, ref p);
            r.AttrNames.Add(name);
            attrType[a] = I(b, ref p);
            if (firstNumeric < 0 && attrType[a] == 2) firstNumeric = a;   // 首 float 属性
            if (selectAttr != null && string.Equals(name, selectAttr, StringComparison.OrdinalIgnoreCase)) selectIdx = a;
        }
        int gradeAttr = selectIdx >= 0 ? selectIdx : firstNumeric;        // 选中优先, 否则首数值
        if (gradeAttr < 0 && attrCount > 0) gradeAttr = 0;
        r.UsedAttr = gradeAttr >= 0 && gradeAttr < r.AttrNames.Count ? r.AttrNames[gradeAttr] : "";

        int blockCount = I(b, ref p);
        if (blockCount < 0) { r.Error = $"BLK 块数异常 {blockCount}"; return r; }
        r.BlockCount = blockCount;
        int recBytes = 8 + attrCount * 4;
        long recStart = p, recEnd = recStart + (long)recBytes * blockCount;
        if (recEnd > b.Length) { r.Error = "BLK 记录区越界"; return r; }

        // pass 0: maxSub
        int maxSub = 0;
        for (int rr = 0, q = (int)recStart; rr < blockCount; rr++, q += recBytes)
        { int sub = b[q] & 0xF; if (sub > maxSub) maxSub = sub; }
        if (maxSub == 0) maxSub = 11;

        double div = (double)(1L << maxSub);
        double bx = rootX > 0 ? rootX / div : (ex > 0 ? ex / 4096 : 25.0);
        double by = rootY > 0 ? rootY / div : (ey > 0 ? ey / 4096 : 25.0);
        double bz = rootZ > 0 ? rootZ / div : (ez > 0 ? ez / 4096 : 0.5);

        // pass 1: 叶块中心 + 全属性逐块值(供切换)
        var attrArrays = new double[attrCount][];
        for (int a = 0; a < attrCount; a++) attrArrays[a] = new double[blockCount];
        for (int rr = 0, q = (int)recStart; rr < blockCount; rr++, q += recBytes)
        {
            ulong loc = (uint)(b[q] | b[q + 1] << 8 | b[q + 2] << 16 | b[q + 3] << 24)
                      | ((ulong)(uint)(b[q + 4] | b[q + 5] << 8 | b[q + 6] << 16 | b[q + 7] << 24) << 32);
            int sh = maxSub - (int)(loc & 0xF); if (sh < 0) sh = 0; else if (sh > 20) sh = 20;
            int s = 1 << sh;
            long xf = (long)(loc >> 43) << sh;
            long yf = (long)((loc >> 24) & 0x7FFFF) << sh;
            long zf = (long)((loc >> 5) & 0x7FFFF) << sh;
            for (int a = 0; a < attrCount; a++)
            {
                int vp = q + 8 + a * 4;
                attrArrays[a][rr] = attrType[a] == 4
                    ? (b[vp] | b[vp + 1] << 8 | b[vp + 2] << 16 | b[vp + 3] << 24)
                    : BitConverter.ToSingle(b, vp);
            }
            r.Blocks.Add(new BlockModel.Block
            {
                X = ox + (xf + s / 2.0) * bx,
                Y = oy + (yf + s / 2.0) * by,
                Z = oz + (zf + s / 2.0) * bz,
                Size = s * bx,
                Grade = gradeAttr >= 0 ? attrArrays[gradeAttr][rr] : 0,
            });
        }
        for (int a = 0; a < attrCount; a++) r.AllAttrs[r.AttrNames[a]] = attrArrays[a];
        r.Success = true;
        return r;
    }

    // .NET BinaryWriter 串: 7-bit 变长长度 + GBK 字节
    private static string ReadStr(byte[] b, ref int p)
    {
        int len = 0, shift = 0;
        while (true) { byte x = b[p++]; len |= (x & 0x7F) << shift; if ((x & 0x80) == 0) break; shift += 7; if (shift > 35) break; }
        if (len < 0 || p + len > b.Length) return "";
        var s = Gbk.GetString(b, p, len); p += len; return s;
    }
    private static int I(byte[] b, ref int p) { int v = b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24; p += 4; return v; }
    private static double D(byte[] b, ref int p) { double v = BitConverter.ToDouble(b, p); p += 8; return v; }
}
