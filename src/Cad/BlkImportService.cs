using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// "Block_Model_2.0" (.blk) 八叉树块体导入 —— 忠实移植原 `BlkReader` 的公开(逆向)二进制格式:
/// BinaryWriter 风格(7-bit 变长串长 + GBK), magic "Block_Model_2.0"(或 "3DMine_" 前缀) + origin + 根盒 + extent
/// + 属性 schema + blockCount × {u64 loc 位打包定位码, 属性×4B} + 尾部(类别名表 + 类别色表)。
/// loc: bit0-3=层级 sub, Z=bit5..23, Y=bit24..42, X=bit43..(均为固定分区, 勿写死窄位宽 —— 老样本 Y≤8/Z≤10 位,
/// 3DMine 样本用到 11/12 位, 截断会把采场散成好几块)。
///
/// ⚠ 最细格尺寸 = 【根盒 / 2^maxSub】, 三轴**各不相同**(本例 25×25×0.5 m); 叶块是层级 sub 的变尺寸**长方体**
/// (s=2^(maxSub-sub) 个细格, 世界尺寸 s·bx × s·by × s·bz)。此前只带回一个各向同性的 Size=s·bx, 于是块被画成
/// 25×25×25 的立方体 —— 竖向胖了 50 倍, 整个模型糊成一块平板(用户报「位置错乱/坐标不对」)。现按原
/// `BlkReader`/`SurfaceInstanceBuilder` 的口径带回 原点 / 三轴细格尺寸 / 细格维度 / 每叶块层级,
/// 由 <see cref="BlockModelMeta.FromLeaves"/> 装配成各向异性模型。纯逻辑、可单测。
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

    /// <summary>空 cell / 无数据哨兵（分类属性=非块；数值属性=nodata，同原 BlkReader.NoData）。</summary>
    public const double NoData = -1.0;

    public sealed class Result
    {
        public bool Success; public string Error = "";
        public List<BlockModel.Block> Blocks = new();
        public int BlockCount;
        public List<string> AttrNames = new();   // 全属性名(供选取哪个作品位)
        public List<bool> AttrCategorical = new();   // 与 AttrNames 并行: type 4 = 分类(矿岩类型)
        public string UsedAttr = "";              // 实际取作品位的属性名
        public Dictionary<string, double[]> AllAttrs = new();   // 全属性逐块值(供无重导切换活动属性)

        // ── 几何(同原 BlockModelSpec: Origin / BlockSize / Dimensions / SubBlockDepthMax) ──
        public double Ox, Oy, Oz;                  // 原点 = 最细格 (0,0,0) 的角点
        public double Bx = 1, By = 1, Bz = 1;      // 最细格三轴尺寸(根盒 / 2^maxSub) —— 三轴不等
        public int Nx = 1, Ny = 1, Nz = 1;         // 细格维度(叶块铺满的立方体)
        public int MaxSub;                         // 八叉树最深层级
        public int VarCellCount;                   // 尺寸 ≠ 最细格的叶块数(粗块)

        // ── 尾部(可选): 类别名表 + 类别色表 ──
        public List<string> CategoryNames = new();                       // 索引 = 类别码
        public Dictionary<int, (byte r, byte g, byte b)> CategoryColors = new();
        public string ColorTableAttr = "";          // 色表所属属性名(如「矿岩类型」)
        public (byte r, byte g, byte b)? FillColor; // 单色图例色(取煤类色)
        public string? SuggestedAttr;               // 推荐着色属性(同原 SuggestedColormapAttribute)
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
            r.AttrCategorical.Add(attrType[a] == 4);   // type 4 = 分类(int32), 2 = 数值(float32)
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

        // pass 0: maxSub —— 最细层级取【文件实测最大 sub】(老样本 11, 3DMine 12)。
        // 写死 11 会让 3DMine 文件的子块 shift 整体偏一位, 采场被解成"分散的好几块"(同原 BlkReader)。
        int maxSub = 0;
        for (int rr = 0, q = (int)recStart; rr < blockCount; rr++, q += recBytes)
        { int sub = b[q] & 0xF; if (sub > maxSub) maxSub = sub; }
        if (maxSub == 0) maxSub = 11;
        r.MaxSub = maxSub;

        // pass 1: 由定位码求【细格】维度(叶块铺满的立方体), 供模型维度与 bx 缺省回退
        int maxXf = 1, maxYf = 1, maxZf = 1;
        for (int rr = 0, q = (int)recStart; rr < blockCount; rr++, q += recBytes)
        {
            ulong loc = Loc(b, q);
            int sh = Shift(loc, maxSub);
            int xf = ((int)(loc >> 43) + 1) << sh;
            int yf = ((int)((loc >> 24) & 0x7FFFF) + 1) << sh;
            int zf = ((int)((loc >> 5) & 0x7FFFF) + 1) << sh;
            if (xf > maxXf) maxXf = xf;
            if (yf > maxYf) maxYf = yf;
            if (zf > maxZf) maxZf = zf;
        }
        int nx = maxXf, ny = maxYf, nz = maxZf;
        if (blockCount == 0) { nx = ny = nz = 1; }

        // 最细格尺寸 = 【八叉树根盒 / 2^maxSub】(几何正解, 恒为整米: 51200/2^11=25、1024/2^11=0.5)。
        // 三轴各不相同 —— 水平 25 m 规则 + 垂向 ~0.5 m 自适应细分。格尺寸错则【位置也错】。
        // 根盒缺失(=0)才回退 extent/n(同原 BlkReader; 此前回退写死 /4096, 与实际维度无关)。
        double div = (double)(1L << maxSub);
        double bx = rootX > 0 ? rootX / div : (ex > 0 && nx > 0 ? ex / nx : 25.0);
        double by = rootY > 0 ? rootY / div : (ey > 0 && ny > 0 ? ey / ny : 25.0);
        double bz = rootZ > 0 ? rootZ / div : (ez > 0 && nz > 0 ? ez / nz : 0.5);
        r.Ox = ox; r.Oy = oy; r.Oz = oz;
        r.Bx = bx; r.By = by; r.Bz = bz;
        r.Nx = nx; r.Ny = ny; r.Nz = nz;

        // pass 2: 叶块中心 + 全属性逐块值(供切换)。Size = 叶块的 X 边长(= s·bx);
        // Y/Z 边长按模型纵横比推(s·by / s·bz), 见 BlockModelMeta.CellScale。
        r.Blocks.Capacity = blockCount;   // 三百万块靠 List 自增会翻倍搬十几次(每块 40 B, 白搬两百多 MB)
        var attrArrays = new double[attrCount][];
        for (int a = 0; a < attrCount; a++) attrArrays[a] = new double[blockCount];
        for (int rr = 0, q = (int)recStart; rr < blockCount; rr++, q += recBytes)
        {
            ulong loc = Loc(b, q);
            int sh = Shift(loc, maxSub);
            int s = 1 << sh;
            if (s != 1) r.VarCellCount++;
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

        ReadTail(b, (int)recEnd, r);
        r.SuggestedAttr = Suggest(r);
        r.Success = true;
        return r;
    }

    private static ulong Loc(byte[] b, int q)
        => (uint)(b[q] | b[q + 1] << 8 | b[q + 2] << 16 | b[q + 3] << 24)
         | ((ulong)(uint)(b[q + 4] | b[q + 5] << 8 | b[q + 6] << 16 | b[q + 7] << 24) << 32);

    /// <summary>叶块层级位移 sh: 边长 = 2^sh 个最细格。防御异常层级(同原 BlkReader)。</summary>
    private static int Shift(ulong loc, int maxSub)
    { int sh = maxSub - (int)(loc & 0xF); return sh < 0 ? 0 : sh > 20 ? 20 : sh; }

    /// <summary>
    /// 尾部(可选, 缺失即忽略): [i32]类别数 + 类别名表; [i32]ver + [str]属性名 + [i32]色数 + 各 {名, f32 RGB}。
    /// 类别名表的索引就是类别码, 颜色表按【名】给色 → 翻成码存。解析失败不影响导入(同原 BlkReader)。
    /// </summary>
    private static void ReadTail(byte[] b, int pos, Result r)
    {
        try
        {
            int p = pos;
            if (b.Length - p >= 4)
            {
                int catCount = I(b, ref p);
                if (catCount >= 0 && catCount <= 4096)
                    for (int i = 0; i < catCount && p < b.Length; i++) r.CategoryNames.Add(ReadStr(b, ref p));
            }
            if (b.Length - p >= 4)
            {
                I(b, ref p);                          // ver
                r.ColorTableAttr = ReadStr(b, ref p);   // 颜色表所属属性名(如「矿岩类型」)
                int colorCount = I(b, ref p);
                if (colorCount >= 0 && colorCount <= 4096)
                    for (int i = 0; i < colorCount && p < b.Length; i++)
                    {
                        string nm = ReadStr(b, ref p);
                        float cr = F(b, ref p), cg = F(b, ref p), cb = F(b, ref p);
                        var col = (B(cr), B(cg), B(cb));
                        int code = r.CategoryNames.IndexOf(nm);   // 名 → 码
                        if (code >= 0) r.CategoryColors[code] = col;
                        if (r.FillColor == null && (nm.Contains("SEAM") || nm.Contains("煤"))) r.FillColor = col;
                    }
            }
        }
        catch { /* 尾部可选, 忽略解析失败 */ }
    }

    /// <summary>
    /// 推荐着色属性(同原 BlkReader)：优先「带颜色表的分类属性」(导入即按矿岩类型逐类上色, 最直观),
    /// 否则退回连续型(发热量 / 首个数值列), 最后兜底首列。
    /// </summary>
    private static string? Suggest(Result r)
    {
        if (r.CategoryColors.Count > 0)
        {
            for (int a = 0; a < r.AttrNames.Count; a++)
                if (r.AttrCategorical[a] && r.AttrNames[a] == r.ColorTableAttr) return r.AttrNames[a];
            for (int a = 0; a < r.AttrNames.Count; a++)
                if (r.AttrCategorical[a]) return r.AttrNames[a];
        }
        string? s = null;
        for (int a = 0; a < r.AttrNames.Count; a++)
            if (!r.AttrCategorical[a]) { s = r.AttrNames[a]; if (r.AttrNames[a].Contains("发热")) break; }
        return s ?? (r.AttrNames.Count > 0 ? r.AttrNames[0] : null);
    }

    /// <summary>属性单位(同原 BlkReader.UnitFor)。</summary>
    public static string UnitFor(string name) => name switch
    {
        "比重" => "t/m3",
        "灰分" or "硫分" or "水分" or "挥发分" => "%",
        "发热量" => "kcal/kg",
        _ => "",
    };

    private static byte B(float v) => (byte)Math.Clamp((int)Math.Round(v * 255.0f), 0, 255);
    private static float F(byte[] b, ref int p) { float v = BitConverter.ToSingle(b, p); p += 4; return v; }

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
