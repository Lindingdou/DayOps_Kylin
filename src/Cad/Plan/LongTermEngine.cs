using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 【LT7/LT8】中长远的**唯一量源**（原 <c>LongTermBlockSource</c>）：激活块体沿【人为指定的工作线】推进轴的累计煤/岩剖面。
/// 走「刀量切割 / 量驱动开采模板」的同一个引擎（<see cref="TemplateDrivingEngine.BuildAdvanceProfile"/>），不另写扫描。
/// 本类只把块体变成一条 (累计采出, 累计剥离) 曲线；均衡是 VpBalanceSolver 的事，切年是 <see cref="LongTermScheduler"/> 的事。
/// </summary>
public static class LongTermBlockSource
{
    public sealed class AdvanceCurve
    {
        public bool Ok;
        public string Error = "";
        public string Provenance = "";
        public List<double> CumCoalWanT = new();
        public List<double> CumStripWanM3 = new();
        public List<double> AdvanceM = new();
        public double TotalCoalWanT => CumCoalWanT.Count > 0 ? CumCoalWanT[^1] : 0;
        public double TotalStripWanM3 => CumStripWanM3.Count > 0 ? CumStripWanM3[^1] : 0;
        public double OverallRatio => TotalCoalWanT > 1e-9 ? TotalStripWanM3 / TotalCoalWanT : 0;
        public double SliceWidthM;
        public int Bins => Math.Max(0, CumCoalWanT.Count - 1);
    }

    /// <summary>当前是否有可用的量源（界面拿它决定拦不拦）。</summary>
    public static bool HasActiveBlockModel(BlockModelMeta? m, out string note)
    {
        if (m == null) { note = "未激活块体模型"; return false; }
        if (!BlockModelCoal.TryGetClassifier(m, out _, out _)) { note = $"块体「{m.Name}」里没识别出「煤」属性列"; return false; }
        note = $"块体「{m.Name}」";
        return true;
    }

    /// <summary>沿工作线取累计曲线。sliceWidthM ≤ 0 时自动取（推进总长的 1/200，夹在 5~50m）。</summary>
    public static AdvanceCurve Build(BlockModelMeta? model, WorkLineAdvanceVariant wl, double benchHeightM, double faceAngleDeg,
                                     double minBermM, double coalDensity, double sliceWidthM = 0)
    {
        var c = new AdvanceCurve();
        if (model == null) { c.Error = "未激活块体模型 —— 中长远的量只能来自块体（BM1），请先加载并激活一个带煤属性的块体模型"; return c; }
        var geo = ToSamples(wl);
        if (geo == null) { c.Error = "工作线几何不全（基线点 < 2 或没有逐段方向）—— 请在「派生计划方案」里重新拾取一次"; return c; }

        double slice = sliceWidthM;
        if (slice <= 0)
        {
            double span = SpanAlongAdvance(wl);
            slice = Math.Clamp(span / 200.0, 5.0, 50.0);
        }
        var cells = BlockModelCells.Build(model, out string prov);
        if (cells == null || cells.Count == 0) { c.Error = $"块体「{model.Name}」里没识别出「煤」属性列或无块"; return c; }

        var p = TemplateDrivingEngine.BuildAdvanceProfile(cells, geo, faceAngleDeg, benchHeightM, minBermM, slice, prov);
        if (!p.Success) { c.Error = "块体沿工作线分箱失败：" + p.Error; return c; }
        if (p.NBins <= 0 || p.CumCoal.Length == 0) { c.Error = "沿这条工作线一个含煤箱都没有 —— 工作线是不是不在块体范围内？"; return c; }

        c.Provenance = p.Provenance;
        c.SliceWidthM = p.SliceWidth;
        c.CumCoalWanT.Add(0); c.CumStripWanM3.Add(0); c.AdvanceM.Add(0);
        for (int i = 0; i < p.NBins; i++)
        {
            // Kylin 引擎的 CumCoal/CumRock 已是 m³（CellBox.VolM3 求和）
            c.CumCoalWanT.Add(p.CumCoal[i] * coalDensity / 1e4);
            c.CumStripWanM3.Add(p.CumRock[i] / 1e4);
            c.AdvanceM.Add((i + 1) * p.SliceWidth);
        }
        if (c.TotalCoalWanT <= 1e-9) { c.Error = $"沿这条工作线累计煤量为 0（{p.Provenance}）—— 换条工作线或检查煤属性列"; return c; }
        c.Ok = true;
        return c;
    }

