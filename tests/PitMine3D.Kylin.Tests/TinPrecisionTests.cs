using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests;

/// <summary>建网精度：矿区量级坐标(七位数)下，顶点必须毫米级保真，不被合并也不被挪动。</summary>
public class TinPrecisionTests
{
    private readonly ITestOutputHelper _o;
    public TinPrecisionTests(ITestOutputHelper o) { _o = o; }

    private const double X0 = 4_412_345.678;   // 典型矿区坐标
    private const double Y0 = 39_512_876.543;

    [Fact]
    public void 毫米间距的点不会被合并()
    {
        // 相距 1mm 的两串点：合并容差是 1mm，恰好 1mm 的点应当各自保留
        var line = new List<(double x, double y)>();
        for (int i = 0; i < 20; i++) line.Add((X0 + i * 0.001, Y0));
        var col = PolylineTin.Collect(new[] { new PolylineTin.Line { Points = line, FlatZ = 100 } });
        _o.WriteLine($"输入 {line.Count} 点(间距 1mm) → 收集 {col.Verts.Count} 点, 合并 {col.MergedVertices}");
        Assert.Equal(line.Count, col.Verts.Count);
        Assert.Equal(0, col.MergedVertices);
    }

    [Fact]
    public void 顶点坐标原样保留_不被量化()
    {
        var line = new List<(double x, double y)>
        { (X0, Y0), (X0 + 12.3456789, Y0 + 7.7654321), (X0 + 5.5, Y0 + 20.25) };
        var col = PolylineTin.Collect(new[] { new PolylineTin.Line { Points = line, FlatZ = 123.456789 } });
        foreach (var p in line)
        {
            var hit = col.Verts.First(v => Math.Abs(v.x - p.x) < 1e-9 && Math.Abs(v.y - p.y) < 1e-9);
            Assert.Equal(p.x, hit.x, 9);      // 纳米级都不差: 存的是原值, 量化只用于建索引
            Assert.Equal(p.y, hit.y, 9);
            Assert.Equal(123.456789, hit.z, 9);
        }
    }

    [Fact]
    public void 亚毫米高差在三角网里保住()
    {
        // 两条等高线相差 0.5mm —— 建出来的面必须保留这个高差
        var a = new List<(double x, double y)>();
        var b = new List<(double x, double y)>();
        for (int i = 0; i < 30; i++) { a.Add((X0 + i, Y0)); b.Add((X0 + i, Y0 + 10)); }
        var col = PolylineTin.Collect(new[]
        {
            new PolylineTin.Line { Points = a, FlatZ = 1000.0000 },
            new PolylineTin.Line { Points = b, FlatZ = 1000.0005 },
        });
        var zs = col.Verts.Select(v => v.z).Distinct().OrderBy(z => z).ToList();
        _o.WriteLine($"高程取值: {string.Join(", ", zs.Select(z => z.ToString("0.0000")))}");
        Assert.Equal(2, zs.Count);
        Assert.Equal(0.0005, zs[1] - zs[0], 9);
    }

    [Fact]
    public void 体素抽稀会破坏毫米精度_故不能默认开()
    {
        // 这条是"反面证据": 抽稀按格取代表点, 必然丢点、必然改变面形。
        // 需要毫米级建网时不能走这条路 —— 用它来钉住这个事实。
        var v = new List<(double x, double y, double z)>();
        for (int i = 0; i < 400; i++)
            for (int j = 0; j < 400; j++) v.Add((X0 + i * 0.5, Y0 + j * 0.5, 100 + i * 0.001));
        var outp = PolylineTin.Downsample(v, 5000, out double vox);
        _o.WriteLine($"抽稀: {v.Count} → {outp.Count} 点, 格边长 {vox:0.###} m");
        Assert.True(outp.Count < v.Count / 10);
        Assert.True(vox > 0.5, "格边长远大于毫米 —— 抽稀与毫米级建网不相容");
    }
}
