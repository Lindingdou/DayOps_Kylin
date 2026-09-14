using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 读原版 PitMine 工程文件 .pmx(私有二进制, 与 Kylin 自己的文本 .pmx 不同)——工程互操作: 在 Kylin 打开原版工程。
/// 格式规格见原 PmxFormat(managed, 非 native): Header(32B 'PMX1') + 段表(Strings/Layers/TextStyles/Entities) +
/// 每实体 recordLen(可跳过未知类型)。忠实移植核心实体: 线/点/多段线/文字/三角网(→棱线)/圆/圆弧;
/// 复杂类型(MText/Hatch/标注/椭圆/样条)按 recordLen 跳过(MVP, 读全档不崩)。little-endian(BinaryReader 默认)。
/// 纯逻辑、可单测(合成档往返)。
/// </summary>
public static class PmxImportService
{
    public sealed class Result
    {
        public bool Success;
        public string Error = "";
        public List<SceneEntity> Entities = new();
        public List<string> LayerNames = new();
        public int Lines, Points, Polylines, Texts, Meshes, Circles, Arcs, Skipped;
        public string Summary =>
            $"线 {Lines}·点 {Points}·多段线 {Polylines}·文字 {Texts}·网格 {Meshes}·圆 {Circles}·弧 {Arcs}" + (Skipped > 0 ? $"·跳过 {Skipped}(复杂类型)" : "");
    }

    const uint MagicStart = 0x31584D50;   // 'P','M','X','1'
    const int SecStrings = 1, SecLayers = 2, SecTextStyles = 3, SecEntities = 6;

