// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimEquipMotionStage.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  会动的设备符号 —— 把「本月在这个单元」升级成「本月从这里推进到那里」
//
//  ── 为什么另开一个舞台，而不是把 EquipmentStage 改成会动的 ──
//  EquipmentStage 走的是**实体**（BuildColoredMeshOnLayer，带体积的多色实心块）。
//  它动不了，原因在它自己的文件头 :25-61 已经逐条核死，至今仍成立：
//    · IEntityCapability **没有变换矩阵** —— 建出来就钉在那儿；
//    · 逐帧删建 = 每帧十几条 Undo，几十帧刷爆 Undo 栈；
//    · 逐帧切显隐不 MarkRenderDirty（xllAcEd.cpp:11318），mesh 进缓存就一直画。
//  所以「让实体动起来」这条路不存在，本舞台走的是**动态 overlay 批量通道**
//  （<see cref="ISimDynamicOverlay"/>）：一帧一次 P/Invoke 换掉整组线段，不入库、不占 Undo。
//
//  ⇒ 两者是**两种介质，不是两套数据**：位置 / 走向 / 配色 / 状态全部吃 EquipmentStage 已经
//    解好的 <see cref="EquipSymbol"/>，本舞台一行都不重解。真轨查不到时的降级、
//    同单元多台的排布，那些账都在那边记着，本舞台不复制也不覆盖。
//
//  ── 介质差异：本舞台画的是**三维线框**，形状与实体层是同一份定义 ──
//  overlay 是 line list（内核 MarkerOverlayService 的 LineSpec），没有真填充，
//  所以实心块画不出来 —— 但**形态不必因此另起一套**：
//  <see cref="EquipSymbolLibrary"/> 已经抽成只依赖 <see cref="IEquipShapeSink"/>，
//  实体层落成三角面（<see cref="EquipMeshBuffer"/>）、本舞台落成边（<see cref="EquipWireBuffer"/>），
//  **角点集合逐点相同，只是连法不同**。所以两个图层里的同一台铲不可能长得不一样。
//  （上一版本舞台自己写了一套「底盘矩形 + 两笔特征线」的俯视轮廓 —— 那就是两份形态，
//    改一边另一边不动，而两边各自都自洽、都不报错。已删。）
//
//  ── R-E1 位移只有一个来源：所属作业面的期内推进 ──
//  设备在期内的位移 = 推进距离 × 期内相位 φ，方位取**窗口已有的推进方位**
//  （「平面推进示意」用的那一个，不另起一套参数）。
//  推进距离/方位任一没解出来 ⇒ 该设备**原地不动**并计数 —— 绝不编一条巡回路线。
//  「让它来回晃一晃看着像在干活」就是编位置：图上量出来的位移不再对应任何真实的量。
//
//  ── R-E2 期次相位 φ 与车流钟不是一个钟 ──
//  φ∈[0,1] 是「这一期走到哪儿了」，由期次钟给（月压成几秒）；
//  车流按真实车速走矿山时间。两者同屏但不同尺度，图例里写明白。
//
//  ── R-E3 卡车不在本舞台画 ──
//  卡车就是车流动点（<see cref="SimFlowStage"/>）。两边都画就成了双份车队：
//  图上数出来的车数是台账的两倍，而两边各自都自洽。
//
//  ── R-E4 尺寸是世界尺寸，不自动放大 ──
//  设备本来就有真实尺寸（铲 ~20 m）。自动放大到「看得见」之后，
//  图上量出来的「设备 vs 台阶」的大小关系就是假的。要放大只能由人在界面上显式给倍率，
//  且倍率必须写进图例。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>设备动态符号的显示口径。</summary>
public sealed class SimEquipMotionParams
{
    /// <summary>
    /// 本期推进距离 m（沿 <see cref="AdvanceAzimuthDeg"/>）。NaN / &lt;0 = 没解出来 ⇒ 设备不位移。
    /// 取自与「平面推进示意」同一个数（<c>SimFrame.MineAdvanceM</c>），不另算。
    /// </summary>
    public double AdvanceM { get; set; } = double.NaN;

