using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PitMine3D.Kylin;

/// <summary>
/// 用户配置(JSON 文件 + 内存缓存 + 延迟写盘) —— 忠实移植原版 <c>UserSettingsService</c> 的行为:
///   · Get/Set 只碰内存, 不阻塞界面; Set 之后 500ms 静默期一到, 后台线程整份写一次
///   · 写盘走 .tmp + 原子改名, 中途断电保留旧配置
///   · 解析失败 → 当空配置启动(用户旧设置丢, 但程序照常起)
///   · 关闭时 <see cref="Flush"/> 同步落盘(原版 MainWindow.OnWindowClosing → FlushPersistence)
///
/// 存放位置与 crash.log 同目录(用户数据目录 ~/.local/share/PitMine3D.Kylin/)：
/// 原版把 Config/ 放在 exe 边上(便携), 但麒麟上程序装在 /opt 或 /usr 下常是只读的,
/// 写不进去配置就会静默丢失, 故落用户目录。
/// </summary>
public sealed class UserSettings : IDisposable
{
    private const int DebounceMs = 500;
    private const string ConfigFileName = "config.json";

    private static UserSettings? _current;

    /// <summary>全局实例(进程内唯一)。</summary>
    public static UserSettings Current => _current ??= new UserSettings();

    private readonly object _lock = new();
    private readonly Dictionary<string, JsonElement> _data = new(StringComparer.Ordinal);
    private readonly string _configFile;

    private int _pendingScheduleId;
    private volatile bool _disposed;

    // UnsafeRelaxedJsonEscaping: 不把中文转成 \uXXXX —— 配置文件是给人看/手改的, 路径里全是中文,
    // 转义后一眼看不出是哪个目录。本地文件不入 HTML, 不存在注入面。
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions ReadOpts =
        new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>配置目录(与 crash.log 同目录)。</summary>
    public string ConfigDir { get; }

    /// <summary>配置文件全路径(诊断/信息栏显示用)。</summary>
    public string ConfigFile => _configFile;

    /// <summary>构造即同步读一次 config.json(不存在则空配置)。测试可传 dir 指定目录。</summary>
    public UserSettings(string? dir = null)
    {
        ConfigDir = dir ?? Path.GetDirectoryName(CrashLog.Path) ?? Path.GetTempPath();
        try { Directory.CreateDirectory(ConfigDir); } catch { /* 建不了目录 → 后面写盘失败, 只影响持久化 */ }
        _configFile = Path.Combine(ConfigDir, ConfigFileName);
        LoadFromDisk();
    }

    /// <summary>读一项; 不存在/类型对不上 → 返回 defaultValue。</summary>
    public T? Get<T>(string key, T? defaultValue = default)
    {
        JsonElement el;
        lock (_lock) { if (!_data.TryGetValue(key, out el)) return defaultValue; }
        try { return JsonSerializer.Deserialize<T>(el.GetRawText(), ReadOpts); }
        catch { return defaultValue; }
    }

    /// <summary>写一项(内存即时生效, 落盘延迟到静默期后)。</summary>
    public void Set<T>(string key, T value)
    {
        JsonElement el;
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
            el = doc.RootElement.Clone();
        }
        catch { return; }

        lock (_lock) { _data[key] = el; }
        ScheduleWrite();
    }

    public bool ContainsKey(string key) { lock (_lock) { return _data.ContainsKey(key); } }

    public bool Remove(string key)
    {
        bool removed;
        lock (_lock) { removed = _data.Remove(key); }
        if (removed) ScheduleWrite();
        return removed;
    }

    /// <summary>同步立即写盘(关闭前调用, 免得静默期里的改动丢掉)。</summary>
    public void Flush()
    {
        Interlocked.Increment(ref _pendingScheduleId);   // 让在途的延迟任务失效, 由本次统一写
        WriteToDisk();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Flush(); } catch { }   // 顺序: 先落盘再置位, 否则 WriteToDisk 开头会早退把最后一次写吞掉
        _disposed = true;
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_configFile)) return;
        try
        {
            var bytes = File.ReadAllBytes(_configFile);
            if (bytes.Length == 0) return;
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            lock (_lock)
                foreach (var prop in doc.RootElement.EnumerateObject())
                    _data[prop.Name] = prop.Value.Clone();
        }
        catch (Exception ex)
        {
            CrashLog.Write("配置", $"加载失败(按空配置启动): {ex.Message}");
        }
    }

    // 每次 Set 排一次延迟任务; 静默期内反复 Set 只写最后一次(调度号对不上的任务直接退出)。
    private void ScheduleWrite()
    {
        int myId = Interlocked.Increment(ref _pendingScheduleId);
        _ = Task.Run(async () =>
        {
            await Task.Delay(DebounceMs).ConfigureAwait(false);
            if (Volatile.Read(ref _pendingScheduleId) != myId) return;
            WriteToDisk();
        });
    }

    private void WriteToDisk()
    {
        if (_disposed) return;
        byte[] bytes;
        try { lock (_lock) { bytes = JsonSerializer.SerializeToUtf8Bytes(_data, WriteOpts); } }
        catch (Exception ex) { CrashLog.Write("配置", $"序列化失败: {ex.Message}"); return; }

        string tmp = _configFile + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _configFile, overwrite: true);   // 原子改名
        }
        catch (Exception ex)
        {
            CrashLog.Write("配置", $"写盘失败: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