    /// <summary>把方案里存的工作线还原成引擎吃的几何（纯数据，脱 GUI 可上台架）。</summary>
    public static WorkLineSamples? ToSamples(WorkLineAdvanceVariant wl)
    {
        if (wl == null || wl.Baseline.Count < 2 || wl.Samples.Count < 1) return null;
        var g = new WorkLineSamples
        {
            Success = true,
            AdvanceMode = wl.AdvanceMode == AdvanceMode.FixedPivot ? (byte)2 : (byte)0,
            DirMode = 0, Closed = wl.Closed, HasFanParams = wl.HasPivot, PivotX = wl.PivotX, PivotY = wl.PivotY,
        };
        g.Baseline.AddRange(wl.Baseline);
        g.Samples.AddRange(wl.Samples);
        return g;
    }

    private static double SpanAlongAdvance(WorkLineAdvanceVariant wl)
    {
        if (wl.Baseline.Count < 2) return 1000;
        double az = wl.AdvanceAzimuthDeg * Math.PI / 180.0;
        double dx = Math.Sin(az), dy = Math.Cos(az);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var pt in wl.Baseline)
        {
            double t = pt.X * dx + pt.Y * dy;
            if (t < lo) lo = t;
            if (t > hi) hi = t;
        }
        double baseLen = WorkLinePicker.BaselineLength(wl.Baseline);
        return Math.Max(hi - lo, baseLen);
    }
}

/// <summary>
/// 【LT1】把图上选中的「工作线」实体量成一条 <see cref="WorkLineAdvanceVariant"/>（原 <c>WorkLinePicker</c>）。
/// 方位 = 逐段推进方向的矢量和方位角（0=正北/+Y，90=正东/+X），不从基线走向算；方式 = 实体的推进方式；handle = 源实体供重读。
/// </summary>
public static class WorkLinePicker
{
    public readonly record struct PickResult(WorkLineAdvanceVariant? Variant, string Message)
    {
        public bool Success => Variant != null;
    }

    /// <summary>从当前选集拾取恰好 1 条工作线（宿主给几何 + handle）。</summary>
    public static PickResult PickSelected(IPlanEntityHost? host, string? label = null)
    {
        if (host == null) return new(null, "未接入图形能力");
        var geo = host.SelectedWorkLine(out long handle, out string err);
        if (geo == null || !geo.Success)
            return new(null, string.IsNullOrEmpty(err) ? "请在图上**选中恰好 1 条工作线**（不是普通多段线 —— 用「创建工作线」先转化）" : err);
        var v = FromGeometry(geo, handle, label);
        if (v == null) return new(null, "工作线没有有效推进方向（逐段方向全为零矢量）——在图上拖一下箭头再试");
        return new(v, $"已拾取工作线：{v.Label}（{v.Caption}）");
    }

    /// <summary>按 handle 重读实体最新方向/长度，回填到已有变体（【LT5】箭头改过要跟得上）。</summary>
    public static bool Refresh(IPlanEntityHost? host, WorkLineAdvanceVariant wl)
    {
        if (host == null || wl.SourceHandle == 0) return false;
        var geo = host.WorkLineByHandle(wl.SourceHandle);
        if (geo == null || !geo.Success) return false;
        var fresh = FromGeometry(geo, wl.SourceHandle, wl.Label);
        if (fresh == null) return false;
        wl.AdvanceAzimuthDeg = fresh.AdvanceAzimuthDeg; wl.AdvanceMode = fresh.AdvanceMode;
        wl.HasPivot = fresh.HasPivot; wl.PivotX = fresh.PivotX; wl.PivotY = fresh.PivotY;
        wl.WorkLineLenM = fresh.WorkLineLenM;
        wl.Baseline.Clear(); wl.Baseline.AddRange(fresh.Baseline);
        wl.Samples.Clear(); wl.Samples.AddRange(fresh.Samples);
        wl.Closed = fresh.Closed;
        return true;
    }

    /// <summary>几何 → 变体（纯算，可离线台架直接喂几何验收）。</summary>
    public static WorkLineAdvanceVariant? FromGeometry(WorkLineSamples geo, long handle, string? label = null)
    {
        if (!geo.Success || geo.Baseline.Count < 2) return null;
        double sx = 0, sy = 0;
        foreach (var s in geo.Samples) { sx += s.Dx; sy += s.Dy; }
        if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9) return null;
        double az = Math.Atan2(sx, sy) * 180.0 / Math.PI;
        if (az < 0) az += 360;

