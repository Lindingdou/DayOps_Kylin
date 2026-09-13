// 忠实移植自原 PitMine3D Modules/TaskLib/Adjust/StageSimPlayer.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Simulation;

namespace PitMine3D.Kylin.TaskLib.Adjust;

// ─────────────────────────────────────────────────────────────────────────────
//  单环节演示 —— 点开甘特上的一格，在三维里放「那一天、那个区域、那道工序」。
//
//  ── 画面由三层叠成（每层各回答一个问题）──
//    ① 全部区域的轮廓（暗）        ：这块地在全矿的哪儿；
//    ② 本区域到**昨天**为止的位置  ：前面那些天已经推到哪了（累计推进）；
//    ③ 本日推进带（亮 + 会走）     ：今天这道工序要吃掉/堆出的那一条。
//  ①②静止，③随播放进度在「昨天位置 → 今天位置」之间扫。停在 100% 就是当日期末形态。
//
//  ── 走 overlay 批量组，不走实体 ──
//  逐帧动画走实体有三条死路（建删压 Undo / 没有变换矩阵 / 显隐不 MarkRenderDirty），
//  见 SimDynamicOverlay.cs 头注。本类每帧每组一次 P/Invoke，帧末一次 RequestRender。
//  组名统一 "ADJ:" 前缀 —— 内核侧还会再加 "X:"，与动态模拟窗的组构造上撞不上，
//  两个窗口同时开着也不会互相擦掉。
//
//  ── 三条不糊弄的地方 ──
//  · 推进距离解不出（缺 H/L）就**不动轮廓**，只画区域和标签，并把原因写进 Notes。
//    宁可画面上"没推进"，也不拿缺省 H=12/L=1100 推出一个看着很像的假距离。
//  · 区域高程拿不到（既无台账 xyz 也采不到现状面）时按 0 m 摆，并明说绝对高程不可信。
//  · **没有相机 API**：IViewCapability/IPitDesignCapability 都不提供 ZoomTo/SetCamera，
//    所以本类做不到「点开自动飞到该区域」。给出的是区域中心的世界坐标，由人自己转过去。
//    这一条如实写在 Notes 里，不做假的"已定位"提示。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次演示的解算结果（画了什么 + 有什么没画成）。</summary>
public sealed class StagePlayInfo
{
    public bool Ok { get; set; }
    /// <summary>一行摘要（界面标题旁显示）。</summary>
    public string Headline { get; set; } = "";
    /// <summary>逐条口径 / 降级说明。</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>本日推进距离 m（NaN = 解不出）。</summary>
    public double AdvanceM { get; set; } = double.NaN;
    /// <summary>到昨天为止的累计推进 m。</summary>
    public double CumBeforeM { get; set; }
    /// <summary>本日量（采装实方 / 排土占容）。</summary>
    public double VolumeM3 { get; set; }
    /// <summary>区域中心世界坐标（人工转视角用；区域缺失时为 NaN）。</summary>
    public double CenterX { get; set; } = double.NaN;
    public double CenterY { get; set; } = double.NaN;
    public double CenterZ { get; set; } = double.NaN;
    /// <summary>该区域是否落在已装载的正射底图范围内（0..1 顶点比例；未装底图为 -1）。</summary>
    public double BasemapCoverage { get; set; } = -1;
    public bool RegionMatched { get; set; }

    /// <summary>
    /// 本格**聚焦对象**的世界包围盒（期初环 ∪ 期末环）。无区域时全 NaN。
    /// <para>不是全矿包围盒 —— 相机要装的是"这一格"，不是"整个矿"。</para>
    /// </summary>
    public double FocusMinX { get; set; } = double.NaN;
    public double FocusMinY { get; set; } = double.NaN;
    public double FocusMaxX { get; set; } = double.NaN;
    public double FocusMaxY { get; set; } = double.NaN;
    /// <summary>焦点包围盒解出来了吗（NaN 判一次即可，省得调用方逐个判）。</summary>
    public bool HasFocusBox => !double.IsNaN(FocusMinX) && FocusMaxX > FocusMinX && FocusMaxY > FocusMinY;

    /// <summary>推进方位解算结果（Resolved=false 表示本次走的是全周等距）。</summary>
    public AdvanceAzimuthInfo Azimuth { get; set; } = AdvanceAzimuthInfo.No("未解算");

    // ── 逐班（一天之内的三班状态）────────────────────────────────────────────
    /// <summary>本环节当日的各班分段，按开班时刻排序。</summary>
    public List<ShiftSlice> Shifts { get; set; } = new();
    /// <summary>当前播放到的当日时刻 h（0..24）。</summary>
    public double ClockHour { get; set; }
    /// <summary>当前时刻所在的班次名（在班间空档时为空串）。</summary>
    public string ActiveShift { get; set; } = "";
    /// <summary>当前班已走完的比例 0..1（不在任何班内时为 0）。</summary>
    public double ActiveShiftProgress { get; set; }
    /// <summary>到当前时刻为止本日已完成的量 m³。</summary>
    public double DoneTodayM3 { get; set; }

