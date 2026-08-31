using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// WeCAD KDF 二进制格式导入 —— 忠实移植原 KdfReader(magic "wecad_bin_version_2021")。
/// 逐实体扫描(1B 长度 + "AcDb*" 类名 → 按类分派)：AcDb3DPolyline/AcDbHatch → PolylineEntity,
/// AcDbText/AcDbMText → TextEntity, AcDbLayerTableRecord → 图层色。其余表记录忠实前扫跳过。
/// 每实体公共头 ReadCommon(图层名 + BGR 色)。GBK 中文。产出可编辑 EntityImportResult。纯字节解析。
/// </summary>
public static class KdfImportService
{
    public const string ExpectedMagic = "wecad_bin_version_2021\n";

    private static (float r, float g, float b) Rgb(byte r, byte g, byte b)
    {
        if (r == 0 && g == 0 && b == 0) return (0.86f, 0.9f, 0.6f);   // 纯黑提亮为浅色(在深底可见)
        return (r / 255f, g / 255f, b / 255f);
    }

    public static DxfImportService.EntityImportResult Load(string path)
    {
        var result = new DxfImportService.EntityImportResult();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { result.Error = $"读取失败：{ex.Message}"; return result; }
        return LoadBytes(data);
    }

    /// <summary>从内存字节解析 KDF(可单测: 导出字节直接回读)。</summary>
    public static DxfImportService.EntityImportResult LoadBytes(byte[] data)
    {
        var result = new DxfImportService.EntityImportResult();
        try { Parse(data, result); }
        catch (Exception ex) { result.Error = $"KDF 解析失败：{ex.Message}"; }
        return result;
    }

