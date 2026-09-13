// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/TruckAutoAssignTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// TA 组：**按 n\* 自动配车**（<c>FaceEquipmentAutoAssigner.AssignTrucks</c>）。
///
/// <para>被判的现象：真库 2026-08-20 的盘子上，29 个采装面**每一个**都是
/// 「荐 6~9 台 / 实配 0 台」，而在用卡车池有 233 台；装配来源文案里
/// <b>连一句配车的话都没有</b>（AssignTrucks 返回空串 ⇒ 不追加任何说明）。
/// 结果是派车单三个班全部展不开，逐条写着"尚未配车"。</para>
/// </summary>
public class TruckAutoAssignTests
{
    private readonly ITestOutputHelper _out;
    public TruckAutoAssignTests(ITestOutputHelper o) { _out = o; }

    private static FaceInput Face(string zone, int recommend, double dayTarget = 10000)
    {
        var f = new FaceInput { Zone = zone, Process = ProcessType.Load, DayTargetM3 = dayTarget };
        f.Group.MainEquipment = "WK-" + zone;
        f.Group.RecommendedTrucks = recommend;
        return f;
    }

    private static List<string> Pool(int n)
        => Enumerable.Range(1, n).Select(i => $"T-{i:000}").ToList();

    /// <summary>
    /// TA1 荐车数解出来了、车池有车、面上一台没配 ⇒ <b>必须真配上</b>，并把配了多少说出来。
    /// <para>这一条正是现场那个状态的最小复现。</para>
    /// </summary>
    [Fact]
    public void TA1_荐车数有车池有就该配上()
    {
        var faces = new List<FaceInput> { Face("岩1308", 6), Face("岩1296", 6), Face("岩1272", 6) };

        string label = FaceEquipmentAutoAssigner.AssignTrucks(faces, Pool(233));
        _out.WriteLine("返回：" + (label.Length == 0 ? "(空串)" : label));
        foreach (var f in faces) _out.WriteLine($"  {f.Zone} 荐={f.Group.RecommendedTrucks} 实配={f.Group.Trucks.Count}");

        Assert.All(faces, f => Assert.Equal(f.Group.RecommendedTrucks, f.Group.Trucks.Count));
        Assert.False(string.IsNullOrWhiteSpace(label),
            "配了车就必须留下说明 —— 返回空串等于这一步没发生过，界面上查不到任何痕迹");

        // 定车制：一台车只跟一台铲
        var all = faces.SelectMany(f => f.Group.Trucks).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    /// <summary>TA2 台账已配过的面不许被覆盖（人填的是资产）。</summary>
    [Fact]
    public void TA2_台账已配的面不被覆盖()
    {
        var manual = Face("岩1308", 6);
        manual.Group.Trucks.AddRange(new[] { "T-900", "T-901" });
        var auto = Face("岩1296", 6);

        FaceEquipmentAutoAssigner.AssignTrucks(new List<FaceInput> { manual, auto }, Pool(50));

        Assert.Equal(new[] { "T-900", "T-901" }, manual.Group.Trucks);   // 原样
        Assert.Equal(6, auto.Group.Trucks.Count);
        Assert.DoesNotContain("T-900", auto.Group.Trucks);               // 不许把人填的车再配一遍
    }

    /// <summary>
    /// TA3 <b>荐车数为 0 时不配</b>（编组没解出来就不猜一个车数），
    /// 但也**不许一声不吭**：一个面都配不上时要说清是为什么。
    /// </summary>
    [Fact]
    public void TA3_荐车数为零时不猜但要出声()
    {
        var faces = new List<FaceInput> { Face("岩1308", 0), Face("岩1296", 0) };

        string label = FaceEquipmentAutoAssigner.AssignTrucks(faces, Pool(233));
        _out.WriteLine("返回：" + (label.Length == 0 ? "(空串)" : label));

        Assert.All(faces, f => Assert.Empty(f.Group.Trucks));
        Assert.False(string.IsNullOrWhiteSpace(label),
            "29 个面一台车都没配上却返回空串 —— 装配来源文案里于是一个字都没有，"
          + "而派车单那边逐条报「尚未配车」。这一步失灵时必须自己说出来。");
    }

    /// <summary>TA4 车不够时按大面优先配满，缺口如实报，且不循环复用同一台车。</summary>
    [Fact]
    public void TA4_车不够时报缺口且不复用()
    {
        var big = Face("岩1308", 6, dayTarget: 50000);
        var small = Face("岩1296", 6, dayTarget: 1000);

        string label = FaceEquipmentAutoAssigner.AssignTrucks(new List<FaceInput> { big, small }, Pool(8));
        _out.WriteLine("返回：" + label);

        Assert.Equal(6, big.Group.Trucks.Count);      // 大面先配满
        Assert.Equal(2, small.Group.Trucks.Count);    // 剩下的给小面
        Assert.Contains("配不满", label);
        var all = big.Group.Trucks.Concat(small.Group.Trucks).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
