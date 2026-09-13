// 忠实移植自原 PitMine3D Modules/TaskLib/Features/DynamicSimWindow.xaml.cs 的 DeCollideLabels（逐行对应）。
// 原版把它挂在 WPF 窗口类上（StageSimPlayer 以 Features.DynamicSimWindow.DeCollideLabels 调用）；
// Kylin 侧窗口是 Avalonia，纯函数独立成文件，调用方改指到这里。
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Simulation;

public static class LabelDeCollide
{
    /// <summary>
    /// 铭牌避让：把挤在一起的铭牌沿 Y 依次往下让，避免叠字。
    ///
    /// <para><b>为什么需要</b>：实测中班/夜班整班都是检修，七台设备的铭牌落在采场同一小片上，
    /// 图上是一坨读不出来的字（截图上 WK-12/WK-10/WK-16/WK-14 完全叠在一起）。
    /// 三维那一层要答的就是「谁在哪个面上」，字叠了就等于没答。</para>
    ///
    /// <para><b>口径</b>：只沿 Y 往下推、只推被挡住的那个，顺序不变 —— 铭牌与它的标记之间
    /// 因此会有几十米的偏移，但标记本身没动，"哪个牌子对哪个点"仍然按上下顺序读得出来。
    /// 默认俯视下世界 Y 就是屏幕上下，所以这个推法在默认视角上直接对应"往下让一行"。</para>
    /// </summary>
    public static void DeCollideLabels(List<double> xyz, List<float> heights)
    {
        int n = xyz.Count / 3;
        if (n < 2) return;

        // 世界系里一行的高度 / 一个牌子的宽度（按 13px 字高、约 10 个字估）
        var order = Enumerable.Range(0, n).OrderByDescending(i => xyz[i * 3 + 1]).ToList();
        var placed = new List<(double X, double Y, double Row, double Wide)>();

        foreach (int i in order)
        {
            double h = i < heights.Count ? heights[i] : 12.0;
            double row = h * 1.45, wide = h * 7.0;
            double x = xyz[i * 3], y = xyz[i * 3 + 1];

            bool moved = true; int guard = 0;
            while (moved && guard++ < 64)
            {
                moved = false;
                foreach (var q in placed)
                    if (Math.Abs(x - q.X) < Math.Max(wide, q.Wide) && Math.Abs(y - q.Y) < Math.Max(row, q.Row))
                    { y = q.Y - Math.Max(row, q.Row); moved = true; break; }
            }
            xyz[i * 3 + 1] = y;
            placed.Add((x, y, row, wide));
        }
    }
}
