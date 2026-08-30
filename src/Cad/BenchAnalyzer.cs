using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>台阶参数分析的一行（坡面 或 平盘）。</summary>
public sealed class BenchRow
{
    public int Index;
    public string Kind = "";        // 坡面 / 平盘
    public double Height;           // 坡面:台阶高
    public double FaceAngleDeg;     // 坡面:坡面角
    public double Width;            // 平盘宽 / 坡面水平投影
    public double TopZ, ToeZ, StartDist, EndDist, StartZ, EndZ;
}

/// <summary>台阶参数分析结果。</summary>
public sealed class ProcessParamResult
{
    public List<BenchRow> Rows = new();
    public double OverallSlopeDeg;  // 整体帮坡角
    public double TotalRun, TotalHeight;
    public int FaceCount, BermCount;
}

/// <summary>
/// 台阶参数分析 —— 移植自原 `PointCloudLib.BenchAnalyzer.Analyze` 纯算：沿剖面(里程,高程)按坡度
/// 分平盘/坡面段(行程编码合并连续同类)，出台阶高/坡面角/平盘宽/整体帮坡角。原吃 ProfileResult, 此按数组。
/// </summary>
public static class BenchAnalyzer
{
    public static ProcessParamResult Analyze(IReadOnlyList<double> dists, IReadOnlyList<double> zs, double flatDeg = 15.0)
    {
        var res = new ProcessParamResult();
        if (dists == null || zs == null || dists.Count < 2 || zs.Count != dists.Count) return res;

        var d = new List<double>(); var z = new List<double>();
        for (int i = 0; i < dists.Count; i++)
            if (!double.IsNaN(zs[i])) { d.Add(dists[i]); z.Add(zs[i]); }
        int n = d.Count;
        if (n < 2) return res;

        double flatT = Math.Tan(flatDeg * Math.PI / 180.0);

        int SegClass(int k)
        {
            double dd = d[k + 1] - d[k];
            if (dd <= 1e-9) return -1;
            double slope = Math.Abs(z[k + 1] - z[k]) / dd;
            return slope < flatT ? 0 : 1;    // 0=平盘 / 1=坡面
        }

        var runs = new List<(int cls, int i0, int i1)>();
        int curCls = SegClass(0), runStart = 0;
        for (int k = 1; k < n - 1; k++)
        {
            int c = SegClass(k);
            if (c != curCls) { runs.Add((curCls, runStart, k)); curCls = c; runStart = k; }
        }
        runs.Add((curCls, runStart, n - 1));

        int idx = 1;
        foreach (var r in runs)
        {
            if (r.cls < 0) continue;
            double z0 = z[r.i0], z1 = z[r.i1], d0 = d[r.i0], d1 = d[r.i1];
            double h = Math.Abs(z1 - z0), w = Math.Abs(d1 - d0);
            if (r.cls == 1)
            {
                double ang = w > 1e-6 ? Math.Atan(h / w) * 180.0 / Math.PI : 90.0;
                res.Rows.Add(new BenchRow { Index = idx++, Kind = "坡面", Height = h, FaceAngleDeg = ang, Width = w, TopZ = Math.Max(z0, z1), ToeZ = Math.Min(z0, z1), StartDist = d0, EndDist = d1, StartZ = z0, EndZ = z1 });
                res.FaceCount++;
            }
            else
            {
                res.Rows.Add(new BenchRow { Index = idx++, Kind = "平盘", Width = w, TopZ = z0, ToeZ = z1, StartDist = d0, EndDist = d1, StartZ = z0, EndZ = z1 });
                res.BermCount++;
            }
        }

        res.TotalRun = d[n - 1] - d[0];
        res.TotalHeight = Math.Abs(z[n - 1] - z[0]);
        res.OverallSlopeDeg = res.TotalRun > 1e-6 ? Math.Atan(res.TotalHeight / res.TotalRun) * 180.0 / Math.PI : 90.0;
        return res;
    }
}
