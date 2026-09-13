// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/FaceProcessTree.cs（逐行对应；仅命名空间适配，图标类型 WPF ImageSource → Avalonia IImage）
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using PitMine3D.Kylin.Data;                       // EquipmentCategory
using PitMine3D.Kylin.TaskLib.Simulation;         // EquipKind / EquipState / EquipIconLibrary
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  「确定开采程序」的设备工艺树 —— 面 → 工序 → 设备
//
//  为什么是树不是网格：所属关系本来就是树。四个型号列并排的时候，
//  「它们同属一个面的四道工序」这层关系在界面上<b>看不出来</b>；工艺参数也只能塞成一个字符串。
//
//  ★ 树上每一项都能就地改 —— 它是**配置界面**，不是只读展示。
//
//  ★ 最要紧的一条：免爆的面在树上<b>根本没有穿孔/爆破两个分支</b>，
//    而不是"有分支但空着"。空着 ≠ 不需要 —— 这两件事在网格里长得一模一样
//    （延米都是 0），在树上靠结构就分开了。
//
//  ★ 图标不新画：走 EquipIconLibrary（= 三维那套 EquipSymbolLibrary 形态 + EquipPalette 配色
//    + SimPanelCamera 轴测）。另画一套的话，同一台钻机在配置树和三维里长得不一样、颜色也不一样。
// ─────────────────────────────────────────────────────────────────────────────

public abstract class TreeNodeBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n ?? ""));

    private bool _expanded = true;
    public bool IsExpanded { get => _expanded; set { _expanded = value; Raise(); } }
}

/// <summary>树的根节点 = 一个作业面。</summary>
public sealed class FaceNode : TreeNodeBase
{
    private readonly WorkingFace _f;
    private readonly Action _onChanged;

    public FaceNode(WorkingFace f, int attributedUnits, double monthInSituM3, Action onChanged)
    {
        _f = f; _onChanged = onChanged;
        AttributedUnits = attributedUnits;
        MonthInSituM3 = monthInSituM3;
        Children = new ObservableCollection<ProcessNode>();
        Rebuild();
    }

    public WorkingFace Face => _f;
    public ObservableCollection<ProcessNode> Children { get; }

    /// <summary>归属到这个面的采掘单元数。<b>0 = 面上钉的一切都是摆设</b>，必须显示出来。</summary>
    public int AttributedUnits { get; }
    /// <summary>本月该面的原位实方（来自排产；没排过是 0）。</summary>
    public double MonthInSituM3 { get; }

    public string Header => _f.Name;

    /// <summary>副标题：物料 · 标高 · 份额 · 归属单元 · 本月量。</summary>
    public string Sub
    {
        get
        {
            string s = $"{_f.MaterialName} · {_f.BenchElevationM:0}m · 份额{_f.SharePct:0}%";
            s += AttributedUnits > 0 ? $" · 归属{AttributedUnits}单元" : " · ◆ 未归属到任何单元";
            if (MonthInSituM3 > 0) s += $" · 本月{MonthInSituM3 / 1e4:0.##}万m³";
            return s;
        }
    }

    /// <summary>
    /// 面级告警。<b>只报这一级独有的、以及下级的【条数汇总】</b> ——
    /// 逐条列出来的话，每条型号问题会在面节点和工序节点上各出现一次；
    /// 三个面就是十几行红字，真正要看的那一条反而被淹掉。细节归工序节点。
    /// </summary>
    public string Warn
    {
        get
        {
            var w = new List<string>();
            if (AttributedUnits == 0)
                w.Add("没有单元归属到这个面 —— 下面配的型号与工艺这一轮一条都不生效");

            int badModel = Children.Count(c => c.HasWarn);
            if (badModel > 0) w.Add($"{badModel} 个工序的型号排产时挑不到设备（见下）");

            // 工艺算不出量是面级独有的（参数挂在面的 Process 上），逐条报
            w.AddRange((_f.Process ?? new FaceProcessChain())
                       .CheckComputable(_f.Name, _f.MaterialCode, 0));
            return w.Count == 0 ? "" : string.Join("\n", w);
        }
    }
    public bool HasWarn => Warn.Length > 0;

    /// <summary>
    /// 按物料重建子节点。<b>免爆面不生成穿孔与爆破两支</b>。
    /// 改了物料或"是否穿爆"之后要重调 —— 分支的存在与否本身就是信息。
    /// </summary>
    public void Rebuild()
    {
        Children.Clear();
        var p = _f.Process ?? (_f.Process = new FaceProcessChain());
        bool blast = p.ResolveDrilling(_f.MaterialCode);

        if (blast)
        {
            Children.Add(new ProcessNode(_f, FaceProcess.Drill, _onChanged));
            Children.Add(new ProcessNode(_f, FaceProcess.Blast, _onChanged));
        }
        Children.Add(new ProcessNode(_f, FaceProcess.Load, _onChanged));
        Children.Add(new ProcessNode(_f, FaceProcess.Haul, _onChanged));
        Children.Add(new ProcessNode(_f, FaceProcess.Dump, _onChanged));

        Raise(nameof(Children)); Raise(nameof(Sub)); Raise(nameof(Warn)); Raise(nameof(HasWarn));
    }

