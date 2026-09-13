// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportArchive.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>
/// 一条报表存档 —— 生成时的完整快照：模板定义 + 上下文 + 渲染树（值定格在生成时刻）。
/// 支持回溯(渲染 Doc)、重生成(用 Def+Ctx 对当前数据重算)、两期对比(读各自 Doc 合计)。
/// </summary>
public sealed class ReportArchiveEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
    public string PeriodName { get; set; } = "";
    public string PeriodLabel { get; set; } = "";
    public DateTime ArchivedAt { get; set; }

    public ReportDefinition Def { get; set; } = new();   // 供重生成
    public GenerationContext Ctx { get; set; } = new();  // 供重生成
    public ReportDocument Doc { get; set; } = new();     // 快照（回溯/对比）

    /// <summary>列表副行显示（不序列化）。</summary>
    [JsonIgnore]
    public string SubLine => $"{TemplateName} · {PeriodName} · {ScopeLabel} · {ArchivedAt:MM-dd HH:mm}";
}

/// <summary>报表存档库（JSON 落盘于 LocalApplicationData/PitMine/report_archives）。仿路网存档范式。</summary>
public static class ReportArchiveStore
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitMine", "report_archives");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>全部存档，按归档时间倒序（最新在前）。</summary>
    public static List<ReportArchiveEntry> LoadAll()
    {
        var list = new List<ReportArchiveEntry>();
        foreach (var f in Files())
        {
            var d = TryLoad(f);
            if (d != null) list.Add(d);
        }
        return list.OrderByDescending(d => d.ArchivedAt).ToList();
    }

    public static void Save(ReportArchiveEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        File.WriteAllText(PathOf(entry.Id), JsonSerializer.Serialize(entry, Opt));
    }

    public static void Delete(string id)
    {
        var p = PathOf(id);
        if (File.Exists(p)) File.Delete(p);
    }

    private static string PathOf(string id)
    {
        var safe = string.Join("_", (id ?? "arc").Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(Dir(), safe + ".json");
    }

    private static IEnumerable<string> Files()
    {
        try { return Directory.EnumerateFiles(Dir(), "*.json"); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static ReportArchiveEntry? TryLoad(string file)
    {
        try
        {
            var txt = File.ReadAllText(file);
            return string.IsNullOrWhiteSpace(txt) ? null : JsonSerializer.Deserialize<ReportArchiveEntry>(txt, Opt);
        }
        catch { return null; }
    }
}
