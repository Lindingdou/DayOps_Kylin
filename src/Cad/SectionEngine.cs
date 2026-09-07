using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>一条剖面交线链：原位三维坐标 + 展开(里程 s, 标高 z)成对数组。</summary>
public sealed class SectionChain
{
    public int MeshIndex;                 // 来源三角网序号（用于分色）
    public List<double> Xyz = new();      // 原位 [x,y,z,...]
    public List<double> Sz = new();       // 展开 [s,z,...]，s = 全局里程（沿剖面折线累计）
}

/// <summary>剖面线一段：起点 / 单位方向 / 长度 / 全局里程起点。引擎切割与钻孔投影共用。</summary>
public readonly record struct SectionSegment(double X0, double Y0, double Ux, double Uy, double Len, double S0);

/// <summary>
/// 切割剖面引擎（忠实移植原 <c>MeshEditLib.Sections.SectionEngine</c>，纯 C#）：剖面线逐段生成竖直平面，对每张三角网做
/// 符号距离切割（与 marching-triangles 同族）→ 边键拼链 → 按段内里程裁剪 → 全局里程拼接。
/// </summary>
public static class SectionEngine
{
    /// <summary>剖面折线 → 段表（长度为 0 的段被剔除），totalLength = 全线里程。</summary>
    public static List<SectionSegment> BuildSegmentTable(double[] sectionXyz, out double totalLength)
    {
        var segs = new List<SectionSegment>();
        totalLength = 0;
        int np = sectionXyz.Length / 3;
        for (int i = 0; i + 1 < np; i++)
        {
            double x0 = sectionXyz[i * 3], y0 = sectionXyz[i * 3 + 1];
            double dx = sectionXyz[(i + 1) * 3] - x0, dy = sectionXyz[(i + 1) * 3 + 1] - y0;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;
            segs.Add(new SectionSegment(x0, y0, dx / len, dy / len, len, totalLength));
            totalLength += len;
        }
        return segs;
    }

    /// <param name="sectionXyz">剖面线扁平顶点 [x,y,z,...]（Z 不参与，竖直面贯穿全高程）。</param>
    public static List<SectionChain> Build(
        IReadOnlyList<(double[] verts, int[] tris)> meshes, double[] sectionXyz,
        out double totalLength, out string warning)
    {
        warning = "";
        var result = new List<SectionChain>();
        if (sectionXyz.Length / 3 < 2) { totalLength = 0; warning = "剖面线顶点不足 2 个；"; return result; }
        var segs = BuildSegmentTable(sectionXyz, out totalLength);
        if (segs.Count == 0) { warning = "剖面线所有段长度为 0；"; return result; }

        for (int m = 0; m < meshes.Count; m++)
        {
            var (verts, tris) = meshes[m];
            int nv = verts.Length / 3;
            var d = new double[nv];
            var s = new double[nv];
            foreach (var seg in segs)
            {
                double nx = -seg.Uy, ny = seg.Ux;
                for (int i = 0; i < nv; i++)
                {
                    double wx = verts[i * 3] - seg.X0, wy = verts[i * 3 + 1] - seg.Y0;
                    double di = wx * nx + wy * ny;
                    d[i] = Math.Abs(di) < 1e-9 ? 1e-9 : di;
                    s[i] = wx * seg.Ux + wy * seg.Uy;
                }
                foreach (var chain in CutMesh(verts, tris, d, s))
                    foreach (var clipped in ClipChainByS(chain, seg.Len))
                    {
                        var sc = new SectionChain { MeshIndex = m };
                        foreach (var (x, y, z, si) in clipped)
                        {
                            sc.Xyz.Add(x); sc.Xyz.Add(y); sc.Xyz.Add(z);
                            sc.Sz.Add(seg.S0 + si); sc.Sz.Add(z);
                        }
                        if (sc.Xyz.Count >= 6) result.Add(sc);
                    }
            }
        }
        return result;
    }

