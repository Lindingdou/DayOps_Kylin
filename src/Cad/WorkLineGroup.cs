using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「组合工作线」的纯逻辑（原 <c>AcDbWorkLineGroup</c> / <c>CreateWorkLineGroupFromSelection</c>）：
/// 把 ≥2 条工作线按<b>最近端点</b>串成一条连续推进 front。成员保留自己（不合并几何），
/// 端点不重合的接缝架「软连接段」（虚线 + 箭头，不是实几何）；端点本就重合的接缝算硬接缝。
/// </summary>
public static class WorkLineGroup
{
    /// <summary>链上的一个成员：原序号 + 是否需要反向（使其起点接上前一段的终点）。</summary>
    public readonly record struct Link(int Index, bool Reversed);

    /// <summary>成员 i 的终点 → 成员 i+1 的起点之间的软连接（端点已按链向取好）。</summary>
    public readonly record struct SoftLink(int FromIndex, int ToIndex, (double x, double y) From, (double x, double y) To)
    {
        public double LengthM => Math.Sqrt((To.x - From.x) * (To.x - From.x) + (To.y - From.y) * (To.y - From.y));
    }

    public sealed class ChainResult
    {
        public List<Link> Order { get; } = new();
        public List<SoftLink> SoftLinks { get; } = new();
        public int HardSeams { get; set; }
        public double TotalGapM => SoftLinks.Sum(s => s.LengthM);
    }

    /// <summary>
    /// 按最近端点贪心串链：从"最孤立"的一端（离其它端点最远的端点所在成员）起，每步接距当前链尾最近的未用端点。
    /// 缺口 ≤ <paramref name="tol"/> 记硬接缝，否则出软连接段。
    /// </summary>
    public static ChainResult Chain(IReadOnlyList<((double x, double y) a, (double x, double y) b)> members, double tol)
    {
        var res = new ChainResult();
        int n = members.Count;
        if (n == 0) return res;
        if (n == 1) { res.Order.Add(new Link(0, false)); return res; }
        double tol2 = tol * tol;

        // 起点：全部端点里离其余端点最远的那个 —— 它多半是 front 的一端
        int startIdx = 0; bool startRev = false; double bestIso = -1;
        for (int i = 0; i < n; i++)
            foreach (bool useB in new[] { false, true })
            {
                var e = useB ? members[i].b : members[i].a;
                double nearest = double.PositiveInfinity;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    nearest = Math.Min(nearest, Math.Min(D2(e, members[j].a), D2(e, members[j].b)));
                }
                if (nearest > bestIso) { bestIso = nearest; startIdx = i; startRev = useB; }   // 起始端在"外侧" ⇒ 链从另一端接出去
            }

        var used = new bool[n];
        used[startIdx] = true;
        res.Order.Add(new Link(startIdx, startRev));
        var tail = startRev ? members[startIdx].a : members[startIdx].b;
        int prev = startIdx;
        for (int step = 1; step < n; step++)
        {
            int bi = -1; bool brev = false; double bd = double.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (used[j]) continue;
                double da = D2(tail, members[j].a), db = D2(tail, members[j].b);
                if (da < bd) { bd = da; bi = j; brev = false; }
                if (db < bd) { bd = db; bi = j; brev = true; }
            }
            used[bi] = true;
            var head = brev ? members[bi].b : members[bi].a;
            if (bd <= tol2) res.HardSeams++;
            else res.SoftLinks.Add(new SoftLink(prev, bi, tail, head));
            res.Order.Add(new Link(bi, brev));
            tail = brev ? members[bi].a : members[bi].b;
            prev = bi;
        }
        return res;
    }

    private static double D2((double x, double y) p, (double x, double y) q) => (p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y);
}

/// <summary>
/// 「连接台阶线」的纯逻辑（原内核 <c>JoinBenchLines</c> + <c>JoinBenchLineDialog</c>）：把被切碎的台阶线按首尾相接关系串成整条，
/// 保台阶语义 —— 新线继承原线的图层/颜色/标高（每条链取其首段），可选只在同图层内连接、串完首尾重合的收成闭合环。
/// </summary>
public static class BenchLineJoin
{
    public sealed class Input
    {
        public int Id;                                   // 调用方的实体序号
        public string Layer = "";
        public double Z;
        public IReadOnlyList<(double x, double y)> Points = Array.Empty<(double x, double y)>();
        public bool Closed;
    }

    public sealed class Chain
    {
        public List<int> MemberIds { get; } = new();     // 参与的实体序号（按链序）
        public List<(double x, double y)> Points { get; } = new();
        public string Layer = "";
        public double Z;
        public bool AutoClosed;
    }

    public sealed class Report
    {
        public List<Chain> Chains { get; } = new();      // 只含 ≥2 段拼成的链（单段原样不动）
        public int CandidateOpen, SkippedClosed, ConsumedCount, AutoClosedCount;
        public bool NothingJoined => Chains.Count == 0;
    }

    public static Report Join(IReadOnlyList<Input> lines, double tol, bool autoClose, bool sameLayerOnly)
    {
        var rep = new Report();
        var open = lines.Where(l => !l.Closed && l.Points.Count >= 2).ToList();
        rep.SkippedClosed = lines.Count(l => l.Closed);
        rep.CandidateOpen = open.Count;
        var groups = sameLayerOnly ? open.GroupBy(l => l.Layer).Select(g => g.ToList()).ToList() : new List<List<Input>> { open };
        double tol2 = tol * tol;
        foreach (var g in groups)
        {
            var used = new bool[g.Count];
            for (int i = 0; i < g.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var chain = new Chain { Layer = g[i].Layer, Z = g[i].Z };
                chain.Points.AddRange(g[i].Points); chain.MemberIds.Add(g[i].Id);
                bool ext = true;
                while (ext)
                {
                    ext = false;
                    for (int j = 0; j < g.Count; j++)
                    {
                        if (used[j]) continue;
                        var pj = g[j].Points;
                        var cs = chain.Points[0]; var ce = chain.Points[^1];
                        if (D2(ce, pj[0]) <= tol2) { for (int k = 1; k < pj.Count; k++) chain.Points.Add(pj[k]); }
                        else if (D2(ce, pj[^1]) <= tol2) { for (int k = pj.Count - 2; k >= 0; k--) chain.Points.Add(pj[k]); }
                        else if (D2(cs, pj[^1]) <= tol2) { for (int k = pj.Count - 2; k >= 0; k--) chain.Points.Insert(0, pj[k]); }
                        else if (D2(cs, pj[0]) <= tol2) { for (int k = 1; k < pj.Count; k++) chain.Points.Insert(0, pj[k]); }
                        else continue;
                        used[j] = true; ext = true; chain.MemberIds.Add(g[j].Id);
                    }
                }
                if (chain.MemberIds.Count < 2) continue;
                if (autoClose && chain.Points.Count >= 4 && D2(chain.Points[0], chain.Points[^1]) <= tol2)
                { chain.Points.RemoveAt(chain.Points.Count - 1); chain.AutoClosed = true; rep.AutoClosedCount++; }
                rep.ConsumedCount += chain.MemberIds.Count;
                rep.Chains.Add(chain);
            }
        }
        return rep;
    }

    private static double D2((double x, double y) p, (double x, double y) q) => (p.x - q.x) * (p.x - q.x) + (p.y - q.y) * (p.y - q.y);
}
