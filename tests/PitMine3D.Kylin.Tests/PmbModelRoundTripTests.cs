using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// .pmb 整模型往返（导出块体 → 导入块体）：除几何与属性值外，原版 PmbmWriter 还落
/// PropertySchema(11) / DisplayStyle(16) / DeletedCells(17) / CategoryMeta(19) 四段 —— 这里钉住它们不再丢。
/// </summary>
public class PmbModelRoundTripTests
{
    private static BlockModelMeta MakeModel()
    {
        var m = BlockModelMeta.CreateRegular("煤层块体", 100, 200, 300, 10, 10, 5, 3, 2, 2);   // 12 块
        m.Description = "往返测试";
        m.RotationZDeg = 30;
        m.StorageMode = BlockStorageMode.Adaptive;
        m.SubBlockDepthMax = 3;
        m.SubMinX = 2.5; m.SubMinY = 2.5; m.SubMinZ = 1.25;

        m.PropertySchema.Add(new BlockPropertyColumn { Name = "ash", DataType = BlockPropertyType.Float, Unit = "%", DefaultValue = 0, Description = "灰分" });
        m.PropertySchema.Add(new BlockPropertyColumn
        {
            Name = "rock_type", DataType = BlockPropertyType.UInt32, Unit = "", DefaultValue = 0, Description = "岩性 id",
            IsCategorical = true, CategoryLabels = new List<string> { "煤", "砂岩", "泥岩" },
        });

        var ash = m.EnsureAttr("ash");
        var rock = m.EnsureAttr("rock_type");
        for (int i = 0; i < m.Blocks.Count; i++) { ash[i] = i * 1.5; rock[i] = i % 3; }

        m.DisplayStyle.FillColor = (0x12, 0x34, 0x56);
        m.DisplayStyle.EdgeColor = (0x78, 0x9A, 0xBC);
        m.DisplayStyle.EdgeMode = BlockEdgeMode.HiddenUnlessSelected;
        m.DisplayStyle.EdgeWidthPx = 1.5;
        m.DisplayStyle.DefaultColormap = BlockColormapPreset.Turbo;
        var cat = m.DisplayStyle.EnsureCategoryColors("rock_type");
        cat[0] = (10, 10, 10); cat[1] = (200, 180, 120); cat[2] = (90, 90, 140);

        m.ActiveColormapAttribute = "ash";
        m.DeletedIds.Add(3); m.DeletedIds.Add(7);
        return m;
    }

    [Fact]
    public void Whole_model_round_trip_keeps_grid_schema_style_and_deleted()
    {
        var m = MakeModel();
        var bytes = PmbExportService.FromModel(m);
        var r = PmbImportService.Parse(bytes);

        Assert.True(r.Success);
        Assert.Equal("煤层块体", r.ModelName);
        Assert.Equal("往返测试", r.Description);
        Assert.Equal(30, r.RotationZDeg, 6);
        Assert.Equal(BlockStorageMode.Adaptive, r.StorageMode);
        Assert.Equal(3, r.SubBlockDepthMax);
        Assert.Equal(2.5, r.SubMinX, 4);
        Assert.Equal(1.25, r.SubMinZ, 4);
        Assert.Equal((3, 2, 2), (r.Nx, r.Ny, r.Nz));
        Assert.Equal(12, r.Blocks.Count);

        // 属性表
        Assert.Equal(2, r.Schema.Count);
        Assert.Equal("ash", r.Schema[0].Name);
        Assert.Equal("%", r.Schema[0].Unit);
        Assert.Equal("灰分", r.Schema[0].Description);
        Assert.False(r.Schema[0].IsCategorical);
        Assert.Equal(BlockPropertyType.UInt32, r.Schema[1].DataType);
        Assert.True(r.Schema[1].IsCategorical);
        Assert.Equal(new[] { "煤", "砂岩", "泥岩" }, r.Schema[1].CategoryLabels!);

        // 显示样式 + 活动着色属性
        Assert.NotNull(r.Style);
        Assert.Equal(((byte)0x12, (byte)0x34, (byte)0x56), r.Style!.FillColor);
        Assert.Equal(((byte)0x78, (byte)0x9A, (byte)0xBC), r.Style.EdgeColor);
        Assert.Equal(BlockEdgeMode.HiddenUnlessSelected, r.Style.EdgeMode);
        Assert.Equal(1.5, r.Style.EdgeWidthPx, 6);
        Assert.Equal(BlockColormapPreset.Turbo, r.Style.DefaultColormap);
        Assert.Equal("ash", r.ActiveColormapAttribute);

        // 分类逐类颜色
        var colors = r.Style.CategoryColors["rock_type"];
        Assert.Equal(3, colors.Count);
        Assert.Equal(((byte)200, (byte)180, (byte)120), colors[1]);

        // 已删集合 + 属性值（x-fastest 线性序）
        Assert.Equal(new HashSet<long> { 3, 7 }, r.DeletedCells);
        Assert.Equal(4.5, r.AllAttrs["ash"][3], 6);
        Assert.Equal(2, r.AllAttrs["rock_type"][11], 6);
    }

    [Fact]
    public void Round_trip_restores_geometry_centers()
    {
        var m = MakeModel();
        var r = PmbImportService.Parse(PmbExportService.FromModel(m));
        for (int i = 0; i < m.Blocks.Count; i++)
        {
            Assert.Equal(m.Blocks[i].X, r.Blocks[i].X, 6);
            Assert.Equal(m.Blocks[i].Y, r.Blocks[i].Y, 6);
            Assert.Equal(m.Blocks[i].Z, r.Blocks[i].Z, 6);
        }
    }

    [Fact]
    public void Scoped_export_writes_subset_without_deleted_section()
    {
        var m = MakeModel();
        var scope = new List<int> { 0, 1, 2 };   // 只导第一排
        var r = PmbImportService.Parse(PmbExportService.FromModel(m, scope, new[] { "ash" }));
        Assert.True(r.Success);
        Assert.Equal(3, r.Blocks.Count);
        Assert.Empty(r.DeletedCells);
        Assert.Single(r.Schema);
        Assert.Equal("ash", r.Schema[0].Name);
        Assert.Equal(new[] { 0.0, 1.5, 3.0 }, r.AllAttrs["ash"]);
    }

    [Fact]
    public void Legacy_geometry_only_file_still_reads()
    {
        // 旧写法（只有 Strings/GridSpec/Blocks）必须照样能读，且元数据段缺失时给默认值
        var g = new PmbExportService.Grid(0, 0, 0, 1, 1, 1, 2, 1, 1);
        var r = PmbImportService.Parse(PmbExportService.ToBytes(g, new List<(string, double[])> { ("grade", new[] { 1.0, 2.0 }) }, "old"));
        Assert.True(r.Success);
        Assert.Equal("old", r.ModelName);
        Assert.Empty(r.Schema);
        Assert.Null(r.Style);
        Assert.Empty(r.DeletedCells);
        Assert.Equal(0, r.RotationZDeg, 6);
    }
}