        var v = new WorkLineAdvanceVariant
        {
            Label = label ?? (handle != 0 ? $"工作线#{handle:X}" : "工作线"),
            AdvanceAzimuthDeg = Math.Round(az, 1),
            AdvanceMode = geo.AdvanceMode == 2 ? AdvanceMode.FixedPivot : AdvanceMode.Parallel,
            SourceHandle = handle,
            WorkLineLenM = BaselineLength(geo.Baseline),
        };
        if (geo.HasFanParams && v.AdvanceMode == AdvanceMode.FixedPivot) { v.HasPivot = true; v.PivotX = geo.PivotX; v.PivotY = geo.PivotY; }
        v.Baseline.AddRange(geo.Baseline);
        v.Samples.AddRange(geo.Samples);
        v.Closed = geo.Closed;
        return v;
    }

    public static double BaselineLength(IReadOnlyList<(double X, double Y, double Z)> pts)
    {
        double sum = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        return sum;
    }
}

/// <summary>
/// 【LT6】中长远的排土侧（原 <c>LongTermDumpBridge</c>）：按【给定的排土形态】(dump_strip 台账) 反出逐年排土——
/// 内排起转年之前一律外排；之后内排优先，排满再转外排；两池都吃不下的记在那一年头上；真内排率 = 几何反算。
/// 台账没接时不硬凑：保持政策估算，但在 DumpSourceText 里写明。
/// </summary>
public static class LongTermDumpBridge
{
    public const double DefaultSwellKr = 1.25;

    public sealed class DumpForm
    {
        public List<DumpStrip> Inner = new();
        public List<DumpStrip> Outer = new();
        public bool HasAny => Inner.Count > 0 || Outer.Count > 0;
        public double InnerCapacityM3 => Inner.Sum(s => s.CapacityM3);
        public double OuterCapacityM3 => Outer.Sum(s => s.CapacityM3);
        public string Text => HasAny
            ? $"排土形态=排土条带台账（内排 {Inner.Count} 带/{InnerCapacityM3 / 1e4:N0} 万m³ · 外排 {Outer.Count} 带/{OuterCapacityM3 / 1e4:N0} 万m³）"
            : "排土形态未接（内排率按内排起转年估算，不是几何反算）";
    }

    /// <summary>读 dump_strip 台账。读不到/没接库一律返回空池，不抛。</summary>
    public static DumpForm TryReadForm(System.Data.Common.DbConnection? conn)
    {
        var f = new DumpForm();
        try
        {
            if (conn == null) return f;
            foreach (var s in Data.DumpStripRepo.All(conn))
            {
                if (s.CapacityM3 <= 0) continue;
                if (string.Equals(s.Category, "internal_dump", StringComparison.OrdinalIgnoreCase)) f.Inner.Add(s);
                else f.Outer.Add(s);
            }
        }
        catch { }
        return f;
    }

