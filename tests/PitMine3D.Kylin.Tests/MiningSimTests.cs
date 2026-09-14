using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 采剥模拟引擎数值核（§三二四，忠实原 <c>MiningSimEngine</c>）。
/// 这组测试盯的是原版注释里点名过的几个坑：推进方向必须取**短轴**、范围取**分位**而非 min/max、
/// 下台阶滞后但末端要收缩到 0（否则 p=1 采不完）、内排**严格滞后**工作面、外排按**自身**范围渐进。
/// </summary>
public class MiningSimTests
{
    /// <summary>造一个长条采场：沿 X 长 200m、沿 Y 宽 60m、6 层台阶，下两层是煤。</summary>
    private static List<MiningSim.Cell> Pit(double x0 = 0, double y0 = 0)
    {
        var cells = new List<MiningSim.Cell>();
        for (int ix = 0; ix < 20; ix++)
            for (int iy = 0; iy < 6; iy++)
                for (int iz = 0; iz < 6; iz++)
                    cells.Add(new MiningSim.Cell(
                        x0 + ix * 10 + 5, y0 + iy * 10 + 5, iz * 10 + 5,
                        10, 10, 10, IsCoal: iz < 2));   // 低层(iz 小)是煤
        return cells;
    }

    // ── 推进坐标系 ────────────────────────────────────────────
    [Fact]
    public void 推进方向取短轴而不是长轴()
    {
        // 采场沿 X 长、沿 Y 窄 → 长轴是 X, 工作面平行 X, 推进沿 Y。
        // 取成长轴的话工作面会与工作线错位(原注释点名的坑)。
        var f = MiningSim.BuildFrame(Pit());
        Assert.True(Math.Abs(f.Uy) > 0.99, $"推进方向应贴近 ±Y, 实际 ({f.Ux:0.###},{f.Uy:0.###})");
        Assert.True(Math.Abs(f.Ux) < 0.01);
    }

    [Fact]
    public void 范围取分位剔掉离群块()
    {
        // 在 200m 外扔一个孤立块: 用 min/max 会把推进轴拉到两倍长, 分位应基本不动
        var withOutlier = Pit();
        withOutlier.Add(new MiningSim.Cell(100, 400, 5, 10, 10, 10, false));
        double spanClean = MiningSim.BuildFrame(Pit()) is var a ? a.MainMax - a.MainMin : 0;
        double spanDirty = MiningSim.BuildFrame(withOutlier) is var b ? b.MainMax - b.MainMin : 0;
        Assert.True(spanDirty < spanClean * 1.2, $"离群块把推进轴拉长了: {spanClean:0.#} → {spanDirty:0.#}");
    }

    [Fact]
    public void 起推方向由内排位置定()
    {
        var pit = Pit();
        var f0 = MiningSim.BuildFrame(pit);
        // 内排放在推进轴的低端 → 正向; 放高端 → 反向
        var (lowX, lowY) = f0.ToWorld(f0.MainMin, 0);
        var (hiX, hiY) = f0.ToWorld(f0.MainMax, 0);
        var low = new List<MiningSim.Cell> { new(lowX, lowY, 5, 10, 10, 10, false) };
        var hi = new List<MiningSim.Cell> { new(hiX, hiY, 5, 10, 10, 10, false) };
        Assert.True(MiningSim.BuildFrame(pit, low).Forward);
        Assert.False(MiningSim.BuildFrame(pit, hi).Forward);
    }

    [Fact]
    public void 空采场不崩且给可用的默认坐标系()
    {
        var f = MiningSim.BuildFrame(new List<MiningSim.Cell>());
        Assert.True(f.StripW > 0);
        Assert.True(f.CrossBandW > 0);
        Assert.InRange(f.OrderOf(0), 0, MiningSim.PitStripTarget - 1);
    }

