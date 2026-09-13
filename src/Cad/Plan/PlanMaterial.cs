using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  月计划的「物料维」本体 —— 全模块唯一的密度 / 膨胀系数来源。
//
//  露天矿的方量有三个状态，混用口径会让「排得下吗」在数学上无解：
//    原位实方 V实 ──×Ks(松散系数)──▶ 运输松方 V松       ：卡车载重/配车按它算
//                  ──×Kr(残余膨胀)──▶ 排弃占容 V容       ：排土场剩余库容按它扣
//  · V实：块体模型/几何量算口径 —— 储量、品位、剥采比全按它算。
//  · V松：爆破铲装后的体积 —— 车厢容积/配车数按它算。
//  · V容：排弃并自然沉降稳定后占用的库容 —— Kr = Ks×(1−沉降率)，岩石排土场沉降率约 15%~25%，
//         所以 Kr 远小于 Ks；拿 Ks 扣库容会把排土场算小 30%，是最常见的口径事故。
//  吨量是三个口径之间唯一守恒的中间量，跨口径换算一律经吨量落地。
//
//  物料还决定「能去哪」：表土是复垦资源必须单独堆存，煤不能进排土场 —— 这是合规硬约束，
//  在 UI 上就应当过滤掉不合规的去向，而不是等排产完再报错。
//
//  本文件不引 TaskLib（模块加载顺序 PlanLib(50) 在 TaskLib(70) 之前，方向只能是 TaskLib→PlanLib），
//  但物料码与常数与 TaskLib.Domain.MaterialCatalog 逐项对齐，两侧反射对接时口径一致。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>去向设施类型。决定运距特征（内排多为下排、外排上排）与容量口径。</summary>
public enum PlanSinkKind
{
    ExternalDump, // 外排土场（占地）
    InternalDump, // 内排土场（回填采空区）——运距短、多为下排，降本核心
    Crusher,      // 破碎站（半固定/移动）
    Silo,         // 原煤仓
    Stockpile,    // 储煤场/临时堆场（配矿缓冲、低质煤暂存）
    TopsoilYard,  // 表土堆场（复垦专用）
}

/// <summary>去向类型的显示文案 / 排弃判定 / 等效运距折算。</summary>
public static class PlanSinkKinds
{
    public static string Label(this PlanSinkKind k) => k switch
    {
        PlanSinkKind.ExternalDump => "外排土场",
        PlanSinkKind.InternalDump => "内排土场",
        PlanSinkKind.Crusher => "破碎站",
        PlanSinkKind.Silo => "原煤仓",
        PlanSinkKind.Stockpile => "储煤场",
        PlanSinkKind.TopsoilYard => "表土堆场",
        _ => "—",
    };

    /// <summary>是否为排弃类去向（占排土库容、按 Kr 扣容）。破碎站/煤仓是通过型，不占库容。</summary>
    public static bool IsDumping(this PlanSinkKind k)
        => k is PlanSinkKind.ExternalDump or PlanSinkKind.InternalDump or PlanSinkKind.TopsoilYard;

    /// <summary>
    /// 等效运距折算系数（工程经验值）：等效运距 = 实际运距 × 本系数。
    /// 内排多为下排（重车下坡、油耗与循环时间低）故 &lt;1；外排上排故 &gt;1；通过型去向取 1。
    /// 面上显式填了 EquivHaulKm 时以面上为准，本系数只在没填时兜底。
    /// </summary>
    public static double EquivFactor(this PlanSinkKind k) => k switch
    {
        PlanSinkKind.InternalDump => 0.85,
        PlanSinkKind.ExternalDump => 1.20,
        PlanSinkKind.TopsoilYard => 1.10,
        _ => 1.00,
    };

