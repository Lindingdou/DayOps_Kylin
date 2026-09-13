// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/UnitFlowBridge.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Units;
namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 单元链的对位结果 → 月计划物料流（<see cref="PlanFlow"/>）的<b>转换器</b>。
///
/// <para><b>它不是分配算法</b> —— 一方的去向、运距、顺序全部由 <c>UnitPlanEngine</c> 解出来，
/// 这里只做三件事：<b>归并粒度</b>（单元 → 作业面·物料）、<b>换单位</b>（m³ → 万m³）、
/// <b>把去向映射到去向台账</b>。按 `docs/短期生产计划_按钮职责切分.md` §〇 定的主干，
/// 配对只有单元链一处产出，采排配对是它的视图。</para>
///
/// <para><b>映射失败必须明账</b>：排土位置码形如 <c>内排土场1-L3-1000200</c>，而去向台账里是
/// <c>dump_site</c> 的名字 —— 两边对不上时<b>落到「未分配」并点名说出来</b>，
/// 绝不静默丢掉（丢掉的后果是矩阵合计比排产少一截，而每一项校核仍然是 ✓）。</para>
/// </summary>
public static class UnitFlowBridge
{
    public sealed class Result
    {
        public readonly List<PlanFlow> Flows = new();
        /// <summary>引擎替用户做的每一个决定 + 每一处映射不上的去向。<b>一条都不许静默</b>。</summary>
        public readonly List<string> Notes = new();

        /// <summary>映射上台账的笔数 / 总笔数 —— 不报的话"接上了"和"一笔没接上"看起来一模一样。</summary>
        public int Mapped, Total;

        /// <summary>转换前后的实方合计（m³）—— 守恒判据用，差一点都要说。</summary>
        public double SourceInSituM3, FlowInSituM3;

        public string Caption => Total == 0
            ? "没有可转换的流"
            : $"对位 {Total} 笔 → 归并成 {Flows.Count} 条流；去向映射命中 {Mapped}/{Total}"
              + (Mapped < Total ? $"，其余 {Total - Mapped} 笔落「未分配」" : "");
    }

    /// <summary>
    /// 把一次单元排产的逐笔流归并成月计划流。
    /// </summary>
    /// <param name="r">单元链的排产结果（<c>UnitPlanStore.Last</c>）。</param>
    /// <param name="catalog">去向台账（<c>PlanDestinationCatalog.Current</c>）。</param>
    public static Result ToPlanFlows(UnitPlanResult? r, IReadOnlyList<PlanDestination>? catalog)
    {
        var res = new Result();
        if (r == null || !r.Success || r.Assignments.Count == 0)
        {
            res.Notes.Add("◆ 没有可用的单元排产结果 —— 先在「采掘单元清单」按目标排一次。");
            return res;
        }

        var dests = catalog ?? Array.Empty<PlanDestination>();
        // 去向按名字对：台账里是排土场/破碎站的名字，位置码的前缀正是同一个名字（DumpSlot.DumpName）。
        // 名字带空格/全半角差异很常见，所以归一化后再比；重名取第一个并留条。
        var byName = new Dictionary<string, PlanDestination>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dests)
        {
            string k = Norm(d.Name);
            if (k.Length == 0) continue;
            if (!byName.ContainsKey(k)) byName[k] = d;
            else res.Notes.Add($"· 去向台账里「{d.Name}」重名，映射取先出现的那个。");
        }

