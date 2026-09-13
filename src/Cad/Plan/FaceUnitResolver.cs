// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/FaceUnitResolver.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Units;             // MineUnit / UnitKind
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  采掘单元 → 作业面 的归属
//
//  这一层存在的唯一理由：「确定开采程序」在【面】上钉设备型号与工艺，而排产引擎排的是【单元】。
//  没有这份归属，面上钉的一切都落不到任何一个单元上 —— 界面写得满满当当，排产一条都不认。
//
//  ⚠ 归属是【规则】，不是【猜】。两者的区别在于：
//     规则是写出来的、可核对的、命中率是报出来的、命中不了就明说命中不了；
//     猜是"总得给每个单元配一个面"，于是配错了也没人知道。
//     本类只做前者：FA1–FA7 逐条写明，匹配率与未匹配清单一起返回，
//     **宁可留 30% 单元不受约束，也不给它们随便安一个面**。
//
//  规则（FA 前缀 —— U/L 已被采掘单元台账占用，别再撞号）：
//    FA1  手工覆盖优先于一切规则（人指定了就是人说了算）
//    FA2  物料相容：煤面只配煤单元，岩/表土面只配岩单元
//    FA3  标高命中：面的台阶标高落在单元 [ZLo−tol, ZHi+tol] 之内
//    FA4  多个面命中 ⇒ 取台阶标高最贴近单元【顶板】的那个（台阶标高按平盘标高理解）
//    FA5  FA4 之后仍并列 ⇒ 判【歧义】，不配，单列报出
//    FA6  一个面都没命中 ⇒ 不配，单列报出
//    FA7  免爆单元 = 归属面的物料 NeedsBlasting==false 的那些单元（表土/风化层直接铲装）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次归属解算的结果 —— 对应关系 + 「多少配上了、哪些没配上、为什么」。</summary>
public sealed class FaceUnitResolution
{
    /// <summary>单元号 → 作业面名。<b>只含真配上的</b>，配不上的不在这里（也不给兜底值）。</summary>
    public Dictionary<string, string> UnitToFace { get; } = new(StringComparer.Ordinal);

    /// <summary>FA7：归属面的物料不需要爆破的那些单元 —— 直接喂 <c>EquipmentAssignInput.NoBlastUnitIds</c>。</summary>
    public HashSet<string> NoBlastUnits { get; } = new(StringComparer.Ordinal);

    /// <summary>FA6：一个面都没命中的单元（附原因，给人读的）。</summary>
    public List<string> Unmatched { get; } = new();

    /// <summary>FA5：多个面并列、判不出归谁的单元（附候选面名，给人读的）。</summary>
    public List<string> Ambiguous { get; } = new();

    /// <summary>
    /// 上面两张表对应的<b>单元号</b>（给程序读的），与 <see cref="Unmatched"/>/<see cref="Ambiguous"/> 同序等长。
    ///
    /// <para><b>为什么单开这两张表</b>：归属覆盖窗口要按单元号列待定项。
    /// 从那两句给人读的话里正则抠单元号也能跑，但文案一改就静默抠空 ——
    /// 窗口里一行不显示，看起来就像"这一轮全配上了"。<b>要什么就单独给什么，别从展示文本反解。</b></para>
    /// </summary>
    public List<string> UnmatchedIds { get; } = new();
    public List<string> AmbiguousIds { get; } = new();

    /// <summary>全部没配上的单元号（歧义 + 没命中）—— 归属覆盖窗口的输入。</summary>
    public IEnumerable<string> PendingIds => AmbiguousIds.Concat(UnmatchedIds);

    /// <summary>参与解算的单元数 / 真配上的单元数。</summary>
    public int UnitCount, MatchedCount;

    /// <summary>手工覆盖生效了几条（FA1）。</summary>
    public int ManualCount;

    /// <summary>匹配率 0~1。<b>低不是错误，是事实</b> —— 但必须显示出来。</summary>
    public double MatchRate => UnitCount > 0 ? (double)MatchedCount / UnitCount : 0;

    /// <summary>逐面配到几个单元 —— 面上钉了型号却一个单元都没配上，是个必须看见的状态。</summary>
    public Dictionary<string, int> PerFace { get; } = new(StringComparer.Ordinal);

    public List<string> Notes { get; } = new();