    public static string Apply(LongTermPlan p, DumpForm? form, double swellKr = DefaultSwellKr)
    {
        if (p == null) return "";
        form ??= new DumpForm();
        if (!form.HasAny)
        {
            if (p.Result != null) p.Result.DumpSourceText = form.Text;
            return form.Text;
        }

        double kr = swellKr > 1.0 ? swellKr : 1.0;
        var innerSeq = Dump.DumpAdvanceByVolume.Sort(form.Inner, Dump.DumpAdvanceByVolume.Order.StepAbreast);
        var outerSeq = Dump.DumpAdvanceByVolume.Sort(form.Outer, Dump.DumpAdvanceByVolume.Order.StepAbreast);
        double innerFilled = 0, outerFilled = 0;
        double totalInner = 0, totalOuter = 0, totalOverflow = 0;
        string fullYear = "—";

        for (int i = 0; i < p.Periods.Count; i++)
        {
            var per = p.Periods[i];
            double needM3 = per.StripWanM3 * 1e4 * kr;
            per.InnerDumpWanM3 = per.OuterDumpWanM3 = per.DumpOverflowWanM3 = 0;
            per.DumpAdvanceM = 0; per.DumpCellText = "";
            if (needM3 <= 1e-6) continue;

            bool innerOpen = p.InnerDumpEnabled && i >= p.InnerDumpStartYear && innerSeq.Count > 0;
            var parts = new List<string>();
            double advance = 0;

            if (innerOpen)
            {
                var r = Dump.DumpAdvanceByVolume.Advance(innerSeq, needM3, 1.0, Dump.DumpAdvanceByVolume.Order.StepAbreast, innerFilled);
                var sl = r.Slices.FirstOrDefault();
                if (sl != null && sl.PlacedM3 > 1e-6)
                {
                    innerFilled += sl.PlacedM3; per.InnerDumpWanM3 = sl.PlacedM3 / 1e4; totalInner += sl.PlacedM3;
                    needM3 -= sl.PlacedM3; advance = Math.Max(advance, sl.MaxAdvanceM);
                    parts.Add($"内排 L{sl.TopLevel}·第{sl.TopStep}带");
                }
            }
            if (needM3 > 1e-6 && outerSeq.Count > 0)
            {
                var r = Dump.DumpAdvanceByVolume.Advance(outerSeq, needM3, 1.0, Dump.DumpAdvanceByVolume.Order.StepAbreast, outerFilled);
                var sl = r.Slices.FirstOrDefault();
                if (sl != null && sl.PlacedM3 > 1e-6)
                {
                    outerFilled += sl.PlacedM3; per.OuterDumpWanM3 = sl.PlacedM3 / 1e4; totalOuter += sl.PlacedM3;
                    needM3 -= sl.PlacedM3; advance = Math.Max(advance, sl.MaxAdvanceM);
                    parts.Add($"外排 L{sl.TopLevel}·第{sl.TopStep}带");
                }
            }
            if (needM3 > 1e-6)
            {
                per.DumpOverflowWanM3 = needM3 / 1e4; totalOverflow += needM3;
                if (fullYear == "—") fullYear = per.Label;
                parts.Add($"排不下 {needM3 / 1e4:N0} 万m³");
            }
            per.DumpAdvanceM = Math.Round(advance, 1);
            per.DumpCellText = string.Join(" → ", parts);
            per.Dump = per.InnerDumpWanM3 >= per.OuterDumpWanM3 && per.InnerDumpWanM3 > 0 ? DumpMode.Internal : DumpMode.External;
        }

        double tot = totalInner + totalOuter + totalOverflow;
        if (p.Result != null)
        {
            p.Result.InnerDumpPct = tot > 1e-6 ? Math.Round(totalInner / tot * 100, 0) : 0;
            p.Result.AvgHaulKm = Math.Round(2.8 - p.Result.InnerDumpPct / 100.0 * 1.2, 1);
            p.Result.DumpSourceText = form.Text;
            p.Result.DumpFullYearLabel = fullYear;
            p.Result.DumpOverflowWanM3 = Math.Round(totalOverflow / 1e4, 0);
        }
        string msg = $"{form.Text}；内排 {totalInner / 1e4:N0} 万m³ / 外排 {totalOuter / 1e4:N0} 万m³（内排率 {(tot > 1e-6 ? totalInner / tot * 100 : 0):0}%，几何反算）";
        if (totalOverflow > 1e-6) msg += $"；⚠ {fullYear} 年起排不下，全期共 {totalOverflow / 1e4:N0} 万m³ 无处可排 —— 需扩场或另找去向";
        return msg;
    }
}

/// <summary>
/// 中长远进度计划「规划计算」内核（原 <c>LongTermScheduler</c>）：量只从块体沿工作线来（LT7）→ 基建剥离=几何给的（第一箱煤前的累计剥离）
/// → 产出段顶点(锚点=(0,基建剥离)) → VP 曲线 DP 分 K 段均衡（LT9）→ 生产期沿曲线按 A_p 切年，剥离量=均衡折线差分 → 逐年现金流/NPV
/// → 评价 → 排土侧（LT6）。没有可用块体就不排、不兜底、不造数。
/// </summary>
public static class LongTermScheduler
{
    /// <summary>台阶几何来源（原优先「开采模板」，取不到用方案里的台阶高 + 兜底角/平盘）。宿主可注入。</summary>
    public static Func<LongTermPlan, (double HeightM, double FaceAngleDeg, double MinBermM)>? BenchGeometryHook { get; set; }

