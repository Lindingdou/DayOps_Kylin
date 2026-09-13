// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/UnitAzimuthTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 走向方位角的判据。
///
/// <para>加这一列是为了让<b>盒子退路</b>朝向正确 —— 此前 2312 个盒子长轴一律朝东。
/// 两条硬要求：<b>A3 老台账仍然能读</b>（用户盘子里那份就是 31 列的老格式），
/// <b>A4 盒子顶点要真的转过去</b>（不能只是把数存进去、几何一动不动）。</para>
///
/// <para><b>哨兵是 null 不是 0</b>：0° 是合法方位（正南北走向）。
/// 用 0 当"没填"，老台账读进来会全部变成"走向正南北"，盒子一本正经地朝错方向，
/// 而每个数看上去都正常。A1 钉这一条。</para>
/// </summary>
public class UnitAzimuthTests
{
    // 造一条两点的轨：坡顶线沿 (dirX,dirY) 走向，坡底线整体沿推进方向偏 −eps
    private static (double[] crest, double[] toe) Rail(double dirX, double dirY, double lenM = 100, double eps = 0.5)
    {
        double L = Math.Sqrt(dirX * dirX + dirY * dirY);
        double ex = dirX / L, ey = dirY / L;
        double nx = -ey, ny = ex;                       // 推进方向 = 走向左垂线
        var crest = new double[6];
        var toe = new double[6];
        for (int k = 0; k < 2; k++)
        {
            double t = k * lenM;
            crest[k * 3] = ex * t + nx * eps; crest[k * 3 + 1] = ey * t + ny * eps; crest[k * 3 + 2] = 10;
            toe[k * 3] = ex * t; toe[k * 3 + 1] = ey * t; toe[k * 3 + 2] = 0;
        }
        return (crest, toe);
    }

    // ═══ A1：null 与 0 是两件事 ═══
    //
    // ★ 口径改过（2026-08-18）：走向<b>先从坡顶轨自身的切线</b>求，坡底轨只是退路。
    //   所以"两条轨 XY 重合"、"没有坡底轨"这两种**不再是算不出来** —— 坡顶轨自己就有方向，
    //   而走向本来就是台阶线自己的方向。判据跟着改口径，但要钉住的那条没变：
    //   **真算不出来的时候返回 false，绝不返回 0**（0° 是合法方位＝正南北）。
    [Fact]
    public void A1_算不出来返回false而不是0()
    {
        // 真正算不出来的：坡顶轨退化成一个点（所有折点重合），坡底轨也跟它重合
        var deg = new double[] { 0, 0, 5, 0, 0, 5 };
        var degToe = new double[] { 0, 0, 0, 0, 0, 0 };
        Assert.False(MiningUnitLedger.TryStrikeAzimuthDeg(deg, degToe, out _));
        Assert.False(MiningUnitLedger.TryStrikeAzimuthDeg(null, null, out _));
        Assert.False(MiningUnitLedger.TryStrikeAzimuthDeg(null, degToe, out _));   // 没有坡顶轨、坡底轨也退化
        Assert.False(MiningUnitLedger.TryStrikeAzimuthDeg(new double[] { 1, 2, 3 }, null, out _));  // 点数不够

        // 反过来：坡顶轨自己有方向时【算得出来】，哪怕没有坡底轨 —— 这是新口径的正面
        var c = new double[] { 0, 0, 5, 100, 0, 5 };
        Assert.True(MiningUnitLedger.TryStrikeAzimuthDeg(c, null, out double az));
        Assert.Equal(90, az, 3);                    // 朝东
    }

    // ═══ A2：方位角算得对（X=东 Y=北，自北顺时针，规约到 [0,180)）═══
    [Theory]
    [InlineData(1, 0, 90)]      // 走向朝东 ⇒ 方位 90°
    [InlineData(0, 1, 0)]       // 走向朝北 ⇒ 方位 0°
    [InlineData(1, 1, 45)]      // 东北
    [InlineData(-1, 1, 135)]    // 西北
    [InlineData(-1, 0, 90)]     // 朝西 == 朝东（盒子 180° 对称）
    [InlineData(0, -1, 0)]      // 朝南 == 朝北
    public void A2_方位角与走向一致(double dx, double dy, double want)
    {
        var (c, t) = Rail(dx, dy);
        Assert.True(MiningUnitLedger.TryStrikeAzimuthDeg(c, t, out double az));
        Assert.InRange(az, 0, 180);
        Assert.Equal(want, az, 3);
    }

    // ═══ A3：老 31 列台账仍然能读，方位角为 null 并退回轴对齐 ═══
    [Fact]
    public void A3_老格式台账仍然能读且方位角为null()
    {
        // 用户盘子里那份就是这个表头（没有「走向方位°」）
        const string oldCsv =
            "# 采掘单元台账 · 基表（全量）\n" +
            "UnitId,类型,采场,煤层/台阶,带号,幅号,幅数,中心X,中心Y,中心Z,最低Z,最高Z," +
            "走向长m,推进宽m,厚度m,煤量m3,煤量t,毛量m3,含煤m3,净岩量m3,库容m3," +
            "推进序,期次,状态,完成度,去向,运距km,备注\n" +
            "4-B3-P27,煤,采场1,4,3,27,37,621908.16,4381658.95,1255.25,1249.16,1261.73," +
            "98.01,40,11.763,46117.466,62258.579,,,,,0,,,,,,\n";

        Assert.True(MiningUnitLedger.TryRead(oldCsv, out var rows, out var issues), string.Join("\n", issues));
        Assert.Single(rows);
        Assert.Equal("4-B3-P27", rows[0].UnitId);
        Assert.Equal(LedgerKind.Coal, rows[0].Kind);
        Assert.Equal(98.01, rows[0].LengthM, 2);       // 老列照常读出来
        Assert.Null(rows[0].AzimuthDeg);               // ★ 新列缺失 ⇒ null，不是 0
    }

