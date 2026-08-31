using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// WeCAD KDF 二进制格式导出 —— 忠实复刻原 KdfWriter 字节布局(magic "wecad_bin_version_2021")。
/// 场景实体 → KDF: 直线/多段线/矩形/多边形/圆/圆弧 → AcDb3DPolyline(折线化), 文字 → AcDbText。
/// 点无对应 KDF 实体故跳过。公共头 = u32 字段数 + tag06 图层(GBK) + 3B BGR + 6B 零 + double 1.0 + 2B 00 01 + u32 0。
/// 与 <see cref="KdfImportService"/> 往返一致(导出→导入 实体/图层/色 保真)。纯字节, BuildBytes 可单测。
/// </summary>
public static class KdfExportService
{
    private const string Magic = "wecad_bin_version_2021\n";   // 23 字节, 头长度字节 = 23

    /// <summary>导出场景到 .kdf；返回写出实体数(不含跳过的点)。</summary>
    public static int Export(Scene scene, string path, LayerTable? layers = null)
    {
        var (bytes, n) = BuildBytes(scene, layers);
        File.WriteAllBytes(path, bytes);
        return n;
    }

    private static Encoding Gbk()
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding("GBK"); } catch { return Encoding.UTF8; }
    }

    /// <summary>场景 → KDF 字节(可单测)；返回(字节, 写出实体数)。</summary>
    public static (byte[] bytes, int count) BuildBytes(Scene scene, LayerTable? layers = null)
    {
        var gbk = Gbk();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // ── 文件头 ──
        byte[] magic = Encoding.ASCII.GetBytes(Magic);
        w.Write((byte)magic.Length);
        w.Write(magic);

        // ── BlockTable / Model_Space 骨架(复刻原写法, 供 WeCAD 自家 reader 对接; Kylin reader 前扫跳过) ──
        ClassName(w, "AcDbBlockTable"); w.Write((uint)1);
        ClassName(w, "AcDbBlockTableRecord"); Lp(w, Encoding.ASCII, "*Model_Space");
        w.Write((byte)0x28); w.Write(new byte[39]);
        ClassName(w, "AcDbPlotSettings"); w.Write((uint)0);

        // ── LayerTable + records(供图层面板色/开关) ──
        var layerRecs = CollectLayers(scene, layers);
        if (layerRecs.Count > 0)
        {
            ClassName(w, "AcDbLayerTable"); w.Write((uint)layerRecs.Count);
            foreach (var (name, r, g, b, vis, locked) in layerRecs)
                LayerRecord(w, gbk, name, r, g, b, vis, locked);
        }

        // ── 实体 ──
        int n = 0;
        foreach (var e in scene.Entities)
        {
            var (br, bg, bb) = Bgr(e);
            if (e is TextEntity t)
            {
                if (string.IsNullOrEmpty(t.Text)) continue;
                if (t.Text.Contains('\n')) WriteMText(w, gbk, e.LayerName, br, bg, bb, t);   // 多行→AcDbMText(忠实原 KdfWriter)
                else WriteText(w, gbk, e.LayerName, br, bg, bb, t);
                n++;
            }
            else
            {
                var pts = PolyPts(e);
                if (pts == null || pts.Count < 2) continue;   // 点等无折线表示 → 跳过
                WritePolyline(w, gbk, e.LayerName, br, bg, bb, pts);
                n++;
            }
        }

        w.Flush();
        return (ms.ToArray(), n);
    }

    // ── 图层收集: 有 LayerTable 用之, 否则从实体层名去重(色取该层某实体色) ──
    private static List<(string name, byte r, byte g, byte b, bool vis, bool locked)> CollectLayers(Scene scene, LayerTable? layers)
    {
        var res = new List<(string, byte, byte, byte, bool, bool)>();
        if (layers != null)
        {
            foreach (var l in layers.Layers)
                res.Add((l.Name, C(l.Cr), C(l.Cg), C(l.Cb), l.Visible, l.Locked));
            return res;
        }
        var seen = new HashSet<string>();
        foreach (var e in scene.Entities)
        {
            string nm = string.IsNullOrEmpty(e.LayerName) ? "0" : e.LayerName;
            if (seen.Add(nm)) res.Add((nm, C(e.Cr), C(e.Cg), C(e.Cb), true, false));
        }
        return res;
    }

    // ── 场景实体 → 折线顶点(世界坐标, z=0) ──
    private static List<(double x, double y)>? PolyPts(SceneEntity e)
    {
        switch (e)
        {
            case LineEntity l: return new() { (l.X0, l.Y0), (l.X1, l.Y1) };
            case PolylineEntity pl:
            {
                var pts = new List<(double, double)>(pl.Points);
                if (pl.Closed && pts.Count >= 2) pts.Add(pts[0]);   // 闭合→回到首点(reader 不读 closed 位)
                return pts;
            }
            case RectEntity r: return new() { (r.X0, r.Y0), (r.X1, r.Y0), (r.X1, r.Y1), (r.X0, r.Y1), (r.X0, r.Y0) };
            case PolygonEntity pg:
            {
                var pts = new List<(double, double)>();
                for (int i = 0; i <= pg.Sides; i++)
                {
                    double ang = pg.Rotation + 2 * Math.PI * i / pg.Sides;
                    pts.Add((pg.Cx + pg.Radius * Math.Cos(ang), pg.Cy + pg.Radius * Math.Sin(ang)));
                }
                return pts;
            }
            case CircleEntity c:
            {
                var pts = new List<(double, double)>();
                const int N = 72;
                for (int i = 0; i <= N; i++)
                {
                    double ang = 2 * Math.PI * i / N;
                    pts.Add((c.Cx + c.Radius * Math.Cos(ang), c.Cy + c.Radius * Math.Sin(ang)));
                }
                return pts;
            }
            case ArcEntity a: return ArcPts(a);
            default: return null;   // Point 等无折线表示
        }
    }

    private static List<(double x, double y)>? ArcPts(ArcEntity a)
    {
        var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
        if (cc == null) return new() { (a.X1, a.Y1), (a.X3, a.Y3) };   // 退化→直线
        var (cx, cy, r) = cc.Value;
        double a1 = Math.Atan2(a.Y1 - cy, a.X1 - cx), am = Math.Atan2(a.Y2 - cy, a.X2 - cx), a3 = Math.Atan2(a.Y3 - cy, a.X3 - cx);
        double N(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
        bool ccw = N(am - a1) <= N(a3 - a1);
        double sweep = ccw ? N(a3 - a1) : -N(a1 - a3);
        int steps = Math.Max(8, (int)(Math.Abs(sweep) / (2 * Math.PI) * 72));
        var pts = new List<(double, double)>();
        for (int i = 0; i <= steps; i++)
        {
            double ang = a1 + sweep * i / steps;
            pts.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
        }
        return pts;
    }

    // ── 字节写入(复刻原 KdfWriter) ──
    private static void ClassName(BinaryWriter w, string name)
    {
        byte[] b = Encoding.ASCII.GetBytes(name);
        w.Write((byte)b.Length); w.Write(b);
    }

    private static void Lp(BinaryWriter w, Encoding enc, string s)
    {
        byte[] b = enc.GetBytes(s ?? "");
        if (b.Length > 255) b = b[..255];
        w.Write((byte)b.Length); w.Write(b);
    }

    private static void EntityCommon(BinaryWriter w, Encoding gbk, uint fieldCount, string layer, byte br, byte bg, byte bb)
    {
        w.Write(fieldCount);
        w.Write((byte)0x06);
        Lp(w, gbk, string.IsNullOrEmpty(layer) ? "0" : layer);
        w.Write(bb); w.Write(bg); w.Write(br);   // 3B BGR
        w.Write(new byte[6]);                     // 6B 保留
        w.Write(1.0);                             // double 线型比例
        w.Write((byte)0x00); w.Write((byte)0x01); // 2B flags
        w.Write((uint)0);                         // u32 handle
    }

    private static void WritePolyline(BinaryWriter w, Encoding gbk, string layer, byte br, byte bg, byte bb, List<(double x, double y)> pts)
    {
        ClassName(w, "AcDb3DPolyline");
        EntityCommon(w, gbk, 9, layer, br, bg, bb);
        w.Write((byte)0x02); w.Write(0.0);                                   // tag02 placeholder
        w.Write((byte)0x05); w.Write(0.0); w.Write(0.0); w.Write(1.0);       // tag05 normal
        w.Write((byte)0x00); w.Write((byte)0x00);                            // 2B closed flag
        w.Write((byte)0x02); w.Write(0.0);
        w.Write((byte)0x02); w.Write(0.0);
        w.Write((byte)0x0c); w.Write((uint)pts.Count);                       // tag0c 顶点数
        foreach (var (x, y) in pts)
        {
            w.Write(x); w.Write(y); w.Write(0.0);   // XYZ
            w.Write(new byte[8]);                    // 8B 保留
            w.Write((byte)0);                        // 1B strlen=0
        }
        w.Write((byte)0x00);                         // 1B end-of-entity
    }

    private static void WriteMText(BinaryWriter w, Encoding gbk, string layer, byte br, byte bg, byte bb, TextEntity t)
    {
        ClassName(w, "AcDbMText");
        EntityCommon(w, gbk, 14, layer, br, bg, bb);
        w.Write((byte)0x04); w.Write(t.X); w.Write(t.Y); w.Write(0.0);       // tag04 pos
        w.Write((byte)0x05); w.Write(0.0); w.Write(0.0); w.Write(1.0);       // tag05 normal
        w.Write((byte)0x05); w.Write(0.0); w.Write(0.0); w.Write(0.0);       // tag05 第二(变换向量)
        w.Write((byte)0x02); w.Write(t.Height > 0 ? t.Height : 5.0);         // 行高
        w.Write((byte)0x02); w.Write(1.0);
        w.Write((byte)0x01); w.Write((uint)0);                              // 可选 tag01
        w.Write((byte)0x03); Lp(w, gbk, t.Text.Replace("\n", "\r\n"));       // tag03 内容 \r\n 连接
        w.Write(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 }); w.Write((uint)0);
        w.Write((byte)0x03); Lp(w, Encoding.ASCII, "MAPGIS_SimSun ");        // tag03 字体名
    }

    private static void WriteText(BinaryWriter w, Encoding gbk, string layer, byte br, byte bg, byte bb, TextEntity t)
    {
        ClassName(w, "AcDbText");
        EntityCommon(w, gbk, 12, layer, br, bg, bb);
        w.Write((byte)0x04); w.Write(t.X); w.Write(t.Y); w.Write(0.0);       // tag04 pos
        w.Write((byte)0x05); w.Write(0.0); w.Write(0.0); w.Write(1.0);       // tag05 normal
        w.Write((byte)0x02); w.Write(t.Height > 0 ? t.Height : 5.0);         // 高度(idx0)
        w.Write((byte)0x02); w.Write(t.WidthFactor > 0 ? t.WidthFactor : 1.0); // 字宽(idx1)
        w.Write((byte)0x02); w.Write(t.Rotation);                            // 旋转(idx2)
        w.Write((byte)0x02); w.Write(t.ObliqueAngle);                        // 倾斜(idx3)
        w.Write((byte)0x03); Lp(w, gbk, t.Text);                             // tag03 内容 GBK
        w.Write(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 }); w.Write((uint)0);
        w.Write((byte)0x03); Lp(w, Encoding.ASCII, "MAPGIS_SimSun ");        // tag03 字体名
    }

    private static void LayerRecord(BinaryWriter w, Encoding gbk, string name, byte r, byte g, byte b, bool vis, bool locked)
    {
        ClassName(w, "AcDbLayerTableRecord");
        w.Write((uint)7);
        w.Write((byte)0x03); Lp(w, gbk, name);   // tag03 名(GBK)
        w.Write((byte)0x01);                      // 色模式占位
        w.Write(b); w.Write(g); w.Write(r);       // 3B BGR
        w.Write((byte)(vis ? 0x00 : 0x01));       // 可见
        w.Write((byte)(locked ? 0x01 : 0x00));    // 锁定
        w.Write((uint)0);
        w.Write(new byte[6]);
        w.Write((byte)0x03); w.Write((byte)0x00);
    }

    private static byte C(float f) => (byte)Math.Clamp((int)Math.Round(f * 255f), 0, 255);
    private static (byte r, byte g, byte b) Bgr(SceneEntity e) => (C(e.Cr), C(e.Cg), C(e.Cb));
}
