using System;
using System.Linq;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 移植原 GeoDataBase 的仓储服务层（SqlLib 仓储核 + 实体 + IGeoDataContext + EquipmentDataContext 门面）
/// 的冒烟：对真实种子库（50 迁移）走一遍 Dapper 映射 —— 布尔列、日期文本列、复合主键、Upsert。
/// TaskLib 引擎族（作业面台账 / 去向台账 / 装配盘子）全靠这个门面取数，这里先把地基踩实。
/// </summary>
public class GeoDataContextFacadeTests
{
    [Fact]
    public void Facade_reads_seeded_equipment_and_models()
    {
        using var db = GeoDatabase.OpenWith(new SqliteDialect());
        var ctx = db.Services;
        Assert.Same(ctx, EquipmentDataContext.Current);

        var all = ctx.Equipment.All();
        Assert.True(all.Count > 0, "种子库应有设备");
        var trucks = ctx.Equipment.ByCategory(EquipmentCategory.Truck);
        Assert.True(trucks.Count > 0);
        Assert.All(trucks, e => Assert.Equal(EquipmentCategory.Truck.ToString(), e.Category));

        var models = ctx.Models.All();
        Assert.True(models.Count > 0);
    }

    [Fact]
    public void ShiftCalendar_roundtrips_bool_and_date_text_columns()
    {
        using var db = GeoDatabase.OpenWith(new SqliteDialect());
        var svc = db.Services.ShiftCalendar;
        var d = new DateTime(2031, 3, 4);
        svc.Upsert(new ShiftCalendar { Date = d, Shift = "A", StartTime = "08:00", IsBlastShift = true, Weather = "晴" });
        svc.Upsert(new ShiftCalendar { Date = d, Shift = "B", StartTime = "16:00", IsBlastShift = false });

        var day = svc.ByDate(d);
        Assert.Equal(2, day.Count);
        Assert.True(day.Single(s => s.Shift == "A").IsBlastShift);
        Assert.False(day.Single(s => s.Shift == "B").IsBlastShift);
        Assert.Equal(d, day[0].Date.Date);

        var blast = svc.BlastShifts(d, d);
        Assert.Single(blast);
        Assert.NotNull(svc.Get(d, "A"));
        Assert.Null(svc.Get(d, "C"));
    }

    [Fact]
    public void WorkingFaceRouting_upsert_get_delete()
    {
        using var db = GeoDatabase.OpenWith(new SqliteDialect());
        var svc = db.Services.WorkingFaceRoutings;
        svc.Upsert(new WorkingFaceRouting { FaceCode = "T-面-1", Process = "Load", DayTargetM3 = 1234, DestinationId = "D1", QualityAshPct = 12.5 });
        var r = svc.Get("T-面-1");
        Assert.NotNull(r);
        Assert.Equal(1234, r!.DayTargetM3, 6);
        Assert.Equal(12.5, r.QualityAshPct!.Value, 6);
        Assert.Null(r.QualityCvMjKg);

        svc.Upsert(new WorkingFaceRouting { FaceCode = "T-面-1", Process = "Load", DayTargetM3 = 99 });
        Assert.Equal(99, svc.Get("T-面-1")!.DayTargetM3, 6);
        svc.Delete("T-面-1");
        Assert.Null(svc.Get("T-面-1"));
    }

    [Fact]
    public void DumpSites_and_LoadUnloadPoints_and_Production_read()
    {
        using var db = GeoDatabase.OpenWith(new SqliteDialect());
        var ctx = db.Services;
        Assert.True(ctx.DumpSites.All(activeOnly: false).Count >= 0);   // 种子库不带排土场，只验读得通
        Assert.True(ctx.LoadUnloadPoints.All().Count >= 0);
        var prod = ctx.Production.All();
        Assert.True(prod.Count > 0, "种子库应有生产记录");
        var first = prod[0];
        var byDate = ctx.Production.ByDate(first.Date);
        Assert.Contains(byDate, p => p.Id == first.Id);
        Assert.True(ctx.Fault.All().Count > 0);
        Assert.True(ctx.Plan.All().Count > 0);
    }
}
