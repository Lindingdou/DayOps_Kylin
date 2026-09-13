// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/CompileOverrides.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  编制的人工锚点 —— 「编制配置」窗口里那几个能改的数，改完要**真的落到盘子上**。
//
//  这里收的两类锚点原先都是"三处各一套数、谁也不喂引擎"的状态：
//    · 综合配煤标准：质量标准窗写 12.8/21.5/0.7（纯文案）、编制配置窗写 13/21/0.8（写死的
//      TextBox，没名字没绑定）、引擎 BlendStandard 的缺省是 12.8/21.5/0.7。三个地方三套数，
//      装箱信的是第三套，前两个界面改了什么都不会发生。
//    · 天气降效：编制配置窗有这个输入框，ExploderConfig 里根本没有对应字段，
//      于是班次日历那一列「天气」纯属展示——设计文档写的"天气恶劣→全盘降效回摊"落不下去。
//
//  现在统一到本类：界面改 → Save 落盘 → 下次装配盘子时 ApplyTo 盖上去 → 引擎按它算。
//  落盘位置与其它执行期事实同一个根（%LOCALAPPDATA%/PitMine/compile_config.json）。
//
//  容错沿用 TaskPersistence 三原则：目录不存在自动建 · 文件坏了返回缺省而不抛 · 写失败报 false。
//  **锚点是"人改过的"才写**：四项全是可空的，没填过就保持 null，由引擎缺省接管——
//  把缺省值也写进文件，日后引擎调缺省时这份旧文件会把老数字顶回来，那是最难查的一类回归。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>编制配置的人工锚点（四项各自可空 = 没改过就不覆盖引擎缺省）。</summary>
public sealed class CompileAnchors
{
    /// <summary>综合灰分上限 %。</summary>
    public double? MaxAshPct { get; set; }

    /// <summary>综合热值下限 MJ/kg。</summary>
    public double? MinCalorificMJkg { get; set; }

    /// <summary>综合硫上限 %。</summary>
    public double? MaxSulfurPct { get; set; }

    /// <summary>天气/路况降效 %（0..95）。0 与 null 语义不同：0 = 人工确认过今天不降效。</summary>
    public double? WeatherDeratePct { get; set; }

    /// <summary>备采保有下限（天）。0 = 人工确认不校核；null = 没设过，引擎缺省（也是不校核）。</summary>
    public double? MinPreparedDays { get; set; }

    /// <summary>
    /// 面日产能的有效工时（h/日，0.5..24）。<b>作业组织重分配与配煤重分配共用的那个闸</b>：
    /// 面日产能上限 = 编组班产 × 本值 × 天气系数。null = 引擎缺省 20h。
    /// </summary>
    public double? EffHoursPerDay { get; set; }

    /// <summary>
    /// 交接班损失（非首班每班扣的坡道时长 h，0..4）。null = 引擎缺省 0.5h。
    /// <b>0 与 null 语义不同</b>：0 = 人工确认本矿不扣交接（如连续接班）。
    /// </summary>
    public double? HandoverRampH { get; set; }

    // ── 按环节降效（null = 跟随 WeatherDeratePct 那个全盘值）──────────────────
    //  三项都不设 ⇒ 与只有全盘值时逐位相同，这条兼容性由判据钉死。

    /// <summary>采装环节降效 %（电铲装车）。</summary>
    public double? LoadDeratePct { get; set; }
    /// <summary>运输环节降效 %（重车/空车行驶、卸点排队）。</summary>
    public double? HaulDeratePct { get; set; }
    /// <summary>排土环节降效 %（推土机平整）。</summary>
    public double? DumpDeratePct { get; set; }

    /// <summary>
    /// 逐受矿点的入仓煤质标准（键 = SinkNode.Id，退而求其次用 Name）。三项各自可空，
    /// 缺项落回全矿级 <see cref="BlendStandard"/>。空字典 = 全部按全矿级判。
    /// <para>真源应当是去向台账；台账还没有这几列，故先由本锚点承担（见 <see cref="SinkNode.MaxAshPct"/>）。</para>
    /// </summary>
    public Dictionary<string, SinkBlendAnchor> SinkBlend { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 作业组织策略（<see cref="WorkOrganization"/> 的枚举名）。null = 没设过 → 引擎缺省 Balanced。
    /// <b>存枚举名而不是序号</b>：日后往中间插一个策略，存了序号的旧文件会整体错位成另一种策略。
    /// </summary>
    public string? Organization { get; set; }

    public bool HasBlend => MaxAshPct.HasValue || MinCalorificMJkg.HasValue || MaxSulfurPct.HasValue;
    /// <summary>有没有设过分环节降效（任一项）。</summary>
    public bool HasLinkDerate => LoadDeratePct.HasValue || HaulDeratePct.HasValue || DumpDeratePct.HasValue;

    public bool IsEmpty => !HasBlend && !WeatherDeratePct.HasValue && !MinPreparedDays.HasValue
                        && !EffHoursPerDay.HasValue && !HandoverRampH.HasValue
                        && !HasLinkDerate && SinkBlend.Count == 0
                        && string.IsNullOrWhiteSpace(Organization);

    public CompileAnchors Clone() => new()
    {
        MaxAshPct = MaxAshPct,
        MinCalorificMJkg = MinCalorificMJkg,
        MaxSulfurPct = MaxSulfurPct,
        WeatherDeratePct = WeatherDeratePct,
        MinPreparedDays = MinPreparedDays,
        EffHoursPerDay = EffHoursPerDay,
        HandoverRampH = HandoverRampH,
        LoadDeratePct = LoadDeratePct,
        HaulDeratePct = HaulDeratePct,
        DumpDeratePct = DumpDeratePct,
        // 深拷：浅拷会让 Clone 出来的那份与原件共用同一个字典，
        // 「改了不保存」和「重置」都会顺手把原件一起改掉
        SinkBlend = SinkBlend.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        Organization = Organization,
    };
}

/// <summary>一个受矿点的入仓煤质锚点。三项各自可空，缺项落回全矿级。</summary>
public sealed class SinkBlendAnchor
{
    public double? MaxAshPct { get; set; }
    public double? MinCalorificMJkg { get; set; }
    public double? MaxSulfurPct { get; set; }

