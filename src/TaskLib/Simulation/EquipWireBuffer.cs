// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/EquipWireBuffer.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  设备形态的**线框**写入端 —— 与实体层共用同一套形状定义
//
//  为什么要它：动画层只能走 overlay（逐帧可动、不入库、不占 Undo），而 overlay 是 line list，
//  没有真填充。于是同一台铲需要两种介质：实体的多色实心块、动画的三维线框。
//  两边各写一套「铲长什么样」的话，改一边另一边不动 —— 现象是同一台设备在两个图层里长得不一样，
//  而两边各自都自洽、都不报错。所以形态只在 EquipSymbolLibrary 里写一次，
//  本类只回答「一个盒子落成哪 12 条边」。
//
//  ── 与 EquipMeshBuffer 的对应（逐条，不是"差不多"）──
//    Box  → 转成 Hex（同一套 8 个角点），12 条边：底 4 + 顶 4 + 竖 4
//    Hex  → 同上，用调用方给的 8 个角点
//    PrismX → 两端盖各 sides 条 + 纵向 sides 条（不画端盖的辐条，那在缩小视图下糊成一坨）
//  ⇒ 线框的**角点集合与实体的角点集合逐点相同**，只是连法不同。形态不可能漂。
//
//  ── 代价 ──
//  一台设备 3~4 个体 ≈ 36~48 条边。60 台 ≈ 2900 段，一次 P/Invoke 推完（组批量口）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 三维线框累加缓冲。写进来的是**局部**坐标，<see cref="BeginPose"/> 之后由缓冲自己变换到世界系
/// —— 与 <see cref="EquipMeshBuffer"/> 同一套位姿口径（尺度 → 绕 Z 旋 → 平移）。
/// </summary>
public sealed class EquipWireBuffer : IEquipShapeSink
{
    private readonly List<double> _seg = new();   // 每段 6 个 double
    private readonly List<uint> _rgb = new();

    private double _ox, _oy, _oz, _cos = 1, _sin, _s = 1;

    /// <summary>段数。</summary>
    public int SegmentCount => _rgb.Count;

    /// <summary>「顶点数」= 段数 × 2。<b>只为满足 <see cref="IEquipShapeSink"/> 的对账口径</b>，
    /// 与实体那边的顶点数不可比（连法不同），别拿两者互相校验。</summary>
    public int VertexCount => _rgb.Count * 2;

    public bool IsEmpty => _rgb.Count == 0;

    /// <summary>扁平段坐标 [x0,y0,z0,x1,y1,z1,...]（世界系）。</summary>
    public double[] WorldSegments => _seg.ToArray();
    /// <summary>逐段颜色 0xRRGGBB。</summary>
    public uint[] SegmentRgb => _rgb.ToArray();

    public void Clear() { _seg.Clear(); _rgb.Clear(); }

    public void BeginPose(double ox, double oy, double oz, double headingRad, double scale)
    {
        _ox = ox; _oy = oy; _oz = oz;
        _cos = Math.Cos(headingRad); _sin = Math.Sin(headingRad);
        _s = scale;
    }

    public void EndPose() { }

    public void Box(double x0, double x1, double y0, double y1, double z0, double z1, uint rgb)
        => Hex(x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0,
               x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1, rgb);

    public void Hex(double x0, double y0, double z0, double x1, double y1, double z1,
                    double x2, double y2, double z2, double x3, double y3, double z3,
                    double x4, double y4, double z4, double x5, double y5, double z5,
                    double x6, double y6, double z6, double x7, double y7, double z7,
                    uint rgb)
    {
        // 底 4
        Edge(x0, y0, z0, x1, y1, z1, rgb);
        Edge(x1, y1, z1, x2, y2, z2, rgb);
        Edge(x2, y2, z2, x3, y3, z3, rgb);
        Edge(x3, y3, z3, x0, y0, z0, rgb);
        // 顶 4
        Edge(x4, y4, z4, x5, y5, z5, rgb);
        Edge(x5, y5, z5, x6, y6, z6, rgb);
        Edge(x6, y6, z6, x7, y7, z7, rgb);
        Edge(x7, y7, z7, x4, y4, z4, rgb);
        // 竖 4
        Edge(x0, y0, z0, x4, y4, z4, rgb);
        Edge(x1, y1, z1, x5, y5, z5, rgb);
        Edge(x2, y2, z2, x6, y6, z6, rgb);
        Edge(x3, y3, z3, x7, y7, z7, rgb);
    }

    public void PrismX(double x0, double x1, double zc0, double zc1, double radiusY, int sides, uint rgb)
    {
        if (sides < 3) sides = 3;
        // ★ 角点公式与 EquipMeshBuffer.PrismX 逐字相同（连 rz<=0 也不额外兜底）——
        //   兜底一加，退化情形下两种介质的角点就不一样了，而那正是本类存在的理由。
        double zc = (zc0 + zc1) * 0.5, rz = (zc1 - zc0) * 0.5;

        double Py(int i) => radiusY * Math.Cos(2 * Math.PI * i / sides);
        double Pz(int i) => zc + rz * Math.Sin(2 * Math.PI * i / sides);

        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            Edge(x0, Py(i), Pz(i), x0, Py(j), Pz(j), rgb);   // 后盖
            Edge(x1, Py(i), Pz(i), x1, Py(j), Pz(j), rgb);   // 前盖
            Edge(x0, Py(i), Pz(i), x1, Py(i), Pz(i), rgb);   // 纵向
        }
    }

    /// <summary>写一条边（局部 → 世界）。零长段丢弃（内核对它没有意义，还占一次投影）。</summary>
    private void Edge(double ax, double ay, double az, double bx, double by, double bz, uint rgb)
    {
        if (ax == bx && ay == by && az == bz) return;
        Xf(ax, ay, az, out double wx0, out double wy0, out double wz0);
        Xf(bx, by, bz, out double wx1, out double wy1, out double wz1);
        _seg.Add(wx0); _seg.Add(wy0); _seg.Add(wz0);
        _seg.Add(wx1); _seg.Add(wy1); _seg.Add(wz1);
        _rgb.Add(rgb);
    }

    private void Xf(double x, double y, double z, out double wx, out double wy, out double wz)
    {
        double sx = x * _s, sy = y * _s, sz = z * _s;
        wx = _ox + sx * _cos - sy * _sin;
        wy = _oy + sx * _sin + sy * _cos;
        wz = _oz + sz;
    }
}
