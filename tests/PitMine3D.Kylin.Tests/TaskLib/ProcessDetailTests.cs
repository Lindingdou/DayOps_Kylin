// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ProcessDetailTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.ShiftOps;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 工序内部细衔接预测的判据（P 组）。
///
/// <para>这一层把"配 2 车 &lt; 荐 6 车 ⇒ 铲将待车"这句定性话算成节拍：
/// 铲装满一台要 t_load，车每隔 W/n 回来一台，谁慢谁定速。
/// 它是**解析解不是随机模拟** —— 同样输入永远同样的数，所以每条都判得死。</para>
/// </summary>
public sealed class ProcessDetailTests
{
    private readonly ITestOutputHelper _out;
    public ProcessDetailTests(ITestOutputHelper o) => _out = o;

    private static ProcessLine Line(int trucks, double km, double spanH = 8, int rec = 6)
        => new()
        {
            Zone = "主采面·东", Shovel = "WK-10",
            Trucks = Enumerable.Range(1, trucks).Select(i => $"T-{i:00}").ToList(),
            RecommendedTrucks = rec,
            StartHour = 0, EndHour = spanH,
            HaulKm = km,
            TargetM3 = 10000,
        };

    // ── 节拍 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void P1_周转时间就是各段之和()
    {
        var d = new LineDetail(Line(4, 2.2)).Predict();
        var p = d.Params;
        double want = (p.SpotMin + p.LoadMin + p.UnloadMin) / 60.0 + 2.2 / p.FullKmh + 2.2 / p.EmptyKmh;
        _out.WriteLine($"周转 {d.CycleH * 60:0.###} min　期望 {want * 60:0.###} min");
        Assert.Equal(want, d.CycleH, 9);
    }

    [Fact]
    public void P2_车少就铲等车_车多就车排队()
    {
        var few = new LineDetail(Line(1, 2.2)).Predict();
        var many = new LineDetail(Line(20, 2.2)).Predict();
        _out.WriteLine($"1 车：铲节拍 {few.ShovelTactH * 60:0.##} min　车节拍 {few.TruckTactH * 60:0.##} min ⇒ {few.Kind.Label()}");
        _out.WriteLine($"20车：铲节拍 {many.ShovelTactH * 60:0.##} min　车节拍 {many.TruckTactH * 60:0.##} min ⇒ {many.Kind.Label()}");

        Assert.Equal(Bottleneck.ShovelWaitsTruck, few.Kind);
        Assert.True(few.ShovelWaitH > 0);
        Assert.Equal(0.0, few.TruckQueueH, 9);

        Assert.Equal(Bottleneck.TruckWaitsShovel, many.Kind);
        Assert.True(many.TruckQueueH > 0);
        Assert.Equal(0.0, many.ShovelWaitH, 9);
    }

    /// <summary>车数正好把两个节拍配平时，两侧等待都必须归零 —— 否则模型里有恒定的假等待。</summary>
    [Fact]
    public void P3_节拍配平时两侧都不等()
    {
        // 找到让 W/n 恰好等于 t_load 的车数（取整后最接近的那个）
        var probe = new LineDetail(Line(1, 2.2)).Predict();
        int nStar = (int)Math.Round(probe.CycleH / probe.ShovelTactH);
        var d = new LineDetail(Line(nStar, 2.2)).Predict();
        _out.WriteLine($"n*={nStar}　铲节拍 {d.ShovelTactH * 60:0.###}　车节拍 {d.TruckTactH * 60:0.###}"
                     + $"　铲空转 {d.ShovelWaitH * 60:0.##} min　车排队 {d.TruckQueueH * 60:0.##} min");
        Assert.True(Math.Min(d.ShovelWaitH, d.TruckQueueH) < 1e-9, "两侧不可能同时在等");
        Assert.True(d.ShovelUtil > 0.85, $"配平附近铲作业率应当很高，实得 {d.ShovelUtil:P0}");
    }