    private static Encoding Gbk()
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding("GBK"); } catch { return Encoding.UTF8; }
    }

    private static void Parse(byte[] data, DxfImportService.EntityImportResult result)
    {
        var gbk = Gbk();
        if (data.Length < 24) throw new InvalidDataException("KDF 文件太小，缺少文件头");
        if (data[0] != 23) throw new InvalidDataException($"KDF 头长度异常: {data[0]}");
        if (Encoding.ASCII.GetString(data, 1, 23) != ExpectedMagic) throw new InvalidDataException("非 KDF 文件");

        int dataLen = data.Length;
        int i = 24;
        double xMin = double.MaxValue, xMax = double.MinValue, yMin = double.MaxValue, yMax = double.MinValue;
        var layerColors = new Dictionary<string, (float, float, float)>();
        int nPoly = 0, nText = 0, nHatch = 0, nErr = 0;

        void EnsureLayer(string name, float r, float g, float b)
        {
            if (string.IsNullOrEmpty(name)) name = "0";
            if (!result.LayerColors.ContainsKey(name)) { result.LayerColors[name] = (r, g, b); result.LayerOrder.Add(name); }
        }

        while (i < dataLen - 6)
        {
            if (!IsAcDbAt(data, i, out int classLen)) { i++; continue; }
            int classStart = i + 1;
            string className = Encoding.ASCII.GetString(data, classStart, classLen);
            int bodyStart = classStart + classLen;
            int nextEntity = -1;
            try
            {
                switch (className)
                {
                    case "AcDb3DPolyline":
                    {
                        var (layer, col, verts) = ParsePolyline(data, bodyStart, gbk, out nextEntity);
                        var (cr, cg, cb) = Rgb(col.r, col.g, col.b);
                        var pl = new PolylineEntity { LayerName = string.IsNullOrEmpty(layer) ? "0" : layer, Cr = cr, Cg = cg, Cb = cb };
                        foreach (var v in verts) pl.Points.Add((v.x, v.y));
                        if (pl.Points.Count >= 2) { result.Entities.Add(pl); EnsureLayer(pl.LayerName, cr, cg, cb); nPoly++; UpdateBbox(verts, ref xMin, ref xMax, ref yMin, ref yMax); }
                        break;
                    }
                    case "AcDbHatch":
                    {
                        var (layer, col, verts) = ParseHatch(data, bodyStart, gbk, out nextEntity);
                        var (cr, cg, cb) = Rgb(col.r, col.g, col.b);
                        var pl = new PolylineEntity { LayerName = string.IsNullOrEmpty(layer) ? "0" : layer, Cr = cr, Cg = cg, Cb = cb, Closed = true };
                        foreach (var v in verts) pl.Points.Add((v.x, v.y));
                        if (pl.Points.Count >= 2) { result.Entities.Add(pl); EnsureLayer(pl.LayerName, cr, cg, cb); nHatch++; UpdateBbox(verts, ref xMin, ref xMax, ref yMin, ref yMax); }
                        break;
                    }
                    case "AcDbText":
                    {
                        var t = ParseText(data, bodyStart, gbk, out nextEntity);
                        if (!string.IsNullOrEmpty(t.text))
                        {
                            var (cr, cg, cb) = Rgb(t.col.r, t.col.g, t.col.b);
                            result.Entities.Add(new TextEntity { X = t.pos.x, Y = t.pos.y, Height = t.height > 0 ? t.height : 2.0, Rotation = t.rotation, Text = t.text, LayerName = string.IsNullOrEmpty(t.layer) ? "0" : t.layer, Cr = cr, Cg = cg, Cb = cb });
                            EnsureLayer(string.IsNullOrEmpty(t.layer) ? "0" : t.layer, cr, cg, cb); nText++;
                            UpdateBbox(new[] { t.pos }, ref xMin, ref xMax, ref yMin, ref yMax);
                        }
                        break;
                    }
                    case "AcDbMText":
                    {
                        var m = ParseMText(data, bodyStart, gbk, out nextEntity);
                        var (cr, cg, cb) = Rgb(m.col.r, m.col.g, m.col.b);
                        double lh = m.lineHeight > 0 ? m.lineHeight : 2.0;
                        string joined = string.Join("\n", m.lines);   // 合成单一多行 TextEntity(Tessellate 逐行下落, 与源 MText 一一对应)
                        if (joined.Trim().Length > 0)
                        {
                            result.Entities.Add(new TextEntity { X = m.pos.x, Y = m.pos.y, Height = lh, Text = joined, LayerName = string.IsNullOrEmpty(m.layer) ? "0" : m.layer, Cr = cr, Cg = cg, Cb = cb });
                            nText++;
                        }
                        EnsureLayer(string.IsNullOrEmpty(m.layer) ? "0" : m.layer, cr, cg, cb);
                        UpdateBbox(new[] { m.pos }, ref xMin, ref xMax, ref yMin, ref yMax);
                        break;
                    }
                    case "AcDbLayerTableRecord":
                    {
                        var (name, col) = ParseLayerRecord(data, bodyStart, gbk, out nextEntity);
                        var (cr, cg, cb) = Rgb(col.r, col.g, col.b);
                        layerColors[name] = (cr, cg, cb);
                        break;
                    }
                    default:
                        nextEntity = -1;   // 其它表/样式记录：前扫到下一 AcDb*（与原默认分支一致）
                        break;
                }
            }
            catch (Exception ex) { result.Warnings.Add($"解析 {className} @0x{i:x} 失败: {ex.Message}"); nextEntity = -1; nErr++; }

            if (nextEntity > i) i = nextEntity;
            else i = bodyStart;
        }

        // 图层表记录的颜色覆盖到已用图层（若先见实体后见图层记录）
        foreach (var kv in layerColors) if (result.LayerColors.ContainsKey(kv.Key)) result.LayerColors[kv.Key] = kv.Value;

        result.Bounds = xMin <= xMax ? new[] { xMin, yMin, xMax, yMax } : new double[] { 0, 0, 0, 0 };
        result.TypeCounts["多段线"] = nPoly + nHatch;
        result.TypeCounts["文字"] = nText;
        if (result.LayerOrder.Count == 0) { result.LayerColors["0"] = (0.86f, 0.9f, 0.6f); result.LayerOrder.Add("0"); }
        result.Warnings.Add($"KDF：折线 {nPoly} · 填充 {nHatch} · 文字 {nText}" + (nErr > 0 ? $" · 跳过 {nErr} 解析异常" : ""));
    }

    // ─────────────────────────── 实体解析(忠实原 KdfReader) ───────────────────────────
    private readonly record struct P3(double x, double y, double z);
    private readonly record struct Col(byte r, byte g, byte b);

    private static (string layer, Col col) ReadCommon(byte[] d, ref int o, Encoding gbk)
    {
        o += 4;                                                // u32 field count
        if (d[o] != 0x06) throw new InvalidDataException($"@0x{o:x}: 期望 layer tag 06");
        int ll = d[o + 1];
        string layer = gbk.GetString(d, o + 2, ll);
        o += 2 + ll;
        byte b = d[o], g = d[o + 1], r = d[o + 2];             // 3B BGR
        o += 23;                                                // 3B 色 + 6B 保留 + 8B double + 2B flags + 4B handle
        return (layer, new Col(r, g, b));
    }

    private static (string layer, Col col, List<P3> verts) ParsePolyline(byte[] d, int o, Encoding gbk, out int nextOff)
    {
        int o0 = o;
        var (layer, col) = ReadCommon(d, ref o, gbk);
        if (d[o] == 0x02) o += 9;
        if (d[o] == 0x05) o += 25;
        o += 2;
        if (d[o] == 0x02) o += 9;
        if (d[o] == 0x02) o += 9;
        if (d[o] != 0x0c) throw new InvalidDataException($"Polyline @0x{o0:x}: 找不到顶点 tag 0c (0x{d[o]:x2})");
        int cnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 1, 4));
        o += 5;
        var verts = new List<P3>(cnt);
        for (int k = 0; k < cnt; k++)
        {
            double x = BitConverter.ToDouble(d, o), y = BitConverter.ToDouble(d, o + 8), z = BitConverter.ToDouble(d, o + 16);
            verts.Add(new P3(x, y, z));
            int sl = d[o + 32];
            o += 33 + sl;
        }
        o += 1;
        nextOff = o;
        return (layer, col, verts);
    }

    private static (string layer, Col col, List<P3> verts) ParseHatch(byte[] d, int o, Encoding gbk, out int nextOff)
    {
        int o0 = o;
        var (layer, col) = ReadCommon(d, ref o, gbk);
        if (d[o] == 0x03) { int pl = d[o + 1]; o += 2 + pl; }   // pattern name
        if (d[o] == 0x02) o += 9;
        if (d[o] == 0x02) o += 9;
        if (d[o] == 0x05) o += 25;
        if (d[o] == 0x02) o += 9;
        int scanLimit = Math.Min(o + 64, d.Length);
        while (o < scanLimit && d[o] != 0x0c) o++;
        if (d[o] != 0x0c) throw new InvalidDataException($"Hatch @0x{o0:x}: 找不到边界 tag 0c");
        int cnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 1, 4));
        o += 5;
        var bnd = new List<P3>(cnt);
        for (int k = 0; k < cnt; k++)
        {
            double x = BitConverter.ToDouble(d, o), y = BitConverter.ToDouble(d, o + 8), z = BitConverter.ToDouble(d, o + 16);
            bnd.Add(new P3(x, y, z));
            int sl = d[o + 32];
            o += 33 + sl;
        }
        nextOff = o;
        return (layer, col, bnd);
    }

    private static (string layer, Col col, P3 pos, double height, double rotation, string text) ParseText(byte[] d, int o, Encoding gbk, out int nextOff)
    {
        int o0 = o;
        var (layer, col) = ReadCommon(d, ref o, gbk);
        if (d[o] != 0x04) throw new InvalidDataException($"Text @0x{o0:x}: 期望 tag 04");
        var pos = new P3(BitConverter.ToDouble(d, o + 1), BitConverter.ToDouble(d, o + 9), BitConverter.ToDouble(d, o + 17));
        o += 25;
        if (d[o] == 0x05) o += 25;
        double height = 0, rotation = 0; int idx02 = 0;
        while (d[o] == 0x02 && idx02 < 6)
        {
            double v = BitConverter.ToDouble(d, o + 1);
            if (idx02 == 0) height = v; else if (idx02 == 2) rotation = v;
            o += 9; idx02++;
        }
        string text = "";
        if (d[o] == 0x03) { int sl = d[o + 1]; text = gbk.GetString(d, o + 2, sl); o += 2 + sl; }
        int probe = FindNextAcDb(d, o);
        nextOff = probe > 0 ? probe : o;
        return (layer, col, pos, height, rotation, text);
    }

    private static (string layer, Col col, P3 pos, double lineHeight, string[] lines) ParseMText(byte[] d, int o, Encoding gbk, out int nextOff)
    {
        int o0 = o;
        var (layer, col) = ReadCommon(d, ref o, gbk);
        if (d[o] != 0x04) throw new InvalidDataException($"MText @0x{o0:x}: 期望 tag 04");
        var pos = new P3(BitConverter.ToDouble(d, o + 1), BitConverter.ToDouble(d, o + 9), BitConverter.ToDouble(d, o + 17));
        o += 25;
        while (d[o] == 0x05) o += 25;
        double lineHeight = 0; int idx02 = 0;
        while (d[o] == 0x02 && idx02 < 8)
        {
            double v = BitConverter.ToDouble(d, o + 1);
            if (idx02 == 0) lineHeight = v;
            o += 9; idx02++;
        }
        if (d[o] == 0x01) o += 5;
        string text = "";
        if (d[o] == 0x03) { int sl = d[o + 1]; text = gbk.GetString(d, o + 2, sl); o += 2 + sl; }
        int probe = FindNextAcDb(d, o);
        nextOff = probe > 0 ? probe : o;
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        return (layer, col, pos, lineHeight, lines);
    }

    private static (string name, Col col) ParseLayerRecord(byte[] d, int o, Encoding gbk, out int nextOff)
    {
        o += 4;
        if (d[o] != 0x03) throw new InvalidDataException($"LayerTableRecord @0x{o:x}: 期望 tag 03");
        int nlen = d[o + 1];
        string name = gbk.GetString(d, o + 2, nlen);
        o += 2 + nlen;
        o += 1;                                                 // flag byte
        byte b = d[o], g = d[o + 1], r = d[o + 2];
        o += 3;
        o += 2;                                                 // vis + lock
        int next = FindNextAcDb(d, o);
        nextOff = next > 0 ? next : o;
        return (name, new Col(r, g, b));
    }

    // ─────────────────────────── 工具(忠实原 KdfReader) ───────────────────────────
    private static bool IsAcDbAt(byte[] d, int i, out int classLen)
    {
        classLen = 0;
        byte len = d[i];
        if (len < 4 || len > 60) return false;
        if (i + 1 + 4 > d.Length) return false;
        if (d[i + 1] != (byte)'A' || d[i + 2] != (byte)'c' || d[i + 3] != (byte)'D' || d[i + 4] != (byte)'b') return false;
        for (int k = 0; k < len; k++) { byte c = d[i + 1 + k]; if (c < 0x20 || c >= 0x7f) return false; }
        classLen = len;
        return true;
    }

    private static int FindNextAcDb(byte[] d, int from)
    {
        for (int i = from; i < d.Length - 6; i++) if (IsAcDbAt(d, i, out _)) return i;
        return -1;
    }

    private static void UpdateBbox(IReadOnlyList<P3> verts, ref double xMin, ref double xMax, ref double yMin, ref double yMax)
    {
        foreach (var v in verts)
        {
            if (v.x < 1e5 || v.x > 1e7) continue;               // 过滤占位/离群坐标(忠实原 UpdateBbox 地理范围假设)
            if (v.y < 1e6 || v.y > 1e7) continue;
            if (v.x < xMin) xMin = v.x;
            if (v.x > xMax) xMax = v.x;
            if (v.y < yMin) yMin = v.y;
            if (v.y > yMax) yMax = v.y;
        }
    }
}
