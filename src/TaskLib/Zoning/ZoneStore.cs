// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneStore.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  作业区域台账的读写 —— 落点就是 GeoDataBase 的 mineable_region。
//
//  ── 为什么不另起一张表 ──
//  这张表已经是全项目共用的「作业区域」注册表：三维推演的区域轮廓
//  （SimRegionLoader.Load 的第一顺位）、路网中心线提取的裁剪范围
//  （RoadLibPlugin.LoadClipRegions）、短期计划的「采场/排土场圈定」都读它。
//  本窗口再造一张表 = 现场画完区域，推演里什么也不会变。
//
//  ── 这里唯一的算法是「Z 从哪来」──
//  正射影像只有 XY，画在影像上的点没有高程。而 SimRegionLoader 解析 points_json 时
//  是这么写的（SimRegions.ParseXyz）：
//        return (ring, zn > 0 ? zs / zn : double.NaN);
//  只要有 ≥3 个点 zn 恒 > 0 ⇒ 写 z=0 拿回来的是 **0.0 而不是 NaN**，
//  于是 ZSource 被标成「可采区域台账 points_json 的真实 xyz」，
//  ResolveElevations 里那条"没有 Z 就去现状面采"的兜底整条分支**不会跑**，
//  层体全部摆在 0m，界面还理直气壮说这是台账真值。
//
//  所以本类的纪律是：**落库前 Z 必须有出处**（现状面逐点插值，或人工填一个基准标高），
//  出处原样写进 note 列。解不出就拒绝入库，不补 0。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>区域边界的一个顶点（世界坐标，米）。</summary>
public readonly record struct ZonePoint(double X, double Y, double Z);

/// <summary>一块作业区域（= 一行 mineable_region）。</summary>
public sealed class ZoneRecord
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "mineable";
    public string Note { get; set; } = "";
    public long Visible { get; set; } = 1;
    /// <summary>
    /// 本期是否纳入作业范围。**与 <see cref="Visible"/> 是两件事**：Visible 管"画不画它"，
    /// 本项管"算不算数" —— 三维推演的推进轮廓（<c>SimRegionLoader.Load</c>）与路网中心线提取的
    /// 裁剪范围（<c>RoadLibPlugin.LoadClipRegions</c>）只认本项。见 V043。
    /// </summary>
    public bool Active { get; set; } = true;
    public List<ZonePoint> Ring { get; set; } = new();

    public bool IsDump => Category is "external_dump" or "internal_dump";

    /// <summary>范围闸门（两个工作帮）：采场 / 排土场的子区域，不是一块地，不参与推进。</summary>
    public bool IsWorkingSlope => Category is "pit_working_slope" or "dump_working_slope";

    /// <summary>采场类（含未分类的「可采区域」）——与 <see cref="SimRegion.IsPit"/> 同一判据（工作帮两类都不算）。</summary>
    public bool IsPit => !IsDump && !IsWorkingSlope;

    public int PointCount => Ring.Count;
    public bool RingUsable => Ring.Count >= 3;

    /// <summary>多边形面积 m²（绝对值，鞋带公式）。</summary>
    public double AreaM2
    {
        get
        {
            if (Ring.Count < 3) return 0;
            double a = 0;
            for (int i = 0; i < Ring.Count; i++)
            {
                var p = Ring[i]; var q = Ring[(i + 1) % Ring.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return Math.Abs(a / 2);
        }
    }

    public double AvgZ => Ring.Count == 0 ? double.NaN : Ring.Average(p => p.Z);

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        if (Ring.Count == 0) return (0, 0, 0, 0);
        return (Ring.Min(p => p.X), Ring.Min(p => p.Y), Ring.Max(p => p.X), Ring.Max(p => p.Y));
    }

    /// <summary>note 里记的高程出处（<see cref="ZoneElevation"/> 写的那一段）。没有就返回空串。</summary>
    public string ZProvenance
    {
        get
        {
            const string tag = ZoneElevation.NoteTag;
            if (string.IsNullOrEmpty(Note)) return "";
            int i = Note.IndexOf(tag, StringComparison.Ordinal);
            if (i < 0) return "";
            int end = Note.IndexOf('\n', i);
            return (end < 0 ? Note[(i + tag.Length)..] : Note[(i + tag.Length)..end]).Trim();
        }
    }
}