    public void RefreshHeader()
    { Raise(nameof(Header)); Raise(nameof(Sub)); Raise(nameof(Warn)); Raise(nameof(HasWarn)); }
}

/// <summary>树的子节点 = 一道工序。<b>可就地改</b>：型号、配车数、关键参数。</summary>
public sealed class ProcessNode : TreeNodeBase
{
    private readonly WorkingFace _f;
    private readonly Action _onChanged;

    public ProcessNode(WorkingFace f, FaceProcess process, Action onChanged)
    {
        _f = f; Process = process; _onChanged = onChanged;
    }

    public FaceProcess Process { get; }

    public string ProcessName => Process switch
    {
        FaceProcess.Drill => "穿孔",
        FaceProcess.Blast => "爆破",
        FaceProcess.Load => "采装",
        FaceProcess.Haul => "运输",
        _ => "排土",
    };

    /// <summary>本工序排不排设备。<b>爆破不排</b> —— 它是个窗口不是一台机器。</summary>
    public bool HasEquipment => Process != FaceProcess.Blast;

    /// <summary>
    /// 工序图标 —— 走 <see cref="EquipIconLibrary"/>（三维那套符号），<b>不另画</b>。
    /// 爆破没有设备，返回 null（模板里就不显示图标）。
    /// </summary>
    public IImage? Icon => HasEquipment ? EquipIconLibrary.Get(EquipKindOf) : null;

    /// <summary>本工序对应的设备类别。采装按已选型号在电铲/前装机之间判。</summary>
    private EquipKind EquipKindOf => Process switch
    {
        FaceProcess.Drill => EquipKind.Drill,
        FaceProcess.Haul => EquipKind.Truck,
        FaceProcess.Dump => EquipKind.Dozer,
        // 采装：选的型号是前装机就画前装机 —— 两者同一道工序，图标要跟着实际设备走
        FaceProcess.Load => LoaderIsWheel ? EquipKind.Loader : EquipKind.Shovel,
        _ => EquipKind.Other,
    };

    private bool LoaderIsWheel
        => PlanEquipModelCatalog.All.FirstOrDefault(m =>
               string.Equals(m.Model, _f.LoaderModel, StringComparison.OrdinalIgnoreCase))
           ?.Category == EquipmentCategory.Loader;

    // ── 型号：就地下拉 ────────────────────────────────────────────────
    /// <summary>
    /// 可选型号（首项「不约束」）。爆破没有设备，返回空表。
    ///
    /// <para>★ 面上钉的型号若<b>不在字典里</b>（改过型号台账、或库没就绪），也要把它作为一项塞进来 ——
    /// 否则 SelectedValue 匹配不上任何项，<b>下拉显示成空的</b>，人看不到自己钉的是什么，
    /// 只能从旁边那行红字里猜。空下拉和"没配"长得一模一样。</para>
    /// </summary>
    public List<PlanEquipModel> ModelOptions
    {
        get
        {
            var pool = Process switch
            {
                FaceProcess.Drill => PlanEquipModelCatalog.DrillOptions,
                FaceProcess.Load => PlanEquipModelCatalog.LoaderOptions,
                FaceProcess.Haul => PlanEquipModelCatalog.TruckOptions,
                FaceProcess.Dump => PlanEquipModelCatalog.DozerOptions,
                _ => new List<PlanEquipModel>(),
            };
            string m = Model;
            if (m.Length > 0 && !pool.Any(x => string.Equals(x.Model, m, StringComparison.OrdinalIgnoreCase)))
                pool.Add(new PlanEquipModel { Model = m, Category = EquipmentCategory.Other });
            return pool;
        }
    }

    public string Model
    {
        get => Process switch
        {
            FaceProcess.Drill => _f.DrillModel,
            FaceProcess.Load => _f.LoaderModel,
            FaceProcess.Haul => _f.TruckModel,
            FaceProcess.Dump => _f.DozerModel,
            _ => "",
        };
        set
        {
            string v = (value ?? "").Trim();
            switch (Process)
            {
                case FaceProcess.Drill: _f.DrillModel = v; break;
                case FaceProcess.Load: _f.LoaderModel = v; break;
                case FaceProcess.Haul: _f.TruckModel = v; break;
                case FaceProcess.Dump: _f.DozerModel = v; break;
                default: return;
            }
            Raise(); Raise(nameof(Icon)); Raise(nameof(Detail)); Raise(nameof(Warn)); Raise(nameof(HasWarn));
            _onChanged();
        }
    }

    // ── 配车数：只有运输这一支有 ───────────────────────────────────────
    public bool HasTruckCount => Process == FaceProcess.Haul;
    public int TrucksPerLoader
    {
        get => _f.TrucksPerLoader;
        set { _f.TrucksPerLoader = Math.Max(0, value); Raise(); Raise(nameof(Detail)); _onChanged(); }
    }