    public bool IsEmpty => !MaxAshPct.HasValue && !MinCalorificMJkg.HasValue && !MaxSulfurPct.HasValue;

    public SinkBlendAnchor Clone() => new()
    {
        MaxAshPct = MaxAshPct, MinCalorificMJkg = MinCalorificMJkg, MaxSulfurPct = MaxSulfurPct,
    };
}

/// <summary>编制人工锚点的读写与套用。</summary>
public static class CompileOverrides
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static CompileAnchors? _cache;

    /// <summary>最近一次读写的结果文案（供 UI 显示"存到哪了 / 为什么没存上"）。</summary>
    public static string LastIoLabel { get; private set; } = "";

    /// <summary>锚点文件路径。</summary>
    public static string FilePath => Path.Combine(TaskPersistence.RootDir(), "compile_config.json");

    /// <summary>当前锚点（首次访问读盘；读不到/读坏了返回空锚点，不抛）。</summary>
    public static CompileAnchors Current
    {
        get
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(FilePath))
                {
                    var a = JsonSerializer.Deserialize<CompileAnchors>(File.ReadAllText(FilePath), Opt);
                    _cache = a ?? new CompileAnchors();
                    LastIoLabel = a != null ? $"已读锚点：{FilePath}" : $"锚点文件为空：{FilePath}";
                }
                else
                {
                    _cache = new CompileAnchors();
                    LastIoLabel = "未保存过编制锚点，按引擎缺省";
                }
            }
            catch (Exception ex)
            {
                _cache = new CompileAnchors();
                LastIoLabel = $"锚点文件读不出（{Short(ex)}），按引擎缺省";
            }
            return _cache;
        }
    }

    /// <summary>保存锚点。成功 true；失败 false 并把原因写进 <see cref="LastIoLabel"/>（不抛）。</summary>
    public static bool Save(CompileAnchors anchors)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(anchors ?? new CompileAnchors(), Opt));
            _cache = (anchors ?? new CompileAnchors()).Clone();
            LastIoLabel = $"已保存：{FilePath}";
            ProductionPlanContext.Invalidate();   // 盘子里缓存的那一份结果按老锚点算的，作废
            return true;
        }
        catch (Exception ex)
        {
            LastIoLabel = $"保存失败（{Short(ex)}）";
            return false;
        }
    }

    /// <summary>丢弃内存缓存（换项目/外部改过文件后调用）。</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>
    /// 单测夹具：直接置入一份锚点，**不读盘也不写盘**（判据不许依赖也不许污染开发机上的真文件）。
    /// 传 null 恢复"下次访问时读盘"。
    /// </summary>
    internal static void SetForTest(CompileAnchors? anchors)
    {
        _cache = anchors;
        LastIoLabel = anchors == null ? "" : "（单测夹具）";
    }

    /// <summary>
    /// 把锚点盖到盘子上。返回一句来源文案（没有任何锚点时返回空串，调用方据此不显示这一段）。
    /// 只盖人改过的那几项：配煤三项任一为 null 即保留引擎缺省，绝不用 0 去顶。
    /// </summary>
    public static string ApplyTo(ExploderConfig cfg)
    {
        if (cfg == null) return "";
        var a = Current;
        if (a.IsEmpty) return "";

        var parts = new System.Collections.Generic.List<string>();

        if (a.HasBlend)
        {
            cfg.Blend ??= new BlendStandard();
            if (a.MaxAshPct is { } ash) cfg.Blend.MaxAshPct = ash;
            if (a.MinCalorificMJkg is { } cv) cfg.Blend.MinCalorificMJkg = cv;
            if (a.MaxSulfurPct is { } s) cfg.Blend.MaxSulfurPct = s;
            parts.Add($"配煤 灰≤{cfg.Blend.MaxAshPct:0.##}% / 热≥{cfg.Blend.MinCalorificMJkg:0.##} / 硫≤{cfg.Blend.MaxSulfurPct:0.##}%");
        }

        if (a.WeatherDeratePct is { } d)
        {
            cfg.WeatherDeratePct = d;
            parts.Add(d > 0.01 ? $"天气降效 {d:0.#}%" : "天气不降效（人工确认）");
        }

        if (a.MinPreparedDays is { } mp)
        {
            cfg.MinPreparedDays = mp;
            parts.Add(mp > 0.01 ? $"备采保有下限 {mp:0.#} 天" : "不校核备采保有（人工确认）");
        }

        // 分环节降效：三项各自可空，null 的那项在引擎里跟随全盘值（见 ExploderConfig.WeatherFactorFor）。
        // 这里不做"没设就写全盘值"的填充 —— 填了就分不出「跟随」与「人工确认与全盘同值」，
        // 而后者是有意义的（日后调全盘值时，前者跟着走、后者不该跟）。
        if (a.LoadDeratePct is { } dl) cfg.LoadDeratePct = dl;
        if (a.HaulDeratePct is { } dh) cfg.HaulDeratePct = dh;
        if (a.DumpDeratePct is { } dd) cfg.DumpDeratePct = dd;
        if (a.HasLinkDerate)
            parts.Add($"分环节降效 采装 {cfg.LoadDeratePct ?? cfg.WeatherDeratePct:0.#}%"
                    + $"/运输 {cfg.HaulDeratePct ?? cfg.WeatherDeratePct:0.#}%"
                    + $"/排土 {cfg.DumpDeratePct ?? cfg.WeatherDeratePct:0.#}%");

        // 有效工时与交接班损失：两者都**只在人锚定过时才盖**，且都会改变"当日到底能干多少"。
        // 它们与降效的分工写在 ExploderConfig 的注释里：降效落在能力上、交接落在时窗上。
        if (a.EffHoursPerDay is { } eh)
        {
            cfg.EffHoursPerDay = eh;
            parts.Add($"面日产能工时 {eh:0.#}h");
        }

        if (a.HandoverRampH is { } hr)
        {
            cfg.HandoverRampH = hr;
            parts.Add(hr > 0.001 ? $"交接班损失 {hr:0.##}h/班" : "不扣交接班（人工确认）");
        }

        // 认不出的策略名一律**忽略并保持缺省**，不猜也不抛：锚点文件可能是手改坏的，
        // 猜一个"最像"的策略会让全盘目标被重分配，而用户完全不知道为什么。
        if (!string.IsNullOrWhiteSpace(a.Organization)
            && Enum.TryParse<WorkOrganization>(a.Organization.Trim(), ignoreCase: true, out var org))
        {
            cfg.Organization = org;
            if (org != WorkOrganization.Balanced) parts.Add($"作业组织 {OrgLabel(org)}");
        }

        return parts.Count == 0 ? "" : "编制锚点：" + string.Join(" · ", parts);
    }

    /// <summary>
    /// 把逐受矿点的入仓标准盖到去向登记簿上。<b>必须单独一步</b>：
    /// <see cref="ApplyTo"/> 跑在盘子装配的 ①.5，那时 <c>cfg.Sinks</c> 还是空的（去向登记簿是第 ⑤ 步才载入），
    /// 在那里盖等于盖了个寂寞 —— 而且不会报任何错。
    /// <para>返回一句来源文案（一个点都没盖到时返回空串）。台账自带标准的点<b>不覆盖</b>：
    /// 锚点是给"台账还没有这几列"用的补位，台账真有了就该以台账为准。</para>
    /// </summary>
    public static string ApplySinkBlend(ExploderConfig? cfg)
    {
        if (cfg?.Sinks == null) return "";
        var a = Current;
        if (a.SinkBlend.Count == 0) return "";

        int hit = 0, miss = 0;
        foreach (var (key, lim) in a.SinkBlend)
        {
            if (lim == null || lim.IsEmpty) continue;
            SinkNode? s;
            try
            {
                s = cfg.Sinks.Find(key)
                 ?? cfg.Sinks.All.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
            }
            catch { s = null; }

            if (s == null) { miss++; continue; }
            if (s.HasBlendLimits) continue;                 // 台账自带的不覆盖

            s.MaxAshPct = lim.MaxAshPct;
            s.MinCalorificMJkg = lim.MinCalorificMJkg;
            s.MaxSulfurPct = lim.MaxSulfurPct;
            hit++;
        }

        if (hit == 0 && miss == 0) return "";
        // 对不上的键要报出来：去向台账改过名/换过 id 之后，锚点会**静默失效**，
        // 而配煤照样按全矿级判并报「达标」—— 那是最像正常的一种坏。
        return $"逐去向配煤锚点：{hit} 个受矿点已套用"
             + (miss > 0 ? $"；⚠ {miss} 个键在去向登记簿里找不到（台账改过名或换过 id，这几条现在不生效）" : "");
    }

    /// <summary>策略的中文名（界面与来源文案共用一份，别在两处各写各的）。</summary>
    public static string OrgLabel(WorkOrganization org) => org switch
    {
        WorkOrganization.Concentrated => "集中强采型",
        WorkOrganization.MultiFace => "多面展开型",
        _ => "均衡型",
    };

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
