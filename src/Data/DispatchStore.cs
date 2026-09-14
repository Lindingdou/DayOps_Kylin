using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 任务单据的落盘（移植原 <c>TaskLib</c> 那套 <c>%LOCALAPPDATA%/PitMine/</c> JSON 存储）。
///
/// <para>
/// <b>两本账，语义不同</b>：
/// <list type="bullet">
///   <item><b>实例</b>（<see cref="TaskInstance"/>）按稳定键<b>覆盖</b> —— 一条任务此刻是什么状态，只有一个答案。</item>
///   <item><b>回执</b>（<see cref="DispatchReceipt"/>）只<b>追加</b> —— 单据流水只增不改。
///     撤回是"又发生了一件事"，不是把上一件事擦掉；抹掉流水，"谁在几点下达过"就查不到了。</item>
/// </list>
/// </para>
///
/// ── Kylin 侧的实现差异（登记）──
/// <list type="number">
///   <item>落 JSON 文件而不是建表：原版就是 JSON，且 Kylin 建表要同时动 SQLite 与 openGauss 两套迁移
///     并登记行版本 —— 单据这一摊将来若要落库，那是一次单独的迁移，不在本轮混做。</item>
///   <item>文件放在与 <c>crash.log</c> 同一个目录（麒麟上即用户目录下），同 <see cref="UserSettings"/> 的选择。</item>
///   <item>读坏了<b>不抛</b>：返回空表并把原因写进 <see cref="LastError"/>。单据文件坏掉不该拦着人开窗，
///     但也<b>绝不能静默当成"没有单据"</b> —— 那会让人以为今天一条都没下达过，然后重下一遍。</item>
/// </list>
/// </summary>
public static class DispatchStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不转义：文件是给人看/手改的
    };

    /// <summary>最近一次读写出的问题（空 = 没问题）。<b>读坏与读空是两回事</b>，调用方据此区分。</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>落盘目录（测试可改指向临时目录）。</summary>
    public static string? DirOverride { get; set; }

    private static string Dir => DirOverride
        ?? Path.GetDirectoryName(PitMine3D.Kylin.CrashLog.Path)
        ?? Path.GetTempPath();

    private static string InstanceFile => Path.Combine(Dir, "task_instances.json");
    private static string ReceiptFile => Path.Combine(Dir, "dispatch_receipts.json");

    // ── 实例 ────────────────────────────────────────────────

    /// <summary>读全部实例（键 = 稳定键）。读不到/读坏了返回空表，原因见 <see cref="LastError"/>。</summary>
    public static Dictionary<string, TaskInstance> LoadInstances()
    {
        LastError = "";
        var map = new Dictionary<string, TaskInstance>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(InstanceFile)) return map;
            var list = JsonSerializer.Deserialize<List<TaskInstance>>(File.ReadAllText(InstanceFile), Json);
            foreach (var i in list ?? new List<TaskInstance>())
                if (i != null && !string.IsNullOrWhiteSpace(i.StableKey)) map[i.StableKey] = i;
        }
        catch (Exception ex) { LastError = "单据文件读不出（" + Short(ex) + "）"; }
        return map;
    }

    /// <summary>写回全部实例。成功返回空串，失败返回原因（不抛）。</summary>
    public static string SaveInstances(IEnumerable<TaskInstance>? instances)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var list = (instances ?? Enumerable.Empty<TaskInstance>())
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.StableKey))
                .OrderBy(i => i.PlanDate, StringComparer.Ordinal)
                .ThenBy(i => i.StableKey, StringComparer.Ordinal)
                .ToList();
            File.WriteAllText(InstanceFile, JsonSerializer.Serialize(list, Json));
            LastError = "";
            return "";
        }
        catch (Exception ex) { LastError = "单据写盘失败（" + Short(ex) + "）"; return LastError; }
    }

    // ── 回执（只追加）────────────────────────────────────────

    public static List<DispatchReceipt> LoadReceipts()
    {
        LastError = "";
        try
        {
            if (!File.Exists(ReceiptFile)) return new List<DispatchReceipt>();
            return JsonSerializer.Deserialize<List<DispatchReceipt>>(File.ReadAllText(ReceiptFile), Json)
                ?? new List<DispatchReceipt>();
        }
        catch (Exception ex) { LastError = "回执文件读不出（" + Short(ex) + "）"; return new List<DispatchReceipt>(); }
    }

    /// <summary>
    /// 追加回执。<b>先读后写</b>而不是覆盖 —— 回执是流水，覆盖等于把之前的流水抹了。
    /// 读坏时<b>拒绝写</b>：那样写下去会把读不出来的那一段永久顶掉。
    /// </summary>
    public static string AppendReceipts(IEnumerable<DispatchReceipt>? more)
    {
        var add = (more ?? Enumerable.Empty<DispatchReceipt>()).Where(r => r != null).ToList();
        if (add.Count == 0) return "";
        var all = LoadReceipts();
        if (LastError.Length > 0)
            return LastError + " —— 为免把读不出来的那一段顶掉，本次不写。请先处理该文件再重试。";
        all.AddRange(add);
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ReceiptFile, JsonSerializer.Serialize(all, Json));
            return "";
        }
        catch (Exception ex) { LastError = "回执写盘失败（" + Short(ex) + "）"; return LastError; }
    }

    /// <summary>某条任务的回执流水（按时间正序）。</summary>
    public static List<DispatchReceipt> ReceiptsOf(string stableKey)
        => LoadReceipts()
            .Where(r => string.Equals(r.StableKey, stableKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.At)
            .ToList();

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