    /// <summary>
    /// 是不是原版二进制工程(头 4 字节 'PMX1')。Kylin 自己存的 .pmx 是文本, 同后缀不同格式 ——
    /// 「打开」/文件管理器双击 据此分派, 否则拿文本解析器去读二进制只会报"打开失败"。
    /// 读不到(不存在/无权限)按 false 处理, 让后续按文本走并给出真实错误。
    /// </summary>
    public static bool IsOriginalPmx(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4 && head[0] == (byte)'P' && head[1] == (byte)'M' && head[2] == (byte)'X' && head[3] == (byte)'1';
        }
        catch { return false; }
    }

    public static Result Load(string path)
    {
        var res = new Result();
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { res.Error = ex.Message; return res; }
        if (bytes.Length < 32) { res.Error = "文件过小, 非 PMX"; return res; }

        try
        {
            using var ms = new MemoryStream(bytes, false);
            using var br = new BinaryReader(ms, Encoding.UTF8, false);

            uint magic = br.ReadUInt32();
            if (magic != MagicStart) { res.Error = $"magic 不符(0x{magic:X8}), 非 PMX1 工程(可能是 Kylin 文本 .pmx, 用『打开』)"; return res; }
            br.ReadUInt32();                 // version
            br.ReadUInt32();                 // flags
            br.ReadInt64();                  // fileSize
            long secTableOff = br.ReadInt64();
            int secCount = br.ReadInt32();
            if (secCount < 0 || secCount > 64) { res.Error = "段数异常"; return res; }

            long stringsOff = -1, layersOff = -1, stylesOff = -1, entsOff = -1;
            ms.Position = secTableOff;
            for (int i = 0; i < secCount; i++)
            {
                int id = br.ReadInt32();
                br.ReadInt32();              // reserved
                long off = br.ReadInt64();
                br.ReadInt64();              // size
                switch (id)
                {
                    case SecStrings: stringsOff = off; break;
                    case SecLayers: layersOff = off; break;
                    case SecTextStyles: stylesOff = off; break;
                    case SecEntities: entsOff = off; break;
                }
            }

            // ── Strings ──
            var strings = new List<string>();
            if (stringsOff >= 0)
            {
                ms.Position = stringsOff;
                int cnt = br.ReadInt32();
                for (int i = 0; i < cnt; i++)
                {
                    int len = br.ReadUInt16();
                    if (len == 0xFFFF) len = br.ReadInt32();
                    strings.Add(len > 0 ? Encoding.UTF8.GetString(br.ReadBytes(len)) : "");
                }
            }
            string Str(int idx) => (idx >= 0 && idx < strings.Count) ? strings[idx] : "";

            // ── Layers(名 + 色) ──
            var layerName = new List<string>();
            var layerColor = new List<(float r, float g, float b)?>();
            if (layersOff >= 0)
            {
                ms.Position = layersOff;
                int cnt = br.ReadInt32();
                for (int i = 0; i < cnt; i++)
                {
                    int nameIdx = br.ReadInt32();
                    byte mode = br.ReadByte();
                    byte b0 = br.ReadByte(), b1 = br.ReadByte(), b2 = br.ReadByte();
                    br.ReadByte();           // flags
                    br.ReadByte();           // lineWeight
                    br.ReadBytes(3);         // reserved
                    layerName.Add(Str(nameIdx));
                    // 图层色同样要认索引色: 现场工程多是 ByLayer + ACI, 只认 TrueColor 的话整张图都成默认灰
                    layerColor.Add(mode == 2 ? ((float, float, float)?)(b0 / 255f, b1 / 255f, b2 / 255f)
                                 : mode == 1 && b0 != 0 ? DxfImportService.AciToRgb(b0)
                                 : null);
                }
            }
            res.LayerNames = layerName;

            // ── TextStyles(宽度系数/倾斜角, 供文字) ──
            var styWidth = new List<double>();
            var styOblique = new List<double>();
            if (stylesOff >= 0)
            {
                ms.Position = stylesOff;
                int cnt = br.ReadInt32();
                for (int i = 0; i < cnt; i++)
                {
                    br.ReadInt32();          // nameStrIdx
                    br.ReadInt32();          // fontStrIdx
                    br.ReadDouble();         // height
                    styWidth.Add(br.ReadDouble());
                    styOblique.Add(br.ReadDouble());
                }
            }
            double StyW(int i) => (i >= 0 && i < styWidth.Count && styWidth[i] > 1e-9) ? styWidth[i] : 1.0;
            double StyO(int i) => (i >= 0 && i < styOblique.Count) ? styOblique[i] : 0.0;

            // ── Entities ──
            if (entsOff < 0) { res.Error = "无实体段"; return res; }
            ms.Position = entsOff;
            int ecount = br.ReadInt32();
            for (int e = 0; e < ecount; e++)
            {
                int recordLen = br.ReadInt32();
                long start = ms.Position;               // type 字段起
                long end = start + recordLen;
                try
                {
                    byte type = br.ReadByte();
                    int layerIdx = br.ReadInt32();
                    byte cmode = br.ReadByte();
                    byte c0 = br.ReadByte(), c1 = br.ReadByte(), c2 = br.ReadByte();
                    var (cr, cg, cb) = Color(cmode, c0, c1, c2, layerIdx, layerColor);
                    string lname = (layerIdx >= 0 && layerIdx < layerName.Count) ? layerName[layerIdx] : "0";
                    void Init(SceneEntity se) { se.Cr = cr; se.Cg = cg; se.Cb = cb; se.LayerName = lname; }

                    switch (type)
                    {
                        case 1:   // Line
                        {
                            var (ax, ay, az) = Xyz(br); var (bx, by, bz) = Xyz(br);
                            // 标高照收: 本实体模型每条线单一 Elevation, 两端不等高时取均值(同 DXF 导入口径)
                            var le = new LineEntity { X0 = ax, Y0 = ay, X1 = bx, Y1 = by, Elevation = (az + bz) * 0.5 }; Init(le);
                            res.Entities.Add(le); res.Lines++; break;
                        }
                        case 3:   // Point
                        {
                            var (px, py, pz) = Xyz(br);
                            var pe = new PointEntity { X = px, Y = py, Elevation = pz }; Init(pe);
                            res.Entities.Add(pe); res.Points++; break;
                        }
                        case 2:   // Polyline
                        {
                            byte flags = br.ReadByte();
                            int vc = br.ReadInt32();
                            var pts = new List<(double, double)>(Math.Max(0, vc));
                            var zs = new List<double>(Math.Max(0, vc));
                            for (int k = 0; k < vc; k++) { var (vx, vy, vz) = Xyz(br); pts.Add((vx, vy)); zs.Add(vz); }
                            var pl = new PolylineEntity { Closed = (flags & 1) != 0 }; pl.Points.AddRange(pts); Init(pl);
                            // 三维信息照收: 逐点变高(三维多段线/断面线)存 Zs; 等高(等高线/台阶线)只存 Elevation。
                            // 丢了这一步, 图在三维里就是压平的一张纸 —— 转起来看不出任何起伏。
                            SetZ(pl, zs);
                            res.Entities.Add(pl); res.Polylines++; break;   // bulge(圆弧段)MVP 忽略, 按直线
                        }
                        case 4:   // Text
                        {
                            var (px, py, pz) = Xyz(br); Xyz(br);          // pos, alignPt(忽略)
                            double h = br.ReadDouble(), rot = br.ReadDouble();
                            br.ReadByte(); br.ReadByte();                 // hAlign, vAlign
                            int txtIdx = br.ReadInt32(); int styIdx = br.ReadInt32();
                            var te = new TextEntity { X = px, Y = py, Height = h > 1e-9 ? h : 1, Rotation = rot, Text = Str(txtIdx), WidthFactor = StyW(styIdx), ObliqueAngle = StyO(styIdx) };
                            te.Elevation = pz;
                            Init(te); res.Entities.Add(te); res.Texts++; break;
                        }
                        case 7:   // TriangleMesh → 棱线(去重)
                        {
                            var (bxp, byp, bzp) = Xyz(br);
                            int vc = br.ReadInt32(); int ic = br.ReadInt32(); byte mflags = br.ReadByte();
                            var vx = new double[vc]; var vy = new double[vc]; var vz = new double[vc];
                            for (int k = 0; k < vc; k++) { vx[k] = bxp + br.ReadSingle(); vy[k] = byp + br.ReadSingle(); vz[k] = bzp + br.ReadSingle(); }
                            var seen = new HashSet<long>(PackedKeyComparer.Instance);
                            for (int k = 0; k + 2 < ic; k += 3)
                            {
                                int i0 = br.ReadInt32(), i1 = br.ReadInt32(), i2 = br.ReadInt32();
                                AddEdge(res, i0, i1, vx, vy, vz, seen, cr, cg, cb, lname);
                                AddEdge(res, i1, i2, vx, vy, vz, seen, cr, cg, cb, lname);
                                AddEdge(res, i2, i0, vx, vy, vz, seen, cr, cg, cb, lname);
                            }
                            res.Meshes++; break;
                        }
                        case 14:  // Circle
                        {
                            var (cx, cy, cz) = Xyz(br); double rad = br.ReadDouble();
                            var ce = new CircleEntity { Cx = cx, Cy = cy, Radius = rad, Elevation = cz }; Init(ce);
                            res.Entities.Add(ce); res.Circles++; break;
                        }
                        case 15:  // Arc → 3 点弧
                        {
                            var (cx, cy, cz) = Xyz(br); double rad = br.ReadDouble();
                            double a0 = br.ReadDouble(), a1 = br.ReadDouble();
                            double am = a0 + NormSweep(a0, a1) * 0.5;
                            var ae = new ArcEntity
                            {
                                X1 = cx + rad * Math.Cos(a0), Y1 = cy + rad * Math.Sin(a0),
                                X2 = cx + rad * Math.Cos(am), Y2 = cy + rad * Math.Sin(am),
                                X3 = cx + rad * Math.Cos(a1), Y3 = cy + rad * Math.Sin(a1),
                                Elevation = cz,
                            };
                            Init(ae); res.Entities.Add(ae); res.Arcs++; break;
                        }
                        default:  // MText/Hatch/Dimension/Ellipse/Spline… 按 recordLen 跳过
                            res.Skipped++; break;
                    }
                }
                catch { res.Skipped++; }
                ms.Position = end;                       // recordLen 权威: 无论读没读全, 定位到下一实体
            }

            res.Success = res.Entities.Count > 0 || ecount == 0;
            if (!res.Success && res.Error == "") res.Error = "未解析出任何可显示实体(可能全是复杂类型)";
            return res;
        }
        catch (Exception ex) { res.Error = $"解析失败: {ex.GetType().Name} {ex.Message}"; return res; }
    }

    static (double x, double y, double z) Xyz(BinaryReader br) => (br.ReadDouble(), br.ReadDouble(), br.ReadDouble());

    static double NormSweep(double a0, double a1) { double s = a1 - a0; while (s <= 0) s += 2 * Math.PI; while (s > 2 * Math.PI) s -= 2 * Math.PI; return s; }

    static void AddEdge(Result res, int i, int j, double[] vx, double[] vy, double[] vz, HashSet<long> seen, float cr, float cg, float cb, string lname)
    {
        if (i < 0 || j < 0 || i >= vx.Length || j >= vx.Length) return;
        long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
        if (!seen.Add(key)) return;
        res.Entities.Add(new LineEntity
        {
            X0 = vx[i], Y0 = vy[i], X1 = vx[j], Y1 = vy[j],
            Elevation = (vz[i] + vz[j]) * 0.5,      // 棱线取两端均值(每条线单一标高), 三角网的起伏得以保留
            Cr = cr, Cg = cg, Cb = cb, LayerName = lname,
        });
    }

    /// <summary>
    /// 把读到的逐点 Z 落到多段线上: 各点不等高 → 存 Zs(真三维, 落面/剖面/等值线都靠它);
    /// 各点等高(等高线/台阶线的常态) → 只存 Elevation, 省一份数组。全 0 则什么都不设(平面图元)。
    /// </summary>
    static void SetZ(PolylineEntity pl, List<double> zs)
    {
        if (zs.Count == 0 || zs.Count != pl.Points.Count) return;
        double min = zs[0], max = zs[0];
        foreach (var z in zs) { if (z < min) min = z; if (z > max) max = z; }
        if (max - min > 1e-9) pl.Zs = zs;            // 三维多段线: 逐点保 Z
        else if (Math.Abs(min) > 1e-12) pl.Elevation = min;   // 等高: 一个标高就够
    }

    /// <summary>
    /// 实体/图层的存色 → RGB。原版三种色模式:
    ///   0 = ByLayer   → 取所在图层的颜色
    ///   1 = Indexed   → AutoCAD 颜色索引(ACI), 索引存在第一个字节; 走与 DXF 导入同一张表, 保住图纸原色
    ///                   (索引 0 = ByBlock: 本系统无块概念, 按 ByLayer 处理)
    ///   2 = TrueColor → 直接是 RGB
    /// 认不出的索引/无色 → 导入默认(偏蓝灰), 同 DXF 导入的回落口径。
    /// </summary>
    static (float, float, float) Color(byte mode, byte c0, byte c1, byte c2, int layerIdx, List<(float r, float g, float b)?> layerColor)
    {
        if (mode == 2) return (c0 / 255f, c1 / 255f, c2 / 255f);                       // TrueColor 精确
        if (mode == 1 && c0 != 0) return DxfImportService.AciToRgb(c0);                // Indexed(ACI)
        if ((mode == 0 || (mode == 1 && c0 == 0))
            && layerIdx >= 0 && layerIdx < layerColor.Count && layerColor[layerIdx] is { } lc) return lc;   // ByLayer / ByBlock
        return (0.72f, 0.74f, 0.8f);                                                    // 无色 → 导入默认(偏蓝灰)
    }
}
