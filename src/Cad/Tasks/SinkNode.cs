using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks;

// 忠实逐字移植 TaskLib.Domain.SinkNode —— 去向本体(排土场/破碎站/煤仓/堆场统一为「汇」)：
// 库容(占容方 Kr)/接纳能力/按量推进距离/煤质入仓标准/时空可用性。自足(仅依赖 MaterialSpec)，可单测。

/// <summary>一个受排/受矿点。容量口径一律是【占容方 m³】。</summary>
public sealed class SinkNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SinkKind Kind { get; set; } = SinkKind.ExternalDump;

    public double DesignCapacityM3 { get; set; }
    public double FilledM3 { get; set; }
    public double RemainingM3 => DesignCapacityM3 <= 0 ? double.PositiveInfinity : Math.Max(0, DesignCapacityM3 - FilledM3);
    public bool IsCapacityLimited => DesignCapacityM3 > 0;
    public double FillRate => DesignCapacityM3 <= 0 ? 0 : Math.Min(1, FilledM3 / DesignCapacityM3);

    public double AcceptTph { get; set; }

    public double? MaxAshPct { get; set; }
    public double? MinCalorificMJkg { get; set; }
    public double? MaxSulfurPct { get; set; }
    public bool HasBlendLimits => MaxAshPct.HasValue || MinCalorificMJkg.HasValue || MaxSulfurPct.HasValue;
    public HashSet<string> AcceptedMaterials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double BenchHeightM { get; set; } = 20;
    public double WorkLineLengthM { get; set; }
    public int ActiveBenchLevel { get; set; } = 1;
    public double BenchSlopeAngleDeg { get; set; } = 35;

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double FallbackHaulKm { get; set; } = 2.5;

    public string Status { get; set; } = "active";
    public double OpenFromHour { get; set; }
    public double OpenToHour { get; set; } = 24;
    public string? OpenFromPeriod { get; set; }

    public bool IsDumping => Kind.IsDumping();
    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
    public string RefEntityId { get; set; } = "";

    /// <summary>是否接纳该物料：先看本点白名单，再看物料自身的允许去向类型。</summary>
    public bool Accepts(MaterialSpec m)
    {
        if (AcceptedMaterials.Count > 0) return AcceptedMaterials.Contains(m.Code);
        return m.Accepts(Kind);
    }

    /// <summary>本时段可接收的吨量上限（受通过能力约束）。0/负工时视为不限。</summary>
    public double ThroughputCapT(double hours)
        => AcceptTph <= 0 || hours <= 0 ? double.PositiveInfinity : AcceptTph * hours;

    public double RemainingAfter(double dumpM3) => IsCapacityLimited ? Math.Max(0, RemainingM3 - dumpM3) : double.PositiveInfinity;

    /// <summary>按占容方反算排土推进距离 m：d = V容 / (工作线长 × 台阶高)。</summary>
    public double AdvanceMetersFor(double dumpM3)
    {
        double denom = WorkLineLengthM * BenchHeightM;
        return denom <= 1e-6 ? 0 : dumpM3 / denom;
    }

    public string CapacityCaption => IsCapacityLimited
        ? $"余 {RemainingM3 / 1e4:0.##} 万m³（填{FillRate * 100:0.#}%）" : "容量不限";
    public string Caption => $"{Name}（{Kind.Label()}）";

    public SinkNode Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind, DesignCapacityM3 = DesignCapacityM3, FilledM3 = FilledM3,
        AcceptTph = AcceptTph, AcceptedMaterials = new HashSet<string>(AcceptedMaterials, StringComparer.OrdinalIgnoreCase),
        MaxAshPct = MaxAshPct, MinCalorificMJkg = MinCalorificMJkg, MaxSulfurPct = MaxSulfurPct,
        BenchHeightM = BenchHeightM, WorkLineLengthM = WorkLineLengthM, ActiveBenchLevel = ActiveBenchLevel,
        BenchSlopeAngleDeg = BenchSlopeAngleDeg, X = X, Y = Y, Z = Z, FallbackHaulKm = FallbackHaulKm,
        Status = Status, OpenFromHour = OpenFromHour, OpenToHour = OpenToHour, OpenFromPeriod = OpenFromPeriod, RefEntityId = RefEntityId,
    };
}

/// <summary>去向登记簿。未接台账时用空登记簿(假去向比没有去向坏得多)。</summary>
public sealed class SinkRegistry
{
    private readonly Dictionary<string, SinkNode> _sinks = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<SinkNode> All => _sinks.Values;
    public IEnumerable<SinkNode> Active => _sinks.Values.Where(s => s.IsActive);
    public SinkNode? Find(string? id) => id != null && _sinks.TryGetValue(id, out var s) ? s : null;
    public void Put(SinkNode s) { if (!string.IsNullOrWhiteSpace(s.Id)) _sinks[s.Id] = s; }
    public void Clear() => _sinks.Clear();
    public void Load(IEnumerable<SinkNode> sinks) { Clear(); foreach (var s in sinks) Put(s); }
    public IEnumerable<SinkNode> CandidatesFor(MaterialSpec m) => Active.Where(s => s.Accepts(m) && s.RemainingM3 > 0);
    public double AddFilled(string sinkId, double dumpM3)
    {
        var s = Find(sinkId);
        if (s == null) return 0;
        s.FilledM3 = Math.Max(0, s.FilledM3 + dumpM3);
        return s.FillRate;
    }
    public SinkRegistry Clone() { var r = new SinkRegistry(); foreach (var s in _sinks.Values) r.Put(s.Clone()); return r; }
    public static SinkRegistry Sample() => new();
}