    public static void Schedule(LongTermPlan p, BlockModelMeta? model, LongTermDumpBridge.DumpForm? dumpForm = null,
                                LongTermBlockSource.AdvanceCurve? curve = null)
    {
        p.Periods.Clear();
        p.Result = null;
        p.ScheduleNote = "";

        double rho = LongTermPlan.DefaultCoalDensity;
        double Ap = Math.Max(1, p.DesignCapacityWanTa);

        var bench = BenchGeometryFromTemplate(p);
        curve ??= LongTermBlockSource.Build(model, p.WorkLine, bench.HeightM, bench.FaceAngleDeg, bench.MinBermM, rho);
        if (!curve.Ok) { p.ScheduleNote = curve.Error; return; }

        var cx = curve.CumCoalWanT;
        var cy = curve.CumStripWanM3;
        int firstCoal = 0;
        while (firstCoal < cx.Count && cx[firstCoal] <= 1e-9) firstCoal++;
        if (firstCoal >= cx.Count) { p.ScheduleNote = $"沿这条工作线一箱煤都没有（{curve.Provenance}）"; return; }
        double basicStripTotal = firstCoal > 0 ? cy[firstCoal - 1] : 0;

        var xs = new List<double> { 0 };
        var ys = new List<double> { basicStripTotal };
        var advs = new List<double> { curve.AdvanceM[Math.Max(0, firstCoal - 1)] };
        for (int i = firstCoal; i < cx.Count; i++) { xs.Add(cx[i]); ys.Add(cy[i]); advs.Add(curve.AdvanceM[i]); }
        int M = xs.Count - 1;

        var bal = VpBalanceSolver.Solve(xs, ys, p.BalanceStageCount);
        if (!bal.Ok) { p.ScheduleNote = $"均衡求解失败：产出段只有 {xs.Count} 个顶点（{curve.Provenance}）"; return; }

        int basic = Math.Max(0, p.BasicStrippingYears);
        int ramp = Math.Max(0, p.RampUpYears);
        double basicPerYear = basic > 0 ? basicStripTotal / basic : 0;

        double cumCoal = 0, cumStrip = 0, cumDisc = 0;
        double d = p.DiscountRatePct / 100.0;
        int yi = 0;
        int designCalcIdx = -1;
        var periods = new List<PlanPeriod>();

        for (int k = 0; k < basic; k++)
        {
            double strip = basicPerYear;
            cumStrip += strip;
            double cf0 = -strip * p.StripCostYuanM3;
            double disc0 = cf0 / Math.Pow(1 + d, yi);
            cumDisc += disc0;
            periods.Add(new PlanPeriod
            {
                Label = $"{p.StartYear + yi}", CoalWanT = 0, StripWanM3 = Math.Round(strip, 0), Ratio = 0,
                CumCoal = 0, CumStrip = Math.Round(cumStrip, 0), CapacityPct = 0, Phase = PlanPhase.Basic, AdvanceM = 0, AdvanceRateMpa = 0,
                StageNo = 0, StageRatio = 0, LeadStripWanM3 = Math.Round(cumStrip, 0),
                Dump = DumpMode.External, CashFlowWan = Math.Round(cf0, 0), NpvWan = Math.Round(disc0, 0),
            });
            yi++;
        }

        double vtx = 0;
        double cumBalanced = basicStripTotal;
        int prodIdx = 0;
        double peakLead = 0;

        while (vtx < M - 1e-9 && prodIdx < 300)
        {
            double capPct = 1.0;
            PlanPhase phase = PlanPhase.Stable;
            if (prodIdx < ramp && ramp > 0)
            {
                double r0 = Math.Clamp(p.RampFirstYearPct / 100.0, 0.05, 1.0);
                capPct = ramp <= 1 ? 1.0 : r0 + (1 - r0) * prodIdx / (ramp - 1.0);
                capPct = Math.Clamp(capPct, r0, 1.0);
                phase = PlanPhase.RampUp;
            }

            double target = Ap * capPct;
            double coalStart = InterpAt(xs, vtx);
            double want = coalStart + target;
            double vtxEnd = LocateX(xs, want);
            bool tail = vtxEnd >= M - 1e-9;
            if (tail) vtxEnd = M;

            double coal = InterpAt(xs, vtxEnd) - coalStart;
            if (coal <= 1e-9) break;
            if (tail) phase = PlanPhase.Decline;

            double cumCoalEnd = InterpAt(xs, vtxEnd);
            double balancedEnd = VpBalanceSolver.BalanceY(xs, ys, bal.Breakpoints, cumCoalEnd);
            double strip = Math.Max(0, balancedEnd - cumBalanced);
            cumBalanced = balancedEnd;

            double demandEnd = InterpAt(ys, vtxEnd);
            double lead = balancedEnd - demandEnd;
            if (lead > peakLead) peakLead = lead;

            int segNo = SegmentOf(bal.Breakpoints, (int)Math.Ceiling(vtxEnd)) + 1;
            double segRatio = segNo - 1 < bal.Segments.Count ? bal.Segments[segNo - 1].RatioM3PerT : 0;

            double ratio = coal > 1e-9 ? strip / coal : 0;
            bool internalDump = p.InnerDumpEnabled && prodIdx >= p.InnerDumpStartYear;
            double stripCostFactor = internalDump ? 0.85 : 1.0;
            double cf = coal * (p.CoalPriceYuanT - p.MiningCostYuanT) - strip * p.StripCostYuanM3 * stripCostFactor;
            double disc = cf / Math.Pow(1 + d, yi);
            cumCoal += coal; cumStrip += strip; cumDisc += disc;

            double advance = InterpAt(advs, vtxEnd) - InterpAt(advs, vtx);

            if (capPct >= 0.999 && designCalcIdx < 0) designCalcIdx = periods.Count;
            periods.Add(new PlanPeriod
            {
                Label = $"{p.StartYear + yi}",
                CoalWanT = Math.Round(coal, 0), StripWanM3 = Math.Round(strip, 0), Ratio = Math.Round(ratio, 2),
                CumCoal = Math.Round(cumCoal, 0), CumStrip = Math.Round(cumStrip, 0),
                CapacityPct = Math.Round(capPct * 100, 0), Phase = phase,
                AdvanceM = Math.Round(advance, 0), AdvanceRateMpa = Math.Round(advance, 0),
                StageNo = segNo, StageRatio = Math.Round(segRatio, 2), LeadStripWanM3 = Math.Round(lead, 0),
                Dump = internalDump ? DumpMode.Internal : DumpMode.External,
                CashFlowWan = Math.Round(cf, 0), NpvWan = Math.Round(disc, 0),
            });
            yi++; prodIdx++;
            vtx = vtxEnd;
            if (tail) break;
        }

        if (designCalcIdx >= 0) periods[designCalcIdx].IsDesignCalcYear = true;
        foreach (var pp in periods) p.Periods.Add(pp);

        double peak = periods.Where(z => z.CoalWanT > 0).Select(z => z.Ratio).DefaultIfEmpty(0).Max();
        p.Result = Evaluate(p, periods, peak, basicStripTotal, designCalcIdx >= 0 ? periods[designCalcIdx].Label : "—");
        p.Result.QuantitySourceText = $"{curve.Provenance} · 沿工作线「{p.WorkLine.Label}」分箱 Δ={curve.SliceWidthM:0.#}m · {curve.Bins} 箱";
        p.Result.BalanceStages = bal.UsedK;
        p.Result.PeakLeadStripWanM3 = Math.Round(peakLead, 0);
        p.ScheduleNote = $"{p.Result.QuantitySourceText}；均衡 {bal.UsedK} 段（" + string.Join(" / ", bal.Segments.Select(z => $"n={z.RatioM3PerT:0.00}")) + "）";

        LongTermDumpBridge.Apply(p, dumpForm);
    }