    /// <summary>
    /// 推进方位角（度，与窗口的「方位(°)」同口径）。<see cref="AdvanceResolved"/> 为 false 时不用。
    /// </summary>
    public double AdvanceAzimuthDeg { get; set; }

    /// <summary>
    /// 推进方位是不是**单一方向**。「全周等距」推进模式下没有单一方位 ⇒ false ⇒ 设备不位移
    /// （沿某一个编出来的方位平移会让人以为工作面朝那边走）。
    /// </summary>
    public bool DirectionalAdvance { get; set; }

    /// <summary>符号总长 m（世界尺寸，铲/卡的量级）。</summary>
    public double SymbolLengthM { get; set; } = 18.0;

    /// <summary>符号放大倍率（R-E4：默认 1，改了必须写进图例）。夹在 [0.2, 20]。</summary>
    public double SymbolScale { get; set; } = 1.0;

    /// <summary>符号抬升 m —— 纯显示偏移，避与地表/层体 z-fight。</summary>
    public double LiftM { get; set; } = 2.0;

    /// <summary>画卡车吗。<b>默认关且不建议开</b>（R-E3：卡车是车流动点，开了就是双份车队）。</summary>
    public bool IncludeTrucks { get; set; }

    /// <summary>一帧最多画多少段（超了截断并报出来）。</summary>
    public int MaxSegments { get; set; } = 4000;

    /// <summary>
    /// 画设备铭牌（图上跟着符号跑的设备编号）。默认<b>开</b> ——
    /// 「这是一台铲」和「这是 EX3600_1 那台铲」是两个信息，后者才让人对得上排产表。
    /// </summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>铭牌世界字高 m（视口按相机投影出像素字号，&lt;6px 自动省绘）。</summary>
    public double LabelHeightM { get; set; } = 8.0;

    /// <summary>
    /// 铭牌里带上作业状态（「EX3600_1·检修」）。默认关：状态已经用明度表达了，
    /// 文字里再写一遍会把铭牌撑长、在缩小视图下互相压盖。
    /// </summary>
    public bool LabelWithState { get; set; }

    public bool AdvanceResolved =>
        DirectionalAdvance && !double.IsNaN(AdvanceM) && !double.IsInfinity(AdvanceM) && AdvanceM >= 0;

    public double Scale
    {
        get
        {
            double s = SymbolScale;
            if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0) return 1.0;
            return Math.Clamp(s, 0.2, 20.0);
        }
    }
}

/// <summary>一次设备动态符号重建的结论。</summary>
public sealed class SimEquipMotionResult
{
    public string PeriodKey { get; internal set; } = "";

    /// <summary>喂进来的符号数。</summary>
    public int FedSymbols { get; internal set; }
    /// <summary>真正画的台数。</summary>
    public int Drawn { get; internal set; }
    /// <summary>按 R-E3 跳过的卡车数（它们是车流动点）。</summary>
    public int SkippedTrucks { get; internal set; }
    /// <summary>会随期次相位位移的台数。</summary>
    public int Moving { get; internal set; }
    /// <summary>原地不动的台数（推进没解出来）。</summary>
    public int Static { get; internal set; }

    public bool Balanced => FedSymbols == Drawn + SkippedTrucks;

    /// <summary>本帧推了多少段线。</summary>
    public int Segments { get; internal set; }
    /// <summary>超上限被舍掉的段数。</summary>
    public int DroppedByCap { get; internal set; }

    /// <summary>期末位移量 m（0 = 不位移）。</summary>
    public double AdvanceM { get; internal set; }

