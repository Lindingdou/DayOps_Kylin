// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/MiningUnitLedger.cs（逐行对应；仅命名空间适配）
using System;
using System.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Dump;   // DumpStripPlanner.Cell（Kylin 侧已按原版移植，见 Cad/Dump）

/// <summary>台账里一行是哪一类东西。<b>写进表里，不靠猜</b>。</summary>
public enum LedgerKind
{
    Coal = 0,
    Rock = 1,
    /// <summary>排土位置（潜在排土条带的一个位置）。它不是"采掘"单元，但要和采场单元一起管。</summary>
    Dump = 2,
}

/// <summary>
/// 【采掘单元台账】采矿模型（煤 + 岩）与排土模型<b>合在一张表</b>里管。
///
/// <para><b>为什么必须有「类型」这一列</b>：原先的表靠"有没有『净岩量m3』这一列"猜整张表是煤还是岩
/// （<c>MiningUnitPlanWindow.WriteTo</c> 里的 <c>_rows.Any(r =&gt; r.Kind == "岩")</c>）。
/// 那个猜法在<b>单一类型的表</b>上碰巧对，一旦三类合表就必错：
/// 煤行的吨位会被写进「净岩量m3」列，读回来<b>整表变成岩</b>，而且不报任何错。
/// 类型是行的身份，身份要写下来，不能从别的列反推。</para>
///
/// <para><b>一个格式一份实现</b>：读、写、三类的构造全在本类。
/// 此前排土位置有一份自己的 CSV（<c>DumpStripDialog.ExportCsv</c>，列完全不同），
/// 采矿模型有另一份（<c>MiningPlanExporter</c>）—— 两份格式的表<b>没法合并管理</b>，
/// 而"合起来管"正是台账存在的理由。</para>
///
/// <para><b>量列各填各的，空着就是空着</b>：煤填煤量、岩填毛/含煤/净、排土填库容，
/// 其余列<b>留空而不是写 0</b> —— 写 0 是在说"这个量是零"，那不是真的。</para>
/// </summary>
public static class MiningUnitLedger
{
    /// <summary>
    /// 一笔<b>流向</b>：本单元本期有多少方运到哪儿、多远、什么物料。
    /// <para>与 <c>PlanLib.ShortTerm.PlanFlow</c> 和 <c>MineAssLib.Driving.UnitFlow</c> 同构 ——
    /// 三处描述的是同一件事，字段名保持对得上，别各起各的。</para>
    /// </summary>
    public sealed class Flow
    {
        /// <summary>去向编号（岩 = 排土位置 Code，煤 = 出矿点 Code）。</summary>
        public string Destination = "";
        /// <summary>本笔的原位实方（m³）。</summary>
        public double InSituM3;
        /// <summary>本笔的等效运距 km。null = 没算过（<b>不是 0</b>）。</summary>
        public double? HaulKm;
        /// <summary>物料码（与下游目录对齐：coal / rock / topsoil …）。</summary>
        public string MaterialCode = "";

        public Flow Copy() => new() { Destination = Destination, InSituM3 = InSituM3, HaulKm = HaulKm, MaterialCode = MaterialCode };
    }

    /// <summary>台账一行。三类共用一套字段，各自只填自己那几个量。</summary>
    public sealed class Row
    {
        // ── 标识 ──
        /// <summary>唯一身份。采场 = <c>层-B带号-P幅号</c>；排土 = <c>排土场-L级-P幅-S带</c>（即 <c>Cell.Code</c>）。</summary>
        public string UnitId = "";
        public LedgerKind Kind;
        /// <summary>采场名 / 排土场名。</summary>
        public string Region = "";
        /// <summary>煤层号 / 岩台阶名 / 排土台阶级。</summary>
        public string Seam = "";
        /// <summary>采场 = 露头带号；排土 = 推进带序 <c>StepIndex</c>。</summary>
        public int Band;
        /// <summary>沿走向第几幅 / 共几幅。</summary>
        public int Panel, PanelCount = 1;

        // ── 位置 ──
        public double Cx, Cy, Cz, ZLo, ZHi;

        // ── 尺寸 ──
        public double LengthM, WidthM, ThickM;

        /// <summary>
        /// 走向方位角（度，从<b>北</b>起顺时针，规约到 [0,180)）。<b>null = 不知道</b>。
        ///
        /// <para><b>⚠ 哨兵必须是 null 不是 0</b>：0° 是一个完全合法的方位（正南北走向）。
        /// 用 0 当"没填"的话，所有老台账读进来都会变成"走向正南北"，
        /// 盒子会一本正经地朝着一个错方向 —— 而每个数看上去都正常。</para>
        ///
        /// <para>只用来给<b>盒子退路</b>定朝向。有真轨时走真轨，这一列不参与。
        /// 规约到 [0,180) 是因为盒子 180° 对称，存 0 和 180 没有区别。</para>
        /// </summary>
        public double? AzimuthDeg;

        // ── 量（各类只填自己那几个，其余为 null = 空，不是 0）──
        public double? CoalM3, CoalT;
        public double? GrossM3, InCoalM3, NetRockM3;
        /// <summary>排土库容，口径 = <b>占容方 V容</b>（D6）。能接多少采场实方 = 它 ÷ Kr。</summary>
        public double? DumpCapM3;

        // ── 计划（人填 / 算法填）──
        public int Seq;
        public string Period = "";
        public string Status = "";
        /// <summary>累计完成度 0~1。跨月幅靠它，「在采」这个状态说不出采了多少（U1）。</summary>
        public double Done;
        public string Note = "";

