using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// PMB(PitMine 块体模型 v1)导出 —— 忠实移植原 `PmbmWriter`/`PmbmFormat` 的公开二进制布局(源码可见):
/// Header(32B 'PMB1') + 段表(24B/项) + Strings(名池) + GridSpec(全字段: origin/blockSize/dims/rotation/storageMode/subblock)
/// + Blocks(Dense, 属性存 double 数组, x-fastest) + Footer(activeCount + IEEE CRC32 + 'PMB1' 逆序 magicEnd)。
/// 与 <see cref="PmbImportService"/> 成读写对: 写→读往返还原网格+全属性(最强验证)。
/// Kylin grade-only 数据模型无 PropertySchema(11)/DisplayStyle(16) 可忠实填, 故略去该二段(读端不需; 写臆造默认反不忠实)——记录此限。
/// 纯逻辑、可单测。
/// </summary>
public static class PmbExportService
{
    public const uint MagicStart = 0x31424D50;   // 'P','M','B','1'
    public const uint MagicEnd = 0x504D4231;     // '1','B','M','P' (LE)
    public const uint Version = 1;
    public const int HeaderSize = 32, SectionEntrySize = 24, FooterSize = 16;
    public const uint SectionStrings = 1, SectionGridSpec = 10, SectionBlocks = 12;

    /// <summary>规则网格规格(原点=最小角, 块尺寸, 维度)。</summary>
    public readonly record struct Grid(double Ox, double Oy, double Oz, double Sx, double Sy, double Sz, int Nx, int Ny, int Nz);

    /// <summary>
    /// grid + 属性(名, x-fastest 值数组, 长度须 = Nx·Ny·Nz) → PMB v1 字节。modelName 入 Strings 名池。
    /// 属性可空(仅写网格几何, Blocks 段省略)。
    /// </summary>
    public static byte[] ToBytes(Grid g, IReadOnlyList<(string name, double[] values)>? attrs, string modelName = "block_model")
    {
        long blockCount = (long)g.Nx * g.Ny * g.Nz;
        if (g.Nx <= 0 || g.Ny <= 0 || g.Nz <= 0) throw new ArgumentException("网格维度须为正");
        attrs ??= new List<(string, double[])>();
        foreach (var (nm, v) in attrs)
            if (v.Length != blockCount) throw new ArgumentException($"属性 {nm} 值数 {v.Length} ≠ 块数 {blockCount}");

        // ── Strings 名池: [modelName, "", attr 名...] ──
        var strings = new List<string> { modelName, "" };
        var attrNameIdx = new int[attrs.Count];
        for (int a = 0; a < attrs.Count; a++) { attrNameIdx[a] = strings.Count; strings.Add(attrs[a].name); }

        byte[] stringsSeg = SerializeStrings(strings);
        byte[] gridSeg = SerializeGridSpec(g, nameIdx: 0, descIdx: 1);
        byte[]? blocksSeg = attrs.Count > 0 ? SerializeBlocks(blockCount, attrs, attrNameIdx) : null;

        int sectionCount = 2 + (blocksSeg != null ? 1 : 0);   // Strings + GridSpec (+ Blocks)
        int sectionTableSize = sectionCount * SectionEntrySize;
        long dataOffset = HeaderSize + sectionTableSize;
        long offStrings = dataOffset;
        long offGrid = offStrings + stringsSeg.Length;
        long offBlocks = offGrid + gridSeg.Length;
        long endOfData = offGrid + gridSeg.Length + (blocksSeg?.Length ?? 0);
        long fileSize = endOfData + FooterSize;

        using var ms = new MemoryStream((int)fileSize);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        // Header (32B)
        bw.Write(MagicStart); bw.Write(Version); bw.Write((uint)0);
        bw.Write(fileSize); bw.Write((long)HeaderSize); bw.Write((uint)sectionCount);
        // SectionTable
        WriteSectionEntry(bw, SectionStrings, offStrings, stringsSeg.Length);
        WriteSectionEntry(bw, SectionGridSpec, offGrid, gridSeg.Length);
        if (blocksSeg != null) WriteSectionEntry(bw, SectionBlocks, offBlocks, blocksSeg.Length);
        // Section data
        bw.Write(stringsSeg); bw.Write(gridSeg);
        if (blocksSeg != null) bw.Write(blocksSeg);
        // Footer: activeBlockCount(8) + crc32(4) + magicEnd(4); crc 覆盖 [0, fileSize-16)
        bw.Write((long)0);
        bw.Flush();
        var fileBytes = ms.ToArray();
        uint crc = Crc32(fileBytes, (int)(fileSize - FooterSize));
        bw.Write(crc); bw.Write(MagicEnd);
        return ms.ToArray();
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

    private static byte[] SerializeGridSpec(Grid g, int nameIdx, int descIdx)
    {
        using var ms = new MemoryStream(96);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(nameIdx); bw.Write(descIdx);
        bw.Write(g.Ox); bw.Write(g.Oy); bw.Write(g.Oz);
        bw.Write(g.Sx); bw.Write(g.Sy); bw.Write(g.Sz);
        bw.Write(g.Nx); bw.Write(g.Ny); bw.Write(g.Nz);
        bw.Write(0.0);            // rotationZ (弧度)
        bw.Write((byte)0);        // storageMode 0=Dense
        bw.Write((byte)0);        // subBlockDepthMax
        bw.Write((float)0); bw.Write((float)0); bw.Write((float)0);   // subMinSize
        bw.Write((ushort)0);      // reserved
        return ms.ToArray();
    }

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