    public string ClockText
    {
        get
        {
            int h = (int)ClockHour, m = (int)Math.Round((ClockHour - h) * 60);
            if (m == 60) { h++; m = 0; }
            return $"{h:00}:{m:00}";
        }
    }
}

/// <summary>一天之内的一个班次分段。</summary>
public sealed class ShiftSlice
{
    public string Shift { get; set; } = "";
    public double StartHour { get; set; }
    public double EndHour { get; set; }
    /// <summary>本班计划量（采装实方 / 排土占容）。</summary>
    public double VolumeM3 { get; set; }
    /// <summary>本班实绩量（没录为 0）。</summary>
    public double ActualM3 { get; set; }
    public string MainEquip { get; set; } = "";
    public int EquipCount { get; set; }
    /// <summary>本班的未完成原因码（只有真实天才可能有）。</summary>
    public List<IncompleteReason> Reasons { get; set; } = new();
    public double FaultHours { get; set; }

    public double SpanH => Math.Max(0, EndHour - StartHour);
    public bool IsAbnormal => Reasons.Any(r => r != IncompleteReason.OverAchieved);

    /// <summary>到时刻 <paramref name="h"/> 为止本班已完成的比例 0..1（按时间线性）。</summary>
    public double FractionAt(double h)
        => SpanH <= 1e-9 ? (h >= EndHour ? 1 : 0)
                         : Math.Clamp((h - StartHour) / SpanH, 0, 1);

    public string Caption =>
        $"{(Shift.Length > 0 ? Shift : "未分班")}　{Hm(StartHour)}–{Hm(EndHour)}"
        + (VolumeM3 > 1e-6 ? $"　{VolumeM3:N0} m³" : "")
        + (MainEquip.Length > 0 ? $"　{MainEquip}" : "")
        + (EquipCount > 1 ? $"（{EquipCount} 台）" : "");

    private static string Hm(double h)
    {
        int hh = (int)h, mm = (int)Math.Round((h - hh) * 60);
        if (mm == 60) { hh++; mm = 0; }
        return $"{hh:00}:{mm:00}";
    }
}

/// <summary>
/// 单环节演示的落地端。构造一次（装区域集与采场参数），之后逐格 <see cref="Play"/>。
/// 永不抛。
/// </summary>
public sealed class StageSimPlayer
{
    // 组名：内核侧会再加 "X:" 前缀，这里只要自己内部不撞即可
    public const string GRegions = "ADJ:regions";
    public const string GBefore = "ADJ:before";
    public const string GCurrent = "ADJ:current";
    public const string GTarget = "ADJ:target";
    public const string GBand = "ADJ:band";
    public const string GEquip = "ADJ:equip";
    public const string GLabel = "ADJ:label";

    private static readonly string[] AllGroups =
        { GRegions, GBefore, GCurrent, GTarget, GBand, GEquip, GLabel };

    private ISimDynamicOverlay _sink;
    private SimRegionSet _regions = new();
    private SimMiningParams _prm = new();

    public StageSimPlayer(ISimDynamicOverlay? sink = null)
        => _sink = sink ?? SimDynamicOverlay.Current;

    /// <summary>
    /// 换落地端（窗内面板 ↔ 内核主视口）。
    /// <para><b>换之前先把旧端抹干净</b>：两条落地端各画各的，不抹的话旧端会留下一张
    /// 再也不会更新的残影 —— 那比"没画"更坏，人会对着一张停在上一格的图读数。</para>
    /// </summary>
    public void SwitchSink(ISimDynamicOverlay sink)
    {
        if (sink == null || ReferenceEquals(sink, _sink)) return;
        try { Stop(); } catch { }
        _sink = sink;
    }

    /// <summary>
    /// 铭牌字高（世界米）。默认 4.5 —— 主视口那种大画面下合适。
    /// <para><b>为什么要能调</b>：字高是世界米，投出来的像素 = 字高 × 相机尺度，
    /// 而落地端对 &lt;6px 的字一律省绘。窗内面板只有几百像素宽，把一块 300 m 的区域装满时
    /// 尺度约 1 px/m ⇒ 4.5 m 的字只有 4.5 px，<b>一个字都不会画出来</b>。
    /// 调用方按当前尺度反推一个下限即可（见 DynamicAdjustWindow）。</para>
    /// </summary>
    public double LabelHeightM { get; set; } = 4.5;