        // 键 = (作业面, 物料码, 去向Id 或 "")；值 = 累加的实方与运距加权
        var acc = new Dictionary<(string Face, string Mat, string Dest), Agg>();
        var unmapped = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in r.Assignments)
        {
            string face = FaceOf(a);
            foreach (var f in a.Flows)
            {
                res.Total++;
                res.SourceInSituM3 += f.InSituM3;

                string mat = ResolveMaterial(f, a);
                var hit = Lookup(byName, f.DestinationName, f.DestinationCode);
                if (hit != null) res.Mapped++;
                else if (f.InSituM3 > 1e-9)
                    unmapped[DestLabel(f)] = unmapped.GetValueOrDefault(DestLabel(f)) + f.InSituM3;

                var key = (face, mat, hit?.Id ?? "");
                if (!acc.TryGetValue(key, out var g)) acc[key] = g = new Agg { Dest = hit, RefId = a.UnitId };
                g.InSituM3 += f.InSituM3;
                g.KmVol += f.HaulKm * f.InSituM3;
            }
            // 排不下的量也要出现在矩阵里 —— 否则"排不下"在这张表上等于不存在
            if (a.UnplacedM3 > 1e-9)
            {
                var key = (face, ResolveMaterialByKind(a), "");
                if (!acc.TryGetValue(key, out var g)) acc[key] = g = new Agg { Dest = null, RefId = a.UnitId };
                g.InSituM3 += a.UnplacedM3;
                res.SourceInSituM3 += a.UnplacedM3;
            }
        }

        foreach (var kv in acc.OrderBy(k => k.Key.Face, StringComparer.Ordinal).ThenBy(k => k.Key.Mat, StringComparer.Ordinal))
        {
            var g = kv.Value;
            if (g.InSituM3 <= 1e-9) continue;
            var flow = new PlanFlow
            {
                SourceName = kv.Key.Face,
                SourceRefId = g.RefId,
                MaterialCode = kv.Key.Mat,
                InSituWanM3 = Math.Round(g.InSituM3 / 1e4, 4),
            };
            if (g.Dest != null)
            {
                flow.DestinationId = g.Dest.Id;
                flow.DestinationName = g.Dest.Name;
                flow.DestinationKind = g.Dest.Kind;
                // 运距用引擎算出来的（体积加权），台账那个 FallbackHaulKm 只在引擎没给时兜底 ——
                // 反过来用台账值会把"接了路网/按真质心算"的结果悄悄换成一个静态数
                double km = g.InSituM3 > 1e-9 ? g.KmVol / g.InSituM3 : 0;
                flow.HaulKm = Math.Round(km > 1e-9 ? km : g.Dest.FallbackHaulKm, 2);
                flow.EquivHaulKm = Math.Round(flow.HaulKm * g.Dest.Kind.EquivFactor(), 2);
            }
            res.Flows.Add(flow);
            res.FlowInSituM3 += g.InSituM3;
        }

        if (unmapped.Count > 0)
            res.Notes.Add($"◆ 有 {unmapped.Count} 个去向在去向台账里找不到同名项，这些量落到「未分配」列："
                        + string.Join("、", unmapped.OrderByDescending(k => k.Value).Take(5)
                                                    .Select(k => $"{k.Key} {k.Value / 1e4:0.0}万m³"))
                        + (unmapped.Count > 5 ? " …" : "")
                        + "。对法是**按名字**（排土位置码的前缀 = 排土场名），改台账名字或排土场名让两边一致。");

        // 守恒：转换前后实方必须逐位对得上（比相对差 —— 取整是显示不是账）
        double m = Math.Max(Math.Abs(res.SourceInSituM3), Math.Abs(res.FlowInSituM3));
        if (m > 1e-9 && Math.Abs(res.SourceInSituM3 - res.FlowInSituM3) / m > 1e-6)
            res.Notes.Add($"◆ 归并前后实方对不上：{res.SourceInSituM3:0.##} → {res.FlowInSituM3:0.##} m³ —— 这是转换器的错，不是数据的错。");

        return res;
    }

    private sealed class Agg
    {
        public PlanDestination? Dest;
        public string RefId = "";
        public double InSituM3, KmVol;
    }

    /// <summary>作业面 = <c>层-B带号</c>（UnitId 是 <c>层-B带号-P幅号</c>，去掉幅就是面）。</summary>
    private static string FaceOf(UnitAssignment a)
        => a.BandId > 0 ? $"{a.SeamCode}-B{a.BandId}" : (a.SeamCode.Length > 0 ? a.SeamCode : a.UnitId);

    private static string ResolveMaterial(UnitFlow f, UnitAssignment a)
    {
        if (f.IsCoalSink || a.Kind == UnitKind.Coal) return PlanMaterialCatalog.Coal;
        // 引擎侧的物料码（GapMaterial.Code）与本地目录同名就用它，否则按岩 —— 认不出来要说，不猜
        var spec = PlanMaterialCatalog.Resolve(f.MaterialCode);
        return spec.Code.Length > 0 ? spec.Code : PlanMaterialCatalog.Rock;
    }

    private static string ResolveMaterialByKind(UnitAssignment a)
        => a.Kind == UnitKind.Coal ? PlanMaterialCatalog.Coal : PlanMaterialCatalog.Rock;

    private static PlanDestination? Lookup(Dictionary<string, PlanDestination> byName, string? name, string? code)
    {
        string n = Norm(name);
        if (n.Length > 0 && byName.TryGetValue(n, out var d)) return d;
        // 位置码形如「排土场名-L3-1000200」：截到第一个 '-L' 之前再试一次
        string c = Norm(code);
        int i = c.IndexOf("-l", StringComparison.OrdinalIgnoreCase);
        if (i > 0 && byName.TryGetValue(c.Substring(0, i), out var d2)) return d2;
        return null;
    }

    private static string DestLabel(UnitFlow f)
        => f.DestinationName.Length > 0 ? f.DestinationName
         : f.DestinationCode.Length > 0 ? f.DestinationCode : "（空去向）";

    private static string Norm(string? s)
        => (s ?? "").Replace(" ", "").Replace("　", "").Trim();
}
