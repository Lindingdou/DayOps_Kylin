// 忠实移植自原 PitMine3D Modules/RoadLib/Routing/HaulCaliper.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 「共享运输约束」<c>transport.constraints</c> 的**本地只读镜像**。
///
/// 为什么是镜像而不是直接引用 <c>MineAssLib.Models.TransportConstraintSettings</c>：
/// RoadLib 不引用 MineAssLib（引了会把 BlockModelLib 整条依赖链拖进来），两模块只经 <see cref="IUserSettings"/>
/// 的 JSON 通信。**属性名即 JSON key，必须与源类逐字一致** —— 改一个字母，那一项就静默变成默认值，
/// 表现是"约束窗里明明填了 6%，寻径还是按 8% 走"，而且不报任何错。
///
/// 只镜像 RoadLib 用得上的那几项；源类里其余字段（线形/竖曲线/平盘宽…）与本模块无关，不抄。
/// 唯一编辑入口仍是 MineAssLib「约束条件设置」窗，本模块**只读不写**。
/// </summary>
public sealed class TransportConstraintMirror
{
    /// <summary>卡车型号档（展示用）。</summary>
    public string TruckClass { get; set; } = "";
    /// <summary>额定载重 t/车。</summary>
    public double TruckPayload { get; set; }
    /// <summary>额定爬坡度 %（限制坡度的物理上限）。</summary>
    public double TruckClimbPct { get; set; }
    /// <summary>限制坡度 i_max %。</summary>
    public double MaxGradePct { get; set; }
    /// <summary>设计车速 km/h。</summary>
    public double DesignSpeedKmh { get; set; }
    /// <summary>运输单价 元/(t·km)。源类默认 0 = 用户没填。</summary>
    public double HaulUnitCost { get; set; }
}

/// <summary>
/// 一次寻径 / 运距计算的**口径**：择路权重 + 限坡 + 车型 + 单价 + 装卸时间 + 吸附半径。
///
/// 存在的理由：这三件事以前各写各的 —— 点对点按里程、等效运距按成本、报表按里程，限坡三处都没给值，
/// 车型三处都是 <see cref="TruckProfile.Default"/> 的硬编码 90t，同一对源汇能给出三个不同的答案。
/// 现在全部从这一个对象取，并且**把口径原文打在每张结果的表头上** —— 数字是怎么来的，读数之前就能看见。
///
/// 口径分两层：
///   · 来自 ③ <c>transport.constraints</c> 的（载重/限坡/车速/单价）—— 用户在「约束条件设置」里改；
///   · RoadLib 自己的缺省（坡度折算系数、装卸时间、吸附半径）—— ③ 里没有这些字段，摘要里如实标注来源。
/// </summary>
public sealed class HaulCaliper
{
    /// <summary>共享运输约束的持久化键（与 MineAssLib 侧的 <c>TransportConstraintProfiles.MirrorKey</c> 同值）。</summary>
    public const string SettingsKey = "transport.constraints";

    /// <summary>空车平路车速 / 重车平路车速。③ 只给一个 <c>DesignSpeedKmh</c>，空车速按此比例派生。</summary>
    private const double EmptySpeedRatio = 1.6;

    /// <summary>择路口径（按什么找最短路）。默认时间最短——卡车实际按最快路走，不按最短路走。</summary>
    public WeightMode Mode { get; init; } = WeightMode.Time;

    /// <summary>限坡 %（0=不限）。取自 ③ 的 <c>MaxGradePct</c>。</summary>
    public double MaxGradePct { get; init; }

    /// <summary>车型与运输参数（喂给求解器）。</summary>
    public TruckProfile Truck { get; init; } = TruckProfile.Default;

    /// <summary>车型档名（展示）。</summary>
    public string TruckClass { get; init; } = "（未设）";

    /// <summary>运输单价 元/(t·km)；<c>null</c> = ③ 里没填，成本一律显示「—」而**不是 0**。</summary>
    public double? UnitHaulCostPerTonKm { get; init; }

    /// <summary>装车（含就位）时间 min。</summary>
    public double SpotLoadMin { get; init; } = 3.0;

    /// <summary>卸车 + 调车时间 min。</summary>
    public double ManeuverDumpMin { get; init; } = 1.5;