    public bool Available => _sink.Available;
    public string StatusLabel => _sink.StatusLabel;
    public SimRegionSet Regions => _regions;

    /// <summary>
    /// 本期工序作业区的轮廓索引（按 工序 + 面名 取）。null / 空 = 全部退面级轮廓。
    /// <para><b>它只换几何</b>：类别（推进极性）、汇绑定、边坡角一律沿用面级那块区域。</para>
    /// </summary>
    private SimProcessRegionSet? _procRegions;

    /// <summary>装区域轮廓与采场几何参数。开窗时调一次；台账改过再调。</summary>
    public string Reload()
    {
        _azCache.Clear();          // 方位是从台账反算的，台账重读了就得重算
        try
        {
            SinkRegistry? sinks = null;
            try { sinks = SinkRegistryLoader.Current; } catch { }
            _regions = SimRegionLoader.Load(sinks);
            // 工序轮廓：让每道工序演在自己那块地上（S1–S3）。装不到就整体退面级轮廓，
            // 与这一层出现之前逐字一致 —— 差别只在文案上说清楚了退没退。
            try { _procRegions = SimProcessRegions.Load(PeriodOfWorkDate(), _regions); }
            catch { _procRegions = null; }
        }
        catch { _regions = new SimRegionSet(); }

        try { _prm = SimMiningParams.Load(); } catch { _prm = new SimMiningParams(); }

        return _regions.IsEmpty
            ? "区域轮廓：一块都没装到（既无可采区域台账，也无去向台账可据以造示意图）——演示只能出标签。"
            : _regions.SourceLabel;
    }

    /// <summary>抹掉全部演示图元（关窗 / 换格前调）。幂等。</summary>
    public void Stop()
    {
        foreach (var g in AllGroups) _sink.Clear(g);
        _sink.RequestRender();
    }

    /// <summary>
    /// 放一格。<paramref name="progress"/> ∈ [0,1]：0 = 当日期初形态，1 = 当日期末形态；
    /// 由调用方用定时器推着走即成动画。
    /// </summary>
    /// <param name="cell">甘特上被点开的那一格。</param>
    /// <param name="tl">整条时间轴（算「到昨天为止」的累计推进要用）。</param>
    public StagePlayInfo Play(StageGanttCell cell, DayStageTimeline tl, double progress)
    {
        var info = new StagePlayInfo();
        try { return PlayCore(cell, tl, Math.Clamp(double.IsNaN(progress) ? 1 : progress, 0, 1), info); }
        catch (Exception ex)
        {
            info.Headline = $"演示解算失败：{ex.GetType().Name}";
            info.Notes.Add("已抹掉图元避免留下半张画面。");
            try { Stop(); } catch { }
            return info;
        }
    }

