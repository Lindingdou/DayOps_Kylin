using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// PMB(PitMine 块体模型 v1)导出 —— 忠实移植原 `PmbmWriter`/`PmbmFormat` 的公开二进制布局(源码可见):
/// Header(32B 'PMB1') + 段表(24B/项) + Strings(名池) + GridSpec(全字段: origin/blockSize/dims/rotation/storageMode/subblock)
/// + PropertySchema(11) + Blocks(Dense, 属性存 double 数组, x-fastest) + DisplayStyle(16) + DeletedCells(17) + CategoryMeta(19)
/// + Footer(activeCount + IEEE CRC32 + 'PMB1' 逆序 magicEnd)。
/// 与 <see cref="PmbImportService"/> 成读写对: 写→读往返还原网格 + 全属性 + 属性表 + 显示样式 + 已删集合 + 分类码名/配色。
/// 纯逻辑、可单测。
/// </summary>
public static class PmbExportService
{
    public const uint MagicStart = 0x31424D50;   // 'P','M','B','1'
    public const uint MagicEnd = 0x504D4231;     // '1','B','M','P' (LE)
    public const uint Version = 1;
    public const int HeaderSize = 32, SectionEntrySize = 24, FooterSize = 16;
    public const uint SectionStrings = 1, SectionGridSpec = 10, SectionPropertySchema = 11, SectionBlocks = 12;
    public const uint SectionDisplayStyle = 16, SectionDeletedCells = 17, SectionCategoryMeta = 19;

    /// <summary>规则网格规格(原点=最小角, 块尺寸, 维度)。</summary>
    public readonly record struct Grid(double Ox, double Oy, double Oz, double Sx, double Sy, double Sz, int Nx, int Ny, int Nz);

    /// <summary>
    /// 随网格一起落盘的模型元数据（原 PmbmWriter 的 GridSpec 全字段 + PropertySchema/DisplayStyle/DeletedCells/CategoryMeta 四段）。
    /// 不填 = 只写几何，与旧行为一致。
    /// </summary>
    public sealed class ModelMeta
    {
        public string Name = "block_model";
        public string Description = "";
        /// <summary>绕 Z 旋转（度；段里按原版存弧度）。</summary>
        public double RotationZDeg;
        public BlockStorageMode StorageMode = BlockStorageMode.Dense;
        public int SubBlockDepthMax;
        public double SubMinX, SubMinY, SubMinZ;
        /// <summary>属性表（列名/类型/单位/默认值/备注/是否分类 + 分类码名表）。</summary>
        public List<BlockPropertyColumn> Schema = new();
        /// <summary>显示样式（填充/边线/边宽/默认色带 + 逐类颜色）。</summary>
        public BlockDisplayStyle? Style;
        /// <summary>活动着色属性（DisplayStyle 段 v1.4 字段）。</summary>
        public string? ActiveColormapAttribute;
        /// <summary>已删 cell 的线性下标（x-fastest）。</summary>
        public HashSet<long> DeletedCells = new();
    }

    /// <summary>
    /// grid + 属性(名, x-fastest 值数组, 长度须 = Nx·Ny·Nz) → PMB v1 字节。modelName 入 Strings 名池。
    /// 属性可空(仅写网格几何, Blocks 段省略)。
    /// </summary>
    public static byte[] ToBytes(Grid g, IReadOnlyList<(string name, double[] values)>? attrs, string modelName = "block_model")
        => ToBytes(g, attrs, new ModelMeta { Name = modelName });

