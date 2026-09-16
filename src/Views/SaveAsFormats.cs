using System;
using Avalonia.Platform.Storage;

namespace PitMine3D.Kylin.Views;

internal static class SaveAsFormats
{
    private static readonly string[] SupportedExtensions = { ".pmx", ".dxf", ".dwg" };

    internal static FilePickerFileType[] CreatePickerChoices() =>
    [
        new FilePickerFileType("PitMine 图形 (PMX)") { Patterns = new[] { "*.pmx" } },
        new FilePickerFileType("DXF 图纸") { Patterns = new[] { "*.dxf" } },
        new FilePickerFileType("DWG 图纸") { Patterns = new[] { "*.dwg" } }
    ];

    internal static bool IsSupportedExtension(string extension) =>
        Array.Exists(SupportedExtensions, supported =>
            string.Equals(supported, extension, StringComparison.OrdinalIgnoreCase));
}
