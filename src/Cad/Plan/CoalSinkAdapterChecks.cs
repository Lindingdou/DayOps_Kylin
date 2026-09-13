// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/CoalSinkAdapterChecks.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;         // LoadUnloadPoint
using PitMine3D.Kylin.Cad.Units;             // MineUnit / UnitKind
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  CoalSinkAdapter 的判据。
//
//  全部打在 CoalSinkAdapter.ResolveFrom（纯函数版）上 —— 不碰数据库，所以能离线跑、能重放。
//
//  写判据的纪律（本仓库有前科）：只判"成功"的判据会空过。所以每条都同时钉住
//  【该发生什么】和【不该发生什么】：
//    · 判"降级了"的同时判"没谎称是真点"；
//    · 判"用了真点"的同时判"坐标不等于采场质心" —— 否则适配把台账整个忽略掉、
//      一路降级，"Sinks 非空"这个判据照样全绿。
//  ResolveFrom 的返回值里 Source / Rejected / 坐标三样都在，就是为了让判据抓得住。
//
//  ★ 这套判据做过变异验证（不是"跑绿了就算数"）：
//    M1 坐标判据改成恒真      → J3/J3b/J4 挂（9 条）  ✔ 杀掉
//    M3 忽略台账一路降级      → J2/J4/J7/J9/J10 挂（10 条）✔ 杀掉
//    M2 只去掉 KindOf 名字分支 → **全绿**  ✘ 没杀掉（见 J7 处说明）
//    M4 名字分支 + FromText「仓」规则一起去 → J7 挂  ✔ 杀掉
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="CoalSinkAdapter"/> 的离线判据。<see cref="RunAll"/> 返回失败描述，空 = 全过。
/// </summary>
public static class CoalSinkAdapterChecks
{
    // 采场质心固定在 (1000, 2000, 1185)：三个煤单元围着它摆，质心正好是它。
    private const double PitCx = 1000, PitCy = 2000, PitCz = 1185;

    private static List<MineUnit> CoalUnits() => new()
    {
        new MineUnit { UnitId = "c4-B1-P1", Kind = UnitKind.Coal, Cx = 900,  Cy = 1900, Cz = 1180, InSituM3 = 10000 },
        new MineUnit { UnitId = "c4-B1-P2", Kind = UnitKind.Coal, Cx = 1000, Cy = 2000, Cz = 1185, InSituM3 = 10000 },
        new MineUnit { UnitId = "c4-B1-P3", Kind = UnitKind.Coal, Cx = 1100, Cy = 2100, Cz = 1190, InSituM3 = 10000 },
    };

    private static List<MineUnit> RockOnly() => new()
    {
        new MineUnit { UnitId = "岩1185-B1-P1", Kind = UnitKind.Rock, Cx = 900, Cy = 1900, Cz = 1200, InSituM3 = 50000 },
    };

    private static LoadUnloadPoint P(long id, string name, string kind, string? sub,
                                     double x, double y, double z, double tph = 0)
        => new() { Id = id, Name = name, Kind = kind, UnloadSub = sub, X = x, Y = y, Z = z, ThroughputTph = tph };

    private static bool IsPitCentroid(CoalSink s)
        => Math.Abs(s.Cx - PitCx) < 1e-6 && Math.Abs(s.Cy - PitCy) < 1e-6;