    /// <summary>带完整元数据的写出（导出块体走这一支，与原版 PmbmWriter.Write 同段集）。</summary>
    public static byte[] ToBytes(Grid g, IReadOnlyList<(string name, double[] values)>? attrs, ModelMeta meta)
    {
        long blockCount = (long)g.Nx * g.Ny * g.Nz;
        if (g.Nx <= 0 || g.Ny <= 0 || g.Nz <= 0) throw new ArgumentException("网格维度须为正");
        attrs ??= new List<(string, double[])>();
        foreach (var (nm, v) in attrs)
            if (v.Length != blockCount) throw new ArgumentException($"属性 {nm} 值数 {v.Length} ≠ 块数 {blockCount}");
        meta ??= new ModelMeta();

        // ── Strings 名池：所有引用它的段先把字符串登记进来，最后统一序列化 ──
        var strings = new List<string>();
        var strIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        int Str(string? v)
        {
            v ??= "";
            if (strIdx.TryGetValue(v, out int i)) return i;
            i = strings.Count; strings.Add(v); strIdx[v] = i; return i;
        }
        int nameIdx = Str(meta.Name);
        int descIdx = Str(meta.Description);
        var attrNameIdx = new int[attrs.Count];
        for (int a = 0; a < attrs.Count; a++) attrNameIdx[a] = Str(attrs[a].name);

        byte[] gridSeg = SerializeGridSpec(g, nameIdx, descIdx, meta);
        byte[]? schemaSeg = meta.Schema.Count > 0 ? SerializePropertySchema(meta.Schema, Str) : null;
        byte[]? blocksSeg = attrs.Count > 0 ? SerializeBlocks(blockCount, attrs, attrNameIdx) : null;
        byte[]? styleSeg = meta.Style != null ? SerializeDisplayStyle(meta.Style, string.IsNullOrEmpty(meta.ActiveColormapAttribute) ? -1 : Str(meta.ActiveColormapAttribute)) : null;
        byte[]? deletedSeg = meta.DeletedCells.Count > 0 ? SerializeDeletedCells(meta.DeletedCells) : null;
        byte[]? catSeg = SerializeCategoryMeta(meta, Str);
        byte[] stringsSeg = SerializeStrings(strings);   // 名池最后序列化：上面各段还会往里加字符串

        var segs = new List<(uint id, byte[] data)> { (SectionStrings, stringsSeg), (SectionGridSpec, gridSeg) };
        if (schemaSeg != null) segs.Add((SectionPropertySchema, schemaSeg));
        if (blocksSeg != null) segs.Add((SectionBlocks, blocksSeg));
        if (styleSeg != null) segs.Add((SectionDisplayStyle, styleSeg));
        if (deletedSeg != null) segs.Add((SectionDeletedCells, deletedSeg));
        if (catSeg != null) segs.Add((SectionCategoryMeta, catSeg));

        int sectionTableSize = segs.Count * SectionEntrySize;
        long off = HeaderSize + sectionTableSize;
        var offsets = new long[segs.Count];
        for (int i = 0; i < segs.Count; i++) { offsets[i] = off; off += segs[i].data.Length; }
        long fileSize = off + FooterSize;

        using var ms = new MemoryStream((int)fileSize);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        // Header (32B)
        bw.Write(MagicStart); bw.Write(Version); bw.Write((uint)0);
        bw.Write(fileSize); bw.Write((long)HeaderSize); bw.Write((uint)segs.Count);
        // SectionTable
        for (int i = 0; i < segs.Count; i++) WriteSectionEntry(bw, segs[i].id, offsets[i], segs[i].data.Length);
        // Section data
        foreach (var seg in segs) bw.Write(seg.data);
        // Footer: activeBlockCount(8) + crc32(4) + magicEnd(4); crc 覆盖 [0, fileSize-16)
        bw.Write(blockCount - meta.DeletedCells.Count);
        bw.Flush();
        var fileBytes = ms.ToArray();
        uint crc = Crc32(fileBytes, (int)(fileSize - FooterSize));
        bw.Write(crc); bw.Write(MagicEnd);
        return ms.ToArray();
    }

