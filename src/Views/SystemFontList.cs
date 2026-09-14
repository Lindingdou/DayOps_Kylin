using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「字体」下拉的条目表 —— 对应原版 Ribbon 注释组的 comboTextFont(全局切换文字显示字体)：
/// 首项「原始 (按图纸)」= 取消覆盖回自动探测; 其后是**本机已装**的字体, 常用中文字体置顶并显示中文名,
/// 其余按显示名排序。原版列的是 Windows 字体, 麒麟上装的是思源/文泉驿一类, 故中文名表两边都收。
///
/// 排序/取名是纯逻辑(<see cref="Build"/> 可单测); 真去问系统装了什么字体的是 <see cref="FromSystem"/>。
/// </summary>
public static class SystemFontList
{
    /// <summary>首项：取消全局覆盖, 文字按图纸/自动探测的字体渲染。</summary>
    public const string OriginalLabel = "原始 (按图纸)";

    /// <summary>
    /// 常用中文字体置顶顺序 + 中文名。前段是麒麟/统信上常见的开源中文字体, 后段是 Windows 自带 ——
    /// 两个平台各取所需, 装了才会出现在下拉里。
    /// </summary>
    public static readonly (string Family, string Cn)[] PinnedChinese =
    {
        ("Noto Sans CJK SC", "思源黑体"),
        ("Source Han Sans CN", "思源黑体 CN"),
        ("Noto Serif CJK SC", "思源宋体"),
        ("Source Han Serif CN", "思源宋体 CN"),
        ("WenQuanYi Zen Hei", "文泉驿正黑"),
        ("WenQuanYi Micro Hei", "文泉驿微米黑"),
        ("AR PL UMing CN", "文鼎明体"),
        ("AR PL UKai CN", "文鼎楷体"),
        ("Droid Sans Fallback", "Droid 中文"),
        ("SimSun", "宋体"),
        ("NSimSun", "新宋体"),
        ("SimHei", "黑体"),
        ("Microsoft YaHei", "微软雅黑"),
        ("KaiTi", "楷体"),
        ("FangSong", "仿宋"),
        ("DengXian", "等线"),
        ("LiSu", "隶书"),
        ("YouYuan", "幼圆"),
        ("STSong", "华文宋体"),
        ("STKaiti", "华文楷体"),
        ("STFangsong", "华文仿宋"),
        ("STHeiti", "华文黑体"),
        ("STZhongsong", "华文中宋"),
        ("Microsoft JhengHei", "微软正黑体"),
    };

    /// <summary>
    /// 由「已装字体族名」生成下拉条目 (显示名, 字体名)。首项恒为「原始 (按图纸)」(字体名为空);
    /// 常用中文字体按 <see cref="PinnedChinese"/> 顺序置顶显示为"中文名 Family"; 其余去重后按名排序。
    /// </summary>
    public static List<(string Display, string Family)> Build(IEnumerable<string>? installed)
    {
        var items = new List<(string, string)> { (OriginalLabel, "") };
        if (installed == null) return items;

        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in installed)
            if (!string.IsNullOrWhiteSpace(f)) have.Add(f.Trim());

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (family, cn) in PinnedChinese)
        {
            if (!have.Contains(family) || !used.Add(family)) continue;
            items.Add(($"{cn} {family}", family));
        }

        foreach (var f in have.Where(f => !used.Contains(f))
                              .OrderBy(f => f, StringComparer.CurrentCulture))
            items.Add((f, f));

        return items;
    }

    /// <summary>问 Avalonia 要本机已装字体, 再交给 <see cref="Build"/> 排。取不到就只给首项。</summary>
    public static List<(string Display, string Family)> FromSystem()
    {
        try
        {
            var names = Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name);
            return Build(names);
        }
        catch (Exception ex)
        {
            PitMine3D.Kylin.CrashLog.Write("字体", $"枚举系统字体失败: {ex.Message}");
            return Build(null);
        }
    }
}
