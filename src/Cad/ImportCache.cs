using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// DXF/DWG 导入结果的二进制缓存 —— 让"再次打开同一张图纸"快到秒级以内。
///
/// 为什么要它：几十 MB 的图纸，时间几乎全花在第三方 DWG 解析器里（51MB 实测 6.2 秒，
/// 我们自己的映射只占几十毫秒），把读取开关全关也只省 14%，动不了。既然同一张图纸
/// 反复打开是常态，就把**映射完的场景实体**按紧凑二进制存一份，下次直接读回来。
/// 实测 51MB / 50.7 万图元：JSON（SceneIO）要 3.9 秒、文件 343 MB，本格式不到 1 秒、几十 MB。
///
/// 失效判定只看源文件的**大小 + 修改时间 + 格式版本**：图纸一改（哪怕同名）三者必变一项，
/// 缓存自动作废重建。不算内容哈希 —— 为了省几秒去把 51MB 全读一遍算摘要，本末倒置。
///
/// 安全网：写入时遇到不认识的实体类型就整份放弃（不留半份缓存）；读取时任何一处对不上
/// （魔数/版本/戳记/计数）都返回 false，调用方老老实实回去解析。缓存坏了最多是慢，不该出错。
/// </summary>
public static class ImportCache
{
    private const string Magic = "PMCACHE";
    /// <summary>格式版本。实体字段增减必须 +1，否则旧缓存会被当成新格式读出乱数据。v2: 三维面合并成的三角网。</summary>
    private const int Version = 2;

    // 实体类型标签。DXF/DWG 导入只产出这 7 种（见 DxfImportService 的 Finalize / FinalizeMesh 调用处）。
    private const byte TLine = 1, TPolyline = 2, TPoint = 3, TCircle = 4, TArc = 5, TText = 6, TMesh = 7;

    /// <summary>缓存目录（用户数据目录下，可随时整个删掉）。</summary>
    public static string Dir
    {
        get
        {
            string baseDir = Path.GetDirectoryName(PitMine3D.Kylin.CrashLog.Path) ?? Path.GetTempPath();
            return Path.Combine(baseDir, "import-cache");
        }
    }

    /// <summary>某源文件对应的缓存文件路径（按完整路径散列，避免同名不同目录撞车）。</summary>
    public static string PathFor(string sourcePath)
    {
        ulong h = 1469598103934665603UL;                       // FNV-1a 64
        foreach (char c in sourcePath.ToLowerInvariant()) { h ^= c; h *= 1099511628211UL; }
        return Path.Combine(Dir, $"{h:x16}.pmc");
    }

    private static (long size, long mtime) Stamp(string sourcePath)
    {
        var fi = new FileInfo(sourcePath);
        return (fi.Length, fi.LastWriteTimeUtc.Ticks);
    }

    /// <summary>
    /// 尝试读缓存。命中返回 true 并给出与解析等价的导入结果；任何不一致都返回 false（照常解析）。
    /// </summary>
    public static bool TryLoad(string sourcePath, out DxfImportService.EntityImportResult result)
    {
        result = new DxfImportService.EntityImportResult();
        try
        {
            string cache = PathFor(sourcePath);
            if (!File.Exists(cache) || !File.Exists(sourcePath)) return false;
            var (size, mtime) = Stamp(sourcePath);

            using var fs = File.OpenRead(cache);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadString() != Magic) return false;
            if (r.ReadInt32() != Version) return false;
            if (r.ReadInt64() != size || r.ReadInt64() != mtime) return false;   // 源文件变了

            result.Bounds = new[] { r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble() };

            int layerCount = r.ReadInt32();
            var layers = new string[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                string name = r.ReadString();
                layers[i] = name;
                result.LayerOrder.Add(name);
                result.LayerColors[name] = (r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                bool has = r.ReadBoolean();
                if (has) result.LayerStates[name] = (r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean());
            }

            int typeCount = r.ReadInt32();
            for (int i = 0; i < typeCount; i++) result.TypeCounts[r.ReadString()] = r.ReadInt32();

            int centerCount = r.ReadInt32();
            result.Centers.Capacity = centerCount;
            for (int i = 0; i < centerCount; i++) result.Centers.Add((r.ReadDouble(), r.ReadDouble()));

            int warnCount = r.ReadInt32();
            for (int i = 0; i < warnCount; i++) result.Warnings.Add(r.ReadString());

            int n = r.ReadInt32();
            result.Entities.Capacity = n;
            for (int i = 0; i < n; i++)
            {
                var e = ReadEntity(r, layers);
                if (e == null) return false;
                result.Entities.Add(e);
            }
            if (r.ReadString() != Magic) return false;          // 尾标: 文件被截断就在这儿露馅
            return true;
        }
        catch
        {
            result = new DxfImportService.EntityImportResult();
            return false;
        }
    }

