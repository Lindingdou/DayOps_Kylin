using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// MapGIS 6.x .WL(线) / .WT(点注记) 读取器 —— 忠实移植原 MapGisWlReader/MapGisWtReader
/// (逆向文档化的二进制格式, 详见原 MapGIS_WL_Format.md / MapGisWtReader 头注)。
/// 产出可编辑 <see cref="PolylineEntity"/>(线, 含等高线 Z 注入) / <see cref="TextEntity"/>(注记),
/// 复用 <see cref="DxfImportService.EntityImportResult"/> 走既有可编辑导入通道。纯字节解析, 可单测。
/// </summary>
public static class MapGisImportService
{
    public const string ExpectedMagic = "WMAP`D2";
    public const byte WlSubtype = (byte)'1';
    public const byte WtSubtype = (byte)'2';
    public const byte WpSubtype = (byte)'3';

    /// <summary>MapGIS 内部 color ID → RGB(0xRRGGBB)。移自原 MapGisWlReader.MapGisColorTable(实测 1:1)。</summary>
    private static readonly int[] ColorRgb = new int[256];

    static MapGisImportService()
    {
        for (int i = 0; i < 256; i++) ColorRgb[i] = 0x000000;   // 默认黑
        ColorRgb[1] = 0x000000;  ColorRgb[2] = 0x00FFFF;  ColorRgb[3] = 0xFF00FF;  ColorRgb[5] = 0x0000FF;
        ColorRgb[6] = 0xFF0000;  ColorRgb[7] = 0x00FF00;  ColorRgb[49] = 0x999900; ColorRgb[90] = 0x00AF00;
        ColorRgb[148] = 0xBF8F00; ColorRgb[200] = 0xBF00DF;
    }

