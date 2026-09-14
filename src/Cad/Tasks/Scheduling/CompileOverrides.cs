using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>一个受矿点的入仓煤质锚点。三项各自可空，缺项落回全矿级。</summary>
public sealed class SinkBlendAnchor
{
    public double? MaxAshPct { get; set; }
    public double? MinCalorificMJkg { get; set; }
    public double? MaxSulfurPct { get; set; }

    public bool IsEmpty => !MaxAshPct.HasValue && !MinCalorificMJkg.HasValue && !MaxSulfurPct.HasValue;

    public SinkBlendAnchor Clone() => new()
    { MaxAshPct = MaxAshPct, MinCalorificMJkg = MinCalorificMJkg, MaxSulfurPct = MaxSulfurPct };
}

/// <summary>
/// 编制配置的人工锚点。<b>每一项都是可空的</b> —— 没改过就保持 null，由引擎缺省接管。
/// <para>
/// <b>为什么不把缺省值也写进文件</b>（原版注释，照搬）：日后引擎调缺省时，这份旧文件会把老数字顶回来，
/// 那是最难查的一类回归 —— 它算得出来、不报错，只是和现在的口径不是一套。
/// </para>
/// </summary>
public sealed class CompileAnchors
{
    /// <summary>综合灰分上限 %。</summary>
    public double? MaxAshPct { get; set; }
    /// <summary>综合热值下限 MJ/kg。</summary>
    public double? MinCalorificMJkg { get; set; }
    /// <summary>综合硫上限 %。</summary>
    public double? MaxSulfurPct { get; set; }

    /// <summary>天气/路况降效 %（0..95）。<b>0 与 null 语义不同</b>：0 = 人工确认过今天不降效。</summary>
    public double? WeatherDeratePct { get; set; }

    /// <summary>备采保有下限（天）。0 = 人工确认不校核；null = 没设过（引擎缺省也是不校核）。</summary>
    public double? MinPreparedDays { get; set; }

    /// <summary>面日产能的有效工时 h/日（0.5..24）。null = 引擎缺省 20h。</summary>
    public double? EffHoursPerDay { get; set; }

    /// <summary>交接班损失 h/班（0..4）。<b>0 与 null 语义不同</b>：0 = 人工确认本矿不扣交接。</summary>
    public double? HandoverRampH { get; set; }

    // ── 按环节降效（null = 跟随 WeatherDeratePct 那个全盘值）──
    /// <summary>采装环节降效 %。</summary>
    public double? LoadDeratePct { get; set; }
    /// <summary>运输环节降效 %。</summary>
    public double? HaulDeratePct { get; set; }
    /// <summary>排土环节降效 %。</summary>
    public double? DumpDeratePct { get; set; }