    /// <summary>断点序列里顶点 idx 落在第几段（0 起）。</summary>
    public static int SegmentOf(IReadOnlyList<int> bps, int idx)
    {
        for (int s = 0; s + 1 < bps.Count; s++)
            if (idx <= bps[s + 1]) return s;
        return Math.Max(0, bps.Count - 2);
    }

    private static (double HeightM, double FaceAngleDeg, double MinBermM) BenchGeometryFromTemplate(LongTermPlan p)
    {
        if (BenchGeometryHook != null)
        {
            try { return BenchGeometryHook(p); } catch { }
        }
        double h = p.BenchHeightM > 0 ? p.BenchHeightM : 12.0;
        return (h, 65.0, 8.0);
    }

    public static double InterpAt(IReadOnlyList<double> a, double t)
    {
        if (a.Count == 0) return 0;
        if (t <= 0) return a[0];
        if (t >= a.Count - 1) return a[^1];
        int i = (int)Math.Floor(t);
        double f = t - i;
        return a[i] + (a[i + 1] - a[i]) * f;
    }

    public static double LocateX(IReadOnlyList<double> a, double v)
    {
        if (a.Count == 0) return 0;
        if (v <= a[0]) return 0;
        for (int i = 1; i < a.Count; i++)
        {
            if (a[i] >= v - 1e-12)
            {
                double dx = a[i] - a[i - 1];
                return dx > 1e-12 ? (i - 1) + (v - a[i - 1]) / dx : i;
            }
        }
        return a.Count - 1;
    }

    /// <summary>一键排产比选：在基础约束上，按用户已指定的工作线（一条 = 一套）真场排产 + 联合评分，挑出推荐（LT2/LT4：不展开任何写死组合）。</summary>
    public static (List<LongTermPlan> schemes, LongTermPlan? best) ComposeFrom(
        LongTermBase basis, IReadOnlyList<WorkLineAdvanceVariant> workLines, BlockModelMeta? model, LongTermDumpBridge.DumpForm? dumpForm)
    {
        if (workLines == null || workLines.Count == 0) return (new List<LongTermPlan>(), null);
        var schemes = Generate(basis, workLines, model, dumpForm, null, null, schedule: true);
        var scored = schemes.Where(s => s.Result != null).ToList();
        LongTermComparer.Score(scored);
        var best = scored.Where(s => s.Result!.Ok).OrderByDescending(s => s.Result!.CompositeScore).FirstOrDefault()
                ?? scored.OrderByDescending(s => s.Result!.CompositeScore).FirstOrDefault();
        return (schemes, best);
    }

