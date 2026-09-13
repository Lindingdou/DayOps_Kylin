// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimProcessRegions.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;          // ProcessZone
using PitMine3D.Kylin.TaskLib.Domain;                        // ProcessType
using PitMine3D.Kylin.TaskLib.Zoning;                        // ProcessZoneStore

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  工序作业区 → 推演轮廓。**让每道工序演在自己的那块地上**。
//
//  ══ 在这之前是怎么演的 ══
//  推演按**面名**配轮廓（`SimRegionLoader` 读 mineable_region，`MatchRegion` 按名字找）。
//  于是同一个作业面的五道工序共用同一条轮廓：
//  穿孔那一格演在电铲脚下、爆破警戒那一格演在采装带上、排土卸载演在整幅排土幅上。
//  画面每一帧都规整，量与班次也都是真的 —— 只有**位置**是错的，而位置恰恰是演示要说明的那件事。
//
//  ══ 这一层做什么 ══
//  按「期次 + 工序 + 面名」到 process_zone 里取那块地的真实环（带真 xyz），
//  换掉这一格的轮廓。取不到就**原样退回**面级轮廓（旧行为），并说清楚退了。
//
//  ══ 三条口径 ══
//  **S1 只换几何，不换身份**：类别（决定推进极性）、汇绑定、边坡角一律**沿用面级那块区域**。
//      工序区自己不带内外排 —— 拿它去判极性，排土轮廓会朝反方向动，而推演不报错。
//  **S2 运输没有工序区**（P9），退回采装那块地：车是从铲下装、往卸载点走的，
//      演在采装面上比演在"没有"上有意义。
//  **S3 取不到要说出来**，不静默退：「这一格演的是工序区」与「演的是整个面」
//      画面上分不出来，而它们说的是两件事。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 一块工序作业区：几何 + 它自己的台账字段。
///
/// <para><b>为什么不只存 <see cref="SimRegion"/></b>：<see cref="SimRegion"/> 回答的是「这块地在哪」，
/// 而工序层还要回答「这块地是哪道工序的、量是哪个口径的、超前几天、警戒半径多少」。
/// 那几个数就在 <c>process_zone</c> 的行上，丢掉之后界面只能拿面积去猜 ——
/// 而三个方量口径（控制方量 / 原位实方 / 排弃占容）**一混列就再也分不开**（PS8）。</para>
/// </summary>
public sealed class SimProcessPatch
{
    /// <summary>工序码（<c>process_zone.process</c>）。</summary>
    public string Process { get; init; } = "";
    /// <summary>这块地自己的名字（分块时带 -2/-3 后缀）。</summary>
    public string Name { get; init; } = "";
    /// <summary>所属作业面 / 分组键（爆破警戒按炮次分组，这里是炮次键）。</summary>
    public string GroupKey { get; init; } = "";
    public SimRegion Region { get; init; } = new();

    public double AreaM2 { get; init; }
    /// <summary>本块的量（口径见 <see cref="Basis"/>）。null = 台账没给，**不是 0**。</summary>
    public double? VolumeM3 { get; init; }
    /// <summary>量的口径文本（原位实方 / 控制方量 / 排弃占容）。</summary>
    public string Basis { get; init; } = "";
    /// <summary>穿孔超前天数。</summary>
    public double? LeadDays { get; init; }
    /// <summary>爆破警戒半径 m。</summary>
    public double? GuardRadiusM { get; init; }
    /// <summary>带宽 m（排土两带）。</summary>
    public double? BandWidthM { get; init; }
    /// <summary>覆盖的采掘单元数。</summary>
    public long UnitCount { get; init; }
    /// <summary>该由谁去干（警戒区为空 —— 它派的是清场，人不是机）。</summary>
    public string EquipRole { get; init; } = "";

    /// <summary>中文工序名。</summary>
    public string ProcessName => SimProcessPalette.NameOfCode(Process);
    /// <summary>作业区还是禁入区（PS4）。</summary>
    public SimProcessDraw Draw => SimProcessPalette.DrawOf(Process);
}

/// <summary>一期工序作业区的推演轮廓索引。</summary>
public sealed class SimProcessRegionSet
{
    /// <summary>键 = 工序码 + 分隔符 + 面名（都小写）。分隔符取不可打印字符：面名里出现它的概率为 0，用 '-' 之类会和分块后缀（面名-2）撞上。</summary>
    private readonly Dictionary<string, SimRegion> _map = new(StringComparer.Ordinal);

    private readonly List<SimProcessPatch> _patches = new();

    public string Period { get; init; } = "";
    public string SourceLabel { get; set; } = "";
    public int Count => _map.Count;
    public bool IsEmpty => _map.Count == 0;

