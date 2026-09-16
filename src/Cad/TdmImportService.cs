using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 3DMine 2011 私有二进制 .3dm 网格导入 —— 忠实移植原 TdmReader(magic "3DMine_2011_Bin")。
/// 逐网格锚点扫描：AcDbFace 类名→标签(GBK)+色, nVerts + 顶点段(25B/顶点=3 double+1B),
/// 三角段(16B/三角=count+3 索引)。网格→去重三角边→显示态线框(P3_C3, 保留 Z 供 3D 视图)。
/// 复用 <see cref="DxfImportService.ImportResult"/> 走既有网格显示导入通道。纯字节解析, 可单测。
/// 注: .3dm 亦是 Rhino 扩展名; 本类仅识 3DMine_2011_Bin, 其余(含 3DMine Solid 文本)报错。
/// </summary>
public static class TdmImportService
{
    public const string ExpectedMagic = "3DMine_2011_Bin";
    private const int MagicHeaderSize = 16;      // [1B len=15] + 15B
    private const int VertexStride = 25;         // 24B XYZ doubles + 1B 分隔符
    private const int AnchorWindow = 1 << 16;
    private static readonly byte[] Anchor = { 0x05, 0x73, 0x6F, 0x6C, 0x69, 0x64, 0x09, 0x00, 0x00, 0x00 };
    private static readonly byte[] ClassToken = { 0x08, 0x41, 0x63, 0x44, 0x62, 0x46, 0x61, 0x63, 0x65 };
    private static readonly (float r, float g, float b) MeshColor = (0.70f, 0.78f, 0.85f);

    /// <summary>是否为 3DMine_2011_Bin 二进制(供导入分派提前判别)。</summary>
    public static bool IsBinary3dm(byte[] data) =>
        data.Length >= MagicHeaderSize && data[0] == ExpectedMagic.Length &&
        Encoding.ASCII.GetString(data, 1, ExpectedMagic.Length) == ExpectedMagic;

    public const string StringMarker = "3DMine String File";

