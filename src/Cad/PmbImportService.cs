using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// PMB(PitMine 块体模型 v1)导入 —— 忠实移植原 `PmbmReader`/`PmbmFormat` 的公开二进制格式(源码可见, 非专有黑盒):
/// Header(32B magic 'PMB1') + 段表(24B/项) + GridSpec(origin/blockSize/dims) + Blocks(Dense, 属性存为 double 数组) + Footer。
/// 取几何(网格→cell 中心)+首属性作品位, 入 grade-only 块模型(多属性→单属性=数据模型限, 记录)。宽容读取(跳 CRC)。可单测。
/// </summary>
public static class PmbImportService
{
    public const uint MagicStart = 0x31424D50;   // 'P','M','B','1'
    public const int HeaderSize = 32, SectionEntrySize = 24, FooterSize = 16;
    public const uint SectionGridSpec = 10, SectionBlocks = 12;

    public sealed class Result
    {
        public bool Success; public string Error = "";
        public List<BlockModel.Block> Blocks = new();
        public int Nx, Ny, Nz;
    }

    public static Result Load(string path)
    {
        try { return Parse(File.ReadAllBytes(path)); }
        catch (System.Exception ex) { return new Result { Error = ex.Message }; }
    }

    public static Result Parse(byte[] bytes)
    {
        var r = new Result();
        if (bytes == null || bytes.Length < HeaderSize + FooterSize) { r.Error = "PMB 过小"; return r; }
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        if (br.ReadUInt32() != MagicStart) { r.Error = "PMB magic 不匹配"; return r; }
        br.ReadUInt32();                              // version
        br.ReadUInt32();                              // flags
        long fileSize = br.ReadInt64();
        long stOff = br.ReadInt64();
        uint sectionCount = br.ReadUInt32();
        if (stOff != HeaderSize || sectionCount > 64) { r.Error = "PMB 头非法"; return r; }

        long gridOff = -1, blocksOff = -1;
        ms.Position = stOff;
        for (int i = 0; i < sectionCount; i++)
        {
            uint id = br.ReadUInt32(); br.ReadUInt32();   // id, flags
            long off = br.ReadInt64(); br.ReadInt64();    // offset, size
            if (id == SectionGridSpec) gridOff = off;
            else if (id == SectionBlocks) blocksOff = off;
        }
        if (gridOff < 0) { r.Error = "PMB 缺 GridSpec 段"; return r; }

        // ── GridSpec ──
        ms.Position = gridOff;
        br.ReadInt32(); br.ReadInt32();               // nameIdx, descIdx
        double ox = br.ReadDouble(), oy = br.ReadDouble(), oz = br.ReadDouble();
        double bsx = br.ReadDouble(), bsy = br.ReadDouble(), bsz = br.ReadDouble();
        int nx = br.ReadInt32(), ny = br.ReadInt32(), nz = br.ReadInt32();
        r.Nx = nx; r.Ny = ny; r.Nz = nz;
        long blockCount = (long)nx * ny * nz;
        if (nx <= 0 || ny <= 0 || nz <= 0 || blockCount > 50_000_000) { r.Error = "PMB 网格维度非法"; return r; }

        // ── Blocks 首属性作品位(可无) ──
        double[]? grade = null;
        if (blocksOff >= 0)
        {
            ms.Position = blocksOff;
            byte storageMode = br.ReadByte(); br.ReadBytes(3);   // reserved
            long bc = br.ReadInt64(); uint attrCount = br.ReadUInt32();
            if (storageMode == 0 && bc == blockCount && attrCount > 0)   // Dense
            {
                br.ReadInt32(); br.ReadByte(); br.ReadBytes(3);          // nameIdx, dataType, reserved(首属性)
                uint valueCount = br.ReadUInt32();
                if (valueCount == blockCount)
                {
                    grade = new double[blockCount];
                    for (long i = 0; i < blockCount; i++) grade[i] = br.ReadDouble();
                }
            }
        }

        double size = System.Math.Min(bsx, System.Math.Min(bsy, bsz));   // 标量尺寸(近似非立方)
        long nxy = (long)nx * ny;
        for (long idx = 0; idx < blockCount; idx++)
        {
            long k = idx / nxy, rem = idx % nxy, j = rem / nx, i = rem % nx;   // x-fastest(忠实原索引序)
            r.Blocks.Add(new BlockModel.Block
            {
                X = ox + (i + 0.5) * bsx,
                Y = oy + (j + 0.5) * bsy,
                Z = oz + (k + 0.5) * bsz,
                Size = size,
                Grade = grade != null ? grade[idx] : 0,
            });
        }
        r.Success = true;
        return r;
    }
}
