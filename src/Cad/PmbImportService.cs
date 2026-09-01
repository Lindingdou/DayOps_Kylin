using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// PMB(PitMine 块体模型 v1)导入 —— 忠实移植原 `PmbmReader`/`PmbmFormat` 的公开二进制格式(源码可见, 非专有黑盒):
/// Header(32B magic 'PMB1') + 段表(24B/项) + GridSpec(origin/blockSize/dims) + Blocks(Dense, 属性存为 double 数组) + Footer。
/// 取几何(网格→cell 中心)+选定/首属性作品位; 全属性留存 AllAttrs(供 切换属性/属性赋值, 同 BLK)。宽容读取(跳 CRC)。可单测。
/// </summary>
public static class PmbImportService
{
    public const uint MagicStart = 0x31424D50;   // 'P','M','B','1'
    public const int HeaderSize = 32, SectionEntrySize = 24, FooterSize = 16;
    public const uint SectionStrings = 1, SectionGridSpec = 10, SectionBlocks = 12;

    public sealed class Result
    {
        public bool Success; public string Error = "";
        public List<BlockModel.Block> Blocks = new();
        public int Nx, Ny, Nz;
        public List<string> AttrNames = new();
        public string UsedAttr = "";
        public Dictionary<string, double[]> AllAttrs = new();   // 全属性逐块值(供无重导切换活动属性/属性报告, 同 BLK)
    }

    public static Result Load(string path, string? selectAttr = null)
    {
        try { return Parse(File.ReadAllBytes(path), selectAttr); }
        catch (System.Exception ex) { return new Result { Error = ex.Message }; }
    }

    /// <summary>selectAttr 非空则取该名属性作品位(找不到回落首属性)。</summary>
    public static Result Parse(byte[] bytes, string? selectAttr = null)
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

        long gridOff = -1, blocksOff = -1, stringsOff = -1;
        ms.Position = stOff;
        for (int i = 0; i < sectionCount; i++)
        {
            uint id = br.ReadUInt32(); br.ReadUInt32();   // id, flags
            long off = br.ReadInt64(); br.ReadInt64();    // offset, size
            if (id == SectionGridSpec) gridOff = off;
            else if (id == SectionBlocks) blocksOff = off;
            else if (id == SectionStrings) stringsOff = off;
        }
        if (gridOff < 0) { r.Error = "PMB 缺 GridSpec 段"; return r; }

        // ── Strings(属性名池) ──
        var strings = new List<string>();
        if (stringsOff >= 0)
        {
            ms.Position = stringsOff;
            uint sc = br.ReadUInt32();
            for (int i = 0; i < sc && i < 1_000_000; i++)
            {
                ushort len16 = br.ReadUInt16();
                int len = len16 == 0xFFFF ? br.ReadInt32() : len16;
                if (len < 0 || ms.Position + len > bytes.Length) break;
                strings.Add(System.Text.Encoding.UTF8.GetString(br.ReadBytes(len)));
            }
        }

        // ── GridSpec ──
        ms.Position = gridOff;
        br.ReadInt32(); br.ReadInt32();               // nameIdx, descIdx
        double ox = br.ReadDouble(), oy = br.ReadDouble(), oz = br.ReadDouble();
        double bsx = br.ReadDouble(), bsy = br.ReadDouble(), bsz = br.ReadDouble();
        int nx = br.ReadInt32(), ny = br.ReadInt32(), nz = br.ReadInt32();
        r.Nx = nx; r.Ny = ny; r.Nz = nz;
        long blockCount = (long)nx * ny * nz;
        if (nx <= 0 || ny <= 0 || nz <= 0 || blockCount > 50_000_000) { r.Error = "PMB 网格维度非法"; return r; }

        // ── Blocks: 一遍扫全属性(名+值偏移), 再取选定(或首)作品位 ──
        double[]? grade = null;
        if (blocksOff >= 0)
        {
            ms.Position = blocksOff;
            byte storageMode = br.ReadByte(); br.ReadBytes(3);   // reserved
            long bc = br.ReadInt64(); uint attrCount = br.ReadUInt32();
            if (storageMode == 0 && bc == blockCount && attrCount > 0 && attrCount <= 4096)   // Dense
            {
                var attrValueOff = new List<long>();
                int validAttrs = 0;
                for (int a = 0; a < attrCount; a++)
                {
                    int nameIdx = br.ReadInt32(); br.ReadByte(); br.ReadBytes(3);   // nameIdx, dataType, reserved
                    uint valueCount = br.ReadUInt32();
                    string name = (nameIdx >= 0 && nameIdx < strings.Count) ? strings[nameIdx] : $"attr{a}";
                    r.AttrNames.Add(name);
                    attrValueOff.Add(ms.Position);
                    if (valueCount != blockCount) break;                            // 布局异常, 止
                    validAttrs++;
                    ms.Position += (long)valueCount * 8;                            // 跳到下一属性
                }
                int selIdx = 0;
                if (selectAttr != null)
                { int f = r.AttrNames.FindIndex(nm => string.Equals(nm, selectAttr, System.StringComparison.OrdinalIgnoreCase)); if (f >= 0 && f < validAttrs) selIdx = f; }
                if (validAttrs > 0)
                {
                    // 读全部有效属性 → AllAttrs(供无重导切换/属性报告, 同 BLK); 巨模型(值数>50M)跳过保内存, 仅读选定作品位。
                    bool keepAll = (long)validAttrs * blockCount <= 50_000_000;
                    if (keepAll)
                        for (int a = 0; a < validAttrs; a++)
                        {
                            ms.Position = attrValueOff[a];
                            var vals = new double[blockCount];
                            for (long i = 0; i < blockCount; i++) vals[i] = br.ReadDouble();
                            r.AllAttrs[r.AttrNames[a]] = vals;
                        }
                    if (keepAll) grade = r.AllAttrs[r.AttrNames[selIdx]];
                    else
                    {
                        ms.Position = attrValueOff[selIdx];
                        grade = new double[blockCount];
                        for (long i = 0; i < blockCount; i++) grade[i] = br.ReadDouble();
                    }
                    r.UsedAttr = r.AttrNames[selIdx];
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
