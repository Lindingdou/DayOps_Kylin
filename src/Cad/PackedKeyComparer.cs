using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 打包成 64 位整数的复合键(边 <c>(lo&lt;&lt;32)|hi</c>、格 <c>(gx&lt;&lt;32)|gy</c>、(顶点,颜色) 等)专用比较器。
/// <para>
/// 坑：<c>long.GetHashCode()</c> = 高 32 位 ^ 低 32 位。三角网的边两端号只差 1 / 行宽 / 行宽+1，
/// 异或之后整张网只剩几百个不同散列值，Dictionary/HashSet 退化成链表——实测 300×300 格网 27 万条边
/// 要 11 s（散列值 601 个），百万三角的面模型「生成三角网边界」十几分钟都跑不完；
/// 换成本比较器同一份数据 11 ms。栅格格键 (gx,gy) 同病：gx^gy 在方阵上只有 ~√n 个值。
/// </para>
/// 凡是 long/ulong 打包键的 Dictionary/HashSet 一律传 <see cref="Instance"/>；键本身不变，
/// 仍可按 <c>(int)(key &gt;&gt; 32)</c> / <c>(int)key</c> 拆回两端。
/// </summary>
public sealed class PackedKeyComparer : IEqualityComparer<long>, IEqualityComparer<ulong>
{
    public static readonly PackedKeyComparer Instance = new();
    private PackedKeyComparer() { }

    public bool Equals(long x, long y) => x == y;
    public bool Equals(ulong x, ulong y) => x == y;
    public int GetHashCode(long k) => Mix((ulong)k);
    public int GetHashCode(ulong k) => Mix(k);

    /// <summary>murmur3 fmix64 终混：把 64 位所有比特搅进低 32 位，高低半区差 1 也散得开。</summary>
    public static int Mix(ulong z)
    {
        z ^= z >> 33; z *= 0xff51afd7ed558ccdUL;
        z ^= z >> 33; z *= 0xc4ceb9fe1a85ec53UL;
        z ^= z >> 33;
        return (int)z;
    }
}
