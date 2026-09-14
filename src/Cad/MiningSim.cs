using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 露天矿「采剥 → 排土」同步演示引擎（忠实原 <c>BlockModelLib.Simulation.MiningSimEngine</c> 的数值核）。
///
/// 原引擎 763 行里，**算法是纯托管的、显示才走 native**（<c>AcVxInterop.PitMine_VoxelRegisterModel /
/// VoxelSetVisible / VoxelRequestRender</c>）。这里按 [[unlock-blocked-insights]] 的老规矩拆开：
/// 分带 / 排序 / 台阶滞后 / 排土门控 / 方量统计 这一套**逐行照移**并可单测；
/// 谁该显示只出一份 <see cref="Frame_"/>…<see cref="Step"/> 的**可见性清单**，由调用方拿去驱动自己的场景
/// （Kylin 的块体是场景里的 <c>MeshEntity</c>，不需要 native 体素通道）。
///
/// 原版的口径（都在下面逐条标了出处）：
///   · <b>推进方向取短轴</b>：主轴(PCA 大特征向量)是采场**长轴**，标准条带开采是工作面平行长轴、
///     沿**短轴**推进；用世界 X/Y 推进会与工作线错位（原注释点名的坑）。
///   · <b>范围取 2%~98% 体积加权分位</b>，剔稀疏离群 —— 用 min/max 会被一两个飞点把整个推进轴拉长。
///   · <b>下台阶滞后上台阶</b>（<c>BenchLag</c>）：上覆岩先剥、下伏煤后采，剥采平行由此自然形成；
///     且末端滞后**收缩到 0**，保证 p=1 时各台阶全部采完（不收缩的话最下台阶永远差几带）。
///   · <b>内排严格门控在工作面之后</b>（<c>Lag</c> 条带，且 <c>Lag &gt; </c> 采煤滞后）：先彻底采空再回填，
///     绝不超前；外排沿自身范围渐进堆进；两者都从下向上一层层长。
/// </summary>
public static class MiningSim
{
    // ── 原版常量，逐个照搬（改任何一个都会改变演示节奏，故集中在此并标明含义）──
    /// <summary>采场沿推进轴切成多少条采掘带。</summary>
    public const int PitStripTarget = 48;
    /// <summary>横向分几个标段带（三个工作面并行下推）。</summary>
    public const int PanelCount = 3;
    /// <summary>每个标段带里的采掘带数。</summary>
    public const int BandLen = PitStripTarget / PanelCount;
    /// <summary>排土竖向层数（从下往上长）。</summary>
    public const int DumpZLayers = 8;
    /// <summary>内排滞后工作面的条带数 —— 必须大于采煤滞后：先彻底采空再回填。</summary>
    public const int Lag = 6;
    /// <summary>水平台阶(平盘)层数。</summary>
    public const int NBench = 6;
    /// <summary>每下一台阶滞后上台阶的条带数。</summary>
    public const int BenchLag = 1;
    /// <summary>一根排土柱从露头长到满层所占的进度窗口。</summary>
    public const double FillWindow = 0.16;

    /// <summary>喂给引擎的一个块：世界中心 + 尺寸 + 是不是煤。</summary>
    public readonly record struct Cell(double X, double Y, double Z, double Sx, double Sy, double Sz, bool IsCoal)
    {
        public double Volume => Sx * Sy * Sz;
    }

    /// <summary>
    /// 推进坐标系：主轴(短轴=推进方向) + 横轴(长轴=工作面方向) + 沿两轴的分位范围。
    /// <c>Forward</c> = 从 <c>MainMin</c> 一侧起推（由内排位置定：让采空区就近回填）。
    /// </summary>
    public sealed class Frame
    {
        public double Ux, Uy;            // 推进方向单位向量(短轴)
        public double Cx, Cy;            // 体积加权质心
        public double MainMin, MainMax;  // 沿推进轴的分位范围
        public double StripW;            // 一条采掘带的宽度
        public bool Forward;
        public double CrossMin, CrossBandW;

        /// <summary>世界点在推进轴上的投影（相对质心）。</summary>
        public double MainOf(double x, double y) => (x - Cx) * Ux + (y - Cy) * Uy;
        /// <summary>世界点在横轴上的投影（相对质心）。横轴 = 推进轴左转 90°，符号必须与建范围时一致，
        /// 否则标段会全挤到一端（原注释点名的坑）。</summary>
        public double CrossOf(double x, double y) => (x - Cx) * -Uy + (y - Cy) * Ux;
        /// <summary>(主, 横) → 世界。</summary>
        public (double x, double y) ToWorld(double main, double cross)
            => (Cx + Ux * main - Uy * cross, Cy + Uy * main + Ux * cross);