    public string Summary()
        => UnitCount == 0
           ? "没有单元参与归属解算。"
           : $"单元 {UnitCount} 个 · 配上作业面 {MatchedCount} 个（{MatchRate * 100:0.#}%）"
             + (ManualCount > 0 ? $" · 其中手工指定 {ManualCount} 个" : "")
             + (Ambiguous.Count > 0 ? $" · ◆ 歧义 {Ambiguous.Count} 个" : "")
             + (Unmatched.Count > 0 ? $" · ◆ 没配上 {Unmatched.Count} 个" : "");
}

/// <summary>
/// 把采掘单元归到作业面上。<b>纯计算，不碰数据库、不碰界面。</b>
/// </summary>
public static class FaceUnitResolver
{
    /// <summary>标高命中的容差（m）。台阶标高与单元顶/底板之间总有零点几米的口径差。</summary>
    public const double DefaultElevToleranceM = 2.0;

    /// <summary>
    /// 解算归属。
    /// </summary>
    /// <param name="units">本月参与排产的单元（要 ZLo/ZHi/Kind，所以吃 <see cref="MineUnit"/> 不吃 UnitAssignment）。</param>
    /// <param name="faces">「确定开采程序」里的作业面清单。</param>
    /// <param name="manual">手工覆盖：单元号 → 作业面名（FA1）。可空。</param>
    /// <param name="elevTolM">标高容差（FA3）。</param>
    public static FaceUnitResolution Resolve(IEnumerable<MineUnit>? units,
                                             IEnumerable<WorkingFace>? faces,
                                             IReadOnlyDictionary<string, string>? manual = null,
                                             double elevTolM = DefaultElevToleranceM)
    {
        var res = new FaceUnitResolution();
        var uList = (units ?? Enumerable.Empty<MineUnit>())
                    .Where(u => u != null && u.UnitId.Length > 0).ToList();
        var fList = (faces ?? Enumerable.Empty<WorkingFace>())
                    .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Name)).ToList();

        res.UnitCount = uList.Count;
        foreach (var f in fList) res.PerFace[f.Name] = 0;

        if (uList.Count == 0) { res.Notes.Add("· 没有单元 —— 先跑一次「采掘单元清单 → 按目标排产」。"); return res; }
        if (fList.Count == 0)
        {
            res.Notes.Add("◆ 一个作业面都没有 —— 先在「确定开采程序」里建面，否则面级设备/工艺配置无处可落。");
            foreach (var u in uList) AddUnmatched(res, u.UnitId, "没有任何作业面");
            return res;
        }

        double tol = Math.Max(0, elevTolM);
        var byName = new Dictionary<string, WorkingFace>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fList) byName[f.Name] = f;

        int manualMiss = 0;
        foreach (var u in uList)
        {
            // ── FA1 手工覆盖优先 ──────────────────────────────────────
            if (manual != null && manual.TryGetValue(u.UnitId, out string? mf) && !string.IsNullOrWhiteSpace(mf))
            {
                if (byName.TryGetValue(mf, out var hitFace))
                {
                    Accept(res, u, hitFace);
                    res.ManualCount++;
                    continue;
                }
                manualMiss++;   // 指定了一个不存在的面 —— 不当没指定，如实报
                AddUnmatched(res, u.UnitId, $"手工指定的面「{mf}」在作业面清单里不存在");
                continue;
            }

            // ── FA2 物料相容 + FA3 标高命中 ────────────────────────────
            var cand = new List<(WorkingFace F, double D)>();
            foreach (var f in fList)
            {
                if (!MaterialCompatible(f, u)) continue;                          // FA2
                if (f.BenchElevationM < u.ZLo - tol || f.BenchElevationM > u.ZHi + tol) continue;  // FA3
                cand.Add((f, Math.Abs(f.BenchElevationM - u.ZHi)));               // FA4 的排序键
            }

            if (cand.Count == 0)
            {
                // FA6：说清是哪一条挡住的 —— "没配上"三个字帮不了任何人
                bool anyMat = fList.Any(f => MaterialCompatible(f, u));
                AddUnmatched(res, u.UnitId, anyMat
                    ? $"{(u.IsCoal ? "煤" : "岩")}·{u.ZLo:0}~{u.ZHi:0}m：没有台阶标高落在这个区间内的同物料作业面"
                    : $"{(u.IsCoal ? "煤" : "岩")}：一个物料相容的作业面都没有");
                continue;
            }

            // ── FA4 取标高最贴近顶板的 ────────────────────────────────
            cand.Sort((a, b) =>
            {
                int c = a.D.CompareTo(b.D);
                return c != 0 ? c : string.CompareOrdinal(a.F.Name, b.F.Name);
            });

            // ── FA5 并列判歧义（不许随便挑一个）────────────────────────
            if (cand.Count > 1 && Math.Abs(cand[0].D - cand[1].D) < 1e-6)
            {
                AddAmbiguous(res, u.UnitId,
                    $"{cand.Count(x => Math.Abs(x.D - cand[0].D) < 1e-6)} 个面标高一样近："
                    + string.Join("、", cand.Where(x => Math.Abs(x.D - cand[0].D) < 1e-6).Select(x => x.F.Name)));
                continue;
            }

            Accept(res, u, cand[0].F);
        }

        // ── 报出来：匹配率、逐面、以及三种"没配上" ──────────────────────
        res.Notes.Add("· " + res.Summary());
        if (manualMiss > 0)
            res.Notes.Add($"◆ {manualMiss} 条手工指定的面名在作业面清单里查不到 —— 这些单元<b>没有</b>退回自动匹配，"
                        + "而是照样算没配上。人写错的名字被悄悄改成自动匹配的结果，比不匹配更难查。");

        var emptyFaces = res.PerFace.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        if (emptyFaces.Count > 0)
        {
            var pinned = emptyFaces.Where(n => byName.TryGetValue(n, out var f) && f.EquipConfiguredCount > 0).ToList();
            res.Notes.Add($"· {emptyFaces.Count} 个作业面这个月一个单元都没配上（{string.Join("、", emptyFaces.Take(5))}）"
                        + (pinned.Count > 0
                            ? $"，<b>其中 {pinned.Count} 个是配了设备型号的</b>（{string.Join("、", pinned.Take(3))}）—— "
                              + "那些型号这个月一台设备都约束不到，等于没配。"
                            : "。"));
        }

        if (res.Unmatched.Count > 0)
            res.Notes.Add($"◆ {res.Unmatched.Count} 个单元没配上作业面，它们<b>不受任何面级约束</b>"
                        + "（排产会在全矿在册设备里挑，穿爆也按「需爆破」算）。"
                        + "前几个：" + string.Join("；", res.Unmatched.Take(3)));
        if (res.Ambiguous.Count > 0)
            res.Notes.Add($"◆ {res.Ambiguous.Count} 个单元有多个作业面并列命中，<b>引擎不替你挑</b> —— "
                        + "同标高同物料摆了两个面，只有现场知道哪个是哪个。"
                        + "要定就在采掘单元台账里手工指定。前几个：" + string.Join("；", res.Ambiguous.Take(3)));

        res.Notes.Add($"· 归属口径 FA1–FA7：手工优先 → 物料相容 → 台阶标高落在单元 [{-tol:0.#}, +{tol:0.#}]m 区间内 → "
                    + "多个命中取标高最贴近顶板的 → 并列判歧义不配。"
                    + "<b>配不上就是配不上，不给兜底面</b>。");
        return res;
    }

    /// <summary>
    /// 记一条"没配上"。<b>单元号与给人读的那句话【一起】写进去</b> ——
    /// 分两处写迟早会漏一处，而漏了之后归属覆盖窗口里就少一行，看起来像"这个单元配上了"。
    /// </summary>
    private static void AddUnmatched(FaceUnitResolution res, string unitId, string why)
    {
        res.UnmatchedIds.Add(unitId);
        res.Unmatched.Add($"{unitId}（{why}）");
    }

    private static void AddAmbiguous(FaceUnitResolution res, string unitId, string why)
    {
        res.AmbiguousIds.Add(unitId);
        res.Ambiguous.Add($"{unitId}（{why}）");
    }

    /// <summary>FA2：煤面只配煤单元，岩/表土面只配岩单元。</summary>
    private static bool MaterialCompatible(WorkingFace f, MineUnit u)
        => PlanMaterialCatalog.Resolve(f.MaterialCode).IsOre == u.IsCoal;

    private static void Accept(FaceUnitResolution res, MineUnit u, WorkingFace f)
    {
        res.UnitToFace[u.UnitId] = f.Name;
        res.MatchedCount++;
        res.PerFace[f.Name] = res.PerFace.TryGetValue(f.Name, out int c) ? c + 1 : 1;

        // FA7：面的物料不需要爆破 ⇒ 这个单元免爆（表土/风化层直接铲装）
        if (!PlanMaterialCatalog.Resolve(f.MaterialCode).NeedsBlasting)
            res.NoBlastUnits.Add(u.UnitId);
    }
}