    /// <summary>
    /// 从 Kylin 块体模型直接写出（导出块体「全部块」走这条）：模型自己的网格 + 全属性 + 属性表 + 样式 + 已删集合 + 分类元数据。
    /// scope 非空 = 只导这批块（Kylin 的「当前选择集 / 属性表达式」范围），此时按子集重建网格、不写已删段。
    /// attrNames 非空 = 只导这些属性列。
    /// </summary>
    public static byte[] FromModel(BlockModelMeta m, IReadOnlyList<int>? scope = null, IReadOnlyList<string>? attrNames = null)
    {
        if (m == null) throw new ArgumentNullException(nameof(m));
        var cols = attrNames == null
            ? new List<BlockPropertyColumn>(m.PropertySchema)
            : m.PropertySchema.FindAll(c => { foreach (var n in attrNames) if (n == c.Name) return true; return false; });
        var meta = new ModelMeta
        {
            Name = m.Name, Description = m.Description, RotationZDeg = m.RotationZDeg,
            StorageMode = m.StorageMode, SubBlockDepthMax = m.SubBlockDepthMax,
            SubMinX = m.SubMinX, SubMinY = m.SubMinY, SubMinZ = m.SubMinZ,
            Schema = cols, Style = m.DisplayStyle,
            ActiveColormapAttribute = cols.Exists(c => c.Name == m.ActiveColormapAttribute) ? m.ActiveColormapAttribute : null,
        };

        bool whole = scope == null && m.IsRegular && m.Blocks.Count == (long)m.Nx * m.Ny * m.Nz;
        if (whole)
        {
            // 模型本身就是 x-fastest 规则网格 → 原样落盘，块序即线性下标，已删集合可完整保留
            var g = new Grid(m.Ox, m.Oy, m.Oz, m.Sx, m.Sy, m.Sz, m.Nx, m.Ny, m.Nz);
            var list = new List<(string name, double[] values)>(cols.Count);
            foreach (var c in cols)
            {
                var arr = new double[m.Blocks.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = m.GetValue(c.Name, i);
                list.Add((c.Name, arr));
            }
            foreach (var id in m.DeletedIds) meta.DeletedCells.Add(id);
            return ToBytes(g, list, meta);
        }

        // 子集 / 非规则：按块中心重建网格（同 FromBlocks），已删块本就不在范围内，故不写已删段
        var idx = scope ?? BuildAllIndices(m);
        var blocks = new List<BlockModel.Block>(idx.Count);
        var sub = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var c in cols) sub[c.Name] = new double[idx.Count];
        for (int n = 0; n < idx.Count; n++)
        {
            var b = m.Blocks[idx[n]];
            b.Size = m.Sx;
            blocks.Add(b);
            foreach (var c in cols) sub[c.Name][n] = m.GetValue(c.Name, idx[n]);
        }
        var (grid, gridAttrs) = FromBlocks(blocks, sub);
        // FromBlocks 恒插一列 grade（块体自带品位）；属性表里没有同名列时剔掉，免得 schema 与 Blocks 段对不上
        if (!cols.Exists(c => string.Equals(c.Name, "grade", StringComparison.OrdinalIgnoreCase)))
            gridAttrs.RemoveAll(a => string.Equals(a.Item1, "grade", StringComparison.OrdinalIgnoreCase));
        return ToBytes(grid, gridAttrs, meta);
    }

    private static List<int> BuildAllIndices(BlockModelMeta m)
    {
        var all = new List<int>(m.Blocks.Count);
        for (int i = 0; i < m.Blocks.Count; i++) if (!m.DeletedIds.Contains(i)) all.Add(i);
        return all;
    }

    /// <summary>
    /// 从 Kylin 块体列表重建规则网格 + 属性 x-fastest 数组(按各块中心算 i/j/k 归位, 与输入顺序无关)。
    /// extraAttrs 为并行于 blocks 的全属性(名→逐块值, 同 BLK/PMB 导入序); 恒含 "grade"。
    /// </summary>
    public static (Grid grid, List<(string name, double[] values)> attrs) FromBlocks(
        IReadOnlyList<BlockModel.Block> blocks, IReadOnlyDictionary<string, double[]>? extraAttrs = null)
    {
        if (blocks == null || blocks.Count == 0) throw new ArgumentException("无块体");
        double size = blocks[0].Size > 0 ? blocks[0].Size : 1;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var b in blocks)
        {
            if (b.X < minX) minX = b.X; if (b.X > maxX) maxX = b.X;
            if (b.Y < minY) minY = b.Y; if (b.Y > maxY) maxY = b.Y;
            if (b.Z < minZ) minZ = b.Z; if (b.Z > maxZ) maxZ = b.Z;
        }
        int nx = (int)Math.Round((maxX - minX) / size) + 1;
        int ny = (int)Math.Round((maxY - minY) / size) + 1;
        int nz = (int)Math.Round((maxZ - minZ) / size) + 1;
        nx = Math.Max(1, nx); ny = Math.Max(1, ny); nz = Math.Max(1, nz);
        long nxy = (long)nx * ny, blockCount = nxy * nz;
        var g = new Grid(minX - size / 2, minY - size / 2, minZ - size / 2, size, size, size, nx, ny, nz);