    private StagePlayInfo PlayCore(StageGanttCell cell, DayStageTimeline tl, double t, StagePlayInfo info)
    {
        info.VolumeM3 = cell.TargetVolumeM3;

        // ── 找区域 ──
        //  先问「这道工序在这个面上的那块地」（工序作业区），取不到才退面级轮廓。
        //  退没退要说出来（S3）：两者画面上分不出来，说的却是两件事 ——
        //  面级轮廓意味着这一格演在**整个作业面**上，而不是这道工序真正待的那条带。
        var region = _procRegions?.Find(cell.Process, cell.Region);
        bool byProcess = region != null;
        region ??= MatchRegion(cell.Region);
        info.RegionMatched = region != null;

        if (!_sink.Available)
        {
            info.Headline = "三维通道未接上，本格只能看明细。";
            info.Notes.Add(_sink.StatusLabel);
            return info;
        }

        // 背景：全部区域轮廓（暗），让人知道这块地在全矿的哪儿
        PushRegionContext(region);

        if (region == null)
        {
            _sink.Clear(GBefore); _sink.Clear(GCurrent); _sink.Clear(GTarget);
            _sink.Clear(GBand); _sink.Clear(GEquip);
            PushLabels(new List<(double, double, double, string, uint)>());
            _sink.RequestRender();
            info.Headline = $"「{cell.Region}」在区域台账里找不到对应轮廓。";
            info.Notes.Add("环节的量与班次是真的，但没有轮廓就画不出推进带。"
                         + "请在「采场/排土场圈定」里圈出这块地，或让作业面名与区域名对得上"
                         + $"（当前已装区域：{(_regions.IsEmpty ? "无" : string.Join("、", _regions.Regions.Select(r => r.Name)))}）。");
            return info;
        }

        info.Notes.Add(byProcess
            ? $"轮廓取自**工序作业区**（{cell.Process.Label()}）—— 这一格演的是这道工序自己那块地，"
            + "不是整个作业面。"
            : $"轮廓取自**面级区域**「{region.Name}」—— 本期没有「{cell.Process.Label()}」的工序作业区，"
            + "所以这一格演在整个作业面上，位置不区分工序。"
            + "（到「作业区划分 · 工序作业区」生成并入库即可按工序分开演。）");

        double z = double.IsNaN(region.Z) ? 0 : region.Z;
        info.CenterX = region.Centroid.X; info.CenterY = region.Centroid.Y; info.CenterZ = z;
        info.BasemapCoverage = OrthophotoBasemap.IsLoaded ? OrthophotoBasemap.CoverageOf(region) : -1;

        // ── 推进距离 ──
        // 只有量型工序谈得上推进；穿孔/爆破/检修画的是"这块地今天有这道工序"，轮廓不动。
        bool volumetric = cell.IsVolumeProcess;
        double cumBefore = 0, today = double.NaN;

        if (volumetric)
        {
            (cumBefore, today) = Advances(cell, tl, region);
            info.CumBeforeM = cumBefore;
            info.AdvanceM = today;
        }

        bool outward = region.IsDump;
        var baseRing = region.Ring;

        // 推进方位：解得出就走**定向平移**（真实平行推进），解不出退全周等距，绝不猜方向
        var az = AzimuthOf(region);
        var mode = az.Resolved ? SimAdvanceMode.Directional : SimAdvanceMode.Uniform;
        double azDeg = az.Resolved ? az.AzimuthDeg : 0;
        info.Azimuth = az;

        var beforeRing = double.IsNaN(today) && cumBefore <= 1e-9
            ? baseRing.ToList()
            : RingOffset.Offset(baseRing, cumBefore, outward, mode, azDeg);

        double todayAdv = double.IsNaN(today) ? 0 : today;

        // ── 一天之内按**班**走，不是一段连续推进 ──
        //  进度条映射的是当日**时钟**（从最早开班到最晚收班），而不是"完成度"。
        //  量按各班自己的时窗线性累积 ⇒ 班间空档时轮廓不动、时钟照走，
        //  这正是现场的样子：不排班的那几个小时确实没有推进。
        var slices = SliceShifts(cell);
        info.Shifts = slices;

        double dayDone = DayFraction(slices, t, out double clock, out string active,
                                     out double activeFrac, out var onShift);
        info.ClockHour = clock;
        info.ActiveShift = active;
        info.ActiveShiftProgress = activeFrac;
        info.DoneTodayM3 = cell.TargetVolumeM3 * dayDone;

        var targetRing = RingOffset.Offset(baseRing, cumBefore + todayAdv, outward, mode, azDeg);
        var currentRing = RingOffset.Offset(baseRing, cumBefore + todayAdv * dayDone, outward, mode, azDeg);

        // 焦点包围盒取**期初 ∪ 期末**：只取区域轮廓的话，排土那种向外推的会推出画面。
        SetFocusBox(info, beforeRing, targetRing);

        // ── 推：期初（暗）/ 当前（亮）/ 期末（虚，目标）/ 推进带横档 ──
        uint proc = ProcArgb(cell.Process);
        PushRing(GBefore, beforeRing, z, 0x66888888);
        PushRing(GTarget, targetRing, z, Fade(proc, 0x55));
        PushRing(GCurrent, currentRing, z, proc);
        PushBand(beforeRing, currentRing, z, Fade(proc, 0xAA));

        // ── 设备符号：只画**当前在班**那一段的编组；班间空档不画（那时候现场没人）──
        PushEquip(onShift?.EquipCount ?? 0, currentRing, z, proc);

        // ── 铭牌 ──
        var labels = new List<(double, double, double, string, uint)>
        {
            (region.Centroid.X, region.Centroid.Y, z + 10,
             $"{cell.Region} · {cell.Process.Label()}　{cell.Date:MM-dd}", proc),
        };

        // 第二行：当日时钟 + 当前班次（班间空档明说"停"，别让人以为卡住了）
        labels.Add((region.Centroid.X, region.Centroid.Y, z + 6,
            onShift != null
                ? $"{info.ClockText}　{(onShift.Shift.Length > 0 ? onShift.Shift : "在班")}"
                  + $"　{info.ActiveShiftProgress * 100:0}%"
                  + (onShift.MainEquip.Length > 0 ? $"　{onShift.MainEquip}" : "")
                : $"{info.ClockText}　班间空档（不排班，无推进）",
            onShift != null ? Fade(proc, 0xEE) : 0x88888888));

        if (volumetric)
        {
            string unit = cell.Process == ProcessType.Dump ? "m³占容" : "m³实方";
            string adv = double.IsNaN(today) ? "推进距离未解出" : $"本日推进 {today:0.##} m";
            labels.Add((region.Centroid.X, region.Centroid.Y, z + 2,
                        $"{info.DoneTodayM3:N0} / {cell.TargetVolumeM3:N0} {unit}　{adv}", Fade(proc, 0xCC)));
        }
        PushLabels(labels);

        _sink.RequestRender();

        // ── 摘要与口径 ──
        info.Ok = true;
        info.Headline = volumetric
            ? (double.IsNaN(today)
                ? $"{cell.Region} · {cell.Process.Label()}　{cell.TargetVolumeM3:N0} m³（推进距离未解出，轮廓不动）"
                : $"{cell.Region} · {cell.Process.Label()}　{cell.TargetVolumeM3:N0} m³ ⇒ 本日推进 {today:0.##} m"
                  + $"（累计 {cumBefore + today:0.##} m）")
            : $"{cell.Region} · {cell.Process.Label()}　本日有此工序（非量型，轮廓不动）";

        if (cell.Projected)
            info.Notes.Add("本格是**推算天**：量沿用当日盘子的日目标，不是有人排出来的那一天。");
        else
            info.Notes.Add("本格是**当日盘子的真任务**"
                         + (cell.ActualVolumeM3 > 1e-6
                             ? $"，已录实绩 {cell.ActualVolumeM3:N0} m³（达成 {cell.ActualVolumeM3 / Math.Max(1e-6, cell.TargetVolumeM3) * 100:0}%）。"
                             : "，尚未录实绩。"));

        if (volumetric)
        {
            if (double.IsNaN(today))
                info.Notes.Add("⚠ 推进距离解不出（缺台阶高 H / 工作线长 L）⇒ 轮廓**原地不动**。"
                             + "不拿缺省 H/L 推一个看着很像的假距离 —— 在「动态模拟」里填好 H/L 并应用后重开本窗即可。");
            else
                info.Notes.Add($"推进反算：v = V ÷ (L × H) = {cell.TargetVolumeM3:N0} ÷ ({_prm.WorkLineLengthM:0} × {_prm.BenchHeightM:0.##}) "
                             + $"= {today:0.###} m。{_prm.SourceLabel}");
            info.Notes.Add(outward ? "排土类区域按**外扩**推进（堆填往外长）。"
                                   : "采场类区域按**内缩**推进（挖除往里退）。");
            info.Notes.Add(info.Azimuth.Resolved
                ? "轮廓走**定向平移**（平行推进）：" + info.Azimuth.SourceLabel
                : "轮廓走**全周等距**（不是平行推进）：" + info.Azimuth.SourceLabel);
        }

        info.Notes.Add(double.IsNaN(region.Z)
            ? "⚠ 该区域高程取不到（台账无 xyz，现状面也采不到）⇒ 按 0 m 摆放：形状与推进量可信，**绝对高程不可信**。"
            : $"层位高程 {z:0.##} m　来源：{(region.ZSource.Length > 0 ? region.ZSource : "可采区域台账")}");

        if (region.Synthetic)
            info.Notes.Add("⚠ 本区轮廓是**示意图形**（未圈画可采区域）：形状不代表真实边界，推进距离与量是真的。");

        if (OrthophotoBasemap.IsLoaded)
        {
            if (info.BasemapCoverage <= 0)
                info.Notes.Add("⚠ 本区**完全落在正射底图之外**，演示画面下没有影像。请核对影像与工程是否同一套坐标系。");
            else if (info.BasemapCoverage < 0.999)
                info.Notes.Add($"本区有 {info.BasemapCoverage * 100:0}% 的轮廓点落在底图范围内，出框那一侧没有影像。");
        }
        else
        {
            info.Notes.Add("未装正射底图：演示画在素色地表上。点上方「载入正射影像」贴一张当期航拍图，"
                         + "推进带压在哪条路、哪个平盘上就能直接核对。");
        }

        info.Notes.Add($"区域中心 X={region.Centroid.X:0.##} Y={region.Centroid.Y:0.##} Z={z:0.##}　"
                     + "—— 宿主没有提供相机 API，本窗**不会自动飞过去**，请自行在三维视口里转到该坐标。");

        return info;
    }

