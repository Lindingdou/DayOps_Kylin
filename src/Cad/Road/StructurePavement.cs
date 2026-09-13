// 忠实移植自原 PitMine3D Modules/RoadLib/Render/StructurePavement.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 「结构路面」可视化 v1 几何核心（需求07④）。
/// 把道路中心线按路宽在 XY 平面等宽外扩成一条闭合的『结构路面带』(ribbon) 多边形，
/// 用于高质量表征运输联络。纯几何、零 UI 依赖、可单测。
///
/// 设计约束：RoadLib 不能引用 MineAssLib（两模块互不引用），故 v1 自包含——
/// 等宽外扩，不依赖逐站宽度，不贴台阶面（贴面留待 v2）。
/// </summary>
public static class StructurePavement
{
    /// <summary>切向退化判定阈值（XY 平面段长 &lt; 此值视为重合点）。</summary>
    private const double TangentEpsilon = 1e-6;

    /// <summary>
    /// 按半宽 h = widthM/2 把每条中线在 XY 平面沿法向左右外扩成闭合 ribbon。
    /// 左侧顺序点 + 右侧逆序点 + 回到起点闭合，返回 flat [x,y,z, x,y,z, ...]。
    /// 每个外扩点的 Z 沿用对应中线点的 Z。
    /// </summary>
    /// <param name="centerlines">多条中线（每条为有序三维点列）。</param>
    /// <param name="widthM">路面总宽（米）。半宽 = widthM/2。</param>
    /// <returns>每条有效中线对应一条闭合 ribbon 的扁平坐标数组；不足 2 点或全退化的中线被跳过。</returns>
    public static List<double[]> BuildRibbons(
        IReadOnlyList<IReadOnlyList<Point3d>> centerlines, double widthM)
    {
        var result = new List<double[]>();
        if (centerlines == null || widthM <= 0) return result;

        double halfWidth = widthM / 2.0;
        foreach (var line in centerlines)
        {
            if (line == null || line.Count < 2) continue;
            var ribbon = BuildOne(line, halfWidth);
            if (ribbon != null) result.Add(ribbon);
        }
        return result;
    }

    /// <summary>
    /// 重载：输入扁平 [x,y,z, ...] 中线（每条一个 double[]），内部转回点列后复用核心逻辑。
    /// </summary>
    /// <param name="flatCenterlines">多条中线，每条为扁平坐标数组（长度须为 3 的倍数）。</param>
    /// <param name="widthM">路面总宽（米）。</param>
    /// <returns>每条有效中线对应一条闭合 ribbon 的扁平坐标数组。</returns>
    public static List<double[]> BuildRibbons(
        IReadOnlyList<double[]> flatCenterlines, double widthM)
    {
        var result = new List<double[]>();
        if (flatCenterlines == null || widthM <= 0) return result;

        double halfWidth = widthM / 2.0;
        foreach (var flat in flatCenterlines)
        {
            if (flat == null || flat.Length < 6) continue;   // 至少 2 点
            int n = flat.Length / 3;
            var pts = new List<Point3d>(n);
            for (int i = 0; i < n; i++)
                pts.Add(new Point3d(flat[3 * i], flat[3 * i + 1], flat[3 * i + 2]));

            var ribbon = BuildOne(pts, halfWidth);
            if (ribbon != null) result.Add(ribbon);
        }
        return result;
    }

    /// <summary>
    /// 对单条中线生成闭合 ribbon。
    /// 每个中线点用相邻点切向(前后差分)在 XY 平面旋转 90° 得单位法向：
    /// 法向 (nx, ny) = 旋转后的 (-uy, ux)，其中 (ux, uy) 为 XY 单位切向。
    /// 首尾点用单段方向；切向退化(段长 &lt; 阈值)的点沿用上一个法向。
    /// 返回 flat：左侧顺序 + 右侧逆序 + 回到起点闭合；不足 2 点或全退化返回 null。
    /// </summary>
    private static double[]? BuildOne(IReadOnlyList<Point3d> line, double halfWidth)
    {
        int n = line.Count;
        if (n < 2) return null;

        // 1) 逐点求 XY 平面单位法向（退化点先标记，稍后回填）。
        var normals = new (double nx, double ny, bool valid)[n];
        for (int i = 0; i < n; i++)
        {
            // 取该点的切向：内部点用前后差分，首尾点用相邻单段方向。
            double tx, ty;
            if (i == 0)
            {
                tx = line[1].X - line[0].X;
                ty = line[1].Y - line[0].Y;
            }
            else if (i == n - 1)
            {
                tx = line[n - 1].X - line[n - 2].X;
                ty = line[n - 1].Y - line[n - 2].Y;
            }
            else
            {
                tx = line[i + 1].X - line[i - 1].X;
                ty = line[i + 1].Y - line[i - 1].Y;
            }

            double len = Math.Sqrt(tx * tx + ty * ty);
            if (len < TangentEpsilon)
            {
                normals[i] = (0, 0, false);   // 退化，待沿用上一个法向
                continue;
            }

            double ux = tx / len, uy = ty / len;
            // XY 平面旋转 90°：(ux, uy) → (-uy, ux)。约定此为「左」侧法向。
            normals[i] = (-uy, ux, true);
        }

        // 2) 回填退化点：
        //    - 开头连续退化段用「首个有效法向」补齐（此前没有可沿用的上一个法向）。
        //    - 其余退化点正向沿用上一个有效法向。
        int firstValid = -1;
        for (int i = 0; i < n; i++) if (normals[i].valid) { firstValid = i; break; }
        if (firstValid < 0) return null;   // 整条中线全退化（所有点重合）

        for (int i = 0; i < firstValid; i++)
            normals[i] = (normals[firstValid].nx, normals[firstValid].ny, true);

        double lastNx = normals[firstValid].nx, lastNy = normals[firstValid].ny;
        for (int i = firstValid + 1; i < n; i++)
        {
            if (normals[i].valid) { lastNx = normals[i].nx; lastNy = normals[i].ny; }
            else normals[i] = (lastNx, lastNy, true);
        }

        // 3) 左右外扩：左侧 = 点 + h·法向；右侧 = 点 - h·法向。
        //    左侧顺序 + 右侧逆序 + 回到起点闭合，构成闭合多边形。
        //    总点数 = 2n + 1（最后一点回到左侧起点闭合）。
        var flat = new double[(2 * n + 1) * 3];
        int p = 0;

        // 左侧顺序（i: 0 → n-1）
        for (int i = 0; i < n; i++)
        {
            flat[p++] = line[i].X + halfWidth * normals[i].nx;
            flat[p++] = line[i].Y + halfWidth * normals[i].ny;
            flat[p++] = line[i].Z;
        }
        // 右侧逆序（i: n-1 → 0）
        for (int i = n - 1; i >= 0; i--)
        {
            flat[p++] = line[i].X - halfWidth * normals[i].nx;
            flat[p++] = line[i].Y - halfWidth * normals[i].ny;
            flat[p++] = line[i].Z;
        }
        // 回到左侧起点闭合
        flat[p++] = line[0].X + halfWidth * normals[0].nx;
        flat[p++] = line[0].Y + halfWidth * normals[0].ny;
        flat[p++] = line[0].Z;

        return flat;
    }
}
