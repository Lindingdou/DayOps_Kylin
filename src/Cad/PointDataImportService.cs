using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点数据导入 —— 解析 CSV / TXT / XYZ / PTS 等文本坐标文件（测量点、孔口、采样点等）。
/// 宽松约定：逐行按 逗号/空白/分号/制表符 切分，取每行前 2~3 个可解析为数值的字段为 X,Y[,Z]；
/// 表头、注释(# ; //)、字段不足的行自动跳过。与具体列格式无关，能吃多数导出文本。
/// 纯逻辑、可单测。产出可交给绘制场景变为可编辑的点实体。
/// </summary>
public static class PointDataImportService
{
    private static readonly char[] Seps = { ',', ' ', '\t', ';' };

    public sealed class Result
    {
        public bool Success => Error == null;
        public List<(double x, double y, double z)> Points { get; } = new();
        public int SkippedLines { get; set; }
        public string? Error { get; set; }
        /// <summary>[minX,minY,maxX,maxY]（无点时全 0）。</summary>
        public double[] Bounds { get; set; } = { 0, 0, 0, 0 };
    }

    public static Result Load(string filePath)
    {
        try { return Parse(File.ReadAllText(filePath)); }
        catch (Exception ex) { return new Result { Error = $"读取失败：{ex.Message}" }; }
    }

    /// <summary>解析文本内容为点集（可单测，不碰文件系统）。</summary>
    public static Result Parse(string text)
    {
        var r = new Result();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("//")) { r.SkippedLines++; continue; }

            var tokens = line.Split(Seps, StringSplitOptions.RemoveEmptyEntries);
            var nums = new List<double>(4);
            foreach (var t in tokens)
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) nums.Add(v);

            if (nums.Count < 2) { r.SkippedLines++; continue; }   // 表头/说明行等

            double x = nums[0], y = nums[1], z = nums.Count >= 3 ? nums[2] : 0;
            r.Points.Add((x, y, z));
            if (x < minX) minX = x; if (y < minY) minY = y;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y;
        }

        if (r.Points.Count == 0) { r.Error = "未解析到有效坐标点（每行需至少 2 个数值）"; return r; }
        r.Bounds = new[] { minX, minY, maxX, maxY };
        return r;
    }
}
