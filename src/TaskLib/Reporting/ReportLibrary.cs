// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportLibrary.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 报表模板库 —— 内置预设（只读）+ 用户自定义（JSON 落盘，随时增删改）合并成一个库。
/// "定制化" = 克隆内置或新建 → 改样式/绑指标 → Save 存 JSON；下次一键生成即可选用。
/// 目录约定沿用 TaskPersistence：LocalApplicationData/PitMine/report_templates。
/// </summary>
public static class ReportLibrary
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitMine", "report_templates");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>全部模板 = 内置（只读）+ 自定义（可改）。同 Id 时自定义覆盖内置。</summary>
    public static List<ReportDefinition> LoadAll()
    {
        var list = BuiltInTemplates.All();
        var byId = list.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var f in CustomFiles())
        {
            var d = TryLoad(f);
            if (d == null) continue;
            d.BuiltIn = false;
            byId[d.Id] = d;   // 自定义覆盖同 Id 内置
        }
        return byId.Values.OrderByDescending(d => d.BuiltIn).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToList();
    }

    public static void Save(ReportDefinition def)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));
        def.BuiltIn = false;   // 存下来的都是可改的自定义模板
        File.WriteAllText(PathOf(def.Id), JsonSerializer.Serialize(def, Opt));
    }

    public static void Delete(string id)
    {
        var p = PathOf(id);
        if (File.Exists(p)) File.Delete(p);
    }

    public static bool IsCustom(string id) => File.Exists(PathOf(id));

    private static string PathOf(string id)
    {
        var safe = string.Join("_", (id ?? "tpl").Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Dir(), safe + ".json");
    }

    private static IEnumerable<string> CustomFiles()
    {
        try { return Directory.EnumerateFiles(Dir(), "*.json"); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static ReportDefinition? TryLoad(string file)
    {
        try
        {
            var txt = File.ReadAllText(file);
            return string.IsNullOrWhiteSpace(txt) ? null : JsonSerializer.Deserialize<ReportDefinition>(txt, Opt);
        }
        catch { return null; }   // 损坏文件跳过，绝不让一键失败
    }
}