        /// <summary>主轴投影 → 采掘带序（0..PitStripTarget-1），按 Forward 决定从哪头起数。</summary>
        public int OrderOf(double main)
        {
            double t = Forward ? (main - MainMin) : (MainMax - main);
            return Math.Clamp((int)Math.Floor(t / StripW), 0, PitStripTarget - 1);
        }

        /// <summary>横轴投影 → 标段带号。</summary>
        public int PanelOf(double cross)
            => Math.Clamp((int)Math.Floor((cross - CrossMin) / CrossBandW), 0, PanelCount - 1);

        /// <summary>采掘带序 → 主轴坐标（工作面位置）。</summary>
        public double MainAtOrder(double order) => Forward ? MainMin + order * StripW : MainMax - order * StripW;
    }

    /// <summary>采场的一个桶：(采掘带序 × 台阶层) 一格，煤/岩方量分开记。</summary>
    public sealed class PitStrip
    {
        public int Panel, Order, BenchZ;
        public double CoalVol, WasteVol;
        public bool Visible = true;
        public List<int> CellIndices = new();   // 指回输入 cells 的下标, 供调用方驱动自己的场景
    }

    /// <summary>排土的一个桶：(推进条带 × 竖向层)。<c>Type</c> 0=外排 1=内排。</summary>
    public sealed class DumpLayer
    {
        public int Type, Order, ZOrder;
        public double Vol;
        public bool Visible;
        public List<int> CellIndices = new();
    }

    /// <summary>一帧读数（同原 <c>SimReadout</c>）。</summary>
    public readonly record struct Readout(
        double Progress, int MinedStrips, int TotalStrips,
        double CoalVolM3, double WasteVolM3, double ExtVolM3, double IntVolM3,
        string Phase, string PanelInfo);

    // ── 建推进坐标系 ────────────────────────────────────────────────
    /// <summary>
    /// 由采场块体建推进坐标系。<paramref name="dumpForDir"/> 给内排块体时，用它的质心决定从哪头起推
    /// （让采空区就近回填）；给 null 则一律正向。
    /// </summary>
    public static Frame BuildFrame(IReadOnlyList<Cell> pit, IReadOnlyList<Cell>? dumpForDir = null)
    {
        if (pit == null || pit.Count == 0)
            return new Frame { Ux = 1, Uy = 0, MainMin = 0, MainMax = 1, StripW = 1.0 / PitStripTarget, Forward = true, CrossMin = 0, CrossBandW = 1.0 / PanelCount };

        // 体积加权质心
        double sw = 0, sx = 0, sy = 0;
        foreach (var c in pit) { double w = c.Volume; sx += c.X * w; sy += c.Y * w; sw += w; }
        double cx = sw > 0 ? sx / sw : pit[0].X, cy = sw > 0 ? sy / sw : pit[0].Y;

        // 2D 协方差 → 大特征向量 = 长轴
        double cxx = 0, cxy = 0, cyy = 0;
        foreach (var c in pit)
        {
            double w = c.Volume, dx = c.X - cx, dy = c.Y - cy;
            cxx += w * dx * dx; cxy += w * dx * dy; cyy += w * dy * dy;
        }
        double tr = cxx + cyy, det = cxx * cyy - cxy * cxy;
        double disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        double l1 = tr / 2 + disc;
        double ux, uy;
        if (Math.Abs(cxy) > 1e-9) { ux = l1 - cyy; uy = cxy; }
        else { ux = cxx >= cyy ? 1 : 0; uy = cxx >= cyy ? 0 : 1; }
        double nrm = Math.Sqrt(ux * ux + uy * uy);
        if (nrm < 1e-12) { ux = 1; uy = 0; nrm = 1; }
        ux /= nrm; uy /= nrm;

        // 推进方向取**短轴**（长轴是工作面方向）
        double mux = -uy, muy = ux;

        var (amin, amax) = WeightedRangeDir(pit, cx, cy, mux, muy, 0.02, 0.98);
        // 横向必须沿 CrossOf 的方向 (−muy, mux) 算, 否则符号不匹配 → 标段全挤到一端
        var (cmin, cmax) = WeightedRangeDir(pit, cx, cy, -muy, mux, 0.02, 0.98);

        bool forward = true;
        if (dumpForDir != null && dumpForDir.Count > 0)
        {
            double c = CentroidProj(dumpForDir, cx, cy, mux, muy);
            forward = (c - amin) <= (amax - c);
        }
        return new Frame
        {
            Ux = mux, Uy = muy, Cx = cx, Cy = cy,
            MainMin = amin, MainMax = amax, StripW = Math.Max(1e-6, (amax - amin) / PitStripTarget),
            Forward = forward, CrossMin = cmin, CrossBandW = Math.Max(1e-6, (cmax - cmin) / PanelCount),
        };
    }

