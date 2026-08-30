using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 多段线合并（组合工作线）—— 端点在容差内相接的多段线首尾连成一条（贪心，两端延伸）。
/// 纯逻辑、可单测。返回合并后的多段线点列表（不相接的各自成链）。
/// </summary>
public static class PolylineJoin
{
    public static List<List<(double x, double y)>> Join(
        IReadOnlyList<IReadOnlyList<(double x, double y)>> inputs, double tol)
    {
        double tol2 = tol * tol;
        bool Near((double x, double y) a, (double x, double y) b)
            => (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) <= tol2;

        var used = new bool[inputs.Count];
        var result = new List<List<(double x, double y)>>();
        for (int i = 0; i < inputs.Count; i++)
        {
            if (used[i] || inputs[i].Count == 0) continue;
            used[i] = true;
            var chain = new List<(double x, double y)>(inputs[i]);
            bool extended = true;
            while (extended)
            {
                extended = false;
                for (int j = 0; j < inputs.Count; j++)
                {
                    if (used[j] || inputs[j].Count < 2) continue;
                    var pj = inputs[j];
                    var cs = chain[0]; var ce = chain[^1];
                    var js = pj[0]; var je = pj[^1];
                    if (Near(ce, js)) { for (int k = 1; k < pj.Count; k++) chain.Add(pj[k]); }
                    else if (Near(ce, je)) { for (int k = pj.Count - 2; k >= 0; k--) chain.Add(pj[k]); }
                    else if (Near(cs, je)) { for (int k = pj.Count - 2; k >= 0; k--) chain.Insert(0, pj[k]); }
                    else if (Near(cs, js)) { for (int k = 1; k < pj.Count; k++) chain.Insert(0, pj[k]); }
                    else continue;
                    used[j] = true; extended = true;
                }
            }
            result.Add(chain);
        }
        return result;
    }
}
