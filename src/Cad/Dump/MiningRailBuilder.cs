using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 台阶壳子的【竖直断面轨】生成器 —— 原 BlockModelLib.Domain.SeamOutcropBandExtractor 里 BuildMiningRails /
/// BuildMiningRailSegments 及其三个几何助手的逐行搬运（本文件只收这一段，露头带提取本身不在此）。
/// 排土条带与采矿模型都靠它把「坡顶线 + 坡底线」配成能喂给 DumpStripPlanner 的 crest/toe 轨对。
/// </summary>
public static class MiningRailBuilder
{
    /// <summary>采样面高程回调（同原 SeamOutcropBandExtractor.SampleZ）。</summary>
    public delegate bool SampleZ(double x, double y, out double z);

    /// <summary>原 Band 里轨生成用到的三个字段。</summary>
    public sealed class Band
    {
        public double[] ParentCrestXyz = Array.Empty<double>();
        public double[] CrestXyz = Array.Empty<double>();
        public double[] ToeXyz = Array.Empty<double>();
        public int CrestPointCount => CrestXyz.Length / 3;
        public int ToePointCount => ToeXyz.Length / 3;
    }

    /// <summary>
    /// 由一条露头带产出【建体用】的两条轨：前脸 = 坡顶线处的竖直煤层断面。
    ///
    /// 【为什么不能拿露头带当前脸】
    /// CarveStrip 的模型是「前脸 + 沿推进方向平移 W」，隐含前提是【前脸近垂直、推进近垂直于前脸】。
    /// 岩台阶符合（坡面 65~75°，水平推进正好横切）。但露头带是躺在 16~36° 缓坡上的一条带，
    /// 推进方向（水平、由坡底指向坡顶）几乎【躺在前脸平面内】—— 平移 W 不是横切扫体，
    /// 而是把带沿它自己的方向拉长，出来必然是薄板。
    ///
    /// 更要命的是：露头带的定义是"现状面落在顶底板【之间】"，即带内每一点现状面都切在煤层内部，
    /// 剩下的煤只有 现状面→底板 那一薄层 —— 从坡顶线处的满煤厚渐变到坡底线处的 0。
    /// 把整条带当前脸，体在带内那 20~40m 就是个楔子。现场看到的楔形薄刃正是这段的真实几何，
    /// 不是 bug，是把不该算的部分算进来了。
    ///
    /// 【正解】满煤厚在【坡顶线】那侧（现状面正好切在顶板，底下是完整煤层）。
    /// 所以前脸取坡顶线处的竖直断面（顶板 → 底板），再往高墙里推 W：
    ///   toe   = 坡顶线 XY，Z 取底板
    ///   crest = 坡顶线 XY 沿推进方向偏 eps，Z 取顶板
    /// eps 是给内核判推进方向用的（它按 toe→crest 求 d）；前脸因此近乎竖直，
    /// 扫出来的体厚度【从头到尾都是煤厚】，没有楔形段。
    /// </summary>
    public static bool BuildMiningRails(Band band, SampleZ roofZ, SampleZ floorZ,
                                        out double[] crestXyz, out double[] toeXyz,
                                        double epsM = 0.5, double resampleStepM = 10.0)
    {
        var segs = BuildMiningRailSegments(band, roofZ, floorZ, epsM, resampleStepM);
        if (segs.Count == 0) { crestXyz = Array.Empty<double>(); toeXyz = Array.Empty<double>(); return false; }
        var best = segs.OrderByDescending(t => t.Crest.Length).First();   // 兼容旧调用：取最长一段
        crestXyz = best.Crest; toeXyz = best.Toe;
        return true;
    }