    private static List<List<(double x, double y, double z, double s)>> CutMesh(double[] verts, int[] tris, double[] d, double[] s)
    {
        var edgePt = new Dictionary<long, (double x, double y, double z, double s)>();
        var segList = new List<(long e0, long e1)>();
        var atEdge = new Dictionary<long, (int s0, int s1)>();

        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            long k0 = d[a] * d[b] < 0 ? EdgeKey(a, b) : -1;
            long k1 = d[b] * d[c] < 0 ? EdgeKey(b, c) : -1;
            long k2 = d[c] * d[a] < 0 ? EdgeKey(c, a) : -1;
            long e0 = -1, e1 = -1;
            if (k0 >= 0) e0 = k0;
            if (k1 >= 0) { if (e0 < 0) e0 = k1; else e1 = k1; }
            if (k2 >= 0) { if (e0 < 0) e0 = k2; else if (e1 < 0) e1 = k2; }
            if (e0 < 0 || e1 < 0) continue;

            AddEdgePoint(edgePt, e0, verts, d, s);
            AddEdgePoint(edgePt, e1, verts, d, s);
            int sid = segList.Count;
            segList.Add((e0, e1));
            Hang(atEdge, e0, sid);
            Hang(atEdge, e1, sid);
        }

        var chains = new List<List<(double, double, double, double)>>();
        var used = new bool[segList.Count];
        foreach (var kv in atEdge)
        {
            if (kv.Value.s1 >= 0 || used[kv.Value.s0]) continue;
            chains.Add(Walk(kv.Key, kv.Value.s0, segList, atEdge, used, edgePt, false));
        }
        for (int i = 0; i < segList.Count; i++)
        {
            if (used[i]) continue;
            chains.Add(Walk(segList[i].e0, i, segList, atEdge, used, edgePt, true));
        }
        return chains;
    }

    private static List<(double, double, double, double)> Walk(
        long startKey, int startSeg, List<(long e0, long e1)> segList,
        Dictionary<long, (int s0, int s1)> atEdge, bool[] used,
        Dictionary<long, (double x, double y, double z, double s)> edgePt, bool loop)
    {
        var pts = new List<(double, double, double, double)> { edgePt[startKey] };
        long key = startKey;
        int seg = startSeg;
        while (true)
        {
            used[seg] = true;
            long next = segList[seg].e0 == key ? segList[seg].e1 : segList[seg].e0;
            pts.Add(edgePt[next]);
            var (s0, s1) = atEdge[next];
            int follow = s0 != seg && s0 >= 0 && !used[s0] ? s0
                       : s1 != seg && s1 >= 0 && !used[s1] ? s1 : -1;
            if (follow < 0) break;
            key = next; seg = follow;
        }
        if (loop && pts.Count >= 2)
        {
            var first = pts[0];
            var last = pts[pts.Count - 1];
            if (Math.Abs(first.Item1 - last.Item1) > 1e-9 || Math.Abs(first.Item2 - last.Item2) > 1e-9) pts.Add(first);
        }
        return pts;
    }

    /// <summary>把链按段内里程 s ∈ [0, len] 裁剪（边界线性插值），可能拆成多段。</summary>
    private static List<List<(double x, double y, double z, double s)>> ClipChainByS(
        List<(double x, double y, double z, double s)> pts, double len)
    {
        var outChains = new List<List<(double, double, double, double)>>();
        var cur = new List<(double, double, double, double)>();
        void Flush() { if (cur.Count >= 2) outChains.Add(cur); cur = new List<(double, double, double, double)>(); }

        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double t0 = 0, t1 = 1, ds = b.s - a.s;
            if (Math.Abs(ds) < 1e-12) { if (a.s < 0 || a.s > len) { Flush(); continue; } }
            else
            {
                double tLo = (0 - a.s) / ds, tHi = (len - a.s) / ds;
                if (tLo > tHi) (tLo, tHi) = (tHi, tLo);
                t0 = Math.Max(t0, tLo); t1 = Math.Min(t1, tHi);
                if (t0 >= t1) { Flush(); continue; }
            }
            var pa = Lerp(a, b, t0); var pb = Lerp(a, b, t1);
            if (cur.Count == 0) cur.Add(pa);
            else
            {
                var last = cur[cur.Count - 1];
                if (Math.Abs(last.Item1 - pa.x) > 1e-9 || Math.Abs(last.Item2 - pa.y) > 1e-9) { Flush(); cur.Add(pa); }
            }
            cur.Add(pb);
            if (t1 < 1) Flush();
        }
        Flush();
        return outChains;
    }

    private static (double x, double y, double z, double s) Lerp(
        (double x, double y, double z, double s) a, (double x, double y, double z, double s) b, double t) =>
        (a.x + t * (b.x - a.x), a.y + t * (b.y - a.y), a.z + t * (b.z - a.z), a.s + t * (b.s - a.s));

    private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static void AddEdgePoint(Dictionary<long, (double x, double y, double z, double s)> edgePt, long key, double[] verts, double[] d, double[] s)
    {
        if (edgePt.ContainsKey(key)) return;
        int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
        double t = d[a] / (d[a] - d[b]);
        edgePt[key] = (verts[a * 3] + t * (verts[b * 3] - verts[a * 3]),
                       verts[a * 3 + 1] + t * (verts[b * 3 + 1] - verts[a * 3 + 1]),
                       verts[a * 3 + 2] + t * (verts[b * 3 + 2] - verts[a * 3 + 2]),
                       s[a] + t * (s[b] - s[a]));
    }

    private static void Hang(Dictionary<long, (int s0, int s1)> atEdge, long key, int sid)
    {
        if (atEdge.TryGetValue(key, out var v)) atEdge[key] = (v.s0, sid);
        else atEdge[key] = (sid, -1);
    }

    /// <summary>在里程 s 处竖直采所有交线的标高（可能多个：不同来源网 / 折叠面多次穿越）。动态剖面·厚度分析用。</summary>
    public static List<(double z, int mi)> SampleAt(IReadOnlyList<SectionChain> chains, double s)
    {
        var hits = new List<(double z, int mi)>();
        foreach (var c in chains)
        {
            var sz = c.Sz;
            for (int i = 0; i + 3 < sz.Count; i += 2)
            {
                double sa = sz[i], za = sz[i + 1], sbb = sz[i + 2], zb = sz[i + 3];
                double lo = Math.Min(sa, sbb), hi = Math.Max(sa, sbb);
                if (s < lo - 1e-9 || s > hi + 1e-9) continue;
                if (Math.Abs(sbb - sa) < 1e-9) { hits.Add((za, c.MeshIndex)); continue; }
                double t = (s - sa) / (sbb - sa);
                if (t < -1e-9 || t > 1 + 1e-9) continue;
                hits.Add((za + t * (zb - za), c.MeshIndex));
            }
        }
        return hits;
    }

    /// <summary>里程处各层位标高列(自上而下, 合并同网重复点)——厚度 = 相邻两项之差。</summary>
    public static List<(double z, int mi)> ColumnAt(IReadOnlyList<SectionChain> chains, double s)
    {
        var hits = SampleAt(chains, s);
        hits.Sort((a, b) => b.z.CompareTo(a.z));
        var col = new List<(double z, int mi)>();
        foreach (var h in hits)
        {
            if (col.Count > 0 && col[col.Count - 1].mi == h.mi && Math.Abs(col[col.Count - 1].z - h.z) < 1e-4) continue;
            col.Add(h);
        }
        return col;
    }
}