    /// <summary>
    /// 本期全部工序作业区（画图与读数用），已按现场先后 + 面积降序排好。
    ///
    /// <para><b>与 <see cref="Find"/> 是两条路，不要合并</b>：<c>Find</c> 回答「这道工序在这个面上演在哪」，
    /// 只收进得了推演的那几类（爆破退回采装那块地、运输退回采装）；
    /// 本表是**台账原样**，含 <c>blast_guard</c> 与 <c>dump_doze</c> 这两类
    /// <c>Find</c> 永远不会返回的地 —— 而它们恰恰是「五道工序各在哪」这张图上最该看见的两块。</para>
    /// </summary>
    public IReadOnlyList<SimProcessPatch> Patches => _patches;

    internal void Add(string process, string name, SimRegion r) => _map[Key(process, name)] = r;

    internal void AddPatch(SimProcessPatch p) => _patches.Add(p);

    internal void SortPatches() => _patches.Sort((a, b) =>
    {
        int c = SimProcessPalette.OrderOfCode(a.Process).CompareTo(SimProcessPalette.OrderOfCode(b.Process));
        return c != 0 ? c : b.AreaM2.CompareTo(a.AreaM2);
    });

    private static string Key(string process, string name)
        => (process ?? "").Trim().ToLowerInvariant() + '\u0001' + (name ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// 找这道工序在这个面上的那块地。找不到返回 null（调用方退回面级轮廓）。
    /// </summary>
    /// <param name="faceName">作业面名 / 区域名。</param>
    public SimRegion? Find(ProcessType process, string faceName)
    {
        string code = CodeOf(process);
        if (code.Length == 0) return null;

        if (_map.TryGetValue(Key(code, faceName), out var hit)) return hit;

        // 分块时名字带 -2 / -3 后缀（Z6）；主块对不上就取本组最大的那一块。
        // 不做「互相包含」那种模糊匹配 —— 那会让「采场1」配上「采场10」。
        string prefix = Key(code, faceName) + "-";
        var alt = _map.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                      .OrderByDescending(kv => kv.Value.AreaM2)
                      .Select(kv => kv.Value)
                      .FirstOrDefault();
        return alt;
    }

    /// <summary>
    /// 工序 → 工序区码。
    /// <para><b>运输退回采装</b>（S2）：运输没有面状区域（P9），
    /// 而车是从铲下装的 —— 演在采装那块地上比演在「没有」上有意义。</para>
    /// <para><b>爆破退回采装</b>（S4）：<b>爆破干的就是那块将要采剥的地</b>，
    /// 它是采剥的<b>前序工序</b>，不是另一块地。
    /// 原先这里指向 <see cref="ProcessZone.ProcBlastGuard"/> —— 那是**爆破警戒范围**，
    /// 是安全清场的边界（<c>ProcessZone</c> 自己的标签就写着「爆破警戒」，
    /// 且它<b>不派设备</b>：派的是清场，人不是机）。拿警戒范围当作业区有两个后果：
    /// 台账里有警戒区时，爆破被演在一个比作业面大一圈的范围上；
    /// 台账里没有时（绝大多数情况），它一路退到面级轮廓，位置更不对。
    /// 两种都不报错，画面看着都规整。</para>
    /// <para><b>排土取卸载带</b>：那是卡车去的地方；推排带是推土机的，
    /// 日作业计划的 Dump 环节说的是卸载。</para>
    /// </summary>
    public static string CodeOf(ProcessType p) => p switch
    {
        ProcessType.Drill => ProcessZone.ProcDrill,
        ProcessType.Blast => ProcessZone.ProcLoad,      // S4：爆破 = 采剥的前序，同一块地
        ProcessType.Load => ProcessZone.ProcLoad,
        ProcessType.Haul => ProcessZone.ProcLoad,
        ProcessType.Dump => ProcessZone.ProcDumpTip,
        _ => "",
    };
}

/// <summary>按期装载工序作业区的推演轮廓。永不抛。</summary>
public static class SimProcessRegions
{
    /// <summary>
    /// 装一期。
    /// </summary>
    /// <param name="period">期次 yyyy-MM。</param>
    /// <param name="faceLevel">面级区域集（mineable_region）—— <b>类别/汇/边坡角从这里沿用</b>（S1）。</param>
    public static SimProcessRegionSet Load(string period, SimRegionSet? faceLevel)
    {
        var set = new SimProcessRegionSet { Period = period ?? "" };
        if (string.IsNullOrWhiteSpace(period))
        {
            set.SourceLabel = "工序轮廓：没有期次，本次不按工序换轮廓。";
            return set;
        }

        List<ProcessZone> rows;
        try { rows = ProcessZoneStore.LoadPeriod(period); }
        catch (Exception ex)
        {
            set.SourceLabel = $"工序轮廓：工序作业区台账读不到（{ex.GetType().Name}）—— 全部退面级轮廓。";
            return set;
        }
        return LoadFrom(period, rows, faceLevel);
    }