    /// <summary>是否为 3DMine String File(.3ds 文本, 首行含标记)。</summary>
    public static bool LooksLikeStringFile(byte[] data)
    {
        int n = 0; while (n < data.Length && n < 512 && data[n] != (byte)'\n' && data[n] != (byte)'\r') n++;
        return Encoding.Latin1.GetString(data, 0, n).IndexOf(StringMarker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// 3DMine String File(.3ds 文本, GBK)导入为可编辑折线 —— 忠实移植原 TdmStringReader。
    /// 顶点行(code,X,Y,Z)累积成一条折线; "0,…" 边界行末 3 浮点=下条折线 RGB(0..1, 负=默认); DbSText 计数跳过。
    /// </summary>
    public static DxfImportService.EntityImportResult LoadStrings(string path)
    {
        var result = new DxfImportService.EntityImportResult();
        string[] lines;
        try { lines = File.ReadAllLines(path, Gbk()); }
        catch (Exception ex) { result.Error = $"读取失败：{ex.Message}"; return result; }
        string layer = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(layer)) layer = "3DMine";
        try { ParseStrings(lines, result, layer); }
        catch (Exception ex) { result.Error = $"3DMine String 解析失败：{ex.Message}"; }
        return result;
    }

    /// <summary>可单测入口: 直接传入行数组解析。</summary>
    public static void ParseStrings(string[] lines, DxfImportService.EntityImportResult result, string layer = "3DMine")
    {
        if (lines.Length < 2 || lines[0].IndexOf(StringMarker, StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidDataException("不是 3DMine String File(.3ds 文本)");
        if (string.IsNullOrWhiteSpace(layer)) layer = "3DMine";
        PolylineEntity? cur = null;
        bool nextHasColor = false; byte nr = 0, ng = 0, nb = 0;
        int nPoly = 0, nText = 0;

        void Flush()
        {
            if (cur != null && cur.Points.Count >= 2)
            {
                var a = cur.Points[0]; var z = cur.Points[cur.Points.Count - 1];
                cur.Closed = cur.Points.Count >= 3 && Math.Abs(a.x - z.x) < 1e-6 && Math.Abs(a.y - z.y) < 1e-6;
                if (cur.Closed && cur.Has3D)
                    cur.Closed = Math.Abs(cur.Zs![0] - cur.Zs[^1]) < 1e-6;
                cur.LayerName = layer;
                result.Entities.Add(cur); nPoly++;
            }
            cur = null;
        }

        for (int i = 2; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = line.Split(',');
            if (TryCoord(f, out double x, out double y, out double z))
            {
                if (cur == null)
                {
                    cur = new PolylineEntity { Zs = new List<double>(), Cr = 1, Cg = 1, Cb = 1 };
                    if (nextHasColor) { cur.Cr = nr / 255f; cur.Cg = ng / 255f; cur.Cb = nb / 255f; }
                }
                cur.Points.Add((x, y));
                cur.Zs!.Add(z);
                continue;
            }
            Flush();
            if (f.Length > 0 && f[0] == "0") nextHasColor = TryRgb(f, out nr, out ng, out nb);
            else { nextHasColor = false; if (line.IndexOf("DbSText", StringComparison.OrdinalIgnoreCase) >= 0) nText++; }
        }
        Flush();

        ApplyStringAxisOrder(result, nPoly);
        PopulateStringExtents(result);
        if (nPoly > 0 && !result.LayerColors.ContainsKey(layer)) { result.LayerColors[layer] = (1, 1, 1); result.LayerOrder.Add(layer); }
        result.TypeCounts["折线"] = result.TypeCounts.GetValueOrDefault("折线") + nPoly;
        if (nText > 0) result.Warnings.Add($"跳过 {nText} 个文字注记(DbSText)");
    }

    private const double NorthingMin = 1.5e6;
    private const double NorthingMax = 6.0e6;

    private static bool LooksLikeNorthing(double v) => v >= NorthingMin && v <= NorthingMax;

    // String File 的坐标既可能是测量序(北,东,高), 也可能已是 CAD 序(东,北,高)。
    // 与 PitMine3D 的 TdmStringReader 一致：整文件多数表决一次，判不清时保持原序。
    private static void ApplyStringAxisOrder(DxfImportService.EntityImportResult result, int polylineCount)
    {
        int survey = 0, cad = 0, total = 0;
        int firstPolyline = result.Entities.Count - polylineCount;
        for (int entityIndex = firstPolyline; entityIndex < result.Entities.Count; entityIndex++)
            if (result.Entities[entityIndex] is PolylineEntity p)
                foreach (var (x, y) in p.Points)
                {
                    total++;
                    bool first = LooksLikeNorthing(x), second = LooksLikeNorthing(y);
                    if (first && !second) survey++;
                    else if (second && !first) cad++;
                }

        if (survey <= cad) return;
        for (int entityIndex = firstPolyline; entityIndex < result.Entities.Count; entityIndex++)
            if (result.Entities[entityIndex] is PolylineEntity p)
                for (int i = 0; i < p.Points.Count; i++)
                    p.Points[i] = (p.Points[i].y, p.Points[i].x);

        result.Warnings.Add($"源文件为测量序(北,东,高),已换轴为 CAD 序(东,北,高)({survey}/{total} 个顶点命中判据)");
    }

    // 可编辑导入的共享取景链依赖 Bounds/Centers。漏填会把相机定位到默认原点，
    // 双击滚轮再把原点与矿区范围合并，数公里模型会被压进数千公里视野而看似空白。
    private static void PopulateStringExtents(DxfImportService.EntityImportResult result)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var poly in result.Entities)
        {
            if (poly is not PolylineEntity p || p.Points.Count == 0) continue;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var (x, y) in p.Points)
            {
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
            result.Centers.Add(((x0 + x1) * 0.5, (y0 + y1) * 0.5));
            if (x0 < minX) minX = x0; if (x1 > maxX) maxX = x1;
            if (y0 < minY) minY = y0; if (y1 > maxY) maxY = y1;
        }
        if (minX <= maxX) result.Bounds = new[] { minX, minY, maxX, maxY };
    }

    // 顶点行: 去尾随空字段后恰 4 段, 首段整数 code, 后 3 段浮点 → (X,Y,Z)。
    private static bool TryCoord(string[] f, out double x, out double y, out double z)
    {
        x = y = z = 0;
        int n = f.Length;
        while (n > 0 && f[n - 1].Length == 0) n--;
        if (n != 4) return false;
        if (!int.TryParse(f[0].Trim(), out _)) return false;
        if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
        if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
        if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
        return true;
    }

    // 折线头末 3 浮点 = R,G,B(0..1); 负值(−1)=默认色返 false。
    private static bool TryRgb(string[] f, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        int n = f.Length;
        while (n > 0 && f[n - 1].Length == 0) n--;
        if (n < 3) return false;
        if (!double.TryParse(f[n - 3], NumberStyles.Float, CultureInfo.InvariantCulture, out double rr)) return false;
        if (!double.TryParse(f[n - 2], NumberStyles.Float, CultureInfo.InvariantCulture, out double gg)) return false;
        if (!double.TryParse(f[n - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double bb)) return false;
        if (rr < 0 || gg < 0 || bb < 0) return false;
        if (rr > 1.001 || gg > 1.001 || bb > 1.001) return false;
        r = (byte)Math.Round(rr * 255); g = (byte)Math.Round(gg * 255); b = (byte)Math.Round(bb * 255);
        return true;
    }

    private sealed class Mesh
    {
        public string Tag = "";
        public bool HasColor; public byte Cr, Cg, Cb;
        public double[] Vx = Array.Empty<double>(), Vy = Array.Empty<double>(), Vz = Array.Empty<double>();
        public int[] Indices = Array.Empty<int>();
        public int Skipped;
    }

    private static Encoding Gbk()
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding("GBK"); } catch { return Encoding.UTF8; }
    }

