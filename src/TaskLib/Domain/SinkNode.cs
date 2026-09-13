// 忠实移植自原 PitMine3D Modules/TaskLib/Domain/SinkNode.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Domain;

// ─────────────────────────────────────────────────────────────────────────────
//  去向本体 —— 「排弃/送到哪」的权威定义。
//
//  把 dump_site(排土场) / load_unload_point(破碎站·煤仓·堆场) 统一成一类「汇」，
//  这样流向分配、库容校核、运距计算才能一视同仁地处理。
//
//  露天矿特有的三条约束都落在这里：
//   · 剩余库容按【排弃占容方 V容 = V实×Kr】扣，不是按实方扣。
//   · 排土场自下而上分层排弃，同一时刻只有一个「当前可排台阶层」在接收。
//   · 内排土场必须等采空区形成才能启用（OpenFromPeriod / Status）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 一个受排/受矿点。容量口径一律是【占容方 m³】——排土场吃的是沉降后的体积。
/// </summary>
public sealed class SinkNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SinkKind Kind { get; set; } = SinkKind.ExternalDump;

    // ── 容量（占容方 m³）──
    /// <summary>设计容量（占容方 m³）。破碎站/煤仓这类通过型去向可留 0 表示不限。</summary>
    public double DesignCapacityM3 { get; set; }
    /// <summary>已排弃/已入库（占容方 m³）。</summary>
    public double FilledM3 { get; set; }
    /// <summary>剩余库容（占容方 m³）。DesignCapacity=0 视为不限。</summary>
    public double RemainingM3 => DesignCapacityM3 <= 0 ? double.PositiveInfinity : Math.Max(0, DesignCapacityM3 - FilledM3);
    public bool IsCapacityLimited => DesignCapacityM3 > 0;
    public double FillRate => DesignCapacityM3 <= 0 ? 0 : Math.Min(1, FilledM3 / DesignCapacityM3);

    // ── 接纳能力 ──
    /// <summary>卸点通过能力 t/h。0=不限。</summary>
    public double AcceptTph { get; set; }

    // ── 入仓煤质标准（仅受矿类去向：破碎站/煤仓/堆场）──────────────────────────
    //
    //  配煤本来就是**按受矿点**成立的：两个破碎站各有各的合同指标，把两边的煤混在一起
    //  算一个"全矿综合灰分"没有物理含义 —— 那堆煤永远不会真的混到一起。
    //  三项各自可空：null = 该项按全矿级缺省（ExploderConfig.Blend）判。
    //  真源应当是去向台账；台账还没有这几列时由「编制配置」的人工锚点填（CompileOverrides）。

    /// <summary>本受矿点的入仓灰分上限 %。null = 按全矿级缺省。</summary>
    public double? MaxAshPct { get; set; }
    /// <summary>本受矿点的入仓热值下限 MJ/kg。null = 按全矿级缺省。</summary>
    public double? MinCalorificMJkg { get; set; }
    /// <summary>本受矿点的入仓硫分上限 %。null = 按全矿级缺省。</summary>
    public double? MaxSulfurPct { get; set; }

    /// <summary>本点是否自带过入仓标准（三项有其一即算）。</summary>
    public bool HasBlendLimits => MaxAshPct.HasValue || MinCalorificMJkg.HasValue || MaxSulfurPct.HasValue;
    /// <summary>允许接纳的物料码。空=按物料自身的 AllowedSinks 判定。</summary>
    public HashSet<string> AcceptedMaterials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── 排土工艺参数（推进反算与三维模拟用）──
    /// <summary>排土台阶高 m。</summary>
    public double BenchHeightM { get; set; } = 20;
    /// <summary>排土工作线长 m。推进距离 d = V容 / (L × h)。</summary>
    public double WorkLineLengthM { get; set; }
    /// <summary>当前可排台阶层（自下而上，1 起）。排土场必须先形成底部承载层。</summary>
    public int ActiveBenchLevel { get; set; } = 1;
    /// <summary>台阶坡面角(°)。</summary>
    public double BenchSlopeAngleDeg { get; set; } = 35;

    // ── 位置与运距 ──
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    /// <summary>无路网可解时的兜底运距 km（三层兜底的最后一层）。</summary>
    public double FallbackHaulKm { get; set; } = 2.5;

    // ── 时空可用性 ──
    /// <summary>状态 active / full / closed。</summary>
    public string Status { get; set; } = "active";
    /// <summary>当日开放时窗（0..24）；Open 到 Close 之外不接收。0/24=全天。</summary>
    public double OpenFromHour { get; set; }
    public double OpenToHour { get; set; } = 24;
    /// <summary>启用期次（内排土场须等采空区形成；空=已启用）。</summary>
    public string? OpenFromPeriod { get; set; }

    /// <summary>是否为排弃类（占库容）。</summary>
    public bool IsDumping => Kind.IsDumping();
    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>关联的三维/台账实体（dump_site.dump_id 或 mineable_region.id），供三维模拟定位。</summary>
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

    /// <summary>排弃 V容 后的剩余库容。</summary>
    public double RemainingAfter(double dumpM3) => IsCapacityLimited ? Math.Max(0, RemainingM3 - dumpM3) : double.PositiveInfinity;

    /// <summary>
    /// 按占容方反算排土推进距离 m：d = V容 / (工作线长 × 台阶高)。
    /// 三维模拟据此把排土坡顶线沿推进方向偏移，生成当期堆填面。
    /// </summary>
    public double AdvanceMetersFor(double dumpM3)
    {
        double denom = WorkLineLengthM * BenchHeightM;
        return denom <= 1e-6 ? 0 : dumpM3 / denom;
    }

    public string CapacityCaption => IsCapacityLimited
        ? $"余 {RemainingM3 / 1e4:0.##} 万m³（填{FillRate * 100:0.#}%）"
        : "容量不限";

    public string Caption => $"{Name}（{Kind.Label()}）";

    public SinkNode Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind,
        DesignCapacityM3 = DesignCapacityM3, FilledM3 = FilledM3,
        AcceptTph = AcceptTph, AcceptedMaterials = new HashSet<string>(AcceptedMaterials, StringComparer.OrdinalIgnoreCase),
        MaxAshPct = MaxAshPct, MinCalorificMJkg = MinCalorificMJkg, MaxSulfurPct = MaxSulfurPct,
        BenchHeightM = BenchHeightM, WorkLineLengthM = WorkLineLengthM, ActiveBenchLevel = ActiveBenchLevel,
        BenchSlopeAngleDeg = BenchSlopeAngleDeg,
        X = X, Y = Y, Z = Z, FallbackHaulKm = FallbackHaulKm,
        Status = Status, OpenFromHour = OpenFromHour, OpenToHour = OpenToHour, OpenFromPeriod = OpenFromPeriod,
        RefEntityId = RefEntityId,
    };
}

