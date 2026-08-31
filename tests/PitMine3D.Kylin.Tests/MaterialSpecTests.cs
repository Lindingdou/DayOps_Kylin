using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>TaskLib 物料规格/目录回归（忠实移植 MaterialSpec：换算/混采/煤岩分类）。</summary>
public class MaterialSpecTests
{
    [Fact]
    public void Coal_and_rock_conversions()
    {
        var coal = MaterialCatalog.Resolve(MaterialCatalog.Coal);
        Assert.Equal(1.35, coal.InSituDensityTPerM3, 6);
        Assert.Equal(1350, coal.ToTonnage(1000), 3);        // 1000 m³ × 1.35
        Assert.True(coal.IsOre);
        var rock = MaterialCatalog.Resolve(MaterialCatalog.Rock);
        Assert.True(rock.NeedsBlasting);
        Assert.False(rock.IsOre);
        Assert.Equal(1500, rock.ToLooseM3(1000), 3);        // Ks 1.50
        Assert.Equal(1150, rock.ToDumpM3(1000), 3);         // Kr 1.15
    }

    [Fact]
    public void Unknown_code_falls_back_to_rock()
    {
        Assert.Equal("硬岩", MaterialCatalog.Resolve("???").Name);   // 保守回落
    }

    [Fact]
    public void Mix_parse_ratio_and_ore_fraction()
    {
        var mix = MaterialMix.Parse("煤7∶岩3");
        Assert.Equal(0.7, mix.OreFraction, 3);              // 煤 70%
        Assert.Equal(MaterialCatalog.Coal, mix.PrimaryCode);
        // 1000 m³ 混采: 700 煤 + 300 岩 → 吨 = 700×1.35 + 300×2.50
        Assert.Equal(700 * 1.35 + 300 * 2.50, mix.ToTonnage(1000), 3);
    }

    [Fact]
    public void CodeFromText_classifies_seam_and_rock_codes()
    {
        Assert.Equal(MaterialCatalog.Coal, MaterialCatalog.CodeFromText("c4"));    // 煤层号
        Assert.Equal(MaterialCatalog.Coal, MaterialCatalog.CodeFromText("c9"));
        Assert.Equal(MaterialCatalog.Rock, MaterialCatalog.CodeFromText("rh"));    // 岩
        Assert.Equal(MaterialCatalog.Coal, MaterialCatalog.CodeFromText("煤"));
        Assert.Equal(MaterialCatalog.Topsoil, MaterialCatalog.CodeFromText("表土"));
        Assert.Equal(MaterialCatalog.Interburden, MaterialCatalog.CodeFromText("夹矸"));
    }

    [Fact]
    public void Empty_text_falls_back_to_rock_conservatively()
    {
        var mix = MaterialMix.Parse(null);
        Assert.Equal(MaterialCatalog.Rock, mix.PrimaryCode);   // 保守按剥离
    }

    [Fact]
    public void Sink_dumping_classification()
    {
        Assert.True(SinkKind.ExternalDump.IsDumping());
        Assert.True(SinkKind.InternalDump.IsDumping());
        Assert.False(SinkKind.Crusher.IsDumping());
        Assert.False(SinkKind.Silo.IsDumping());
    }
}
