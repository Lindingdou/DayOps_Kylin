using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks;

// 忠实逐字移植 TaskLib.Domain.MaterialSpec —— 物料目录 + 体积/吨量换算 + 煤岩分类。全自足、可单测。

/// <summary>物料大类。决定爆破需求、允许去向、是否计入采出量。</summary>
public enum MaterialKind { Topsoil, Weathered, Rock, Interburden, Coal, LowGrade }

/// <summary>去向设施类型。决定运距特征与容量口径。</summary>
public enum SinkKind { ExternalDump, InternalDump, Crusher, Silo, Stockpile, TopsoilYard }

public static class MaterialEnumLabels
{
    public static string Label(this MaterialKind k) => k switch
    {
        MaterialKind.Topsoil => "表土", MaterialKind.Weathered => "风化岩", MaterialKind.Rock => "硬岩",
        MaterialKind.Interburden => "夹矸", MaterialKind.Coal => "煤", MaterialKind.LowGrade => "低质煤", _ => "",
    };
    public static string Label(this SinkKind k) => k switch
    {
        SinkKind.ExternalDump => "外排土场", SinkKind.InternalDump => "内排土场", SinkKind.Crusher => "破碎站",
        SinkKind.Silo => "原煤仓", SinkKind.Stockpile => "储煤场", SinkKind.TopsoilYard => "表土堆场", _ => "",
    };
    /// <summary>是否为排弃类去向（占排土库容、按 Kr 扣容）。</summary>
    public static bool IsDumping(this SinkKind k)
        => k is SinkKind.ExternalDump or SinkKind.InternalDump or SinkKind.TopsoilYard;
}

/// <summary>一种物料的完整规格。密度与两个膨胀系数是全部体积/吨量换算的唯一来源。</summary>
public sealed class MaterialSpec
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public MaterialKind Kind { get; set; }
    public double InSituDensityTPerM3 { get; set; } = 2.4;
    public double SwellFactor { get; set; } = 1.5;
    public double ResidualSwellFactor { get; set; } = 1.15;
    public bool NeedsBlasting { get; set; }
    public HashSet<SinkKind> AllowedSinks { get; set; } = new();
    public bool IsOre => Kind is MaterialKind.Coal or MaterialKind.LowGrade;

    public double ToLooseM3(double inSituM3) => inSituM3 * SwellFactor;
    public double ToDumpM3(double inSituM3) => inSituM3 * ResidualSwellFactor;
    public double ToTonnage(double inSituM3) => inSituM3 * InSituDensityTPerM3;
    public double FromTonnage(double t) => InSituDensityTPerM3 <= 1e-6 ? 0 : t / InSituDensityTPerM3;
    public bool Accepts(SinkKind sink) => AllowedSinks.Count == 0 || AllowedSinks.Contains(sink);
    public string Caption => $"{Name}（ρ{InSituDensityTPerM3:0.##} · Ks{SwellFactor:0.00} · Kr{ResidualSwellFactor:0.00}）";
}

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
    public string PrimaryCode => Shares.Count == 0 ? "" : Shares.OrderByDescending(s => s.Fraction).First().MaterialCode;
    public double FractionOf(string code)
        => Shares.Where(s => string.Equals(s.MaterialCode, code, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Fraction);
    /// <summary>矿(煤)占比——采出量与剥离量的拆分依据。</summary>
    public double OreFraction => Shares.Sum(s => MaterialCatalog.Resolve(s.MaterialCode).IsOre ? s.Fraction : 0);
    public IEnumerable<(MaterialSpec Spec, double InSituM3)> Split(double inSituM3)
        => Shares.Select(s => (MaterialCatalog.Resolve(s.MaterialCode), inSituM3 * s.Fraction));
    public double BlendedDensity => Shares.Sum(s => MaterialCatalog.Resolve(s.MaterialCode).InSituDensityTPerM3 * s.Fraction);
    public double ToTonnage(double inSituM3) => Split(inSituM3).Sum(x => x.Spec.ToTonnage(x.InSituM3));
    public double ToLooseM3(double inSituM3) => Split(inSituM3).Sum(x => x.Spec.ToLooseM3(x.InSituM3));
    public double ToDumpM3(double inSituM3) => Split(inSituM3).Sum(x => x.Spec.ToDumpM3(x.InSituM3));

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
        _ => string.Join("∶", Shares.OrderByDescending(s => s.Fraction).Select(s => $"{MaterialCatalog.Resolve(s.MaterialCode).Name}{s.Fraction * 10:0.#}")),
    };

    /// <summary>解析历史自由文本："煤7∶岩3" / "煤" / "岩"。解析不出回落硬岩(按剥离)。</summary>
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
            for (int i = 0; i < p.Length; i++) if (char.IsDigit(p[i]) || p[i] == '.') { digitAt = i; break; }
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

