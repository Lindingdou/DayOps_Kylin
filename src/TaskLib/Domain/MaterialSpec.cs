// 忠实移植自原 PitMine3D Modules/TaskLib/Domain/MaterialSpec.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Domain;

// ─────────────────────────────────────────────────────────────────────────────
//  物料本体 —— 「采剥什么」的权威定义。
//
//  露天矿的方量在三个状态间流转，混用口径会让「排得下吗」在数学上无解：
//    原位实方 V实 ──×Ks(松散系数)──▶ 运输松方 V松 ──▶ 排弃占容 V容 = V实×Kr(残余膨胀)
//  · V实：块体模型/几何量算口径，储量、品位、剥采比全按它算。
//  · V松：爆破铲装后的体积，卡车载重/车厢容积校核、配车数按它算。
//  · V容：排弃并自然沉降稳定后占用的库容，排土场剩余库容按它扣。
//    Kr = Ks × (1 − 沉降率)，岩石排土场沉降率约 15%~25%，故 Kr 远小于 Ks。
//
//  物料还决定两件事：需不需要先爆破（硬岩要，表土不要），以及允许进哪些去向
//  （表土必须单独堆存供复垦，不得混入岩石排土场；煤不能进排土场）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>物料大类。决定爆破需求、允许去向、是否计入采出量。</summary>
public enum MaterialKind
{
    Topsoil,      // 表土/腐殖土——复垦资源，必须单独堆存
    Weathered,    // 风化岩/软岩——可直接铲装
    Rock,         // 硬岩——需爆破
    Interburden,  // 夹矸——煤层间岩，需爆破
    Coal,         // 煤/矿石——计入采出量
    LowGrade,     // 低质煤/低品位——进临时堆场待配矿
}

/// <summary>去向设施类型。决定运距特征（内排下坡、外排上坡）与容量口径。</summary>
public enum SinkKind
{
    ExternalDump, // 外排土场
    InternalDump, // 内排土场（采空区回填）——运距短、多为下排，降本核心
    Crusher,      // 破碎站（半固定/移动）
    Silo,         // 原煤仓
    Stockpile,    // 储煤场/临时堆场（配矿缓冲、低品位暂存）
    TopsoilYard,  // 表土堆场（复垦专用）
}

public static class MaterialEnumLabels
{
    public static string Label(this MaterialKind k) => k switch
    {
        MaterialKind.Topsoil => "表土",
        MaterialKind.Weathered => "风化岩",
        MaterialKind.Rock => "硬岩",
        MaterialKind.Interburden => "夹矸",
        MaterialKind.Coal => "煤",
        MaterialKind.LowGrade => "低质煤",
        _ => "",
    };

    public static string Label(this SinkKind k) => k switch
    {
        SinkKind.ExternalDump => "外排土场",
        SinkKind.InternalDump => "内排土场",
        SinkKind.Crusher => "破碎站",
        SinkKind.Silo => "原煤仓",
        SinkKind.Stockpile => "储煤场",
        SinkKind.TopsoilYard => "表土堆场",
        _ => "",
    };

    /// <summary>是否为排弃类去向（占排土库容、按 Kr 扣容）。</summary>
    public static bool IsDumping(this SinkKind k)
        => k is SinkKind.ExternalDump or SinkKind.InternalDump or SinkKind.TopsoilYard;
}

