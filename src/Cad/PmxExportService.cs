using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 写原版 PitMine 工程文件 .pmx(私有二进制)——反向互操作: 把 Kylin 场景实体存成原版可打开的工程。
/// 与 <see cref="PmxImportService"/> 同规格(PmxFormat): Header('PMX1') + 段表(Strings/Layers/TextStyles/Entities) + Footer(CRC32)。
/// 映射 Kylin→PMX: 线1/点3/多段线2(闭合)/文字4/圆14/圆弧15(3点↔外接圆心角); 矩形·正多边形→闭合多段线2。
/// Kylin 2D 场景全 8 类实体(Line/Point/Polyline/Rect/Circle/Arc/Text/Polygon)均已导出; MText/Hatch/椭圆/样条 系原版实体, Kylin 未建模故无从导(非导出缺)。
/// TrueColor 精确; 图层名成串表。纯逻辑、可单测(与 PmxImportService 往返)。
/// </summary>
public static class PmxExportService
{
    const uint MagicStart = 0x31584D50, MagicEnd = 0x504D5831;

    public static bool SaveToFile(string path, IEnumerable<SceneEntity> entities, out string err, out int written)
    {
        err = ""; written = 0;
        try { var (bytes, n) = Build(entities); File.WriteAllBytes(path, bytes); written = n; return true; }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    public static (byte[] bytes, int entityCount) Build(IEnumerable<SceneEntity> entities)
    {
        var strList = new List<string>();
        var strMap = new Dictionary<string, int>();
        int Intern(string s) { s ??= ""; if (strMap.TryGetValue(s, out int i)) return i; i = strList.Count; strList.Add(s); strMap[s] = i; return i; }

        // 图层: 收集唯一层名 → 索引
        var layerList = new List<string>();
        var layerMap = new Dictionary<string, int>();
        int Layer(string ln) { ln ??= "0"; if (layerMap.TryGetValue(ln, out int i)) return i; i = layerList.Count; layerList.Add(ln); layerMap[ln] = i; return i; }

        // ── 实体段(先建, 顺便注册串/层) ──
        using var ems = new MemoryStream(); using var ew = new BinaryWriter(ems);
        int count = 0;
        void XY(BinaryWriter w, double x, double y) { w.Write(x); w.Write(y); w.Write(0.0); }
        void Ent(byte type, SceneEntity e, Action<BinaryWriter> spec)
        {
            using var bms = new MemoryStream(); using var bw = new BinaryWriter(bms);
            bw.Write(type); bw.Write(Layer(e.LayerName));
            bw.Write((byte)2);                                   // TrueColor
            bw.Write((byte)Math.Clamp(e.Cr * 255f, 0, 255)); bw.Write((byte)Math.Clamp(e.Cg * 255f, 0, 255)); bw.Write((byte)Math.Clamp(e.Cb * 255f, 0, 255));
            spec(bw);
            var body = bms.ToArray();
            ew.Write(body.Length); ew.Write(body); count++;
        }

        foreach (var e in entities)
        {
            switch (e)
            {
                case LineEntity l: Ent(1, l, w => { XY(w, l.X0, l.Y0); XY(w, l.X1, l.Y1); }); break;
                case HatchEntity ha:   // 填充: 原版 Hatch 记录本移植不写, 按图案线导出(出图一致)
                    foreach (var (hx1, hy1, hx2, hy2) in ha.Lines())
                        Ent(1, ha, w => { XY(w, hx1, hy1); XY(w, hx2, hy2); });
                    break;
                case PointEntity p: Ent(3, p, w => XY(w, p.X, p.Y)); break;
                case PolylineEntity pl:
                    Ent(2, pl, w => { w.Write((byte)(pl.Closed ? 1 : 0)); w.Write(pl.Points.Count); foreach (var (vx, vy) in pl.Points) XY(w, vx, vy); });
                    break;
                case RectEntity rc:
                    Ent(2, rc, w => { w.Write((byte)1); w.Write(4); XY(w, rc.X0, rc.Y0); XY(w, rc.X1, rc.Y0); XY(w, rc.X1, rc.Y1); XY(w, rc.X0, rc.Y1); });
                    break;
                case PolygonEntity pg when pg.Sides >= 3 && pg.Radius > 1e-9:   // 正多边形 → 闭合多段线2(N 顶点)
                    Ent(2, pg, w =>
                    {
                        w.Write((byte)1); w.Write(pg.Sides);
                        for (int vi = 0; vi < pg.Sides; vi++)
                        { double a = pg.Rotation + 2 * Math.PI * vi / pg.Sides; XY(w, pg.Cx + pg.Radius * Math.Cos(a), pg.Cy + pg.Radius * Math.Sin(a)); }
                    });
                    break;
                case CircleEntity c: Ent(14, c, w => { XY(w, c.Cx, c.Cy); w.Write(c.Radius); w.Write(0.0); w.Write(0.0); w.Write(1.0); }); break;   // center + radius + normal(0,0,1)
                case TextEntity t:
                    Ent(4, t, w =>
                    {
                        XY(w, t.X, t.Y); XY(w, t.X, t.Y);       // pos, alignPt
                        w.Write(t.Height); w.Write(t.Rotation);
                        w.Write((byte)0); w.Write((byte)0);     // hAlign, vAlign
                        w.Write(Intern(t.Text)); w.Write(-1);   // textStrIdx, styleIdx=-1(默认宽度/倾斜; MVP 不出样式)
                    });
                    break;
                case ArcEntity ar:   // 圆弧: 三点 → 外接圆心/半径/起终角(reader case 15 对称)。选 a0,a1 使 CCW(a0→a1)经中点。
                {
                    var cc = ArcMath.Circumcircle(ar.X1, ar.Y1, ar.X2, ar.Y2, ar.X3, ar.Y3);
                    if (cc == null) { Ent(1, ar, w => { XY(w, ar.X1, ar.Y1); XY(w, ar.X3, ar.Y3); }); break; }   // 三点共线 → 退化直线(忠实 Tessellate)
                    var (cx, cy, r) = cc.Value;
                    double aS = Math.Atan2(ar.Y1 - cy, ar.X1 - cx), aM = Math.Atan2(ar.Y2 - cy, ar.X2 - cx), aE = Math.Atan2(ar.Y3 - cy, ar.X3 - cx);
                    static double Sweep(double x, double y) { double s = y - x; while (s <= 0) s += 2 * Math.PI; while (s > 2 * Math.PI) s -= 2 * Math.PI; return s; }
                    double a0, a1;
                    if (Sweep(aS, aM) <= Sweep(aS, aE)) { a0 = aS; a1 = aE; } else { a0 = aE; a1 = aS; }   // 保 CCW 经中点(curve 恒等)
                    Ent(15, ar, w => { XY(w, cx, cy); w.Write(r); w.Write(a0); w.Write(a1); });
                    break;
                }
                // 其它复杂类型(MText/Hatch/标注/椭圆/样条): MVP 暂不导出
            }
        }
        byte[] entBytes;
        { using var f = new MemoryStream(); using var fw = new BinaryWriter(f); fw.Write(count); fw.Write(ems.ToArray()); entBytes = f.ToArray(); }

        // ── 串表段(实体建完后, strList/layerList 已定) ──
        // 层名也要进串表
        var layerNameIdx = new int[layerList.Count];
        for (int i = 0; i < layerList.Count; i++) layerNameIdx[i] = Intern(layerList[i]);

        byte[] strBytes;
        { using var s = new MemoryStream(); using var sw = new BinaryWriter(s); sw.Write(strList.Count); foreach (var str in strList) { var b = Encoding.UTF8.GetBytes(str); if (b.Length >= 0xFFFF) { sw.Write((ushort)0xFFFF); sw.Write(b.Length); } else sw.Write((ushort)b.Length); sw.Write(b); } strBytes = s.ToArray(); }

        byte[] layerBytes;
        { using var s = new MemoryStream(); using var sw = new BinaryWriter(s); sw.Write(layerList.Count); for (int i = 0; i < layerList.Count; i++) { sw.Write(layerNameIdx[i]); sw.Write((byte)2); sw.Write((byte)200); sw.Write((byte)200); sw.Write((byte)200); sw.Write((byte)0); sw.Write((byte)0); sw.Write(new byte[3]); } layerBytes = s.ToArray(); }

        byte[] styleBytes; { using var s = new MemoryStream(); using var sw = new BinaryWriter(s); sw.Write(0); styleBytes = s.ToArray(); }

        // ── 组装: Header(32) + 段表(4×24=96) + 段 + Footer(16) ──
        int hdrTable = 32 + 4 * 24;
        long sOff = hdrTable, lOff = sOff + strBytes.Length, tOff = lOff + layerBytes.Length, eOff = tOff + styleBytes.Length;
        long bodyEnd = eOff + entBytes.Length;
        long fileSize = bodyEnd + 16;

        using var ms = new MemoryStream(); using var w2 = new BinaryWriter(ms);
        w2.Write(MagicStart); w2.Write(1u); w2.Write(0u); w2.Write(fileSize); w2.Write(32L); w2.Write(4);
        void Sec(int id, long off, long size) { w2.Write(id); w2.Write(0); w2.Write(off); w2.Write(size); }
        Sec(1, sOff, strBytes.Length); Sec(2, lOff, layerBytes.Length); Sec(3, tOff, styleBytes.Length); Sec(6, eOff, entBytes.Length);
        w2.Write(strBytes); w2.Write(layerBytes); w2.Write(styleBytes); w2.Write(entBytes);
        // Footer: entityCount(8) + crc32(4, over [0,fileSize-16)) + magicEnd(4)
        w2.Write((long)count);
        uint crc = Crc32(ms.GetBuffer(), 0, (int)(fileSize - 16));
        w2.Write(crc); w2.Write(MagicEnd);
        return (ms.ToArray(), count);
    }

    // CRC32 IEEE(多项式 0xEDB88320)
    static readonly uint[] CrcTable = BuildCrcTable();
    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }
    static uint Crc32(byte[] buf, int off, int len)
    {
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < len; i++) c = CrcTable[(c ^ buf[off + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
