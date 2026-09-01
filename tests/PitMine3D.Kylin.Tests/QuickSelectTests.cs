using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 快速选择(QSELECT) 回归 —— 忠实移植原 QuickSelectFilter/Catalog/Model 的已知值验证 +
/// Kylin 场景实体 → 快照 → 过滤 的端到端。核心正确点: Include/Exclude 的「类型先决条件」语义。
/// </summary>
public class QuickSelectTests
{
    // ── 通配匹配(双指针回溯) ─────────────────────────────────────────────
    [Theory]
    [InlineData("煤层顶板", "煤*", true)]
    [InlineData("煤层顶板", "*顶板", true)]
    [InlineData("煤层顶板", "煤*板", true)]
    [InlineData("煤层顶板", "*层*", true)]
    [InlineData("煤层顶板", "煤层顶板", true)]
    [InlineData("煤层顶板", "煤?顶板", true)]     // ? = 恰好一字符
    [InlineData("煤层顶板", "煤?板", false)]      // 少一字符
    [InlineData("Layer01", "layer*", true)]        // 大小写不敏感
    [InlineData("abc", "a*c", true)]
    [InlineData("abc", "a?c", true)]
    [InlineData("ac", "a?c", false)]
    [InlineData("", "*", true)]                    // * 吞 0 字符
    [InlineData("x", "", false)]
    public void WildcardMatch_known_cases(string input, string pattern, bool expected)
        => Assert.Equal(expected, QuickSelectFilter.WildcardMatch(input, pattern));

    // ── 数值比较: 按量级放大的相对容差 ───────────────────────────────────
    [Fact]
    public void Number_compare_uses_magnitude_relative_tolerance()
    {
        // native to_wstring 只留 6 位: 1/3 存成 "0.333333", 拿 1e-9 卡 = 会全落空。
        Assert.True(QuickSelectFilter.Compare("0.333333", "0.333333", QuickSelectOperator.Equals, QuickSelectValueKind.Number));
        Assert.True(QuickSelectFilter.Compare("5", "5", QuickSelectOperator.Equals, QuickSelectValueKind.Number));
        Assert.True(QuickSelectFilter.Compare("6", "5", QuickSelectOperator.Greater, QuickSelectValueKind.Number));
        Assert.False(QuickSelectFilter.Compare("5", "5", QuickSelectOperator.Greater, QuickSelectValueKind.Number));
        Assert.True(QuickSelectFilter.Compare("5", "5", QuickSelectOperator.GreaterOrEqual, QuickSelectValueKind.Number));
        Assert.True(QuickSelectFilter.Compare("4.9", "5", QuickSelectOperator.Less, QuickSelectValueKind.Number));
        Assert.True(QuickSelectFilter.Compare("3", "5", QuickSelectOperator.NotEquals, QuickSelectValueKind.Number));
        Assert.False(QuickSelectFilter.Compare("abc", "5", QuickSelectOperator.Equals, QuickSelectValueKind.Number));   // 解析失败=不匹配
    }

    [Fact]
    public void Boolean_and_text_compare()
    {
        Assert.True(QuickSelectFilter.Compare("是", "true", QuickSelectOperator.Equals, QuickSelectValueKind.Boolean));
        Assert.True(QuickSelectFilter.Compare("false", "否", QuickSelectOperator.Equals, QuickSelectValueKind.Boolean));
        Assert.True(QuickSelectFilter.Compare("0", "false", QuickSelectOperator.Equals, QuickSelectValueKind.Boolean));
        Assert.True(QuickSelectFilter.Compare("煤层", "煤层", QuickSelectOperator.Equals, QuickSelectValueKind.Text));
        Assert.True(QuickSelectFilter.Compare("LAYER", "layer", QuickSelectOperator.Equals, QuickSelectValueKind.Text));  // 大小写不敏感
        Assert.True(QuickSelectFilter.Compare("煤层顶", "煤*", QuickSelectOperator.Wildcard, QuickSelectValueKind.Text));
    }