/// <summary>
/// 一种物料的完整规格。密度与两个膨胀系数是全部体积/吨量换算的唯一来源——
/// 严禁在别处再写死密度常量。
/// </summary>
public sealed class MaterialSpec
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public MaterialKind Kind { get; set; }

    /// <summary>原位（实方）密度 t/m³。</summary>
    public double InSituDensityTPerM3 { get; set; } = 2.4;

    /// <summary>松散系数 Ks = V松 / V实。岩 1.45~1.55，煤 1.30~1.40，表土 1.15~1.25。</summary>
    public double SwellFactor { get; set; } = 1.5;

    /// <summary>残余膨胀系数 Kr = V排弃占容 / V实（沉降稳定后）。岩 1.10~1.20，土 1.05~1.15。</summary>
    public double ResidualSwellFactor { get; set; } = 1.15;

    /// <summary>是否需先穿孔爆破才能铲装。</summary>
    public bool NeedsBlasting { get; set; }

    /// <summary>允许进入的去向类型。空集=不限（不建议）。</summary>
    public HashSet<SinkKind> AllowedSinks { get; set; } = new();

    /// <summary>是否计入采出量（煤/矿）。false 者计入剥离量。</summary>
    public bool IsOre => Kind is MaterialKind.Coal or MaterialKind.LowGrade;

    public double ToLooseM3(double inSituM3) => inSituM3 * SwellFactor;
    public double ToDumpM3(double inSituM3) => inSituM3 * ResidualSwellFactor;
    public double ToTonnage(double inSituM3) => inSituM3 * InSituDensityTPerM3;
    public double FromTonnage(double t) => InSituDensityTPerM3 <= 1e-6 ? 0 : t / InSituDensityTPerM3;

    public bool Accepts(SinkKind sink) => AllowedSinks.Count == 0 || AllowedSinks.Contains(sink);

    public string Caption => $"{Name}（ρ{InSituDensityTPerM3:0.##} · Ks{SwellFactor:0.00} · Kr{ResidualSwellFactor:0.00}）";
}

/// <summary>
/// 一个作业面产出的物料构成。露天矿一个采装面常同时出煤和岩（"煤7∶岩3"），
/// 必须按份额拆开才能分别定去向、分别算吨量与占容。
/// </summary>
public sealed class MaterialShare
{
    public string MaterialCode { get; set; } = "";
    public double Fraction { get; set; }   // 0..1，按实方体积占比
}

public sealed class MaterialMix
{
    public List<MaterialShare> Shares { get; set; } = new();

    public static MaterialMix Single(string code) => new() { Shares = { new MaterialShare { MaterialCode = code, Fraction = 1 } } };

    public bool IsEmpty => Shares.Count == 0;

    /// <summary>主物料（份额最大者）。</summary>
    public string PrimaryCode => Shares.Count == 0 ? "" : Shares.OrderByDescending(s => s.Fraction).First().MaterialCode;

    public double FractionOf(string code)
        => Shares.Where(s => string.Equals(s.MaterialCode, code, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Fraction);

    /// <summary>矿(煤)占比——采出量与剥离量的拆分依据。</summary>
    public double OreFraction => Shares.Sum(s => MaterialCatalog.Resolve(s.MaterialCode).IsOre ? s.Fraction : 0);

    /// <summary>按实方体积拆到各物料的实方量。</summary>
    public IEnumerable<(MaterialSpec Spec, double InSituM3)> Split(double inSituM3)
        => Shares.Select(s => (MaterialCatalog.Resolve(s.MaterialCode), inSituM3 * s.Fraction));

    /// <summary>混合物按份额加权的原位密度。</summary>
    public double BlendedDensity
        => Shares.Sum(s => MaterialCatalog.Resolve(s.MaterialCode).InSituDensityTPerM3 * s.Fraction);

    public double ToTonnage(double inSituM3) => Split(inSituM3).Sum(x => x.Spec.ToTonnage(x.InSituM3));
    public double ToLooseM3(double inSituM3) => Split(inSituM3).Sum(x => x.Spec.ToLooseM3(x.InSituM3));
    public double ToDumpM3(double inSituM3) => Split(inSituM3).Sum(x => x.Spec.ToDumpM3(x.InSituM3));

    /// <summary>归一化份额（容错：用户填 7/3 而非 0.7/0.3）。</summary>
    public MaterialMix Normalized()
    {
        double sum = Shares.Sum(s => s.Fraction);
        if (sum <= 1e-6) return this;
        return new MaterialMix { Shares = Shares.Select(s => new MaterialShare { MaterialCode = s.MaterialCode, Fraction = s.Fraction / sum }).ToList() };
    }

    public string Caption => Shares.Count switch
    {
        0 => "",
        1 => MaterialCatalog.Resolve(Shares[0].MaterialCode).Name,
        _ => string.Join("∶", Shares.OrderByDescending(s => s.Fraction)
                                    .Select(s => $"{MaterialCatalog.Resolve(s.MaterialCode).Name}{s.Fraction * 10:0.#}")),
    };

    /// <summary>
    /// 解析历史自由文本："煤7∶岩3" / "煤" / "岩" / "表土" / "煤6:岩4"。
    /// 解析不出时回落为硬岩单一物料（保守：按剥离处理）。
    /// </summary>
    public static MaterialMix Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Single(MaterialCatalog.Rock);

        var parts = text.Split(new[] { '∶', ':', '：', '/', '+', '、' }, StringSplitOptions.RemoveEmptyEntries);
        var shares = new List<MaterialShare>();
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

            string code = MaterialCatalog.CodeFromText(nameText);
            if (code.Length == 0) continue;
            shares.Add(new MaterialShare { MaterialCode = code, Fraction = weight });
        }

