// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/MineUnit.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.UnitLedger;
namespace PitMine3D.Kylin.Cad.Units;

/// <summary>采掘单元的类别。排土位置不是采掘单元，它在 <see cref="DumpSlot"/> 那边。</summary>
public enum UnitKind
{
    /// <summary>煤单元 —— 产出侧，凑月煤量的就是它。</summary>
    Coal = 0,
    /// <summary>岩单元 —— 剥离侧，要找地方排的就是它。</summary>
    Rock = 1,
}

/// <summary>
/// 一个<b>采掘单元</b> = 图上一个体 = 计划表里一行。
///
/// <para>直接对应 <c>MiningModelPlanner.Strip</c>：那边已经把采场切成
/// 「层 → 带 → 幅」的体了，这里<b>不重造几何</b>，只做排产要用的口径对齐
/// （量的三口径、平面占地、期初完成度）。</para>
///
/// <para><b>UnitId 是身份，不是标签</b>：与 <c>MiningPlanExporter</c> 导出的 CSV、图上实体的图层编号
/// 三处一致（<c>层-B带号-P幅号</c>）。排产结果按它回写台账、按它定位到图纸。
/// 将来采场侧补上多带枚举（见 `docs/采掘单元排产_设计.md` §2）时要加一位 <c>-S带序</c>，
/// 所以<b>别在任何地方假设 UnitId 只有三段</b>。</para>
/// </summary>
public sealed class MineUnit
{
    /// <summary>唯一身份（<c>层-B带号-P幅号</c>），与图上实体、CSV 台账一致。</summary>
    public string UnitId = "";

    public UnitKind Kind;
    public bool IsCoal => Kind == UnitKind.Coal;

    /// <summary>煤层号 / 岩台阶名（岩台阶以底标高命名，如 "岩1185"）。</summary>
    public string SeamCode = "";
    /// <summary>带号 / 幅号 / 本带共几幅 —— 定序要用（同带同幅连着采，少转场）。</summary>
    public int BandId, PanelIndex, PanelCount = 1;

    /// <summary>质心（世界坐标）。<b>运距的源点就是它</b> —— 单元层不必再从推进坐标 u 反推。</summary>
    public double Cx, Cy, Cz;
    /// <summary>本单元的最低 / 最高标高（m）。压覆判定要用。</summary>
    public double ZLo, ZHi;

    /// <summary>走向长（m） / 推进宽（m） / 厚度（m）。</summary>
    public double StrikeLenM, WidthM, ThickM;

    /// <summary>
    /// 原位实方（m³）。煤 = 煤量；岩 = <b>净岩量</b>（已扣掉穿过该体的煤）。
    /// <para>拿毛量会把同一方土算两遍 —— <c>Strip</c> 那边毛/煤/净三列都留着正是为了这个账。</para>
    /// </summary>
    public double InSituM3;

    /// <summary>物料下标，索引进 <see cref="DumpAllocationInput.Materials"/>。煤单元不排土，此值无意义。</summary>
    public int MaterialIndex;

    /// <summary>
    /// 期初<b>完成度</b> 0~1。0 = 未采，1 = 已采，(0,1) = 在采（上月切下来的跨月幅，U1）。
    /// <para><b>这一列现有 CSV 里没有</b>：台账的「在采」状态只说"正在干"，说不出"干了多少"。
    /// 没有它，跨月幅一进下个月就会被当成整幅重新排一遍，量凭空多出来。</para>
    /// </summary>
    public double DoneFraction;

    /// <summary>本单元还剩多少没采（实方 m³）。</summary>
    public double RemainM3 => Math.Max(0, InSituM3 * (1.0 - Clamp01(DoneFraction)));

    /// <summary>是不是上月留下的跨月幅 —— 本月最高优先级（U1）。</summary>
    public bool IsCarryOver => DoneFraction > 1e-9 && DoneFraction < 1 - 1e-9;

    /// <summary>
    /// 前脸轨的平面折点（扁平 [x,y,x,y,...]）—— 压覆判定用。
    /// <para>用折线而不是包围盒：采掘带是沿露头带走的<b>弯条</b>，一条 500m 长的弯带
    /// 拿矩形一框，会把整片弯内的地都算成它的占地 ⇒ 压覆边凭空多出一堆。</para>
    /// </summary>
    public double[] RailXy = Array.Empty<double>();

    /// <summary>平面包围盒（含推进宽膨胀）—— 压覆粗筛用，避免 O(n²) 的逐点距离。</summary>
    public double MinX, MinY, MaxX, MaxY;

    /// <summary>吨量（t）= 实方 × 密度。<b>吨是三个体积口径之间唯一守恒的中间量</b>。</summary>
    public double TonnageT(double density) => InSituM3 * Math.Max(0, density);