    public List<string> Notes { get; } = new();
    public string Summary { get; internal set; } = "";
    public string LegendText { get; internal set; } = "";

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine(LegendText);
        foreach (var n in Notes)
            sb.AppendLine(n.StartsWith("◆", StringComparison.Ordinal) || n.StartsWith("·", StringComparison.Ordinal)
                          ? n : "· " + n);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 会动的设备符号舞台。<b>永不抛</b>。
/// <para>用法：换期 / 换指派时 <see cref="Rebuild"/>，每帧 <see cref="Tick"/>(期内相位 φ)。</para>
/// </summary>
public sealed class SimEquipMotionStage
{
    /// <summary>符号线的组名。</summary>
    public const string Group = "simequip.body";
    /// <summary>铭牌文字的组名。</summary>
    public const string LabelGroup = "simequip.tag";

    private readonly ISimDynamicOverlay _sink;
    public SimEquipMotionStage(ISimDynamicOverlay? sink = null) => _sink = sink ?? SimDynamicOverlay.Current;

    public SimEquipMotionParams Params { get; set; } = new();
    public SimEquipMotionResult? Last { get; private set; }
    public bool Available => _sink.Available;

    private readonly List<EquipSymbol> _syms = new();
    /// <summary>形态缓冲。逐帧复用（只 Clear 不重建）—— 每帧 new 一个 List 在 60fps 下是纯浪费。</summary>
    private readonly EquipWireBuffer _wire = new();
    private double[] _bufXyz = Array.Empty<double>();
    private uint[] _bufArgb = Array.Empty<uint>();
    // 铭牌缓冲：文案在 Rebuild 时定死（逐帧只动锚点），所以 _tagText 不逐帧重建
    private readonly List<string> _tagText = new();
    private double[] _tagXyz = Array.Empty<double>();
    private uint[] _tagArgb = Array.Empty<uint>();
    private float[] _tagH = Array.Empty<float>();
    private byte[] _tagHA = Array.Empty<byte>(), _tagVA = Array.Empty<byte>();
    private bool _pushedAny;

