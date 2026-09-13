// 忠实移植自原 PitMine3D Modules/TaskLib/Zoning/ZoneLinkage.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.TaskLib.Zoning;

// ─────────────────────────────────────────────────────────────────────────────
//  「这块区域会不会进推演」的当场判定。
//
//  ── 为什么需要它 ──
//  区域画完入库，推演那边能不能用上，取决于四件事，而**一件都不会报错**：
//    ① Category 定极性：pit/mineable 内缩（采场推进）、*_dump 外扩（排土堆填）；
//    ② Name 要对上盘子：采场类按名字对源（FaceInput.Zone / EngineeringPositionId），
//       排土类按名字对汇（SinkNode.Name / Id / RefEntityId）；
//    ③ 顶点 ≥3（少一个点整块被 SimRegionLoader 静默跳过）；
//    ④ Z 要有出处（写 0 会被当成台账实测高程，层体整体摆到 0m）。
//  对不上时 SimPlanScene.Build 走的是"不猜"分支：该区**整期不动**，
//  界面显示"本期无采掘"，没有任何红字。所以必须在划区这一步就把它说出来。
//
//  ── 判据不做镜像 ──
//  名字配对直接调 <see cref="SimPlanScene.NameHit"/>（推演真正用的那个函数），
//  汇的配对直接读 <see cref="SimRegionLoader.Load"/> 跑完 MatchSinks 之后的结果。
//  照抄一份判据，改一处就两边分家 —— 那正是这个面板要防的事。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>区域在推演里的绑定方式。</summary>
public enum ZoneBind
{
    /// <summary>按名字直接对上了源/汇。</summary>
    Direct,
    /// <summary>没对上，但同类区域全场只有这一块 —— 推演会让它吃下全部量（诚实兜底，总量不丢）。</summary>
    Fallback,
    /// <summary>没对上且不止一块 —— 推演**不猜**，该区整期不动。</summary>
    None,
}

/// <summary>一块区域的推演关联诊断。</summary>
public sealed class ZoneLink
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public bool IsDump { get; init; }
    /// <summary>本期是否选定（未选定的一律进不了推演，其余四条判得再对也没用）。</summary>
    public bool Active { get; init; }

    /// <summary>推进极性（Category 决定，写给人看）。</summary>
    public string Polarity { get; init; } = "";
    public ZoneBind Bind { get; init; }
    /// <summary>绑定到哪个源/汇（清单里的一列）。</summary>
    public string BindText { get; init; } = "";
    /// <summary>几何是否可用（顶点 ≥3）。</summary>
    public bool RingOk { get; init; }
    /// <summary>高程出处一句话。</summary>
    public string ZText { get; init; } = "";
    /// <summary>排土类的坡面角来源（配到汇才有；没有则层体退回垂直壁）。</summary>
    public string SlopeText { get; init; } = "";

    /// <summary>清单里那一列的结论：会进推演 / 兜底进推演 / 本期不动。</summary>
    public string Verdict { get; init; } = "";
    /// <summary>结论成立还是有问题（清单着色用）。</summary>
    public bool Healthy { get; init; }
    /// <summary>逐条展开的诊断详情（选中行下方显示）。</summary>
    public List<string> Detail { get; init; } = new();
}

/// <summary>诊断一整批区域的结果。</summary>
public sealed class ZoneLinkReport
{
    public List<ZoneLink> Links { get; init; } = new();
    /// <summary>盘子来源（对谁诊断的）。</summary>
    public string Header { get; init; } = "";
    /// <summary>可供「改名」下拉的候选：盘子里的源名 + 汇名。</summary>
    public List<string> BindableNames { get; init; } = new();
    /// <summary>整批的口径说明。</summary>
    public List<string> Notes { get; init; } = new();

    /// <summary>选定 + 几何成立 + 名字对上 —— 真会驱动推演的块数。</summary>
    public int OkCount => Links.Count(l => l.Healthy && l.Bind == ZoneBind.Direct);
    /// <summary>选定了但进不去（几何不成立 / 名字对不上）—— 这才是要人处理的。</summary>
    public int DeadCount => Links.Count(l => l.Active && (l.Bind == ZoneBind.None || !l.RingOk));
    /// <summary>本期未选定的块数（不是错，是范围）。</summary>
    public int InactiveCount => Links.Count(l => !l.Active);
}

