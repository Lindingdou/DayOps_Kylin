using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>一项单价的来源。<b>缺省值不是核准值</b> —— 两者必须分得开。（移植原 <c>MineAssLib.Models.CostSource</c>）</summary>
public enum CostSource
{
    /// <summary>出厂缺省 —— <b>没人核准过</b>，只是为了让算式跑得起来。</summary>
    Default = 0,
    /// <summary>从旧口径迁来（迁移时原样搬，值本身仍未经核准）。</summary>
    Migrated = 1,
    /// <summary>现场核准。</summary>
    Approved = 2,
}

/// <summary>
/// <b>生产成本口径</b> —— 全系统<b>唯一一处</b>单价（原规则 MU14，逐字移植 <c>MineAssLib.Models.ProductionCostBook</c>）。
/// 境界优化 <see cref="EconParams"/> 的缺省全部取自这里；方案仍可逐项覆盖，但"填过"与"没填过"要分得开（<see cref="CostSource"/>）。
/// </summary>
public sealed class ProductionCostBook
{
    // ── 采矿手册 4 式那一组（与 EconParams 同名同义，别再各存各的）─────────
    /// <summary>d · 原煤售价（元/t）。</summary>
    public double CoalPriceYuanT { get; private set; } = 320;
    /// <summary>a · 露天纯采矿成本（元/t）。</summary>
    public double MiningCostYuanT { get; private set; } = 95;
    /// <summary>b · 剥离成本（元/m³，<b>体积口径</b>）。</summary>
    public double StripCostYuanM3 { get; private set; } = 30;
    /// <summary>C_D · 矿石地下采矿成本（元/t）。成本比较法要用。</summary>
    public double UndergroundCostYuanT { get; private set; }
    /// <summary>e · 单位最低盈利（元/t）。</summary>
    public double MinProfitYuanT { get; private set; }
    /// <summary>c · 分摊复垦费（元/t）。</summary>
    public double ReclaimCostYuanT { get; private set; }

    // ── 运输与排土 ─────────────────────────────────────────────────────────
    /// <summary>运输单价（元/(t·km)）。运输功 × 它 = 运输成本。</summary>
    public double HaulCostYuanTKm { get; private set; }
    /// <summary>排土单价（元/m³ <b>占容方</b>）。</summary>
    public double DumpCostYuanM3 { get; private set; }

    // ── 换算与折现 ─────────────────────────────────────────────────────────
    public double DiscountRatePct { get; private set; } = 8;
    public double CoalDensityTM3 { get; private set; } = 1.35;

    /// <summary>设备移设不在本册里 —— 现场口径是「尽量就近」，是距离目标，不设单价。</summary>
    public const string RelocationNote = "移设按【就近】用距离衡量，不设单价（MU11）";

    private readonly Dictionary<string, CostSource> _src = new(StringComparer.Ordinal);
    private readonly List<string> _notes = new();

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>某一项的来源。没记过 = <see cref="CostSource.Default"/>。</summary>
    public CostSource SourceOf(string item)
        => _src.TryGetValue(item ?? "", out var s) ? s : CostSource.Default;

    /// <summary>全部项名（与属性名一致）。</summary>
    public static readonly string[] Items =
    {
        nameof(CoalPriceYuanT), nameof(MiningCostYuanT), nameof(StripCostYuanM3),
        nameof(UndergroundCostYuanT), nameof(MinProfitYuanT), nameof(ReclaimCostYuanT),
        nameof(HaulCostYuanTKm), nameof(DumpCostYuanM3),
        nameof(DiscountRatePct), nameof(CoalDensityTM3),
    };

    /// <summary>还停在出厂缺省、<b>没人核准过</b>的项。</summary>
    public IReadOnlyList<string> UnapprovedItems()
        => Items.Where(i => SourceOf(i) != CostSource.Approved).ToList();

