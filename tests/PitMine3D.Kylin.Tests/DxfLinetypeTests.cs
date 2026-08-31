using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>DXF 导入线型解析(ResolveDash) —— ByLayer 取图层线型, 显式实体线型优先。</summary>
public class DxfLinetypeTests
{
    [Fact]
    public void Explicit_entity_linetype_wins()
    {
        Assert.Equal(new[] { 6.0, 3.0 }, DxfImportService.ResolveDash("DASHED", "CONTINUOUS"));
        Assert.Null(DxfImportService.ResolveDash("实线", "DASHED"));   // 显式实线优先, 覆盖图层虚线
    }

    [Fact]
    public void ByLayer_resolves_to_layer_linetype()
    {
        Assert.Equal(new[] { 6.0, 3.0 }, DxfImportService.ResolveDash("ByLayer", "DASHED"));
        Assert.Equal(new[] { 6.0, 3.0 }, DxfImportService.ResolveDash(null, "虚线"));   // 空=ByLayer
        Assert.Null(DxfImportService.ResolveDash("ByLayer", "CONTINUOUS"));            // 图层实线 → 实线
    }

    [Fact]
    public void Unknown_names_are_solid()
    {
        Assert.Null(DxfImportService.ResolveDash("SomeCustom", "AlsoUnknown"));
    }
}
