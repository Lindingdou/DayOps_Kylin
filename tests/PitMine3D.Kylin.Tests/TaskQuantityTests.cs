using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>TaskLib 量核算回归（忠实移植 TaskQuantity：按工序取账 / 无出处写「—」/ 分账合计）。</summary>
public class TaskQuantityTests
{
    [Fact]
    public void OD1_load_uses_in_situ_volume_and_tonnage()
    {
        var t = new ProductionTask { Process = ProcessType.Load, TargetVolumeM3 = 12345, TargetTonnageT = 30000 };
        var l = TaskQuantity.Of(t);
        Assert.True(l.HasValue);
        Assert.Equal("原位实方", l.Basis);
        Assert.Equal("m³", l.Unit);
        Assert.Equal(12345, l.Value, 6);
        Assert.Contains("30,000 t", l.Second);
    }

    [Fact]
    public void OD1_haul_uses_tonnage_not_volume()
    {
        var t = new ProductionTask { Process = ProcessType.Haul, HaulTonnageT = 8900, TripCount = 89 };
        var l = TaskQuantity.Of(t);
        Assert.Equal("承运量", l.Basis);
        Assert.Equal("t", l.Unit);
        Assert.Equal(8900, l.Value, 6);
        Assert.Contains("89 车次", l.Second);
    }

    [Fact]
    public void OD2_blast_has_no_volume_basis()
    {
        var t = new ProductionTask { Process = ProcessType.Blast };
        var l = TaskQuantity.Of(t);
        Assert.False(l.HasValue);           // 爆破无方量口径
        Assert.Equal("—", l.Caption);       // 写「—」不写 0
        Assert.Contains("爆破无方量口径", l.Why);
    }

    [Fact]
    public void OD2_missing_quantity_shows_dash_not_zero()
    {
        var t = new ProductionTask { Process = ProcessType.Load };   // 无量
        var l = TaskQuantity.Of(t);
        Assert.False(l.HasValue);
        Assert.Equal("—", l.Caption);
    }

    [Fact]
    public void Dump_falls_back_to_target_volume_for_old_snapshots()
    {
        // 老快照: 排土量写在 TargetVolumeM3 而非 DumpVolumeM3 → 回退取用, 不再乘 Kr。
        var t = new ProductionTask { Process = ProcessType.Dump, TargetVolumeM3 = 5000 };
        var l = TaskQuantity.Of(t);
        Assert.True(l.HasValue);
        Assert.Equal(5000, l.Value, 6);
        Assert.Equal("排弃占容", l.Basis);
    }

    [Fact]
    public void OD3_totals_keep_separate_ledgers_no_grand_sum()
    {
        var tasks = new List<ProductionTask>
        {
            new() { Process = ProcessType.Drill, ControlVolumeM3 = 10000, Drill = new DrillInfo { PlanHoles = 12, PlanMeters = 340 } },
            new() { Process = ProcessType.Load, TargetVolumeM3 = 20000, TargetTonnageT = 50000 },
            new() { Process = ProcessType.Haul, HaulTonnageT = 50000, TripCount = 500, EffectiveHaulKm = 2.5 },
            new() { Process = ProcessType.Dump, DumpVolumeM3 = 24000 },
            new() { Process = ProcessType.Idle },   // 空闲不计
        };
        var s = TaskQuantity.Sum(tasks);
        Assert.Equal(4, s.TaskCount);                    // Idle 不计
        Assert.Equal(10000, s.DrillControlM3, 6);
        Assert.Equal(12, s.DrillHoles);
        Assert.Equal(20000, s.LoadInSituM3, 6);
        Assert.Equal(50000, s.HaulTonnageT, 6);
        Assert.Equal(50000 * 2.5, s.WorkTKm, 3);         // 运输功 = 吨×运距
        Assert.Equal(2.5, s.AvgHaulKm, 6);
        Assert.Equal(24000, s.DumpM3, 6);
        // 分账列示: 抬头含四本账各自的段, 无"合计 N 万m³"总数
        Assert.Contains("穿孔", s.Caption);
        Assert.Contains("采装", s.Caption);
        Assert.Contains("运输", s.Caption);
        Assert.Contains("排土", s.Caption);
    }

    [Fact]
    public void Empty_shift_caption()
    {
        Assert.Equal("本班无任务。", TaskQuantity.Sum(new List<ProductionTask>()).Caption);
    }
}