    // ── JSON 摊平: 数组拆下标, 嵌套拆点号, 坏 JSON 空字典不抛 ─────────────
    [Fact]
    public void FlattenJson_arrays_and_nesting()
    {
        var d = QuickSelectFilter.FlattenJson("{\"startPoint\":[1,2,3],\"radius\":5.5,\"closed\":true}");
        Assert.Equal("1", d["startPoint.0"]);
        Assert.Equal("3", d["startPoint.2"]);
        Assert.Equal("5.5", d["radius"]);
        Assert.Equal("true", d["closed"]);
        Assert.Empty(QuickSelectFilter.FlattenJson("{坏"));   // 坏 JSON → 空
        Assert.Empty(QuickSelectFilter.FlattenJson(null));
    }

    // ── 目录: 类型专属特性 / 通用特性 / 运算符裁剪 ───────────────────────
    [Fact]
    public void Catalog_properties_and_operators()
    {
        // typeId=null(所有图元) 只给通用特性 —— 不给"半径"这种把用户往空集里带的选项。
        var common = QuickSelectCatalog.PropertiesFor(null);
        Assert.Contains(common, p => p.Key == "layer");
        Assert.DoesNotContain(common, p => p.Key == "radius");
        // 圆 = 通用 + 半径/圆心。
        var circle = QuickSelectCatalog.PropertiesFor(QuickSelectCatalog.TypeCircle);
        Assert.Contains(circle, p => p.Key == "radius");
        Assert.Contains(circle, p => p.Key == "layer");
        // 文本运算符不含 > < (字典序对图层名/文本没有可预期语义)。
        var textOps = QuickSelectCatalog.OperatorsFor(QuickSelectValueKind.Text);
        Assert.Contains(QuickSelectOperator.Wildcard, textOps);
        Assert.DoesNotContain(QuickSelectOperator.Greater, textOps);
        // 数值含 > < 但不含通配。
        var numOps = QuickSelectCatalog.OperatorsFor(QuickSelectValueKind.Number);
        Assert.Contains(QuickSelectOperator.Greater, numOps);
        Assert.DoesNotContain(QuickSelectOperator.Wildcard, numOps);
        // Find / SourceOf。
        Assert.Equal(QuickSelectValueKind.Number, QuickSelectCatalog.Find("radius")!.Kind);
        Assert.Equal(QuickSelectSource.Core, QuickSelectCatalog.SourceOf("layer"));
        Assert.Equal(QuickSelectSource.Json, QuickSelectCatalog.SourceOf("radius"));
        Assert.Null(QuickSelectCatalog.Find("nope"));
    }

