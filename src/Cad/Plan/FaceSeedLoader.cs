// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/FaceSeedLoader.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  期次台账 → 派生作业面的种子。
//
//  这一层只做「读 + 换口径」，一条判断都不做 —— 分组、标高、份额全在
//  <see cref="FaceAutoBuilder"/> 里，那样判据喂算例就够，不用真台账。
//
//  ⚠ **排土位置不是作业面**：期次文件里有几百行排土位置（本期的卸点），
//    它们没有台阶、没有物料、不归属设备。混进来的话会派生出一堆
//    "岩 1000m" 的假面，而每个面看上去都正常。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次装载的结果。</summary>
public sealed class FaceSeedLoadResult
{
    public List<FaceSeedUnit> Units = new();
    public string Label = "";
    /// <summary>装载过程中的话（编码、丢行、口径）。</summary>
    public List<string> Notes = new();
    public bool Ok => Units.Count > 0;
}

/// <summary>把某一期的采场单元读成派生种子。永不抛。</summary>
public static class FaceSeedLoader
{
    /// <summary>
    /// 读一期。
    /// </summary>
    /// <param name="period">期次（如 <c>2026-08</c>）。</param>
    /// <param name="ledgerRoot">台账目录；null = 默认。</param>
    public static FaceSeedLoadResult Load(string? period, string? ledgerRoot = null)
    {
        var res = new FaceSeedLoadResult();
        string p = (period ?? "").Trim();
        if (p.Length == 0) { res.Label = "没有指定期次。"; return res; }

        List<MiningUnitLedger.Row> rows;
        try
        {
            var store = new MonthlyUnitLedgerStore(ledgerRoot);
            if (!store.Exists(p))
            {
                res.Label = $"◆ 盘上没有期次「{p}」（{store.MonthPath(p)}）—— "
                          + "先去「采掘单元清单」点一次「一键排本月」。"
                          + "**这里不拿基表全量顶替**：那等于把全矿家底当成本月任务。";
                return res;
            }
            if (!store.TryLoad(p, out rows, out var issues))
            { res.Label = $"◆ 期次「{p}」读不出来。"; res.Notes.AddRange(issues ?? new List<string>()); return res; }
            foreach (var s in (issues ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)))
                res.Notes.Add(s);
        }
        catch (Exception ex) { res.Label = $"◆ 期次读取出错（{ex.GetType().Name}：{ex.Message}）"; return res; }

        int dump = 0, noZ = 0;
        foreach (var r in rows)
        {
            if (r == null) continue;
            // 排土位置不是作业面 —— 它没有台阶、没有物料、不归属设备
            if (r.Kind == LedgerKind.Dump) { dump++; continue; }

            bool coal = r.Kind == LedgerKind.Coal;
            if (!Finite(r.ZLo) || !Finite(r.ZHi) || r.ZHi <= r.ZLo) noZ++;   // 交给派生器点名，这里只计数

            res.Units.Add(new FaceSeedUnit
            {
                UnitId = r.UnitId,
                IsCoal = coal,
                ZLo = r.ZLo, ZHi = r.ZHi,
                CoalT = coal ? (r.CoalT ?? 0) : 0,
                RockM3 = coal ? 0 : (r.NetRockM3 ?? 0),
                BenchName = r.Seam ?? "",
            });
        }

        res.Label = $"期次「{p}」：采场单元 {res.Units.Count} 个"
                  + $"（煤 {res.Units.Count(u => u.IsCoal)} · 岩 {res.Units.Count(u => !u.IsCoal)}）"
                  + (dump > 0 ? $"　· 另有 {dump} 行排土位置**不参与派生**（它不是作业面）" : "")
                  + (noZ > 0 ? $"　◆ {noZ} 个单元标高不成立，派生时会被点名" : "");
        return res;
    }

    /// <summary>
    /// 盘上最新的一期。
    /// <para><b>退到最新一期时必须让调用方说出来</b> —— 否则人以为在派生本月，
    /// 其实派生的是三个月前那一期，而面上的标高全都对得上、看不出任何异样。</para>
    /// </summary>
    public static string LatestPeriod(out string note, string? ledgerRoot = null)
    {
        note = "";
        try
        {
            var months = new MonthlyUnitLedgerStore(ledgerRoot).ListMonths();
            if (months.Count == 0)
            { note = "台账目录里一期都没有 —— 先「一键排本月」。"; return ""; }
            string latest = months[^1];
            if (months.Count > 1) note = $"盘上有 {months.Count} 期，取最新的「{latest}」。";
            return latest;
        }
        catch (Exception ex) { note = $"期次列表读不出来（{ex.GetType().Name}）"; return ""; }
    }

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
