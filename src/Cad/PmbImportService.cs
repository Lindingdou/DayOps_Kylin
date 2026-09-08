using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// PMB(PitMine 块体模型 v1)导入 —— 忠实移植原 `PmbmReader`/`PmbmFormat` 的公开二进制格式(源码可见, 非专有黑盒):
/// Header(32B magic 'PMB1') + 段表(24B/项) + GridSpec(origin/blockSize/dims/rotation/storage/subblock) + PropertySchema(11)
/// + Blocks(12, Dense, 属性存为 double 数组) + DisplayStyle(16) + DeletedCells(17) + CategoryMeta(19) + Footer。
/// 取几何(网格→cell 中心)+选定/首属性作品位; 全属性留存 AllAttrs(供 切换属性/属性赋值, 同 BLK);
/// 属性表/显示样式/已删集合/分类码名与配色随文件回读(与 <see cref="PmbExportService"/> 成往返)。宽容读取(跳 CRC)。可单测。
/// </summary>
public static class PmbImportService
{
    public const uint MagicStart = 0x31424D50;   // 'P','M','B','1'
    public const int HeaderSize = 32, SectionEntrySize = 24, FooterSize = 16;
    public const uint SectionStrings = 1, SectionGridSpec = 10, SectionPropertySchema = 11, SectionBlocks = 12;
    public const uint SectionDisplayStyle = 16, SectionDeletedCells = 17, SectionCategoryMeta = 19;

    public sealed class Result
    {
        public bool Success; public string Error = "";
        public List<BlockModel.Block> Blocks = new();
        public int Nx, Ny, Nz;
        public List<string> AttrNames = new();
        public string UsedAttr = "";
        public Dictionary<string, double[]> AllAttrs = new();   // 全属性逐块值(供无重导切换活动属性/属性报告, 同 BLK)

        // ── 随文件回读的模型元数据(段 10 尾段字段 + 11/16/17/19) ──
        public string ModelName = "";
        public string Description = "";
        public double RotationZDeg;
        public BlockStorageMode StorageMode = BlockStorageMode.Dense;
        public int SubBlockDepthMax;
        public double SubMinX, SubMinY, SubMinZ;
        /// <summary>属性表(列名/类型/单位/默认值/备注/是否分类 + 分类码名表)。文件无该段则为空。</summary>
        public List<BlockPropertyColumn> Schema = new();
        /// <summary>显示样式(填充/边线/边宽/默认色带 + 逐类颜色)。文件无该段则 null。</summary>
        public BlockDisplayStyle? Style;
        /// <summary>活动着色属性(DisplayStyle 段 v1.4 字段)。</summary>
        public string? ActiveColormapAttribute;
        /// <summary>已删 cell 线性下标。</summary>
        public HashSet<long> DeletedCells = new();
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