    /// <summary>
    /// 换期 / 换指派时调。<paramref name="symbols"/> 直接给
    /// <c>EquipmentStage.Current.Last?.Symbols</c> —— 位置与走向一行都不重解（见文件头）。
    /// </summary>
    public SimEquipMotionResult Rebuild(string periodKey, IReadOnlyList<EquipSymbol>? symbols)
    {
        var res = new SimEquipMotionResult { PeriodKey = periodKey ?? "" };
        _syms.Clear();
        try
        {
            res.FedSymbols = symbols?.Count ?? 0;
            for (int i = 0; i < res.FedSymbols; i++)
            {
                var s = symbols![i];
                if (s == null) continue;
                if (s.Kind == EquipKind.Truck && !Params.IncludeTrucks) { res.SkippedTrucks++; continue; }
                _syms.Add(s);
            }
            res.Drawn = _syms.Count;

            // 铭牌文案在这里定死（逐帧只动锚点）—— 文案每帧重拼会在 60fps 下每秒造上千个字符串。
            _tagText.Clear();
            if (Params.ShowLabels)
            {
                foreach (var s in _syms)
                {
                    string id = s.MachineId.Length > 0 ? s.MachineId : "(无编号)";
                    _tagText.Add(Params.LabelWithState ? id + "·" + EquipPalette.Name(s.State) : id);
                }
                int n = _tagText.Count;
                if (_tagArgb.Length < n)
                {
                    _tagXyz = new double[n * 3];
                    _tagArgb = new uint[n];
                    _tagH = new float[n];
                    _tagHA = new byte[n];
                    _tagVA = new byte[n];
                }
                for (int i = 0; i < n; i++)
                {
                    // 铭牌用设备本色的**亮一档**：与符号同色相 ⇒ 一眼对得上是哪台的牌子；
                    // 亮一档是为了在深底图上够跳（符号本身可能被状态压暗）。
                    _tagArgb[i] = 0xFF000000u | EquipPalette.Shade(EquipPalette.Rgb(_syms[i].Kind), 1.25);
                    _tagH[i] = (float)Math.Max(0.5, Params.LabelHeightM);
                    _tagHA[i] = 0;   // 左对齐 —— 牌子挂在符号右侧
                    _tagVA[i] = 2;   // 垂直居中
                }
            }

            bool moves = Params.AdvanceResolved && Params.AdvanceM > 1e-6;
            res.AdvanceM = moves ? Params.AdvanceM : 0;
            res.Moving = moves ? res.Drawn : 0;
            res.Static = moves ? 0 : res.Drawn;

            if (res.SkippedTrucks > 0)
                res.Notes.Add($"· 跳过 {res.SkippedTrucks} 台卡车 —— 卡车是**车流动点**（SimFlowStage）。"
                            + "两边都画就成了双份车队：图上数出来的车数是台账的两倍，而两边各自都自洽。");
            if (!moves)
                res.Notes.Add("◆ 设备**原地不动**：" + WhyStatic()
                            + "　—— 不编巡回路线（编出来的位移在图上量得出来，却不对应任何真实的量）。");
            else
                res.Notes.Add($"· 期内位移 = 推进 {Params.AdvanceM:0.#} m × 期内相位，方位 {Params.AdvanceAzimuthDeg:0.#}°"
                            + "（取自「平面推进示意」用的同一个数，不另算）。");
            if (Math.Abs(Params.Scale - 1.0) > 1e-9)
                res.Notes.Add($"◆ 符号放大 ×{Params.Scale:0.##} 已开 —— 图上「设备 vs 台阶」的大小关系**不再是真的**。");

            res.Summary = res.Drawn == 0
                ? "◆ 设备符号：本期一台都没有（先在「设备指派」里排产，再看这里）。"
                : $"设备符号：{res.Drawn} 台" + (moves ? $"，随期次相位推进 {Params.AdvanceM:0.#} m" : "，原地");
            res.LegendText =
                $"图例：**三维线框**（overlay 是线，没有实心填充；形态与「摆设备」那层的实体符号**同一份定义**，"
              + $"角点逐点相同、只是连法不同）；色相=设备类别、明度=作业状态（与实体层同一套 EquipPalette）；"
              + $"符号长 {Params.SymbolLengthM * Params.Scale:0.#} m（世界尺寸）"
              + (Math.Abs(Params.Scale - 1.0) > 1e-9 ? $"，**含放大 ×{Params.Scale:0.##}**" : "")
              + "；设备走**期次钟**（一期压成几秒），车流走真实车速 —— 同屏两个钟。";
        }
        catch (Exception ex)
        {
            res.Notes.Add($"设备动态符号重建异常（{ex.GetType().Name}: {ex.Message}）→ 本期不画。");
            res.Summary = "◆ 设备符号：重建异常，本期一台都没画。";
            _syms.Clear();
        }
        Last = res;
        return res;
    }

    private string WhyStatic()
    {
        if (!Params.DirectionalAdvance)
            return "推进模式是「全周等距」，没有单一推进方位";
        if (double.IsNaN(Params.AdvanceM) || double.IsInfinity(Params.AdvanceM))
            return "本期推进距离没解出来（台阶高 H / 工作线长 L 未接计划）";
        if (Params.AdvanceM <= 1e-6)
            return "本期推进距离为 0";
        return "推进参数不完整";
    }