        /// <summary>
        /// 本单元本期的<b>逐笔流向</b>。<b>一个单元可以拆到多个去向</b> ——
        /// 一个岩单元填不满一个排土位置、或一个位置装不下一个单元，都会拆。
        ///
        /// <para><b>⚠ 早先这里只有一个 <c>Destination</c> + 一个 <c>HaulKm</c>，
        /// 拆开的流在存盘那一刻就被压平成一笔</b> —— 实测一个月 36 个岩单元用掉 46 个排土位置，
        /// 拆分是常态不是例外。压平之后运距是错的、排土位置的占用也对不上，
        /// 而每一项汇总看上去都正常。</para>
        /// </summary>
        public readonly List<Flow> Flows = new();

        /// <summary>
        /// 主要去向（<b>派生</b>：量最大的那一笔）。留着是为了让只关心"排到哪儿"的表和旧格式还能用。
        /// <b>写入无效</b> —— 去向的事实在 <see cref="Flows"/> 里，两处可写就会漂。
        /// </summary>
        public string Destination
        {
            get
            {
                if (Flows.Count == 0) return _legacyDest;
                var top = Flows[0];
                foreach (var f in Flows) if (f.InSituM3 > top.InSituM3) top = f;
                return top.Destination;
            }
            set { if (Flows.Count == 0) _legacyDest = value ?? ""; }
        }
        private string _legacyDest = "";

        /// <summary>
        /// 到去向的等效运距 km（<b>派生</b>：按量加权平均）。null = 没算过。
        /// <para><b>不是"取第一笔"</b>：一个单元拆到远近两个位置时，取第一笔会让运输功差一大截，
        /// 而那个数看上去完全合理。</para>
        /// </summary>
        public double? HaulKm
        {
            get
            {
                if (Flows.Count == 0) return _legacyHaul;
                double v = 0, wk = 0;
                foreach (var f in Flows) { if (f.HaulKm.HasValue) { v += f.InSituM3; wk += f.HaulKm.Value * f.InSituM3; } }
                return v > 1e-9 ? wk / v : null;
            }
            set { if (Flows.Count == 0) _legacyHaul = value; }
        }
        private double? _legacyHaul;

        /// <summary>逐笔流的量合计（m³ 实方）。与本月采出量对账用。</summary>
        public double FlowSumM3 { get { double s = 0; foreach (var f in Flows) s += f.InSituM3; return s; } }

        /// <summary>报表主量：煤 = 吨，岩 = 净岩量 m³，排土 = 库容 m³。</summary>
        public double Qty => Kind switch
        {
            LedgerKind.Coal => CoalT ?? 0,
            LedgerKind.Rock => NetRockM3 ?? 0,
            _ => DumpCapM3 ?? 0,
        };
        public string QtyUnit => Kind == LedgerKind.Coal ? "t" : "m³";
        public string KindText => KindToText(Kind);
    }

    public static string KindToText(LedgerKind k) => k switch
    {
        LedgerKind.Coal => "煤", LedgerKind.Rock => "岩", _ => "排土",
    };

    /// <summary>
    /// 文本 → 类型。<b>认不出来要说认不出来</b>，不能默认成煤。
    /// <para>原先写成 <c>_ =&gt; Coal</c> 是<b>失败开</b>：类型格里写「岩石」「废石」「排土场」
    /// 全会静默变成煤，量按 <see cref="Row.Qty"/> 取 <c>CoalT ?? 0</c> 归零，
    /// 而汇总里煤的个数 +1、吨位 +0 —— 每个数看上去都正常。
    /// 这个类存在的理由就是"类型是身份、不能从别的列反推"，默认值等于把反推又放了回来。</para>
    /// </summary>
    public static bool TryKindFromText(string? s, out LedgerKind kind)
    {
        switch ((s ?? "").Trim())
        {
            case "煤": kind = LedgerKind.Coal; return true;
            case "岩": kind = LedgerKind.Rock; return true;
            case "排土": kind = LedgerKind.Dump; return true;
            default: kind = LedgerKind.Coal; return false;
        }
    }

    /// <summary>宽松版（保留给显示/筛选用）。解析文件<b>不要用它</b> —— 用 <see cref="TryKindFromText"/>。</summary>
    public static LedgerKind KindFromText(string? s)
    { TryKindFromText(s, out var k); return k; }

    /// <summary>
    /// 按<b>这一行实际填了哪个量列</b>推断类型（不是按表头有没有那一列）。
    /// <para>表头是整表的属性，逐行推断拿它当依据必错：本类写出的表头<b>恒含</b>「净岩量m3」，
    /// 于是"按表头推断"对每一行都得出【岩】—— 煤行也不例外。</para>
    /// </summary>
    private static bool TryInferKindFromRow(double? coalT, double? coalM3, double? netRock,
                                            double? gross, double? cap, out LedgerKind kind)
    {
        bool c = coalT.HasValue || coalM3.HasValue;
        bool r = netRock.HasValue || gross.HasValue;
        bool d = cap.HasValue;
        int n = (c ? 1 : 0) + (r ? 1 : 0) + (d ? 1 : 0);
        kind = c ? LedgerKind.Coal : r ? LedgerKind.Rock : LedgerKind.Dump;
        return n == 1;                     // 只有唯一一类量填着，推断才算数
    }

    /// <summary>煤视密度默认值（t/m³）。与 <see cref="MiningPlanExporter.DefaultCoalDensity"/> 同源。</summary>
    public const double DefaultCoalDensity = MiningPlanExporter.DefaultCoalDensity;

    // ══════════════════════════════════════════════════════════════
    //  构造：三类各一个入口
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 采掘带的 UnitId —— <b>全仓库唯一的拼法</b>。
    ///
    /// <para>这一句此前在三个地方各写了一遍（本文件生成侧、
    /// <c>UnitSolidStage</c> 的真轨索引、<c>EquipmentStage</c> 的设备落位），
    /// 三处的注释都写着"必须和台账逐字一致"—— 那句注释本身就是证据：
    /// <b>它是靠人记着才对的</b>。三处里任何一处改了格式，另外两处不会报错，
    /// 只会静静地对不上号（真轨查不到 → 全退盒子；设备摆不到单元上 → 一台都不出现），
    /// 而这两种失败看上去都像"数据不全"。</para>
    ///
    /// <para>排土位置<b>不走这条</b>：它用 <c>DumpStripPlanner.Cell.Code</c>，是另一套编码。</para>
    /// </summary>
    public static string StripUnitId(string? seamCode, int bandId, int panelIndex)
        => $"{seamCode}-B{bandId}-P{panelIndex}";

