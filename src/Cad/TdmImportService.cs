using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

    private static void Parse(byte[] data, DxfImportService.ImportResult result)
    {
        int n = data.Length;
        if (!IsBinary3dm(data))
            throw new InvalidDataException("非 3DMine_2011_Bin 二进制(Rhino .3dm 或 3DMine Solid 文本暂不支持)");
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

            var seen = new HashSet<long>();
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