    /// <summary>
    /// 逐受矿点的入仓煤质标准（键 = <c>SinkNode.Id</c>，退而求其次用 Name）。
    /// 真源应当是去向台账；台账还没有这几列，故先由本锚点承担。
    /// </summary>
    public Dictionary<string, SinkBlendAnchor> SinkBlend { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasBlend => MaxAshPct.HasValue || MinCalorificMJkg.HasValue || MaxSulfurPct.HasValue;
    public bool HasLinkDerate => LoadDeratePct.HasValue || HaulDeratePct.HasValue || DumpDeratePct.HasValue;

    public bool IsEmpty => !HasBlend && !WeatherDeratePct.HasValue && !MinPreparedDays.HasValue
                        && !EffHoursPerDay.HasValue && !HandoverRampH.HasValue
                        && !HasLinkDerate && SinkBlend.Count == 0;

    public CompileAnchors Clone() => new()
    {
        MaxAshPct = MaxAshPct, MinCalorificMJkg = MinCalorificMJkg, MaxSulfurPct = MaxSulfurPct,
        WeatherDeratePct = WeatherDeratePct, MinPreparedDays = MinPreparedDays,
        EffHoursPerDay = EffHoursPerDay, HandoverRampH = HandoverRampH,
        LoadDeratePct = LoadDeratePct, HaulDeratePct = HaulDeratePct, DumpDeratePct = DumpDeratePct,
        // 深拷：浅拷会让 Clone 出来的那份与原件共用同一个字典，「改了不保存」和「重置」都会顺手改掉原件
        SinkBlend = SinkBlend.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// 编制人工锚点的读写与套用（移植原 <c>TaskLib.Engine.CompileOverrides</c>）。
///
/// <para>
/// 原版要解决的问题（照搬）：这几个能改的数原先都是<b>"几处各一套、谁也不喂引擎"</b>的状态 ——
/// 质量标准窗写一套纯文案、编制配置窗是几个写死没绑定的输入框、引擎 <c>BlendStandard</c> 又有自己的缺省；
/// 装箱信的是最后那套，前面两个界面改了什么都不会发生。天气降效更彻底：界面上有输入框，
/// <c>ExploderConfig</c> 里根本没有对应字段，"天气恶劣→全盘降效回摊"落不下去。
/// 现在统一到本类：<b>界面改 → Save → 下次装盘时 ApplyTo 盖上去 → 引擎按它算</b>。
/// </para>
///
/// ── Kylin 侧的实现差异（登记）──
/// <list type="number">
///   <item>落盘走 <see cref="UserSettings"/>（进程内单例、麒麟上按用户目录落盘），不是原版那个
///     <c>%LOCALAPPDATA%/PitMine/compile_config.json</c> —— 同 §三三九 运输约束方案的处置，
///     少一条路就少一处"两份配置对不上"的来路。</item>
///   <item><b>作业组织策略（<c>Organization</c>）未移</b>：它要的是按策略重分配全盘目标那一段引擎逻辑，
///     Kylin 尚无。锚点里也<b>不放这一项</b> —— 存了却没人读，就又变回原版当初那个
///     "界面改了什么都不会发生"的状态。</item>
///   <item>原版 <c>Save</c> 会顺手 <c>ProductionPlanContext.Invalidate()</c> 作废缓存盘子；
///     Kylin 没有那层缓存，故不需要（调用方每次自行装盘）。</item>
/// </list>
/// </summary>
public static class CompileOverrides
{
    /// <summary>配置键（<see cref="UserSettings"/> 里的那一项）。</summary>
    public const string SettingsKey = "taskCompileAnchors";

    private static CompileAnchors? _cache;

    /// <summary>最近一次读写的结果文案（供 UI 显示"存到哪了 / 为什么没存上"）。</summary>
    public static string LastIoLabel { get; private set; } = "";

    /// <summary>当前锚点（首次访问读配置；读不到/读坏了返回空锚点，不抛）。</summary>
    public static CompileAnchors Current
    {
        get
        {
            if (_cache != null) return _cache;
            try
            {
                var a = UserSettings.Current.Get<CompileAnchors>(SettingsKey);
                _cache = a ?? new CompileAnchors();
                LastIoLabel = a != null ? "已读编制锚点" : "未保存过编制锚点，按引擎缺省";
            }
            catch (Exception ex)
            {
                _cache = new CompileAnchors();
                LastIoLabel = $"锚点读不出（{Short(ex)}），按引擎缺省";
            }
            return _cache;
        }
    }

    /// <summary>保存锚点。成功 true；失败 false 并把原因写进 <see cref="LastIoLabel"/>（不抛）。</summary>
    public static bool Save(CompileAnchors? anchors)
    {
        var a = anchors ?? new CompileAnchors();
        try
        {
            UserSettings.Current.Set(SettingsKey, a);
            UserSettings.Current.Flush();
            _cache = a.Clone();
            LastIoLabel = "已保存编制锚点";
            return true;
        }
        catch (Exception ex)
        {
            LastIoLabel = $"保存失败（{Short(ex)}）";
            return false;
        }
    }

    /// <summary>丢弃内存缓存（外部改过配置后调用）。</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>单测夹具：直接置入一份锚点，<b>不读盘也不写盘</b>。传 null 恢复"下次访问时读配置"。</summary>
    internal static void SetForTest(CompileAnchors? anchors)
    {
        _cache = anchors;
        LastIoLabel = anchors == null ? "" : "（单测夹具）";
    }

    /// <summary>
    /// 把锚点盖到盘子上，返回一句来源文案（没有任何锚点时返回空串，调用方据此不显示这一段）。
    /// <b>只盖人改过的那几项</b>：任一为 null 即保留引擎缺省，绝不用 0 去顶。
    /// </summary>
    /// <param name="anchors">要套的那一份；null = 当前锚点。界面「试算回显」传一份未保存的进来，
    /// <b>不必去动缓存</b> —— 为了预览而改全局状态，是最容易漏回滚的一种写法。</param>
    public static string ApplyTo(ExploderConfig? cfg, CompileAnchors? anchors = null)
    {
        if (cfg == null) return "";
        var a = anchors ?? Current;
        if (a.IsEmpty) return "";

        var parts = new List<string>();

        if (a.HasBlend)
        {
            cfg.Blend ??= new BlendStandard();
            if (a.MaxAshPct is { } ash) cfg.Blend.MaxAshPct = ash;
            if (a.MinCalorificMJkg is { } cv) cfg.Blend.MinCalorificMJkg = cv;
            if (a.MaxSulfurPct is { } su) cfg.Blend.MaxSulfurPct = su;
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

        // 分环节降效：null 的那项在引擎里跟随全盘值。这里**不做"没设就写全盘值"的填充** ——
        // 填了就分不出「跟随」与「人工确认与全盘同值」，而后者是有意义的
        //（日后调全盘值时，前者跟着走、后者不该跟）。
        if (a.LoadDeratePct is { } dl) cfg.LoadDeratePct = dl;
        if (a.HaulDeratePct is { } dh) cfg.HaulDeratePct = dh;
        if (a.DumpDeratePct is { } dd) cfg.DumpDeratePct = dd;
        if (a.HasLinkDerate)
            parts.Add($"分环节降效 采装 {cfg.LoadDeratePct ?? cfg.WeatherDeratePct:0.#}%"
                    + $"/运输 {cfg.HaulDeratePct ?? cfg.WeatherDeratePct:0.#}%"
                    + $"/排土 {cfg.DumpDeratePct ?? cfg.WeatherDeratePct:0.#}%");

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

        return parts.Count == 0 ? "" : "编制锚点：" + string.Join(" · ", parts);
    }

    /// <summary>
    /// 把逐受矿点的入仓标准盖到去向登记簿上。<b>必须单独一步</b>：装盘时去向登记簿比锚点晚载入，
    /// 在 <see cref="ApplyTo"/> 里盖等于盖了个寂寞 —— 而且不会报任何错。
    /// <para>台账自带标准的点<b>不覆盖</b>：锚点是给"台账还没有这几列"用的补位，台账真有了就以台账为准。</para>
    /// </summary>
    public static string ApplySinkBlend(SinkRegistry? sinks, CompileAnchors? anchors = null)
    {
        if (sinks == null) return "";
        var a = anchors ?? Current;
        if (a.SinkBlend.Count == 0) return "";

        int hit = 0, miss = 0;
        foreach (var (key, lim) in a.SinkBlend)
        {
            if (lim == null || lim.IsEmpty) continue;
            SinkNode? s;
            try
            {
                s = sinks.Find(key)
                 ?? sinks.All.FirstOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
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
        // 对不上的键要报出来：去向台账改过名/换过 id 之后锚点会**静默失效**，
        // 而配煤照样按全矿级判并报「达标」—— 那是最像正常的一种坏。
        return $"逐去向配煤锚点：{hit} 个受矿点已套用"
             + (miss > 0 ? $"；⚠ {miss} 个键在去向登记簿里找不到（台账改过名或换过 id，这几条现在不生效）" : "");
    }

    // ── 界面用的量程校验（改坏的数不该悄悄生效）───────────────────────────────

    /// <summary>把一格文本读成锚点值：空 = 没设过（null）；认不出或越界返回原因。</summary>
    public static string? ParseAnchor(string? text, double lo, double hi, string label, out double? value)
    {
        value = null;
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;                     // 空 = 不设，不是 0
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double v))
            return $"{label}「{t}」认不出（写个数，留空表示不设）。";
        if (v < lo || v > hi) return $"{label}要在 {lo:0.##}~{hi:0.##} 之间，填的是 {v:0.##}。";
        value = v;
        return null;
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