    [Fact]
    public void P4_加车能减少铲空转_直到配平为止()
    {
        double last = double.MaxValue;
        for (int n = 1; n <= 6; n++)
        {
            var d = new LineDetail(Line(n, 2.2)).Predict();
            _out.WriteLine($"  {n} 车 ⇒ 铲空转 {d.ShovelWaitH * 60:0.#} min　趟数 {d.Trips}　出方 {d.PredictedM3:0} m³");
            Assert.True(d.ShovelWaitH <= last + 1e-9, $"加到 {n} 车反而更空转了");
            last = d.ShovelWaitH;
        }
    }

    // ── 段展开 ───────────────────────────────────────────────────────────────

    [Fact]
    public void P5_每趟的段首尾相接且不越界()
    {
        var d = new LineDetail(Line(3, 2.2)).Predict(maxTrips: 12);
        Assert.NotEmpty(d.Segments);

        var byTrip = d.Segments.GroupBy(s => s.TripIndex).OrderBy(g => g.Key).ToList();
        foreach (var g in byTrip)
        {
            var seq = g.OrderBy(s => s.StartH).ToList();
            for (int i = 0; i + 1 < seq.Count; i++)
                Assert.Equal(seq[i].EndH, seq[i + 1].StartH, 9);
        }
        _out.WriteLine($"展开 {byTrip.Count} 趟 · {d.Segments.Count} 段");
        Assert.All(d.Segments, s => Assert.True(s.StartH >= d.Line.StartHour - 1e-9));
    }

    /// <summary>一趟料只在**装车**那一段记量 —— 别处也记就会把同一批料记两遍。</summary>
    [Fact]
    public void P6_量只记在装车段()
    {
        var d = new LineDetail(Line(3, 2.2)).Predict(maxTrips: 10);
        foreach (var s in d.Segments)
            if (s.Act != CycleAct.LoadTruck) Assert.Equal(0.0, s.VolumeM3, 9);

        double perTrip = d.Segments.Where(s => s.Act == CycleAct.LoadTruck).Select(s => s.VolumeM3).Distinct().Single();
        _out.WriteLine($"单车装载 {perTrip} m³");
        Assert.Equal(d.Params.TruckM3, perTrip, 9);
    }

    [Fact]
    public void P7_行车段的距离与速度要对得上时长()
    {
        var d = new LineDetail(Line(3, 2.2)).Predict(maxTrips: 4);
        foreach (var s in d.Segments.Where(s => s.Act is CycleAct.HaulFull or CycleAct.HaulEmpty))
        {
            double want = s.DistanceKm / s.SpeedKmh;
            _out.WriteLine($"  {s.Act.Label()}　{s.DistanceKm:0.##} km @ {s.SpeedKmh:0} ⇒ {s.DurationH * 60:0.###} min");
            Assert.Equal(want, s.DurationH, 9);
        }
    }

    // ── 边界 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void P8_一台车都没配就是零产_且说明白()
    {
        var d = new LineDetail(Line(0, 2.2)).Predict();
        _out.WriteLine("  " + string.Join(" │ ", d.Notes));
        Assert.Equal(0, d.Trips);
        Assert.Equal(0.0, d.PredictedM3, 9);
        Assert.Equal(Bottleneck.ShovelWaitsTruck, d.Kind);
        Assert.Contains(d.Notes, n => n.Contains("一台车都没配"));
    }

    [Fact]
    public void P9_运距为零要提示预测偏高()
    {
        var d = new LineDetail(Line(3, 0)).Predict();
        _out.WriteLine("  " + string.Join(" │ ", d.Notes));
        Assert.Contains(d.Notes, n => n.Contains("运距为 0"));
        Assert.DoesNotContain(d.Segments, s => s.Act is CycleAct.HaulFull or CycleAct.HaulEmpty);
    }

    /// <summary>展开条数只截断**画图**，不许改总量 —— 截断了账就是错的。</summary>
    [Fact]
    public void P10_展开条数不影响总量()
    {
        var a = new LineDetail(Line(3, 2.2)).Predict(maxTrips: 5);
        var b = new LineDetail(Line(3, 2.2)).Predict(maxTrips: 500);
        _out.WriteLine($"  展 5 趟：Trips={a.Trips} 出方={a.PredictedM3:0}　展 500 趟：Trips={b.Trips} 出方={b.PredictedM3:0}");
        Assert.Equal(b.Trips, a.Trips);
        Assert.Equal(b.PredictedM3, a.PredictedM3, 9);
        Assert.True(a.Segments.Count < b.Segments.Count, "算例前提：5 趟确实比 500 趟展得少");
    }

