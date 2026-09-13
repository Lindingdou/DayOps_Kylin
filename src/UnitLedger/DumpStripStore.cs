// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/DumpStripStore.cs（逐行对应；仅命名空间适配）
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;

namespace PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 「排土条带」最近一次生成的位置清单 —— <b>本会话内</b>的交接点。
///
/// <para>为什么要有它：位置清单是<b>排产的输入</b>（剥下来的岩总得有地方去），
/// 而生成它的是 `DumpStripDialog`、用它的是「量驱动月度采剥接续」窗口，两边不在一个模块里，
/// 又没有落盘的中间文件。与其让排产窗口去猜、或者要求用户先导出再导入，
/// 不如把最近一次结果放这儿。</para>
///
/// <para><b>只在会话内有效，不持久化</b>：位置清单依赖于当时的排土场台阶线，
/// 图改了它就是陈的。所以 <see cref="StampedAt"/> / <see cref="SourceNote"/> 一起带着走，
/// 取用方要把来源显示出来 —— <b>让人知道这份清单是什么时候、按哪个排土场生成的</b>。</para>
/// </summary>
public static class DumpStripStore
{
    /// <summary>最近一次生成的结果（null = 本会话还没生成过）。</summary>
    public static DumpStripPlanner.Result? Last { get; private set; }

    /// <summary>生成时刻（本地时间）。</summary>
    public static DateTime StampedAt { get; private set; }

    /// <summary>来源说明（排土场名 / 参数摘要），供取用方原样显示。</summary>
    public static string SourceNote { get; private set; } = "";

    /// <summary>最近一次<b>被挡下的</b>交接（空结果）的时刻；<c>default</c> = 没有过。</summary>
    public static DateTime RejectedAt { get; private set; }

    /// <summary>被挡下那次的来源说明。</summary>
    public static string RejectedNote { get; private set; } = "";

    // 先后次序用**自增序号**判，不用时间戳：`DateTime.Now` 只有约 15ms 分辨率，
    // "失败紧接着成功"会落在同一刻上，用 >= 判就会反过来报"上次失败了"。
    // 时间戳只留着**显示给人看**。
    private static long _seq, _okSeq, _badSeq;

    /// <summary>上一次交接是被挡下的（挡下之后没有新的成功结果进来）。</summary>
    public static bool LastAttemptFailed => _badSeq > 0 && _badSeq > _okSeq;

    /// <summary>
    /// 一行摘要：取用方直接显示这句，别自己再编一遍。
    ///
    /// <para><b>为什么要报"上一次失败了"</b>：<see cref="Put"/> 挡空结果是对的
    /// （<b>别拿空的盖掉上一次的好结果</b>），但它<b>挡得无声无息</b> ——
    /// 用户刚在「排土条带」里看到"切完一个位置都没出来"，转到排产窗口点「取排土位置」，
    /// 看到的却是一句<b>数字齐全、语气正面</b>的摘要，描述的是**更早那一次**。
    /// 时间戳虽然带着，但它只说"这份是几点生成的"，不说"你刚才那次没成"。
    /// 这份清单是排产的输入，拿陈的去排产，排出来的方案是照着不存在的排土场排的。</para>
    /// </summary>
    public static string Caption
    {
        get
        {
            string body = Last == null || Last.Cells.Count == 0
                ? "本会话还没生成过排土位置 —— 先跑一次「排土条带」"
                : $"{Last.Cells.Count} 个位置 · {Last.LevelCount} 级 · 总库容 {Last.TotalCapacityM3 / 1e4:0.0}万m³"
                  + $" · {SourceNote}（{StampedAt:MM-dd HH:mm} 生成）";
            if (!LastAttemptFailed) return body;
            string who = RejectedNote.Length > 0 ? $"「{RejectedNote}」" : "";
            return $"◆ 最近一次{who}生成没出位置（{RejectedAt:MM-dd HH:mm}），下面是**更早**那一次：\n" + body;
        }
    }

    /// <summary>
    /// 由「排土条带」在生成后调用（<b>成功失败都调</b>）。<paramref name="note"/> 写排土场名与关键参数。
    /// 空结果不覆盖上一次的好结果，但会<b>记一笔</b>，由 <see cref="Caption"/> 说出来。
    /// </summary>
    public static void Put(DumpStripPlanner.Result? r, string note)
    {
        if (r == null || r.Cells.Count == 0)
        {
            _badSeq = ++_seq;
            RejectedAt = DateTime.Now; RejectedNote = note ?? "";
            return;                                   // 空结果不覆盖上一次的好结果
        }
        _okSeq = ++_seq;
        Last = r;
        SourceNote = note ?? "";
        StampedAt = DateTime.Now;
    }

    /// <summary>清空（排土场重划、或用户明确要求重来时）。</summary>
    public static void Clear()
    {
        Last = null; SourceNote = ""; StampedAt = default;
        RejectedAt = default; RejectedNote = ""; _seq = _okSeq = _badSeq = 0;
    }
}