    /// <summary>中文/英文文本 → 去向类型（读台账 dump_type / unload_sub 用，认不出按外排）。</summary>
    public static PlanSinkKind FromText(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return PlanSinkKind.ExternalDump;
        if (t.Contains("内排", StringComparison.Ordinal) || t.Contains("internal", StringComparison.OrdinalIgnoreCase)) return PlanSinkKind.InternalDump;
        if (t.Contains("表土", StringComparison.Ordinal) || t.Contains("topsoil", StringComparison.OrdinalIgnoreCase)) return PlanSinkKind.TopsoilYard;
        if (t.Contains("破碎", StringComparison.Ordinal) || t.Contains("crusher", StringComparison.OrdinalIgnoreCase)) return PlanSinkKind.Crusher;
        if (t.Contains("仓", StringComparison.Ordinal) || t.Contains("silo", StringComparison.OrdinalIgnoreCase)) return PlanSinkKind.Silo;
        if (t.Contains("堆场", StringComparison.Ordinal) || t.Contains("储煤", StringComparison.Ordinal)
            || t.Contains("stock", StringComparison.OrdinalIgnoreCase)) return PlanSinkKind.Stockpile;
        return PlanSinkKind.ExternalDump;
    }
}

/// <summary>
/// 一种物料的完整规格。密度与两个膨胀系数是全部体积/吨量换算的唯一来源 —— 严禁在别处再写死密度。
/// </summary>
public sealed class PlanMaterialSpec
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>原位（实方）密度 t/m³。</summary>
    public double InSituDensityTPerM3 { get; init; } = 2.50;

    /// <summary>松散系数 Ks = V松/V实（运输口径）。</summary>
    public double SwellFactor { get; init; } = 1.50;

    /// <summary>残余膨胀系数 Kr = V容/V实（沉降稳定后的排弃占容口径）。</summary>
    public double ResidualSwellFactor { get; init; } = 1.15;

    /// <summary>是否需先穿孔爆破才能铲装（硬岩/夹矸要，表土不要）。</summary>
    public bool NeedsBlasting { get; init; }

    /// <summary>是否计入采出量（煤/低质煤）。false 者计入剥离量。</summary>
    public bool IsOre { get; init; }

    /// <summary>允许进入的去向类型（合规硬约束：表土只进表土堆场，煤不进排土场）。</summary>
    public IReadOnlyCollection<PlanSinkKind> AllowedSinks { get; init; } = Array.Empty<PlanSinkKind>();

    // ── 换算（对"万"单位同样成立：万m³ × t/m³ = 万t）──
    /// <summary>实方 → 吨量（万m³ → 万t）。</summary>
    public double ToTonnage(double inSitu) => inSitu * InSituDensityTPerM3;
    /// <summary>吨量 → 实方（万t → 万m³）。</summary>
    public double FromTonnage(double tonnage) => InSituDensityTPerM3 <= 1e-9 ? 0 : tonnage / InSituDensityTPerM3;
    /// <summary>实方 → 运输松方（配车口径）。</summary>
    public double ToLoose(double inSitu) => inSitu * SwellFactor;
    /// <summary>实方 → 排弃占容方（扣库容口径）。</summary>
    public double ToDump(double inSitu) => inSitu * ResidualSwellFactor;

    /// <summary>该物料能否进这类去向。</summary>
    public bool Accepts(PlanSinkKind kind) => AllowedSinks.Count == 0 || AllowedSinks.Contains(kind);

    public string Caption => $"{Name}（ρ{InSituDensityTPerM3:0.##} · Ks{SwellFactor:0.00} · Kr{ResidualSwellFactor:0.00}）";
}

/// <summary>
/// 物料参数表。默认值取露天煤矿常用工程经验区间的中值，与 TaskLib.Domain.MaterialCatalog 逐项同值。
/// 全模块（排产 / 采排配对 / 库容校核 / 运输功）一律经 <see cref="Resolve"/> 取参数。
/// </summary>
public static class PlanMaterialCatalog
{
    public const string Topsoil = "topsoil";
    public const string Weathered = "weathered";
    public const string Rock = "rock";
    public const string Interburden = "interburden";
    public const string Coal = "coal";
    public const string LowGrade = "lowgrade";