    /// <summary>沿 (dx,dy) 投影的**体积加权分位**区间（相对 cx,cy）。用 min/max 会被离群块拉长整条推进轴。</summary>
    public static (double lo, double hi) WeightedRangeDir(IReadOnlyList<Cell> cells, double cx, double cy,
                                                         double dx, double dy, double plo, double phi)
    {
        int n = cells.Count;
        if (n == 0) return (0, 1);
        var arr = new (double c, double w)[n];
        double total = 0;
        for (int i = 0; i < n; i++)
        {
            double px = cells[i].X - cx, py = cells[i].Y - cy;
            double w = cells[i].Volume;
            arr[i] = (px * dx + py * dy, w);
            total += w;
        }
        if (total <= 0) return (0, 1);
        Array.Sort(arr, (a, b) => a.c.CompareTo(b.c));
        double lo = arr[0].c, hi = arr[n - 1].c, cum = 0;
        bool gotLo = false;
        for (int i = 0; i < n; i++)
        {
            cum += arr[i].w;
            if (!gotLo && cum >= plo * total) { lo = arr[i].c; gotLo = true; }
            if (cum >= phi * total) { hi = arr[i].c; break; }
        }
        if (hi <= lo) hi = lo + 1;
        return (lo, hi);
    }

    /// <summary>沿 (dx,dy) 的体积加权质心投影。</summary>
    public static double CentroidProj(IReadOnlyList<Cell> cells, double cx, double cy, double dx, double dy)
    {
        double sum = 0, wsum = 0;
        foreach (var c in cells)
        {
            double w = c.Volume, px = c.X - cx, py = c.Y - cy;
            sum += (px * dx + py * dy) * w; wsum += w;
        }
        return wsum > 0 ? sum / wsum : 0;
    }