/// <summary>
/// 剖面结果 → 场景实体（忠实移植原 <c>SectionBuilder.BuildPmbi</c> / <c>SectionCutWindow.BuildPmbi</c>，PMBI → SceneEntity）：
/// 原位三维交线（按来源网分色）+ 展开剖面图（图框 / 标高网格 + 标注 / 里程刻度 + 标注 / 剖面折线 / 钻孔投影柱状 / 图名）。
/// 纯静态、无副作用，供「创建剖面」与「动态剖面·展绘」共用。
/// </summary>
public static class SectionBuilder
{
    public static readonly (byte r, byte g, byte b)[] Palette =
    {
        (230, 92, 0), (0, 160, 230), (60, 200, 120), (210, 80, 210), (235, 200, 0), (240, 90, 90),
    };

    public sealed class Options
    {
        public bool Want3d = true;
        public bool WantProfile = true;
        public string Layer3d = "剖面交线";
        public string LayerProfile = "剖面图";
        public double Vex = 1.0;      // 垂直夸大
        public double GridZ = 10.0;   // 标高网格间距 (m)
        public double GridS = 100.0;  // 里程刻度间距 (m)
        public double TextH = 2.0;    // 字高 (m)
        public double? BaseX;         // 展开图基点（null=剖面线起点南侧自动）
        public double? BaseY;
        public string SecName = "A";
        public double ColW = 4.0;     // 钻孔柱宽
        public bool LabelHoleId = true;
        public bool LabelOffset = true;
    }