    internal static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>一行摘要（日志/报错里原样用，别各处再编一遍）。</summary>
    public string Caption =>
        $"{UnitId}（{(IsCoal ? "煤" : "岩")} {SeamCode}，{InSituM3 / 1e4:0.##}万m³"
        + (DoneFraction > 1e-9 ? $"，已采 {DoneFraction * 100:0.#}%" : "") + "）";
}

/// <summary>
/// 把「采矿模型」的采掘带（<see cref="MiningModelPlanner.Strip"/>）适配成排产吃的 <see cref="MineUnit"/>。
///
/// <para><b>不重造几何</b>：走向长、推进宽、厚度、方量、前脸轨那边全算好了，这里只做口径对齐。
/// 与 <see cref="DumpSlotAdapter"/> 是对称的两个适配器 —— 采场侧一个、排土侧一个。</para>
///
/// <para><b>⚠ 岩单元必须取净岩量</b>：<c>Strip.EstVolumeM3</c> 对岩已经是扣煤后的净量，
/// <c>GrossVolumeM3</c> 是毛量。拿毛量排产会让同一方土在煤账和岩账里各算一遍，
/// 剥采比整体偏大，<b>而每一项单独校核都对</b>。</para>
/// </summary>
public static class MineUnitAdapter
{
    /// <summary>
    /// 转换一批采掘带。
    /// </summary>
    /// <param name="strips">来自 <see cref="MiningModelPlanner"/> 的采掘带。</param>
    /// <param name="kind">煤还是岩 —— 决定方量口径。<b>调用方说了算，不猜</b>：
    /// 岩台阶的 <c>SeamCode</c> 形如 "岩1185"，但按名字前缀猜煤岩是个会静默错的判据
    /// （煤层号完全可以叫「岩石沟3号」）。</param>
    /// <param name="materialIndex">物料下标（岩用）。</param>
    /// <param name="existing">台账里已有的完成度（UnitId → 0~1）。重算模型后据此回填 —— 几何可以重算，采到哪一步不能丢。</param>
    public static List<MineUnit> ToUnits(IReadOnlyList<MiningModelPlanner.Strip>? strips,
                                         UnitKind kind,
                                         int materialIndex = 0,
                                         IReadOnlyDictionary<string, double>? existing = null)
    {
        var list = new List<MineUnit>();
        if (strips == null || strips.Count == 0) return list;

        foreach (var s in strips)
        {
            if (s == null) continue;
            int n = s.CrestXyz.Length / 3;
            if (n == 0) continue;

            double vol = s.EstVolumeM3;                     // 岩：EstVolumeM3 已是扣煤后的净量
            if (vol <= 1e-9) continue;                       // 零量体不进清单（几何侧已记账）

            double cx = 0, cy = 0, cz = 0, zlo = double.MaxValue, zhi = double.MinValue;
            var rail = new double[n * 2];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                double x = s.CrestXyz[k * 3], y = s.CrestXyz[k * 3 + 1];
                double zt = s.CrestXyz[k * 3 + 2];
                double zb = k * 3 + 2 < s.ToeXyz.Length ? s.ToeXyz[k * 3 + 2] : zt;
                cx += x; cy += y; cz += (zt + zb) * 0.5;
                if (zb < zlo) zlo = zb;
                if (zt > zhi) zhi = zt;
                rail[k * 2] = x; rail[k * 2 + 1] = y;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            cx /= n; cy /= n; cz /= n;

            double w = s.AdvanceWidthM > 1e-6 ? s.AdvanceWidthM : 40.0;
            string unitId = $"{s.SeamCode}-B{s.BandId}-P{s.PanelIndex}";

            double done = 0;
            if (existing != null && existing.TryGetValue(unitId, out double d)) done = MineUnit.Clamp01(d);

            list.Add(new MineUnit
            {
                UnitId = unitId,
                Kind = kind,
                SeamCode = s.SeamCode,
                BandId = s.BandId,
                PanelIndex = s.PanelIndex,
                PanelCount = Math.Max(1, s.PanelCount),
                Cx = cx, Cy = cy, Cz = cz,
                ZLo = zlo, ZHi = zhi,
                StrikeLenM = s.StrikeLenM,
                WidthM = w,
                ThickM = s.ThickM,
                InSituM3 = vol,
                MaterialIndex = Math.Max(0, materialIndex),
                DoneFraction = done,
                RailXy = rail,
                // 包围盒按推进宽膨胀：前脸轨只是体的一条边，体本身往推进方向占了 W
                MinX = minX - w, MinY = minY - w, MaxX = maxX + w, MaxY = maxY + w,
            });
        }
        return list;
    }

