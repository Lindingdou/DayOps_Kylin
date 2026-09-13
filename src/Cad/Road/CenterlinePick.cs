// 忠实移植自原 PitMine3D Modules/RoadLib/Network/CenterlinePick.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 「视口点一下 → 点中的是哪条中心线」的纯几何判定，供「中心线管理」的视口点选用。
///
/// 按 <b>XY 平面</b>判距（点击落点的 Z 来自地表射线求交 / 投影面，与中线自己的标高本就对不齐，
/// 掺进来只会让"点得挺准却选不中"），且判的是<b>到线段</b>的距离而非到顶点 ——
/// 提取出来的中线一段可以几十米长，只看顶点会让线中间整段点不中。
///
/// 半径外一律返回 -1：点空处不能顺手删掉几百米外那条最近的路。
/// </summary>
public static class CenterlinePick
{
    /// <summary>
    /// 找 XY 距 (<paramref name="px"/>, <paramref name="py"/>) 最近的一条中线。
    /// </summary>
    /// <param name="linesFlat">候选中线，逐条扁平 [x,y,z,...]；顶点 &lt; 2 的条目跳过。</param>
    /// <param name="maxDistM">命中半径 (m)：最近的一条也超出它就算没点中。</param>
    /// <param name="distM">命中线的 XY 距离；未命中为 <see cref="double.NaN"/>。</param>
    /// <returns><paramref name="linesFlat"/> 中的下标；未命中 = -1。</returns>
    public static int NearestIndex(double px, double py, IReadOnlyList<double[]>? linesFlat,
                                   double maxDistM, out double distM)
    {
        distM = double.NaN;
        if (linesFlat == null || linesFlat.Count == 0 || !(maxDistM > 0)) return -1;

        // 先求真正的最近条（严格 <：并列时取靠前那条，同一份输入两次点同一处必得同一条），
        // 再拿半径卡一刀（含边界）—— 把半径混进比较里会让"恰好在半径上"变成不命中。
        int best = -1;
        double best2 = double.MaxValue;
        for (int i = 0; i < linesFlat.Count; i++)
        {
            var f = linesFlat[i];
            if (f == null || f.Length < 6) continue;
            for (int s = 0; s + 5 < f.Length; s += 3)
            {
                double d2 = DistSqToSeg(px, py, f[s], f[s + 1], f[s + 3], f[s + 4]);
                if (d2 < best2) { best2 = d2; best = i; }
            }
        }
        if (best < 0) return -1;

        double d = Math.Sqrt(best2);
        if (d > maxDistM) return -1;
        distM = d;
        return best;
    }

    /// <summary>XY 平面上点到线段的距离平方（端点重合的退化段按点算）。</summary>
    private static double DistSqToSeg(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax, aby = by - ay;
        double l2 = abx * abx + aby * aby;
        double t = l2 < 1e-12 ? 0.0 : ((px - ax) * abx + (py - ay) * aby) / l2;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double dx = px - (ax + abx * t), dy = py - (ay + aby * t);
        return dx * dx + dy * dy;
    }

    /// <summary>中线三维长度 (m)：删除前报"删掉多长的路"用。</summary>
    public static double Length3d(double[]? flat)
    {
        if (flat == null || flat.Length < 6) return 0.0;
        double s = 0;
        for (int i = 0; i + 5 < flat.Length; i += 3)
        {
            double dx = flat[i + 3] - flat[i], dy = flat[i + 4] - flat[i + 1], dz = flat[i + 5] - flat[i + 2];
            s += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        return s;
    }
}