        long gridOff = -1, blocksOff = -1, stringsOff = -1, schemaOff = -1, styleOff = -1, styleSize = 0, deletedOff = -1, catOff = -1;
        ms.Position = stOff;
        for (int i = 0; i < sectionCount; i++)
        {
            uint id = br.ReadUInt32(); br.ReadUInt32();   // id, flags
            long off = br.ReadInt64(); long sz = br.ReadInt64();
            if (id == SectionGridSpec) gridOff = off;
            else if (id == SectionBlocks) blocksOff = off;
            else if (id == SectionStrings) stringsOff = off;
            else if (id == SectionPropertySchema) schemaOff = off;
            else if (id == SectionDisplayStyle) { styleOff = off; styleSize = sz; }
            else if (id == SectionDeletedCells) deletedOff = off;
            else if (id == SectionCategoryMeta) catOff = off;
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
        int gridNameIdx = br.ReadInt32(), gridDescIdx = br.ReadInt32();
        double ox = br.ReadDouble(), oy = br.ReadDouble(), oz = br.ReadDouble();
        double bsx = br.ReadDouble(), bsy = br.ReadDouble(), bsz = br.ReadDouble();
        int nx = br.ReadInt32(), ny = br.ReadInt32(), nz = br.ReadInt32();
        r.Nx = nx; r.Ny = ny; r.Nz = nz;
        r.ModelName = StringAt(strings, gridNameIdx);
        r.Description = StringAt(strings, gridDescIdx);
        // 段尾: rotationZ(弧度→度) + storageMode + subBlockDepthMax + subMinSize(float×3)。旧文件可能没写全, 读不到就用默认。
        try
        {
            r.RotationZDeg = br.ReadDouble() * 180.0 / System.Math.PI;
            r.StorageMode = (BlockStorageMode)br.ReadByte();
            r.SubBlockDepthMax = br.ReadByte();
            r.SubMinX = br.ReadSingle(); r.SubMinY = br.ReadSingle(); r.SubMinZ = br.ReadSingle();
        }
        catch (EndOfStreamException) { }
        long blockCount = (long)nx * ny * nz;
        if (nx <= 0 || ny <= 0 || nz <= 0 || blockCount > 50_000_000) { r.Error = "PMB 网格维度非法"; return r; }

        // ── PropertySchema(11) ──
        if (schemaOff >= 0)
        {
            ms.Position = schemaOff;
            uint colCount = br.ReadUInt32();
            for (int i = 0; i < colCount && i < 4096; i++)
            {
                int cnIdx = br.ReadInt32();
                var type = (BlockPropertyType)br.ReadByte();
                br.ReadByte(); br.ReadUInt16();            // encoding + reserved
                int unitIdx = br.ReadInt32();
                double defVal = br.ReadDouble();
                int dscIdx = br.ReadInt32();
                byte flags = br.ReadByte();
                br.ReadByte(); br.ReadByte(); br.ReadByte();   // reserved
                r.Schema.Add(new BlockPropertyColumn
                {
                    Name = StringAt(strings, cnIdx), DataType = type, Unit = StringAt(strings, unitIdx),
                    DefaultValue = defVal, Description = StringAt(strings, dscIdx), IsCategorical = (flags & 0x01) != 0,
                });
            }
        }

        // ── DisplayStyle(16)：v1.4 起末尾多 4B activeAttrStrIdx（段长 ≥24 才有） ──
        if (styleOff >= 0)
        {
            ms.Position = styleOff;
            uint fill = br.ReadUInt32(), edge = br.ReadUInt32();
            var em = (BlockEdgeMode)br.ReadByte();
            var cm = (BlockColormapPreset)br.ReadByte();
            br.ReadUInt16();   // reserved
            double edgeWidth = br.ReadDouble();
            int activeIdx = styleSize >= 24 ? br.ReadInt32() : -1;
            r.Style = new BlockDisplayStyle
            {
                FillColor = FromArgb(fill), EdgeColor = FromArgb(edge), EdgeMode = em,
                DefaultColormap = cm, EdgeWidthPx = edgeWidth > 0 ? edgeWidth : 1.0,
            };
            if (activeIdx >= 0) r.ActiveColormapAttribute = StringAt(strings, activeIdx);
        }

        // ── DeletedCells(17) ──
        if (deletedOff >= 0)
        {
            ms.Position = deletedOff;
            uint dc = br.ReadUInt32();
            for (int i = 0; i < dc && i < 50_000_000; i++)
            {
                if (ms.Position + 8 > bytes.Length) break;
                r.DeletedCells.Add(br.ReadInt64());
            }
        }

        // ── CategoryMeta(19)：分类属性的码→名 + 逐类颜色，按列名关联 ──
        if (catOff >= 0)
        {
            ms.Position = catOff;
            uint entryCount = br.ReadUInt32();
            for (int e = 0; e < entryCount && e < 4096; e++)
            {
                string colName = StringAt(strings, br.ReadInt32());
                uint labelCount = br.ReadUInt32();
                if (labelCount > 65536) break;
                var labels = new List<string>((int)labelCount);
                for (int i = 0; i < labelCount; i++) labels.Add(StringAt(strings, br.ReadInt32()));
                uint colorCount = br.ReadUInt32();
                if (colorCount > 65536) break;
                var colors = new List<(int code, uint argb)>((int)colorCount);
                for (int i = 0; i < colorCount; i++) colors.Add((br.ReadInt32(), br.ReadUInt32()));
                // 先整段吃完再落地：列名对不上也不能错位后续条目
                var col = r.Schema.Find(c => c.Name == colName);
                if (col == null) continue;
                if (labels.Count > 0) col.CategoryLabels = labels;
                if (colors.Count > 0)
                {
                    r.Style ??= new BlockDisplayStyle();
                    var map = r.Style.EnsureCategoryColors(col.Name);
                    foreach (var c in colors) map[c.code] = FromArgb(c.argb);
                }
            }
        }

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

    private static string StringAt(List<string> strings, int idx) => idx >= 0 && idx < strings.Count ? strings[idx] : "";

    private static (byte r, byte g, byte b) FromArgb(uint argb) => ((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
}