        if (shares.Count == 0) return Single(MaterialCatalog.Rock);
        return new MaterialMix { Shares = shares }.Normalized();
    }
}

/// <summary>
/// 一种物料在一条任务/作业面上的去向分项。
///
/// 露天矿一个电铲在同一时窗里挖的是混采料（煤7∶岩3），但煤去破碎站、岩去排土场——
/// 是**一条任务、多个去向**，不能拆成两条任务（同一台铲会撞成"设备双占"）。
/// 所以去向必须能按物料分项挂在任务上，单去向字段只保留为主去向（份额最大者），供单据显示与兼容。
/// </summary>
public sealed class MaterialDestination
{
    public string MaterialCode { get; set; } = "";
    public double Fraction { get; set; }              // 该物料占本任务实方量的比例 0..1
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public SinkKind DestinationKind { get; set; } = SinkKind.ExternalDump;
    public double HaulKm { get; set; }
    public double EquivHaulKm { get; set; }

    public MaterialSpec Spec => MaterialCatalog.Resolve(MaterialCode);
    public double EffectiveHaulKm => EquivHaulKm > 1e-6 ? EquivHaulKm : HaulKm;
    public bool HasDestination => !string.IsNullOrWhiteSpace(DestinationId) || !string.IsNullOrWhiteSpace(DestinationName);

    public string Caption => $"{Spec.Name} {Fraction * 100:0.#}% → {(string.IsNullOrWhiteSpace(DestinationName) ? "（未定）" : DestinationName)}";

    public MaterialDestination Clone() => (MaterialDestination)MemberwiseClone();
}

/// <summary>
/// 物料目录。默认值取露天煤矿常用工程经验区间的中值；接入 GeoDataBase 物料台账后
/// 由 <see cref="Override"/> 覆盖，其余代码一律经 <see cref="Resolve"/> 取值。
/// </summary>
public static class MaterialCatalog
{
    public const string Topsoil = "topsoil";
    public const string Weathered = "weathered";
    public const string Rock = "rock";
    public const string Interburden = "interburden";
    public const string Coal = "coal";
    public const string LowGrade = "lowgrade";

    private static readonly Dictionary<string, MaterialSpec> _specs = BuildDefaults();

