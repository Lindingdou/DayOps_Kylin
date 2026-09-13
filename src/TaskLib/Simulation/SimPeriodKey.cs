// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimPeriodKey.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>
/// 帧 → 期次键（<c>yyyy-MM</c>）。采掘单元台账按月存，舞台要拿这个键把每个单元放到对应帧上。
///
/// <para><b>为什么单独一个类</b>：这条规则曾经写在窗口的私有方法里，写错了也测不到 ——
/// 第一版按「第 0 位起 7 个字符、第 4 位是 '-'」取，而真实帧标签是「<c>推算 2026-08 月</c>」，
/// 前面带前缀。结果期次全空 ⇒ PeriodKeys 空数组 ⇒「定不了每个单元落在第几帧」⇒ 一个体都建不出来。
/// 现在它在 UI 外面，判据能直接钉住。</para>
///
/// <para><b>两条纪律</b>：
/// ① 权威字段是 <see cref="SimFrame.Period"/>（= <c>PeriodRow.Label</c>），
///    <see cref="SimFrame.Label"/> 是给人看的，只当退路；
/// ② 认不出来就返回空字符串，<b>不猜</b> —— 猜错的期次会让单元整体错帧，而画面看上去完全正常。</para>
/// </summary>
public static class SimPeriodKey
{
    // 年份 + 分隔 + 月份；分隔认 '-' '/' '年'，两侧容空格。
    // ⚠ 不锚定开头 —— 前缀是常态（"推算 2026-08 月"）。加个 ^ 就是那次 0 个体的根因。
    private static readonly Regex Span = new(@"(19|20)\d{2}\s*[-/年]\s*\d{1,2}", RegexOptions.Compiled);
    private static readonly Regex Split = new(@"^((?:19|20)\d{2})\D+(\d{1,2})$", RegexOptions.Compiled);

    /// <summary>从一段文字里抠出 <c>yyyy-MM</c>。抠不出返回空串。</summary>
    public static string Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var m = Span.Match(text);
        if (!m.Success) return "";
        var mm = Split.Match(m.Value.Replace(" ", ""));
        if (!mm.Success) return "";
        int mo = int.Parse(mm.Groups[2].Value);
        if (mo < 1 || mo > 12) return "";          // "2026-13" 不是月份，宁可当认不出
        return $"{mm.Groups[1].Value}-{mo:00}";
    }

    /// <summary>一帧的期次键：先 <see cref="SimFrame.Period"/>，再退 <see cref="SimFrame.Label"/>。</summary>
    public static string Of(SimFrame? f)
    {
        if (f == null) return "";
        var k = Parse(f.Period);
        return k.Length > 0 ? k : Parse(f.Label);
    }

    /// <summary>
    /// 整条时间轴的<b>有序</b>期次键（第 i 个 = 第 i 帧）。认不出的帧留空串<b>占位</b> ——
    /// 位置绝不能错位，宁可某几帧没有键，也不能把后面的键往前挪。
    /// </summary>
    public static string[] Of(SimTimeline? tl)
    {
        if (tl == null) return Array.Empty<string>();
        var keys = new string[tl.Frames.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = Of(tl.Frames[i]);
        return keys;
    }
}
