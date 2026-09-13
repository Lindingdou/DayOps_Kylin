using System;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 「排土条带」最近一次结果的交接点 —— <b>转发到 <see cref="UnitLedger.DumpStripStore"/>（原 BlockModelLib.Domain.DumpStripStore 的忠实移植）</b>。
/// <para>此前这里是一份同名同体的独立 static：排土条带窗口往这一份写，而采掘单元清单/三维模拟/量驱动采剥接续读的是
/// UnitLedger 那一份 —— 两份状态互不相通，「生成过条带」在下游永远看成「本会话还没生成过」。改成转发，只留一份状态。</para>
/// </summary>
public static class DumpStripStore
{
    public static DumpStripPlanner.Result? Last => UnitLedger.DumpStripStore.Last;
    public static DateTime StampedAt => UnitLedger.DumpStripStore.StampedAt;
    public static string SourceNote => UnitLedger.DumpStripStore.SourceNote;
    public static DateTime RejectedAt => UnitLedger.DumpStripStore.RejectedAt;
    public static string RejectedNote => UnitLedger.DumpStripStore.RejectedNote;
    public static bool LastAttemptFailed => UnitLedger.DumpStripStore.LastAttemptFailed;
    public static string Caption => UnitLedger.DumpStripStore.Caption;
    public static void Put(DumpStripPlanner.Result? r, string note) => UnitLedger.DumpStripStore.Put(r, note);
    public static void Clear() => UnitLedger.DumpStripStore.Clear();
}
