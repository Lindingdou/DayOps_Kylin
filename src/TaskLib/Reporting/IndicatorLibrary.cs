// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/IndicatorLibrary.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 自定义指标库（计算规则）—— 用户定义的指标 JSON 落盘，随时增删改。
/// 目录：LocalApplicationData/PitMine/report_indicators。内置指标在代码里，不入此库。
/// </summary>
public static class IndicatorLibrary
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitMine", "report_indicators");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<CustomIndicatorDef> LoadAll()
    {
        var list = new List<CustomIndicatorDef>();
        foreach (var f in Files())
        {
            var d = TryLoad(f);
            if (d != null) list.Add(d);
        }
        return list.OrderBy(d => d.Name, StringComparer.CurrentCulture).ToList();
    }

    public static void Save(CustomIndicatorDef def)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));
        File.WriteAllText(PathOf(def.Id), JsonSerializer.Serialize(def, Opt));
    }

    public static void Delete(string id)
    {
        var p = PathOf(id);
        if (File.Exists(p)) File.Delete(p);
    }

    private static string PathOf(string id)
    {
        var safe = string.Join("_", (id ?? "ind").Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Dir(), safe + ".json");
    }

    private static IEnumerable<string> Files()
    {
        try { return Directory.EnumerateFiles(Dir(), "*.json"); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static CustomIndicatorDef? TryLoad(string file)
    {
        try
        {
            var txt = File.ReadAllText(file);
            return string.IsNullOrWhiteSpace(txt) ? null : JsonSerializer.Deserialize<CustomIndicatorDef>(txt, Opt);
        }
        catch { return null; }
    }
}
