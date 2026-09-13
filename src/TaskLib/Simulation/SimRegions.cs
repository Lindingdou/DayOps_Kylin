// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimRegions.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  平面推进几何 —— 把「本期推进 N 米」变成一条会动的轮廓线。
//
//  轮廓来源三层（能拿真的绝不画假的）：
//    ① GeoDataBase 的 mineable_region 台账（「采场/排土场圈定」存的边界，带真实 XYZ）；
//    ② IPitDesignCapability.GetMineableRegions()（同一批区域的轻量只读快照，仅 XY）；
//    ③ 都没有 → **示意图形**：尺寸由真实工程量反推（排土场面积 = 设计容量 / 台阶高，
//       采场面积 = 工作线长 × 0.6 倍工作线长），并全程打「示意」标记。
//
//  推进偏移两种模式：
//    · 等距（Uniform）：全周等距内缩/外扩。不知道推进方位时的诚实简化。
//    · 定向（Directional）：只有朝向推进方位那一侧的顶点沿方位平移——对应「平行推进」，
//      坡底线沿推进方位偏移 v，正是需求里写的那条几何反算。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>平面点（世界坐标，米）。</summary>
public readonly record struct SimPoint(double X, double Y)
{
    public static SimPoint operator +(SimPoint a, SimPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static SimPoint operator -(SimPoint a, SimPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static SimPoint operator *(SimPoint a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>轮廓推进模式。</summary>
public enum SimAdvanceMode
{
    /// <summary>全周等距内缩/外扩（不知道推进方位时的简化）。</summary>
    Uniform,
    /// <summary>定向平移：朝向推进方位那一侧沿方位偏移（平行推进）。</summary>
    Directional,
}

/// <summary>一块作业区域的平面轮廓。</summary>
public sealed class SimRegion
{
    public string Name { get; set; } = "";
    /// <summary>mineable / pit / external_dump / internal_dump。</summary>
    public string Category { get; set; } = "mineable";
    public List<SimPoint> Ring { get; set; } = new();
    /// <summary>代表高程 m（台账里有 Z 时取均值；没有则 NaN）。</summary>
    public double Z { get; set; } = double.NaN;
    /// <summary>
    /// 高程来源（三层：可采区域台账真 xyz → 现状面三角网插值 → 0m 兜底）。界面按这句判断能不能信绝对高程。
    /// </summary>
    public string ZSource { get; set; } = "";
    /// <summary>
    /// 台阶坡面角 α(°)：排土类由 <see cref="SinkNode.BenchSlopeAngleDeg"/> 配汇时带过来；
    /// 采场类留 NaN，由 <see cref="SimMiningParams.FaceAngleDeg"/> 供给。NaN = 本区不自带坡面角。
    /// </summary>
    public double SlopeAngleDeg { get; set; } = double.NaN;
    /// <summary>示意图形（不是真实边界），界面必须标出来。</summary>
    public bool Synthetic { get; set; }
    /// <summary>关联的汇 Id（排土类区域配到 SinkNode 后填）。</summary>
    public string SinkId { get; set; } = "";

    public bool IsDump => Category is "external_dump" or "internal_dump";
    public bool IsInternalDump => Category == "internal_dump";

    /// <summary>
    /// 范围闸门（两个工作帮），<b>不是一块地</b>：它是采场 / 排土场<b>之内</b>再收的一层，
    /// 与母范围重叠是正常的（圈定窗口对它豁免了"空间唯一"的扣除运算）。
    /// 推演里必须整块排除 —— 否则同一片地会有两条推进轮廓，量算两遍。
    /// </summary>
    public bool IsWorkingSlope => Category is "pit_working_slope" or "dump_working_slope";

    /// <summary>
    /// 采场类（含未分类的「可采区域」）。<b>两个工作帮不算</b>：
    /// 原先写作 <c>!IsDump</c> 的黑名单，会把 <c>pit_working_slope</c> 当成又一块采场，
    /// 而 <c>dump_working_slope</c> 因为字面不是那两个 dump 值，竟被当成<b>采场</b>（推进极性反了）。
    /// </summary>
    public bool IsPit => !IsDump && !IsWorkingSlope;

    public SimPoint Centroid
    {
        get
        {
            if (Ring.Count == 0) return new SimPoint(0, 0);
            double x = Ring.Average(p => p.X), y = Ring.Average(p => p.Y);
            return new SimPoint(x, y);
        }
    }

    /// <summary>多边形面积 m²（绝对值）。</summary>
    public double AreaM2 => Math.Abs(SignedArea(Ring));

    public SimRegion Clone() => new()
    {
        Name = Name, Category = Category, Ring = new List<SimPoint>(Ring),
        Z = Z, ZSource = ZSource, SlopeAngleDeg = SlopeAngleDeg,
        Synthetic = Synthetic, SinkId = SinkId,
    };

    internal static double SignedArea(IReadOnlyList<SimPoint> r)
    {
        if (r.Count < 3) return 0;
        double a = 0;
        for (int i = 0; i < r.Count; i++)
        {
            var p = r[i]; var q = r[(i + 1) % r.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a / 2;
    }
}

/// <summary>区域装载结果（含来源文案与降级说明）。</summary>
public sealed class SimRegionSet
{
    public List<SimRegion> Regions { get; set; } = new();
    public string SourceLabel { get; set; } = "";
    /// <summary>高程解算的来源与降级说明（界面直接显示；一条都不吞）。</summary>
    public List<string> ElevationNotes { get; set; } = new();
    /// <summary>全部是示意图形（未接真实边界）。</summary>
    public bool AllSynthetic => Regions.Count > 0 && Regions.All(r => r.Synthetic);
    public bool IsEmpty => Regions.Count == 0;

    public IEnumerable<SimRegion> Pits => Regions.Where(r => r.IsPit);
    public IEnumerable<SimRegion> Dumps => Regions.Where(r => r.IsDump);

    /// <summary>全部轮廓的包围盒（minX,minY,maxX,maxY）；空集返回单位盒。</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var r in Regions)
            foreach (var p in r.Ring)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        if (minX > maxX) return (0, 0, 1, 1);
        return (minX, minY, maxX, maxY);
    }
}

public static class SimRegionLoader
{
    /// <summary>
    /// 装载作业区域轮廓。三层兜底，永不抛。
    /// </summary>
    /// <param name="sinks">用于给排土类区域配汇 Id，以及在无真实边界时按设计容量造示意图形。</param>
    public static SimRegionSet Load(SinkRegistry? sinks)
    {
        var set = new SimRegionSet();

        // ① GeoDataBase 台账（带真实 Z）
        //
        //  ★ 只取 active=1（本期选定纳入作业范围的）。历史遗留的老采场、下期才启用的排土场
        //    留在台账里但不该进本期推演 —— 见 V043。**排除了几块必须说出来**：
        //    "静默少一块"正是这一列要消灭的问题，不能换个理由再犯一次。
        int skippedInactive = 0, skippedThin = 0, skippedGate = 0;
        try
        {
            var svc = PitMine3D.Kylin.Data.EquipmentDataContext.MineableRegion;
            foreach (var r in svc.All())
            {
                if (r.Active == 0) { skippedInactive++; continue; }
                // 两个工作帮是范围闸门（采场/排土场的子区域），不是一块地：进来就是同一片地的第二条轮廓。
                if (IsGateCategory(r.Category)) { skippedGate++; continue; }
                var (ring, z) = ParseXyz(r.PointsJson);
                if (ring.Count < 3) { skippedThin++; continue; }
                set.Regions.Add(new SimRegion
                {
                    Name = string.IsNullOrWhiteSpace(r.Name) ? $"区域{set.Regions.Count + 1}" : r.Name,
                    Category = string.IsNullOrWhiteSpace(r.Category) ? "mineable" : r.Category,
                    Ring = ring, Z = z,
                    ZSource = double.IsNaN(z) ? "" : "可采区域台账 points_json 的真实 xyz（顶点 Z 均值）",
                });
            }
            if (set.Regions.Count > 0)
                set.SourceLabel = $"区域边界：可采区域台账（mineable_region，{set.Regions.Count} 块选定，带真实高程）"
                                + (skippedInactive > 0 ? $"；另有 {skippedInactive} 块**未选定**，本期不参与推演" : "")
                                + (skippedThin > 0 ? $"；{skippedThin} 块顶点不足 3 被跳过" : "")
                                + (skippedGate > 0 ? $"；{skippedGate} 块是**工作帮**（采场/排土场的子区域，范围闸门），不作独立轮廓进推演" : "");
            else if (skippedInactive > 0)
                set.SourceLabel = $"区域边界：台账里有 {skippedInactive} 块区域，但**一块都没选定**"
                                + "（在「作业区划分」里勾选「选定」列）—— 下面退到示意图形";
        }
        catch { set.Regions.Clear(); skippedInactive = skippedThin = 0; }

        // ② Platform capability（仅 XY）
        if (set.Regions.Count == 0)
        {
            try
            {
                var cap = SimHost.PitDesign;
                if (cap != null)
                {
                    foreach (var r in cap.GetMineableRegions())
                    {
                        if (IsGateCategory(r.Category)) continue;   // 同上：工作帮不作独立轮廓
                        var ring = ParseXy(r.RingXy);
                        if (ring.Count < 3) continue;
                        set.Regions.Add(new SimRegion
                        {
                            Name = string.IsNullOrWhiteSpace(r.Name) ? $"区域{set.Regions.Count + 1}" : r.Name,
                            Category = string.IsNullOrWhiteSpace(r.Category) ? "mineable" : r.Category,
                            Ring = ring, Z = double.NaN,
                        });
                    }
                    if (set.Regions.Count > 0)
                        set.SourceLabel = $"区域边界：三维宿主 IPitDesignCapability（{set.Regions.Count} 块，仅平面 XY，无高程）";
                }
            }
            catch { set.Regions.Clear(); }
        }

        // ③ 示意图形（尺寸由真实工程量反推）
        if (set.Regions.Count == 0)
        {
            string why = skippedInactive > 0
                ? $"台账里 {skippedInactive} 块区域**一块都没选定**（不是没画）"
                : "未圈画可采区域";
            set.Regions = Synthesize(sinks);
            set.SourceLabel = set.Regions.Count > 0
                ? $"区域边界：**示意图形**（{why}；采场尺寸按工作线长、排土场面积按设计容量÷台阶高反推）"
                : $"区域边界：无（{why}，也无去向台账可据以造示意图）";
        }
        else if (skippedInactive > 0)
        {
            set.ElevationNotes.Add($"作业范围：台账共 {set.Regions.Count + skippedInactive} 块区域，"
                + $"本期选定 {set.Regions.Count} 块进推演，**排除 {skippedInactive} 块未选定的**。"
                + "被排除的区域不出现在推进轮廓里，它们对应的源/汇量也就没有区域承接 —— "
                + "若某个作业面的量突然找不到落点，先回「作业区划分」核一遍选定状态。");
        }

        MatchSinks(set, sinks);
        ResolveElevations(set);
        return set;
    }

    /// <summary>
    /// 高程三层兜底（能拿真的绝不摆 0m）：
    /// <list type="number">
    /// <item>可采区域台账 <c>mineable_region.points_json</c> 的真 xyz —— 装载时已取到（<see cref="SimRegion.Z"/>）；</item>
    /// <item>拿不到就用**文档里的现状面三角网**采 Z：把区域轮廓的每个顶点做 XY 投影，
    ///       落在哪个三角形里就按重心坐标插值出 Z，取命中点的均值作为区域代表高程；</item>
    /// <item>两条都不行才落 0 m，并保留原有的警示。</item>
    /// </list>
    /// 第 ② 层只在第 ① 层缺失时才跑（现状面扫描要遍历文档实体，不白花这个钱）。
    /// </summary>
    private static void ResolveElevations(SimRegionSet set)
    {
        var need = set.Regions.Where(r => double.IsNaN(r.Z)).ToList();
        if (need.Count == 0)
        {
            if (set.Regions.Count > 0)
                set.ElevationNotes.Add($"层体高程：{set.Regions.Count} 块区域全部取自可采区域台账的真实 xyz。");
            return;
        }

        // 示意图形的 XY 是本包造的，拿它去现状面上采 Z 只会采到一个毫不相干的高程 —— 宁可不采。
        if (need.All(r => r.Synthetic))
        {
            set.ElevationNotes.Add("层体高程：轮廓是示意图形（坐标为本包生成），不去现状面采 Z（采了也是无关高程）→ 按 0 m 摆放。");
            return;
        }

        SimTerrainSampler? terrain = null;
        try { terrain = SimTerrainSampler.FromDocument(); }
        catch { terrain = null; }

        if (terrain == null || !terrain.Available)
        {
            set.ElevationNotes.Add($"层体高程：{need.Count} 块区域的轮廓只有平面 XY，"
                + $"且{terrain?.Reason ?? "文档中取不到现状面三角网"} → 只能按 0 m 基准摆放（形状与体积可信，绝对高程不可信）。");
            foreach (var r in need) r.ZSource = "无（轮廓仅 XY，且无现状面可采 Z）→ 0m 兜底";
            return;
        }

        int ok = 0;
        foreach (var r in need)
        {
            var (z, hit, total) = terrain.SampleRingZ(r.Ring);
            if (double.IsNaN(z)) { r.ZSource = $"现状面采样失败（{total} 个轮廓点全部落在现状面之外）→ 0m 兜底"; continue; }
            r.Z = z;
            r.ZSource = $"现状面三角网 XY 投影插值（{hit}/{total} 点命中，取均值 {z:0.##}m）";
            ok++;
        }

        if (ok > 0)
            set.ElevationNotes.Add($"层体高程：{ok}/{need.Count} 块无高程区域已从**现状面三角网**采到 Z（{terrain.Reason}）—— "
                                 + "这是插值出来的地表高程，不是台账实测值，绝对高程按现状面的精度计。");
        if (ok < need.Count)
            set.ElevationNotes.Add($"层体高程：仍有 {need.Count - ok} 块区域采不到 Z（轮廓落在现状面覆盖范围外）→ 按 0 m 摆放。");
    }

    /// <summary>把排土类区域配到去向登记簿的汇（先按名字，再按 RefEntityId）。</summary>
    private static void MatchSinks(SimRegionSet set, SinkRegistry? sinks)
    {
        if (sinks == null) return;
        foreach (var r in set.Regions.Where(x => x.IsDump || x.SinkId.Length == 0))
        {
            var hit = sinks.All.FirstOrDefault(s =>
                       string.Equals(s.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.RefEntityId, r.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Id, r.Name, StringComparison.OrdinalIgnoreCase)
                    || (r.Name.Length > 1 && s.Name.Contains(r.Name, StringComparison.OrdinalIgnoreCase))
                    || (s.Name.Length > 1 && r.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase)));
            if (hit != null && hit.IsDumping)
            {
                r.SinkId = hit.Id;
                if (!r.IsDump) r.Category = hit.Kind == SinkKind.InternalDump ? "internal_dump" : "external_dump";
                // 排土侧的坡面角就长在汇台账上（dump_site.bench_slope_angle_deg），直接带过来给层体放坡用
                if (hit.BenchSlopeAngleDeg > 1e-6 && hit.BenchSlopeAngleDeg <= 90) r.SlopeAngleDeg = hit.BenchSlopeAngleDeg;
            }
        }
    }

    /// <summary>示意图形：采场一块 + 每个排弃类去向一块，尺寸由真实工程量反推。</summary>
    private static List<SimRegion> Synthesize(SinkRegistry? sinks)
    {
        var list = new List<SimRegion>();
        var prm = SimMiningParams.Load();
        double pitW = Math.Max(200, prm.WorkLineLengthM);          // 工作线长 → 采场宽
        double pitH = Math.Max(150, prm.WorkLineLengthM * 0.6);

        list.Add(new SimRegion
        {
            Name = "采场（示意）", Category = "pit", Synthetic = true,
            Ring = Rect(0, 0, pitW, pitH, chamfer: 0.12),
        });

        var dumping = (sinks?.All ?? Array.Empty<SinkNode>()).Where(s => s.IsDumping).ToList();
        double gap = pitW * 0.35, cursorY = 0;
        foreach (var s in dumping)
        {
            // 面积 = 设计容量(占容方) / 台阶高；无容量则按工作线长给个方形
            double area = s.IsCapacityLimited && s.BenchHeightM > 1e-6
                ? s.DesignCapacityM3 / s.BenchHeightM
                : Math.Max(1, s.WorkLineLengthM) * Math.Max(1, s.WorkLineLengthM) * 0.5;
            double side = Math.Sqrt(Math.Max(1e4, area));
            side = Math.Min(side, pitW * 2.5);   // 限幅，免得一个巨型排土场把采场挤成一条线

            double x0 = pitW + gap, y0 = cursorY;
            list.Add(new SimRegion
            {
                Name = s.Name, Category = s.Kind == SinkKind.InternalDump ? "internal_dump" : "external_dump",
                Synthetic = true, SinkId = s.Id,
                SlopeAngleDeg = s.BenchSlopeAngleDeg > 1e-6 && s.BenchSlopeAngleDeg <= 90 ? s.BenchSlopeAngleDeg : double.NaN,
                Ring = Rect(x0, y0, side, side * 0.7, chamfer: 0.18),
            });
            cursorY += side * 0.7 + pitH * 0.15;
        }
        return list;
    }

    private static List<SimPoint> Rect(double x, double y, double w, double h, double chamfer)
    {
        double c = Math.Max(0, Math.Min(0.45, chamfer)) * Math.Min(w, h);
        return new List<SimPoint>
        {
            new(x + c, y), new(x + w - c, y),
            new(x + w, y + c), new(x + w, y + h - c),
            new(x + w - c, y + h), new(x + c, y + h),
            new(x, y + h - c), new(x, y + c),
        };
    }

    /// <summary>这个 category 是"范围闸门"（两个工作帮）而不是一块地 —— 见 <see cref="SimRegion.IsWorkingSlope"/>。</summary>
    private static bool IsGateCategory(string? category)
        => category is "pit_working_slope" or "dump_working_slope";

    private static (List<SimPoint> Ring, double Z) ParseXyz(string? json)
    {
        var ring = new List<SimPoint>();
        double zs = 0; int zn = 0;
        try
        {
            var flat = JsonSerializer.Deserialize<double[]>(json ?? "[]");
            if (flat == null || flat.Length < 9) return (ring, double.NaN);
            int n = flat.Length / 3;
            for (int i = 0; i < n; i++)
            {
                ring.Add(new SimPoint(flat[3 * i], flat[3 * i + 1]));
                zs += flat[3 * i + 2]; zn++;
            }
        }
        catch { return (new List<SimPoint>(), double.NaN); }
        return (ring, zn > 0 ? zs / zn : double.NaN);
    }

    private static List<SimPoint> ParseXy(double[]? flat)
    {
        var ring = new List<SimPoint>();
        if (flat == null || flat.Length < 6) return ring;
        for (int i = 0; i + 1 < flat.Length; i += 2) ring.Add(new SimPoint(flat[i], flat[i + 1]));
        return ring;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  轮廓偏移
// ─────────────────────────────────────────────────────────────────────────────

public static class RingOffset
{
    /// <summary>顶点尖角处偏移放大的限幅（1/cos(θ/2) 会在锐角处爆炸，必须钳）。</summary>
    private const double MaxMiter = 2.5;

    /// <summary>
    /// 偏移一条闭合轮廓。
    /// </summary>
    /// <param name="dist">偏移距离 m（≥0）。</param>
    /// <param name="outward">true=外扩（排土），false=内缩（采场）。</param>
    /// <param name="mode">等距 / 定向。</param>
    /// <param name="azimuthDeg">推进方位角（0=+X，90=+Y，逆时针正）；仅定向模式用。</param>
    public static List<SimPoint> Offset(IReadOnlyList<SimPoint> ring, double dist, bool outward,
                                        SimAdvanceMode mode, double azimuthDeg)
    {
        if (ring.Count < 3 || double.IsNaN(dist) || double.IsInfinity(dist) || dist <= 1e-9)
            return ring.ToList();

        var pts = Ccw(ring);
        int n = pts.Count;
        var normals = new SimPoint[n];       // 每条边的外法向（CCW 环：(dy,-dx)）
        for (int i = 0; i < n; i++)
        {
            var d = pts[(i + 1) % n] - pts[i];
            double len = d.Length;
            normals[i] = len <= 1e-9 ? new SimPoint(0, 0) : new SimPoint(d.Y / len, -d.X / len);
        }

        double sign = outward ? 1 : -1;
        var result = new List<SimPoint>(n);

        if (mode == SimAdvanceMode.Directional)
        {
            double rad = azimuthDeg * Math.PI / 180.0;
            var dir = new SimPoint(Math.Cos(rad), Math.Sin(rad));
            for (int i = 0; i < n; i++)
            {
                // 顶点法向 = 相邻两边外法向之和（不必归一，只判朝向）
                var np = normals[(i - 1 + n) % n];
                var nn = normals[i];
                var vn = np + nn;
                double dot = vn.X * dir.X + vn.Y * dir.Y;
                // 朝向推进方位那一侧的顶点沿方位平移；背面不动 → 轮廓整体沿方位「推进」
                result.Add(dot > 0 ? pts[i] + dir * dist : pts[i]);
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                var np = normals[(i - 1 + n) % n];
                var nn = normals[i];
                var b = np + nn;
                double bl = b.Length;
                if (bl <= 1e-9) { result.Add(pts[i]); continue; }
                var bn = new SimPoint(b.X / bl, b.Y / bl);
                // 斜接放大 1/cos(θ/2) = 1/(bn·nn)，锐角处钳到 MaxMiter，避免尖刺
                double c = bn.X * nn.X + bn.Y * nn.Y;
                double miter = Math.Min(MaxMiter, c > 1e-6 ? 1.0 / c : MaxMiter);
                result.Add(pts[i] + bn * (sign * dist * miter));
            }
        }

        // 内缩过度会自交/翻面：面积翻号或缩到 <2% 就认为「采空/收口」，退化为一个收口小环。
        double a0 = Math.Abs(SimRegion.SignedArea(pts));
        double a1 = SimRegion.SignedArea(result);
        if (!outward && (a1 <= 0 || (a0 > 1e-6 && Math.Abs(a1) / a0 < 0.02)))
            return Collapse(pts);

        return result;
    }

    /// <summary>收口：退化成质心附近一个很小的环（表示该区已基本采完）。</summary>
    private static List<SimPoint> Collapse(IReadOnlyList<SimPoint> pts)
    {
        double cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        double r = Math.Max(1.0, Math.Sqrt(Math.Abs(SimRegion.SignedArea(pts))) * 0.03);
        var ring = new List<SimPoint>();
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            ring.Add(new SimPoint(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return ring;
    }

    /// <summary>统一成逆时针（外法向公式才成立）。</summary>
    private static List<SimPoint> Ccw(IReadOnlyList<SimPoint> ring)
    {
        var pts = ring.ToList();
        if (SimRegion.SignedArea(pts) < 0) pts.Reverse();
        return pts;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  一帧的平面场景（2D 画布与三维 overlay 共用同一份几何）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一块区域在某一帧的推演轮廓。</summary>
public sealed class SimPlanShape
{
    public SimRegion Region { get; set; } = new();
    /// <summary>期初轮廓（上一帧末）。</summary>
    public List<SimPoint> Before { get; set; } = new();
    /// <summary>期末轮廓（本帧推进后）。</summary>
    public List<SimPoint> After { get; set; } = new();
    /// <summary>本期推进距离 m（NaN=参数缺失，轮廓不动）。</summary>
    public double AdvanceM { get; set; } = double.NaN;
    /// <summary>累计推进距离 m。</summary>
    public double CumAdvanceM { get; set; }
    /// <summary>本期该区的工程量文案。</summary>
    public string VolumeCaption { get; set; } = "";

    // ── 三维层体用的反算三件套（<see cref="SimSolidBuilder"/> 的体积自检靠它们）──
    /// <summary>
    /// 本期该区的台账体积 m³：采场 = 挖除**实方**；排土 = 堆填**占容方**。0 = 未解出。
    /// 与 <see cref="AdvanceM"/> 同一次匹配算出，供层体几何体积做独立比对。
    /// </summary>
    public double TargetVolumeM3 { get; set; }
    /// <summary>本期该区的台阶高 m（采场 H / 排土 h排）。0 = 未解出。</summary>
    public double BenchHeightM { get; set; }
    /// <summary>本期该区的工作线长 m（体积偏差归因用）。0 = 未解出。</summary>
    public double WorkLineLengthM { get; set; }
    /// <summary>快满/排穿标记（排土类）。</summary>
    public bool Alert { get; set; }
    public bool IsDump => Region.IsDump;

    // ── 台阶放坡（真台阶几何用；NaN/0 = 未解析 → 层体退回垂直壁，不猜）──
    /// <summary>
    /// 台阶坡面角 α(°)。采场取 <see cref="SimMiningParams.FaceAngleDeg"/>（需 SlopeResolved），
    /// 排土取 <see cref="SimRegion.SlopeAngleDeg"/>（来自 SinkNode.BenchSlopeAngleDeg）。NaN = 未解析。
    /// </summary>
    public double SlopeAngleDeg { get; set; } = double.NaN;
    /// <summary>平盘（安全平台）宽 W(m)。NaN = 未解析。</summary>
    public double BermWidthM { get; set; } = double.NaN;
    /// <summary>放坡参数的来源/降级说明（一条都不吞，界面直接显示）。</summary>
    public string SlopeSourceLabel { get; set; } = "";
    /// <summary>放坡参数成立（α∈(0,90]、W≥0），层体可按真台阶建。</summary>
    public bool SlopeUsable => SlopeAngleDeg > 1e-6 && SlopeAngleDeg <= 90 + 1e-9
                            && !double.IsNaN(SlopeAngleDeg) && !double.IsNaN(BermWidthM) && BermWidthM >= 0;
}

/// <summary>一帧的完整平面场景。</summary>
public sealed class SimPlanScene
{
    public int FrameIndex { get; set; }
    public List<SimPlanShape> Shapes { get; set; } = new();
    public string ModeCaption { get; set; } = "";
    public List<string> Notes { get; set; } = new();

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Acc(IEnumerable<SimPoint> r)
        {
            foreach (var p in r)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }
        foreach (var s in Shapes) { Acc(s.Region.Ring); Acc(s.Before); Acc(s.After); }
        if (minX > maxX) return (0, 0, 1, 1);
        return (minX, minY, maxX, maxY);
    }

    // ── 场景装配 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 按时间轴的前 <paramref name="frameIndex"/> 期累计推进量，算出各区域的期初/期末轮廓。
    /// <para>
    /// 区域 ↔ 源/汇 的配对：
    ///  · 排土类区域按 <see cref="SimRegion.SinkId"/> 直接对上汇；对不上且只有一块排土区域时，
    ///    吃下全部排弃占容（诚实的兜底：总量不丢）。
    ///  · 采场类区域按名字包含对上源；对不上且只有一块采场区域时，吃下全部采场挖除量。
    /// </para>
    /// </summary>
    public static SimPlanScene Build(SimRegionSet set, SimTimeline tl, int frameIndex,
                                     SimAdvanceMode mode, double azimuthDeg)
    {
        var scene = new SimPlanScene { FrameIndex = frameIndex };
        scene.ModeCaption = mode == SimAdvanceMode.Uniform
            ? "轮廓推进：全周等距（采场内缩 / 排土外扩）"
            : $"轮廓推进：定向平移，方位 {azimuthDeg:0}°（平行推进）";

        if (set.IsEmpty || tl.Count == 0) return scene;

        var pits = set.Pits.ToList();
        var dumps = set.Dumps.ToList();

        foreach (var r in set.Regions)
        {
            var shape = new SimPlanShape { Region = r };
            double cumBefore = 0, cumAfter = 0;
            double thisAdv = double.NaN;
            string cap = "";
            bool alert = false;

            for (int i = 0; i <= frameIndex && i < tl.Count; i++)
            {
                var f = tl.Frames[i];
                var g = r.IsDump ? DumpStep(f, r, dumps.Count) : PitStep(f, r, pits.Count);
                double adv = double.IsNaN(g.AdvanceM) ? 0 : g.AdvanceM;
                if (i < frameIndex) cumBefore += adv;
                cumAfter += adv;
                if (i == frameIndex)
                {
                    thisAdv = g.AdvanceM;
                    shape.TargetVolumeM3 = g.VolumeM3;
                    shape.BenchHeightM = g.BenchHeightM;
                    shape.WorkLineLengthM = g.WorkLineLengthM;
                    (cap, alert) = Caption(f, r, dumps.Count, pits.Count);
                }
            }

            shape.AdvanceM = thisAdv;
            shape.CumAdvanceM = cumAfter;
            shape.VolumeCaption = cap;
            shape.Alert = alert;
            shape.Before = RingOffset.Offset(r.Ring, cumBefore, outward: r.IsDump, mode, azimuthDeg);
            shape.After = RingOffset.Offset(r.Ring, cumAfter, outward: r.IsDump, mode, azimuthDeg);
            ResolveSlope(shape, tl.MiningParams);
            scene.Shapes.Add(shape);
        }

        if (!tl.MiningParams.Resolved)
            scene.Notes.Add("采场推进距离未解出（缺台阶高/工作线长），采场轮廓不动 —— 不拿缺省值伪造推进。" +
                            "在上方填 H/L 后点「应用参数」即可开始采场推演。");
        if (!tl.MiningParams.SlopeResolved && scene.Shapes.Any(s => !s.IsDump))
            scene.Notes.Add(tl.MiningParams.SlopeSourceLabel);
        if (set.AllSynthetic)
            scene.Notes.Add("当前轮廓是**示意图形**（未圈画可采区域）：形状不代表真实边界，推进距离与工程量是真的。");

        // 尺度提示：一个班的排土推进往往只有几十厘米，落在千米级的场子上肉眼看不出来。
        // 与其让人以为「没动」，不如直说：推进量以数字为准，要看得见变化请切到月/年。
        double maxAdv = scene.Shapes.Where(s => !double.IsNaN(s.CumAdvanceM)).Select(s => s.CumAdvanceM).DefaultIfEmpty(0).Max();
        double scale = Math.Sqrt(Math.Max(1, set.Regions.Select(r => r.AreaM2).DefaultIfEmpty(0).Max()));
        if (maxAdv > 0 && scale > 1 && maxAdv / scale < 0.004 && tl.Granularity == SimGranularity.Shift)
            scene.Notes.Add($"本粒度下累计推进仅 {maxAdv:0.##} m，相对区域尺度（约 {scale:0} m）过小，轮廓变化肉眼不可见——" +
                            "推进量以右侧数字为准；切到「月 / 年」粒度可看到明显的逐期推进。");

        return scene;
    }

    /// <summary>
    /// 给一块区域定放坡参数（坡面角 α / 平盘宽 W）—— **采排两侧各有各的来源，不许串用**。
    /// <list type="bullet">
    /// <item><b>排土侧</b>：α 取汇台账 <c>dump_site.bench_slope_angle_deg</c>（<see cref="SinkNode.BenchSlopeAngleDeg"/>），
    ///       这是排土场自己的安息角量级参数；台账没有平盘宽字段，只能沿用界面填的 W 并如实标注。</item>
    /// <item><b>采场侧</b>：α/W 取 <see cref="SimMiningParams"/>（软读计划 → 界面手填 → 未解析）。
    ///       未解析时**不给值**，层体退回垂直壁。</item>
    /// </list>
    /// </summary>
    private static void ResolveSlope(SimPlanShape shape, SimMiningParams prm)
    {
        if (shape.IsDump)
        {
            double a = shape.Region.SlopeAngleDeg;
            if (!(a > 1e-6) || a > 90 || double.IsNaN(a))
            {
                shape.SlopeSourceLabel = "排土坡面角未解出（该区未配到去向台账或台账未录 bench_slope_angle_deg）→ 堆填层按**垂直壁**建。";
                return;
            }
            shape.SlopeAngleDeg = a;
            // 平盘宽：去向台账没有这一列。有界面值就沿用并标注来源，没有就按 0（纯坡面、无安全平台），不编。
            if (prm.SlopeUsable)
            {
                shape.BermWidthM = prm.BermWidthM;
                shape.SlopeSourceLabel = $"排土 α={a:0.#}° 取自去向台账；平盘宽 W={prm.BermWidthM:0.##}m **沿用采场侧的界面值**"
                                       + "（去向台账无平盘宽字段），只影响台阶间水平错距，不影响本期体积。";
            }
            else
            {
                shape.BermWidthM = 0;
                shape.SlopeSourceLabel = $"排土 α={a:0.#}° 取自去向台账；平盘宽 W 无来源 → 按 0 处理"
                                       + "（台阶紧贴叠放，不是工程实态，只是不编一个宽度出来）。";
            }
            return;
        }

        if (!prm.SlopeUsable) { shape.SlopeSourceLabel = prm.SlopeSourceLabel; return; }
        shape.SlopeAngleDeg = prm.FaceAngleDeg;
        shape.BermWidthM = prm.BermWidthM;
        shape.SlopeSourceLabel = prm.SlopeSourceLabel;
    }

    /// <summary>
    /// 一块区域在一期里的反算结果：推进距离 + **台账体积** + 台阶高 + 工作线长。
    /// 体积口径随极性走：采场 = 挖除实方，排土 = 堆填占容方，两者不可混用。
    /// <para>
    /// 之所以把体积一起带出来：三维层体的几何体积（环带面积 × 台阶高）要跟它对比才叫校核。
    /// 若只留推进距离，层体体积会退化成 v×L×H 的恒等式，校核就成了自己证明自己。
    /// </para>
    /// </summary>
    private readonly record struct SimStepGeom(double AdvanceM, double VolumeM3, double BenchHeightM, double WorkLineLengthM);

    private static readonly SimStepGeom NoStep = new(double.NaN, 0, 0, 0);

    private static SimStepGeom PitStep(SimFrame f, SimRegion r, int pitCount)
    {
        var matched = f.Sources.Where(s => NameHit(r.Name, s.SourceName) || NameHit(r.Name, s.SourceId)).ToList();
        if (matched.Count == 0)
        {
            if (pitCount != 1) return NoStep;          // 多块采场又对不上 → 不猜
            double a = f.MineAdvanceM;
            // 全矿只有一块采场：把全部源的推进按体积合并（而不是取平均），才对得上总挖除量
            var ok = f.Sources.Where(s => s.AdvanceResolved).ToList();
            if (ok.Count == 0) return NoStep;
            double totalV = ok.Sum(s => s.InSituM3);
            double h0 = ok[0].BenchHeightM, l0 = ok[0].WorkLineLengthM;
            double lh = l0 * h0;
            return new SimStepGeom(lh > 1e-6 ? totalV / lh : a, totalV, h0, l0);
        }
        double vol = matched.Where(s => s.AdvanceResolved).Sum(s => s.InSituM3);
        double h = matched[0].BenchHeightM, l = matched[0].WorkLineLengthM;
        if (!matched.Any(s => s.AdvanceResolved))
            return new SimStepGeom(double.NaN, matched.Sum(s => s.InSituM3), h, l);
        double denom = l * h;
        return new SimStepGeom(denom > 1e-6 ? vol / denom : double.NaN, vol, h, l);
    }

    private static SimStepGeom DumpStep(SimFrame f, SimRegion r, int dumpCount)
    {
        var byId = f.Sinks.FirstOrDefault(s => r.SinkId.Length > 0
            && string.Equals(s.SinkId, r.SinkId, StringComparison.OrdinalIgnoreCase));
        var s2 = byId ?? f.Sinks.FirstOrDefault(s => s.IsDumping && NameHit(r.Name, s.SinkName));
        if (s2 != null)
            return new SimStepGeom(s2.AdvanceResolved ? s2.AdvanceM : double.NaN,
                                   s2.AcceptedDumpM3, s2.BenchHeightM, s2.WorkLineLengthM);

        if (dumpCount != 1) return NoStep;
        var all = f.Sinks.Where(x => x.IsDumping && x.AdvanceResolved).ToList();
        if (all.Count == 0) return NoStep;
        // 唯一一块排土区吃下全部排弃：体积取合计，台阶高/工作线长按占容加权（多汇混排时的诚实折中）
        double v = all.Sum(x => x.AcceptedDumpM3);
        double bh = v > 1e-6 ? all.Sum(x => x.BenchHeightM * x.AcceptedDumpM3) / v : all[0].BenchHeightM;
        double wl = v > 1e-6 ? all.Sum(x => x.WorkLineLengthM * x.AcceptedDumpM3) / v : all[0].WorkLineLengthM;
        return new SimStepGeom(all.Sum(x => x.AdvanceM), v, bh, wl);
    }

    private static (string Caption, bool Alert) Caption(SimFrame f, SimRegion r, int dumpCount, int pitCount)
    {
        if (r.IsDump)
        {
            var s = f.Sinks.FirstOrDefault(x => r.SinkId.Length > 0 && string.Equals(x.SinkId, r.SinkId, StringComparison.OrdinalIgnoreCase))
                 ?? f.Sinks.FirstOrDefault(x => x.IsDumping && NameHit(r.Name, x.SinkName));
            if (s == null && dumpCount == 1)
            {
                double d = f.Sinks.Where(x => x.IsDumping).Sum(x => x.AcceptedDumpM3);
                return ($"堆填 {d / 1e4:0.##}万m³占容", false);
            }
            if (s == null) return ("本期无投放", false);
            return ($"堆填 {s.AcceptedDumpM3 / 1e4:0.##}万m³占容 · {s.CapacityText}", s.Overflow || s.NearFull);
        }

        var srcs = f.Sources.Where(x => NameHit(r.Name, x.SourceName) || NameHit(r.Name, x.SourceId)).ToList();
        if (srcs.Count == 0 && pitCount == 1) srcs = f.Sources;
        if (srcs.Count == 0) return ("本期无采掘", false);
        double v = srcs.Sum(x => x.InSituM3);
        double ore = srcs.Sum(x => x.OreInSituM3);
        return ($"挖除 {v / 1e4:0.##}万m³实方（矿 {ore / 1e4:0.##} / 剥 {(v - ore) / 1e4:0.##}）", false);
    }

    /// <summary>
    /// 区域名 ↔ 源/汇名的配对判据（相等 / 互相包含）。
    /// <para><b>internal 而不是 private</b>：「作业区划分」窗口要当场告诉人"这块区域能不能对上盘子里的源/汇"，
    /// 那个判断必须与推演真正用的**是同一个函数**。照着抄一份镜像，判据一改两边就悄悄分家，
    /// 界面报绿而推演里那块区域一动不动。</para>
    /// </summary>
    internal static bool NameHit(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || (a.Length > 1 && b.Contains(a, StringComparison.OrdinalIgnoreCase))
            || (b.Length > 1 && a.Contains(b, StringComparison.OrdinalIgnoreCase));
    }
}