/// <summary>作业区域台账（mineable_region）的读写。所有方法永不抛，失败以返回值 + 文案表达。</summary>
public static class ZoneStore
{
    /// <summary>类别键（与 mineable_region.category 一致，全项目同一套）。</summary>
    // ⚠ 只许在**末尾**追加：作业区域窗口的类别下拉是按下标映射到本表的，插在中间会把已有区域改判成别的类。
    public static readonly string[] CategoryKeys =
        { "pit", "external_dump", "internal_dump", "mineable", "pit_working_slope", "dump_working_slope" };

    /// <summary>
    /// 类别 → 中文名。<b>只转发 <see cref="MineableRegion.DisplayName"/></b> ——
    /// 值域与显示名都在实体那一层定义，这里再写一份 switch 就会像此前那样：实体侧把
    /// <c>mineable</c> 的显示名改成"未分类"、又加了两个工作帮，这个窗口还在把它们全显示成"可采区域"。
    /// </summary>
    public static string CategoryZh(string category) => MineableRegion.DisplayName(category);

    /// <summary>类别默认色 0xRRGGBB —— 与短期「采场/排土场圈定」的 overlay 配色对齐，两处看到的是同一批颜色。</summary>
    public static uint CategoryColor(string category) => category switch
    {
        "pit" => 0xD85A30u,            // 采场 橙
        "external_dump" => 0x2E6FCFu,  // 外排土场 蓝
        "internal_dump" => 0x1D9E75u,  // 内排土场 青
        // 两个工作帮：与母范围同色系但更亮（与短期「采场/排土场圈定」的 CategoryColor 一一对齐）。
        "pit_working_slope" => 0xF2B14Au,   // 剥采工作帮 琥珀
        "dump_working_slope" => 0x6FB7F5u,  // 排土工作帮 亮蓝
        _ => 0x26D973u,                // 未分类 绿
    };

    /// <summary>最近一次读写的结果文案（界面直接显示）。</summary>
    public static string LastLabel { get; private set; } = "";

    private static IMineableRegionService? Service()
    {
        try { return PitMine3D.Kylin.Data.EquipmentDataContext.MineableRegion; }
        catch (Exception ex) { LastLabel = $"作业区域台账不可用（{Short(ex)}）"; return null; }
    }

    /// <summary>台账是否可用（GeoDataBase 已就绪）。</summary>
    public static bool Available => Service() != null;

    /// <summary>读全表。台账不可用 / 读失败返回空表，原因写进 <see cref="LastLabel"/>。</summary>
    public static List<ZoneRecord> LoadAll()
    {
        var list = new List<ZoneRecord>();
        var svc = Service();
        if (svc == null) return list;

        try
        {
            foreach (var e in svc.All())
            {
                var ring = ParseRing(e.PointsJson);
                list.Add(new ZoneRecord
                {
                    Id = e.Id,
                    Name = e.Name ?? "",
                    Category = string.IsNullOrWhiteSpace(e.Category) ? "mineable" : e.Category,
                    Note = e.Note ?? "",
                    Visible = e.Visible,
                    Active = e.Active != 0,
                    Ring = ring,
                });
            }
            int on = list.Count(z => z.Active);
            LastLabel = list.Count > 0
                ? $"作业区域台账：{list.Count} 块，本期选定 {on} 块进推演"
                  + (on < list.Count ? $"（{list.Count - on} 块未选定，不参与推演与路网裁剪）" : "")
                : "作业区域台账为空 —— 推演会退到**示意图形**（尺寸由工程量反推，位置不代表真实边界）";
        }
        catch (Exception ex) { LastLabel = $"读台账失败（{Short(ex)}）"; }
        return list;
    }

    /// <summary>入库一块新区域。ring 须已带真 Z（<see cref="ZoneElevation"/> 解出的）。成功返回新 id，失败返回 0。</summary>
    /// <param name="extraNote">附加说明（自动生成时记期次/来源/单元清单）。写在 Z 出处那一行之后。</param>
    public static long Insert(string name, string category, IReadOnlyList<ZonePoint> ring, string zProvenance,
                              string extraNote = "")
    {
        var svc = Service();
        if (svc == null) return 0;
        if (ring.Count < 3) { LastLabel = "至少 3 个点才能成区域"; return 0; }

        try
        {
            var e = new MineableRegion
            {
                Name = name,
                Category = category,
                PointsJson = SerializeRing(ring),
                Visible = 1,
                Note = ComposeNote(zProvenance, extraNote),
            };
            long id = svc.Insert(e);
            LastLabel = $"已入库「{name}」（{ring.Count} 点）";
            return id;
        }
        catch (Exception ex) { LastLabel = $"入库失败（{Short(ex)}）"; return 0; }
    }

