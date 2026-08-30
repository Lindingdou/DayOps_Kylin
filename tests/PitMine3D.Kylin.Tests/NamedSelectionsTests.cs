using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>命名选择集回归（创建/调用/覆盖/轮转）。</summary>
public class NamedSelectionsTests
{
    private static LineEntity L() => new() { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1 };

    [Fact]
    public void Store_and_get_by_name()
    {
        var ns = new NamedSelections();
        var a = L(); var b = L();
        ns.Store("组1", new List<SceneEntity> { a, b });
        Assert.Equal(1, ns.Count);
        Assert.True(ns.Has("组1"));
        var got = ns.Get("组1")!;
        Assert.Equal(2, got.Count);
        Assert.Same(a, got[0]);                       // 存的是引用
    }

    [Fact]
    public void Store_same_name_overwrites()
    {
        var ns = new NamedSelections();
        ns.Store("g", new List<SceneEntity> { L() });
        ns.Store("g", new List<SceneEntity> { L(), L() });
        Assert.Equal(1, ns.Count);                    // 覆盖非新增
        Assert.Equal(2, ns.Get("g")!.Count);
    }

    [Fact]
    public void At_cycles_with_wraparound()
    {
        var ns = new NamedSelections();
        ns.Store("a", new List<SceneEntity> { L() });
        ns.Store("b", new List<SceneEntity> { L() });
        Assert.Equal("a", ns.At(0)!.Value.name);
        Assert.Equal("b", ns.At(1)!.Value.name);
        Assert.Equal("a", ns.At(2)!.Value.name);      // 环绕
    }

    [Fact]
    public void Empty_returns_null()
    {
        var ns = new NamedSelections();
        Assert.Null(ns.At(0));
        Assert.Null(ns.Get("none"));
    }
}
