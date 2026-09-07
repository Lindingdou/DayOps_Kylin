using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// AutoCAD 索引色(ACI)标准色板 —— 对齐原版 Ribbon「特性」组的 EntityColorPicker。
/// 原版取色器给 随层/随块 + ACI 索引色板 + 自定义 RGB; Kylin 实体只存 RGB 浮点,
/// 没有随块概念, 故「随层」实现为"取所在图层的颜色", 其余照 ACI 前 9 号 + 灰阶。
/// 纯逻辑、可单测。
/// </summary>
public static class AciPalette
{
    /// <summary>ACI 索引 → (中文名, R, G, B)。取 AutoCAD 标准前 9 号 + 常用灰阶。</summary>
    public static readonly (int aci, string name, byte r, byte g, byte b)[] Entries =
    {
        (1, "红",   255,   0,   0),
        (2, "黄",   255, 255,   0),
        (3, "绿",     0, 255,   0),
        (4, "青",     0, 255, 255),
        (5, "蓝",     0,   0, 255),
        (6, "洋红", 255,   0, 255),
        (7, "白",   255, 255, 255),
        (8, "深灰", 128, 128, 128),
        (9, "浅灰", 192, 192, 192),
        (30, "橙",  255, 127,   0),
        (92, "草绿",127, 255,   0),
        (140,"天蓝",  0, 127, 255),
    };

    /// <summary>ACI 索引 → 浮点 RGB; 未知索引返回 null。</summary>
    public static (float r, float g, float b)? ByIndex(int aci)
    {
        foreach (var e in Entries)
            if (e.aci == aci) return (e.r / 255f, e.g / 255f, e.b / 255f);
        return null;
    }

    /// <summary>浮点 RGB → "#RRGGBB"。</summary>
    public static string Hex(float r, float g, float b) =>
        $"#{(int)Math.Round(Math.Clamp(r, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(g, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(b, 0, 1) * 255):X2}";

    /// <summary>该 RGB 若正好命中色板则给中文名, 否则给 "#RRGGBB"。取色器关闭态的显示文本。</summary>
    public static string DisplayName(float r, float g, float b)
    {
        foreach (var e in Entries)
            if (Near(r, e.r) && Near(g, e.g) && Near(b, e.b)) return e.name;
        return Hex(r, g, b);
        static bool Near(float v, byte b255) => Math.Abs(v * 255 - b255) < 0.6;
    }

    /// <summary>解析 "#RRGGBB" / "RRGGBB" / "r,g,b"(0..255) / ACI 索引号; 失败返回 false。</summary>
    public static bool TryParse(string text, out float r, out float g, out float b)
    {
        r = g = b = 0;
        string t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        if (t.StartsWith("#")) t = t.Substring(1);
        if ((t.Length == 6) && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
        { r = ((v >> 16) & 0xFF) / 255f; g = ((v >> 8) & 0xFF) / 255f; b = (v & 0xFF) / 255f; return true; }
        var p = t.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 3
            && int.TryParse(p[0], out int ri) && int.TryParse(p[1], out int gi) && int.TryParse(p[2], out int bi))
        { r = Math.Clamp(ri, 0, 255) / 255f; g = Math.Clamp(gi, 0, 255) / 255f; b = Math.Clamp(bi, 0, 255) / 255f; return true; }
        if (p.Length == 1 && int.TryParse(p[0], out int aci) && ByIndex(aci) is { } c)
        { r = c.r; g = c.g; b = c.b; return true; }
        // 中文色名(色板里的)
        foreach (var e in Entries)
            if (e.name == t) { r = e.r / 255f; g = e.g / 255f; b = e.b / 255f; return true; }
        return false;
    }
}

/// <summary>
/// 特性面板在「非单选」时显示的内容 —— 对齐原版:
/// 多选 → MultiSelectionProperties(总数量 + 分类计数); 空选 → DocumentProperties(文档/视图状态)。
/// 原版按 AcDb/Scene 两类计数, Kylin 只有托管场景实体, 故按实体类型计数(同一意图)。
/// 纯逻辑、可单测。
/// </summary>
public static class PropertyPanelModel
{
    /// <summary>多选统计行(分类, 标签, 值)。忠实原版「选择」分组: 先总数, 再逐类。</summary>
    public static List<(string cat, string label, string value)> MultiSelectionRows(IReadOnlyList<SceneEntity> sel)
    {
        var rows = new List<(string, string, string)> { ("选择", "总数量", sel.Count.ToString(CultureInfo.InvariantCulture)) };
        foreach (var grp in sel.GroupBy(EntityTypeName.Of).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            rows.Add(("选择", grp.Key, grp.Count().ToString(CultureInfo.InvariantCulture)));
        return rows;
    }

    /// <summary>空选时的文档属性行。忠实原版 DocumentProperties 的「文档」+「视图」两组。</summary>
    public static List<(string cat, string label, string value)> DocumentRows(
        string? docName, int entityCount, int selectedCount, bool ortho, bool snap, bool is3D)
    {
        return new List<(string, string, string)>
        {
            ("文档", "名称", string.IsNullOrWhiteSpace(docName) ? "未命名" : docName!),
            ("视图", "实体总数", entityCount.ToString(CultureInfo.InvariantCulture)),
            ("视图", "选择数量", selectedCount.ToString(CultureInfo.InvariantCulture)),
            ("视图", "正交模式", ortho ? "开" : "关"),
            ("视图", "捕捉模式", snap ? "开" : "关"),
            ("视图", "3D 视图", is3D ? "是" : "否"),
        };
    }
}
