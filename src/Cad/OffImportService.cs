using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// Geomview OFF 网格导入 —— 开放文本格式（顶点表 + 面表），对应原程序 OffImportService。
/// 解析三角/多边形面，抽取多边形边为线段（交错 P3_C3，去重），复用 <see cref="DxfImportService.ImportResult"/>
/// 以直接走既有显示/图层/对象树/捕捉管线。纯逻辑、可单测。
/// </summary>
public static class OffImportService
{
    private static readonly (float r, float g, float b) MeshColor = (0.70f, 0.82f, 0.72f);   // 网格线色（淡绿）
    private static readonly char[] Ws = { ' ', '\t', '\r' };

    public static DxfImportService.ImportResult Load(string filePath)
    {
        try { return Parse(File.ReadAllText(filePath)); }
        catch (Exception ex) { return new DxfImportService.ImportResult { Error = $"读取失败：{ex.Message}" }; }
    }

    /// <summary>解析 OFF 文本（可单测）。</summary>
    public static DxfImportService.ImportResult Parse(string text)
    {
        var result = new DxfImportService.ImportResult();

        // 去注释(#...)与空行，得到有效 token 行
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            string s = raw;
            int h = s.IndexOf('#');
            if (h >= 0) s = s.Substring(0, h);
            s = s.Trim();
            if (s.Length > 0) lines.Add(s);
        }
        if (lines.Count == 0) { result.Error = "空文件"; return result; }

        int idx = 0;
        // 头行：OFF / COFF / NOFF / STOFF / 4OFF …（含 "OFF"）
        string head = lines[0].Replace(" ", "").ToUpperInvariant();
        if (head.EndsWith("OFF")) idx = 1;

        if (idx >= lines.Count) { result.Error = "缺少计数行"; return result; }
        var counts = lines[idx++].Split(Ws, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length < 2 || !int.TryParse(counts[0], out int nV) || !int.TryParse(counts[1], out int nF))
        { result.Error = "计数行格式错误（应为 顶点数 面数 [边数]）"; return result; }

        // 顶点
        var vx = new double[nV]; var vy = new double[nV];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < nV; i++)
        {
            if (idx >= lines.Count) { result.Error = $"顶点不足：期望 {nV}"; return result; }
            var t = lines[idx++].Split(Ws, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) { result.Error = $"顶点行 {i} 字段不足"; return result; }
            double.TryParse(t[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x);
            double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y);
            vx[i] = x; vy[i] = y;
            if (x < minX) minX = x; if (y < minY) minY = y;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y;
        }

        // 面 → 去重的多边形边
        var verts = new List<float>(nF * 6);
        var seen = new HashSet<long>(PackedKeyComparer.Instance);
        int edgeCount = 0;
        float cr = MeshColor.r, cg = MeshColor.g, cb = MeshColor.b;
        for (int f = 0; f < nF; f++)
        {
            if (idx >= lines.Count) break;   // 面不足则按已读的算
            var t = lines[idx++].Split(Ws, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 1 || !int.TryParse(t[0], out int k) || k < 2 || t.Length < k + 1) continue;
            for (int j = 0; j < k; j++)
            {
                if (!int.TryParse(t[1 + j], out int a) || !int.TryParse(t[1 + (j + 1) % k], out int b)) continue;
                if (a < 0 || a >= nV || b < 0 || b >= nV || a == b) continue;
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seen.Add(key)) continue;   // 去重共享边
                verts.Add((float)vx[a]); verts.Add((float)vy[a]); verts.Add(0); verts.Add(cr); verts.Add(cg); verts.Add(cb);
                verts.Add((float)vx[b]); verts.Add((float)vy[b]); verts.Add(0); verts.Add(cr); verts.Add(cg); verts.Add(cb);
                edgeCount++;
            }
        }

        if (edgeCount == 0) { result.Error = "未解析到网格边"; return result; }

        result.Success = true;
        result.EntityCount = nF;
        result.SegmentCount = edgeCount;
        result.LineVertices = verts.ToArray();
        result.Bounds = new[] { minX, minY, maxX, maxY };
        result.TypeCounts["网格"] = nF;
        result.TypeGeometry["网格"] = result.LineVertices;
        result.LayerOrder.Add("OFF网格");
        result.LayerCounts["OFF网格"] = nF;
        result.LayerGeometry["OFF网格"] = result.LineVertices;
        return result;
    }
}