    /// <summary>
    /// 按期内相位重画符号。<paramref name="phase01"/>∈[0,1]：0=期初位置，1=期末位置。
    /// 返回本帧推了几段线。
    /// </summary>
    public int Tick(double phase01)
    {
        var res = Last;
        if (res == null || _syms.Count == 0)
        {
            if (_pushedAny) _sink.SetLines(Group, null, null, 0);
            return 0;
        }
        if (double.IsNaN(phase01) || double.IsInfinity(phase01)) phase01 = 0;
        phase01 = Math.Clamp(phase01, 0, 1);

        double adv = res.AdvanceM * phase01;
        double az = Params.AdvanceAzimuthDeg * Math.PI / 180.0;
        // 方位角与窗口口径一致：0° = +X，逆时针为正（「平面推进示意」就是这么用的）。
        double ax = adv * Math.Cos(az), ay = adv * Math.Sin(az);

        double len = Math.Max(0.1, Params.SymbolLengthM * Params.Scale);
        double lift = double.IsNaN(Params.LiftM) || double.IsInfinity(Params.LiftM) ? 0 : Params.LiftM;
        int cap = Math.Max(0, Params.MaxSegments);
        EnsureBuffers(cap);

        int n = 0, dropped = 0, tag = 0;
        bool wantTags = Params.ShowLabels && _tagText.Count == _syms.Count && _tagText.Count > 0;

        // ★ 形态从 EquipSymbolLibrary 出（与实体层同一份定义，见 EquipWireBuffer 头）。
        //   缓冲逐帧复用、只 Clear 不重建 —— 60fps 下每帧 new 一个 List 是纯浪费。
        _wire.Clear();
        foreach (var s in _syms)
        {
            double ch = Math.Cos(s.HeadingRad), sh = Math.Sin(s.HeadingRad);
            double ox = s.X + ax, oy = s.Y + ay, oz = s.Z + lift;

            if (wantTags)
            {
                // 牌子挂在符号**右侧半个符号长**处：跟着一起平移，不压在符号上。
                int t3 = tag * 3;
                _tagXyz[t3] = ox + len * 0.60 * ch;
                _tagXyz[t3 + 1] = oy + len * 0.60 * sh;
                _tagXyz[t3 + 2] = oz + len * 0.45;   // 抬到本体上沿，别埋进线框里
                tag++;
            }

            EquipSymbolLibrary.Emit(_wire, s.Kind, s.State, ox, oy, oz, s.HeadingRad, len);
        }

        var segs = _wire.WorldSegments;
        var cols = _wire.SegmentRgb;
        for (int i = 0; i < cols.Length; i++)
        {
            if (n >= cap) { dropped++; continue; }
            int o = n * 6, q = i * 6;
            _bufXyz[o] = segs[q]; _bufXyz[o + 1] = segs[q + 1]; _bufXyz[o + 2] = segs[q + 2];
            _bufXyz[o + 3] = segs[q + 3]; _bufXyz[o + 4] = segs[q + 4]; _bufXyz[o + 5] = segs[q + 5];
            _bufArgb[n] = 0xFF000000u | cols[i];
            n++;
        }

        res.Segments = n;
        if (dropped != res.DroppedByCap)
        {
            res.DroppedByCap = dropped;
            res.Notes.RemoveAll(t => t.StartsWith("◆ 设备符号段数超过一帧上限", StringComparison.Ordinal));
            if (dropped > 0)
                res.Notes.Add($"◆ 设备符号段数超过一帧上限 {cap}，本帧**舍掉 {dropped} 段**（符号会缺笔画）。");
        }

        _sink.SetLines(Group, _bufXyz, _bufArgb, n);
        if (wantTags) _sink.SetLabels(LabelGroup, _tagXyz, _tagText, _tagArgb, _tagH, _tagHA, _tagVA, tag);
        else if (_pushedAny) _sink.SetLabels(LabelGroup, null, null, null, null, null, null, 0);
        _pushedAny = true;
        return n;
    }

    /// <summary>撤掉本舞台的组。幂等。</summary>
    public void Clear()
    {
        try { _sink.Clear(Group); _sink.Clear(LabelGroup); } catch { }
        _pushedAny = false;
        _syms.Clear();
        _tagText.Clear();
    }

    public void RequestRender() => _sink.RequestRender();

    private void EnsureBuffers(int cap)
    {
        if (_bufArgb.Length >= cap && cap > 0) return;
        int n = Math.Max(cap, 256);
        _bufXyz = new double[n * 6];
        _bufArgb = new uint[n];
    }

}