    /// <summary>
    /// 只换几何 —— <b>名称/类别/选定/id 一律不动</b>。
    ///
    /// <para>自动生成的"更新同名区域"、以及手工拖顶点/平移/等距缩放都走这一条：
    /// 区域的**身份**（谁在引用它、推演里配的是哪个源）挂在名字与 id 上，
    /// 换几何时把身份一起换掉，等于把这块区域从别处的引用里悄悄摘走。</para>
    /// </summary>
    /// <param name="zProvenance">新几何的 Z 出处；空串 = 沿用原来 note 里那一段。</param>
    public static bool UpdateRing(ZoneRecord rec, IReadOnlyList<ZonePoint> ring, string zProvenance = "",
                                  string extraNote = "")
    {
        var svc = Service();
        if (svc == null) return false;
        if (ring.Count < 3) { LastLabel = "至少 3 个点才能成区域 —— 几何没有改"; return false; }
        try
        {
            var e = svc.Get(rec.Id);
            if (e == null) { LastLabel = "该区域已不在台账里（可能被别处删了），请刷新"; return false; }

            string prov = zProvenance.Length > 0 ? zProvenance : rec.ZProvenance;
            e.PointsJson = SerializeRing(ring);
            e.Note = ComposeNote(prov, extraNote);
            svc.Update(e);

            rec.Ring = ring.ToList();
            rec.Note = e.Note;
            LastLabel = $"已更新「{rec.Name}」的边界（{ring.Count} 点，{rec.AreaM2 / 1e4:0.##} 万m²）";
            return true;
        }
        catch (Exception ex) { LastLabel = $"更新失败（{Short(ex)}）"; return false; }
    }

    /// <summary>note 的固定格式：第一行是 Z 出处（<see cref="ZoneRecord.ZProvenance"/> 按它解析），其后随意。</summary>
    private static string ComposeNote(string zProvenance, string extraNote)
    {
        string note = ZoneElevation.NoteTag + (zProvenance ?? "").Replace('\n', ' ').Trim();
        if (!string.IsNullOrWhiteSpace(extraNote)) note += "\n" + extraNote.Trim();
        return note;
    }

    /// <summary>改名 / 改类别（几何与 note 原样保留）。</summary>
    public static bool UpdateMeta(ZoneRecord rec, string name, string category)
    {
        var svc = Service();
        if (svc == null) return false;
        try
        {
            var e = svc.Get(rec.Id);
            if (e == null) { LastLabel = "该区域已不在台账里（可能被别处删了），请刷新"; return false; }
            e.Name = name;
            e.Category = category;
            svc.Update(e);
            rec.Name = name;
            rec.Category = category;
            LastLabel = $"已更新「{name}」";
            return true;
        }
        catch (Exception ex) { LastLabel = $"更新失败（{Short(ex)}）"; return false; }
    }

    /// <summary>改「本期选定」。几何/名称/类别/note 原样保留。</summary>
    public static bool SetActive(ZoneRecord rec, bool active)
    {
        var svc = Service();
        if (svc == null) return false;
        try
        {
            var e = svc.Get(rec.Id);
            if (e == null) { LastLabel = "该区域已不在台账里（可能被别处删了），请刷新"; return false; }
            e.Active = active ? 1 : 0;
            svc.Update(e);
            rec.Active = active;
            LastLabel = active
                ? $"「{rec.Name}」已纳入本期作业范围（进推演 + 进路网裁剪）"
                : $"「{rec.Name}」已移出本期作业范围 —— 台账里还在，但不进推演、不参与路网裁剪";
            return true;
        }
        catch (Exception ex) { LastLabel = $"更新失败（{Short(ex)}）"; return false; }
    }