    /// <summary>解析出的一张三角网(顶点/索引/名称/颜色)，供场景三角网实体直接构造(面模型显示)。</summary>
    public sealed record MeshData(string Name, bool HasColor, byte R, byte G, byte B, double[] Vx, double[] Vy, double[] Vz, int[] Indices, int Skipped);

    public sealed class MeshLoadResult
    {
        public bool Success => Error == null;
        public string? Error;
        public List<MeshData> Meshes = new();
        public List<string> Warnings = new();
    }

    /// <summary>.3dm(3DMine 二进制 / Solid 文本) → 各网格的顶点与三角索引(不做边线展开; 场景侧建 MeshEntity 面模型)。</summary>
    public static MeshLoadResult LoadMeshes(string path)
    {
        var res = new MeshLoadResult();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { res.Error = $"读取失败：{ex.Message}"; return res; }
        var tmp = new DxfImportService.ImportResult();
        try
        {
            List<Mesh> meshes;
            if (IsBinary3dm(data)) meshes = ReadBinaryMeshes(data, tmp);
            else if (LooksLikeSolidFile(data)) meshes = ReadSolidTextMeshes(data, tmp);
            else { res.Error = "非 3DMine .3dm(既非 3DMine_2011_Bin 二进制，也非 3DMine Solid 文本；Rhino .3dm 不支持)"; return res; }
            int idx = 0;
            foreach (var m in meshes)
            {
                idx++;
                if (m.Indices.Length < 3 || m.Vx.Length == 0) continue;
                bool black = m.HasColor && m.Cr == 0 && m.Cg == 0 && m.Cb == 0;
                res.Meshes.Add(new MeshData(string.IsNullOrWhiteSpace(m.Tag) ? $"网格{idx}" : m.Tag, m.HasColor && !black, m.Cr, m.Cg, m.Cb, m.Vx, m.Vy, m.Vz, m.Indices, m.Skipped));
            }
            res.Warnings.AddRange(tmp.Warnings);
            if (res.Meshes.Count == 0) res.Error = "未解析到网格三角";
        }
        catch (Exception ex) { res.Error = $"3DMine .3dm 解析失败：{ex.Message}"; }
        return res;
    }

    public static DxfImportService.ImportResult Load(string path)
    {
        var result = new DxfImportService.ImportResult();
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { result.Error = $"读取失败：{ex.Message}"; return result; }
        try { Parse(data, result); }
        catch (Exception ex) { result.Error = $"3DMine .3dm 解析失败：{ex.Message}"; }
        return result;
    }

