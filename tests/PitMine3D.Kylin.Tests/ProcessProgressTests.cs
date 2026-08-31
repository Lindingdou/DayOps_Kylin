using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>工序进度跟踪核算回归（穿孔按延米/其余按量的达成率聚合）。</summary>
public class ProcessProgressTests
{
    [Fact]
    public void Attainment_load_by_volume_drill_by_meters()
    {
        var load = new ProcessProgress.Row(ProcessType.Load, 1000, 900);
        Assert.Equal(90, ProcessProgress.AttainmentOf(load)!.Value, 3);

        var drill = new ProcessProgress.Row(ProcessType.Drill, 0, 0,
            new DrillQuantity { PlanMeters = 300, ActualMeters = 330 });
        Assert.Equal(110, ProcessProgress.AttainmentOf(drill)!.Value, 3);   // 延米优先
    }

    [Fact]
    public void No_plan_returns_null()
    {
        Assert.Null(ProcessProgress.AttainmentOf(new ProcessProgress.Row(ProcessType.Load, 0, 500)));
        Assert.Null(ProcessProgress.AttainmentOf(new ProcessProgress.Row(ProcessType.Drill, 0, 0, new DrillQuantity())));
    }

    [Fact]
    public void Summarize_groups_by_process()
    {
        var rows = new List<ProcessProgress.Row>
        {
            new(ProcessType.Load, 1000, 900),    // 90%
            new(ProcessType.Load, 2000, 2000),   // 100% done
            new(ProcessType.Haul, 5000, 4000),   // 80%
            new(ProcessType.Drill, 0, 0, new DrillQuantity { PlanMeters = 300, ActualMeters = 330 }), // 110% done
        };
        var s = ProcessProgress.Summarize(rows);
        var load = s.First(x => x.Process == ProcessType.Load);
        Assert.Equal(2, load.Count);
        Assert.Equal(95, load.AvgAttainmentPct, 3);    // (90+100)/2
        Assert.Equal(1, load.DoneCount);               // 100% 那条
        var drill = s.First(x => x.Process == ProcessType.Drill);
        Assert.Equal(110, drill.AvgAttainmentPct, 3);
        Assert.Equal(1, drill.DoneCount);
    }
}