    private static (float r, float g, float b) Rgb(byte colorId)
    {
        int rgb = ColorRgb[colorId];
        // MapGIS 色多为深色, 深黑在深底不可见 → 纯黑(未映射)提亮为浅灰, 保持可见(与导入实体默认色一致意图)
        if (rgb == 0x000000) return (0.86f, 0.9f, 0.6f);
        return (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    }

    /// <summary>按扩展名分派 .wl/.wt。</summary>
    public static DxfImportService.EntityImportResult Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wl" => LoadWl(path),
            ".wt" => LoadWt(path),
            ".wp" => LoadWp(path),
            ".mpj" => LoadProject(path),
            _ => new DxfImportService.EntityImportResult { Error = $"不支持的 MapGIS 格式：{ext}（支持 .wl/.wt/.wp/.mpj）" }
        };
    }

    /// <summary>MPJ 工程成员引用（忠实 MapGisProjectReader）。</summary>
    public readonly record struct ProjectLayer(string FileName, string FullPath, string Ext);

    /// <summary>解析 MapGIS .mpj 工程 → 成员 WL/WT/WP 文件清单（正则抓 ".\\xxx.WL" 形式, 去重, 相对 mpj 目录解析）。</summary>
    public static IReadOnlyList<ProjectLayer> ReadProjectLayers(string mpjPath)
    {
        byte[] data = File.ReadAllBytes(mpjPath);
        if (data.Length < 64) throw new InvalidDataException("MPJ 文件过小");
        if (Encoding.ASCII.GetString(data, 0, 7) != ExpectedMagic) throw new InvalidDataException("不是 MapGIS 文件");
        if (data[7] != (byte)':') throw new InvalidDataException($"不是 MPJ 工程文件 (subtype='{(char)data[7]}')");

        string text = GbkEncoding().GetString(data);
        string mpjDir = Path.GetDirectoryName(mpjPath) ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layers = new List<ProjectLayer>();
        var rx = new Regex(@"\.\\([^\\:?*""<>|\x00-\x1f]+\.(WL|WT|WP))", RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(text))
        {
            string fname = m.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(fname) || !seen.Add(fname)) continue;
            layers.Add(new ProjectLayer(fname, Path.Combine(mpjDir, fname), Path.GetExtension(fname).ToLowerInvariant()));
        }
        return layers;
    }

    /// <summary>导入整个 MapGIS 工程：解析成员清单 → 逐个加载存在的 WL/WT/WP → 各成员一图层合并入一个结果。</summary>
    public static DxfImportService.EntityImportResult LoadProject(string mpjPath)
    {
        var result = new DxfImportService.EntityImportResult();
        IReadOnlyList<ProjectLayer> layers;
        try { layers = ReadProjectLayers(mpjPath); }
        catch (Exception ex) { result.Error = $"MPJ 解析失败：{ex.Message}"; return result; }

        int loaded = 0, missing = 0, failed = 0;
        double xmin = double.MaxValue, ymin = double.MaxValue, xmax = double.MinValue, ymax = double.MinValue;
        foreach (var ly in layers)
        {
            if (!File.Exists(ly.FullPath)) { missing++; continue; }
            var sub = ly.Ext switch { ".wl" => LoadWl(ly.FullPath), ".wt" => LoadWt(ly.FullPath), ".wp" => LoadWp(ly.FullPath), _ => null };
            if (sub == null || !sub.Success) { failed++; continue; }
            foreach (var en in sub.Entities) result.Entities.Add(en);
            foreach (var ln in sub.LayerOrder) { if (!result.LayerColors.ContainsKey(ln)) { result.LayerColors[ln] = sub.LayerColors[ln]; result.LayerOrder.Add(ln); } }
            foreach (var kv in sub.TypeCounts) result.TypeCounts[kv.Key] = (result.TypeCounts.TryGetValue(kv.Key, out int c) ? c : 0) + kv.Value;
            if (sub.Bounds[2] > sub.Bounds[0]) { xmin = Math.Min(xmin, sub.Bounds[0]); ymin = Math.Min(ymin, sub.Bounds[1]); xmax = Math.Max(xmax, sub.Bounds[2]); ymax = Math.Max(ymax, sub.Bounds[3]); }
            loaded++;
        }
        if (loaded == 0) { result.Error = $"MPJ 无可加载成员（清单 {layers.Count}，缺失 {missing}，失败 {failed}）"; return result; }
        result.Bounds = xmin < xmax ? new[] { xmin, ymin, xmax, ymax } : new double[] { 0, 0, 0, 0 };
        result.Warnings.Add($"工程 {layers.Count} 成员：加载 {loaded}，缺失 {missing}，失败 {failed}");
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // .WL 线文件：obj0 属性表(57B/record) + obj1 顶点池(16B/vertex) + obj2 等高线 Z
    // ─────────────────────────────────────────────────────────────────────────
    public static DxfImportService.EntityImportResult LoadWl(string path)
    {
        var result = new DxfImportService.EntityImportResult();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { result.Error = $"读取失败：{ex.Message}"; return result; }
        try
        {
            if (data.Length < 0x300) throw new InvalidDataException("文件过小，不是 MapGIS WL 格式");
            if (Encoding.ASCII.GetString(data, 0, 7) != ExpectedMagic) throw new InvalidDataException("Magic 不匹配");
            if (data[7] != WlSubtype) throw new InvalidDataException($"不是 WL 线文件 (subtype='{(char)data[7]}')");

            double xmin = BitConverter.ToDouble(data, 0x130), ymin = BitConverter.ToDouble(data, 0x138);
            double xmax = BitConverter.ToDouble(data, 0x140), ymax = BitConverter.ToDouble(data, 0x148);

            var sections = ReadSectionIndex(data, 0x291, 16);
            if (sections.Count < 2) throw new InvalidDataException($"Section 数({sections.Count})不够，文件损坏");
            var obj0 = sections[0]; var obj1 = sections[1];

            const int Obj0HeaderSkip = 0x39, RecordStride = 57;
            int rec0 = obj0.off + Obj0HeaderSkip;
            int maxRecords = (obj0.size - Obj0HeaderSkip) / RecordStride;

            string layer = Path.GetFileNameWithoutExtension(path);
            var lines = new List<(PolylineEntity pl, long vStart, int vc, byte color)>(Math.Max(0, maxRecords));

            for (int i = 0; i < maxRecords; i++)
            {
                int rec = rec0 + i * RecordStride;
                if (rec + RecordStride > data.Length) break;
                if (data[rec] != 0x01 || data[rec + 1] != 0x02) break;   // 末尾空 slot

                uint vc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec + 0x0A, 4));
                uint vsoff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec + 0x0E, 4));
                byte mgColor = data[rec + 0x1A];
                if (vc == 0 || vc > 1_000_000) break;
                if (vsoff < 2) continue;

                long startVertexIdx = (vsoff - 2) / 16;
                long byteOff = obj1.off + 2 + startVertexIdx * 16;
                long bytesNeeded = (long)vc * 16;
                if (byteOff + bytesNeeded > data.Length) break;

                var pl = new PolylineEntity { LayerName = layer };
                for (int v = 0; v < vc; v++)
                {
                    int voff = (int)(byteOff + v * 16);
                    pl.Points.Add((BitConverter.ToDouble(data, voff), BitConverter.ToDouble(data, voff + 8)));
                }
                var (r, g, b) = Rgb(mgColor);
                pl.Cr = r; pl.Cg = g; pl.Cb = b;
                lines.Add((pl, vsoff - 2, (int)vc, mgColor));
            }

            // 等高线 Z 注入(obj2)：仅用于诊断/后续三维，托管场景为 2D 折线不改点，但记录 Z 命中数于警告
            int withZ = 0;
            if (sections.Count >= 3)
                withZ = CountZInjectable(data, sections[2], lines);

            foreach (var (pl, _, _, _) in lines) result.Entities.Add(pl);
            result.Bounds = new[] { xmin, ymin, xmax, ymax };
            result.LayerColors[layer] = lines.Count > 0 ? (lines[0].pl.Cr, lines[0].pl.Cg, lines[0].pl.Cb) : (0.86f, 0.9f, 0.6f);
            result.LayerOrder.Add(layer);
            result.TypeCounts["多段线"] = lines.Count;
            if (withZ > 0) result.Warnings.Add($"识别等高线 Z {withZ} 条（2D 折线不改点，Z 供参考）");
        }
        catch (Exception ex) { result.Error = $"WL 解析失败：{ex.Message}"; }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // .WP 区/面文件（arc 级 MVP，忠实原 MapGisWpReader 的 arc 提取，视觉呈现地质图斑边界轮廓）
    //   Section 0 arc 表(57B/record, +0x0E=顶点 byte 偏移) + Section 1 顶点池(相邻差=arc 长)
    //   + Section 6/10 独立顶点池(inner ring/孤立 region)。各 arc 作一条折线。
    //   region 环拓扑重建(Section 3 arc-node)原始自陈不完整，暂记不移。
    // ─────────────────────────────────────────────────────────────────────────
    public static DxfImportService.EntityImportResult LoadWp(string path)
    {
        var result = new DxfImportService.EntityImportResult();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { result.Error = $"读取失败：{ex.Message}"; return result; }
        try
        {
            if (data.Length < 0x300) throw new InvalidDataException("WP 文件过小");
            if (Encoding.ASCII.GetString(data, 0, 7) != ExpectedMagic) throw new InvalidDataException("Magic 不匹配");
            if (data[7] != WpSubtype) throw new InvalidDataException($"不是 WP 文件 (subtype='{(char)data[7]}')");

            double xmin = BitConverter.ToDouble(data, 0x130), ymin = BitConverter.ToDouble(data, 0x138);
            double xmax = BitConverter.ToDouble(data, 0x140), ymax = BitConverter.ToDouble(data, 0x148);

            var sections = ReadSectionIndex(data, 0x291, 16);
            if (sections.Count < 7) throw new InvalidDataException($"WP section 数不足({sections.Count})");

            string layer = Path.GetFileNameWithoutExtension(path);
            var (r, g, b) = Rgb(0);   // WP 逐 region 色需 Section 8 拓扑，arc 级用默认导入色
            var arcs = new List<PolylineEntity>();

            bool InBox(double x, double y) => x > xmin - 100 && x < xmax + 100 && y > ymin - 100 && y < ymax + 100;
            PolylineEntity? BuildArc(long baseOff, int vc)
            {
                var pl = new PolylineEntity { LayerName = layer, Cr = r, Cg = g, Cb = b };
                for (int v = 0; v < vc; v++)
                {
                    long off = baseOff + v * 16;
                    if (off + 16 > data.Length) break;
                    double x = BitConverter.ToDouble(data, (int)off), y = BitConverter.ToDouble(data, (int)(off + 8));
                    if (InBox(x, y)) pl.Points.Add((x, y));
                }
                return pl.Points.Count >= 2 ? pl : null;
            }

            // Section 0 arc 表 + Section 1 顶点池：相邻 +0x0E 差值 = 该 arc 的 byte 长
            var s0 = sections[0]; var s1 = sections[1];
            const int S0HeaderSkip = 57, RecStride = 57;
            int maxRecords = (s0.size - S0HeaderSkip) / RecStride;
            var byteOffsets = new List<uint>(Math.Max(0, maxRecords + 1));
            for (int i = 0; i < maxRecords; i++)
            {
                int rec = s0.off + S0HeaderSkip + i * RecStride;
                if (rec + RecStride > data.Length) break;
                if (data[rec] != 0x01 || data[rec + 1] != 0x02) break;
                byteOffsets.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec + 0x0E, 4)));
            }
            for (int i = 0; i < byteOffsets.Count; i++)
            {
                uint thisOff = byteOffsets[i];
                uint nextOff = (i + 1 < byteOffsets.Count) ? byteOffsets[i + 1] : (uint)s1.size;
                long byteCount = (long)nextOff - thisOff;
                if (byteCount <= 0 || byteCount > 100_000) continue;
                int vc = (int)(byteCount / 16);
                if (vc < 2) continue;
                long arcBase = s1.off + thisOff;
                if (arcBase + (long)vc * 16 > data.Length) break;
                var a = BuildArc(arcBase, vc);
                if (a != null) arcs.Add(a);
            }

            // Section 6/10 独立顶点池(跳 32B 头)
            foreach (var si in new[] { 6, 10 })
            {
                if (si >= sections.Count) continue;
                var s = sections[si]; const int Hdr = 32;
                if (s.size <= Hdr) continue;
                int nVerts = (s.size - Hdr) / 16;
                var a = BuildArc(s.off + Hdr, nVerts);
                if (a != null) arcs.Add(a);
            }

            foreach (var pl in arcs) result.Entities.Add(pl);
            result.Bounds = new[] { xmin, ymin, xmax, ymax };
            result.LayerColors[layer] = (r, g, b);
            result.LayerOrder.Add(layer);
            result.TypeCounts["多段线"] = arcs.Count;
            result.Warnings.Add($"WP arc 级导入 {arcs.Count} 条边界（region 环拓扑重建未移）");
        }
        catch (Exception ex) { result.Error = $"WP 解析失败：{ex.Message}"; }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // .WT 点/注记文件：Section0 属性表(93B/record) + Section1 GBK 字符串池 + Section3 XY 池
    // ─────────────────────────────────────────────────────────────────────────
    public static DxfImportService.EntityImportResult LoadWt(string path)
    {
        var result = new DxfImportService.EntityImportResult();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { result.Error = $"读取失败：{ex.Message}"; return result; }
        try
        {
            if (data.Length < 0x300) throw new InvalidDataException("WT 文件过小");
            if (Encoding.ASCII.GetString(data, 0, 7) != ExpectedMagic) throw new InvalidDataException("Magic 不匹配");
            if (data[7] != WtSubtype) throw new InvalidDataException($"不是 WT 文件 (subtype='{(char)data[7]}')");

            double xmin = BitConverter.ToDouble(data, 0x130), ymin = BitConverter.ToDouble(data, 0x138);
            double xmax = BitConverter.ToDouble(data, 0x140), ymax = BitConverter.ToDouble(data, 0x148);

            var sections = ReadSectionIndex(data, 0x291, 16);
            if (sections.Count < 4) throw new InvalidDataException($"Section 数不足({sections.Count})，文件损坏");
            var s0 = sections[0]; var s1 = sections[1];

            Encoding gbk = GbkEncoding();

            const int Stride = 93;
            int nRecords = s0.size / Stride;
            string layer = Path.GetFileNameWithoutExtension(path);
            int count = 0;

            for (int i = 1; i < nRecords; i++)   // skip record 0 (全 0 header)
            {
                int rec = s0.off + i * Stride;
                if (rec + Stride > data.Length) break;
                if (data[rec] == 0) continue;    // 空 slot

                ushort textLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(rec + 1, 2));
                uint strOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec + 3, 4));
                double x = BitConverter.ToDouble(data, rec + 7);
                double y = BitConverter.ToDouble(data, rec + 15);
                float height = BitConverter.ToSingle(data, rec + 0x20);
                float rotDeg = BitConverter.ToSingle(data, rec + 0x2D);   // +0x2D 非对齐
                byte color = data[rec + 0x4B];

                string text = "";
                if (textLen > 0 && strOff + textLen <= (uint)s1.size)
                {
                    try { text = gbk.GetString(data, s1.off + (int)strOff, textLen); }
                    catch { text = $"<text@{strOff}>"; }
                }
                if (string.IsNullOrEmpty(text)) continue;

                var (r, g, b) = Rgb(color);
                result.Entities.Add(new TextEntity
                {
                    X = x, Y = y,
                    Height = height > 0 ? height : 2.0,   // 高为 0 兜底(避免不可见)
                    Rotation = rotDeg * Math.PI / 180.0,
                    Text = text,
                    LayerName = layer, Cr = r, Cg = g, Cb = b
                });
                count++;
            }

            result.Bounds = new[] { xmin, ymin, xmax, ymax };
            result.LayerColors[layer] = (0.86f, 0.9f, 0.6f);
            result.LayerOrder.Add(layer);
            result.TypeCounts["文字"] = count;
        }
        catch (Exception ex) { result.Error = $"WT 解析失败：{ex.Message}"; }
        return result;
    }

    private static Encoding GbkEncoding()
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding("GBK"); } catch { return Encoding.UTF8; }
    }

    // obj2 等高线 Z 识别(不改点，仅计数)：忠实原 InjectZFromObj2 的 "@Continuous" - 0x14 起点规则。
    private static int CountZInjectable(byte[] data, (int off, int size) obj2, List<(PolylineEntity pl, long vStart, int vc, byte color)> lines)
    {
        int obj2End = obj2.off + obj2.size;
        byte[] needle = Encoding.ASCII.GetBytes("@Continuous");
        var starts = new long[lines.Count];
        for (int i = 0; i < lines.Count; i++) starts[i] = lines[i].vStart;
        Array.Sort(starts);
        int hits = 0, p = obj2.off;
        while (p < obj2End - needle.Length)
        {
            int found = IndexOf(data, needle, p, obj2End - needle.Length);
            if (found < 0) break;
            int recStart = found - 0x14;
            if (recStart >= obj2.off && recStart + 16 <= data.Length)
            {
                double z = BitConverter.ToDouble(data, recStart);
                if (z > 800 && z < 2000)
                {
                    uint key = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(recStart + 9, 4));
                    // 命中判定：key 落在某 line 的 [vStart, vStart+vc*16)
                    int lo = 0, hi = lines.Count - 1, cand = -1;
                    while (lo <= hi) { int mid = (lo + hi) / 2; if (starts[mid] <= key) { cand = mid; lo = mid + 1; } else hi = mid - 1; }
                    if (cand >= 0)
                    {
                        long s = starts[cand];
                        // 找该 start 对应的 line vc
                        foreach (var ln in lines) if (ln.vStart == s) { if (key < s + (long)ln.vc * 16) hits++; break; }
                    }
                }
            }
            p = found + 1;
        }
        return hits;
    }

    private static int IndexOf(byte[] data, byte[] needle, int start, int maxEnd)
    {
        int end = Math.Min(data.Length - needle.Length, maxEnd);
        for (int i = start; i <= end; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++) if (data[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static List<(int off, int size)> ReadSectionIndex(byte[] data, int start, int maxSections)
    {
        var sections = new List<(int, int)>();
        int p = start;
        for (int i = 0; i < maxSections; i++)
        {
            if (p + 10 > data.Length) break;
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 4, 4));
            ushort sentinel = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 8, 2));
            p += 10;
            if (sentinel != 0xFFFF) break;
            if (off == 0 && size == 0) break;
            sections.Add(((int)off, (int)size));
        }
        return sections;
    }
}