public static class ZoneLinkage
{
    /// <summary>
    /// 按当前盘子诊断一批区域。永不抛：盘子读不出来时只诊断几何那两条，并在 Header 里说清楚。
    /// </summary>
    public static ZoneLinkReport Diagnose(IReadOnlyList<ZoneRecord> zones)
    {
        var links = new List<ZoneLink>();
        var notes = new List<string>();
        var names = new List<string>();

        ExploderConfig? cfg = null;
        string header;
        try
        {
            cfg = ProductionPlanContext.Config();
            header = $"按当日盘子诊断（{ProjectScope.Caption}）：源 {cfg.Faces.Count(f => f.Process == ProcessType.Load)} 个 · "
                   + $"排弃类去向 {cfg.Sinks.All.Count(s => s.IsDumping)} 个";
        }
        catch (Exception ex)
        {
            header = $"盘子读不出来（{Short(ex)}）—— 只能诊断几何与高程，名字能不能对上源/汇无从判断";
        }

        // 采场类的候选名：源名 + 工程位置 id（推演两者都会拿来配对）
        // ★ 2026-08-18：**同时带上 FaceInput 本体** —— 绑定改成位置优先，
        //   而位置在 f.SourceX/Y 上。原来这里只提取名字，于是位置那条路根本无从走，
        //   界面上常年是「名字对不上任何作业面」。
        var faceObjs = new List<FaceInput>();
        var faceNames = new List<(string Zone, string Ep)>();
        var sinkNodes = new List<SinkNode>();
        if (cfg != null)
        {
            try
            {
                faceObjs = cfg.Faces.Where(f => f != null && f.Process == ProcessType.Load).ToList();
                faceNames = faceObjs.Select(f => (f.Zone, f.EngineeringPositionId)).ToList();
                sinkNodes = cfg.Sinks.All.Where(s => s.IsDumping).ToList();
            }
            catch { }

            names.AddRange(faceNames.Select(f => f.Zone).Where(s => !string.IsNullOrWhiteSpace(s)));
            names.AddRange(faceNames.Select(f => f.Ep).Where(s => !string.IsNullOrWhiteSpace(s)));
            names.AddRange(sinkNodes.Select(s => s.Name).Where(s => !string.IsNullOrWhiteSpace(s)));
            names = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // 推演对「唯一一块」有兜底：全场只有一块采场 / 一块排土场时，它吃下全部量。
        // ★ 只数 Active 的：SimRegionLoader 压根看不到未选定的那些，
        //   拿全表去数会让"选定后只剩一块"的兜底判反（说成不兜底，实际会兜）。
        int pitCount = zones.Count(z => z.Active && z.IsPit && z.RingUsable);
        int dumpCount = zones.Count(z => z.Active && z.IsDump && z.RingUsable);
        int inactive = zones.Count(z => !z.Active);

        foreach (var z in zones)
            links.Add(One(z, faceNames, faceObjs, sinkNodes, pitCount, dumpCount, cfg != null));

        if (inactive > 0)
            notes.Add($"本期作业范围：{zones.Count - inactive}/{zones.Count} 块选定。"
                    + $"未选定的 {inactive} 块留在台账里，但**不进推演的推进轮廓，也不参与路网中心线提取的裁剪范围**。");
        if (zones.Count > 0 && inactive == zones.Count)
            notes.Add("⚠ 一块都没选定 —— 推演会退回**示意图形**（形状不代表真实位置），"
                    + "路网提取改为全图不限范围。要恢复就在清单里勾「选定」。");
        if (zones.Count == 0)
            notes.Add("台账里一块区域都没有 —— 推演会退到**示意图形**：尺寸由真实工程量反推（采场按工作线长、"
                    + "排土场按设计容量÷台阶高），但**位置与形状不代表真实边界**，也没法跟影像对上。");
        if (pitCount == 1)
            notes.Add("全场只有一块采场类区域：推演会让它**吃下全部采场挖除量**（不按名字配也不丢量）。"
                    + "再画第二块之前，名字对不对得上源都不影响结果；画了第二块之后，两块都必须能对上名字。");
        if (dumpCount == 1)
            notes.Add("全场只有一块排土类区域：推演会让它**吃下全部排弃占容**。同上，第二块一出现，名字就成了硬条件。");

        return new ZoneLinkReport { Links = links, Header = header, BindableNames = names, Notes = notes };
    }

    /// <param name="faceObjs">作业面本体 —— <b>绑定按位置优先</b>，位置在 <c>SourceX/Y</c> 上。
    /// 只传名字的话位置那条路根本无从走（这正是改这一版之前的样子）。</param>
    private static ZoneLink One(ZoneRecord z, List<(string Zone, string Ep)> faces,
                                List<FaceInput> faceObjs, List<SinkNode> sinks,
                                int pitCount, int dumpCount, bool planOk)
    {
        var detail = new List<string>();
        bool ringOk = z.RingUsable;

        // ── ⓪ 本期选定 —— 排在最前：没选定，后面四条判得再对也进不去 ──
        if (!z.Active)
            detail.Add("⓪ 本期未选定：这块区域**不进推演的推进轮廓，也不参与路网中心线提取的裁剪范围**"
                     + "（台账里还在，别处按名字挑区域的功能照常能选到它）。勾上左侧「选定」即可参与。");

        // ── ① 极性 ──
        if (z.IsWorkingSlope)
        {
            // 工作帮不推进：它是采场 / 排土场**之内**再收的一层范围闸门，给「采矿模型」「排土条带」筛范围用。
            // 推演里整块排除（SimRegionLoader 同判据），所以这里不该报一个它根本用不上的极性。
            detail.Add($"① 推进极性：不适用 —— 类别「{ZoneStore.CategoryZh(z.Category)}」是**范围闸门**"
                     + "（采场 / 排土场的子区域），与母范围重叠属正常，不作独立轮廓进推演，也不做扣除运算。");
        }
        else
        {
            string polarity = z.IsDump
                ? $"外扩（排土堆填）—— 类别「{ZoneStore.CategoryZh(z.Category)}」"
                : $"内缩（采场推进）—— 类别「{ZoneStore.CategoryZh(z.Category)}」";
            detail.Add("① 推进极性：" + polarity
                     + "。类别填反了轮廓会朝反方向动，推演不会报错。");
        }

        // ── ② 几何 ──
        if (ringOk)
            detail.Add($"② 几何：{z.PointCount} 个顶点，面积 {z.AreaM2 / 1e4:0.##} 万 m²，可用。");
        else
            detail.Add($"② 几何：只有 {z.PointCount} 个顶点（需 ≥3）—— **推演装载时会静默跳过这一块**，"
                     + "台账里看得见、推演里没有。");

        // ── ③ 高程 ──
        string prov = z.ZProvenance;
        string zText;
        if (!ringOk) zText = "—";
        else if (prov.Length > 0) zText = prov;
        else
        {
            double az = z.AvgZ;
            zText = Math.Abs(az) < 1e-9
                ? "⚠ 全环 Z=0 且无出处记录"
                : $"均值 {az:0.##}m（无出处记录，本窗口之外入库的）";
        }
        detail.Add("③ 高程：" + zText
                 + (Math.Abs(z.AvgZ) < 1e-9 && ringOk
                    ? " —— 0m 会被推演当作**台账实测高程**（ParseXyz 只有在没有顶点时才返回 NaN），"
                    + "于是「没有 Z 就去现状面采」那条兜底不会跑，层体整体摆在 0 米。"
                    : ""));

        // ── ④ 绑定 ──
        ZoneBind bind;
        string bindText;
        string slopeText = "";

        if (!planOk)
        {
            bind = ZoneBind.None;
            bindText = "盘子未就绪";
            detail.Add("④ 绑定：当日盘子读不出来，无法判断名字能不能对上源/汇。");
        }
        else if (z.IsDump)
        {
            var hit = sinks.FirstOrDefault(s =>
                       string.Equals(s.Name, z.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.RefEntityId, z.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Id, z.Name, StringComparison.OrdinalIgnoreCase)
                    || (z.Name.Length > 1 && s.Name.Contains(z.Name, StringComparison.OrdinalIgnoreCase))
                    || (s.Name.Length > 1 && z.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase)));

            if (hit != null)
            {
                bind = ZoneBind.Direct;
                bindText = $"{hit.Name}（{hit.Kind.Label()}）";
                detail.Add($"④ 绑定：已配到去向台账「{hit.Name}」——本期投放到它的占容方会驱动这块区域外扩。");
                if (hit.BenchSlopeAngleDeg > 1e-6 && hit.BenchSlopeAngleDeg <= 90)
                {
                    slopeText = $"α={hit.BenchSlopeAngleDeg:0.#}°（去向台账）";
                    detail.Add($"◆ 放坡：坡面角 α={hit.BenchSlopeAngleDeg:0.#}° 取自去向台账 bench_slope_angle_deg。");
                }
                else
                {
                    slopeText = "未录 → 垂直壁";
                    detail.Add("◆ 放坡：该汇未录 bench_slope_angle_deg —— 堆填层体会按**垂直壁**建（不猜一个安息角出来）。");
                }
            }
            else if (dumpCount == 1)
            {
                bind = ZoneBind.Fallback;
                bindText = "兜底：吃下全部排弃";
                detail.Add("④ 绑定：名字没对上任何去向，但排土类区域全场只有这一块 —— "
                         + "推演让它吃下全部排弃占容（总量不丢）。再画第二块排土区，这条兜底立刻失效。");
                slopeText = "未配汇 → 垂直壁";
            }
            else
            {
                bind = ZoneBind.None;
                bindText = "对不上去向";
                detail.Add($"④ 绑定：名字「{z.Name}」对不上去向台账里任何一个排弃类去向，且排土类区域有 {dumpCount} 块 —— "
                         + "推演**不猜**，这一块整期不动，画面上显示「本期无投放」。"
                         + "改成去向台账里的名字（右侧下拉可选）即可接上。");
                slopeText = "未配汇 → 垂直壁";
            }
        }
        else
        {
            // ★ 位置优先（B1/B2），名字只是最后一条退路（B3）。
            //   原来这里是纯字符串「互相包含」判据 —— 一个坐标都不用，
            //   于是谁改了个名字这块地就整期不动，而画面上只显示「本期无采掘」，不报错。
            var bindRes = ZoneFaceBinding.Match(z.Ring, z.Name, faceObjs);
            var hits = bindRes.Hits.Select(h => h.Face.Zone)
                              .Where(x => !string.IsNullOrWhiteSpace(x))
                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (hits.Count > 0)
            {
                bind = ZoneBind.Direct;
                bindText = string.Join("、", hits);
                // B4：走了哪条路必须说出来 —— 「按位置配上的」与「按名字配上的」
                //     在界面上长得一样，而后者随时会因为改名而断。
                detail.Add($"④ 绑定：按{bindRes.Hits[0].RouteZh}对上 {hits.Count} 个作业面（{bindText}）"
                         + " —— 这些面的当日挖除实方会驱动这块区域内缩。");
                foreach (var n in bindRes.Notes.Where(x => x.StartsWith("◆", StringComparison.Ordinal)))
                    detail.Add("　" + n);
            }
            else if (pitCount == 1)
            {
                bind = ZoneBind.Fallback;
                bindText = "兜底：吃下全部挖除";
                detail.Add("④ 绑定：名字没对上任何作业面，但采场类区域全场只有这一块 —— "
                         + "推演让它吃下全部采场挖除量（总量不丢）。再画第二块采场区，这条兜底立刻失效。");
            }
            else
            {
                bind = ZoneBind.None;
                bindText = "对不上作业面";
                detail.Add($"④ 绑定：**位置与名字都对不上**任何作业面，且采场类区域有 {pitCount} 块 —— "
                         + "推演**不猜**，这一块整期不动，画面上显示「本期无采掘」。"
                         + $"位置这条路是先看有没有面的铲位落在这块地里、再看有没有落在 "
                         + $"{ZoneFaceBinding.DefaultToleranceM:0} m 容差内；两条都不成立才退到名字。"
                         + "所以**改名不一定救得了** —— 更该看的是这些面本期到底在不在这块地上作业。");
                foreach (var n in bindRes.Notes) detail.Add("　" + n);
            }
        }

        bool healthy = z.Active && ringOk && bind != ZoneBind.None;
        string verdict = !z.Active ? "未选定"
                       : !ringOk ? "几何不成立"
                       : bind == ZoneBind.Direct ? "会进推演"
                       : bind == ZoneBind.Fallback ? "兜底进推演"
                       : "本期不动";

        return new ZoneLink
        {
            Name = z.Name,
            Category = z.Category,
            IsDump = z.IsDump,
            Active = z.Active,
            Polarity = z.IsDump ? "外扩" : "内缩",
            Bind = bind,
            BindText = bindText,
            RingOk = ringOk,
            ZText = zText,
            SlopeText = slopeText,
            Verdict = verdict,
            Healthy = healthy,
            Detail = detail,
        };
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
