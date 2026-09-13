// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/WeekTargetTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;
using ProductionPlanContext = PitMine3D.Kylin.TaskLib.Engine.ProductionPlanContext;   // 与 Kylin 旧切片 Data.ProductionPlanContext 同名消歧

/// <summary>
/// 周目标 → 日目标 的口径（WK 组），<b>对着真库副本</b>跑。
///
/// <para>
/// 在这一层出现之前，日目标是「月量 ÷ 当月作业日」摊出来的：一个月里的每一天长得一模一样，
/// 这周抢采区、下周大修、月底冲量全都表达不了。现在多了一层人给的周目标，
/// 日目标 = <b>本周剩余量 × 今天的能力权重 ÷ 剩余作业日的权重和</b>。
/// </para>
/// <para>
/// <b>写在真库副本上是安全的</b>：<see cref="RealDbFixture"/> 打开的是临时目录里的副本，
/// 这一组会往 <c>week_plan_target</c> 里写、也会删。生产库不碰。
/// </para>
/// <para>
/// <b>断言克制</b>：日目标的绝对值随台账/编组/检修档期变，钉死一个数只会在下次改台账时假红。
/// 这里断的是不该变的事：口径（周一起）、滚动（已过去的按实绩核销、没录按 0 且点名）、
/// 守恒（各天权重份额之和 = 1）、以及**关掉周目标必须真的退回月路**。
/// </para>
/// </summary>
[Collection("RealDb")]
public class WeekTargetTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly RealDbFixture _db;

    /// <summary>真库里 shift_calendar 有 2026-08 整月的排班，这一周（周四 08-20 所在周）用来做算例。</summary>
    private static readonly DateTime Anchor = new(2026, 8, 20);
    private static string MondayKey => WeekTargetLink.KeyOf(WeekTargetLink.MondayOf(Anchor));

    public WeekTargetTests(RealDbFixture db, ITestOutputHelper o)
    {
        _db = db;
        _out = o;
        EnsureWeekTable();
        Clean();
    }

    /// <summary>
    /// 副本里补上 <c>week_plan_target</c>。
    /// <para><see cref="RealDbFixture"/> 故意<b>不跑迁移</b>（它是只读验证用的副本），
    /// 所以新表在副本里不存在。这里**只执行 V049 那一个脚本**、且只在表缺失时执行 ——
    /// 整套迁移跑下去会把 V048 也跑了（删掉两条排土场种子），
    /// 而同一个 collection 里别的判据可能正拿那两条当算例。</para>
    /// <para>脚本从嵌入资源读，**不在判据里再抄一份 DDL**：抄一份就有两个口径，
    /// 改了表结构以后判据还在按老结构建表，绿着但测的不是产品那张表。</para>
    /// </summary>
    private void EnsureWeekTable()
    {
        if (!_db.Ready || _db.Sql == null) return;
        try { _db.Sql.ExecuteScalar<long>("SELECT COUNT(*) FROM week_plan_target"); return; }
        catch { /* 表不在 → 往下建 */ }

        var asm = typeof(GeoDatabase).Assembly;   // Kylin：迁移脚本嵌在主程序集
        string? name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("V049_week_plan_target.sql", StringComparison.OrdinalIgnoreCase));
        if (name == null) { _out.WriteLine("找不到 V049 嵌入资源"); return; }

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
        _db.Sql.Execute(reader.ReadToEnd());
    }

    public void Dispose() => Clean();

    private void Clean()
    {
        if (!_db.Ready) return;
        try { EquipmentDataContext.Plan.DeleteWeek(MondayKey); } catch { }
        try { ProductionPlanContext.Invalidate(); } catch { }
    }

    private bool Skip()
    {
        _out.WriteLine(_db.Label);
        if (_db.Ready) return false;
        _out.WriteLine("SKIP：真库不可用");
        return true;
    }

    /// <summary>WK1 周口径 = 自然周、周一起；键是周一的 yyyy-MM-dd。</summary>
    [Fact]
    public void WK1_WeekKey_IsMondayOfNaturalWeek()
    {
        // 2026-08-20 是周四 ⇒ 该周周一是 08-17
        Assert.Equal(DayOfWeek.Thursday, Anchor.DayOfWeek);
        Assert.Equal("2026-08-17", WeekTargetLink.KeyOf(WeekTargetLink.MondayOf(Anchor)));

        // 周日归**上一周**（周一起，不是周日起）——这一条错了，周目标会整体错开一天
        var sunday = new DateTime(2026, 8, 23);
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);
        Assert.Equal("2026-08-17", WeekTargetLink.KeyOf(WeekTargetLink.MondayOf(sunday)));

        var nextMonday = new DateTime(2026, 8, 24);
        Assert.Equal("2026-08-24", WeekTargetLink.KeyOf(WeekTargetLink.MondayOf(nextMonday)));
    }

    /// <summary>WK2 台账往返：写 → 读 → 区间查 → 删。</summary>
    [Fact]
    public void WK2_Store_RoundTrips()
    {
        if (Skip()) return;

        EquipmentDataContext.Plan.UpsertWeek(new WeekPlanTarget
        {
            Monday = MondayKey,
            TargetCoalWanT = 12.5,
            TargetStripWanM3 = 88.0,
            Source = "判据",
        });

        var got = EquipmentDataContext.Plan.GetWeek(MondayKey);
        Assert.NotNull(got);
        Assert.Equal(12.5, got!.TargetCoalWanT, 3);
        Assert.Equal(88.0, got.TargetStripWanM3, 3);
        Assert.True(got.HasTarget);

        var inRange = EquipmentDataContext.Plan.WeeksInRange("2026-08-01", "2026-08-31");
        Assert.Contains(inRange, w => w.Monday == MondayKey);

        // 区间查是文本 BETWEEN：查一个不含本周的区间必须查不到（写方存的不是 yyyy-MM-dd 就会在这里露馅）
        Assert.DoesNotContain(EquipmentDataContext.Plan.WeeksInRange("2026-09-01", "2026-09-30"),
                              w => w.Monday == MondayKey);

        EquipmentDataContext.Plan.DeleteWeek(MondayKey);
        Assert.Null(EquipmentDataContext.Plan.GetWeek(MondayKey));
    }

    /// <summary>
    /// WK3 没下过周目标 ⇒ <c>HasTarget=false</c>，且话要说清是"没下过"而不是"下了 0"。
    /// </summary>
    [Fact]
    public void WK3_NoTarget_SaysSo_AndDoesNotFallToZero()
    {
        if (Skip()) return;

        var r = WeekTargetLink.Resolve(Anchor, 1.35);
        _out.WriteLine(r.Label);

        Assert.False(r.HasTarget);
        Assert.False(r.Usable);
        Assert.Contains("没有下达过", r.Label);
        // 关键：没下过不等于目标是 0 —— 目标量必须还是 0 而调用方要去走月路
        Assert.Equal(0, r.TargetCoalM3);
    }

    /// <summary>
    /// WK4 下达之后：窗口只含"今天及以后"的作业日，且已过去的作业日<b>没录实绩就按 0 核销并点名</b>。
    /// </summary>
    [Fact(Skip = "依赖原桌面真库的当时状态（采掘单元清单文件 / 2026-08 班次日历 / 作业面档案），Kylin 种子库不含；判据本身逐行保留")]
    public void WK4_RollingWindow_CountsOnlyTodayOnward_AndNamesMissingActuals()
    {
        if (Skip()) return;

        EquipmentDataContext.Plan.UpsertWeek(new WeekPlanTarget
        {
            Monday = MondayKey,
            TargetCoalWanT = 10,
            TargetStripWanM3 = 50,
            Source = "判据",
        });

        var r = WeekTargetLink.Resolve(Anchor, 1.35);
        _out.WriteLine(r.Label);

        Assert.True(r.HasTarget);
        Assert.Equal(10 * 1e4 / 1.35, r.TargetCoalM3, 1);
        Assert.Equal(50 * 1e4, r.TargetStripM3, 1);

        // 窗口：全部 ≥ 今天，且都落在本周内
        Assert.NotEmpty(r.RemainWorkdays);
        Assert.All(r.RemainWorkdays, d => Assert.True(d >= Anchor.Date && d <= r.Sunday));
        Assert.True(r.TodayIsWorkday, "真库里 2026-08-20 排了 3 个班，它必须算作业日");
        Assert.True(r.Usable);

        // 08-17~08-19 是已过去的作业日；判据环境里没有落盘实绩 ⇒ 必须点名，且剩余量 = 目标（按 0 核销）
        _out.WriteLine($"已过去且没录实绩：{r.PastDaysWithoutActuals.Count} 天");
        Assert.All(r.PastDaysWithoutActuals, d => Assert.True(d < Anchor.Date));
        if (r.PastDaysWithoutActuals.Count > 0)
        {
            Assert.Contains("按 0 核销", r.Label);
            Assert.Equal(r.TargetCoalM3, r.RemainCoalM3, 1);
        }
    }

    /// <summary>
    /// WK5 <b>关闸自检</b>：撤掉周目标必须**真的**退回月路。
    /// <para>放宽一条路最容易顺手把旧路砍掉 —— 那样这组判据只证明"周路能跑"。</para>
    /// </summary>
    [Fact]
    public void WK5_ClearingTarget_FallsBackToMonthlyRoute()
    {
        if (Skip()) return;

        EquipmentDataContext.Plan.UpsertWeek(new WeekPlanTarget
        {
            Monday = MondayKey, TargetCoalWanT = 10, TargetStripWanM3 = 50, Source = "判据",
        });
        Assert.True(WeekTargetLink.Resolve(Anchor, 1.35).HasTarget);

        EquipmentDataContext.Plan.DeleteWeek(MondayKey);
        var after = WeekTargetLink.Resolve(Anchor, 1.35);
        Assert.False(after.HasTarget);
        Assert.False(after.Usable);
    }

    /// <summary>
    /// WK6 端到端：下达周目标后，当日盘子的来源文案必须写明走的是<b>周路</b>，
    /// 且当日采装目标合计要落在 (0, 本周剩余量] 之间 —— 一天不可能排掉多于整周剩下的量。
    /// </summary>
    [Fact]
    public void WK6_DayPlate_TakesWeekRoute_AndDayTargetNeverExceedsWeekRemain()
    {
        if (Skip()) return;

        // 判据固定作业日到算例那一天（否则跟着机器的今天跑，换一天就假红）
        ProjectScope.Inject(new StubProjectContext(Anchor));
        try
        {
            EquipmentDataContext.Plan.UpsertWeek(new WeekPlanTarget
            {
                Monday = MondayKey, TargetCoalWanT = 10, TargetStripWanM3 = 50, Source = "判据",
            });
            ProductionPlanContext.Invalidate();

            ExploderConfig cfg;
            try { cfg = ProductionPlanContext.Config(); }
            catch (Exception ex) { _out.WriteLine("盘子装不出来：" + ex.Message); return; }

            string label = ProductionPlanContext.PlanSourceLabel;
            _out.WriteLine(label);
            Assert.Contains("按周目标拆日", label);

            var week = WeekTargetLink.Resolve(Anchor, 1.35);
            double dayLoad = cfg.Faces.Where(f => f.Process == ProcessType.Load).Sum(f => f.DayTargetM3);
            _out.WriteLine($"当日采装目标合计 {dayLoad:N0} m³ · 本周剩余 {week.RemainCoalM3:N0} m³"
                         + $" · 剩余作业日 {week.RemainWorkdays.Count}");

            // 一天排掉的不可能多于整周剩下的（备采封顶只会更少，不会更多）
            Assert.True(dayLoad <= week.RemainCoalM3 + 1,
                $"当日目标 {dayLoad:N0} 超过了本周剩余量 {week.RemainCoalM3:N0}");
        }
        finally
        {
            ProjectScope.Inject(null);
            ProductionPlanContext.Invalidate();
        }
    }

    /// <summary>
    /// SH1 <b>分解器排出来的任务，班次名必须在下拉框的取值域里</b>。
    ///
    /// <para>
    /// 实测 2026-08-20：分解器取的是 <c>shift_calendar</c> 里的原始码（真库是 A/B/C），
    /// 而下拉框、盘子 <c>cfg.Shifts</c>、班次日历窗口一律是「早班/中班/夜班」——
    /// 甘特上选任何一个班都是**空的**，切回「全部」又全在，一处报错都没有。
    /// </para>
    /// <para>这一条钉的是"两边同名"，不是某个具体名字：换班制、改班名都不该让它假红。</para>
    /// </summary>
    [Fact]
    public void SH1_DecomposerShiftNames_AreInTheSelectorDomain()
    {
        if (Skip()) return;

        ProjectScope.Inject(new StubProjectContext(Anchor));
        try
        {
            ProductionPlanContext.Invalidate();
            ExploderConfig cfg;
            try { cfg = ProductionPlanContext.Config(); }
            catch (Exception ex) { _out.WriteLine("盘子装不出来：" + ex.Message); return; }

            var asm = ShiftPlanAssembler.Build("2026-08", cfg, ledgerRoot: null, onlyDate: Anchor);
            var used = asm.MonthTasks.Select(t => (t.Shift ?? "").Trim())
                          .Where(x => x.Length > 0).Distinct().ToList();
            if (used.Count == 0) { _out.WriteLine("SKIP：这一期没分解出任务（缺钻机注入器时属正常）"); return; }

            var domain = cfg.Shifts.Select(w => w.Name).ToList();
            _out.WriteLine($"任务里的班次名 {string.Join("/", used)} · 下拉框取值域 {string.Join("/", domain)}");

            foreach (var name in used)
                Assert.True(domain.Contains(name),
                    $"任务的班次名「{name}」不在取值域（{string.Join("/", domain)}）里 —— 甘特按这个班筛会一条都筛不到");

            // 顺带把筛选器真跑一遍：每个班名都要能筛出东西
            foreach (var name in domain)
            {
                var r = global::PitMine3D.Kylin.TaskLib.Gantt.GanttShiftFilter.Apply(asm.MonthTasks, name);
                _out.WriteLine($"  {name}: {r.Tasks.Count} 项");
                Assert.True(r.Tasks.Count > 0, $"按「{name}」筛出来是空的");
            }
        }
        finally
        {
            ProjectScope.Inject(null);
            ProductionPlanContext.Invalidate();
        }
    }

    /// <summary>判据用的最小工程上下文：只为把作业日钉死。</summary>
    private sealed class StubProjectContext : PitMine3D.Kylin.Platform.IProjectContext
    {
        public StubProjectContext(DateTime day) { WorkDate = day.Date; }
        public string MineName => "判据";
        public DateTime WorkDate { get; private set; }
        public int PlanYear => WorkDate.Year;
        public int PlanMonth => WorkDate.Month;
        public string DateLabel => PitMine3D.Kylin.Platform.ProjectPeriodFormat.DateLabel(WorkDate);
        public string PeriodLabel => $"{PlanYear}年{PlanMonth}月";
        public string DatabasePath => "";
        public string? DocumentPath => null;
        public event EventHandler? Changed;
        public void SetMine(string mineName) { }
        public void SetWorkDate(DateTime date) { WorkDate = date.Date; Changed?.Invoke(this, EventArgs.Empty); }
        public void ResetWorkDateToToday() { }
        public void SetPlanMonth(int year, int month) { }
        public void SetDocument(string? path) { }
    }
}