    // ── 分桶 ────────────────────────────────────────────────────────
    /// <summary>采场分桶：(采掘带 × 水平台阶层)，煤/岩方量分记。台阶层 0 = 最上台阶（标高最高）。</summary>
    public static List<PitStrip> BuildPit(IReadOnlyList<Cell> pit, Frame f, out double coalTotal, out double wasteTotal)
    {
        coalTotal = wasteTotal = 0;
        var strips = new List<PitStrip>();
        if (pit.Count == 0) return strips;

        double zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
        foreach (var c in pit)
        {
            double z0 = c.Z - c.Sz * 0.5, z1 = c.Z + c.Sz * 0.5;
            if (z0 < zmin) zmin = z0;
            if (z1 > zmax) zmax = z1;
        }
        double zspan = Math.Max(1e-6, zmax - zmin);

        var buckets = new Dictionary<(int order, int bench), PitStrip>();
        for (int i = 0; i < pit.Count; i++)
        {
            var c = pit[i];
            int order = f.OrderOf(f.MainOf(c.X, c.Y));
            int panel = Math.Clamp(order / BandLen, 0, PanelCount - 1);
            int bench = Math.Clamp((int)((zmax - c.Z) / zspan * NBench), 0, NBench - 1);   // 0 = 最上台阶
            var key = (order, bench);
            if (!buckets.TryGetValue(key, out var s))
                buckets[key] = s = new PitStrip { Panel = panel, Order = order, BenchZ = bench };
            s.CellIndices.Add(i);
            double v = c.Volume;
            if (c.IsCoal) { s.CoalVol += v; coalTotal += v; }
            else { s.WasteVol += v; wasteTotal += v; }
        }
        strips.AddRange(buckets.Values);
        strips.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.BenchZ.CompareTo(b.BenchZ));
        return strips;
    }

    /// <summary>
    /// 排土分桶：(推进条带 × 竖向层)。<paramref name="type"/> 0=外排 1=内排。
    ///
    /// <b>两种 Order 口径不同，不能合并</b>（原版就是分开算的）：
    ///   · <b>内排</b>用采场自己的推进坐标系（<c>Frame.OrderOf</c>）—— 它要被工作面门控，序号必须与采场对齐；
    ///   · <b>外排</b>按**自身**主轴范围铺到 0..PitStripTarget-1 —— 外排场通常整个落在采场推进范围之外，
    ///     照采场口径算会被一起夹到同一个序号上，于是整座外排场"啪"地一次全冒出来，看不出渐进堆进。
    /// Z 分层用块**中心**（同原版），不是包围盒上下沿。
    /// </summary>
    public static List<DumpLayer> BuildDump(IReadOnlyList<Cell> dump, Frame f, int type, out double total)
    {
        total = 0;
        var layers = new List<DumpLayer>();
        if (dump.Count == 0) return layers;

        double zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
        double dmin = double.PositiveInfinity, dmax = double.NegativeInfinity;
        foreach (var c in dump)
        {
            if (c.Z < zmin) zmin = c.Z;
            if (c.Z > zmax) zmax = c.Z;
            double dm = f.MainOf(c.X, c.Y);
            if (dm < dmin) dmin = dm;
            if (dm > dmax) dmax = dm;
        }
        double zspan = Math.Max(1e-6, zmax - zmin), dspan = Math.Max(1e-6, dmax - dmin);

        var buckets = new Dictionary<(int order, int z), DumpLayer>();
        for (int i = 0; i < dump.Count; i++)
        {
            var c = dump[i];
            double dmain = f.MainOf(c.X, c.Y);
            int order = type == 1
                ? f.OrderOf(dmain)
                : Math.Clamp((int)((f.Forward ? dmain - dmin : dmax - dmain) / dspan * (PitStripTarget - 1)), 0, PitStripTarget - 1);
            int zo = Math.Clamp((int)((c.Z - zmin) / zspan * DumpZLayers), 0, DumpZLayers - 1);   // 0 = 最低层
            var key = (order, zo);
            if (!buckets.TryGetValue(key, out var d))
                buckets[key] = d = new DumpLayer { Type = type, Order = order, ZOrder = zo };
            d.CellIndices.Add(i);
            d.Vol += c.Volume;
            total += c.Volume;
        }
        layers.AddRange(buckets.Values);
        layers.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.ZOrder.CompareTo(b.ZOrder));
        return layers;
    }

    // ── 逐帧推进 ────────────────────────────────────────────────────
    /// <summary>
    /// 把进度 <paramref name="p"/>(0..1) 套到采场与排土上：改各桶的 <c>Visible</c>，回一份读数。
    /// <paramref name="reverse"/> 对应原版「反向」开关。
    /// </summary>
    public static Readout Step(double p, IReadOnlyList<PitStrip> pit, IReadOnlyList<DumpLayer> dumps, bool reverse = false)
    {
        p = Math.Clamp(p, 0.0, 1.0);
        int localFace = (int)Math.Round(p * BandLen);   // 各带局部工作面(0..BandLen), 三带并行同速下推

        // 采场：可见 = 还没采掉。下台阶滞后上台阶(剥采平行); 末端滞后收缩到 0 → p=1 各台阶全采完
        double coal = 0, waste = 0;
        foreach (var s in pit)
        {
            int localOrder = s.Order % BandLen;
            int effLocal = reverse ? (BandLen - 1 - localOrder) : localOrder;
            int lag = Math.Min(s.BenchZ * BenchLag, BandLen - localFace);
            s.Visible = effLocal >= localFace - lag;
            if (!s.Visible) { coal += s.CoalVol; waste += s.WasteVol; }   // 已采出的才计方量
        }

        // 排土：内排门控在各带工作面之后(带-局部), 外排沿自身范围渐进; 都从下向上
        double extShown = 0, intShown = 0;
        double extMin = double.MaxValue, extMax = double.MinValue;
        foreach (var d in dumps)
            if (d.Type == 0) { if (d.Order < extMin) extMin = d.Order; if (d.Order > extMax) extMax = d.Order; }
        if (extMin > extMax) { extMin = 0; extMax = 1; }
        double extSpan = Math.Max(1, extMax - extMin);

        foreach (var d in dumps)
        {
            double a;
            if (d.Type == 1)
            {
                int lo = d.Order % BandLen;
                int el = reverse ? (BandLen - 1 - lo) : lo;
                a = Math.Min((double)(el + Lag) / BandLen, 1.0 - FillWindow);   // 封顶 → 内排 p=1 前排满
            }
            else a = (d.Order - extMin) / extSpan * (1.0 - FillWindow);         // 外排铺到接近 p=1 才满
            double colFill = Math.Clamp((p - a) / FillWindow, 0, 1);
            d.Visible = p >= a && d.ZOrder < colFill * DumpZLayers;
            if (d.Visible) { if (d.Type == 0) extShown += d.Vol; else intShown += d.Vol; }
        }

        int mined = Math.Min(localFace, BandLen) * PanelCount;
        string phase = localFace <= 0 ? "就绪：三标段带待推进" : "三标段带并行下推（内排随各带采空跟进）";
        return new Readout(p, mined, PitStripTarget, coal, waste, extShown, intShown,
                           phase, $"各带推进 {localFace}/{BandLen}");
    }

    /// <summary>
    /// 播放时钟（忠实原 <c>MiningSimWindow</c> 的 <c>OnTick</c>/<c>Play</c>/<c>Pause</c>）：
    /// 1× 速度跑完全程 45 秒、每 60ms 一拍。做成纯逻辑是因为"播完再点播放要从头播""循环时跨过 1.0
    /// 要绕回 0 而不是卡在 1.0"这类分支只在边界上出错，靠手点很难点准。窗体只管把它接到定时器上。
    /// </summary>
    public sealed class Playback
    {
        /// <summary>1× 速度跑完全程的秒数（原版常量）。</summary>
        public const double BaseDurationSec = 45.0;
        /// <summary>一拍的毫秒数（原版常量）。</summary>
        public const int TickMs = 60;

        private static double DeltaPerTick => (TickMs / 1000.0) / BaseDurationSec;

        public double Progress { get; private set; }
        public double Speed { get; set; } = 1.0;
        public bool Loop { get; set; }
        public bool Playing { get; private set; }

        /// <summary>点「播放」。已经播到头了就从头播（原版 <c>Play()</c> 的第一句）。</summary>
        public void Play()
        {
            if (Progress >= 1.0) Progress = 0;
            Playing = true;
        }

        public void Pause() => Playing = false;

        /// <summary>点一次播放/暂停按钮。</summary>
        public void Toggle() { if (Playing) Pause(); else Play(); }

        /// <summary>「重置」：停下并回到 0。</summary>
        public void Reset() { Playing = false; Progress = 0; }

        /// <summary>手动拖进度条：原版是**拖动即暂停**，便于定格观察。</summary>
        public void Seek(double p) { Playing = false; Progress = Math.Clamp(p, 0, 1); }

        /// <summary>走一拍。返回新的进度。不在播放态时原地不动。</summary>
        public double Tick()
        {
            if (!Playing) return Progress;
            double np = Progress + DeltaPerTick * Speed;
            if (np >= 1.0) np = Loop ? 0.0 : 1.0;
            Progress = np;
            if (Progress >= 1.0 && !Loop) Playing = false;   // 不循环就停在末帧
            return Progress;
        }
    }

    /// <summary>
    /// 各标段「什么时候从外排切到内排」：内排质心所在采掘带的**带内局部序** + <see cref="Lag"/>，
    /// 夹到 0.10~0.75。没有内排块的标段恒 1.0（永远走外排）。忠实原 <c>BuildSchedule</c>。
    /// </summary>
    public static double[] SwitchPoints(Frame f, IReadOnlyList<Cell> internalDump)
    {
        var sw = new double[PanelCount];
        for (int p = 0; p < PanelCount; p++) sw[p] = 1.0;
        if (internalDump == null || internalDump.Count == 0) return sw;

        // 内排块按标段分组, 各自算质心
        var sumX = new double[PanelCount]; var sumY = new double[PanelCount]; var sumW = new double[PanelCount];
        foreach (var c in internalDump)
        {
            int pnl = f.PanelOf(f.CrossOf(c.X, c.Y));
            double w = c.Volume;
            sumX[pnl] += c.X * w; sumY[pnl] += c.Y * w; sumW[pnl] += w;
        }
        for (int p = 0; p < PanelCount; p++)
        {
            if (sumW[p] <= 0) continue;
            double ix = sumX[p] / sumW[p], iy = sumY[p] / sumW[p];
            int localO = f.OrderOf(f.MainOf(ix, iy)) % BandLen;
            sw[p] = Math.Clamp((double)(localO + Lag) / BandLen, 0.10, 0.75);
        }
        return sw;
    }
}
