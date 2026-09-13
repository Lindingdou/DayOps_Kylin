// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimBlockDumpAzimuthTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  「排土块按【排土位置自己】的走向摆」判据（R-T8）
//
//  ── 踩过的坑（2026-08-22）──
//  堆填块长期沿用**源采场单元**的 HeadingRad —— 而排土条带走的是排土场自己那圈台阶线，
//  与采场台阶线毫无关系。基表实测 446 笔能解出去向的流，两者
//  **中位差 45.1°、51% 超过 45°、最大 89.6°**，图上排土块与「排土条带」建出来的体整体拧着。
//  而位置、体积、颜色、命中率、块数**全部正常** —— 没有一个既有的量看得出来。
//  ⇒ 判据必须直接量【落地端收到的那批线段的主轴方向】，不能问任何标量。
//
//  ── 判什么 ────────────────────────────────────────────────────────────────
//    B1 排土块主轴 = 排土位置自己的走向（与源走向差 60° 时不许跟着源走）。
//    B2 交叉钉：同一次里采场块主轴仍 = 源走向 —— 证明是"各读各的"，
//       不是把两侧一起改成了排土角（那样 B1 也会绿）。
//    B3 没给排土走向时轴对齐（长轴朝东）+ DumpAxisDefault 计数，且**不许**回落成源走向。
//    B4 F0 自检：把排土走向设成别的值，主轴必须跟着动 —— 否则 B1 是恒真的。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimBlockDumpAzimuthTests
{
    /// <summary>只记各组收到的线段坐标（判据要量方向，光有条数不够）。</summary>
    private sealed class GeomSink : ISimDynamicOverlay, ISimFaceSink
    {
        private readonly Dictionary<string, (double[] Xyz, int N)> _lines = new(StringComparer.Ordinal);

        public bool Available => true;
        public string StatusLabel => "台架";

        public bool SetFaces(string group, double[]? xyz, uint[]? argb, int triCount) => true;

        public bool SetLines(string group, double[]? xyz, uint[]? argb, int segCount)
        { _lines[group] = (xyz ?? Array.Empty<double>(), xyz == null ? 0 : Math.Max(0, segCount)); return true; }

        public bool SetMarkers(string group, double[]? xyz, uint[]? argb, float[]? px, byte[]? st, int n) => true;

        public bool SetLabels(string group, double[]? xyz, IReadOnlyList<string>? texts, uint[]? argb,
                              float[]? h, byte[]? ha, byte[]? va, int n) => true;

        public void Clear(string group) { _lines[group] = (Array.Empty<double>(), 0); }
        public void RequestRender() { }

        /// <summary>某组这一帧收到的线段端点（XY），按段数截断 —— 缓冲可能比段数长。</summary>
        public List<(double X, double Y)> Points(string group)
        {
            var pts = new List<(double, double)>();
            if (!_lines.TryGetValue(group, out var g)) return pts;
            for (int i = 0; i < g.N; i++)
            {
                int o = i * 6;
                if (o + 5 >= g.Xyz.Length) break;
                pts.Add((g.Xyz[o], g.Xyz[o + 1]));
                pts.Add((g.Xyz[o + 3], g.Xyz[o + 4]));
            }
            return pts;
        }
    }

    /// <summary>
    /// 点集的主轴方位角（度，北起顺时针，[0,180)）。
    /// <para>盒子是 长 × 宽 的矩形，长 ≫ 宽 时主轴就是它的长轴 —— 与摆放时用的 heading 同一件事。
    /// 点数不足或各向同性（长≈宽）时返回 null，**不返回 0**：0° 是正南北，是个合法答案。</para>
    /// </summary>
    private static double? PrincipalAzimuthDeg(List<(double X, double Y)> pts)
    {
        if (pts.Count < 4) return null;
        double mx = 0, my = 0;
        foreach (var p in pts) { mx += p.X; my += p.Y; }
        mx /= pts.Count; my /= pts.Count;

        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts)
        {
            double dx = p.X - mx, dy = p.Y - my;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        double tr = sxx + syy;
        if (tr < 1e-9) return null;
        double diff = Math.Sqrt((sxx - syy) * (sxx - syy) + 4 * sxy * sxy);
        double l1 = (tr + diff) * 0.5, l2 = (tr - diff) * 0.5;
        if (l1 <= 1e-12 || l1 - l2 < 0.05 * l1) return null;      // 长宽太接近，主轴没有意义

        // 最大特征值对应的特征向量
        double vx, vy;
        if (Math.Abs(sxy) > 1e-12) { vx = l1 - syy; vy = sxy; }
        else { vx = sxx >= syy ? 1 : 0; vy = sxx >= syy ? 0 : 1; }
        double az = Math.Atan2(vx, vy) * 180.0 / Math.PI;         // 北起顺时针
        az %= 180.0;
        if (az < 0) az += 180.0;
        return az;
    }

    /// <summary>两个 [0,180) 方位之间的夹角（度，≤90）。</summary>
    private static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 180.0;
        return Math.Min(d, 180.0 - d);
    }

    private const double SrcAz = 20.0;      // 源采场单元走向
    private const double DumpAz = 80.0;     // 排土条带走向（差 60°，真实数据的中位差是 45°）

    private static SimBlockUnit Unit(double? dumpAz)
        => new()
        {
            UnitId = "U1", Seq = 1,
            Sx = 620000, Sy = 4380000, Sz = 1200,
            LenM = 300, WidM = 30, ThkM = 12,
            AzimuthDeg = SrcAz,
            Dx = 621500, Dy = 4380900, Dz = 1150,
            DumpLenM = 200, DumpWidM = 20, DumpThkM = 8,
            DumpAzimuthDeg = dumpAz,
            InSituM3 = 108000, LooseM3 = 145800, CapacityM3 = 122040,
            Xyz = new[] { 620000.0, 4380000.0, 1200.0, 621500.0, 4380900.0, 1150.0 },
        };

    /// <summary>建一期、扫到堆完那一帧，取排土组 / 采场组的主轴。</summary>
    private static (double? Dump, double? Pit, SimBlockTransferResult Res) Run(double? dumpAz)
    {
        var sink = new GeomSink();
        var st = new SimBlockTransferStage(sink);
        st.Params.WorkPoints = 1;
        st.Params.ShowShovels = false;      // 设备符号会混进线框，判方向时先关掉
        st.Params.ShowDozers = false;
        var res = st.Rebuild("2026-08", new List<SimBlockUnit> { Unit(dumpAz) });

        double? dump = null, pit = null;
        st.Tick(0.05);                      // 采出段：采场块在
        pit = PrincipalAzimuthDeg(sink.Points(SimBlockTransferStage.PitGroup));
        st.Tick(0.99);                      // 堆完：排土块在
        dump = PrincipalAzimuthDeg(sink.Points(SimBlockTransferStage.DumpGroup));
        return (dump, pit, res);
    }

    // ── B1 排土块按排土位置自己的走向摆 ──────────────────────────────────────
    [Fact]
    public void B1_排土块按排土位置自己的走向摆()
    {
        var (dump, _, res) = Run(DumpAz);
        Assert.Equal(0, res.DumpAxisDefault);
        Assert.NotNull(dump);
        Assert.True(AngleDiff(dump!.Value, DumpAz) < 2.0,
            $"排土块主轴 {dump:0.#}°，而排土位置的走向是 {DumpAz}° —— "
          + $"（与源采场单元的 {SrcAz}° 差 {AngleDiff(dump.Value, SrcAz):0.#}°）"
          + "沿用源块朝向的话，图上排土块与「排土条带」建出来的体整体拧着，而没有一个量看得出来。");
        Assert.True(AngleDiff(dump.Value, SrcAz) > 30.0, "排土块跟着源采场单元的走向走了。");
    }

    // ── B2 交叉钉：采场块仍按源走向 ─────────────────────────────────────────
    [Fact]
    public void B2_采场块仍按源单元走向_交叉钉()
    {
        var (_, pit, _) = Run(DumpAz);
        Assert.NotNull(pit);
        Assert.True(AngleDiff(pit!.Value, SrcAz) < 2.0,
            $"采场块主轴 {pit:0.#}°，应当是源单元走向 {SrcAz}° —— "
          + "两侧各读各的，别把排土那个角一起套到采场块上（那样 B1 也会绿）。");
    }

    // ── B3 没给排土走向：轴对齐 + 计数，且不许回落成源走向 ────────────────────
    [Fact]
    public void B3_没有排土走向时轴对齐并计数()
    {
        var (dump, _, res) = Run(null);
        Assert.Equal(1, res.DumpAxisDefault);
        Assert.NotNull(dump);
        Assert.True(AngleDiff(dump!.Value, 90.0) < 2.0,
            $"没给排土走向时应当轴对齐（长轴朝东，90°），实际 {dump:0.#}°。");
        Assert.True(AngleDiff(dump.Value, SrcAz) > 30.0,
            "读不到排土走向时**回落成了源单元的走向** —— 宁可轴对齐并明账计数，也不拿另一处的角冒充。");
        Assert.Contains(res.Notes, s => s.Contains("没读到自己的走向方位"));
    }

    // ── B4 F0 自检：换一个排土走向，主轴必须跟着动 ────────────────────────────
    [Fact]
    public void B4_换排土走向主轴必须跟着动_反例组()
    {
        var a = Run(10.0).Dump;
        var b = Run(100.0).Dump;
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.True(AngleDiff(a!.Value, 10.0) < 2.0 && AngleDiff(b!.Value, 100.0) < 2.0,
            $"喂 10° 得 {a:0.#}°、喂 100° 得 {b:0.#}° —— 主轴没跟着输入走，B1 那条就是恒真的。");
    }
}