    /// <summary>跑全部判据。返回失败描述（空列表 = 全过）。</summary>
    public static List<string> RunAll()
    {
        var f = new List<string>();
        void Fail(string id, string what) => f.Add($"[{id}] {what}");

        // ── J1 空表 ⇒ 必须降级，且必须留条说清是降级 ──────────────────────────
        {
            var r = CoalSinkAdapter.ResolveFrom(Array.Empty<LoadUnloadPoint>(), CoalUnits());
            if (r.Source != CoalSinkSource.PitCentroid)
                Fail("J1", $"空表应降级到采场质心，实际 Source={r.Source}");
            if (r.UsesRealPoints)
                Fail("J1", "空表降级后 UsesRealPoints 仍为真 —— 谎称是真点");
            if (r.Sinks.Count != 1 || !IsPitCentroid(r.Sinks[0]))
                Fail("J1", "降级出矿点应当且仅当是采场质心 (1000,2000)");
            if (!r.Notes.Any(n => n.Contains("空表")))
                Fail("J1", "空表降级没有留条说明是空表");
            if (!r.SourceText.Contains("默认位置"))
                Fail("J1", "降级文案里没有把「默认位置」说出来");
        }

        // ── J2 有真点 ⇒ 必须用真点，且坐标不能是质心（防"忽略台账一路降级"空过）──
        {
            var rows = new[] { P(7, "1号破碎站", "unloading", "crusher", 5000, 6000, 1100) };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.Source != CoalSinkSource.Ledger)
                Fail("J2", $"录了真点却没用，Source={r.Source}");
            if (!r.UsesRealPoints) Fail("J2", "UsesRealPoints 应为真");
            if (r.Sinks.Count != 1) Fail("J2", $"应有 1 个出矿点，实际 {r.Sinks.Count}");
            else
            {
                var s = r.Sinks[0];
                if (IsPitCentroid(s))
                    Fail("J2", "出矿点坐标等于采场质心 —— 台账被忽略了，实际走的是降级");
                if (Math.Abs(s.Cx - 5000) > 1e-6 || Math.Abs(s.Cy - 6000) > 1e-6 || Math.Abs(s.Cz - 1100) > 1e-6)
                    Fail("J2", $"出矿点坐标没取台账值：({s.Cx},{s.Cy},{s.Cz})");
                if (s.Code != "LU-7") Fail("J2", $"码应为 LU-7，实际 {s.Code}");
            }
        }