    /// <summary>视口取点吸附到路网节点的上限 m。超出即不吸附并报实距，绝不静默吸到 500m 外的节点。</summary>
    public double SnapRadiusM { get; init; } = 50.0;

    /// <summary>true = 真读到了 ③；false = 没读到，全套走 RoadLib 缺省（摘要里会写明）。</summary>
    public bool FromSharedSettings { get; init; }

    /// <summary>口径旁注（派生/缺省/越界警告），逐条显示在结果窗口径行下方。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>算法版本标识：改了会改数值的那几处口径都归在它下面，进 <see cref="Hash"/>。</summary>
    public const string AlgoVersion = "haul/v2-seg";

    // ── 构造 ──

    /// <summary>从共享约束读一份口径；<paramref name="settings"/> 为 null 或键不存在时全走缺省并标注。</summary>
    public static HaulCaliper FromUserSettings(UserSettings? settings, WeightMode mode = WeightMode.Time)
        => From(settings?.Get<TransportConstraintMirror>(SettingsKey), mode);

    /// <summary>由镜像对象建口径（纯函数，可单测）。<paramref name="c"/> 为 null = 未设共享约束。</summary>
    public static HaulCaliper From(TransportConstraintMirror? c, WeightMode mode = WeightMode.Time)
    {
        var notes = new List<string>();
        if (c is null)
        {
            notes.Add("未读到共享运输约束（transport.constraints），全部按 RoadLib 缺省；"
                      + "请到「采剥工程设计 → 运输系统 → 约束条件设置」填一次。");
            return new HaulCaliper
            {
                Mode = mode,
                MaxGradePct = 0.0,      // 没有约束就不假装有限坡：0 = 不限，且摘要里写明
                Truck = TruckProfile.Default,
                TruckClass = "（未设）",
                UnitHaulCostPerTonKm = null,
                FromSharedSettings = false,
                Notes = notes,
            };
        }

        double payload = c.TruckPayload > 0 ? c.TruckPayload : TruckProfile.Default.PayloadT;
        if (c.TruckPayload <= 0) notes.Add($"③ 未填额定载重，按缺省 {payload:F0} t/车。");

        double vLoaded = c.DesignSpeedKmh > 0 ? c.DesignSpeedKmh : TruckProfile.Default.FlatSpeedLoadedKph;
        double vEmpty = vLoaded * EmptySpeedRatio;
        notes.Add($"空车平路车速 {vEmpty:F0} km/h = 设计车速 {vLoaded:F0} × {EmptySpeedRatio:F1}（③ 只给一个设计车速，空车速为派生值）。");

        double maxGrade = c.MaxGradePct;
        if (maxGrade > 0 && c.TruckClimbPct > 0 && maxGrade > c.TruckClimbPct + 1e-9)
            notes.Add($"⚠ ③ 的限坡 {maxGrade:F1}% 高于车辆额定爬坡度 {c.TruckClimbPct:F1}%，路能选出来但车爬不动，建议下调。");
        if (maxGrade <= 0)
            notes.Add("⚠ ③ 的限坡为 0 = 不限坡，再陡的边也会被选进路径。");

        double? unit = c.HaulUnitCost > 0 ? c.HaulUnitCost : null;
        if (unit is null) notes.Add("③ 未填运输单价（元/t·km），成本列显示「—」，不按 0 报 0 元/趟。");

        notes.Add($"上坡/下坡等效折算系数 {TruckProfile.Default.UphillEquivK:F1}/{TruckProfile.Default.DownhillEquivK:F1}"
                  + "、装车 3.0 min、卸车调车 1.5 min 为 RoadLib 缺省（③ 暂无这些字段）。");

        return new HaulCaliper
        {
            Mode = mode,
            MaxGradePct = maxGrade,
            Truck = new TruckProfile
            {
                PayloadT = payload,
                FlatSpeedLoadedKph = vLoaded,
                FlatSpeedEmptyKph = vEmpty,
                UnitHaulCostPerTonKm = unit ?? 0.0,   // 0 → PathResult.Cost 出 0，由 UnitHaulCostPerTonKm==null 兜住不显示
            },
            TruckClass = string.IsNullOrWhiteSpace(c.TruckClass) ? "（未设）" : c.TruckClass,
            UnitHaulCostPerTonKm = unit,
            FromSharedSettings = true,
            Notes = notes,
        };
    }