    // ── 分桶 ──────────────────────────────────────────────────
    [Fact]
    public void 采场分桶_每块只进一个桶且煤岩方量分记()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out double coal, out double waste);

        Assert.Equal(pit.Count, strips.Sum(s => s.CellIndices.Count));                 // 不重不漏
        Assert.Equal(pit.Count, strips.SelectMany(s => s.CellIndices).Distinct().Count());
        Assert.Equal(pit.Sum(c => c.Volume), coal + waste, 6);
        Assert.Equal(pit.Where(c => c.IsCoal).Sum(c => c.Volume), coal, 6);
        Assert.All(strips, s => Assert.InRange(s.BenchZ, 0, MiningSim.NBench - 1));
        Assert.All(strips, s => Assert.InRange(s.Order, 0, MiningSim.PitStripTarget - 1));
    }

    [Fact]
    public void 台阶层0是最上层()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        // 顶层块(z=55)所在桶的 BenchZ 应为 0, 底层块(z=5)应为最大层
        double TopZ(MiningSim.PitStrip s) => s.CellIndices.Max(i => pit[i].Z);
        var top = strips.OrderByDescending(TopZ).First();
        var bottom = strips.OrderBy(s => s.CellIndices.Min(i => pit[i].Z)).First();
        Assert.Equal(0, top.BenchZ);
        Assert.Equal(MiningSim.NBench - 1, bottom.BenchZ);
    }

    // ── 逐帧推进 ──────────────────────────────────────────────
    [Fact]
    public void 进度0一块没采_进度1全采完()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out double coalTot, out double wasteTot);

        var r0 = MiningSim.Step(0, strips, new List<MiningSim.DumpLayer>());
        Assert.All(strips, s => Assert.True(s.Visible));
        Assert.Equal(0, r0.CoalVolM3);
        Assert.Equal(0, r0.WasteVolM3);

        var r1 = MiningSim.Step(1, strips, new List<MiningSim.DumpLayer>());
        // 末端滞后收缩到 0 —— 不收缩的话最下台阶永远差几带采不完
        Assert.All(strips, s => Assert.False(s.Visible));
        Assert.Equal(coalTot, r1.CoalVolM3, 6);
        Assert.Equal(wasteTot, r1.WasteVolM3, 6);
    }

    [Fact]
    public void 采出方量随进度单调不减()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        double prev = -1;
        for (double p = 0; p <= 1.0001; p += 0.05)
        {
            var r = MiningSim.Step(Math.Min(p, 1), strips, new List<MiningSim.DumpLayer>());
            double got = r.CoalVolM3 + r.WasteVolM3;
            Assert.True(got >= prev - 1e-9, $"p={p:0.##} 采出量倒退了 {prev:0.#} → {got:0.#}");
            prev = got;
        }
    }

    [Fact]
    public void 下台阶滞后上台阶_剥采平行()
    {
        // 同一采掘带里, 推进到一半时上台阶该已采、下台阶该还在
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        MiningSim.Step(0.5, strips, new List<MiningSim.DumpLayer>());

        var byOrder = strips.GroupBy(s => s.Order).First(g => g.Select(x => x.BenchZ).Distinct().Count() >= MiningSim.NBench);
        var top = byOrder.OrderBy(s => s.BenchZ).First();
        var bot = byOrder.OrderByDescending(s => s.BenchZ).First();
        Assert.True(bot.BenchZ > top.BenchZ);
        // 上台阶先没(被采掉)、下台阶还在 —— 若两者同进退, 就不是台阶状开采
        Assert.True(!top.Visible || bot.Visible, "下台阶不该比上台阶先采");
    }

    [Fact]
    public void 内排严格滞后工作面()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        // 内排块摆在采场同一片范围里(采空区回填)
        var inner = pit.Select(c => new MiningSim.Cell(c.X, c.Y, c.Z, c.Sx, c.Sy, c.Sz, false)).ToList();
        var dumps = MiningSim.BuildDump(inner, f, type: 1, out _);

        // 刚起步时不该有任何内排冒出来 —— 采都没采空, 先填上就是穿帮
        MiningSim.Step(0.01, strips, dumps);
        Assert.All(dumps, d => Assert.False(d.Visible));

        // Lag/BandLen 之前一律不出现
        double gate = (double)MiningSim.Lag / MiningSim.BandLen;
        MiningSim.Step(gate * 0.5, strips, dumps);
        Assert.All(dumps, d => Assert.False(d.Visible));
    }

    [Fact]
    public void 排土从下往上长()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        var inner = pit.Select(c => new MiningSim.Cell(c.X, c.Y, c.Z, c.Sx, c.Sy, c.Sz, false)).ToList();
        var dumps = MiningSim.BuildDump(inner, f, type: 1, out _);

        MiningSim.Step(0.9, strips, dumps);
        foreach (var g in dumps.GroupBy(d => d.Order))
        {
            // 注意：并非每个 ZOrder 都一定有桶（块体的 Z 层数未必等于 DumpZLayers），
            // 所以要比的是"露出来的层是**已有桶**按高度排序的一个前缀"，不是 0..n-1 这串数。
            var existing = g.Select(d => d.ZOrder).OrderBy(z => z).ToList();
            var shown = g.Where(d => d.Visible).Select(d => d.ZOrder).OrderBy(z => z).ToList();
            if (shown.Count == 0) continue;
            // 从下往上长：不能下面空着、上面浮着一层
            Assert.Equal(existing.Take(shown.Count).ToList(), shown);
        }
    }

    [Fact]
    public void 外排按自身范围渐进而不是一次全冒()
    {
        // 外排场整个落在采场推进范围**之外** —— 若照采场口径算序号, 会被夹到同一个值上, 于是一次全冒出来
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        var far = new List<MiningSim.Cell>();
        for (int i = 0; i < 20; i++)
            for (int z = 0; z < 4; z++)
                far.Add(new MiningSim.Cell(-300 - i * 10, 500 + i * 10, z * 10 + 5, 10, 10, 10, false));
        var dumps = MiningSim.BuildDump(far, f, type: 0, out _);

        Assert.True(dumps.Select(d => d.Order).Distinct().Count() > 1, "外排全挤到一个条带序上了");

        MiningSim.Step(0.4, strips, dumps);
        int mid = dumps.Count(d => d.Visible);
        MiningSim.Step(0.9, strips, dumps);
        int late = dumps.Count(d => d.Visible);
        Assert.True(mid > 0 && mid < dumps.Count, $"0.4 时外排应只堆了一部分, 实际 {mid}/{dumps.Count}");
        Assert.True(late > mid, "外排没有随进度继续堆进");
    }

    [Fact]
    public void 反向开关把推进顺序倒过来()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        var noDump = new List<MiningSim.DumpLayer>();

        MiningSim.Step(0.3, strips, noDump);
        var minedFwd = strips.Where(s => !s.Visible).Select(s => s.Order % MiningSim.BandLen).DefaultIfEmpty(-1).Max();
        MiningSim.Step(0.3, strips, noDump, reverse: true);
        var minedRev = strips.Where(s => !s.Visible).Select(s => s.Order % MiningSim.BandLen).DefaultIfEmpty(-1).Min();

        Assert.True(minedFwd >= 0 && minedRev >= 0);
        Assert.True(minedRev > minedFwd, "反向时该从带的另一头开采");
    }

    [Fact]
    public void 读数里的带数与总带数对得上()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var strips = MiningSim.BuildPit(pit, f, out _, out _);
        var r = MiningSim.Step(1, strips, new List<MiningSim.DumpLayer>());
        Assert.Equal(MiningSim.PitStripTarget, r.TotalStrips);
        Assert.Equal(MiningSim.BandLen * MiningSim.PanelCount, r.MinedStrips);
    }

    // ── 外排 → 内排 切换点 ────────────────────────────────────
    // ── 播放时钟 ──────────────────────────────────────────────
    [Fact]
    public void 播放时钟_不循环时停在末帧()
    {
        var pb = new MiningSim.Playback { Speed = 1000 };   // 一拍 0.06/45×1000 > 1, 一拍就冲过头, 专打边界
        pb.Play();
        pb.Tick();
        Assert.Equal(1.0, pb.Progress);
        Assert.False(pb.Playing);      // 到头即停, 不是继续空转
    }

    [Fact]
    public void 播放时钟_循环时绕回0而不是卡在1()
    {
        var pb = new MiningSim.Playback { Speed = 1000, Loop = true };
        pb.Play();
        pb.Tick();
        Assert.Equal(0.0, pb.Progress);
        Assert.True(pb.Playing);
    }

    [Fact]
    public void 播放时钟_播完再点播放从头播()
    {
        var pb = new MiningSim.Playback { Speed = 1000 };
        pb.Play();
        pb.Tick();
        Assert.Equal(1.0, pb.Progress);
        pb.Play();                      // 原版 Play() 第一句就是这条
        Assert.Equal(0.0, pb.Progress);
        Assert.True(pb.Playing);
    }

    [Fact]
    public void 播放时钟_拖动进度即暂停()
    {
        var pb = new MiningSim.Playback();
        pb.Play();
        pb.Seek(0.4);
        Assert.False(pb.Playing);       // 拖动即暂停, 便于定格观察
        Assert.Equal(0.4, pb.Progress, 9);
    }

    [Fact]
    public void 播放时钟_速度按倍数走且暂停时不动()
    {
        var a = new MiningSim.Playback { Speed = 1 };
        var b = new MiningSim.Playback { Speed = 2 };
        a.Play(); b.Play();
        a.Tick(); b.Tick();
        Assert.Equal(a.Progress * 2, b.Progress, 9);

        var c = new MiningSim.Playback { Speed = 1 };
        c.Tick();                        // 没点播放
        Assert.Equal(0.0, c.Progress);
    }

    [Fact]
    public void 播放时钟_一倍速跑完全程约45秒()
    {
        var pb = new MiningSim.Playback { Speed = 1 };
        pb.Play();
        int ticks = 0;
        while (pb.Progress < 1.0 && ticks < 100000) { pb.Tick(); ticks++; }
        double sec = ticks * MiningSim.Playback.TickMs / 1000.0;
        Assert.InRange(sec, 44.0, 46.0);
    }

    [Fact]
    public void 无内排时各标段恒走外排()
    {
        var f = MiningSim.BuildFrame(Pit());
        var sw = MiningSim.SwitchPoints(f, new List<MiningSim.Cell>());
        Assert.Equal(MiningSim.PanelCount, sw.Length);
        Assert.All(sw, x => Assert.Equal(1.0, x));
    }

    [Fact]
    public void 有内排时切换点夹在给定区间内()
    {
        var pit = Pit();
        var f = MiningSim.BuildFrame(pit);
        var inner = pit.Select(c => new MiningSim.Cell(c.X, c.Y, c.Z, c.Sx, c.Sy, c.Sz, false)).ToList();
        var sw = MiningSim.SwitchPoints(f, inner);
        Assert.All(sw, x => Assert.InRange(x, 0.10, 0.75));
    }
}