        // ── J3 坐标缺失 (0,0,0) ⇒ 拒收，不能当真点 ────────────────────────────
        {
            var rows = new[] { P(7, "1号破碎站", "unloading", "crusher", 0, 0, 0) };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.Source != CoalSinkSource.PitCentroid)
                Fail("J3", $"(0,0,0) 的点被当成了真点，Source={r.Source}");
            if (r.RejectedNoCoord != 1) Fail("J3", $"应拒收 1 个，实际 {r.RejectedNoCoord}");
            if (!r.Rejected.Any(x => x.Contains("1号破碎站")))
                Fail("J3", "拒收没有指到具体是哪个点");
            if (r.Sinks.Any(s => !IsPitCentroid(s)))
                Fail("J3", "拒收后仍混进了非质心的出矿点");
        }

        // ── J3b 只补了标高的半成品行 (0,0,1185) ⇒ 同样拒收（XY 判据，不是三零判据）──
        {
            var rows = new[] { P(8, "2号破碎站", "unloading", "crusher", 0, 0, 1185) };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.RejectedNoCoord != 1)
                Fail("J3b", "(0,0,1185) 没被拒收 —— 判据退化成了「三个都是 0」");
            if (r.Source != CoalSinkSource.PitCentroid)
                Fail("J3b", "(0,0,1185) 被当成了真点");
        }

        // ── J4 好点 + 坏点混合 ⇒ 用好点，但坏点仍要留条（不能因为有好点就吞掉）──
        {
            var rows = new[]
            {
                P(7, "1号破碎站", "unloading", "crusher", 5000, 6000, 1100),
                P(8, "2号破碎站", "unloading", "crusher", 0, 0, 0),
            };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.Source != CoalSinkSource.Ledger) Fail("J4", "有可用真点时应走真点");
            if (r.Sinks.Count != 1) Fail("J4", $"只应收 1 个好点，实际 {r.Sinks.Count}");
            if (r.RejectedNoCoord != 1) Fail("J4", "坏点被悄悄吞掉了，没计入拒收");
            if (!r.Rejected.Any(x => x.Contains("2号破碎站")))
                Fail("J4", "有好点时坏点的留条丢了");
        }

        // ── J5 排土场是卸载点但不收煤 ⇒ 不进出矿点，也不算「拒收」──────────────
        {
            var rows = new[] { P(9, "北排土场", "unloading", "dump", 5000, 6000, 1100) };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.Source != CoalSinkSource.PitCentroid)
                Fail("J5", "排土场被当成了出矿点");
            if (r.CoalCapableRows != 0) Fail("J5", $"排土场不该计入能收煤的行，实际 {r.CoalCapableRows}");
            if (r.RejectedNoCoord != 0) Fail("J5", "排土场不是「坐标拒收」，不该计进去");
            if (r.UnloadingRows != 1) Fail("J5", $"卸载点计数应为 1，实际 {r.UnloadingRows}");
        }

        // ── J6 采剥点(源)不是出矿点 ───────────────────────────────────────────
        {
            var rows = new[] { P(10, "采区A装车点", "loading", null, 5000, 6000, 1100) };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.Source != CoalSinkSource.PitCentroid) Fail("J6", "loading 点被当成了出矿点");
            if (r.UnloadingRows != 0) Fail("J6", $"loading 点不该计入卸载点，实际 {r.UnloadingRows}");
        }

        // ── J7 名字里的「原煤仓」要救得回来 ───────────────────────────────────
        //  RoadLib「装卸点设置」的子类下拉没有原煤仓，现场只能落 sub='dump'。
        //  纯按码判会把它当排土场 ⇒ 煤进不去 ⇒ 这个点从出矿点里凭空消失。
        //
        //  ★ 这条判的是【结果】不是【机制】：认出「原煤仓」有两条独立的路 ——
        //    KindOf 的名字分支，以及兜底 PlanSinkKinds.FromText 里的「仓」规则。
        //    实测（变异 M2）去掉名字分支这条判据照样全绿，因为 FromText 接住了；
        //    两条一起去掉（变异 M4）才挂。所以别拿它当"名字优先生效"的证据，
        //    它保的是"名字叫原煤仓的点必须收得进出矿点"这个结果。
        {
            var rows = new[] { P(11, "原煤仓", "unloading", "dump", 5000, 6000, 1100) };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (CoalSinkAdapter.KindOf(rows[0]) != PlanSinkKind.Silo)
                Fail("J7", "名字叫「原煤仓」、子类填 dump 的点没被认成原煤仓");
            if (r.Source != CoalSinkSource.Ledger)
                Fail("J7", "「原煤仓」被子类码 dump 挡掉了 —— 名字优先没生效");
        }

        // ── J8 没有煤单元 ⇒ 不装配出矿点（也不该降级出一个假点）──────────────
        {
            var rows = new[] { P(7, "1号破碎站", "unloading", "crusher", 5000, 6000, 1100) };
            var r = CoalSinkAdapter.ResolveFrom(rows, RockOnly());
            if (r.Source != CoalSinkSource.NoCoal) Fail("J8", $"无煤单元时 Source={r.Source}");
            if (r.Sinks.Count != 0) Fail("J8", "无煤单元却装配了出矿点");
        }

        // ── J9 通过能力：不给小时 ⇒ 不折算（0=不限），给了小时 ⇒ 按乘积 ─────────
        {
            var rows = new[] { P(7, "1号破碎站", "unloading", "crusher", 5000, 6000, 1100, tph: 2000) };

            var noHrs = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (noHrs.Sinks.Count == 1 && Math.Abs(noHrs.Sinks[0].CapacityT) > 1e-9)
                Fail("J9", $"没给月作业小时却折出了月能力 {noHrs.Sinks[0].CapacityT}");
            if (!noHrs.Notes.Any(n => n.Contains("不限")))
                Fail("J9", "没折算时没有留条说明按「不限」处理");

            var hrs = CoalSinkAdapter.ResolveFrom(rows, CoalUnits(), monthlyOperatingHours: 500);
            if (hrs.Sinks.Count != 1 || Math.Abs(hrs.Sinks[0].CapacityT - 1_000_000) > 1e-6)
                Fail("J9", "给了 500h 时月能力应为 2000×500 = 1,000,000 t");
        }

        // ── J10 储煤场 / 破碎站 / 原煤仓 三类都要收得进来 ─────────────────────
        {
            var rows = new[]
            {
                P(1, "1号破碎站", "unloading", "crusher",   5000, 6000, 1100),
                P(2, "原煤仓",    "unloading", "silo",      5100, 6100, 1100),
                P(3, "储煤场",    "unloading", "stockpile", 5200, 6200, 1100),
            };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.Sinks.Count != 3) Fail("J10", $"破碎站/原煤仓/储煤场三类应全收，实际 {r.Sinks.Count}");
            if (r.Sinks.Select(s => s.Code).Distinct().Count() != 3) Fail("J10", "出矿点码有重复");
        }

        // ══ 以下 J11–J20 是【边界与退化输入】：库里的脏数据、调用方的空引用、文案串味 ══
        //  前十条判的是"正常口径对不对"，这十条判的是"不正常时会不会静默出错"。
        //  坐标列是 NOT NULL DEFAULT 0，挡得住 null 挡不住 NaN；卸载点 kind 是自由文本，
        //  大小写/空白全靠读取方自己收。这些都不是假想 —— 是同一张表已经出过的花样。

        // ── J11 NaN / Inf 坐标 ⇒ 拒收（DEFAULT 0 只挡得住"没填"，挡不住脏数）──────
        {
            var r = CoalSinkAdapter.ResolveFrom(
                new[] { P(1, "破碎站N", "unloading", "crusher", double.NaN, 6000, 1100) }, CoalUnits());
            if (r.Source != CoalSinkSource.PitCentroid || r.RejectedNoCoord != 1)
                Fail("J11", $"NaN 坐标没被拒收，Source={r.Source} 拒收数={r.RejectedNoCoord}");

            var r2 = CoalSinkAdapter.ResolveFrom(
                new[] { P(1, "破碎站I", "unloading", "crusher", 5000, double.PositiveInfinity, 1100) }, CoalUnits());
            if (r2.RejectedNoCoord != 1) Fail("J11", "Inf 坐标没被拒收");
        }

        // ── J12 只有一个轴为 0 的点是真点 ⇒ 不能误伤（判据是"XY 都为 0"，不是"任一为 0"）──
        {
            var r = CoalSinkAdapter.ResolveFrom(
                new[] { P(1, "破碎站X", "unloading", "crusher", 5000, 0, 1100) }, CoalUnits());
            if (r.Source != CoalSinkSource.Ledger)
                Fail("J12", "X 有值、Y=0 的点被当成了「没录坐标」—— 坐标判据收得过紧，真点被误伤");
        }

        // ── J13 kind 的大小写 / 前后空白要收得住（库里是自由文本列）────────────────
        {
            var r = CoalSinkAdapter.ResolveFrom(
                new[] { P(1, "破碎站C", "unloading".ToUpperInvariant().PadLeft(12).PadRight(14), "crusher", 5000, 6000, 1100) },
                CoalUnits());
            if (r.Source != CoalSinkSource.Ledger)
                Fail("J13", "kind 写成「 UNLOADING 」就认不出来了");
        }

        // ── J14 子类空 + 名字也认不出 ⇒ 回落外排、不收煤（宁可漏，不可错认成出矿点）──
        {
            var r = CoalSinkAdapter.ResolveFrom(
                new[] { P(1, "东侧卸点", "unloading", null, 5000, 6000, 1100) }, CoalUnits());
            if (r.Source != CoalSinkSource.PitCentroid || r.CoalCapableRows != 0)
                Fail("J14", $"子类空、名字认不出的卸载点被当成了出矿点，Source={r.Source}");
        }

        // ── J15 台账里混进 null 行 / units 为 null ⇒ 不许炸 ────────────────────────
        {
            var r = CoalSinkAdapter.ResolveFrom(
                new LoadUnloadPoint?[] { null, P(1, "破碎站", "unloading", "crusher", 5000, 6000, 1100) }!,
                CoalUnits());
            if (r.Sinks.Count != 1) Fail("J15", $"台账里有 null 行时结果不对，Sinks={r.Sinks.Count}");

            var r2 = CoalSinkAdapter.ResolveFrom(
                new[] { P(1, "破碎站", "unloading", "crusher", 5000, 6000, 1100) }, null);
            if (r2.Source != CoalSinkSource.NoCoal || r2.Sinks.Count != 0)
                Fail("J15", "units=null 时没走「本月没有煤单元」这一支");
        }

        // ── J16 降级文案不许混进真点措辞 ─────────────────────────────────────────
        //  这条判的是**界面上贴出去的那段字**。适配判对了但文案说反，用户照样会把
        //  质心当成真实运距拿去比选 —— 那比算错更糟，因为它看上去是对的。
        {
            var r = CoalSinkAdapter.ResolveFrom(Array.Empty<LoadUnloadPoint>(), CoalUnits());
            string d = r.Describe();
            if (!d.Contains("默认位置") || !d.Contains("空表"))
                Fail("J16", "空表 Describe() 没把「降级 / 空表」说出口");
            if (d.Contains("真实点位"))
                Fail("J16", "降级文案里出现了「真实点位」—— 两种情况串味了");
        }

        // ── J17 真点文案不许混进降级措辞（J16 的反向，两条一起才封得住）────────────
        {
            var r = CoalSinkAdapter.ResolveFrom(
                new[] { P(1, "破碎站", "unloading", "crusher", 5000, 6000, 1100) }, CoalUnits());
            string d = r.Describe();
            if (!d.Contains("真实点位")) Fail("J17", "有真点时文案没说是真实点位");
            if (d.Contains("采场质心")) Fail("J17", "有真点时文案里仍出现「采场质心」");
        }

        // ── J18 月作业小时为负 / NaN ⇒ 按不折算，绝不折出负能力或 NaN 能力 ──────────
        //  负能力会让引擎里的 Remain 判据整条失效（Remain<0 ⇒ 一点也排不进），
        //  而"排不进"和"没这个点"在结果上长得一样。
        {
            var rows = new[] { P(1, "破碎站", "unloading", "crusher", 5000, 6000, 1100, tph: 2000) };
            foreach (double h in new[] { -100.0, double.NaN, double.PositiveInfinity })
            {
                var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits(), h);
                if (r.Sinks.Count != 1 || Math.Abs(r.Sinks[0].CapacityT) > 1e-9)
                    Fail("J18", $"月作业小时={h} 时折出了能力 {(r.Sinks.Count == 1 ? r.Sinks[0].CapacityT : double.NaN)}");
            }
        }

        // ── J19 重名不同 id ⇒ 码必须仍然唯一（下游按 Code 对账，撞码会把两个点并成一个）──
        {
            var rows = new[]
            {
                P(1, "破碎站", "unloading", "crusher", 5000, 6000, 1100),
                P(2, "破碎站", "unloading", "crusher", 5100, 6100, 1100),
            };
            var r = CoalSinkAdapter.ResolveFrom(rows, CoalUnits());
            if (r.Sinks.Count != 2) Fail("J19", $"重名的两个点应各收一个，实际 {r.Sinks.Count}");
            if (r.Sinks.Select(s => s.Code).Distinct().Count() != r.Sinks.Count)
                Fail("J19", "重名点的码撞了 —— 下游按 Code 对账会并成一个");
        }

        // ── J20 名字空白的行要能指得出是哪一个（拒收留条不能只写「」）────────────
        {
            var r = CoalSinkAdapter.ResolveFrom(
                new[] { P(42, "   ", "unloading", "crusher", 0, 0, 0) }, CoalUnits());
            if (r.RejectedNoCoord != 1) Fail("J20", "空名字 + 空坐标的行没被拒收");
            if (!r.Rejected.Any(x => x.Contains("42")))
                Fail("J20", "名字是空白时，拒收留条里没有 id，指不到具体是哪一行");
        }

        return f;
    }

    /// <summary>跑一遍并拼成可读报告（界面/日志直接贴）。</summary>
    public static string Report()
    {
        var f = RunAll();
        return f.Count == 0
            ? "CoalSinkAdapter 判据：全部通过（J1 空表降级 · J2 真点优先 · J3/J3b 坐标缺失拒收 · "
            + "J4 好坏混合 · J5 排土场不收煤 · J6 源点不是汇 · J7 原煤仓收得进 · J8 无煤 · "
            + "J9 通过能力折算 · J10 三类齐全 · J11 NaN/Inf 拒收 · J12 单轴 0 不误伤 · "
            + "J13 kind 大小写 · J14 认不出不错认 · J15 null 输入 · J16/J17 两种文案不串味 · "
            + "J18 负/NaN 小时不折算 · J19 重名码不撞 · J20 空名字指得到行）"
            : $"CoalSinkAdapter 判据：{f.Count} 条没过\n  " + string.Join("\n  ", f);
    }
}