    private static readonly Dictionary<string, PlanMaterialSpec> _specs = new(StringComparer.OrdinalIgnoreCase)
    {
        [Topsoil] = new PlanMaterialSpec
        {
            Code = Topsoil, Name = "表土", InSituDensityTPerM3 = 1.60,
            SwellFactor = 1.20, ResidualSwellFactor = 1.08, NeedsBlasting = false, IsOre = false,
            AllowedSinks = new[] { PlanSinkKind.TopsoilYard },          // 复垦资源，不得混入岩石排土场
        },
        [Weathered] = new PlanMaterialSpec
        {
            Code = Weathered, Name = "风化岩", InSituDensityTPerM3 = 2.10,
            SwellFactor = 1.35, ResidualSwellFactor = 1.12, NeedsBlasting = false, IsOre = false,
            AllowedSinks = new[] { PlanSinkKind.ExternalDump, PlanSinkKind.InternalDump },
        },
        [Rock] = new PlanMaterialSpec
        {
            Code = Rock, Name = "硬岩", InSituDensityTPerM3 = 2.50,
            SwellFactor = 1.50, ResidualSwellFactor = 1.15, NeedsBlasting = true, IsOre = false,
            AllowedSinks = new[] { PlanSinkKind.ExternalDump, PlanSinkKind.InternalDump },
        },
        [Interburden] = new PlanMaterialSpec
        {
            Code = Interburden, Name = "夹矸", InSituDensityTPerM3 = 2.20,
            SwellFactor = 1.40, ResidualSwellFactor = 1.13, NeedsBlasting = true, IsOre = false,
            AllowedSinks = new[] { PlanSinkKind.ExternalDump, PlanSinkKind.InternalDump },
        },
        [Coal] = new PlanMaterialSpec
        {
            // 煤密度沿用 MiningProgramPlan.DefaultCoalDensity(1.35)：境界/中长远/短期三层同一个煤密度，
            // 且 TaskLib 反射读 ShortTermPlan.DefaultCoalDensity 时能对上，不会"同一批煤两个吨数"。
            Code = Coal, Name = "煤", InSituDensityTPerM3 = MiningProgramPlan.DefaultCoalDensity,
            SwellFactor = 1.35, ResidualSwellFactor = 1.10, NeedsBlasting = false, IsOre = true,
            AllowedSinks = new[] { PlanSinkKind.Crusher, PlanSinkKind.Silo, PlanSinkKind.Stockpile },
        },
        [LowGrade] = new PlanMaterialSpec
        {
            Code = LowGrade, Name = "低质煤", InSituDensityTPerM3 = 1.40,
            SwellFactor = 1.35, ResidualSwellFactor = 1.10, NeedsBlasting = false, IsOre = true,
            AllowedSinks = new[] { PlanSinkKind.Stockpile, PlanSinkKind.Silo },
        },
    };

    /// <summary>全部物料（下拉框数据源，顺序 = 剥离物料在前、煤在后，符合自上而下的剥采顺序）。</summary>
    public static IReadOnlyList<PlanMaterialSpec> All { get; } = new[]
    {
        _specs[Topsoil], _specs[Weathered], _specs[Rock], _specs[Interburden], _specs[Coal], _specs[LowGrade],
    };

    /// <summary>取物料规格。未知码回落硬岩（保守：按剥离处理，不会把岩当煤计入采出量）。</summary>
    public static PlanMaterialSpec Resolve(string? code)
        => code != null && _specs.TryGetValue(code, out var s) ? s : _specs[Rock];

    public static bool Exists(string? code) => code != null && _specs.ContainsKey(code);

    /// <summary>物料名（下拉/表格显示用）。</summary>
    public static string NameOf(string? code) => Resolve(code).Name;

    /// <summary>
    /// 默认剥离岩性构成 —— 作业面没给混采构成时，月剥离量按此拆到各岩性。
    /// 取一般露天煤矿的经验比例（表土薄、风化带次之、硬岩为主）；面上填了 MaterialMixText 即被覆盖。
    /// 它的工程意义在于：表土必须单独进表土堆场，不拆开就看不出表土堆场会不会先排满。
    /// </summary>
    public static readonly (string Code, double Fraction)[] DefaultWasteMix =
    {
        (Topsoil, 0.05), (Weathered, 0.25), (Rock, 0.70),
    };

    /// <summary>中文/别名 → 物料码（解析历史自由文本与台账 material 列：c4/c9=煤层号、rh=岩）。</summary>
    public static string CodeFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string t = text.Trim();
        if (_specs.ContainsKey(t)) return t;