    /// <summary>
    /// P11 时刻查询：每一段在它自己的中点必须"在飞"，且**铲一次只干一件事**。
    ///
    /// <para>第一版这条判的是"At(中点) 返回的就是这一段"，直接红了 ——
    /// 因为 n 台车同时跑，一个时刻本来就有好几段在飞（一台在装、一台在重车行、一台在卸），
    /// "返回第一条"取决于列表顺序，语义不成立。多车并行正是配车的意义，
    /// 模型不该把它压成单条。于是拆成 ActiveAt（复数）/ ShovelActAt（单数）。</para>
    /// </summary>
    [Fact]
    public void P11_时刻查询要分清在飞的与铲在干的()
    {
        var d = new LineDetail(Line(3, 2.2)).Predict(maxTrips: 6);

        foreach (var s in d.Segments.Take(14))
        {
            double mid = (s.StartH + s.EndH) / 2;
            Assert.Contains(d.ActiveAt(mid), x => ReferenceEquals(x, s));
        }

        // 铲一次只能干一件事：任意时刻铲侧的段最多一条
        for (int k = 0; k <= 200; k++)
        {
            double t = d.Line.StartHour + d.Line.DurationH * k / 200.0;
            int shovelSide = d.ActiveAt(t).Count(s =>
                s.Act is CycleAct.ShovelWait or CycleAct.Spot or CycleAct.LoadTruck);
            Assert.True(shovelSide <= 1, $"{t:0.###} h 时铲侧有 {shovelSide} 段同时在飞 —— 铲不能分身");
        }

        // 多车确实并行（否则上面那条约束是空的）
        int maxParallel = Enumerable.Range(0, 200)
            .Select(k => d.ActiveAt(d.Line.StartHour + d.Line.DurationH * k / 200.0).Count())
            .Max();
        _out.WriteLine($"同时在飞的段数峰值 {maxParallel}");
        Assert.True(maxParallel >= 2, "算例前提：3 车确实会并行，否则这条判据没在判什么");

        Assert.Empty(d.ActiveAt(d.Line.StartHour - 0.5));
        Assert.Null(d.ShovelActAt(d.Line.StartHour - 0.5));
    }

    [Fact]
    public void P12_时间去向汇总要占满且比例合一()
    {
        var d = new LineDetail(Line(2, 2.2)).Predict(maxTrips: 20);
        var bd = d.Breakdown().ToList();
        foreach (var b in bd) _out.WriteLine($"  {b.Act.Label(),-8} {b.Hours * 60,7:0.#} min　{b.Share:P1}");
        Assert.Equal(1.0, bd.Sum(b => b.Share), 6);
        Assert.Contains(bd, b => b.Act == CycleAct.ShovelWait);   // 2 车确实会空转
    }

    /// <summary>
    /// P0 自检：把车数从"不足"调到"配平"，铲空转必须**真的**从有到无。
    /// 否则 P2/P4 那几条就是恒真的。
    /// </summary>
    [Fact]
    public void P0_自检_车配够了空转必须消失()
    {
        var few = new LineDetail(Line(1, 2.2)).Predict();
        Assert.True(few.ShovelWaitH > 1e-6);          // 前提：确实红

        var probe = new LineDetail(Line(1, 2.2)).Predict();
        int nStar = (int)Math.Ceiling(probe.CycleH / probe.ShovelTactH);
        var ok = new LineDetail(Line(nStar, 2.2)).Predict();
        _out.WriteLine($"  1 车空转 {few.ShovelWaitH * 60:0.#} min ⇒ {nStar} 车空转 {ok.ShovelWaitH * 60:0.#} min");
        Assert.True(ok.ShovelWaitH < 1e-9, "车配够之后铲不该再空转 ⇒ 判据不是恒真");
    }
}
