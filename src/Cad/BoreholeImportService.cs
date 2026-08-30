using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 钻孔数据导入 —— 解析 CSV 钻孔表（GeoDataBase 模块托管切片）。
/// 宽松单表格式：孔号, X, Y, 孔口高程, 自(深), 至(深), 岩性 —— 每行一个分层，按孔号归组。
/// 表头(第2列非数值)、注释(#/;)、字段不足行自动跳过。纯逻辑、可单测。
/// 产出 <see cref="Borehole"/> 供柱状图渲染（矩形/线几何，按岩性配色）。
/// </summary>
public static class BoreholeImportService
{
    private static readonly char[] Seps = { ',', '\t', ';' };

    public sealed class Interval
    {
        public double From;      // 起始深度
        public double To;        // 终止深度
        public string Rock = ""; // 岩性
    }

    public sealed class Borehole
    {
        public string Name = "";
        public double X, Y, Z;                       // 孔口坐标 + 高程
        public List<Interval> Intervals { get; } = new();
        public double TotalDepth => Intervals.Count > 0 ? Max() : 0;
        private double Max() { double m = 0; foreach (var i in Intervals) if (i.To > m) m = i.To; return m; }
    }

    public sealed class Result
    {
        public bool Success => Error == null;
        public string? Error { get; set; }
        public List<Borehole> Boreholes { get; } = new();
        public int SkippedLines { get; set; }
        public double[] Bounds { get; set; } = { 0, 0, 0, 0 };   // 孔口 XY 包围盒
    }

    public static Result Load(string filePath)
    {
        try { return Parse(File.ReadAllText(filePath)); }
        catch (Exception ex) { return new Result { Error = $"读取失败：{ex.Message}" }; }
    }

    public static Result Parse(string text)
    {
        var r = new Result();
        var byName = new Dictionary<string, Borehole>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) { if (line.Length > 0) r.SkippedLines++; continue; }
            var t = line.Split(Seps, StringSplitOptions.None);
            if (t.Length < 3) { r.SkippedLines++; continue; }

            // 数据行判定：第 2/3 列(X,Y)可解析为数值（否则视为表头/说明）
            if (!TryNum(t[1], out double x) || !TryNum(t[2], out double y)) { r.SkippedLines++; continue; }

            string name = t[0].Trim();
            if (name.Length == 0) name = "ZK" + (byName.Count + 1);
            if (!byName.TryGetValue(name, out var bh))
            {
                bh = new Borehole { Name = name, X = x, Y = y };
                if (t.Length > 3 && TryNum(t[3], out double z)) bh.Z = z;
                byName[name] = bh;
                r.Boreholes.Add(bh);
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
            // 分层：自/至/岩性（列 4/5/6，可选）
            if (t.Length > 5 && TryNum(t[4], out double from) && TryNum(t[5], out double to))
                bh.Intervals.Add(new Interval { From = from, To = to, Rock = t.Length > 6 ? t[6].Trim() : "" });
        }

        if (r.Boreholes.Count == 0) { r.Error = "未解析到钻孔（每行需 孔号,X,Y[,高程,自,至,岩性]）"; return r; }
        r.Bounds = new[] { minX, minY, maxX, maxY };
        return r;
    }

    private static bool TryNum(string s, out double v)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