        if (t.Contains("表土", StringComparison.Ordinal) || t.Contains("腐殖", StringComparison.Ordinal) || t.Contains("黄土", StringComparison.Ordinal)) return Topsoil;
        if (t.Contains("风化", StringComparison.Ordinal) || t.Contains("软岩", StringComparison.Ordinal)) return Weathered;
        if (t.Contains("夹矸", StringComparison.Ordinal) || t.Contains("矸", StringComparison.Ordinal)) return Interburden;
        if (t.Contains("低质", StringComparison.Ordinal) || t.Contains("低品位", StringComparison.Ordinal) || t.Contains("劣质", StringComparison.Ordinal)) return LowGrade;
        if (t.Contains("煤", StringComparison.Ordinal) || t.Contains("矿", StringComparison.Ordinal)) return Coal;
        if (t.Contains("岩", StringComparison.Ordinal) || t.Contains("石", StringComparison.Ordinal)) return Rock;

        if (t.StartsWith("c", StringComparison.OrdinalIgnoreCase) && t.Length <= 3) return Coal;
        if (t.Equals("rh", StringComparison.OrdinalIgnoreCase)) return Rock;
        return "";
    }
}

/// <summary>
/// 一个作业面的物料构成（"煤7∶岩3"）。露天矿一个采装面常同时出煤和岩，
/// 必须按份额拆开才能分别定去向、分别算吨量与占容 —— 压成单一物料会让剥离量凭空消失。
/// </summary>
public sealed class PlanMaterialMix
{
    public List<(string Code, double Fraction)> Shares { get; } = new();

    public bool IsEmpty => Shares.Count == 0;

    /// <summary>矿(煤)占比 —— 采出量与剥离量的拆分依据。</summary>
    public double OreFraction => Shares.Sum(s => PlanMaterialCatalog.Resolve(s.Code).IsOre ? s.Fraction : 0);

    /// <summary>剥离侧各岩性的**归一后**份额（空则由调用方回落到 DefaultWasteMix）。</summary>
    public List<(string Code, double Fraction)> WasteShares()
    {
        var waste = Shares.Where(s => !PlanMaterialCatalog.Resolve(s.Code).IsOre).ToList();
        double sum = waste.Sum(s => s.Fraction);
        return sum <= 1e-9 ? new List<(string, double)>()
                           : waste.Select(s => (s.Code, s.Fraction / sum)).ToList();
    }

    /// <summary>采出侧各煤种的归一后份额（空则由调用方回落到单一煤）。</summary>
    public List<(string Code, double Fraction)> OreShares()
    {
        var ore = Shares.Where(s => PlanMaterialCatalog.Resolve(s.Code).IsOre).ToList();
        double sum = ore.Sum(s => s.Fraction);
        return sum <= 1e-9 ? new List<(string, double)>()
                           : ore.Select(s => (s.Code, s.Fraction / sum)).ToList();
    }

    public string Caption => Shares.Count switch
    {
        0 => "",
        1 => PlanMaterialCatalog.NameOf(Shares[0].Code),
        _ => string.Join("∶", Shares.OrderByDescending(s => s.Fraction)
                                    .Select(s => $"{PlanMaterialCatalog.NameOf(s.Code)}{s.Fraction * 10:0.#}")),
    };

    /// <summary>解析自由文本："煤7∶岩3" / "煤6:岩4" / "表土"。解析不出返回空构成（调用方回落）。</summary>
    public static PlanMaterialMix Parse(string? text)
    {
        var mix = new PlanMaterialMix();
        if (string.IsNullOrWhiteSpace(text)) return mix;

        var parts = text.Split(new[] { '∶', ':', '：', '/', '+', '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;

            int digitAt = -1;
            for (int i = 0; i < p.Length; i++)
                if (char.IsDigit(p[i]) || p[i] == '.') { digitAt = i; break; }

            string nameText = digitAt < 0 ? p : p[..digitAt].Trim();
            double weight = 1;
            if (digitAt >= 0 && double.TryParse(p[digitAt..].Trim(), out double w) && w > 0) weight = w;

            string code = PlanMaterialCatalog.CodeFromText(nameText);
            if (code.Length == 0) continue;
            mix.Shares.Add((code, weight));
        }

        double sum = mix.Shares.Sum(s => s.Fraction);
        if (sum > 1e-9)
            for (int i = 0; i < mix.Shares.Count; i++)
                mix.Shares[i] = (mix.Shares[i].Code, mix.Shares[i].Fraction / sum);
        return mix;
    }
}