    // ═══ A4：盒子顶点要真的转过去（不是只把数存进去）═══
    [Fact]
    public void A4_有方位角时盒子真的转了()
    {
        const double cx = 1000, cy = 2000, len = 100, wid = 40, zLo = 0, zHi = 10;

        Assert.True(UnitPrismProbe.Box(cx, cy, len, wid, zLo, zHi, null, out var flat));
        Assert.True(UnitPrismProbe.Box(cx, cy, len, wid, zLo, zHi, 0, out var northSouth));

        static (double W, double H) Extent(double[] xyz)
        {
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            for (int i = 0; i + 2 < xyz.Length; i += 3)
            {
                x0 = Math.Min(x0, xyz[i]); x1 = Math.Max(x1, xyz[i]);
                y0 = Math.Min(y0, xyz[i + 1]); y1 = Math.Max(y1, xyz[i + 1]);
            }
            return (x1 - x0, y1 - y0);
        }

        var f = Extent(flat);
        var ns = Extent(northSouth);

        // 无方位角：长轴朝东 ⇒ X 跨度 = 走向长、Y 跨度 = 推进宽
        Assert.Equal(len, f.W, 3);
        Assert.Equal(wid, f.H, 3);
        // 方位 0°（正南北）：长轴朝北 ⇒ 两个跨度对调。只存数不转几何的话，这一条红。
        Assert.Equal(wid, ns.W, 3);
        Assert.Equal(len, ns.H, 3);
    }

    // ═══ A5：转过之后质心不动、体积不变（旋转是刚体变换）═══
    [Fact]
    public void A5_旋转不改变质心与体积()
    {
        const double cx = 621908, cy = 4381658, len = 98, wid = 40, zLo = 1249, zHi = 1261;

        foreach (double az in new double[] { 0, 30, 45, 90, 137.5 })
        {
            Assert.True(UnitPrismProbe.Box(cx, cy, len, wid, zLo, zHi, az, out var xyz));
            int n = xyz.Length / 3;
            double sx = 0, sy = 0;
            for (int i = 0; i < n; i++) { sx += xyz[i * 3]; sy += xyz[i * 3 + 1]; }
            Assert.Equal(cx, sx / n, 3);
            Assert.Equal(cy, sy / n, 3);
        }
    }

    // ═══ A6：生成侧真的把方位角填进去了 ═══
    [Fact]
    public void A6_FromStrips填了方位角()
    {
        var (c, t) = Rail(1, 1);          // 东北走向
        var strip = new MiningModelPlanner.Strip
        {
            SeamCode = "4", BandId = 3, PanelIndex = 27, PanelCount = 37,
            StrikeLenM = 100, AdvanceWidthM = 40, ThickM = 12,
            CrestXyz = c, ToeXyz = t, EstVolumeM3 = 1000,
        };
        var rows = MiningUnitLedger.FromStrips(new[] { strip }, isRock: false);
        Assert.Single(rows);
        Assert.NotNull(rows[0].AzimuthDeg);
        Assert.Equal(45, rows[0].AzimuthDeg!.Value, 3);
    }

    // ═══ A7：写出去再读回来，方位角不丢也不被改成 0 ═══
    [Fact]
    public void A7_写出再读回方位角往返一致()
    {
        var src = new List<MiningUnitLedger.Row>
        {
            new() { UnitId = "有方位", Kind = LedgerKind.Coal, LengthM = 98, WidthM = 40, ThickM = 12,
                    Cx = 1, Cy = 2, Cz = 3, AzimuthDeg = 137.25 },
            new() { UnitId = "无方位", Kind = LedgerKind.Rock, LengthM = 98, WidthM = 40, ThickM = 12,
                    Cx = 1, Cy = 2, Cz = 3, AzimuthDeg = null },
            new() { UnitId = "正南北", Kind = LedgerKind.Rock, LengthM = 98, WidthM = 40, ThickM = 12,
                    Cx = 1, Cy = 2, Cz = 3, AzimuthDeg = 0 },
        };

        string csv = MiningUnitLedger.ToCsv(src);
        Assert.True(MiningUnitLedger.TryRead(csv, out var back, out var issues), string.Join("\n", issues));

        var byId = back.ToDictionary(r => r.UnitId, StringComparer.Ordinal);
        Assert.Equal(137.25, byId["有方位"].AzimuthDeg!.Value, 2);
        Assert.Null(byId["无方位"].AzimuthDeg);        // null 不许往返成 0
        Assert.NotNull(byId["正南北"].AzimuthDeg);     // 0 不许往返成 null
        Assert.Equal(0, byId["正南北"].AzimuthDeg!.Value, 6);
    }
}

/// <summary>把 internal 的 UnitPrism.FromBox 露给判据用。</summary>
internal static class UnitPrismProbe
{
    public static bool Box(double cx, double cy, double len, double wid, double zLo, double zHi,
                           double? az, out double[] xyz)
        => UnitPrism.FromBox(cx, cy, len, wid, zLo, zHi, 0, 1, out xyz, out _, out _, out _, az);
}