        int Idx(BlockModel.Block b)
        {
            int i = (int)Math.Round((b.X - minX) / size);
            int j = (int)Math.Round((b.Y - minY) / size);
            int k = (int)Math.Round((b.Z - minZ) / size);
            i = Math.Clamp(i, 0, nx - 1); j = Math.Clamp(j, 0, ny - 1); k = Math.Clamp(k, 0, nz - 1);
            return (int)(k * nxy + j * nx + i);
        }

        var attrs = new List<(string, double[])>();
        // grade 恒有(从块体投影)
        var grade = new double[blockCount];
        for (int b = 0; b < blocks.Count; b++) grade[Idx(blocks[b])] = blocks[b].Grade;
        attrs.Add(("grade", grade));
        // 额外全属性(并行 blocks 序 → 归位到网格序)
        if (extraAttrs != null)
            foreach (var kv in extraAttrs)
            {
                if (string.Equals(kv.Key, "grade", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Value.Length != blocks.Count) continue;   // 长度不符则跳(稳健)
                var arr = new double[blockCount];
                for (int b = 0; b < blocks.Count; b++) arr[Idx(blocks[b])] = kv.Value[b];
                attrs.Add((kv.Key, arr));
            }
        return (g, attrs);
    }

    // ── 段序列化 ──
    private static byte[] SerializeStrings(List<string> strings)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((uint)strings.Count);
        foreach (var s in strings)
        {
            var b = Encoding.UTF8.GetBytes(s);
            if (b.Length < 0xFFFF) bw.Write((ushort)b.Length);
            else { bw.Write((ushort)0xFFFF); bw.Write((uint)b.Length); }
            bw.Write(b);
        }
        return ms.ToArray();
    }

    private static byte[] SerializeGridSpec(Grid g, int nameIdx, int descIdx, ModelMeta meta)
    {
        using var ms = new MemoryStream(96);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(nameIdx); bw.Write(descIdx);
        bw.Write(g.Ox); bw.Write(g.Oy); bw.Write(g.Oz);
        bw.Write(g.Sx); bw.Write(g.Sy); bw.Write(g.Sz);
        bw.Write(g.Nx); bw.Write(g.Ny); bw.Write(g.Nz);
        bw.Write(meta.RotationZDeg * Math.PI / 180.0);                // rotationZ: 度→弧度(同原版)
        bw.Write((byte)meta.StorageMode);
        bw.Write((byte)Math.Clamp(meta.SubBlockDepthMax, 0, 255));
        bw.Write((float)meta.SubMinX); bw.Write((float)meta.SubMinY); bw.Write((float)meta.SubMinZ);
        bw.Write((ushort)0);      // reserved
        return ms.ToArray();
    }

    /// <summary>PropertySchema(11)：列名/类型/编码/单位/默认值/备注/flags(bit0=分类 bit1=隐藏)。</summary>
    private static byte[] SerializePropertySchema(List<BlockPropertyColumn> schema, Func<string?, int> str)
    {
        using var ms = new MemoryStream(schema.Count * 32 + 8);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((uint)schema.Count);
        foreach (var col in schema)
        {
            bw.Write(str(col.Name));
            bw.Write((byte)col.DataType);
            bw.Write((byte)0);              // encoding 0=Raw
            bw.Write((ushort)0);            // reserved
            bw.Write(str(col.Unit));
            bw.Write(col.DefaultValue);
            bw.Write(str(col.Description));
            bw.Write((byte)(col.IsCategorical ? 0x01 : 0x00));
            bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0);   // reserved
        }
        return ms.ToArray();
    }

    /// <summary>DisplayStyle(16)：填充/边线 ARGB + 边线模式 + 默认色带 + 边宽 + v1.4 活动着色属性 strIdx。</summary>
    private static byte[] SerializeDisplayStyle(BlockDisplayStyle ds, int activeAttrStrIdx)
    {
        using var ms = new MemoryStream(24);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(Argb(ds.FillColor));
        bw.Write(Argb(ds.EdgeColor));
        bw.Write((byte)ds.EdgeMode);
        bw.Write((byte)ds.DefaultColormap);
        bw.Write((ushort)0);   // reserved
        bw.Write(ds.EdgeWidthPx);
        bw.Write(activeAttrStrIdx);
        return ms.ToArray();
    }

    /// <summary>DeletedCells(17)：升序 dump 线性下标(int64 × N)，同原版。</summary>
    private static byte[] SerializeDeletedCells(HashSet<long> deleted)
    {
        using var ms = new MemoryStream(8 + deleted.Count * 8);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((uint)deleted.Count);
        var arr = new long[deleted.Count];
        deleted.CopyTo(arr);
        Array.Sort(arr);
        foreach (var idx in arr) bw.Write(idx);
        return ms.ToArray();
    }

    /// <summary>
    /// CategoryMeta(19)：分类属性的【码→名】+【逐类颜色】，按列名关联。
    /// 不写这段 = 无分类元数据（岩性/矿岩类型导出后只剩裸码 + 单色就是丢了这段）。
    /// </summary>
    private static byte[]? SerializeCategoryMeta(ModelMeta meta, Func<string?, int> str)
    {
        var entries = new List<(int nameIdx, IReadOnlyList<string>? labels, Dictionary<int, (byte r, byte g, byte b)>? colors)>();
        foreach (var col in meta.Schema)
        {
            var labels = col.CategoryLabels;
            Dictionary<int, (byte r, byte g, byte b)>? colors = null;
            meta.Style?.CategoryColors.TryGetValue(col.Name, out colors);
            bool hasLabels = labels is { Count: > 0 };
            bool hasColors = colors is { Count: > 0 };
            if (!hasLabels && !hasColors) continue;
            entries.Add((str(col.Name), hasLabels ? labels : null, hasColors ? colors : null));
        }
        if (entries.Count == 0) return null;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((uint)entries.Count);
        foreach (var e in entries)
        {
            bw.Write(e.nameIdx);
            bw.Write((uint)(e.labels?.Count ?? 0));
            if (e.labels != null) foreach (var l in e.labels) bw.Write(str(l));
            bw.Write((uint)(e.colors?.Count ?? 0));
            if (e.colors != null) foreach (var kv in e.colors) { bw.Write(kv.Key); bw.Write(Argb(kv.Value)); }
        }
        return ms.ToArray();
    }

    private static uint Argb((byte r, byte g, byte b) c) => 0xFF000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;

    private static byte[] SerializeBlocks(long blockCount, IReadOnlyList<(string name, double[] values)> attrs, int[] attrNameIdx)
    {
        using var ms = new MemoryStream((int)Math.Min(blockCount * attrs.Count * 8 + 256, int.MaxValue));
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0);   // storageMode(0=Dense) + 3B reserved
        bw.Write(blockCount);            // int64
        bw.Write((uint)attrs.Count);
        for (int a = 0; a < attrs.Count; a++)
        {
            bw.Write(attrNameIdx[a]);    // nameStrIdx int32
            bw.Write((byte)0);           // dataType 0=Float(读端按 double 读)
            bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0);   // 3B reserved
            bw.Write((uint)blockCount);  // valueCount
            var v = attrs[a].values;
            for (long i = 0; i < blockCount; i++) bw.Write(v[i]);
        }
        return ms.ToArray();
    }

    private static void WriteSectionEntry(BinaryWriter bw, uint id, long offset, long size)
    {
        bw.Write(id); bw.Write((uint)0); bw.Write(offset); bw.Write(size);
    }

    /// <summary>IEEE 802.3 / zlib CRC32(多项式 0xEDB88320), 覆盖 data[0,len)。</summary>
    private static uint Crc32(byte[] data, int len)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < len; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