    public static bool Delete(long id)
    {
        var svc = Service();
        if (svc == null) return false;
        try { svc.Delete(id); LastLabel = "已删除"; return true; }
        catch (Exception ex) { LastLabel = $"删除失败（{Short(ex)}）"; return false; }
    }

    /// <summary>按类别取下一个不重名的区域名（与短期圈定同一套命名，两处不会重号）。</summary>
    public static string NextName(string category, IEnumerable<ZoneRecord> existing)
    {
        var used = new HashSet<string>(existing.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        string prefix = CategoryZh(category);
        for (int i = 1; ; i++)
        {
            string nm = $"{prefix}{i}";
            if (!used.Contains(nm)) return nm;
        }
    }

    // ── 序列化：扁平 [x0,y0,z0,x1,y1,z1,...]，与 SimRegions.ParseXyz / RoadLib 裁剪同一口径 ──

    public static string SerializeRing(IReadOnlyList<ZonePoint> ring)
    {
        var flat = new double[ring.Count * 3];
        for (int i = 0; i < ring.Count; i++)
        {
            flat[3 * i] = ring[i].X;
            flat[3 * i + 1] = ring[i].Y;
            flat[3 * i + 2] = ring[i].Z;
        }
        return JsonSerializer.Serialize(flat);
    }

    public static List<ZonePoint> ParseRing(string? json)
    {
        var ring = new List<ZonePoint>();
        if (string.IsNullOrWhiteSpace(json)) return ring;
        try
        {
            var flat = JsonSerializer.Deserialize<double[]>(json);
            if (flat == null) return ring;
            int n = flat.Length / 3;
            for (int i = 0; i < n; i++)
                ring.Add(new ZonePoint(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]));
        }
        catch { ring.Clear(); }
        return ring;
    }

    /// <summary>点在多边形内（射线法，只看 XY）。新区域与已有区域重叠时的提示用。</summary>
    public static bool Contains(IReadOnlyList<ZonePoint> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double xi = ring[i].X, yi = ring[i].Y, xj = ring[j].X, yj = ring[j].Y;
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi + double.Epsilon) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 70 ? m : m[..70] + "…";
    }
}