    /// <summary>换一个择路口径（其余不变）。</summary>
    public HaulCaliper WithMode(WeightMode mode) => new()
    {
        Mode = mode,
        MaxGradePct = MaxGradePct,
        Truck = Truck,
        TruckClass = TruckClass,
        UnitHaulCostPerTonKm = UnitHaulCostPerTonKm,
        SpotLoadMin = SpotLoadMin,
        ManeuverDumpMin = ManeuverDumpMin,
        SnapRadiusM = SnapRadiusM,
        FromSharedSettings = FromSharedSettings,
        Notes = Notes,
    };

    // ── 展示 / 复现 ──

    /// <summary>择路口径中文名。</summary>
    public string ModeText => Mode switch
    {
        WeightMode.Distance => "里程最短",
        WeightMode.Time => "时间最短",
        WeightMode.Cost => "成本最省",
        _ => "油耗最省",
    };

    /// <summary>
    /// 短摘要（折叠态表头用）。<see cref="SummaryLine"/> 完整版有 100+ 字，塞进 Expander 头会被窗宽截断 ——
    /// 截断的口径行比没有更糟：看着像有交代，实际读不到关键项。
    /// </summary>
    public string ShortLine()
    {
        string grade = MaxGradePct > 0 ? $"限坡 {MaxGradePct:F1}%" : "不限坡";
        return $"{ModeText} · {grade} · {TruckClass}/{Truck.PayloadT:F0}t · "
             + $"{(UnitHaulCostPerTonKm is { } u ? $"{u:F2} 元/t·km" : "未设单价")} · hash {Hash()}";
    }

    /// <summary>一行口径摘要，打在每张结果的表头。</summary>
    public string SummaryLine()
    {
        string grade = MaxGradePct > 0 ? $"{MaxGradePct:F1}%" : "不限";
        string cost = UnitHaulCostPerTonKm is { } u ? $"{u:F2} 元/t·km" : "未设单价";
        return $"口径：择路={ModeText} · 限坡={grade} · 车型={TruckClass}/{Truck.PayloadT:F0}t · "
             + $"车速 重{Truck.FlatSpeedLoadedKph:F0}/空{Truck.FlatSpeedEmptyKph:F0} km/h · {cost} · "
             + $"坡采样 {RoadEdge.GradeSampleStepM:F0}m · 算法 {AlgoVersion} · "
             + $"来源={(FromSharedSettings ? "共享运输约束" : "RoadLib 缺省")} · hash {Hash()}";
    }

    /// <summary>口径摘要 + 逐条旁注（多行，供结果窗表头区）。</summary>
    public string SummaryBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryLine());
        foreach (var n in Notes) sb.AppendLine("  · " + n);
        return sb.ToString();
    }

    /// <summary>
    /// 口径指纹（前 8 位十六进制）：**按值**算，字段名排序 + double 定点 6 位。
    /// 同口径连算两次必得同一串；换了任何一项立刻变。用来回答"这两个数字是不是一个口径下算出来的"。
    /// 注意不含 Loaded（那是腿的属性）、不含源汇。
    /// </summary>
    public string Hash()
    {
        var parts = new List<string>
        {
            $"algo={AlgoVersion}",
            $"downhillK={TruckProfile.Default.DownhillEquivK:F6}",
            $"gradeStep={RoadEdge.GradeSampleStepM:F6}",
            $"maneuverDump={ManeuverDumpMin:F6}",
            $"maxGrade={MaxGradePct:F6}",
            $"mode={Mode}",
            $"payload={Truck.PayloadT:F6}",
            $"spotLoad={SpotLoadMin:F6}",
            $"uphillK={Truck.UphillEquivK:F6}",
            $"unitCost={(UnitHaulCostPerTonKm ?? 0.0):F6}",
            $"vEmpty={Truck.FlatSpeedEmptyKph:F6}",
            $"vLoaded={Truck.FlatSpeedLoadedKph:F6}",
        };
        parts.Sort(StringComparer.Ordinal);
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
    }

    /// <summary>建一次寻径查询（去程/回程只差 <paramref name="loaded"/>）。</summary>
    public PathQuery Query(bool loaded) => new()
    {
        Mode = Mode,
        Truck = Truck,
        Loaded = loaded,
        MaxGradePct = MaxGradePct,
    };
}