    private static LongTermResult Evaluate(LongTermPlan p, List<PlanPeriod> periods, double peakRatio, double basicStripTotal, string designCalcLabel)
    {
        var prod = periods.Where(z => z.CoalWanT > 0).ToList();
        double serviceLife = prod.Count;
        int basic = periods.Count(z => z.Phase == PlanPhase.Basic);
        int rampN = periods.Count(z => z.Phase == PlanPhase.RampUp);
        double ttc = basic + rampN;
        double plateau = periods.Count(z => z.Phase == PlanPhase.Stable);

        double totStrip = periods.Sum(z => z.StripWanM3);
        double innerStrip = periods.Where(z => z.Dump == DumpMode.Internal).Sum(z => z.StripWanM3);
        double innerPct = totStrip > 0 ? innerStrip / totStrip * 100 : 0;

        double npv = periods.Sum(z => z.NpvWan);
        double cum = 0, payback = serviceLife + basic;
        for (int i = 0; i < periods.Count; i++) { cum += periods[i].NpvWan; if (cum > 0) { payback = i; break; } }

        double[] coal = prod.Select(z => z.CoalWanT).ToArray();
        double[] ratio = prod.Select(z => z.Ratio).ToArray();
        double outCv = Cv(coal), ratCv = Cv(ratio);
        double balance = Math.Clamp(1 - outCv, 0, 1);
        double haul = Math.Round(2.8 - innerPct / 100.0 * 1.2, 1);

        double lifeMin = ServiceLifeMinFor(p.DesignCapacityWanTa);
        bool ok = serviceLife >= lifeMin && peakRatio <= p.EconomicStripRatioMax + 1e-6 && ttc > 0;

        return new LongTermResult
        {
            ServiceLifeYears = serviceLife, TimeToCapacityYears = ttc, DesignCalcYearLabel = designCalcLabel,
            StablePlateauYears = plateau, ProductionRatioPeak = Math.Round(peakRatio, 1),
            BasicStrippingYiM3 = Math.Round(basicStripTotal / 1e4, 2), InnerDumpPct = Math.Round(innerPct, 0),
            AvgHaulKm = haul, Npv = Math.Round(npv, 0), PaybackYears = payback,
            OutputCv = Math.Round(outCv, 3), RatioCv = Math.Round(ratCv, 3), ReserveBalanceCoef = Math.Round(balance, 2), Ok = ok,
        };
    }

    private static double Cv(double[] a)
    {
        if (a.Length == 0) return 0;
        double mean = a.Average();
        if (mean <= 1e-9) return 0;
        double var = a.Select(x => (x - mean) * (x - mean)).Average();
        return Math.Sqrt(var) / mean;
    }

    /// <summary>规范最低服务年限（按设计能力分级，GB50197 近似）。</summary>
    public static double ServiceLifeMinFor(double capWanTa) => capWanTa switch
    {
        >= 1000 => 30, >= 500 => 25, >= 300 => 20, >= 100 => 15, _ => 10
    };

    public readonly record struct RampSpec(string Label, RampProfileKind Kind);
    public readonly record struct CapacitySpec(string Label, double Multiplier);

    /// <summary>在一套基础约束之上，按 已指定工作线 × 能力档 × 达产节奏 的笛卡尔积生成候选，每套独立排产（方向不是轴：它长在工作线实体上）。</summary>
    public static List<LongTermPlan> Generate(LongTermBase basis, IReadOnlyList<WorkLineAdvanceVariant> workLines,
        BlockModelMeta? model, LongTermDumpBridge.DumpForm? dumpForm,
        IReadOnlyList<CapacitySpec>? capacities = null, IReadOnlyList<RampSpec>? ramps = null, bool schedule = true)
    {
        capacities ??= new[] { new CapacitySpec("基准", 1.0) };
        ramps ??= new[] { new RampSpec("基准", basis.RampProfile) };
        bool showCap = capacities.Count > 1, showRamp = ramps.Count > 1;

        var result = new List<LongTermPlan>();
        foreach (var cap in capacities)
            foreach (var ramp in ramps)
                foreach (var wl in workLines)
                {
                    var variant = wl.Copy();
                    double capWanTa = basis.DesignCapacityWanTa * cap.Multiplier;
                    string name = wl.Label + (showCap ? $"·{capWanTa:0}万t" : "") + (showRamp ? $"·{ramp.Label}" : "");
                    var p = basis.NewCandidate(variant, name, capWanTa, ramp.Kind);
                    if (schedule) Schedule(p, model, dumpForm);
                    result.Add(p);
                }
        return result;
    }