/// <summary>一次高程解算的结果。</summary>
public sealed class ZoneZResult
{
    public bool Ok { get; init; }
    /// <summary>带 Z 的顶点（Ok=false 时为空）。</summary>
    public List<ZonePoint> Ring { get; init; } = new();
    /// <summary>高程出处，原样写进 note 列，界面也直接显示这一句。</summary>
    public string Provenance { get; init; } = "";
    /// <summary>失败原因 / 降级说明（Ok=true 时也可能有话说）。</summary>
    public string Message { get; init; } = "";
    public int Hit { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// 画在正射影像上的点只有 XY —— 这里负责把 Z 补齐，并且**必须说出补的是哪来的**。
/// <para>
/// 两条路：① 文档里的现状面三角网逐点插值（首选，<see cref="SimTerrainSampler"/>）；
/// ② 人工填一个基准标高（现状面不在图上时的诚实退路，全环同一个 Z）。
/// 两条都不成立就返回 Ok=false —— 宁可不入库，也不写 0（见本文件头的说明）。
/// </para>
/// </summary>
public static class ZoneElevation
{
    /// <summary>note 里高程出处那一段的前缀。</summary>
    public const string NoteTag = "Z来源：";

    /// <summary>现状面探测结果（开窗时先问一次，别等到入库那一刻才发现没有面）。</summary>
    public readonly record struct TerrainProbe(bool Available, string Reason, int TriangleCount)
    {
        /// <summary>界面直接显示的一行。</summary>
        public string Caption => Available
            ? $"高程自动：现状面三角网可用（{TriangleCount:N0} 三角），框好的顶点逐个投影插值取 Z —— 基准标高留空即可。"
            : $"⚠ 取不到现状面（{Reason}）：**必须先填基准标高**才能入库。"
            + "不会替你补 0 —— 写进去的 0m 会被推演当成台账实测高程，层体整体摆到 0 米，而界面上看不出任何异常。";
    }

    /// <summary>
    /// 探一次文档里有没有现状面三角网。永不抛。
    /// <para>放在开窗时问：否则人对着影像框完一个区，才在入库那一刻被告知"没有现状面，去填标高"，
    /// 白框一次。这条与 <see cref="Resolve"/> 走的是同一个 <see cref="SimTerrainSampler"/>，
    /// 不另写探测逻辑（探测说有、入库说没有，是最难查的一类不一致）。</para>
    /// </summary>
    public static TerrainProbe Probe()
    {
        try
        {
            var t = SimTerrainSampler.FromDocument();
            if (t == null) return new TerrainProbe(false, "文档中没有可用的现状面", 0);
            return new TerrainProbe(t.Available, t.Reason, t.Available ? t.TriangleCount : 0);
        }
        catch (Exception ex) { return new TerrainProbe(false, ex.Message, 0); }
    }

    /// <summary>
    /// 给一条平面环解 Z。
    /// </summary>
    /// <param name="xy">平面顶点（画布上点出来的）。</param>
    /// <param name="manualZ">人工基准标高；给了就整环用它，不去采现状面。</param>
    public static ZoneZResult Resolve(IReadOnlyList<(double X, double Y)> xy, double? manualZ)
    {
        if (xy.Count < 3)
            return new ZoneZResult { Ok = false, Message = "至少 3 个点才能成区域" };

        if (manualZ is { } mz)
        {
            if (double.IsNaN(mz) || double.IsInfinity(mz))
                return new ZoneZResult { Ok = false, Message = "基准标高不是有效数字" };
            return new ZoneZResult
            {
                Ok = true,
                Ring = xy.Select(p => new ZonePoint(p.X, p.Y, mz)).ToList(),
                Provenance = $"人工基准标高 {mz.ToString("0.##", CultureInfo.InvariantCulture)}m（全环同一 Z，非实测）",
                Message = $"整环按 {mz:0.##}m 摆放：形状与面积是真的，**绝对高程是人填的**。",
                Total = xy.Count,
            };
        }

        SimTerrainSampler? terrain;
        try { terrain = SimTerrainSampler.FromDocument(); }
        catch { terrain = null; }

        if (terrain == null || !terrain.Available)
            return new ZoneZResult
            {
                Ok = false,
                Total = xy.Count,
                Message = $"现状面三角网取不到（{terrain?.Reason ?? "文档中没有可用的现状面"}）—— "
                        + "请在下方填一个基准标高再入库。不会替你补 0：写进去的 0m 会被推演当成台账实测高程，"
                        + "层体整体摆到 0 米，而界面上看不出任何异常。",
            };

        var ring = new List<ZonePoint>(xy.Count);
        var zs = new double[xy.Count];
        int hit = 0;
        for (int i = 0; i < xy.Count; i++)
        {
            double z = terrain.SampleZ(xy[i].X, xy[i].Y);
            zs[i] = z;
            if (!double.IsNaN(z)) hit++;
        }

        if (hit == 0)
            return new ZoneZResult
            {
                Ok = false,
                Total = xy.Count,
                Message = $"{xy.Count} 个顶点全部落在现状面覆盖范围之外（现状面：{terrain.Reason}）—— "
                        + "多半是影像与工程不在同一套坐标系，或这块区域超出了现状面的范围。"
                        + "请先核对配准，或填一个基准标高再入库。",
            };

        // 部分命中：没命中的点用命中点的均值补 —— 比留 NaN 好，也比补 0 诚实（出处会写清楚补了几个）
        double mean = zs.Where(z => !double.IsNaN(z)).Average();
        for (int i = 0; i < xy.Count; i++)
            ring.Add(new ZonePoint(xy[i].X, xy[i].Y, double.IsNaN(zs[i]) ? mean : zs[i]));

        string prov = $"现状面三角网 XY 投影插值（{hit}/{xy.Count} 点命中"
                    + (hit < xy.Count ? $"，其余 {xy.Count - hit} 点取命中均值 {mean:0.##}m" : "")
                    + $"；{terrain.TriangleCount} 三角）";

        return new ZoneZResult
        {
            Ok = true,
            Ring = ring,
            Provenance = prov,
            Hit = hit,
            Total = xy.Count,
            Message = hit < xy.Count
                ? $"⚠ 有 {xy.Count - hit} 个顶点落在现状面之外，已按命中点均值 {mean:0.##}m 补 —— 这几处的绝对高程不可信。"
                : $"高程取自现状面三角网插值（均值 {ring.Average(p => p.Z):0.##}m）。",
        };
    }
}