    /// <summary>
    /// 从坡顶线 / 坡底线求<b>走向方位角</b>（度，从北起顺时针，规约到 [0,180)）。
    ///
    /// <para><b>口径复用既有的那条</b>（<c>MiningModelPlanner</c> 求推进方向那段）：
    /// 逐点取 <c>crest−toe</c> 的 XY、各自归一化后求平均 = 推进方向；
    /// <b>走向 = 推进方向的垂线</b>。两条轨在 XY 上不重合（crest 是 toe 沿推进方向偏 0.5 m），
    /// 所以这个向量既不退化、符号也明确。</para>
    ///
    /// <para>算不出来返回 false，<b>不返回 0</b> —— 0° 是合法方位（正南北），
    /// 拿它当"没算出来"会让盒子一本正经地朝错方向。</para>
    /// </summary>
    public static bool TryStrikeAzimuthDeg(double[]? crestXyz, double[]? toeXyz, out double azDeg)
    {
        azDeg = 0;

        // ★ 走向【先从坡顶轨自身的切线求】（2026-08-18）。
        //
        //   原来只有下面那条路：把坡顶轨与坡底轨**按下标一一配对**求推进方向、再取垂线。
        //   可坡底轨是 OffsetRail 偏出来的 —— 折回/自交处会被夹紧、合点，
        //   两条轨的点数与参数化都不再对应；配错的那几幅，"推进方向"就是乱的，垂线跟着乱转。
        //   实测（2112 个排土位置、2020 对相邻幅）：中位只差 3.9°，但 **62 对偏 45° 以上、最大 88°** ——
        //   图上就是那几条横七竖八、跟台阶线拧着的条带。
        //   走向本来就是台阶线自己的方向，不必绕道推进方向去反推。
        //
        //   ⚠ 方向是【轴向量】（180° 对称），求平均必须用**倍角法**：
        //   直接把 (cosθ,sinθ) 相加，170° 与 10° 会互相抵消成 90°，那是最容易错的一步。
        if (crestXyz != null && crestXyz.Length >= 6)
        {
            double c2 = 0, s2 = 0;
            for (int k = 0; k + 1 < crestXyz.Length / 3; k++)
            {
                double dx = crestXyz[(k + 1) * 3] - crestXyz[k * 3];
                double dy = crestXyz[(k + 1) * 3 + 1] - crestXyz[k * 3 + 1];
                double L = Math.Sqrt(dx * dx + dy * dy);
                if (L <= 1e-9) continue;
                double th = Math.Atan2(dx / L, dy / L);      // 方位角：X=东、Y=北
                c2 += Math.Cos(2 * th) * L; s2 += Math.Sin(2 * th) * L;   // 按段长加权
            }
            if (Math.Abs(c2) + Math.Abs(s2) > 1e-9)
            {
                azDeg = Norm180(0.5 * Math.Atan2(s2, c2) * 180.0 / Math.PI);
                return true;
            }
        }

        if (crestXyz == null || toeXyz == null) return false;
        int n = Math.Min(crestXyz.Length, toeXyz.Length) / 3;
        if (n < 1) return false;

        double ax = 0, ay = 0;
        for (int k = 0; k < n; k++)
        {
            double dx = crestXyz[k * 3] - toeXyz[k * 3];
            double dy = crestXyz[k * 3 + 1] - toeXyz[k * 3 + 1];
            double L = Math.Sqrt(dx * dx + dy * dy);
            if (L > 1e-9) { ax += dx / L; ay += dy / L; }
        }
        double al = Math.Sqrt(ax * ax + ay * ay);
        if (al <= 1e-9) return false;               // 两条轨在 XY 上重合 ⇒ 推不出方向

        // 推进方向 (ax,ay) 的垂线就是走向。X=东、Y=北 ⇒ 方位角 = atan2(东, 北)
        double sx = -ay / al, sy = ax / al;
        azDeg = Norm180(Math.Atan2(sx, sy) * 180.0 / Math.PI);
        return true;
    }

    /// <summary>
    /// 规约到 <b>半开区间 [0,180)</b>（走向是轴向量，180° 对称）。
    ///
    /// <para><b>⚠ 端点要吸到 0，不能留在 180</b>：正南北走向的一条轨，倍角法里 2θ = 2π，
    /// 而 2π 在浮点里不是整数圈 —— <c>Atan2</c> 回来的是 −ε，<c>+= 180</c> 之后就成了
    /// <b>179.999999999999997</b>，再报出来是"方位 180°"。盒子转 180° 与转 0° 长得一模一样，
    /// 所以图上看不出来，只有判据（A2 的"朝南 == 朝北"那一条）会红，
    /// 而下游任何"方位在 [0,180)"的假设都已经被破坏了。</para>
    /// </summary>
    private static double Norm180(double deg)
    {
        double a = deg % 180.0;
        if (a < 0) a += 180.0;
        if (a >= 180.0 - 1e-9 || a < 1e-9) a = 0;    // 半开区间：180° 就是 0°
        return a;
    }

