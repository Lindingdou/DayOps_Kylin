using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 把系统字体的 TrueType 字形表喂给 <see cref="GlyphFont"/> —— 视口文字从 7 段笔画字体
/// 升级为真字形(含中文)。按候选字体名依次探测, 取到第一个能读出 glyf/loca/cmap 的即用。
/// </summary>
public static class GlyphFontHost
{
    /// <summary>已启用的字体名(诊断/状态栏用)；未启用为 null。</summary>
    public static string? ActiveFamily { get; private set; }

    /// <summary>挡住 GC 的字体引用(表已复制成托管数组, 这里只是再保一道)。</summary>
    private static IGlyphTypeface? _pinned;

    // 候选字体：忠实原版 StrokeFontRegistry —— 默认中文字体绑 **宋体(SimSun)**,
    // 麒麟上没有宋体, 故把麒麟常见中文字体排在前面, 再退 Windows 的宋体/黑体/雅黑。
    private static readonly string[] Candidates =
    {
        "Noto Sans CJK SC", "Source Han Sans CN", "WenQuanYi Zen Hei", "WenQuanYi Micro Hei",
        "Droid Sans Fallback", "AR PL UMing CN", "AR PL UKai CN", "Noto Sans CJK TC",
        "SimSun", "NSimSun", "SimHei", "Microsoft YaHei", "Microsoft YaHei UI", "KaiTi", "FangSong",
        "DejaVu Sans",
    };

    /// <summary>用来验证"这字体真有中文"的探针字(界面/注记里最常出现的字)。</summary>
    private const string CjkProbe = "钻煤孔层";

    public static void Install()
    {
        // ① 先按候选名找“确实含中文”的字体 —— 注意 Avalonia 找不到会静默换成默认字体(如 Segoe UI),
        //    所以必须用探针字复核, 否则会误认为已装上中文字体。
        foreach (var name in Candidates)
            if (TryUse(new Typeface(new FontFamily(name)), name, requireCjk: true)) return;

        // ② 候选名都不中: 遍历系统已安装字体, 找第一个含中文的(麒麟上字体族名不固定, 靠这步兜底)
        try
        {
            foreach (var fam in FontManager.Current.SystemFonts.Take(400))
                if (TryUse(new Typeface(fam), fam.Name, requireCjk: true)) return;
        }
        catch { }

        // ③ 实在没有中文字体: 退而求其次用拉丁真字形(英文/数字仍比笔画字体好看), 中文交回笔画字体(画不出)
        foreach (var name in Candidates)
            if (TryUse(new Typeface(new FontFamily(name)), name, requireCjk: false)) return;
        TryUse(Typeface.Default, "(默认字体)", requireCjk: false);
    }

    private static bool TryUse(Typeface tf, string label, bool requireCjk)
    {
        try
        {
            var gt = tf.GlyphTypeface;
            if (gt == null) return false;
            // 必需三张表齐全才算可用(CFF/OpenType 曲线字体没有 glyf, 直接跳过)
            foreach (var tag in new[] { "glyf", "loca", "cmap" })
                if (!gt.TryGetTable(TagOf(tag), out var t) || t == null || t.Length == 0) return false;

            // 一次性把要用的表**复制成托管字节数组**, 之后再不碰原生字体对象。
            // 此前是把 gt 捕获进静态委托、渲染时才回调 —— 原生字体一旦被释放, 再调用就是
            // 访问已释放内存, 进程直接段错误, 且时机不定(偶发闪退的由来)。
            var tables = new Dictionary<uint, byte[]>();
            foreach (var tag in new[] { "glyf", "loca", "cmap", "head", "maxp", "hhea", "hmtx" })
            {
                uint t = TagOf(tag);
                if (gt.TryGetTable(t, out var bytes) && bytes is { Length: > 0 })
                    tables[t] = bytes;   // TryGetTable 已返回托管副本, 不持有原生指针
            }
            _pinned = gt;   // 再保一道: 静态引用挡住 GC, 避免任何残留的原生访问踩空
            GlyphFont.Provider = tag => tables.TryGetValue(tag, out var b) ? b : null;
            GlyphFont.Reset();
            // Avalonia 找不到请求的字体会静默换成默认字体(Windows 上是 Segoe UI, 没有中文)——
            // 故必须用探针字复核, 否则会误判"已装上中文字体"。
            bool latinOk = GlyphFont.Outline('A') is { Count: > 0 };
            bool cjkOk = latinOk && CjkProbe.All(ch => GlyphFont.Outline(ch) is { Count: > 0 });
            if (!latinOk || (requireCjk && !cjkOk)) { GlyphFont.Provider = null; GlyphFont.Reset(); return false; }
            string got = gt.FamilyName;
            ActiveFamily = got + (cjkOk ? " (含中文)" : " (仅拉丁, 中文退笔画)") + (got == label ? "" : $" ← 请求 {label}");
            Console.Error.WriteLine($"[FONT] 视口文字使用真字形: {ActiveFamily}");
            return true;
        }
        catch { return false; }
    }

    private static uint TagOf(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];
}