    /// <summary>
    /// 同上，但【在尖灭处断开】而不是架桥，返回若干段。
    ///
    /// 早先遇到煤厚≤0 或采不到顶/底板就 continue，两侧的点被一条直线连起来【跨过】尖灭区 ——
    /// 那一段体是凭空造的，且没记账。实测全场 1 处 18.2m(占走向总长 0.1%，9 号煤 带5 幅12)，
    /// 同时也是相邻幅接缝那处 19.09m 错位的成因 —— 两条红用例指向同一个缺陷。
    /// 真断的位置就该自然断开：尖灭点收尾，之后另起一段。
    /// </summary>
    public static List<(double[] Crest, double[] Toe)> BuildMiningRailSegments(
        Band band, SampleZ roofZ, SampleZ floorZ, double epsM = 0.5, double resampleStepM = 10.0,
        double advanceWindowM = 40.0)
    {
        var outSegs = new List<(double[], double[])>();
        if (band.CrestPointCount < 2 || band.ToePointCount < 2) return outSegs;

        // 【补密】现状面在露头处一个三角管 11m 走向，等值线一幅只落 3~7 个点，
        // 顶底盖 loft 出来是折面、贴不住顶底板。按固定步长沿坡顶线重采样，
        // 每个采样点各自去采顶/底板 —— 步长越小越贴合，代价是点数线性上涨。
        double[] cxy = resampleStepM > 0.1 ? ResampleXy(band.CrestXyz, resampleStepM) : band.CrestXyz;
        int n = cxy.Length / 3;
        if (n < 2) return outSegs;

        // 父带（分幅前的整条坡顶线）；未分幅时就是自己
        double[] par = band.ParentCrestXyz.Length >= 6 ? band.ParentCrestXyz : band.CrestXyz;

        var crest = new List<double>(n * 3);
        var toe = new List<double>(n * 3);

        for (int i = 0; i < n; i++)
        {
            double x = cxy[i * 3], y = cxy[i * 3 + 1];

            // 走向切线在【宽窗口】上取，不是相邻两点的连线。
            // 窗口太窄时凹角处相邻点的推进方向发散剧烈，曲率半径小于 W 后脸就翻过来(蝴蝶结)，
            // 体自交 —— 实测 1296 个后脸段里 65 个折回，最差 cos=-0.997。
            // 窗口取 ~W/步长 个点：偏置距离多大，切线就在多大的尺度上看，急弯被摊平。
            int half = Math.Max(1, (int)Math.Round(advanceWindowM / Math.Max(1.0, resampleStepM)));
            // 【端点用最近的完整窗口】首末点的窗口会被截断成单边，方向估偏 ——
            // 实测端点方向偏离本条带主方向 中位 12.5°、90 分位 76°、最大 163°(几乎完全反向)，
            // 而中点只有 0.8°。180 条里 19 条端点超 60°，现场看就是横插出去的楔子，
            // 和邻近的体撞在一起。把取样中心夹进 [half, n-1-half]，端点沿用最近完整窗口的方向。
            // 切线取自【整条带】而不是本幅：幅界两侧必须算出同一个方向，
            // 否则后脸在幅界处劈叉 —— 实测 87 对平面相交里 54 对是同带相邻幅。
            int pn = par.Length / 3;
            int pi = NearestIndexXy(par, x, y);
            int pc = Math.Min(Math.Max(pi, half), Math.Max(half, pn - 1 - half));
            int ip = Math.Min(pc + half, pn - 1), im = Math.Max(pc - half, 0);
            double sx = par[ip * 3] - par[im * 3];
            double sy = par[ip * 3 + 1] - par[im * 3 + 1];
            double dx = -sy, dy = sx;
            double L = Math.Sqrt(dx * dx + dy * dy);
            if (L < 1e-9) { dx = 1; dy = 0; } else { dx /= L; dy /= L; }

            // 用坡底线上最近点定符号：d 必须背离坡底
            double tx = band.ToeXyz[0], ty = band.ToeXyz[1], bestD = double.MaxValue;
            for (int j = 0; j < band.ToePointCount; j++)
            {
                double ex = band.ToeXyz[j * 3] - x, ey = band.ToeXyz[j * 3 + 1] - y;
                double d2 = ex * ex + ey * ey;
                if (d2 < bestD) { bestD = d2; tx = band.ToeXyz[j * 3]; ty = band.ToeXyz[j * 3 + 1]; }
            }
            if (dx * (x - tx) + dy * (y - ty) < 0) { dx = -dx; dy = -dy; }

            double zr = 0, zf = 0;
            bool ok = roofZ(x, y, out zr) & floorZ(x, y, out zf);
            if (ok) ok = zr - zf > 1e-6;
            if (!ok)
            {
                // 尖灭/无数据：把已攒的收成一段，之后另起 —— 不跨过去连直线
                if (crest.Count >= 6) outSegs.Add((crest.ToArray(), toe.ToArray()));
                crest.Clear(); toe.Clear();
                continue;
            }

            toe.Add(x); toe.Add(y); toe.Add(zf);                              // 断面下沿 = 底板
            crest.Add(x + dx * epsM); crest.Add(y + dy * epsM); crest.Add(zr); // 断面上沿 = 顶板，偏 eps 定方向
        }

        if (crest.Count >= 6 && toe.Count >= 6) outSegs.Add((crest.ToArray(), toe.ToArray()));
        return outSegs;
    }

    /// <summary>沿折线按固定步长重采样（XY 平面弧长；Z 线性插值，随后会被顶/底板覆盖）。</summary>
    private static double[] ResampleXy(double[] a, double step)
    {
        int n = a.Length / 3;
        if (n < 2) return a;

        var cum = new double[n];
        for (int i = 1; i < n; i++)
        {
            double dx = a[i * 3] - a[(i - 1) * 3], dy = a[i * 3 + 1] - a[(i - 1) * 3 + 1];
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = cum[n - 1];
        if (total <= step) return a;

        int m = Math.Max(1, (int)Math.Ceiling(total / step));
        var outA = new double[(m + 1) * 3];
        int k = 1;
        for (int j = 0; j <= m; j++)
        {
            double d = total * j / m;
            while (k < n - 1 && cum[k] < d) k++;
            double seg = cum[k] - cum[k - 1];
            double u = seg > 1e-12 ? (d - cum[k - 1]) / seg : 0;
            for (int c = 0; c < 3; c++)
                outA[j * 3 + c] = a[(k - 1) * 3 + c] + u * (a[k * 3 + c] - a[(k - 1) * 3 + c]);
        }
        return outA;
    }

    private static bool InRingXy(double[] ring, double x, double y)
    {
        bool inside = false;
        int n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[i * 2], yi = ring[i * 2 + 1];
            double xj = ring[j * 2], yj = ring[j * 2 + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>扁平 xyz 里离 (x,y) 最近的点序号。</summary>
    private static int NearestIndexXy(double[] a, double x, double y)
    {
        int best = 0; double bd = double.MaxValue;
        for (int i = 0; i + 2 < a.Length; i += 3)
        {
            double dx = a[i] - x, dy = a[i + 1] - y, d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = i / 3; }
        }
        return best;
    }
}