    /// <summary>转成 Kylin 早期的扁平模型（供既有「中长远规划动态模拟」年轨窗口读新库排出来的方案）。</summary>
    public static Cad.LongTermPlan ToLegacy(LongTermPlan p)
    {
        var l = new Cad.LongTermPlan
        {
            Name = p.Name, DesignCapacityWanTa = p.DesignCapacityWanTa, CoalReserveWanT = p.CoalReserveWanT, BaseStripRatio = p.BaseStripRatio,
            BasicStrippingYears = p.BasicStrippingYears, RampUpYears = p.RampUpYears, DeclineYears = p.DeclineYears,
            RampProfile = (Cad.RampProfileKind)(int)p.RampProfile, StartYear = p.StartYear,
            CoalPriceYuanT = p.CoalPriceYuanT, MiningCostYuanT = p.MiningCostYuanT, StripCostYuanM3 = p.StripCostYuanM3, DiscountRatePct = p.DiscountRatePct,
            InnerDumpEnabled = p.InnerDumpEnabled, InnerDumpStartYear = p.InnerDumpStartYear, EconomicStripRatioMax = p.EconomicStripRatioMax,
            BenchHeightM = p.BenchHeightM, WorkLineLenM = p.WorkLine.WorkLineLenM, WorkLineMode = p.WorkLine.AdvanceMode, AdvanceAzimuthDeg = p.WorkLine.AdvanceAzimuthDeg,
        };
        foreach (var z in p.Periods)
            l.Periods.Add(new Cad.PlanPeriod
            {
                Label = z.Label, CoalWanT = z.CoalWanT, StripWanM3 = z.StripWanM3, Ratio = z.Ratio, CumCoal = z.CumCoal, CumStrip = z.CumStrip,
                CapacityPct = z.CapacityPct, Phase = (Cad.PlanPhase)(int)z.Phase, AdvanceRateMpa = z.AdvanceRateMpa,
                Dump = z.Dump == DumpMode.Internal ? Cad.LongTermDumpMode.Internal : Cad.LongTermDumpMode.External,
                CashFlowWan = z.CashFlowWan, NpvWan = z.NpvWan, IsDesignCalcYear = z.IsDesignCalcYear,
            });
        if (p.Result is { } r)
            l.Result = new Cad.LongTermResult(r.ServiceLifeYears, r.TimeToCapacityYears, r.DesignCalcYearLabel, r.StablePlateauYears, r.ProductionRatioPeak,
                r.BasicStrippingYiM3, r.InnerDumpPct, r.AvgHaulKm, r.Npv, r.PaybackYears, r.OutputCv, r.RatioCv, r.ReserveBalanceCoef, r.Ok)
            { CompositeScore = r.CompositeScore, Name = p.Name };
        return l;
    }
}

/// <summary>进度计划方案「联合对比」评分（原 <c>LongTermComparer</c>）：方向感知归一 + 加权（权重取各方案 DecisionWeights 平均）→ 综合评分/推荐。</summary>
public static class LongTermComparer
{
    public static string Score(IReadOnlyList<LongTermPlan> plans)
    {
        var withR = plans.Where(p => p.Result != null).ToList();
        if (withR.Count == 0) return "—";

        double[] plateau = withR.Select(p => p.Result!.StablePlateauYears).ToArray();
        double[] peak = withR.Select(p => p.Result!.ProductionRatioPeak).ToArray();
        double[] ttc = withR.Select(p => p.Result!.TimeToCapacityYears).ToArray();
        double[] inner = withR.Select(p => p.Result!.InnerDumpPct).ToArray();
        double[] npv = withR.Select(p => p.Result!.Npv).ToArray();
        double[] bal = withR.Select(p => p.Result!.ReserveBalanceCoef).ToArray();

        var w = new DecisionWeights
        {
            StablePlateau = withR.Average(p => p.Decision.StablePlateau), PeakShaving = withR.Average(p => p.Decision.PeakShaving),
            EarlyCapacity = withR.Average(p => p.Decision.EarlyCapacity), InnerDumpRate = withR.Average(p => p.Decision.InnerDumpRate),
            Npv = withR.Average(p => p.Decision.Npv), ReserveBalance = withR.Average(p => p.Decision.ReserveBalance),
        };
        double wsum = w.Sum <= 0 ? 1 : w.Sum;

        LongTermPlan? best = null; double bestScore = -1;
        for (int i = 0; i < withR.Count; i++)
        {
            double s = NormHigh(plateau, i) * w.StablePlateau + NormLow(peak, i) * w.PeakShaving + NormLow(ttc, i) * w.EarlyCapacity
                     + NormHigh(inner, i) * w.InnerDumpRate + NormHigh(npv, i) * w.Npv + NormHigh(bal, i) * w.ReserveBalance;
            double score = s / wsum * 100;
            withR[i].Result!.CompositeScore = Math.Round(score, 0);
            if (withR[i].Result!.Ok && score > bestScore) { bestScore = score; best = withR[i]; }
        }
        if (best == null)
            foreach (var p in withR)
                if (p.Result!.CompositeScore > bestScore) { bestScore = p.Result!.CompositeScore; best = p; }
        return best?.Name ?? "—";
    }

    private static double NormHigh(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NormLow(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }
}