    /// <summary>是否为 3DMine Solid 文本格式(首行以 "3DMine Solid File" 结尾)。</summary>
    public static bool LooksLikeSolidFile(byte[] data)
    {
        if (data == null || data.Length < 32) return false;
        int end = Math.Min(data.Length, 1024);
        int nl = Array.IndexOf(data, (byte)'\n', 0, end);
        int lineLen = nl >= 0 ? nl : end;
        string firstLine = Encoding.Latin1.GetString(data, 0, lineLen);
        return firstLine.IndexOf("3DMine Solid File", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void Parse(byte[] data, DxfImportService.ImportResult result)
    {
        List<Mesh> meshes;
        if (IsBinary3dm(data)) meshes = ReadBinaryMeshes(data, result);
        else if (LooksLikeSolidFile(data)) meshes = ReadSolidTextMeshes(data, result);
        else throw new InvalidDataException("非 3DMine .3dm(既非 3DMine_2011_Bin 二进制，也非 3DMine Solid 文本；Rhino .3dm 不支持)");
        BuildResult(meshes, result);
    }

    private static List<Mesh> ReadBinaryMeshes(byte[] data, DxfImportService.ImportResult result)
    {
        int n = data.Length;
        if (n < MagicHeaderSize + 14) throw new InvalidDataException($"文件过小({n} 字节)");
        var gbk = Gbk();
        var meshes = new List<Mesh>();
        int pos = MagicHeaderSize;
        while (pos < n - 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)) == 0xFFFFFFFF) break;   // 末尾哨兵
            int winEnd = Math.Min(n, pos + AnchorWindow);
            int rel = data.AsSpan(pos, winEnd - pos).IndexOf(Anchor.AsSpan());
            if (rel < 0) { result.Warnings.Add($"偏移 0x{pos:X} 后无网格锚点，停止(已提 {meshes.Count} 网格)"); break; }
            int anchorAt = pos + rel;
            try { meshes.Add(ReadOneMesh(data, n, anchorAt, gbk, out int next)); pos = next; }
            catch (Exception ex) { result.Warnings.Add($"第 {meshes.Count + 1} 网格解析失败:{ex.Message}"); break; }
        }
        return meshes;
    }

    // 3DMine Solid 文本(file_version=3DMine_2009)：顶点块(X,Y,Z, 含小数) → solids 尾标(名+RGB) → 面块(整数三元组) → 重复 → End。忠实原 TdmSolidReader。
    private static List<Mesh> ReadSolidTextMeshes(byte[] data, DxfImportService.ImportResult result)
    {
        var meshes = new List<Mesh>();
        string[] lines = Gbk().GetString(data).Split('\n');
        var vx = new List<double>(); var vy = new List<double>(); var vz = new List<double>();
        var faces = new List<int>();
        string name = ""; bool hasColor = false; byte pr = 0, pg = 0, pb = 0; int skipped = 0;
        bool readingFaces = false;

        void Flush()
        {
            if (vx.Count > 0 && faces.Count > 0)
                meshes.Add(new Mesh { Tag = name, HasColor = hasColor, Cr = pr, Cg = pg, Cb = pb, Vx = vx.ToArray(), Vy = vy.ToArray(), Vz = vz.ToArray(), Indices = faces.ToArray(), Skipped = skipped });
            vx.Clear(); vy.Clear(); vz.Clear(); faces.Clear();
            name = ""; hasColor = false; pr = pg = pb = 0; skipped = 0; readingFaces = false;
        }

        for (int li = 2; li < lines.Length; li++)   // 前两行是路径/版本头
        {
            string line = lines[li];
            if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("End", StringComparison.OrdinalIgnoreCase)) break;

            if (line.StartsWith("solids,", StringComparison.OrdinalIgnoreCase))
            {
                var f = line.Split(',');
                if (f.Length >= 2) name = f[1].Trim();
                if (f.Length >= 6 && double.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cr)
                    && double.TryParse(f[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cg)
                    && double.TryParse(f[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cb))
                { pr = ToByte(cr); pg = ToByte(cg); pb = ToByte(cb); hasColor = true; }
                readingFaces = true;
                continue;
            }

            bool isVertex = line.IndexOf('.') >= 0;   // 浮点=顶点; 纯整数=面索引
            if (!readingFaces) { if (TryVertex(line, out double x, out double y, out double z)) { vx.Add(x); vy.Add(y); vz.Add(z); } }
            else if (isVertex) { Flush(); if (TryVertex(line, out double x, out double y, out double z)) { vx.Add(x); vy.Add(y); vz.Add(z); } }   // 面块里出现浮点 → 下一实体顶点块
            else { if (!TryFace(line, vx.Count, faces)) skipped++; }
        }
        Flush();
        return meshes;
    }

    private static bool TryVertex(string line, out double x, out double y, out double z)
    {
        x = y = z = 0;
        int c0 = line.IndexOf(','); if (c0 < 0) return false;
        int c1 = line.IndexOf(',', c0 + 1); if (c1 < 0) return false;
        int c2 = line.IndexOf(',', c1 + 1); int zEnd = c2 < 0 ? line.Length : c2;
        var inv = System.Globalization.CultureInfo.InvariantCulture; var fl = System.Globalization.NumberStyles.Float;
        return double.TryParse(line.AsSpan(0, c0), fl, inv, out x)
            && double.TryParse(line.AsSpan(c0 + 1, c1 - c0 - 1), fl, inv, out y)
            && double.TryParse(line.AsSpan(c1 + 1, zEnd - c1 - 1), fl, inv, out z);
    }

    private static bool TryFace(string line, int vertCount, List<int> faces)
    {
        int c0 = line.IndexOf(','); if (c0 < 0) return false;
        int c1 = line.IndexOf(',', c0 + 1); if (c1 < 0) return false;
        int c2 = line.IndexOf(',', c1 + 1); int i2End = c2 < 0 ? line.Length : c2;
        var inv = System.Globalization.CultureInfo.InvariantCulture; var it = System.Globalization.NumberStyles.Integer;
        if (!int.TryParse(line.AsSpan(0, c0), it, inv, out int i0) || !int.TryParse(line.AsSpan(c0 + 1, c1 - c0 - 1), it, inv, out int i1) || !int.TryParse(line.AsSpan(c1 + 1, i2End - c1 - 1), it, inv, out int i2)) return false;
        if ((uint)i0 >= (uint)vertCount || (uint)i1 >= (uint)vertCount || (uint)i2 >= (uint)vertCount) return false;
        faces.Add(i0); faces.Add(i1); faces.Add(i2);
        return true;
    }

    private static byte ToByte(double v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)Math.Round(v * 255.0);

    private static void BuildResult(List<Mesh> meshes, DxfImportService.ImportResult result)
    {
        // 网格 → 去重三角边线框(保留 Z), 各网格标签作图层
        var allVerts = new List<float>(4096);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int totalFaces = 0, totalEdges = 0, meshIdx = 0;
        foreach (var m in meshes)
        {
            meshIdx++;
            float cr = m.HasColor ? m.Cr / 255f : MeshColor.r;
            float cg = m.HasColor ? m.Cg / 255f : MeshColor.g;
            float cb = m.HasColor ? m.Cb / 255f : MeshColor.b;
            if (m.HasColor && m.Cr == 0 && m.Cg == 0 && m.Cb == 0) { cr = MeshColor.r; cg = MeshColor.g; cb = MeshColor.b; }
            string layer = string.IsNullOrWhiteSpace(m.Tag) ? $"网格{meshIdx}" : m.Tag;

            var seen = new HashSet<long>(PackedKeyComparer.Instance);
            var layerVerts = new List<float>();
            int nTri = m.Indices.Length / 3;
            totalFaces += nTri;
            void Edge(int a, int b)
            {
                if (a == b) return;
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seen.Add(key)) return;
                void P(int idx) { allVerts.Add((float)m.Vx[idx]); allVerts.Add((float)m.Vy[idx]); allVerts.Add((float)m.Vz[idx]); allVerts.Add(cr); allVerts.Add(cg); allVerts.Add(cb);
                                  layerVerts.Add((float)m.Vx[idx]); layerVerts.Add((float)m.Vy[idx]); layerVerts.Add((float)m.Vz[idx]); layerVerts.Add(cr); layerVerts.Add(cg); layerVerts.Add(cb); }
                P(a); P(b); totalEdges++;
                double ax = m.Vx[a], ay = m.Vy[a], bx = m.Vx[b], by = m.Vy[b];
                if (ax < minX) minX = ax; if (ay < minY) minY = ay; if (ax > maxX) maxX = ax; if (ay > maxY) maxY = ay;
                if (bx < minX) minX = bx; if (by < minY) minY = by; if (bx > maxX) maxX = bx; if (by > maxY) maxY = by;
            }
            for (int t = 0; t < nTri; t++)
            {
                int i0 = m.Indices[t * 3], i1 = m.Indices[t * 3 + 1], i2 = m.Indices[t * 3 + 2];
                Edge(i0, i1); Edge(i1, i2); Edge(i2, i0);
            }
            if (layerVerts.Count > 0)
            {
                result.LayerGeometry[layer] = layerVerts.ToArray();
                result.LayerOrder.Add(layer);
                result.LayerCounts[layer] = nTri;
            }
        }

        if (totalEdges == 0) { result.Error = "未解析到网格三角边"; return; }
        result.Success = true;
        result.EntityCount = totalFaces;
        result.SegmentCount = totalEdges;
        result.LineVertices = allVerts.ToArray();
        result.Bounds = new[] { minX, minY, maxX, maxY };
        result.TypeCounts["网格"] = meshes.Count;
        result.TypeGeometry["网格"] = result.LineVertices;
        result.Warnings.Add($"3DMine：{meshes.Count} 网格 · {totalFaces} 三角 · {totalEdges} 边");
    }

    // 忠实原 TdmReader.ReadOneMesh。
    private static Mesh ReadOneMesh(byte[] data, int n, int anchorAt, Encoding gbk, out int nextPos)
    {
        string tag = ""; bool hasColor = false; byte cr = 0, cg = 0, cb = 0;
        int searchFrom = Math.Max(0, anchorAt - AnchorWindow);
        int classRel = data.AsSpan(searchFrom, anchorAt - searchFrom).LastIndexOf(ClassToken.AsSpan());
        if (classRel >= 0)
        {
            int tagLenOff = searchFrom + classRel + ClassToken.Length + 4;
            if (tagLenOff < anchorAt)
            {
                int tagLen = data[tagLenOff];
                if (tagLen > 0 && tagLenOff + 1 + tagLen <= anchorAt)
                {
                    try { tag = gbk.GetString(data, tagLenOff + 1, tagLen).Trim(); } catch { tag = ""; }
                    int metaOff = tagLenOff + 1 + tagLen;
                    if (metaOff + 8 <= anchorAt && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(metaOff, 4)) == 4)
                    { cr = data[metaOff + 4]; cg = data[metaOff + 5]; cb = data[metaOff + 6]; hasColor = true; }
                }
            }
        }

        int nVertsOff = anchorAt + Anchor.Length;
        if (nVertsOff + 4 > n) throw new InvalidDataException("读不到 nVerts");
        uint nVertsU = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(nVertsOff, 4));
        if (nVertsU == 0 || nVertsU > 10_000_000) throw new InvalidDataException($"顶点数 {nVertsU} 不合理");
        int nVerts = (int)nVertsU;

        int vStart = nVertsOff + 4;
        long vEnd = (long)vStart + (long)nVerts * VertexStride;
        if (vEnd + 8 > n) throw new InvalidDataException($"顶点段越界(nVerts={nVerts})");

        var vx = new double[nVerts]; var vy = new double[nVerts]; var vz = new double[nVerts];
        for (int i = 0; i < nVerts; i++)
        {
            int o = vStart + i * VertexStride;
            vx[i] = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(o, 8));
            vy[i] = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(o + 8, 8));
            vz[i] = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(o + 16, 8));
        }

        int triHdrOff = (int)vEnd;
        uint totalU32 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(triHdrOff + 4, 4));
        if (totalU32 % 4 != 0) throw new InvalidDataException($"三角段长度 {totalU32} 非 4 的倍数");
        int nTris = (int)(totalU32 / 4);
        int triDataOff = triHdrOff + 8;
        long triEnd = (long)triDataOff + (long)nTris * 16;
        if (nTris < 0 || triEnd > n) throw new InvalidDataException($"三角段越界(nTris={nTris})");

        var indices = new List<int>(nTris * 3);
        int skipped = 0;
        for (int i = 0; i < nTris; i++)
        {
            int o = triDataOff + i * 16;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o, 4));
            int i0 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 4, 4));
            int i1 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 8, 4));
            int i2 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 12, 4));
            if (count != 3 || (uint)i0 >= (uint)nVerts || (uint)i1 >= (uint)nVerts || (uint)i2 >= (uint)nVerts) { skipped++; continue; }
            indices.Add(i0); indices.Add(i1); indices.Add(i2);
        }

        nextPos = (int)triEnd;
        return new Mesh { Tag = tag, HasColor = hasColor, Cr = cr, Cg = cg, Cb = cb, Vx = vx, Vy = vy, Vz = vz, Indices = indices.ToArray(), Skipped = skipped };
    }
}