    private static Dictionary<string, MaterialSpec> BuildDefaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Topsoil] = new MaterialSpec
        {
            Code = Topsoil, Name = "表土", Kind = MaterialKind.Topsoil,
            InSituDensityTPerM3 = 1.60, SwellFactor = 1.20, ResidualSwellFactor = 1.08, NeedsBlasting = false,
            AllowedSinks = new() { SinkKind.TopsoilYard },   // 复垦资源，不得混排
        },
        [Weathered] = new MaterialSpec
        {
            Code = Weathered, Name = "风化岩", Kind = MaterialKind.Weathered,
            InSituDensityTPerM3 = 2.10, SwellFactor = 1.35, ResidualSwellFactor = 1.12, NeedsBlasting = false,
            AllowedSinks = new() { SinkKind.ExternalDump, SinkKind.InternalDump },
        },
        [Rock] = new MaterialSpec
        {
            Code = Rock, Name = "硬岩", Kind = MaterialKind.Rock,
            InSituDensityTPerM3 = 2.50, SwellFactor = 1.50, ResidualSwellFactor = 1.15, NeedsBlasting = true,
            AllowedSinks = new() { SinkKind.ExternalDump, SinkKind.InternalDump },
        },
        [Interburden] = new MaterialSpec
        {
            Code = Interburden, Name = "夹矸", Kind = MaterialKind.Interburden,
            InSituDensityTPerM3 = 2.20, SwellFactor = 1.40, ResidualSwellFactor = 1.13, NeedsBlasting = true,
            AllowedSinks = new() { SinkKind.ExternalDump, SinkKind.InternalDump },
        },
        [Coal] = new MaterialSpec
        {
            Code = Coal, Name = "煤", Kind = MaterialKind.Coal,
            InSituDensityTPerM3 = 1.35, SwellFactor = 1.35, ResidualSwellFactor = 1.10, NeedsBlasting = false,
            AllowedSinks = new() { SinkKind.Crusher, SinkKind.Silo, SinkKind.Stockpile },
        },
        [LowGrade] = new MaterialSpec
        {
            Code = LowGrade, Name = "低质煤", Kind = MaterialKind.LowGrade,
            InSituDensityTPerM3 = 1.40, SwellFactor = 1.35, ResidualSwellFactor = 1.10, NeedsBlasting = false,
            AllowedSinks = new() { SinkKind.Stockpile, SinkKind.Silo },
        },
    };

    public static IReadOnlyCollection<MaterialSpec> All => _specs.Values;

    /// <summary>取物料规格。未知码回落硬岩（保守：按剥离、需爆破处理）。</summary>
    public static MaterialSpec Resolve(string? code)
        => code != null && _specs.TryGetValue(code, out var s) ? s : _specs[Rock];

    public static bool Exists(string? code) => code != null && _specs.ContainsKey(code);

    /// <summary>覆盖/新增物料（接入台账时调用）。</summary>
    public static void Override(MaterialSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Code)) _specs[spec.Code] = spec;
    }

    public static void ResetToDefaults()
    {
        _specs.Clear();
        foreach (var kv in BuildDefaults()) _specs[kv.Key] = kv.Value;
    }

    /// <summary>中文/别名 → 物料码。用于解析历史自由文本与台账 material 列（c4/c9/rh/coal）。</summary>
    public static string CodeFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string t = text.Trim();

        if (_specs.ContainsKey(t)) return t;

        if (t.Contains("表土") || t.Contains("腐殖") || t.Contains("黄土")) return Topsoil;
        if (t.Contains("风化") || t.Contains("软岩")) return Weathered;
        if (t.Contains("夹矸") || t.Contains("矸")) return Interburden;
        if (t.Contains("低质") || t.Contains("低品位") || t.Contains("劣质")) return LowGrade;
        if (t.Contains("煤") || t.Contains("矿")) return Coal;
        if (t.Contains("岩") || t.Contains("石")) return Rock;

        // GeoDataBase working_face.material 列：c4/c9=煤层号，rh=岩
        if (t.StartsWith("c", StringComparison.OrdinalIgnoreCase) && t.Length <= 3) return Coal;
        if (t.Equals("rh", StringComparison.OrdinalIgnoreCase)) return Rock;

        return "";
    }
}
