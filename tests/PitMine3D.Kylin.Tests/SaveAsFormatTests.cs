using System;
using System.Linq;
using PitMine3D.Kylin.Views;
using Xunit;

namespace PitMine3D.Kylin.Tests;

public class SaveAsFormatTests
{
    [Fact]
    public void Save_as_offers_only_pmx_dxf_and_dwg()
    {
        var choices = SaveAsFormats.CreatePickerChoices();
        var patterns = choices.SelectMany(choice => choice.Patterns ?? Array.Empty<string>()).ToArray();

        Assert.Equal(new[] { "*.pmx", "*.dxf", "*.dwg" }, patterns);
    }

    [Theory]
    [InlineData(".pmx", true)]
    [InlineData(".dxf", true)]
    [InlineData(".dwg", true)]
    [InlineData(".kdf", false)]
    public void Save_as_rejects_unsupported_extensions(string extension, bool expected)
    {
        bool actual = SaveAsFormats.IsSupportedExtension(extension);

        Assert.Equal(expected, actual);
    }
}
