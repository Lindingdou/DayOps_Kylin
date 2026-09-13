// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/UnitPlanStore.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Cad.Units;
namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 「采掘单元排产」最近一次的结果 —— <b>本会话内</b>的交接点（与 <c>DumpStripStore</c> 对称）。
///
/// <para><b>为什么要有它</b>：按 `docs/短期生产计划_按钮职责切分.md` §〇 定的主干，
/// 配对关系<b>只有单元链一处产出</b>，而看它的是「采排配对」窗口 —— 两个窗口不共享字段。
/// 没有这个交接点，采排配对就只能自己再算一份（它此前正是这么干的，
/// `PlanFlowAllocator` 那套贪心就是第三套配对实现）。</para>
///
/// <para><b>只在会话内有效，不持久化</b>：结果依赖当时的台账与逐月配置表，两者一改它就是陈的。
/// 所以 <see cref="Period"/> / <see cref="StampedAt"/> / <see cref="SourceNote"/> 一起带着走，
/// 取用方必须把来源显示出来 —— <b>让人知道这份对位是哪一期、什么时候、按什么目标排的</b>。</para>
/// </summary>
public static class UnitPlanStore
{
    /// <summary>最近一次排产结果（null = 本会话还没排过）。</summary>
    public static UnitPlanResult? Last { get; private set; }

    /// <summary>那一次排的是哪一期（<c>yyyy-MM</c>；空 = 没填期次）。</summary>
    public static string Period { get; private set; } = "";

    /// <summary>排产时刻（本地时间）。</summary>
    public static DateTime StampedAt { get; private set; }

    /// <summary>来源说明（目标量 + 轴取值摘要），供取用方原样显示。</summary>
    public static string SourceNote { get; private set; } = "";

    /// <summary>最近一次<b>被挡下的</b>交接（失败/不自洽）的时刻；<c>default</c> = 没有过。</summary>
    public static DateTime RejectedAt { get; private set; }

    /// <summary>被挡下那次的说明。</summary>
    public static string RejectedNote { get; private set; } = "";

    // 先后次序用自增序号判，不用时间戳 —— `DateTime.Now` 只有约 15ms 分辨率，
    // "失败紧接着成功"会落在同一刻上，用 >= 判就会反过来报"上次失败了"。（同 DumpStripStore）
    private static long _seq, _okSeq, _badSeq;

    /// <summary>上一次交接是被挡下的（挡下之后没有新的成功结果进来）。</summary>
    public static bool LastAttemptFailed => _badSeq > 0 && _badSeq > _okSeq;

    /// <summary>
    /// 一行摘要：取用方直接显示这句，别自己再编一遍。
    /// <para>与 <c>DumpStripStore.Caption</c> 同一条纪律：<b>"上一次失败了"必须说出来</b> ——
    /// 否则用户刚看到排产报错，转到采排配对却看到一张数字齐全的矩阵，
    /// 而那描述的是**更早**那一次。拿陈的对位去核库容，核的是不存在的计划。</para>
    /// </summary>
    public static string Caption
    {
        get
        {
            string body = Last == null
                ? "本会话还没排过产 —— 先在「采掘单元清单」按目标排一次"
                : $"{(Period.Length > 0 ? Period : "未填期次")} · 煤 {Last.CoalT / 1e4:0.00}万t · "
                  + $"剥离 {Last.StripM3 / 1e4:0.0}万m³ · 单元 {Last.Assignments.Count} 个"
                  + $"（{StampedAt:MM-dd HH:mm} 排的）"
                  + (SourceNote.Length > 0 ? " · " + SourceNote : "");
            if (!LastAttemptFailed) return body;
            string who = RejectedNote.Length > 0 ? $"（{RejectedNote}）" : "";
            return $"◆ 最近一次排产没成{who}，下面是**更早**那一次：\n" + body;
        }
    }

    /// <summary>
    /// 由排产侧在解完后调用（<b>成功失败都调</b>）。
    /// <paramref name="note"/> 写目标量与轴取值摘要。
    /// <para>失败/不自洽的结果<b>不覆盖</b>上一次的好结果，但会记一笔，由 <see cref="Caption"/> 说出来。</para>
    /// </summary>
    public static void Put(UnitPlanResult? r, string period, string note)
    {
        if (r == null || !r.Success || r.Assignments.Count == 0)
        {
            _badSeq = ++_seq;
            RejectedAt = DateTime.Now;
            RejectedNote = r == null ? "没有结果" : r.Success ? "一个单元都没排出来" : r.Error;
            return;
        }
        _okSeq = ++_seq;
        Last = r;
        Period = period ?? "";
        SourceNote = note ?? "";
        StampedAt = DateTime.Now;
    }

    /// <summary>清空（台账重载、期次切换到没排过的月份时）。</summary>
    public static void Clear()
    {
        Last = null; Period = ""; SourceNote = ""; StampedAt = default;
        RejectedAt = default; RejectedNote = ""; _seq = _okSeq = _badSeq = 0;
    }
}
