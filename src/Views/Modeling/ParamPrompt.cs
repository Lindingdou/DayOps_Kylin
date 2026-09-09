using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 命令行参数问答的纯逻辑：把一个 <see cref="PromptDialog.Field"/> 渲染成 AutoCAD 式提示行，
/// 并把用户键入的一行归一化成该项的取值。无 UI 依赖，可单测。
///
/// 提示行格式沿用 AutoCAD：<c>标签(单位) [选项1/选项2] &lt;默认值&gt;</c>；
/// 直接回车 = 取默认值；选项可键入 全名 / 序号 / 唯一前缀。
/// </summary>
public static class ParamPrompt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>是/否项在命令行上的两个选项词（对话框里是复选框）。</summary>
    public static readonly string[] BoolChoices = { "是", "否" };

    /// <summary>该项在命令行上的候选词；非选项/非布尔项返回空。</summary>
    public static string[] ChoicesOf(PromptDialog.Field f)
        => f.Bool ? BoolChoices : (f.Choices ?? Array.Empty<string>());

    /// <summary>提示行（不含末尾冒号）：<c>最大间距(m) &lt;10&gt;</c> / <c>保留侧 [圈内/圈外] &lt;圈内&gt;</c>。</summary>
    public static string PromptText(PromptDialog.Field f)
    {
        string s = f.Label;
        if (!string.IsNullOrWhiteSpace(f.Unit)) s += $"({f.Unit})";
        var ch = ChoicesOf(f);
        if (ch.Length > 0) s += " [" + string.Join("/", ch) + "]";
        string def = DefaultOf(f);
        if (def.Length > 0) s += $" <{def}>";
        return s;
    }

    /// <summary>该项的默认值（布尔项统一成 是/否）。</summary>
    public static string DefaultOf(PromptDialog.Field f)
    {
        if (!f.Bool) return f.Default ?? "";
        return f.Default is "是" or "true" or "True" or "1" ? "是" : "否";
    }

    /// <summary>
    /// 把键入的一行归一化成取值。空行 = 取默认值。
    /// 失败时 <paramref name="error"/> 给出可直接回显的原因。
    /// </summary>
    public static bool TryNormalize(PromptDialog.Field f, string input, out string value, out string error)
    {
        error = "";
        string s = (input ?? "").Trim();
        if (s.Length == 0) s = DefaultOf(f);

        if (f.Bool)
        {
            switch (s.ToLowerInvariant())
            {
                case "是": case "y": case "yes": case "true": case "1": case "t": value = "是"; return true;
                case "否": case "n": case "no": case "false": case "0": case "f": value = "否"; return true;
                default: value = ""; error = $"「{f.Label}」请输入 是/否（或 Y/N）"; return false;
            }
        }

        var ch = f.Choices;
        if (ch != null && ch.Length > 0)
        {
            var hit = ch.FirstOrDefault(c => string.Equals(c, s, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { value = hit; return true; }
            // 序号(1 起)：选项多是中文，敲序号比敲全名快
            if (int.TryParse(s, NumberStyles.Integer, Inv, out int idx) && idx >= 1 && idx <= ch.Length)
            { value = ch[idx - 1]; return true; }
            // 唯一前缀
            var pre = ch.Where(c => c.StartsWith(s, StringComparison.OrdinalIgnoreCase)).ToList();
            if (pre.Count == 1) { value = pre[0]; return true; }
            value = "";
            error = pre.Count > 1
                ? $"「{f.Label}」{s} 不唯一，可选：{string.Join(" / ", pre)}"
                : $"「{f.Label}」请从 {string.Join(" / ", ch)} 中选（可键入序号 1~{ch.Length}）";
            return false;
        }

        if (f.Numeric && !double.TryParse(s, NumberStyles.Float, Inv, out _))
        { value = ""; error = $"「{f.Label}」须为数值"; return false; }

        value = s;
        return true;
    }

    /// <summary>命令行同行给的位置参数：按空格/逗号切分命令词之后的部分。</summary>
    public static List<string> SplitArgs(string rest)
        => (rest ?? "").Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
}
