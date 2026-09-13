// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/MiningModelStore.cs（逐行对应；仅命名空间适配）
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.UnitLedger;

/// <summary>
/// 「采矿模型」最近一次生成的采掘带清单 —— <b>本会话内</b>的交接点。
///
/// <para><b>与 <see cref="DumpStripStore"/> 是对称的一对</b>：那边交接排土位置，这边交接采掘带。
/// 排土侧早就有这个交接点，采场侧一直没有 —— 结果台账只能靠"先导出 CSV 再打开"，
/// 而中间那一步<b>人会忘</b>，忘了就是拿上一次的清单在排产。</para>
///
/// <para><b>煤和岩分开存</b>：它们是分两趟生成的（<c>Generate()</c> 里煤一趟岩一趟），
/// 合并成一份的话，只跑了煤那一趟时会拿到"上一次的岩 + 这一次的煤"，
/// 两半来自不同的参数，<b>而合起来看完全正常</b>。</para>
///
/// <para><b>只在会话内有效，不持久化</b>：清单依赖当时的现状面与地质模型，图改了它就是陈的。
/// 所以 <see cref="StampedAt"/> / <see cref="SourceNote"/> 一起带着走，取用方要把来源显示出来。
/// 要持久化请走 <see cref="MonthlyUnitLedgerStore"/> 的基表。</para>
/// </summary>
public static class MiningModelStore
{
    /// <summary>一次生成的结果。</summary>
    public sealed class Snapshot
    {
        public List<MiningModelPlanner.Strip> Strips = new();
        public DateTime StampedAt;
        public string SourceNote = "";
        /// <summary>采场名（写进台账的「采场」列）。</summary>
        public string RegionName = "";

        public double TotalM3 => Strips.Sum(s => s.EstVolumeM3);
    }

    /// <summary>最近一次的<b>煤</b>采掘带（null = 本会话还没生成过）。</summary>
    public static Snapshot? Coal { get; private set; }
    /// <summary>最近一次的<b>岩</b>采掘带。</summary>
    public static Snapshot? Rock { get; private set; }

    /// <summary>最近一次<b>被挡下的</b>交接（空结果）的时刻与说明；<c>default</c> = 没有过。</summary>
    public static DateTime RejectedAt { get; private set; }
    public static string RejectedNote { get; private set; } = "";

    // 先后次序用自增序号判，不用时间戳：DateTime.Now 只有约 15ms 分辨率，
    // "失败紧接着成功"会落在同一刻上，用 >= 判就会反过来报"上次失败了"。
    private static long _seq, _okSeq, _badSeq;

    /// <summary>上一次交接是被挡下的（挡下之后没有新的成功结果进来）。</summary>
    public static bool LastAttemptFailed => _badSeq > 0 && _badSeq > _okSeq;

    /// <summary>
    /// 一行摘要：取用方直接显示这句，别自己再编一遍。
    /// <para><b>为什么要报"上一次失败了"</b>：<see cref="Put"/> 挡空结果是对的
    /// （别拿空的盖掉上一次的好结果），但它<b>挡得无声无息</b> —— 用户刚在「采矿模型」里
    /// 看到"一条都没建出来"，转到台账点「从采矿模型取」，看到的却是一句数字齐全、语气正面的摘要，
    /// 描述的是<b>更早那一次</b>。</para>
    /// </summary>
    public static string Caption
    {
        get
        {
            string body;
            if (Coal == null && Rock == null)
                body = "本会话还没生成过采矿模型 —— 先跑一次「采矿模型」（煤/岩）";
            else
            {
                var parts = new List<string>();
                if (Coal != null)
                    parts.Add($"煤 {Coal.Strips.Count} 条 {Coal.TotalM3 / 1e4:0.0}万m³（{Coal.StampedAt:MM-dd HH:mm}）");
                if (Rock != null)
                    parts.Add($"岩 {Rock.Strips.Count} 条 {Rock.TotalM3 / 1e4:0.0}万m³（{Rock.StampedAt:MM-dd HH:mm}）");
                body = string.Join(" · ", parts);
                // 只有一半时必须说出来：两趟生成的东西，缺一趟排产就少一半的账
                if (Coal == null) body += "　◆ 还没有煤 —— 排产会没有产出侧";
                if (Rock == null) body += "　◆ 还没有岩 —— 排产会没有剥离侧";
            }
            if (!LastAttemptFailed) return body;
            string who = RejectedNote.Length > 0 ? $"「{RejectedNote}」" : "";
            return $"◆ 最近一次{who}生成没出采掘带（{RejectedAt:MM-dd HH:mm}），下面是**更早**那一次：\n" + body;
        }
    }

    /// <summary>
    /// 由「采矿模型」在生成后调用（<b>成功失败都调</b>）。
    /// 空结果不覆盖上一次的好结果，但会<b>记一笔</b>，由 <see cref="Caption"/> 说出来。
    /// </summary>
    public static void Put(IReadOnlyList<MiningModelPlanner.Strip>? strips, bool isRock,
                           string note, string regionName = "")
    {
        if (strips == null || strips.Count == 0)
        {
            _badSeq = ++_seq;
            RejectedAt = DateTime.Now; RejectedNote = note ?? "";
            return;
        }
        _okSeq = ++_seq;
        var snap = new Snapshot
        {
            Strips = strips.ToList(),
            StampedAt = DateTime.Now,
            SourceNote = note ?? "",
            RegionName = regionName ?? "",
        };
        if (isRock) Rock = snap; else Coal = snap;
    }

    /// <summary>清空（重新开始时）。</summary>
    public static void Clear()
    {
        Coal = null; Rock = null;
        RejectedAt = default; RejectedNote = "";
        _seq = _okSeq = _badSeq = 0;
    }

    /// <summary>
    /// 直接产出台账行（煤 + 岩合在一起）。<b>没有的那一类就是没有</b>，不补空行。
    /// </summary>
    public static List<MiningUnitLedger.Row> ToLedgerRows(double coalDensity = MiningUnitLedger.DefaultCoalDensity)
    {
        var rows = new List<MiningUnitLedger.Row>();
        if (Coal != null) rows.AddRange(MiningUnitLedger.FromStrips(Coal.Strips, false, Coal.RegionName, coalDensity));
        if (Rock != null) rows.AddRange(MiningUnitLedger.FromStrips(Rock.Strips, true, Rock.RegionName, coalDensity));
        return rows;
    }
}