    /// <summary>写缓存（失败静默：缓存写不成只是下次还得慢一点，不该影响本次导入）。</summary>
    public static void Save(string sourcePath, DxfImportService.EntityImportResult res)
    {
        string cache = PathFor(sourcePath);
        string tmp = cache + ".tmp";
        try
        {
            Directory.CreateDirectory(Dir);
            var (size, mtime) = Stamp(sourcePath);

            var layerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < res.LayerOrder.Count; i++) layerIndex[res.LayerOrder[i]] = i;

            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic); w.Write(Version); w.Write(size); w.Write(mtime);
                for (int i = 0; i < 4; i++) w.Write(res.Bounds.Length > i ? res.Bounds[i] : 0.0);

                w.Write(res.LayerOrder.Count);
                foreach (var name in res.LayerOrder)
                {
                    w.Write(name);
                    var c = res.LayerColors.TryGetValue(name, out var cc) ? cc : (0.8f, 0.8f, 0.8f);
                    w.Write(c.Item1); w.Write(c.Item2); w.Write(c.Item3);
                    bool has = res.LayerStates.TryGetValue(name, out var st);
                    w.Write(has);
                    if (has) { w.Write(st.on); w.Write(st.frozen); w.Write(st.locked); }
                }

                w.Write(res.TypeCounts.Count);
                foreach (var kv in res.TypeCounts) { w.Write(kv.Key); w.Write(kv.Value); }

                w.Write(res.Centers.Count);
                foreach (var c in res.Centers) { w.Write(c.x); w.Write(c.y); }

                w.Write(res.Warnings.Count);
                foreach (var s in res.Warnings) w.Write(s);

                w.Write(res.Entities.Count);
                foreach (var e in res.Entities)
                    if (!WriteEntity(w, e, layerIndex)) throw new NotSupportedException($"缓存不支持的图元类型：{e.GetType().Name}");

