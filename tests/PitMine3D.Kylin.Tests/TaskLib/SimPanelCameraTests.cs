// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimPanelCameraTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  面板相机的手感判据
//
//  「手感不好」在代码里通常不是参数没调好，是**投影的反解算错了**。踩过两条：
//   ① 平移把屏幕纵向位移除以 cos(tilt)，而默认俯视 tilt=90° ⇒ cos=0 ⇒ 被夹到 0.15
//      ⇒ 纵向平移放大 6.7 倍。俯视是最常用的角度，所以一上手就飘。
//   ② 缩放锚在画面中心而不是鼠标 ⇒ 每缩一次都要重新平移把目标找回来。
//
//  两条都能证伪，所以都钉住。判据按**端点角度**取值（90° 俯视 / 0° 立面）——
//  中间角度只是"看着别扭"，端点才会暴露公式写反。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimPanelCameraTests
{
    /// <summary>
    /// 台架相机。<b>默认 Perspective=0（纯轴测）</b>——精确性判据只在平行投影下成立。
    ///
    /// <para>★ 这是一条真实的取舍，不是判据凑合：透视让投影**不再是线性**的，
    /// 于是「平移逐像素跟手」「缩放锚点分毫不动」这两条精确性质随深度失效，
    /// 只在视点中心附近成立。产品默认开了 0.35 的透视（要空间感），
    /// 所以下面 C5 单独用容差判「透视下仍然大致跟手」，而 C1/C2 判的是平行投影下的**精确**。</para>
    /// </summary>
    private static SimPanelCamera Cam(double tilt, double az = 30, double scale = 0.05, double persp = 0)
        => new() { Width = 800, Height = 600, TiltDeg = tilt, AzimuthDeg = az, Scale = scale,
                   Cx = 620000, Cy = 4380000, Cz = 1200, ZExaggeration = 4, Perspective = persp };

    /// <summary>内容在屏幕上的位置。</summary>
    private static (double X, double Y) P(SimPanelCamera c, double x, double y, double z)
    { c.Project(x, y, z, out double sx, out double sy, out _); return (sx, sy); }

    // ── C1 平移：内容必须**逐像素**跟着鼠标走，任何俯仰角下都是 ─────────────
    [Theory]
    [InlineData(90.0)]   // 俯视 —— 旧写法就是在这儿放大 6.7 倍
    [InlineData(55.0)]
    [InlineData(20.0)]
    [InlineData(2.0)]    // 近立面
    public void C1_平移让内容逐像素跟着鼠标(double tilt)
    {
        var c = Cam(tilt);
        double wx = 620500, wy = 4380400, wz = 1240;
        var before = P(c, wx, wy, wz);

        // 鼠标拖 (dx, dy)：调用方传的是 (-dx, dy)（见 SimPanelHost）
        double dx = 37, dy = -23;
        c.MoveByScreen(-dx, dy);
        var after = P(c, wx, wy, wz);

        Assert.Equal(dx, after.X - before.X, 6);
        Assert.Equal(dy, after.Y - before.Y, 6);
    }

    // ── C2 缩放：鼠标底下那一点的世界位置不许动 ─────────────────────────────
    [Theory]
    [InlineData(90.0, 1.18)]
    [InlineData(90.0, 1 / 1.18)]
    [InlineData(45.0, 1.18)]
    [InlineData(10.0, 1 / 1.18)]
    public void C2_缩放锚在鼠标底下那一点(double tilt, double factor)
    {
        var c = Cam(tilt);
        // 先找出屏幕点 (620, 180) 底下是哪个世界点：反过来用现有投影凑一个即可 ——
        // 取任意世界点，把它投出来当锚，判据要的是"锚点不动"这件事本身。
        double wx = 621200, wy = 4380900, wz = 1260;
        var anchor = P(c, wx, wy, wz);

        c.ZoomAt(anchor.X, anchor.Y, factor);
        var after = P(c, wx, wy, wz);

        Assert.Equal(anchor.X, after.X, 4);
        Assert.Equal(anchor.Y, after.Y, 4);
    }

    /// <summary>C2 的反例组：锚在画面中心时，**非中心**的点必然会移动 —— 否则 C2 恒真。</summary>
    [Fact]
    public void C2b_锚在中心时非中心点必然移动_反例组()
    {
        var c = Cam(55);
        double wx = 621200, wy = 4380900, wz = 1260;
        var before = P(c, wx, wy, wz);
        c.ZoomAt(c.Width * 0.5, c.Height * 0.5, 1.18);   // 锚在中心
        var after = P(c, wx, wy, wz);
        Assert.True(Math.Abs(after.X - before.X) + Math.Abs(after.Y - before.Y) > 1.0,
                    "锚在中心时非中心点竟然没动 —— 那说明缩放根本没生效，C2 是空过的。");
    }

    // ── C5 透视开着时：仍然大致跟手（容差判），且真的近大远小 ─────────────────
    [Fact]
    public void C5_透视下平移仍大致跟手且确实近大远小()
    {
        var c = Cam(55, persp: 0.35);

        // ① 近大远小：同样大小的两段，靠近观察者的那段投出来更长
        c.Project(620000, 4380000, 1200, out double ax, out double ay, out double dNear0);
        c.Project(620200, 4380000, 1200, out double bx, out double by, out _);
        double lenAtCenter = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));

        // 往观察者方向挪 2km 再量同样的 200m
        c.Project(620000, 4378000, 1200, out double cx2, out double cy2, out double dNear1);
        c.Project(620200, 4378000, 1200, out double dx2, out double dy2, out _);
        double lenNearer = Math.Sqrt((dx2 - cx2) * (dx2 - cx2) + (dy2 - cy2) * (dy2 - cy2));

        Assert.True(dNear1 > dNear0, "算例没造出深度差，判据会空过。");
        Assert.True(lenNearer > lenAtCenter * 1.02,
                    $"透视开着却没有近大远小（{lenAtCenter:0.##} → {lenNearer:0.##}）。");

        // ② 平移仍大致跟手：视点中心附近误差 < 3%
        double wx = 620100, wy = 4380100, wz = 1210;
        c.Project(wx, wy, wz, out double s0x, out double s0y, out _);
        c.MoveByScreen(-40, 25);
        c.Project(wx, wy, wz, out double s1x, out double s1y, out _);
        Assert.InRange(s1x - s0x, 40 * 0.97, 40 * 1.03);
        Assert.InRange(s1y - s0y, 25 * 0.97, 25 * 1.03);
    }

    // ── C3 俯视端点：平面完整铺开、Z 不出现（投影公式写反会在这儿露出来）──
    [Fact]
    public void C3_俯视时Z不参与投影()
    {
        var c = Cam(90);
        var a = P(c, 620500, 4380400, 1200);
        var b = P(c, 620500, 4380400, 1400);   // 只差 200m 高
        Assert.Equal(a.X, b.X, 6);
        Assert.Equal(a.Y, b.Y, 6);             // 俯视：高差不该改变屏幕位置
    }

    // ── C4 正立面端点：只剩高差，平面内的移动不该改变纵向位置 ────────────────
    [Fact]
    public void C4_正立面时平面移动不改变纵向位置()
    {
        var c = Cam(0);
        var a = P(c, 620500, 4380400, 1200);
        var b = P(c, 620500, 4381400, 1200);   // 只差 1000m 平面
        Assert.Equal(a.Y, b.Y, 6);
        var hi = P(c, 620500, 4380400, 1400);
        Assert.True(hi.Y < a.Y - 1, "正立面下抬高 200m 屏幕位置竟然没上移。");
    }
}