    /// <summary>
    /// 台账基表 → 排产输入。<b>这是「存下来的模型」进排产的唯一入口</b>：
    /// 煤/岩 变成 <see cref="MineUnit"/>，排土位置变成 <see cref="DumpSlot"/>。
    ///
    /// <para><b>⚠ 从台账读得到量，读不到走向方位</b>：基表存的是中心点 + 长/宽/厚，
    /// 没有前脸轨的折点。所以占地按<b>以中心点为准、沿 X 轴的近似矩形</b>给，
    /// 压覆判定因此偏保守（可能多连边，不会漏）。要精确的压覆就在生成模型的同一会话里排产
    /// （<c>MiningModelStore</c> 里有真轨）。<b>这条限制由 <paramref name="notes"/> 带出去</b> ——
    /// 悄悄拿近似占地去排先后，排错了没人看得出来。</para>
    /// </summary>
    /// <param name="materialIndex">岩单元的物料下标解析器；null = 一律 0。</param>
    public static (List<MineUnit> Units, List<DumpSlot> Slots) FromLedger(
        IReadOnlyList<MiningUnitLedger.Row>? rows,
        List<string> notes,
        Func<MiningUnitLedger.Row, int>? materialIndex = null)
    {
        var units = new List<MineUnit>();
        var slots = new List<DumpSlot>();
        if (rows == null || rows.Count == 0)
        {
            notes.Add("◆ 基表是空的 —— 先在「采掘单元台账」里从采矿模型和排土条带各取一次。");
            return (units, slots);
        }

        // 排土台阶级从 Seam 列的 "L3" 取；极性要翻，所以先求最大级
        static int DumpLevel(MiningUnitLedger.Row r)
            => r.Seam.Length > 1 && r.Seam[0] == 'L' && int.TryParse(r.Seam.Substring(1), out int lv) ? lv : 0;
        int maxDumpLevel = 0;
        foreach (var r in rows) if (r.Kind == LedgerKind.Dump) maxDumpLevel = Math.Max(maxDumpLevel, DumpLevel(r));

        int noQty = 0;
        foreach (var r in rows)
        {
            if (r == null || r.UnitId.Length == 0) continue;

            if (r.Kind == LedgerKind.Dump)
            {
                double cap = r.DumpCapM3 ?? 0;
                if (cap <= 0) { noQty++; continue; }
                slots.Add(new DumpSlot
                {
                    DumpName = r.Region.Length > 0 ? r.Region : "排土场",
                    // ⚠ 极性必须翻：台账里 L1 = 最上一级（与 Cell.LevelIndex 同源），
                    //   而排土【自下而上】承接。翻错了会从山顶往下排 ——
                    //   图上有台阶、现场没法卸，而每项校核还都是"✓"。
                    Level = Math.Max(0, maxDumpLevel - DumpLevel(r)),
                    // 位次口径走共享件 —— 解码那一侧（三维运输线）从同一个函数拿，不许两处各写一份
                    Order = DumpSlotCode.OrderOf(r.Band, r.Panel),
                    CapacityM3 = cap,                       // 已是占容方（D6），不再换算
                    Cx = r.Cx, Cy = r.Cy, Cz = r.Cz,
                    IsInternal = r.Region.Contains("内排"),
                    AvailableFromMonth = 1,
                });
                continue;
            }

            bool isCoal = r.Kind == LedgerKind.Coal;
            double m3 = isCoal ? (r.CoalM3 ?? 0) : (r.NetRockM3 ?? 0);
            if (m3 <= 1e-9) { noQty++; continue; }

            double len = r.LengthM > 1e-6 ? r.LengthM : 100.0;
            double wid = r.WidthM > 1e-6 ? r.WidthM : 40.0;
            var rail = new[] { r.Cx - len * 0.5, r.Cy, r.Cx + len * 0.5, r.Cy };

            units.Add(new MineUnit
            {
                UnitId = r.UnitId,
                Kind = isCoal ? UnitKind.Coal : UnitKind.Rock,
                SeamCode = r.Seam,
                BandId = r.Band, PanelIndex = r.Panel, PanelCount = Math.Max(1, r.PanelCount),
                Cx = r.Cx, Cy = r.Cy, Cz = r.Cz,
                ZLo = r.ZLo, ZHi = r.ZHi,
                StrikeLenM = len, WidthM = wid, ThickM = r.ThickM,
                InSituM3 = m3,
                MaterialIndex = materialIndex?.Invoke(r) ?? 0,
                DoneFraction = MineUnit.Clamp01(r.Done),
                RailXy = rail,
                MinX = r.Cx - len * 0.5 - wid, MinY = r.Cy - wid,
                MaxX = r.Cx + len * 0.5 + wid, MaxY = r.Cy + wid,
            });
        }

        notes.Add($"· 基表读入：煤 {units.Count(u => u.IsCoal)} 条 · 岩 {units.Count(u => !u.IsCoal)} 条 · "
                + $"排土位置 {slots.Count} 个（合计库容 {slots.Sum(s => s.CapacityM3) / 1e4:0.0} 万m³占容）。");
        if (noQty > 0)
            notes.Add($"◆ 有 {noQty} 行该类的量列是空的，已跳过 —— 空不等于 0，所以不按 0 排。");
        notes.Add("· 压覆按【中心点 + 长宽近似矩形】判（基表不存前脸轨，走向方位读不出来），偏保守。");
        return (units, slots);
    }
}
