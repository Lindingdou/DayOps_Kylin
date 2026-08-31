using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 磁盘图纸文件浏览（文件管理器面板用）。当前列出目录下的 .dxf 文件（顶层）。
/// 递归子目录树 / DWG 等后续扩展。对应 Windows 版文件管理器的磁盘文件树。
/// </summary>
public static class CadFileBrowser
{
    /// <summary>列出目录下的 .dxf 文件；返回 (显示名, 全路径)，按名排序。目录不存在/无权限 → 空表。</summary>
    public static List<(string Name, string Path)> ListDxf(string dir) => ListByExtensions(dir, DxfOnly);

    /// <summary>可导入的图形/网格格式扩展名（与 ImportPath 分派一致）。</summary>
    public static readonly string[] ImportableExtensions =
        { ".dxf", ".dwg", ".off", ".wl", ".wt", ".wp", ".mpj", ".kdf", ".3dm" };

    private static readonly string[] DxfOnly = { ".dxf" };

    /// <summary>列出目录下所有可导入的图形/网格文件（DXF/DWG/OFF/MapGIS/KDF/3DMine）。文件管理器面板用。</summary>
    public static List<(string Name, string Path)> ListImportable(string dir) => ListByExtensions(dir, ImportableExtensions);

    private static List<(string Name, string Path)> ListByExtensions(string dir, string[] exts)
    {
        var list = new List<(string Name, string Path)>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
        var set = new HashSet<string>(exts, System.StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                if (set.Contains(Path.GetExtension(f))) list.Add((Path.GetFileName(f), f));
        }
        catch { /* 无权限等 → 返回已收集的 */ }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }
}