    // ── 参数摘要 ─────────────────────────────────────────────────────
    public string Detail
    {
        get
        {
            var p = _f.Process ?? new FaceProcessChain();
            return Process switch
            {
                FaceProcess.Drill =>
                    $"孔网 {p.HoleSpacingM:0.#}×{p.HoleBurdenM:0.#}m · 台阶 "
                    + (p.BenchHeightM > 0 ? $"{p.BenchHeightM:0.#}m" : "◆按台账")
                    + $" · 超深 {p.SubDrillM:0.#}m · 孔径 {p.HoleDiameterMm:0}mm",
                FaceProcess.Blast =>
                    $"单耗 {p.PowderFactorKgPerM3:0.###}kg/m³ · "
                    + (p.BlastLeadDays > 0 ? $"超前 {p.BlastLeadDays} 工日〔面级〕" : "超前期用全局值")
                    + (p.BlastBatchWanM3 > 0 ? $" · 单次 {p.BlastBatchWanM3:0.##}万m³" : " · 单次不限"),
                FaceProcess.Load =>
                    FaceProcessChain.LoadMethodText(p.LoadMethod)
                    + (p.MiningWidthM > 0 ? $" · 采宽 {p.MiningWidthM:0.#}m" : " · 采宽按台账"),
                FaceProcess.Haul =>
                    (p.ViaCrusher ? "经破碎站 · " : "")
                    + (_f.HasDestination ? $"→ {_f.DestinationName}" : "→ 去向由排产自动配")
                    + (_f.HaulDistanceKm > 0 ? $" {_f.HaulDistanceKm:0.##}km" : ""),
                _ => FaceProcessChain.DumpMethodText(p.DumpMethod),
            };
        }
    }

    /// <summary>本工序的告警（型号挑不到 / 参数算不出）。</summary>
    public string Warn
    {
        get
        {
            if (!HasEquipment) return "";
            string m = Model;
            if (string.IsNullOrWhiteSpace(m)) return "";
            var hit = ModelOptions.FirstOrDefault(x =>
                          string.Equals(x.Model, m, StringComparison.OrdinalIgnoreCase) && x.Model.Length > 0);
            if (hit == null) return $"「{m}」不在型号字典的该类别里 —— 排产时挑不到设备，这个面会欠产";
            if (!hit.Usable) return $"「{hit.Model}」在册 {hit.OnRoll} 台 / 可派 {hit.Dispatchable} 台 —— 排产时一台都挑不出来";
            return "";
        }
    }
    public bool HasWarn => Warn.Length > 0;

    public void RefreshAll()
    {
        Raise(nameof(Model)); Raise(nameof(Icon)); Raise(nameof(Detail));
        Raise(nameof(TrucksPerLoader)); Raise(nameof(Warn)); Raise(nameof(HasWarn));
    }
}

/// <summary>树的数据源。<b>就地改写回 <see cref="WorkingFace"/></b>，没有第二份状态。</summary>
public sealed class FaceProcessTree
{
    public ObservableCollection<FaceNode> Roots { get; } = new();

    /// <summary>
    /// 重建整棵树。
    /// </summary>
    /// <param name="faces">作业面清单。</param>
    /// <param name="attribution">归属结果（单元→面名）。null = 还没解算过，归属数一律显示 0。</param>
    /// <param name="monthByFace">本月逐面实方（面名→m³）。null = 还没排过产。</param>
    /// <param name="onChanged">任一节点被改之后的回调（刷新汇总/落盘）。</param>
    public void Rebuild(IEnumerable<WorkingFace>? faces,
                        IReadOnlyDictionary<string, string>? attribution,
                        IReadOnlyDictionary<string, double>? monthByFace,
                        Action onChanged)
    {
        Roots.Clear();
        var byFace = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in attribution ?? new Dictionary<string, string>())
            byFace[kv.Value] = (byFace.TryGetValue(kv.Value, out int c) ? c : 0) + 1;

        foreach (var f in faces ?? Enumerable.Empty<WorkingFace>())
        {
            if (f == null || string.IsNullOrWhiteSpace(f.Name)) continue;
            double m3 = monthByFace != null && monthByFace.TryGetValue(f.Name, out double v) ? v : 0;
            Roots.Add(new FaceNode(f, byFace.TryGetValue(f.Name, out int n) ? n : 0, m3, onChanged));
        }
    }

    /// <summary>整棵树刷新显示（面上的物料/去向在左边网格改过之后调）。</summary>
    public void RefreshAll()
    {
        foreach (var r in Roots)
        {
            r.Rebuild();                                   // 物料可能变了 ⇒ 免爆分支要重算
            foreach (var c in r.Children) c.RefreshAll();
        }
    }

    /// <summary>统计：配了型号的工序数 / 有告警的工序数。</summary>
    public (int Configured, int Total, int Warned) Stats()
    {
        int cfg = 0, tot = 0, warn = 0;
        foreach (var r in Roots)
            foreach (var c in r.Children)
            {
                if (!c.HasEquipment) continue;
                tot++;
                if (!string.IsNullOrWhiteSpace(c.Model)) cfg++;
                if (c.HasWarn) warn++;
            }
        return (cfg, tot, warn);
    }
}