    // ── 过滤核 Apply: 类型先决条件 + Include/Exclude 语义(核心正确点) ────
    static List<EntitySnapshot> MixedScene()
    {
        // 3 圆(半径 3/6/9, 层 A/A/B) + 2 线(层 A/B) + 1 文字(层 A)
        var ents = new List<SceneEntity>
        {
            new CircleEntity { Cx = 0, Cy = 0, Radius = 3, LayerName = "A" },   // 0
            new CircleEntity { Cx = 0, Cy = 0, Radius = 6, LayerName = "A" },   // 1
            new CircleEntity { Cx = 0, Cy = 0, Radius = 9, LayerName = "B" },   // 2
            new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 0, LayerName = "A" }, // 3
            new LineEntity { X0 = 0, Y0 = 0, X1 = 2, Y1 = 0, LayerName = "B" }, // 4
            new TextEntity { X = 0, Y = 0, Text = "标高100", LayerName = "A" }, // 5
        };
        return QuickSelectSnapshot.FromScene(ents);
    }

    [Fact]
    public void Apply_circles_with_radius_greater_than_5()
    {
        var snaps = MixedScene();
        var c = new QuickSelectCriteria { TypeId = QuickSelectCatalog.TypeCircle, PropertyKey = "radius", Operator = QuickSelectOperator.Greater, Value = "5" };
        var r = QuickSelectFilter.Apply(snaps, c);
        Assert.Equal(6, r.Examined);
        Assert.Equal(new ulong[] { 1, 2 }, r.Handles);   // 半径 6/9 的圆(下标 1/2)
    }

    [Fact]
    public void Exclude_is_type_prerequisite_not_blanket_negation()
    {
        // 「圆 + 半径>5 + 排除」的答案是【半径≤5 的圆】(下标 0)，
        // 绝不能把满图的线/文字(它们"没命中")一起扫进来 —— 类型不符者先出局。
        var snaps = MixedScene();
        var c = new QuickSelectCriteria { TypeId = QuickSelectCatalog.TypeCircle, PropertyKey = "radius", Operator = QuickSelectOperator.Greater, Value = "5", ApplyMode = QuickSelectApplyMode.Exclude };
        var r = QuickSelectFilter.Apply(snaps, c);
        Assert.Equal(new ulong[] { 0 }, r.Handles);   // 只有小圆, 没有任何线/文字
    }

    [Fact]
    public void Apply_by_layer_across_all_types()
    {
        var snaps = MixedScene();
        var c = new QuickSelectCriteria { TypeId = null, PropertyKey = "layer", Operator = QuickSelectOperator.Equals, Value = "A" };
        var r = QuickSelectFilter.Apply(snaps, c);
        Assert.Equal(new ulong[] { 0, 1, 3, 5 }, r.Handles);   // 层 A: 圆0/圆1/线3/文字5
    }

    [Fact]
    public void Apply_type_only_selects_all_of_type()
    {
        var snaps = MixedScene();
        var c = new QuickSelectCriteria { TypeId = QuickSelectCatalog.TypeLine, Operator = QuickSelectOperator.All };
        var r = QuickSelectFilter.Apply(snaps, c);
        Assert.Equal(new ulong[] { 3, 4 }, r.Handles);
    }

    [Fact]
    public void Apply_text_wildcard_on_content()
    {
        var snaps = MixedScene();
        var c = new QuickSelectCriteria { TypeId = QuickSelectCatalog.TypeText, PropertyKey = "textString", Operator = QuickSelectOperator.Wildcard, Value = "标高*" };
        var r = QuickSelectFilter.Apply(snaps, c);
        Assert.Equal(new ulong[] { 5 }, r.Handles);
    }

    [Fact]
    public void Apply_polyline_closed_boolean()
    {
        var open = new PolylineEntity { Closed = false, LayerName = "A" };
        open.Points.AddRange(new[] { (0.0, 0.0), (1.0, 0.0) });
        var closed = new PolylineEntity { Closed = true, LayerName = "A" };
        closed.Points.AddRange(new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0) });
        var snaps = QuickSelectSnapshot.FromScene(new List<SceneEntity> { open, closed });
        var c = new QuickSelectCriteria { TypeId = QuickSelectCatalog.TypePolyline, PropertyKey = "closed", Operator = QuickSelectOperator.Equals, Value = "是" };
        var r = QuickSelectFilter.Apply(snaps, c);
        Assert.Equal(new ulong[] { 1 }, r.Handles);
    }

    // ── 快照构造: 类型 id / 颜色打包 / Extended 键 ───────────────────────
    [Fact]
    public void Snapshot_maps_type_color_and_extended()
    {
        var circle = new CircleEntity { Cx = 10, Cy = 20, Radius = 7, Cr = 1f, Cg = 0f, Cb = 0f, LayerName = "L", LineWeight = 30 };
        var s = QuickSelectSnapshot.From(circle, 42);
        Assert.Equal((ulong)42, s.Handle);
        Assert.Equal(QuickSelectCatalog.TypeCircle, s.TypeId);
        Assert.Equal("L", s.LayerName);
        Assert.Equal(30f, s.Lineweight);
        Assert.Equal(0xFF0000u, s.ColorIndex);          // 纯红 → 0xRRGGBB
        Assert.Equal("7", s.Extended!["radius"]);
        Assert.Equal("10", s.Extended["center.0"]);
        // 通用: 可见性/透明度恒填。
        Assert.Equal("true", s.Extended["visible"]);
    }

    [Fact]
    public void Snapshot_missing_extended_value_never_matches()
    {
        // 直线没有 radius 特性 → 拿 radius 去筛直线, 取不到值一律不匹配(含 <>)。
        var snaps = QuickSelectSnapshot.FromScene(new List<SceneEntity> { new LineEntity { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = "A" } });
        var c = new QuickSelectCriteria { TypeId = null, PropertyKey = "radius", Operator = QuickSelectOperator.NotEquals, Value = "5" };
        var r = QuickSelectFilter.Apply(snaps, c);
        Assert.Empty(r.Handles);
    }

    [Fact]
    public void Apply_null_or_empty_candidates_is_safe()
    {
        var c = new QuickSelectCriteria { TypeId = QuickSelectCatalog.TypeCircle };
        Assert.Empty(QuickSelectFilter.Apply(null, c).Handles);
        Assert.Empty(QuickSelectFilter.Apply(new List<EntitySnapshot>(), c).Handles);
    }
}