    /// <summary>
    /// 算法本体（工序区已装好时用这一版）。
    /// <para><b>离线判据喂合成算例走它</b> —— 裸台架里台账路径静默走兜底，
    /// 拿 <see cref="Load"/> 判等于什么都没判。</para>
    /// </summary>
    public static SimProcessRegionSet LoadFrom(string period, IReadOnlyList<ProcessZone>? rows,
                                               SimRegionSet? faceLevel)
    {
        var set = new SimProcessRegionSet { Period = period ?? "" };
        rows ??= Array.Empty<ProcessZone>();

        var usable = rows.Where(z => z != null && z.Active != 0).ToList();
        if (usable.Count == 0)
        {
            set.SourceLabel = $"工序轮廓：{period} 没有已纳入本期的工序作业区 —— 全部退面级轮廓"
                            + "（每道工序都会演在整个作业面上，位置不区分工序）。"
                            + "到「作业区划分」生成工序作业区并入库即可。";
            return set;
        }

        int noZ = 0, inherited = 0;
        foreach (var z in usable)
        {
            var ring = ProcessZoneStore.ParseRing(z);
            if (ring.Count < 3) continue;

            // S1 只换几何：类别/汇/边坡角沿用面级那块区域。
            //    工序区自己不带内外排，拿它判极性 ⇒ 排土轮廓朝反方向动，而推演不报错。
            var host = faceLevel?.Regions.FirstOrDefault(
                r => string.Equals(r.Name, z.GroupKey, StringComparison.OrdinalIgnoreCase))
                ?? faceLevel?.Regions.FirstOrDefault(
                r => string.Equals(r.Name, z.Name, StringComparison.OrdinalIgnoreCase));
            if (host != null) inherited++;

            double zAvg = ring.Average(p => p.Z);
            if (double.IsNaN(zAvg) || zAvg == 0) noZ++;

            var region = new SimRegion
            {
                Name = z.Name,
                Category = host?.Category ?? CategoryOf(z),
                Ring = ring.Select(p => new SimPoint(p.X, p.Y)).ToList(),
                Z = zAvg,
                ZSource = $"工序作业区 {z.Process}／{z.ZSource}",
                SlopeAngleDeg = host?.SlopeAngleDeg ?? double.NaN,
                SinkId = host?.SinkId ?? "",
                Synthetic = false,
            };
            set.Add(z.Process, z.Name, region);

            // 画图与读数走这一份 —— 它收**台账原样**，含 Find 永远不返回的警戒区与推排带。
            set.AddPatch(new SimProcessPatch
            {
                Process = (z.Process ?? "").Trim(),
                Name = z.Name ?? "",
                GroupKey = z.GroupKey ?? "",
                Region = region,
                AreaM2 = z.AreaM2 > 0 ? z.AreaM2 : region.AreaM2,
                VolumeM3 = z.VolumeM3,
                Basis = z.Basis ?? "",
                LeadDays = z.LeadDays,
                GuardRadiusM = z.GuardRadiusM,
                BandWidthM = z.BandWidthM,
                UnitCount = z.UnitCount,
                EquipRole = z.EquipRoleName ?? "",
            });
        }
        set.SortPatches();

        set.SourceLabel = $"工序轮廓：{period} 装到 {set.Count} 块工序作业区"
                        + $"（{inherited} 块沿用了面级区域的类别与汇绑定）"
                        + " —— 每道工序演在自己那块地上；取不到工序区的格仍退面级轮廓。"
                        + (noZ > 0 ? $"　⚠ {noZ} 块的环高程为 0 或无效，那几块的层体会摆在 0m。" : "");
        return set;
    }

    /// <summary>面级区域配不上时的类别兜底（同 Z8：名字含内排 → 内排，含外排 → 外排，其余按采场）。</summary>
    private static string CategoryOf(ProcessZone z)
    {
        string probe = (z.Name ?? "") + " " + (z.GroupKey ?? "");
        if (z.Process is ProcessZone.ProcDumpTip or ProcessZone.ProcDumpDoze)
            return probe.Contains("内排", StringComparison.Ordinal) ? "internal_dump" : "external_dump";
        return "pit";
    }
}