                w.Write(Magic);
            }
            if (File.Exists(cache)) File.Delete(cache);
            File.Move(tmp, cache);                              // 先写临时文件再改名: 中途崩了也不会留半份
            PitMine3D.Kylin.CrashLog.Write("导入缓存", $"已写 {Path.GetFileName(cache)}（{new FileInfo(cache).Length / 1024 / 1024} MB, {res.Entities.Count} 图元）");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            PitMine3D.Kylin.CrashLog.Write("导入缓存", "写缓存失败(不影响本次导入)：" + ex.Message);
        }
    }

    // ── 逐实体读写 ───────────────────────────────────────────────────────────
    private static void WriteCommon(BinaryWriter w, SceneEntity e, Dictionary<string, int> layers)
    {
        w.Write(layers.TryGetValue(e.LayerName, out int li) ? li : -1);
        if (li < 0) w.Write(e.LayerName);                       // 不在图层表里就存名字本身
        w.Write(e.Cr); w.Write(e.Cg); w.Write(e.Cb);
        w.Write(e.Visible); w.Write(e.LineWeight); w.Write(e.Transparency); w.Write(e.Elevation);
        w.Write(e.Dash == null ? 0 : e.Dash.Length);
        if (e.Dash != null) foreach (double d in e.Dash) w.Write(d);
    }

    private static void ReadCommon(BinaryReader r, SceneEntity e, string[] layers)
    {
        int li = r.ReadInt32();
        e.LayerName = li >= 0 && li < layers.Length ? layers[li] : r.ReadString();
        e.Cr = r.ReadSingle(); e.Cg = r.ReadSingle(); e.Cb = r.ReadSingle();
        e.Visible = r.ReadBoolean(); e.LineWeight = r.ReadInt16(); e.Transparency = r.ReadInt16(); e.Elevation = r.ReadDouble();
        int dashLen = r.ReadInt32();
        if (dashLen > 0) { var d = new double[dashLen]; for (int i = 0; i < dashLen; i++) d[i] = r.ReadDouble(); e.Dash = d; }
    }

    private static bool WriteEntity(BinaryWriter w, SceneEntity e, Dictionary<string, int> layers)
    {
        switch (e)
        {
            case LineEntity l:
                w.Write(TLine); WriteCommon(w, l, layers);
                w.Write(l.X0); w.Write(l.Y0); w.Write(l.X1); w.Write(l.Y1); return true;
            case PolylineEntity p:
                w.Write(TPolyline); WriteCommon(w, p, layers);
                w.Write(p.Closed);
                w.Write(p.Points.Count);
                foreach (var (x, y) in p.Points) { w.Write(x); w.Write(y); }
                w.Write(p.Zs == null ? 0 : p.Zs.Count);
                if (p.Zs != null) foreach (double z in p.Zs) w.Write(z);
                return true;
            case PointEntity pt:
                w.Write(TPoint); WriteCommon(w, pt, layers);
                w.Write(pt.X); w.Write(pt.Y); w.Write(pt.Size); w.Write(pt.Style); return true;
            case CircleEntity c:
                w.Write(TCircle); WriteCommon(w, c, layers);
                w.Write(c.Cx); w.Write(c.Cy); w.Write(c.Radius); w.Write(c.Segments); return true;
            case ArcEntity a:
                w.Write(TArc); WriteCommon(w, a, layers);
                w.Write(a.X1); w.Write(a.Y1); w.Write(a.X2); w.Write(a.Y2); w.Write(a.X3); w.Write(a.Y3); w.Write(a.Segments); return true;
            case TextEntity t:
                w.Write(TText); WriteCommon(w, t, layers);
                w.Write(t.X); w.Write(t.Y); w.Write(t.Height); w.Write(t.Rotation);
                w.Write(t.HAlign); w.Write(t.VAlign); w.Write(t.WidthFactor); w.Write(t.ObliqueAngle);
                w.Write(t.ScreenFacing); w.Write(t.Text ?? ""); return true;
            case MeshEntity m:                                   // 三维面/网格合并网: 名 + 顶点 xyz + 三角索引
                w.Write(TMesh); WriteCommon(w, m, layers);
                w.Write(m.Name ?? "");
                w.Write(m.Verts.Count);
                foreach (var (x, y, z) in m.Verts) { w.Write(x); w.Write(y); w.Write(z); }
                w.Write(m.Tris.Count);
                foreach (var (a, b, c) in m.Tris) { w.Write(a); w.Write(b); w.Write(c); }
                return true;
            default:
                return false;                                    // 不认识 → 整份缓存作废
        }
    }

    private static SceneEntity? ReadEntity(BinaryReader r, string[] layers)
    {
        byte tag = r.ReadByte();
        switch (tag)
        {
            case TLine:
            {
                var l = new LineEntity(); ReadCommon(r, l, layers);
                l.X0 = r.ReadDouble(); l.Y0 = r.ReadDouble(); l.X1 = r.ReadDouble(); l.Y1 = r.ReadDouble(); return l;
            }
            case TPolyline:
            {
                var p = new PolylineEntity(); ReadCommon(r, p, layers);
                p.Closed = r.ReadBoolean();
                int n = r.ReadInt32();
                p.Points.Capacity = n;
                for (int i = 0; i < n; i++) p.Points.Add((r.ReadDouble(), r.ReadDouble()));
                int zn = r.ReadInt32();
                if (zn > 0) { p.Zs = new List<double>(zn); for (int i = 0; i < zn; i++) p.Zs.Add(r.ReadDouble()); }
                return p;
            }
            case TPoint:
            {
                var pt = new PointEntity(); ReadCommon(r, pt, layers);
                pt.X = r.ReadDouble(); pt.Y = r.ReadDouble(); pt.Size = r.ReadDouble(); pt.Style = r.ReadInt32(); return pt;
            }
            case TCircle:
            {
                var c = new CircleEntity(); ReadCommon(r, c, layers);
                c.Cx = r.ReadDouble(); c.Cy = r.ReadDouble(); c.Radius = r.ReadDouble(); c.Segments = r.ReadInt32(); return c;
            }
            case TArc:
            {
                var a = new ArcEntity(); ReadCommon(r, a, layers);
                a.X1 = r.ReadDouble(); a.Y1 = r.ReadDouble(); a.X2 = r.ReadDouble(); a.Y2 = r.ReadDouble();
                a.X3 = r.ReadDouble(); a.Y3 = r.ReadDouble(); a.Segments = r.ReadInt32(); return a;
            }
            case TText:
            {
                var t = new TextEntity(); ReadCommon(r, t, layers);
                t.X = r.ReadDouble(); t.Y = r.ReadDouble(); t.Height = r.ReadDouble(); t.Rotation = r.ReadDouble();
                t.HAlign = r.ReadInt32(); t.VAlign = r.ReadInt32(); t.WidthFactor = r.ReadDouble(); t.ObliqueAngle = r.ReadDouble();
                t.ScreenFacing = r.ReadBoolean(); t.Text = r.ReadString(); return t;
            }
            case TMesh:
            {
                var m = new MeshEntity(); ReadCommon(r, m, layers);
                m.Name = r.ReadString();
                int vn = r.ReadInt32();
                if (vn < 0) return null;
                m.Verts.Capacity = vn;
                for (int i = 0; i < vn; i++) m.Verts.Add((r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
                int tn = r.ReadInt32();
                if (tn < 0) return null;
                m.Tris.Capacity = tn;
                for (int i = 0; i < tn; i++)
                {
                    int a = r.ReadInt32(), b = r.ReadInt32(), c = r.ReadInt32();
                    if (a < 0 || b < 0 || c < 0 || a >= vn || b >= vn || c >= vn) return null;   // 索引越界 = 缓存坏了
                    m.Tris.Add((a, b, c));
                }
                return m;
            }
            default:
                return null;
        }
    }
}