    /// <summary>采矿模型的采掘带（煤或岩）→ 台账行。</summary>
    /// <param name="isRock">煤还是岩。<b>调用方说了算，不按名字猜</b> —— 煤层完全可以叫「岩石沟3号」。</param>
    public static List<Row> FromStrips(IReadOnlyList<MiningModelPlanner.Strip>? strips, bool isRock,
                                       string regionName = "", double coalDensity = DefaultCoalDensity)
    {
        var list = new List<Row>();
        if (strips == null) return list;
        double rho = coalDensity > 0 ? coalDensity : DefaultCoalDensity;

        foreach (var s in strips)
        {
            if (s == null) continue;
            int n = s.CrestXyz.Length / 3;
            if (n == 0) continue;
            double cx = 0, cy = 0, cz = 0, zlo = double.MaxValue, zhi = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                cx += s.CrestXyz[k * 3]; cy += s.CrestXyz[k * 3 + 1];
                double zt = s.CrestXyz[k * 3 + 2];
                double zb = k * 3 + 2 < s.ToeXyz.Length ? s.ToeXyz[k * 3 + 2] : zt;
                cz += (zt + zb) * 0.5;
                if (zb < zlo) zlo = zb;
                if (zt > zhi) zhi = zt;
            }
            cx /= n; cy /= n; cz /= n;

            var r = new Row
            {
                UnitId = StripUnitId(s.SeamCode, s.BandId, s.PanelIndex),
                Kind = isRock ? LedgerKind.Rock : LedgerKind.Coal,
                Region = regionName, Seam = s.SeamCode,
                Band = s.BandId, Panel = s.PanelIndex, PanelCount = Math.Max(1, s.PanelCount),
                Cx = cx, Cy = cy, Cz = cz, ZLo = zlo, ZHi = zhi,
                LengthM = s.StrikeLenM,
                WidthM = s.AdvanceWidthM > 1e-6 ? s.AdvanceWidthM : 40.0,
                ThickM = s.ThickM,
                // 走向方位：算不出来就留 null，让盒子退回轴对齐并记账，不拿 0 冒充
                AzimuthDeg = TryStrikeAzimuthDeg(s.CrestXyz, s.ToeXyz, out double az) ? az : (double?)null,
            };
            if (isRock) { r.GrossM3 = s.GrossVolumeM3; r.InCoalM3 = s.CoalVolumeM3; r.NetRockM3 = s.EstVolumeM3; }
            else { r.CoalM3 = s.EstVolumeM3; r.CoalT = s.EstVolumeM3 * rho; }
            list.Add(r);
        }
        return list;
    }

    /// <summary>
    /// 排土条带的潜在位置 → 台账行。
    /// <para><b>UnitId 直接用 <see cref="DumpStripPlanner.Cell.Code"/></b>：那个编号已经是位置的身份
    /// （落库靠它做唯一索引、图上按它挂属性），台账再造一套编号就等于同一个东西有两个名字。</para>
    /// </summary>
    public static List<Row> FromDumpCells(IReadOnlyList<DumpStripPlanner.Cell>? cells, string dumpName = "")
    {
        var list = new List<Row>();
        if (cells == null) return list;
        foreach (var c in cells)
        {
            if (c == null) continue;
            string id = !string.IsNullOrWhiteSpace(c.Code) ? c.Code
                      : $"{(dumpName.Length > 0 ? dumpName : "排土场")}-L{c.LevelIndex}-P{c.PanelIndex:00}-S{c.StepIndex:00}"
                        + (c.SubIndex > 0 ? $"-{c.SubIndex}" : "");
            list.Add(new Row
            {
                UnitId = id,
                Kind = LedgerKind.Dump,
                Region = dumpName.Length > 0 ? dumpName : DumpNameFromCode(id),
                Seam = $"L{c.LevelIndex}",
                Band = c.StepIndex,                       // 排土的"带"是推进带序
                Panel = c.PanelIndex, PanelCount = Math.Max(1, c.PanelCount),
                Cx = c.Cx, Cy = c.Cy, Cz = c.Cz,
                ZLo = c.ToeZ, ZHi = c.CrestZ,
                LengthM = c.StrikeLenM,
                WidthM = c.StripWidthM,
                ThickM = c.BenchHeightM,
                AzimuthDeg = TryStrikeAzimuthDeg(c.CrestXyz, c.ToeXyz, out double daz) ? daz : (double?)null,
                DumpCapM3 = c.CapacityM3,
            });
        }
        return list;
    }

    private static string DumpNameFromCode(string code)
    {
        int i = code.IndexOf("-L", StringComparison.Ordinal);
        return i > 0 ? code.Substring(0, i) : "排土场";
    }

    // ══════════════════════════════════════════════════════════════
    //  合并：几何以模型为准，人填的按 UnitId 保住
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 用新算的几何刷新台账，<b>按 UnitId 保住人填的那几列</b>（期次/状态/完成度/去向/备注/推进序）。
    ///
    /// <para><b>模型里没有了的行不会悄悄消失</b>：它们进 <paramref name="orphans"/> 由调用方决定怎么办。
    /// 直接丢掉的话，"上个月排了但这次重算没生成"的单元会连同它的排产一起蒸发，
    /// 而总量表看上去完全正常。</para>
    /// </summary>
    public static List<Row> Merge(IReadOnlyList<Row> fresh, IReadOnlyList<Row>? existing, out List<Row> orphans)
    {
        orphans = new List<Row>();
        var outv = new List<Row>(fresh.Count);
        var old = new Dictionary<string, Row>(StringComparer.Ordinal);
        if (existing != null)
            foreach (var e in existing) if (e != null && e.UnitId.Length > 0) old[e.UnitId] = e;

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fresh)
        {
            if (old.TryGetValue(f.UnitId, out var e))
            {
                f.Seq = e.Seq; f.Period = e.Period; f.Status = e.Status;
                f.Done = e.Done; f.Note = e.Note;
                // ⚠ 流是【逐笔】的，必须整组搬过来。只搬"主要去向"那一个派生值的话，
                //   拆到多个位置的单元一过 Merge 就只剩一笔 —— 与当初一行只能存一笔是同一个错。
                f.Flows.Clear();
                foreach (var fl in e.Flows) f.Flows.Add(fl.Copy());
                if (e.Flows.Count == 0) { f.Destination = e.Destination; f.HaulKm = e.HaulKm; }
                used.Add(f.UnitId);
            }
            outv.Add(f);
        }
        foreach (var kv in old)
            if (!used.Contains(kv.Key) && HasPlanData(kv.Value)) orphans.Add(kv.Value);
        return outv;
    }

    /// <summary>
    /// 这一行有没有"人填或算法填过的东西"。
    /// <para><b>必须和 <see cref="Merge"/> 保住的那一组列<b>逐个对齐</b></b>：
    /// Merge 保 Seq / Period / Status / Done / Destination / HaulKm / Note 七列，
    /// 这里就得判这七列。少判一列，只填了那一列的行在模型重算后<b>连同它的值一起静默消失</b>
    /// （既不在结果里，也不进 orphans）。原先漏了 Seq 和 HaulKm。</para>
    /// </summary>
    private static bool HasPlanData(Row r) =>
        r.Seq != 0 || r.Period.Length > 0 || r.Status.Length > 0 || r.Done > 1e-9
        || r.Flows.Count > 0 || r.Destination.Length > 0 || r.HaulKm.HasValue || r.Note.Length > 0;

    /// <summary>
    /// 把 <paramref name="incoming"/> 的<b>计划列</b>合进 <paramref name="baseRows"/>（按 UnitId），
    /// <b>不删任何既有行</b>。基表里有、来的这批没有的行原样留着。
    ///
    /// <para><b>为什么需要它</b>：「保存基表」如果直接把当前表写成基表，
    /// 那么读回一个只有 2 行的期次之后点保存，30 行的基表就被 2 行替换掉了 ——
    /// 而 <c>SaveBaseRaw</c> 按设计不合并、不出孤儿、不留备份，基表也不进「已删除」回收站。
    /// 那不是"保存"，那是"用子集覆盖全集"。</para>
    ///
    /// <paramref name="added"/> = 基表里原来没有的行（新单元）。<b>由调用方说出来</b> ——
    /// 往基表里加行是件该被看见的事。
    /// </summary>
    public static List<Row> UpsertInto(IReadOnlyList<Row>? baseRows, IReadOnlyList<Row> incoming,
                                       out int updated, out int added)
    {
        updated = 0; added = 0;
        var outv = baseRows == null ? new List<Row>() : new List<Row>(baseRows);
        // 重号时后者胜（与 TryRead 对重号「只警告、可继续」的口径一致，不抛）
        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < outv.Count; i++) pos[outv[i].UnitId] = i;

        foreach (var s in incoming)
        {
            if (s == null || s.UnitId.Length == 0) continue;
            if (pos.TryGetValue(s.UnitId, out int i))
            {
                // 来的这一行整个胜出（几何 + 量 + 计划列）：
                // 「从采矿模型取」之后它带的就是新几何，只搬计划列会把新几何丢掉；
                // 「读回期次」之后它的几何本来就是从基表刷过来的，整行搬回去等于原样。
                if (!ReferenceEquals(outv[i], s)) { outv[i] = s; updated++; }
            }
            else { pos[s.UnitId] = outv.Count; outv.Add(s); added++; }
        }
        return outv;
    }

    // ══════════════════════════════════════════════════════════════
    //  CSV：一份实现，读写对称
    // ══════════════════════════════════════════════════════════════

    // 【流序】同一个单元拆到多个去向时，一笔流一行，靠它区分。
    // 空 / 0 = 只有一笔（绝大多数行）。单元级的列在各笔上重复，读回时校验一致。
    private const string Header =
        "UnitId,类型,采场,煤层/台阶,带号,幅号,幅数," +
        "中心X,中心Y,中心Z,最低Z,最高Z," +
        "走向长m,推进宽m,厚度m,走向方位°," +
        "煤量m3,煤量t,毛量m3,含煤m3,净岩量m3,库容m3," +
        "推进序,期次,状态,完成度,流序,去向,流量m3,运距km,物料码,备注";

    public static string ToCsv(IReadOnlyList<Row> rows, string? title = null, double coalDensity = DefaultCoalDensity)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        int nc = rows.Count(r => r.Kind == LedgerKind.Coal),
            nr = rows.Count(r => r.Kind == LedgerKind.Rock),
            nd = rows.Count(r => r.Kind == LedgerKind.Dump);

        sb.AppendLine($"# 采掘单元台账{(string.IsNullOrWhiteSpace(title) ? "" : " · " + title)}"
                    + $"（煤 {nc} · 岩 {nr} · 排土 {nd}）");
        sb.AppendLine("# 一行 = 图上一个体。UnitId 与图层编号一致，可对回图纸。");
        sb.AppendLine("# 【类型】列是行的身份，别靠量列反推 —— 三类合表时反推必错。");
        sb.AppendLine($"# 煤吨位 = 体积 × 视密度 {coalDensity:0.###} t/m³；岩「净岩量」已扣穿过该体的煤；");
        sb.AppendLine("# 排土「库容」是占容方 V容，能接多少采场实方 = 库容 ÷ Kr（残余膨胀系数，随物料走）。");
        sb.AppendLine("# 几何与量以模型为准，重算即刷新；推进序/期次/状态/完成度/去向/备注按 UnitId 保留。");
        sb.AppendLine("# 量列各类只填自己那几个，其余【留空】—— 空不等于 0。");
        sb.AppendLine(Header);

        foreach (var r in rows)
        {
            // 没有流 → 一行；有 n 笔流 → n 行，单元级的列重复，流序 0..n-1。
            int n = Math.Max(1, r.Flows.Count);
            for (int k = 0; k < n; k++)
            {
                var f = k < r.Flows.Count ? r.Flows[k] : null;
                sb.Append(ci, $"{Esc(r.UnitId)},{r.KindText},{Esc(r.Region)},{Esc(r.Seam)},{r.Band},{r.Panel},{r.PanelCount},");
                sb.Append(ci, $"{r.Cx:0.##},{r.Cy:0.##},{r.Cz:0.##},{r.ZLo:0.##},{r.ZHi:0.##},");
                sb.Append(ci, $"{r.LengthM:0.##},{r.WidthM:0.##},{r.ThickM:0.###},");
                // null（不知道）写空串，不写 0 —— 0° 是合法方位，写 0 会把"不知道"变成"正南北"
                sb.Append(ci, $"{(r.AzimuthDeg.HasValue ? r.AzimuthDeg.Value.ToString("0.##", ci) : "")},");
                sb.Append(ci, $"{N(r.CoalM3)},{N(r.CoalT)},{N(r.GrossM3)},{N(r.InCoalM3)},{N(r.NetRockM3)},{N(r.DumpCapM3)},");
                sb.Append(ci, $"{r.Seq},{Esc(r.Period)},{Esc(r.Status)},{(r.Done > 1e-9 ? r.Done.ToString("0.###", ci) : "")},");
                if (f != null)
                    sb.Append(ci, $"{k},{Esc(f.Destination)},{N(f.InSituM3)},{N(f.HaulKm)},{Esc(f.MaterialCode)},");
                else
                    // 没有流时：去向/运距写派生值（兼容只关心"排到哪儿"的旧读法），流量与流序留空
                    sb.Append(ci, $",{Esc(r.Destination)},,{N(r.HaulKm)},,");
                sb.AppendLine(Esc(r.Note));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 读。<b>兼容旧的单类型表</b>（只有「煤量t」或「净岩量m3」、没有「类型」列的那种）：
    /// 没有类型列时按存在的量列推断，并把这件事记进 <paramref name="issues"/> —— 推断出来的类型是<b>猜的</b>，
    /// 要让人知道。
    /// </summary>
    public static bool TryRead(string? csvText, out List<Row> rows, out List<string> issues)
    {
        rows = new List<Row>();
        issues = new List<string>();
        if (string.IsNullOrWhiteSpace(csvText)) { issues.Add("文件是空的。"); return false; }

        // ⚠ 按 '\n' 硬切是错的：备注/去向里的换行在写侧被加了引号，读侧硬切会把一行截成两半，
        //    后半截还会被当成一条新数据行 —— 于是凭空多出一个 UnitId 是半截备注的「单元」。
        //    行的边界必须和字段的引号状态一起判。
        var lines = SplitLines(csvText);
        int hi = Array.FindIndex(lines, l => !l.TrimStart().StartsWith("#") && l.Contains("UnitId"));
        if (hi < 0) { issues.Add("找不到表头（缺 UnitId 列）—— 这不是采掘单元台账。"); return false; }

        var head = SplitCsv(lines[hi]);
        int matched = 0;
        int Idx(params string[] names)
        {
            foreach (var nm in names) { int i = Array.IndexOf(head, nm); if (i >= 0) { matched++; return i; } }
            return -1;
        }
        int iId = Idx("UnitId"), iKind = Idx("类型"), iRegion = Idx("采场", "排土场"),
            iSeam = Idx("煤层/台阶", "煤层", "台阶"), iBand = Idx("带号"), iPanel = Idx("幅号"), iPc = Idx("幅数"),
            iCx = Idx("中心X"), iCy = Idx("中心Y"), iCz = Idx("中心Z"), iZlo = Idx("最低Z"), iZhi = Idx("最高Z"),
            iLen = Idx("走向长m"), iW = Idx("推进宽m"), iTh = Idx("厚度m"),
            // 老台账（31 列）没有这一列 ⇒ Idx 返回 −1 ⇒ ND 给 null ⇒ 盒子退回轴对齐并记账。
            // 读侧是按【列名】匹配的，所以加列天然向后兼容，不需要版本号。
            iAz = Idx("走向方位°", "走向方位度", "方位角"),
            iCoalM3 = Idx("煤量m3"), iCoalT = Idx("煤量t"), iGross = Idx("毛量m3"),
            iInCoal = Idx("含煤m3"), iNet = Idx("净岩量m3"), iCap = Idx("库容m3", "容量m3"),
            iSeq = Idx("推进序"), iPeriod = Idx("期次"), iStatus = Idx("状态"),
            iDone = Idx("完成度"), iFlowNo = Idx("流序"), iDest = Idx("去向"),
            iFlowM3 = Idx("流量m3"), iHaul = Idx("运距km"), iMat = Idx("物料码"), iNote = Idx("备注");

        // ⚠ 「有一行含 UnitId」这个条件太松，松到把不是台账的文件也放进来。
        //   最现实的一条：文件不是 UTF-8（Excel 中文环境另存 .csv 默认 GB18030）。
        //   GB18030 的尾字节 ≥0x40 永不与逗号相撞 ⇒ 列数不变、纯 ASCII 的 UnitId 完好，
        //   而 27 个中文列名全成乱码 ⇒ 每个 Idx 都是 −1 ⇒ 量全 null、几何全 0，
        //   TryRead 却返回 true、issues 里一条 ◆ 都没有。随后「保存基表」把这批空壳写回去，
        //   真基表就没了 —— 而本该报警的 orphans 哨兵也因为计划列全空而一起失灵。
        //   所以要有一条【匹配到几列】的闸门。
        const int MinMatchedColumns = 6;
        if (matched < MinMatchedColumns)
        {
            issues.Add($"◆ 表头只认出 {matched} 列（至少要 {MinMatchedColumns} 列）—— 这多半不是采掘单元台账，"
                     + "或者文件不是 UTF-8 编码（本格式一律 UTF-8；Excel 请选「CSV UTF-8」另存）。"
                     + $"认出的表头是：{string.Join("|", head.Take(8))}…");
            return false;                     // 宁可拒收：半份台账能算出一份"看上去正常"的月计划
        }

        if (iKind < 0)
            issues.Add("· 这张表没有「类型」列（旧格式），类型将逐行按【该行填了哪个量列】推断。"
                     + "存回去时会补上类型列 —— 之后就不用猜了。");

        int kindGuessed = 0, kindUnknown = 0, kindAmbiguous = 0;
        string firstBadKind = "";
        var byId = new Dictionary<string, Row>(StringComparer.Ordinal);   // 多笔流归并用
        var inconsistent = new List<string>();
        var dupIds = new List<string>();                                   // 真重号（重复行，不带流量）

        for (int k = hi + 1; k < lines.Length; k++)
        {
            var line = lines[k].TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var c = SplitCsv(line);
            if (iId < 0 || iId >= c.Length || string.IsNullOrWhiteSpace(c[iId])) continue;

            double? qCoalM3 = ND(c, iCoalM3), qCoalT = ND(c, iCoalT), qGross = ND(c, iGross),
                    qNet = ND(c, iNet), qCap = ND(c, iCap);

            // 类型：① 认得的类型串 → 用它；② 认不得或空 → 按【本行的量列】推断，并【记账】。
            //   逐行推断绝不能看表头 —— 本类写出的表头恒含「净岩量m3」，
            //   照它推每一行都会得出【岩】，煤行也不例外。
            LedgerKind kind;
            string raw = iKind >= 0 && iKind < c.Length ? c[iKind].Trim() : "";
            if (raw.Length > 0 && TryKindFromText(raw, out kind)) { /* 认得，用它 */ }
            else
            {
                if (raw.Length > 0) { kindUnknown++; if (firstBadKind.Length == 0) firstBadKind = raw; }
                if (TryInferKindFromRow(qCoalT, qCoalM3, qNet, qGross, qCap, out kind))
                { if (iKind >= 0) kindGuessed++; }
                else kindAmbiguous++;
            }

            // ── 同一单元的多笔流：归并到同一个 Row，不新建行 ──────────────
            //   一个岩单元拆到两个排土位置就是两行，只有【流序】不同。
            //   早先一行只能存一笔，拆开的流在存盘那一刻被压平 —— 那是丢数据，不是精度损失。
            string uid = c[iId];
            if (byId.TryGetValue(uid, out var exist))
            {
                var f2 = MakeFlow(c, iDest, iFlowM3, iHaul, iMat);
                if (f2 == null)
                {
                    // ⚠ 同一个 UnitId 又出现了，却【不带流量】—— 那不是拆到多个去向，是真重号。
                    //   把它当成多笔流静默合并的话，UnitId 撞号这件事就永远不会被发现，
                    //   而按 UnitId 回填人填列会张冠李戴。
                    dupIds.Add(uid);
                    continue;
                }
                exist.Flows.Add(f2);
                // 单元级的列在各笔上重复，重复就必须一致 —— 不一致说明表被手改坏了，
                // 而按第一行取值会静默用错的那份。
                if (UnitLevelDiffers(exist, c, iDone, iSeq, iPeriod, iStatus)) inconsistent.Add(uid);
                continue;
            }

            var row = new Row
            {
                UnitId = uid, Kind = kind,
                Region = S(c, iRegion), Seam = S(c, iSeam),
                Band = I(c, iBand), Panel = I(c, iPanel), PanelCount = Math.Max(1, I(c, iPc)),
                Cx = D(c, iCx), Cy = D(c, iCy), Cz = D(c, iCz), ZLo = D(c, iZlo), ZHi = D(c, iZhi),
                LengthM = D(c, iLen), WidthM = D(c, iW), ThickM = D(c, iTh),
                AzimuthDeg = ND(c, iAz),
                CoalM3 = qCoalM3, CoalT = qCoalT,
                GrossM3 = qGross, InCoalM3 = ND(c, iInCoal), NetRockM3 = qNet,
                DumpCapM3 = qCap,
                Seq = I(c, iSeq), Period = S(c, iPeriod), Status = S(c, iStatus),
                Done = D(c, iDone), Note = S(c, iNote),
            };
            var f1 = MakeFlow(c, iDest, iFlowM3, iHaul, iMat);
            if (f1 != null) row.Flows.Add(f1);
            else { row.Destination = S(c, iDest); row.HaulKm = ND(c, iHaul); }   // 旧格式：只有去向没有流量
            rows.Add(row);
            byId[uid] = row;
        }
        if (rows.Count == 0) { issues.Add("表头有了，但一行数据都没读到。"); return false; }

        if (inconsistent.Count > 0)
            issues.Add($"◆ 有 {inconsistent.Count} 个单元的多笔流之间，单元级的列（完成度/推进序/期次/状态）对不上"
                     + $"（如 {string.Join("、", inconsistent.Distinct().Take(3))}）—— 已按第一笔取值。"
                     + "同一单元的各笔流必须重复同样的单元级值，不一致说明表被手改坏了。");
        int multi = rows.Count(r => r.Flows.Count > 1);
        if (multi > 0)
            issues.Add($"· {multi} 个单元有多笔流向（拆到了多个去向），已按【流序】归并。");

        // 类型上做过的每一次让步都要说出来 —— 猜对了也要说
        if (kindUnknown > 0)
            issues.Add($"◆ 有 {kindUnknown} 行的「类型」不是 煤/岩/排土（第一个是「{firstBadKind}」），"
                     + "已按该行填了哪个量列推断。类型是行的身份，改回三选一才靠得住。");
        if (kindAmbiguous > 0)
            issues.Add($"◆ 有 {kindAmbiguous} 行既没有可用的类型、量列也判不出唯一一类"
                     + "（一个量都没填、或几类量同时填着），已按【煤】处理 —— 这几行的量在汇总里是 0。");
        else if (kindGuessed > 0)
            issues.Add($"· 有 {kindGuessed} 行的「类型」是空的，已按该行的量列推断出来。");

        // 重号必须报：UnitId 是身份，撞号的两行在下游就是同一个东西。
        // ⚠ 归并之后 rows 里 UnitId 已经唯一（同号的多笔流合成了一行），所以这里判的是
        //   "同一个 UnitId 出现了两笔【去向完全相同】的流" —— 那不是拆分，是真重号。
        if (dupIds.Count > 0)
            issues.Add($"◆ UnitId 重复 {dupIds.Distinct().Count()} 个"
                     + $"（如 {string.Join("、", dupIds.Distinct().Take(3))}）—— 身份撞号，"
                     + "按 UnitId 回填人填列会张冠李戴。重复的行已丢弃，只留第一条。"
                     + "（同一单元拆到多个去向请用【流序】+【流量m3】，那不算重号。）");

        var dupFlow = rows.Where(r => r.Flows.Count > 1
                                   && r.Flows.Select(f => f.Destination).Distinct(StringComparer.Ordinal).Count() < r.Flows.Count)
                          .ToList();
        if (dupFlow.Count > 0)
            issues.Add($"◆ 有 {dupFlow.Count} 个单元出现了【去向相同】的重复流"
                     + $"（如 {string.Join("、", dupFlow.Take(3).Select(r => r.UnitId))}）"
                     + " —— 同一去向应当合成一笔，否则占容会被算两遍。");
        return true;
    }

    /// <summary>
    /// 从一行里取出流。<b>没有「流量m3」这一列或它是空的 ⇒ 不算一笔流</b>（返回 null），
    /// 由调用方退回旧格式的"只有去向"路径。
    /// <para>不能拿去向非空就当有流：旧表只填了去向没有量，当成流会让 <c>FlowSumM3</c> 变成 0，
    /// 而对账那一侧看到的是"流合计 0 ≠ 采出量"，看上去像账不平，其实是格式判错了。</para>
    /// </summary>
    private static Flow? MakeFlow(string[] c, int iDest, int iM3, int iHaul, int iMat)
    {
        double? m3 = ND(c, iM3);
        if (!m3.HasValue) return null;
        return new Flow
        {
            Destination = S(c, iDest),
            InSituM3 = m3.Value,
            HaulKm = ND(c, iHaul),
            MaterialCode = S(c, iMat),
        };
    }

    /// <summary>同一单元的第 2 笔起，单元级的列必须与第 1 笔一致。</summary>
    private static bool UnitLevelDiffers(Row first, string[] c, int iDone, int iSeq, int iPeriod, int iStatus)
        => Math.Abs(first.Done - D(c, iDone)) > 1e-6
        || first.Seq != I(c, iSeq)
        || !string.Equals(first.Period, S(c, iPeriod), StringComparison.Ordinal)
        || !string.Equals(first.Status, S(c, iStatus), StringComparison.Ordinal);

    /// <summary>一行汇总（三类分开报 —— 合成一个数就看不出是哪一类少了）。</summary>
    public static string Summary(IReadOnlyList<Row> rows)
    {
        double coalT = rows.Where(r => r.Kind == LedgerKind.Coal).Sum(r => r.CoalT ?? 0);
        double rockM3 = rows.Where(r => r.Kind == LedgerKind.Rock).Sum(r => r.NetRockM3 ?? 0);
        double capM3 = rows.Where(r => r.Kind == LedgerKind.Dump).Sum(r => r.DumpCapM3 ?? 0);
        int nc = rows.Count(r => r.Kind == LedgerKind.Coal),
            nr = rows.Count(r => r.Kind == LedgerKind.Rock),
            nd = rows.Count(r => r.Kind == LedgerKind.Dump);
        return $"煤 {nc} 个 {coalT / 1e4:0.00}万t · 岩 {nr} 个 {rockM3 / 1e4:0.0}万m³ · 排土 {nd} 个 库容 {capM3 / 1e4:0.0}万m³";
    }

    // ── CSV 基础件 ──────────────────────────────────────────────
    private static string N(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";

    private static string Esc(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v.IndexOfAny(new[] { ',', '"', '\n' }) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    private static string S(string[] c, int i) => i >= 0 && i < c.Length ? c[i] : "";

    private static double D(string[] c, int i) =>
        i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>空 → null（不是 0）。「这个量没有」和「这个量是零」在报表上是两件事。</summary>
    private static double? ND(string[] c, int i) =>
        i >= 0 && i < c.Length && double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int I(string[] c, int i) =>
        i >= 0 && i < c.Length && int.TryParse(c[i], out var v) ? v : 0;

    /// <summary>
    /// 按<b>记录</b>切行，而不是按 <c>'\n'</c> 切。引号内的换行属于字段，不是行边界。
    /// <para><see cref="Esc"/> 会把含换行的备注加上引号写出去（第 147 行那个字符集里就有 <c>'\n'</c>），
    /// 读侧要是按 <c>'\n'</c> 硬切，那条备注的后半截会变成一条<b>新数据行</b> ——
    /// UnitId 是半截备注、量全空、类型按量列推断成一个值，静静地混进清单里。</para>
    /// </summary>
    internal static string[] SplitLines(string text)
    {
        var outv = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"') { q = !q; cur.Append(ch); continue; }
            if (ch == '\n' && !q) { outv.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        outv.Add(cur.ToString());
        return outv.ToArray();
    }

    internal static string[] SplitCsv(string line)
    {
        var outv = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else q = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { outv.Add(cur.ToString()); cur.Clear(); }
            else if (ch != '\r') cur.Append(ch);
        }
        outv.Add(cur.ToString());
        return outv.ToArray();
    }
}