    public sealed record BoreSeg(double From, double To, string Name, string? ColorHex, string Type);

    public sealed record BoreProj(string HoleId, double S, double Offset, double ZCollar, double Depth, List<BoreSeg> Segs)
    {
        public double ZBottom => ZCollar - Depth;
    }

    /// <summary>钻孔 (x,y) → 投影到剖面线: (全局里程, 偏距); 偏距超 band 返回 null。</summary>
    public static (double s, double offset)? Project(IReadOnlyList<SectionSegment> segs, double x, double y, double band)
    {
        double best = double.MaxValue, bestS = 0;
        foreach (var sg in segs)
        {
            double wx = x - sg.X0, wy = y - sg.Y0;
            double sl = Math.Clamp(wx * sg.Ux + wy * sg.Uy, 0, sg.Len);
            double dx = x - (sg.X0 + sl * sg.Ux), dy = y - (sg.Y0 + sl * sg.Uy);
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < best) { best = dist; bestS = sg.S0 + sl; }
        }
        if (best > band || best == double.MaxValue) return null;
        return (bestS, best);
    }

    /// <summary>分层配色：优先库内 ColorHex；缺省煤层深灰、岩性按层名稳定淡色。</summary>
    public static (byte r, byte g, byte b) SegColor(BoreSeg seg)
    {
        if (!string.IsNullOrWhiteSpace(seg.ColorHex))
        {
            string hex = seg.ColorHex!.TrimStart('#');
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                return ((byte)(v >> 16), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
        }
        if (seg.Type == "煤层" || seg.Type == "coal") return (45, 45, 45);
        int h = 17;
        foreach (char c in seg.Name) h = unchecked(h * 31 + c);
        h &= 0x7FFFFFFF;
        return ((byte)(150 + h % 90), (byte)(150 + (h / 7) % 90), (byte)(150 + (h / 49) % 90));
    }

    public static double MinZ(IReadOnlyList<SectionChain> chains)
    {
        double z = double.MaxValue;
        foreach (var c in chains) for (int i = 1; i < c.Sz.Count; i += 2) if (c.Sz[i] < z) z = c.Sz[i];
        return z == double.MaxValue ? 0 : z;
    }

    public static double MaxZ(IReadOnlyList<SectionChain> chains)
    {
        double z = double.MinValue;
        foreach (var c in chains) for (int i = 1; i < c.Sz.Count; i += 2) if (c.Sz[i] > z) z = c.Sz[i];
        return z == double.MinValue ? 0 : z;
    }

    private static float F(byte b) => b / 255f;

    /// <summary>
    /// 组装场景实体。sectionXyz 用于自动定位展开图基点（剖面线起点南侧）。out usedBaseX/Y = 实际用的展开图基点。
    /// </summary>
    public static List<SceneEntity> BuildEntities(IReadOnlyList<SectionChain> chains, double totalLen, double[] sectionXyz,
        Options o, IReadOnlyList<BoreProj>? bores, out double usedBaseX, out double usedBaseY)
    {
        var list = new List<SceneEntity>();
        bores ??= Array.Empty<BoreProj>();
        double zMin = MinZ(chains), zMax = MaxZ(chains);
        foreach (var bp in bores)
        {
            if (bp.ZCollar > zMax) zMax = bp.ZCollar;
            if (bp.ZBottom < zMin) zMin = bp.ZBottom;
        }
        usedBaseX = o.BaseX ?? sectionXyz[0];
        usedBaseY = o.BaseY ?? (sectionXyz[1] - ((zMax - zMin) * o.Vex + 80));
        double baseX = usedBaseX, baseY = usedBaseY;

        if (o.Want3d)
            foreach (var c in chains)
            {
                var (r, g, b) = Palette[c.MeshIndex % Palette.Length];
                var pl = new PolylineEntity { LayerName = o.Layer3d, Cr = F(r), Cg = F(g), Cb = F(b), Zs = new List<double>() };
                for (int i = 0; i + 2 < c.Xyz.Count; i += 3) { pl.Points.Add((c.Xyz[i], c.Xyz[i + 1])); pl.Zs.Add(c.Xyz[i + 2]); }
                list.Add(pl);
            }

        if (o.WantProfile)
        {
            double gridZ = Math.Max(0.1, o.GridZ), gridS = Math.Max(1, o.GridS), textH = Math.Max(0.1, o.TextH);
            double z0 = Math.Floor(zMin / gridZ) * gridZ;
            double z1 = Math.Ceiling(zMax / gridZ) * gridZ;
            if (z1 - z0 < gridZ * 0.5) z1 = z0 + gridZ;
            double H = (z1 - z0) * o.Vex;
            (float r, float g, float b) gray = (200 / 255f, 200 / 255f, 200 / 255f);
            string lp = o.LayerProfile;

            PolylineEntity Poly(bool closed, (float r, float g, float b) col)
                => new() { Closed = closed, LayerName = lp, Cr = col.r, Cg = col.g, Cb = col.b };
            void Line(double x0, double y0, double x1, double y1, (float r, float g, float b) col)
                => list.Add(new LineEntity { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, LayerName = lp, Cr = col.r, Cg = col.g, Cb = col.b });
            void Text(double x, double y, double h, int hAlign, int vAlign, string s, (float r, float g, float b) col)
                => list.Add(new TextEntity { X = x, Y = y, Height = h, HAlign = hAlign, VAlign = vAlign, Text = s, LayerName = lp, Cr = col.r, Cg = col.g, Cb = col.b });

            // 外框
            var frame = Poly(true, gray);
            frame.Points.Add((baseX, baseY)); frame.Points.Add((baseX + totalLen, baseY));
            frame.Points.Add((baseX + totalLen, baseY + H)); frame.Points.Add((baseX, baseY + H));
            list.Add(frame);

            // 标高网格线 + 左侧标高标注(右对齐/垂直居中)
            for (double z = z0; z <= z1 + 1e-9; z += gridZ)
            {
                double y = baseY + (z - z0) * o.Vex;
                if (z > z0 && z < z1) Line(baseX, y, baseX + totalLen, y, gray);
                Text(baseX - textH * 0.6, y, textH, 2, 1, z.ToString("0.#", CultureInfo.InvariantCulture), gray);
            }

            // 里程刻度 + 下方里程标注（含终点）
            for (double s = 0; s <= totalLen + 1e-9; s += gridS)
            {
                double x = baseX + Math.Min(s, totalLen);
                Line(x, baseY, x, baseY - textH * 0.8, gray);
                Text(x, baseY - textH * 1.1, textH, 1, 2, Math.Min(s, totalLen).ToString("0.#", CultureInfo.InvariantCulture), gray);
                if (s < totalLen && s + gridS > totalLen && totalLen - s > gridS * 0.25) s = totalLen - gridS;
            }

            // 剖面折线（多张网叠画同一坐标系，按来源分色）
            foreach (var c in chains)
            {
                var (r, g, b) = Palette[c.MeshIndex % Palette.Length];
                var pl = Poly(false, (F(r), F(g), F(b)));
                for (int i = 0; i + 1 < c.Sz.Count; i += 2) pl.Points.Add((baseX + c.Sz[i], baseY + (c.Sz[i + 1] - z0) * o.Vex));
                if (pl.Points.Count >= 2) list.Add(pl);
            }

            // 钻孔投影柱状：孔轴线 + 分层色块(实心矩形→矩形实体) + 孔号/偏距标注
            (float r, float g, float b) outline = (90 / 255f, 90 / 255f, 90 / 255f);
            foreach (var bp in bores)
            {
                double cx = baseX + bp.S, half = o.ColW * 0.5;
                double yTop = baseY + (bp.ZCollar - z0) * o.Vex;
                double yBot = baseY + (bp.ZBottom - z0) * o.Vex;
                Line(cx, yTop, cx, yBot, outline);
                foreach (var seg in bp.Segs)
                {
                    double ya = baseY + (bp.ZCollar - seg.From - z0) * o.Vex;
                    double yb = baseY + (bp.ZCollar - seg.To - z0) * o.Vex;
                    if (ya - yb < 1e-6) continue;
                    var (r, g, b) = SegColor(seg);
                    list.Add(new RectEntity { X0 = cx - half, Y0 = yb, X1 = cx + half, Y1 = ya, LayerName = lp, Cr = F(r), Cg = F(g), Cb = F(b) });
                    var box = Poly(true, outline);
                    box.Points.Add((cx - half, yb)); box.Points.Add((cx + half, yb)); box.Points.Add((cx + half, ya)); box.Points.Add((cx - half, ya));
                    list.Add(box);
                }
                if (o.LabelHoleId || o.LabelOffset)
                {
                    string label = o.LabelHoleId ? bp.HoleId + (o.LabelOffset ? $" 偏{bp.Offset:0}m" : "") : $"偏{bp.Offset:0}m";
                    Text(cx, yTop + textH * 0.4, textH, 1, 0, label, gray);
                }
            }

            // 图名 + 两端剖面号（A ── A′）
            if (!string.IsNullOrEmpty(o.SecName))
            {
                string title = $"{o.SecName}-{o.SecName}′ 剖面图" + (Math.Abs(o.Vex - 1) > 1e-9 ? $"（垂直×{o.Vex:0.#}）" : "");
                Text(baseX + totalLen * 0.5, baseY + H + textH * 2.4, textH * 1.6, 1, 1, title, gray);
                Text(baseX, baseY + H + textH * 0.5, textH, 1, 1, o.SecName, gray);
                Text(baseX + totalLen, baseY + H + textH * 0.5, textH, 1, 1, o.SecName + "′", gray);
            }
        }
        return list;
    }

    /// <summary>剖面交线 → CSV(带 BOM 由调用方保存): 面序号,里程,标高,X,Y。</summary>
    public static string ToCsv(IReadOnlyList<SectionChain> chains, IReadOnlyList<string>? meshNames = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("面,链,里程 s(m),标高 z(m),X,Y");
        for (int ci = 0; ci < chains.Count; ci++)
        {
            var c = chains[ci];
            string name = meshNames != null && c.MeshIndex < meshNames.Count ? meshNames[c.MeshIndex] : $"面{c.MeshIndex + 1}";
            for (int i = 0; i + 1 < c.Sz.Count; i += 2)
            {
                int k = i / 2 * 3;
                sb.Append(name).Append(',').Append(ci + 1).Append(',')
                  .Append(c.Sz[i].ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.Sz[i + 1].ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.Xyz[k].ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(c.Xyz[k + 1].ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
            }
        }
        return sb.ToString();
    }
}