/// <summary>一种物料在一条任务/作业面上的去向分项。</summary>
public sealed class MaterialDestination
{
    public string MaterialCode { get; set; } = "";
    public double Fraction { get; set; }
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

/// <summary>物料目录。默认值取露天煤矿常用工程经验区间的中值。</summary>
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
        [Topsoil] = new MaterialSpec { Code = Topsoil, Name = "表土", Kind = MaterialKind.Topsoil, InSituDensityTPerM3 = 1.60, SwellFactor = 1.20, ResidualSwellFactor = 1.08, NeedsBlasting = false, AllowedSinks = new() { SinkKind.TopsoilYard } },
        [Weathered] = new MaterialSpec { Code = Weathered, Name = "风化岩", Kind = MaterialKind.Weathered, InSituDensityTPerM3 = 2.10, SwellFactor = 1.35, ResidualSwellFactor = 1.12, NeedsBlasting = false, AllowedSinks = new() { SinkKind.ExternalDump, SinkKind.InternalDump } },
        [Rock] = new MaterialSpec { Code = Rock, Name = "硬岩", Kind = MaterialKind.Rock, InSituDensityTPerM3 = 2.50, SwellFactor = 1.50, ResidualSwellFactor = 1.15, NeedsBlasting = true, AllowedSinks = new() { SinkKind.ExternalDump, SinkKind.InternalDump } },
        [Interburden] = new MaterialSpec { Code = Interburden, Name = "夹矸", Kind = MaterialKind.Interburden, InSituDensityTPerM3 = 2.20, SwellFactor = 1.40, ResidualSwellFactor = 1.13, NeedsBlasting = true, AllowedSinks = new() { SinkKind.ExternalDump, SinkKind.InternalDump } },
        [Coal] = new MaterialSpec { Code = Coal, Name = "煤", Kind = MaterialKind.Coal, InSituDensityTPerM3 = 1.35, SwellFactor = 1.35, ResidualSwellFactor = 1.10, NeedsBlasting = false, AllowedSinks = new() { SinkKind.Crusher, SinkKind.Silo, SinkKind.Stockpile } },
        [LowGrade] = new MaterialSpec { Code = LowGrade, Name = "低质煤", Kind = MaterialKind.LowGrade, InSituDensityTPerM3 = 1.40, SwellFactor = 1.35, ResidualSwellFactor = 1.10, NeedsBlasting = false, AllowedSinks = new() { SinkKind.Stockpile, SinkKind.Silo } },
    };

    public static IReadOnlyCollection<MaterialSpec> All => _specs.Values;
    /// <summary>取物料规格。未知码回落硬岩(保守:按剥离、需爆破)。</summary>
    public static MaterialSpec Resolve(string? code) => code != null && _specs.TryGetValue(code, out var s) ? s : _specs[Rock];
    public static bool Exists(string? code) => code != null && _specs.ContainsKey(code);
    public static void Override(MaterialSpec spec) { if (!string.IsNullOrWhiteSpace(spec.Code)) _specs[spec.Code] = spec; }
    public static void ResetToDefaults() { _specs.Clear(); foreach (var kv in BuildDefaults()) _specs[kv.Key] = kv.Value; }

    /// <summary>中文/别名 → 物料码。用于解析历史自由文本与台账 material 列(c4/c9/rh/coal)。</summary>
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
        if (t.StartsWith("c", StringComparison.OrdinalIgnoreCase) && t.Length <= 3) return Coal;
        if (t.Equals("rh", StringComparison.OrdinalIgnoreCase)) return Rock;
        return "";
    }
}