    // ── 一天之内的逐班分段 ────────────────────────────────────────────────────

    /// <summary>
    /// 把一格拆成**按开班时刻排序**的班次分段。
    /// <para>甘特一格 = 某天某区域某工序，它下面可能挂着早/中/夜三条
    /// （<c>DayStagePlanBuilder.Merge</c> 只合并「同区域+同工序+同班次」，班次不同不合）。
    /// 演示要放的就是这三段。</para>
    /// </summary>
    private static List<ShiftSlice> SliceShifts(StageGanttCell cell)
        => cell.Stages
            .Select(s => new ShiftSlice
            {
                Shift = s.Shift,
                StartHour = s.StartHour,
                EndHour = s.EndHour > s.StartHour ? s.EndHour : s.StartHour + 8,   // 时窗缺失按 8h 班兜底
                VolumeM3 = s.TargetVolumeM3,
                ActualM3 = s.ActualVolumeM3,
                MainEquip = s.MainEquip,
                EquipCount = s.EquipCount,
                Reasons = new List<IncompleteReason>(s.Reasons),
                FaultHours = s.FaultHours,
            })
            .OrderBy(x => x.StartHour).ThenBy(x => x.Shift, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 把播放进度 <paramref name="t"/> 映射成当日时钟，并算出到那个时刻为止已完成的**量的比例**。
    ///
    /// <para><b>进度条走的是时钟，不是完成度。</b>两者在有空档的日子里不一样：
    /// 早班 0–8 干完、中班 8–16 不排、夜班 16–24 再干，那么 t=0.5（12 点）时
    /// 完成度是 50%（早班那一半），而不是 50% 的时间对应 50% 的量。
    /// 按时钟走才能在画面上看出「这几个小时是停着的」。</para>
    ///
    /// <para>量在各班**自己的时窗内**按时间线性累积 —— 没有更细的出处（任务只有起止和总量），
    /// 不假装知道班内的产量曲线。</para>
    /// </summary>
    private static double DayFraction(List<ShiftSlice> slices, double t,
                                      out double clock, out string active, out double activeFrac,
                                      out ShiftSlice? onShift)
    {
        active = ""; activeFrac = 0; onShift = null;

        if (slices.Count == 0) { clock = 24 * t; return t; }

        double lo = slices.Min(s => s.StartHour);
        double hi = slices.Max(s => s.EndHour);
        if (hi <= lo) { clock = lo; return t; }

        clock = lo + (hi - lo) * t;

        double total = slices.Sum(s => s.VolumeM3);
        double done = 0;
        foreach (var s in slices)
        {
            double f = s.FractionAt(clock);
            done += s.VolumeM3 * f;
            if (clock >= s.StartHour && clock < s.EndHour) { active = s.Shift; activeFrac = f; onShift = s; }
        }

        // 收班瞬间（t=1，clock 正好等于最晚收班时刻）按**最后一个班仍在班**处理。
        // 严格用 [start, end) 的话期末那一帧没有任何班在班 ⇒ 设备符号消失、铭牌写"班间空档"，
        // 而"点开即停在收班时刻"正是默认视图 —— 那一眼看到的就会是空场。
        if (onShift == null && clock >= hi - 1e-9)
        {
            onShift = slices[^1];
            active = onShift.Shift; activeFrac = 1;
        }

        // 非量型工序（穿孔/爆破/检修）没有量 ⇒ 退回按时钟比例，否则轮廓永远不动
        if (total <= 1e-6) return Math.Clamp((clock - lo) / (hi - lo), 0, 1);
        return Math.Clamp(done / total, 0, 1);
    }

    // ── 推进量累计 ────────────────────────────────────────────────────────────

    /// <summary>
    /// (到昨天为止的累计推进 m, 本日推进 m)。本日解不出时返回 NaN。
    /// <para>累计只数**同一区域同一工序**的天 —— 把采装与排土的推进加在一起没有意义，
    /// 一个往里退一个往外长。</para>
    /// </summary>
    private (double CumBefore, double Today) Advances(StageGanttCell cell, DayStageTimeline tl, SimRegion region)
    {
        if (!_prm.Usable) return (0, double.NaN);

        double cum = 0;
        for (int i = 0; i < cell.DayIndex && i < tl.Days.Count; i++)
        {
            double v = tl.Days[i].Stages
                .Where(s => string.Equals(s.RegionName, cell.Region, StringComparison.OrdinalIgnoreCase)
                         && s.Process == cell.Process)
                .Sum(s => s.TargetVolumeM3);
            if (v > 1e-6) cum += _prm.AdvanceMetersFor(v);
        }
        double today = cell.TargetVolumeM3 > 1e-6 ? _prm.AdvanceMetersFor(cell.TargetVolumeM3) : 0;
        if (double.IsNaN(cum) || double.IsInfinity(cum)) cum = 0;
        return (cum, today);
    }

    // ── 推进方位（逐区域缓存）─────────────────────────────────────────────────

    private readonly Dictionary<string, AdvanceAzimuthInfo> _azCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 取该区域的推进方位。<b>必须缓存</b>：<see cref="Play"/> 在播放时每 40 ms 调一次，
    /// 而解算要把整个采掘单元台账目录读一遍 —— 逐帧读盘会把动画拖死。
    /// 台账改了就 <see cref="Reload"/>（那里会清缓存）。
    /// </summary>
    private AdvanceAzimuthInfo AzimuthOf(SimRegion region)
    {
        string key = region.Name ?? "";
        if (_azCache.TryGetValue(key, out var hit)) return hit;
        var info = RegionAdvanceAzimuth.Resolve(region);
        _azCache[key] = info;
        return info;
    }

    // ── 区域匹配 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 按名字找区域。对不上返回 null（不拿「只有一块就用它」糊弄）。
    ///
    /// <para><b>模糊配对直接调 <see cref="SimPlanScene.NameHit"/>，不自己再写一份。</b>
    /// 那个函数是推演真正用来把区域对上源/汇的判据，「作业区划分」窗口的联动诊断
    /// （<c>TaskLib.Zoning.ZoneLinkage</c>）也调它。三处各抄一份的话，改一处就悄悄分家 ——
    /// 诊断面板报「这块区域对得上」，而演示里它一动不动。本方法此前就是自己抄的一份。</para>
    ///
    /// <para>唯一的加码是**先试全等**：同名的那块优先，避免"1号采场"被"1号采场东"抢走。
    /// 这只是在 NameHit 的候选里排个序，判据本身没有第二套。</para>
    /// </summary>
    /// <summary>当前工作日期所在月 —— 工序作业区是按期存的。</summary>
    private static string PeriodOfWorkDate()
    {
        try { return TaskLib.Engine.ProjectScope.WorkDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture); }
        catch { return DateTime.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture); }
    }

    private SimRegion? MatchRegion(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _regions.IsEmpty) return null;
        string n = name.Trim();

        var exact = _regions.Regions.FirstOrDefault(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        return _regions.Regions.FirstOrDefault(r => SimPlanScene.NameHit(r.Name, n));
    }

    // ── 推送 ─────────────────────────────────────────────────────────────────

    /// <summary>全部区域轮廓（暗），聚焦的那块跳过（由 GCurrent 亮画）。</summary>
    private void PushRegionContext(SimRegion? focus)
    {
        var segs = new List<double>();
        var cols = new List<uint>();
        foreach (var r in _regions.Regions)
        {
            if (focus != null && ReferenceEquals(r, focus)) continue;
            double z = double.IsNaN(r.Z) ? 0 : r.Z;
            uint c = r.IsDump ? 0x33854F0Bu : 0x33185FA5u;
            AppendRing(segs, cols, r.Ring, z, c);
        }
        _sink.SetLines(GRegions, segs.ToArray(), cols.ToArray(), cols.Count);
    }

    private static void SetFocusBox(StagePlayInfo info, params IReadOnlyList<SimPoint>[] rings)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var r in rings)
        {
            if (r == null) continue;
            foreach (var p in r)
            {
                if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
                y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
            }
        }
        if (x1 <= x0 || y1 <= y0) return;
        info.FocusMinX = x0; info.FocusMinY = y0; info.FocusMaxX = x1; info.FocusMaxY = y1;
    }

    private void PushRing(string group, IReadOnlyList<SimPoint> ring, double z, uint argb)
    {
        var segs = new List<double>();
        var cols = new List<uint>();
        AppendRing(segs, cols, ring, z, argb);
        _sink.SetLines(group, segs.ToArray(), cols.ToArray(), cols.Count);
    }

    /// <summary>推进带：期初环与当前环之间逐顶点连横档（overlay 只有线，没有真填充面）。</summary>
    private void PushBand(IReadOnlyList<SimPoint> a, IReadOnlyList<SimPoint> b, double z, uint argb)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n < 3) { _sink.Clear(GBand); return; }

        var segs = new List<double>(n * 6);
        var cols = new List<uint>(n);
        for (int i = 0; i < n; i++)
        {
            // 顶点没动就不画横档（0 长度线段在内核侧是一个退化图元）
            if (Math.Abs(a[i].X - b[i].X) < 1e-9 && Math.Abs(a[i].Y - b[i].Y) < 1e-9) continue;
            segs.Add(a[i].X); segs.Add(a[i].Y); segs.Add(z);
            segs.Add(b[i].X); segs.Add(b[i].Y); segs.Add(z);
            cols.Add(argb);
        }
        _sink.SetLines(GBand, segs.ToArray(), cols.ToArray(), cols.Count);
    }

    /// <summary>
    /// 设备符号：沿当前作业线均布，台数 = **当前在班**那一段的编组规模（钳 0..24）。
    /// <para>班间空档传 0 ⇒ 抹掉该组：那几个小时现场确实没人，画着设备是假的。</para>
    /// </summary>
    private void PushEquip(int equipCount, IReadOnlyList<SimPoint> ring, double z, uint argb)
    {
        int n = Math.Clamp(equipCount, 0, 24);
        if (n == 0 || ring.Count < 2) { _sink.Clear(GEquip); return; }

        var pts = new List<double>(n * 3);
        var cols = new List<uint>(n);
        var size = new List<float>(n);
        var style = new List<byte>(n);

        for (int i = 0; i < n; i++)
        {
            var p = ring[(int)((long)i * ring.Count / n) % ring.Count];
            pts.Add(p.X); pts.Add(p.Y); pts.Add(z + 1.5);
            cols.Add(argb);
            size.Add(7f);
            style.Add((byte)SimMarkerStyle.Dot);
        }
        _sink.SetMarkers(GEquip, pts.ToArray(), cols.ToArray(), size.ToArray(), style.ToArray(), n);
    }

    private void PushLabels(List<(double X, double Y, double Z, string Text, uint Argb)> items)
    {
        if (items.Count == 0) { _sink.Clear(GLabel); return; }
        var pts = new List<double>(items.Count * 3);
        var txt = new List<string>(items.Count);
        var cols = new List<uint>(items.Count);
        var hgt = new List<float>(items.Count);
        var ha = new List<byte>(items.Count);
        var va = new List<byte>(items.Count);
        foreach (var it in items)
        {
            pts.Add(it.X); pts.Add(it.Y); pts.Add(it.Z);
            // ★ 铭牌一律提亮：两条落地端的底都是深色（主视口默认 #1A1A26、
            //   窗内面板 #0B121F），再叠上正射影像之后更暗。工序本色（如 0xFF185FA5）
            //   画线够用，写字就糊在底图里了 —— 提亮之后仍保留工序色相，认得出是哪道工序。
            txt.Add(it.Text); cols.Add(Lighten(it.Argb, 0.60));
            hgt.Add((float)LabelHeightM);   // 世界米字高：随缩放变大变小（见 LabelHeightM）
            ha.Add(1); va.Add(1); // 居中
        }
        // ★ 沿世界 Y 推开重叠的牌子。
        //   三行铭牌原本只靠 z 分层（z+10 / z+6 / z+2）—— 那在俯视下**完全不管用**：
        //   屏幕上方 = ry·sin(tilt) + dz·cos(tilt)，tilt=90° 时 dz 的系数是 0，
        //   三行字投到同一个点，叠成一坨。轴测下也只有几米高差 ⇒ 小尺度时仍是亚像素。
        //   复用短期窗那份避让（只沿 Y 推、只推被挡住的那个、顺序不变），行距随字高走。
        Simulation.LabelDeCollide.DeCollideLabels(pts, hgt);

        _sink.SetLabels(GLabel, pts.ToArray(), txt, cols.ToArray(), hgt.ToArray(),
                        ha.ToArray(), va.ToArray(), items.Count);
    }

    /// <summary>把颜色朝白色混 <paramref name="k"/>（0=原色，1=纯白）。透明度不动。</summary>
    private static uint Lighten(uint argb, double k)
    {
        k = Math.Clamp(k, 0, 1);
        uint a = argb >> 24;
        uint r = (uint)Math.Round(((argb >> 16) & 0xFF) + (255 - ((argb >> 16) & 0xFF)) * k);
        uint g = (uint)Math.Round(((argb >> 8) & 0xFF) + (255 - ((argb >> 8) & 0xFF)) * k);
        uint b = (uint)Math.Round((argb & 0xFF) + (255 - (argb & 0xFF)) * k);
        return (a << 24) | (Math.Min(r, 255) << 16) | (Math.Min(g, 255) << 8) | Math.Min(b, 255);
    }

    private static void AppendRing(List<double> segs, List<uint> cols,
                                   IReadOnlyList<SimPoint> ring, double z, uint argb)
    {
        if (ring == null || ring.Count < 2) return;
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % ring.Count];
            segs.Add(p.X); segs.Add(p.Y); segs.Add(z);
            segs.Add(q.X); segs.Add(q.Y); segs.Add(z);
            cols.Add(argb);
        }
    }

    /// <summary>工序色（与甘特/日甘特逐色对齐 —— 跨图认色的前提）。</summary>
    private static uint ProcArgb(ProcessType p) => p switch
    {
        ProcessType.Drill => 0xFF534AB7,
        ProcessType.Load => 0xFF185FA5,
        ProcessType.Haul => 0xFF0F6E56,
        ProcessType.Dump => 0xFF854F0B,
        ProcessType.Blast => 0xFFA32D2D,
        _ => 0xFF5F5E5A,
    };

    private static uint Fade(uint argb, byte alpha) => (argb & 0x00FFFFFFu) | ((uint)alpha << 24);
}
