// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimDumpRowIndex.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.UnitLedger;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>
/// 「一笔流 → 它那个<b>排土位置的台账行</b>」的唯一一处查法。
///
/// <para><b>为什么要有它</b>：排土块的<b>尺寸</b>和<b>走向方位</b>都只在基表的排土行上，
/// 而流上带的是<b>去向码</b> —— 两者的键不是一套：</para>
/// <list type="bullet">
///   <item>排土行的键 = <c>UnitId</c>（<see cref="DumpStripPlanner.Cell.Code"/>，形如 <c>内排土场1-L1-P01-S01</c>）</item>
///   <item>流上的 <c>去向</c> = <b>去向码</b>（<see cref="DumpSlotCode"/>，形如 <c>内排土场1-L0-1000100</c>）</item>
/// </list>
///
/// <para><b>踩过的坑（2026-08-22）</b>：三维模拟的块体那一层拿 <c>去向码</c> 去查 <c>UnitId</c> 表 ——
/// 两套码永不相等，实测基表 358 个去向码<b>一个都查不到</b>。后果是：尺寸静默退成源采场块的长宽，
/// 而<b>走向方位那一列根本没人读</b>，堆填块只能沿用源采场单元的朝向 ——
/// 与「排土条带」建出来的体<b>中位拧 45.1°</b>（446 笔流：51% 超 45°、最大 89.6°），
/// 而位置、体积、颜色、块数、命中率<b>全部正常</b>，没有一个既有的量看得出来。</para>
///
/// <para><b>正解</b>：O-D 那一侧已经用 <see cref="DumpSlotCode.BuildIndex"/> 把去向码解成排土行了
/// （<see cref="LedgerOdBuilder"/>），<c>SimHaulOd.DestinationName</c> 就是那一行的 UnitId ——
/// 先按它查；只有码的场合再按码查。<b>两条都查不到就返回 null，不猜</b>。</para>
///
/// <para><b>不挂在"有没有路径"上</b>：路网解不出路径时尺寸/方位照样该有
/// （汇点坐标本来就走 O-D，不需要路网 —— 实测 191 笔 O-D 全部解出汇点、同一批路径命中 0）。</para>
/// </summary>
public sealed class SimDumpRowIndex
{
    private readonly Dictionary<string, MiningUnitLedger.Row> _byUnitId = new(StringComparer.Ordinal);
    private Dictionary<string, MiningUnitLedger.Row> _byCode = new(StringComparer.Ordinal);

    /// <summary>按 UnitId 索引到的排土行数。</summary>
    public int ByUnitIdCount => _byUnitId.Count;
    /// <summary>按去向码索引到的排土行数。</summary>
    public int ByCodeCount => _byCode.Count;
    /// <summary>撞码被整条剔除的去向码数（见 <see cref="DumpSlotCode.BuildIndex"/>）。</summary>
    public int CollidedCodes { get; private set; }

    /// <param name="baseRows">
    /// <b>基表全量</b> —— 排土行的几何只在基表里。传本期月度台账进来的话，这两个索引都会是空的，
    /// 而现象只是"排土块长宽借了源块、方位轴对齐"，不报错。
    /// </param>
    public static SimDumpRowIndex Build(IReadOnlyList<MiningUnitLedger.Row>? baseRows)
    {
        var idx = new SimDumpRowIndex();
        if (baseRows == null) return idx;
        foreach (var r in baseRows)
            if (r != null && r.Kind == LedgerKind.Dump && !idx._byUnitId.ContainsKey(r.UnitId))
                idx._byUnitId[r.UnitId] = r;
        idx._byCode = DumpSlotCode.BuildIndex(baseRows, out _, out var collided);
        idx.CollidedCodes = collided.Count;
        return idx;
    }

    /// <summary>
    /// 查这一笔流的排土行。<b>查不到返回 null</b> —— 调用方据此走"尺寸借源块、方位轴对齐并计数"，
    /// 而不是拿源采场单元的角度冒充。
    /// </summary>
    public MiningUnitLedger.Row? Resolve(SimHaulOd? od, SimHaulPath? path)
    {
        // ① O-D 解出来的排土行 UnitId（LedgerOdBuilder 已经用 DumpSlotCode 解过码了）
        string byName = od?.DestinationName ?? "";
        if (byName.Length == 0) byName = path?.DestinationName ?? "";
        if (byName.Length > 0 && _byUnitId.TryGetValue(byName, out var hit)) return hit;

        // ② 只有去向码的场合（老方案、或 O-D 没建）
        string byCode = od?.DestinationCode ?? "";
        if (byCode.Length == 0) byCode = path?.DestinationCode ?? "";
        if (byCode.Length > 0 && _byCode.TryGetValue(byCode, out var hit2)) return hit2;

        return null;
    }
}
