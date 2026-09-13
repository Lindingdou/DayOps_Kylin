// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/DumpSlotCode.cs（逐行对应；仅命名空间适配）
using System.IO;
using System.Linq;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 排土位置的<b>去向码</b> —— 造码与解码的唯一一处。
///
/// <para><b>为什么住在 BlockModelLib</b>：造码的一侧在 MineAssLib（排产写 <c>去向</c> 列），
/// 解码的一侧在 TaskLib（运输线要把码还原成坐标）。两个程序集互不引用，
/// 而 BlockModelLib 是它们唯一的共同祖先 —— <c>StripUnitId</c> 因为同一个理由已经住在这儿。</para>
///
/// <para><b>为什么不能"两处各写一份 + 判据钉住"</b>：Tests.TaskLib 只引 TaskLib、
/// Tests.MineAssLib 只引 MineAssLib，<b>没有任何一个测试工程能同时拿到两份编码去对</b>。
/// 钉不住的东西就会漂 —— 而漂的后果是"那一笔流被画到另一个位置上"，
/// 线的长度、颜色、命中率全都正常。</para>
///
/// <para><b>⚠ 本仓库已经有第二处、且已经漂了</b>：
/// <c>DumpSlotAdapter.ToSlots</c> 用的是 <c>(StepIndex−1)×1e6 + Panel×100 + Sub</c>，
/// 与这里的 <c>Band×1e6 + Panel×100</c> 差整整 1e6（基表实测 Band 全是 1）。
/// 磁盘上那批码（<c>内排土场1-L0-1000100</c>）走的是<b>本文件这一套</b>，
/// 所以解码按这套来是对的。<b>要不要把 ToSlots 也并过来，是个会让已存方案的去向码作废的决定，
/// 本文件没有替谁拍板</b> —— 只是把口径钉在一处，以后不再各漂各的。</para>
/// </summary>
public static class DumpSlotCode
{
    /// <summary>带（推进带序）在排序键里的位宽。</summary>
    public const int StepStride = 1000000;
    /// <summary>幅号在排序键里的位宽。</summary>
    public const int PanelStride = 100;

    /// <summary>排土场名缺省串。<b>改它会让所有老码解不出</b>。</summary>
    public const string DefaultDumpName = "排土场";

    /// <summary>
    /// 台阶级：台账的 <c>煤层/台阶</c> 列写成 <c>L{n}</c>。解不出返回 false，<b>不返回 0</b>
    /// —— L0 是一个合法的级。
    /// </summary>
    public static bool TryLevelFromSeam(string? seam, out int level)
    {
        level = 0;
        var s = (seam ?? "").Trim();
        if (s.Length < 2) return false;
        if (s[0] != 'L' && s[0] != 'l') return false;
        return int.TryParse(s.Substring(1), out level);
    }

    /// <summary>
    /// 排序位次 = 带×1e6 + 幅×100 + 子号。
    ///
    /// <para><b>带号直接用，不减 1</b>。此前会话内那条路（<c>DumpSlotAdapter.ToSlots</c>）用的是
    /// <c>(StepIndex−1)×1e6</c>，与台账这条差整整 1e6 —— 同一个排土位置在两条路上得到两个码，
    /// 于是「会话内排出来的去向码」在三维解码时<b>一笔都对不上</b>，
    /// 而线的长度、颜色、命中率全都正常。现在两侧都走这一个函数。</para>
    ///
    /// <para><b>子号必须进排序键</b>：外凸拐角把带撑长之后，一带会切成几个位置，
    /// 它们的 (级,幅,带) 完全相同、只差子号。不带子号的话排序键相撞，
    /// 分配器挑不出先后，同一份输入排出来的顺序就不定了。
    /// 台账没有子号列 ⇒ 从台账解码时 <paramref name="sub"/> 恒 0（现有 1048 个位置子号全 0，撞码为 0）。</para>
    /// </summary>
    public static int OrderOf(int band, int panel, int sub = 0)
        => Math.Max(0, band) * StepStride
         + Math.Min(9999, Math.Max(0, panel)) * PanelStride
         + Math.Min(99, Math.Max(0, sub));

    /// <summary>
    /// 拼码：<c>{排土场名}-L{级}-{位次}</c>。
    /// <para><paramref name="level"/> 是<b>翻过极性之后</b>的级（自下而上，最下一级 = 0）——
    /// 翻极性这件事归调用方，因为它要知道这一批里的最大级。</para>
    /// </summary>
    public static string Of(string? dumpName, int level, int band, int panel)
    {
        var name = string.IsNullOrWhiteSpace(dumpName) ? DefaultDumpName : dumpName.Trim();
        return $"{name}-L{Math.Max(0, level)}-{OrderOf(band, panel)}";
    }

    /// <summary>
    /// 这一批排土行里的最大台阶级。<b>极性翻转要用它</b>：
    /// 台账 L1 是最上一级，而排土自下而上承接，所以 <c>翻过的级 = maxLevel − 本行级</c>。
    /// <para>一条排土行都没有时返回 0，调用方据此明说"这一批里没有排土位置"，
    /// 而不是拿 0 当成"最大级就是 0"往下算。</para>
    /// </summary>
    public static int MaxLevelOf(IEnumerable<MiningUnitLedger.Row>? rows)
    {
        int max = 0;
        if (rows == null) return 0;
        foreach (var r in rows)
        {
            if (r == null || r.Kind != LedgerKind.Dump) continue;
            if (TryLevelFromSeam(r.Seam, out int lv) && lv > max) max = lv;
        }
        return max;
    }

    /// <summary>一条排土台账行的去向码。<paramref name="maxLevel"/> 见 <see cref="MaxLevelOf"/>。</summary>
    public static string OfLedgerRow(MiningUnitLedger.Row row, int maxLevel)
    {
        TryLevelFromSeam(row.Seam, out int lv);
        return Of(row.Region, Math.Max(0, maxLevel - lv), row.Band, row.Panel);
    }

    /// <summary>
    /// 建「去向码 → 排土行」索引。
    /// <para><b>撞码整条剔除</b>，不是"后者胜"：两个位置共用一个码时，
    /// 后者胜会把指向它的那笔流画到<b>另一个位置</b>上，而线长、颜色、命中率全都正常，
    /// 只有把它整条拿掉、让那笔流"解不出"才看得见。</para>
    /// <para>台账没有子号列，所以同一 (场,级,带,幅) 的两个子位置会撞码。
    /// 现在 1048 个位置子号全 0、撞码为 0；外凸拐角把一带切成子位置时就会出现。</para>
    /// </summary>
    public static Dictionary<string, MiningUnitLedger.Row> BuildIndex(
        IEnumerable<MiningUnitLedger.Row>? rows, out int maxLevel, out List<string> collided)
    {
        var idx = new Dictionary<string, MiningUnitLedger.Row>(StringComparer.Ordinal);
        collided = new List<string>();
        var list = new List<MiningUnitLedger.Row>();
        if (rows != null) foreach (var r in rows) if (r != null && r.Kind == LedgerKind.Dump) list.Add(r);

        maxLevel = MaxLevelOf(list);
        var bad = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in list)
        {
            string code = OfLedgerRow(r, maxLevel);
            if (idx.ContainsKey(code)) { if (bad.Add(code)) collided.Add(code); continue; }
            idx[code] = r;
        }
        foreach (var c in collided) idx.Remove(c);   // 撞了就谁也别要
        return idx;
    }
}
