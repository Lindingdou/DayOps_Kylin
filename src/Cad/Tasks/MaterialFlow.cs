using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks;

// 忠实逐字移植 TaskLib.Domain.MaterialFlow —— 物料流(六元组)+期物料平衡(剥采比/内排率/运输功)。
// 自足(仅依赖 MaterialSpec), 可单测。

/// <summary>一条物料流：一个期内，从一个源、以一种物料、运往一个汇的量。</summary>
public sealed class MaterialFlow
{
    public string Period { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public double SourceBenchElevationM { get; set; }
    public string MaterialCode { get; set; } = "";
    public double InSituM3 { get; set; }
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind SinkKind { get; set; } = SinkKind.ExternalDump;
    public double HaulKm { get; set; }
    public double EquivHaulKm { get; set; }

    public MaterialSpec Spec => MaterialCatalog.Resolve(MaterialCode);
    public double TonnageT => Spec.ToTonnage(InSituM3);
    public double LooseM3 => Spec.ToLooseM3(InSituM3);
    /// <summary>排弃占容方——排土场按这个扣库容。</summary>
    public double DumpM3 => Spec.ToDumpM3(InSituM3);
    public double EffectiveHaulKm => EquivHaulKm > 1e-6 ? EquivHaulKm : HaulKm;
    /// <summary>运输功 t·km。</summary>
    public double TransportWorkTKm => TonnageT * EffectiveHaulKm;
    public bool IsOre => Spec.IsOre;
    public string RouteCaption => $"{SourceName} → {SinkName}";
    public MaterialFlow Clone() => (MaterialFlow)MemberwiseClone();
}

/// <summary>一个期的物料平衡。采出的每一方都必须有去处；排弃占容 = 剥离占容。</summary>
public sealed class PeriodBalance
{
    public string Period { get; set; } = "";
    public List<MaterialFlow> Flows { get; set; } = new();

    /// <summary>采出量（矿/煤）万 t。</summary>
    public double OreWanT => Flows.Where(f => f.IsOre).Sum(f => f.TonnageT) / 1e4;
    /// <summary>剥离量（非矿）万 m³ 实方。</summary>
    public double StripWanM3 => Flows.Where(f => !f.IsOre).Sum(f => f.InSituM3) / 1e4;
    /// <summary>生产剥采比 m³/t。</summary>
    public double StripRatio => OreWanT <= 1e-9 ? 0 : StripWanM3 / OreWanT;
    /// <summary>排弃占容合计 万 m³。</summary>
    public double DumpedWanM3 => Flows.Where(f => f.SinkKind.IsDumping()).Sum(f => f.DumpM3) / 1e4;

    /// <summary>内排率 % = 内排占容 / 全部排弃占容。</summary>
    public double InternalDumpPct
    {
        get
        {
            double all = Flows.Where(f => f.SinkKind.IsDumping()).Sum(f => f.DumpM3);
            if (all <= 1e-6) return 0;
            return Flows.Where(f => f.SinkKind == SinkKind.InternalDump).Sum(f => f.DumpM3) / all * 100;
        }
    }

    /// <summary>总运输功 万 t·km。</summary>
    public double TransportWorkWanTKm => Flows.Sum(f => f.TransportWorkTKm) / 1e4;

    /// <summary>吨量加权平均运距 km。</summary>
    public double WeightedAvgHaulKm
    {
        get
        {
            double t = Flows.Sum(f => f.TonnageT);
            return t <= 1e-6 ? 0 : Flows.Sum(f => f.TransportWorkTKm) / t;
        }
    }

    public IEnumerable<IGrouping<string, MaterialFlow>> BySink() => Flows.GroupBy(f => f.SinkId);
    public IEnumerable<IGrouping<string, MaterialFlow>> BySource() => Flows.GroupBy(f => f.SourceId);
    public IEnumerable<IGrouping<string, MaterialFlow>> ByMaterial() => Flows.GroupBy(f => f.MaterialCode);

    public double DumpM3At(string sinkId)
        => Flows.Where(f => string.Equals(f.SinkId, sinkId, StringComparison.OrdinalIgnoreCase)).Sum(f => f.DumpM3);
    public double TonnageAt(string sinkId)
        => Flows.Where(f => string.Equals(f.SinkId, sinkId, StringComparison.OrdinalIgnoreCase)).Sum(f => f.TonnageT);
}