/// <summary>
/// 去向登记簿。TaskLib 内部持有；接 GeoDataBase 后由 SinkRegistryLoader 从
/// dump_site / load_unload_point 灌入，未接通时用内置样例。
/// </summary>
public sealed class SinkRegistry
{
    private readonly Dictionary<string, SinkNode> _sinks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<SinkNode> All => _sinks.Values;
    public IEnumerable<SinkNode> Active => _sinks.Values.Where(s => s.IsActive);

    public SinkNode? Find(string? id) => id != null && _sinks.TryGetValue(id, out var s) ? s : null;

    public void Put(SinkNode s) { if (!string.IsNullOrWhiteSpace(s.Id)) _sinks[s.Id] = s; }
    public void Clear() => _sinks.Clear();
    public void Load(IEnumerable<SinkNode> sinks) { Clear(); foreach (var s in sinks) Put(s); }

    /// <summary>某物料可去的所有在用去向。</summary>
    public IEnumerable<SinkNode> CandidatesFor(MaterialSpec m)
        => Active.Where(s => s.Accepts(m) && s.RemainingM3 > 0);

    /// <summary>累加排弃量（占容方）——实绩回灌与计划推演共用。</summary>
    public double AddFilled(string sinkId, double dumpM3)
    {
        var s = Find(sinkId);
        if (s == null) return 0;
        s.FilledM3 = Math.Max(0, s.FilledM3 + dumpM3);
        return s.FillRate;
    }

    public SinkRegistry Clone()
    {
        var r = new SinkRegistry();
        foreach (var s in _sinks.Values) r.Put(s.Clone());
        return r;
    }

    /// <summary>
    /// <b>空登记簿</b> —— 未接台账时的兜底。
    ///
    /// <para><b>这里原来返回五条编出来的去向</b>：北排土场 12000/8500、内排场 6000/1200、
    /// 表土堆场 400/150、1号破碎站、原煤仓 —— 容量、通过能力、兜底运距、台阶高、工作线长
    /// 全是代码里写死的数，和任何一个矿都没关系。</para>
    ///
    /// <para><b>为什么改成空的</b>：假去向比没有去向坏得多。
    /// 排产会把量排到「表土堆场」上、运输功按 2.0km 兜底运距算、库容闸按 400 万m³ 卡 ——
    /// <b>每一个数都算得出来、每一张报表都自洽，而它们说的是另一个矿</b>。
    /// 空着则一路报「一个去向都没有」，人知道该去建档。</para>
    ///
    /// <para>保留这个方法而不是删掉：五处调用点回落到它，返回空比让它们各自处理 null 安全。</para>
    /// </summary>
    public static SinkRegistry Sample() => new();
}