    /// <summary>设一项。非有限值 / 负数一律拒收并留条。</summary>
    public bool Set(string item, double value, CostSource src)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        { _notes.Add($"◆ {item} = {value} 非法（负数或非有限值），已拒收，仍用 {Get(item)}。"); return false; }
        switch (item)
        {
            case nameof(CoalPriceYuanT): CoalPriceYuanT = value; break;
            case nameof(MiningCostYuanT): MiningCostYuanT = value; break;
            case nameof(StripCostYuanM3): StripCostYuanM3 = value; break;
            case nameof(UndergroundCostYuanT): UndergroundCostYuanT = value; break;
            case nameof(MinProfitYuanT): MinProfitYuanT = value; break;
            case nameof(ReclaimCostYuanT): ReclaimCostYuanT = value; break;
            case nameof(HaulCostYuanTKm): HaulCostYuanTKm = value; break;
            case nameof(DumpCostYuanM3): DumpCostYuanM3 = value; break;
            case nameof(DiscountRatePct): DiscountRatePct = value; break;
            case nameof(CoalDensityTM3):
                if (value <= 1e-9) { _notes.Add("◆ 煤容重不能为 0，已拒收。"); return false; }
                CoalDensityTM3 = value; break;
            default: _notes.Add($"◆ 不认识的成本项「{item}」，已忽略。"); return false;
        }
        _src[item] = src;
        return true;
    }

    public double Get(string item) => item switch
    {
        nameof(CoalPriceYuanT) => CoalPriceYuanT,
        nameof(MiningCostYuanT) => MiningCostYuanT,
        nameof(StripCostYuanM3) => StripCostYuanM3,
        nameof(UndergroundCostYuanT) => UndergroundCostYuanT,
        nameof(MinProfitYuanT) => MinProfitYuanT,
        nameof(ReclaimCostYuanT) => ReclaimCostYuanT,
        nameof(HaulCostYuanTKm) => HaulCostYuanTKm,
        nameof(DumpCostYuanM3) => DumpCostYuanM3,
        nameof(DiscountRatePct) => DiscountRatePct,
        nameof(CoalDensityTM3) => CoalDensityTM3,
        _ => 0,
    };

    private static ProductionCostBook _current = CreateWithMigrationNote();

    /// <summary>全系统共用的那一份。<b>别再各处 new</b>。</summary>
    public static ProductionCostBook Current
    {
        get => _current;
        set => _current = value ?? CreateWithMigrationNote();
    }

    /// <summary>判据用：拿一份干净的（不碰全局那份）。</summary>
    public static ProductionCostBook CreateDefault() => new();

    private static ProductionCostBook CreateWithMigrationNote()
    {
        var b = new ProductionCostBook();
        b._notes.Add("· 剥离成本按 30 元/m³（采矿手册四式那一组的口径）。"
                   + "开采程序 NPV 此前自带 28 元/m³，已并入本册 —— 那一处的数会变。");
        b._notes.Add("· " + RelocationNote);
        return b;
    }

    /// <summary>一行摘要（界面/日志直接用）。</summary>
    public string Text()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format(ci, "煤价 {0:0.##} 元/t · 采煤 {1:0.##} 元/t · 剥离 {2:0.##} 元/m³ · "
                                      + "运输 {3:0.###} 元/(t·km) · 排土 {4:0.###} 元/m³占容",
                                    CoalPriceYuanT, MiningCostYuanT, StripCostYuanM3, HaulCostYuanTKm, DumpCostYuanM3));
        var un = UnapprovedItems();
        if (un.Count > 0)
            sb.AppendLine($"◆ 其中 {un.Count}/{Items.Length} 项仍是出厂缺省、未经现场核准："
                        + string.Join(" · ", un) + " —— 拿它算出来的钱只能参考，不能当账。");
        foreach (var n in _notes) sb.AppendLine(n);
        return sb.ToString().TrimEnd();
    }
}
