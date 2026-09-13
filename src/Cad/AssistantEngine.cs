using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 智能助手引擎（菜单驱动引导）——忠实移植原 PitMine3D `MockAiEngine`：
/// 分类菜单(绘制/编辑/标注测量/视图/帮助) → 点选即执行对应命令 + 使用帮助 + 自由文本兜底。
/// 原为规则/对话树引擎(非 LLM), 纯逻辑可托管、可单测。命令的实际执行由宿主(MainWindow)负责。
/// </summary>
public sealed class AssistantEngine
{
    /// <summary>一个可点选项：显示标签 + 可选命令(点选即执行) + 跳转节点。</summary>
    public sealed record Option(string Label, string? Command, string Next);

    /// <summary>一次回复：正文 + 可点选项。</summary>
    public sealed record Reply(string Content, IReadOnlyList<Option> Options);

    private string _node = "welcome";

    public void Reset() => _node = "welcome";
    public string CurrentNode => _node;

    /// <summary>当前节点的回复(欢迎语进入主菜单)。</summary>
    public Reply Current() => NodeReply(_node);

    /// <summary>选择一项 → 跳到其目标节点, 返回新节点回复。命令(若有)由调用方执行。</summary>
    public Reply Select(Option opt)
    {
        _node = opt.Next;
        return NodeReply(_node);
    }

    /// <summary>自由文本：isCommand 判定能否作命令执行; 能则执行提示+主菜单, 否则兜底+主菜单。</summary>
    public Reply HandleText(string text, Func<string, bool> isCommand)
    {
        _node = "main_menu";
        string t = (text ?? "").Trim();
        if (t.Length > 0 && isCommand(t))
            return new Reply($"已为您执行：{t.ToUpperInvariant()} —— 请按视口/命令行提示继续。", MainMenu);
        return new Reply($"您说：\"{t}\"\n\n抱歉，我暂时只能识别命令名或预设功能。下面这些点一下就能直接执行：", MainMenu);
    }

    private static readonly IReadOnlyList<Option> MainMenu = new List<Option>
    {
        new("绘制图形", null, "draw_menu"),
        new("编辑修改", null, "edit_menu"),
        new("标注测量", null, "annotate_menu"),
        new("视图操作", null, "view_menu"),
        new("查看使用帮助", null, "help_text"),
    };

    private const string HelpText =
        "📖 DayOps 使用指南\n\n" +
        "• 绘制：圆 / 矩形 / 直线 / 多段线 / 多边形 / 点\n" +
        "• 编辑：移动 / 复制 / 旋转 / 缩放 / 镜像 / 偏移 / 修剪 / 删除\n" +
        "• 标注测量：对齐标注 / 半径标注 / 测距 / 测角\n" +
        "• 视图：缩放全部 / 平移 / 三维旋转 / 2D-3D 切换\n\n" +
        "提示：点上面的功能会直接执行；也可在输入框直接输入命令名（如 MOVE、CIRCLE）。";

    private static readonly Option Back = new("返回主菜单", null, "main_menu");

    private static Reply NodeReply(string node) => node switch
    {
        "welcome" or "main_menu" =>
            new Reply("您好！我是 PitMine 智能助手 🤖\n\n选择下面的功能，我可以直接帮您执行对应命令：", MainMenu),

        "draw_menu" => new Reply("选择要绘制的图形（点选后请在视口中操作）：", new List<Option>
        {
            new("圆", "CIRCLE", "draw_menu"), new("矩形", "RECTANG", "draw_menu"),
            new("直线", "LINE", "draw_menu"), new("多段线", "PLINE", "draw_menu"),
            new("多边形", "POLYGON", "draw_menu"), new("点", "POINT", "draw_menu"), Back,
        }),

        "edit_menu" => new Reply("选择编辑操作（点选后请先选中对象 / 按提示操作）：", new List<Option>
        {
            new("移动", "MOVE", "edit_menu"), new("复制", "COPY", "edit_menu"),
            new("旋转", "ROTATE", "edit_menu"), new("缩放", "SCALE", "edit_menu"),
            new("镜像", "MIRROR", "edit_menu"), new("偏移", "OFFSET", "edit_menu"),
            new("修剪", "TRIM", "edit_menu"), new("删除", "ERASE", "edit_menu"), Back,
        }),

        "annotate_menu" => new Reply("选择标注 / 测量（点选后请在视口中拾取点）：", new List<Option>
        {
            new("对齐标注", "DIMALIGNED", "annotate_menu"), new("半径标注", "DIMRADIAL", "annotate_menu"),
            new("测量距离", "DIST", "annotate_menu"), new("测量角度", "MANG", "annotate_menu"), Back,
        }),

        "view_menu" => new Reply("选择视图操作：", new List<Option>
        {
            new("缩放全部", "ZOOMEXTENTS", "view_menu"), new("平移", "PAN", "view_menu"),
            new("三维旋转", "3DORBIT", "view_menu"), new("2D/3D 切换", "3DVIEW", "view_menu"),
            new("Gizmo 手柄", "GIZMO", "view_menu"), Back,
        }),

        "help_text" => new Reply(HelpText, new List<Option> { Back }),

        _ => new Reply("您好！我是 PitMine 智能助手 🤖\n\n选择下面的功能：", MainMenu),
    };
}
