// 忠实移植自原 PitMine3D Modules/RoadLib/Network/CenterlineLayerDiff.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 中心线图层的<b>增量落地</b>：给出"图上现在这些（带 handle）"和"算完应该是这些"，
/// 算出哪几条原样不动、哪几条要删、哪几条要新写。
///
/// <b>为什么需要它</b>：手动补一条线要走 <c>RoadNetworkConnector.Connect</c>，而它是把整张网
/// 一起吃进去、整张网吐出来的（焊接会挪端点、T 形会把被穿的线切成两截）。原来的落地写法是
/// 「整层删掉 + 整层重写」——补一条线，1500 条中线全被删一遍再画一遍：一次跨托管边界的大批量
/// 删除、一份上百 MB 的 PMBI、外加引擎把 1500 条全部重新细分上屏。可真正变了的通常只有
/// <b>新画的那条 + 被它打断的那一两条</b>。
///
/// 判"没变"用<b>逐坐标全等</b>（不设容差）：Connect 对没碰到的线是原值搬运，
/// 一个 bit 都不会动；反过来只要被焊过/被切过，坐标必然不同，宁可多写也不能漏写。
/// </summary>
public sealed class CenterlineLayerDiff
{
    /// <summary>原样保留（不删不写）的实体 handle。</summary>
    public IReadOnlyList<ulong> KeepHandles { get; private init; } = Array.Empty<ulong>();

    /// <summary>要从图上删掉的实体 handle（被改动 / 被替代 / 已消失的那些）。</summary>
    public IReadOnlyList<ulong> DeleteHandles { get; private init; } = Array.Empty<ulong>();

    /// <summary>要新写进图层的中线（含被改动后的那几条）。</summary>
    public IReadOnlyList<double[]> WriteLines { get; private init; } = Array.Empty<double[]>();

    /// <summary>是否什么都不用动（新旧完全一致）。</summary>
    public bool IsNoop => DeleteHandles.Count == 0 && WriteLines.Count == 0;

    public string Summary => $"保留 {KeepHandles.Count} 条 / 改写 {WriteLines.Count} 条 / 删旧 {DeleteHandles.Count} 条";

    /// <param name="before">图上现有中线：(实体 handle, 扁平 [x,y,z,...])。</param>
    /// <param name="after">算完的目标中线集。<b>空表不代表"清空图层"</b> —— 调用方必须先自己挡掉
    /// （退化输入把整层中线赔进去这件事，本类不替它做决定，只如实报"全删"）。</param>
    public static CenterlineLayerDiff Compute(
        IReadOnlyList<(ulong Handle, double[] Xyz)>? before,
        IReadOnlyList<double[]>? after)
    {
        var keep = new List<ulong>();
        var del = new List<ulong>();
        var write = new List<double[]>();

        if (before == null || before.Count == 0)
        {
            if (after != null)
                foreach (var l in after) if (l is { Length: >= 6 }) write.Add(l);
            return new CenterlineLayerDiff { WriteLines = write };
        }

        // 旧线按内容哈希分桶；同内容多条时逐条消耗，避免"一条旧线被两条新线同时认领"。
        var buckets = new Dictionary<long, List<int>>(before.Count);
        for (int i = 0; i < before.Count; i++)
        {
            var xyz = before[i].Xyz;
            if (xyz is not { Length: >= 6 }) { del.Add(before[i].Handle); continue; }   // 退化条目直接判删
            long k = HashOf(xyz);
            if (!buckets.TryGetValue(k, out var l)) { l = new List<int>(); buckets[k] = l; }
            l.Add(i);
        }

        var matched = new bool[before.Count];
        if (after != null)
            foreach (var line in after)
            {
                if (line is not { Length: >= 6 }) continue;
                int hit = -1;
                if (buckets.TryGetValue(HashOf(line), out var cand))
                    foreach (int i in cand)
                    {
                        if (matched[i]) continue;
                        if (!SameCoords(before[i].Xyz, line)) continue;   // 哈希撞了也得逐坐标核一遍
                        hit = i;
                        break;
                    }
                if (hit >= 0) { matched[hit] = true; keep.Add(before[hit].Handle); }
                else write.Add(line);
            }

        for (int i = 0; i < before.Count; i++)
            if (!matched[i] && before[i].Xyz is { Length: >= 6 })
                del.Add(before[i].Handle);

        return new CenterlineLayerDiff { KeepHandles = keep, DeleteHandles = del, WriteLines = write };
    }

    /// <summary>内容哈希（FNV-1a 走 double 的原始 bit，不做任何取整 —— 判的是"一个 bit 都没变"）。</summary>
    private static long HashOf(double[] xyz)
    {
        unchecked
        {
            ulong h = 14695981039346656037UL;
            h = (h ^ (ulong)xyz.Length) * 1099511628211UL;
            for (int i = 0; i < xyz.Length; i++)
            {
                ulong b = (ulong)BitConverter.DoubleToInt64Bits(xyz[i]);
                h = (h ^ b) * 1099511628211UL;
                h = (h ^ (b >> 32)) * 1099511628211UL;
            }
            return (long)h;
        }
    }

    private static bool SameCoords(double[] a, double[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i])) return false;   // Equals 而非 == ：NaN 也按"没变"处理，别把坏线反复重写
        return true;
    }
}
