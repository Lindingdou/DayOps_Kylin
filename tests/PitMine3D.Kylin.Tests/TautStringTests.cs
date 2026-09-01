using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>拉紧绳(月度剥离均衡)回归 —— 走廊内单调、增量最平、触边转折、倒挂不可行。</summary>
public class TautStringTests
{
    [Fact]
    public void Linear_must_strip_gives_constant_rate_no_interior_pivot()
    {
        // lo 线性 +10/期, hi 宽松, yEnd=lo[T] → 绳=lo, 增量恒 10, 无内部触顶
        var lo = new double[] { 0, 10, 20, 30 };
        var hi = new double[] { 0, 50, 50, 50 };
        var c = TautString.Solve(lo, hi, 30, out var err, null);
        Assert.NotNull(c);
        Assert.Equal("", err);
        var inc = TautString.ToIncrements(c!);
        Assert.All(inc, d => Assert.Equal(10.0, d, 6));
        Assert.Equal(0.0, TautString.Cv(inc), 6);      // 完全平
    }

    [Fact]
    public void Steep_mid_constraint_forces_pivot_on_lower_envelope()
    {
        // lo=[0,5,25,30]: 第2期必须剥到 25(陡) → 直线 0→30 会在第2期跌破 lo → 绳在第2期触底折
        var lo = new double[] { 0, 5, 25, 30 };
        var hi = new double[] { 0, 50, 50, 50 };
        var pivots = new List<TautString.Pivot>();
        var c = TautString.Solve(lo, hi, 30, out var err, pivots);
        Assert.NotNull(c);
        // 0 → 25 于第2期(斜率12.5), 再 25 → 30(斜率5)
        Assert.Equal(new double[] { 0, 12.5, 25, 30 }, c!, new DoubleArrayComparer(1e-6));
        Assert.Contains(pivots, p => p.Period == 2 && p.OnLower);   // 露煤紧迫月
    }

    [Fact]
    public void Result_stays_within_corridor_and_is_monotone()
    {
        var lo = new double[] { 0, 5, 25, 30, 40 };
        var hi = new double[] { 0, 20, 35, 45, 60 };
        var c = TautString.Solve(lo, hi, 40, out _, null);
        Assert.NotNull(c);
        for (int t = 0; t < c!.Length; t++)
        {
            Assert.True(c[t] >= lo[t] - 1e-6, $"第{t}期跌破下界");
            Assert.True(c[t] <= hi[t] + 1e-6, $"第{t}期冲破上界");
            if (t > 0) Assert.True(c[t] >= c[t - 1] - 1e-6, "非单调");
        }
    }

    [Fact]
    public void Taut_string_minimizes_increment_variance_vs_following_lo()
    {
        // 拉绳解的增量方差 ≤ 直接照 lo 走的增量方差(lo 忽快忽慢, 绳抹平)
        var lo = new double[] { 0, 5, 25, 30 };
        var hi = new double[] { 0, 50, 50, 50 };
        var c = TautString.Solve(lo, hi, 30, out _, null);
        double cvTaut = TautString.Cv(TautString.ToIncrements(c!));
        double cvLo = TautString.Cv(TautString.ToIncrements(lo));
        Assert.True(cvTaut < cvLo, $"拉绳 CV {cvTaut} 应 < 照 lo 走 CV {cvLo}");
    }

    [Fact]
    public void Capacity_ceiling_holds_rope_down_carrying_advance_reserve()
    {
        // hi 压低第1期(能力吃紧) → 绳触顶, 期末实际累计高于 yEnd 差额=超前剥离储备
        var lo = new double[] { 0, 0, 0, 30 };      // 前两期不逼剥, 末期要 30
        var hi = new double[] { 0, 8, 16, 40 };     // 每期能力仅 +8
        var pivots = new List<TautString.Pivot>();
        var c = TautString.Solve(lo, hi, 30, out _, pivots);
        Assert.NotNull(c);
        Assert.True(c![1] <= hi[1] + 1e-6);          // 不超能力
        Assert.Contains(pivots, p => !p.OnLower);    // 有触顶(能力吃紧)月
    }

    [Fact]
    public void Inverted_corridor_is_infeasible()
    {
        // 第2期 lo > hi 倒挂 → 无可行计划
        var lo = new double[] { 0, 10, 40, 50 };
        var hi = new double[] { 0, 20, 30, 60 };     // 第2期 40 > 30
        var c = TautString.Solve(lo, hi, 50, out var err, null);
        Assert.Null(c);
        Assert.Contains("倒挂", err);
    }

    sealed class DoubleArrayComparer : IEqualityComparer<double[]>
    {
        readonly double _tol;
        public DoubleArrayComparer(double tol) => _tol = tol;
        public bool Equals(double[]? a, double[]? b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (System.Math.Abs(a[i] - b[i]) > _tol) return false;
            return true;
        }
        public int GetHashCode(double[] a) => a.Length;
    }
}
