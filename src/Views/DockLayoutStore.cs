using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DMC = Dock.Model.Mvvm.Controls;
using DCore = Dock.Model.Core;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 页面布置(停靠排布)的骨架模型 + 捕获/重建 —— 纯视图模型层、可单测；文件读写与事件挂接在 MainWindow.DockLayout.cs。
///
/// 只记我们关心的骨架：比例容器(方向/比例) · 工具停靠(方位/比例/面板 Id 顺序/当前页) · 文档区(比例) · 分隔条 ·
/// 各侧钉住的面板。面板按 Id 认，文档标签不记。重建时：未知/重复 Id 跳过、空工具组连同多余分隔条一起丢、
/// 只剩一个子容器就提上来；调用方再校验"文档区恰一处 + 不可关面板都在"。
/// </summary>
internal static class DockLayoutStore
{
    public sealed class LayoutNode
    {
        public string Kind { get; set; } = "";          // Proportional / Tool / Document / Splitter
        public string? Orientation { get; set; }        // Proportional: Horizontal / Vertical
        public double? Proportion { get; set; }          // NaN(未定) 存 null, JSON 写不了 NaN
        public string? Alignment { get; set; }          // Tool: Left / Right / Top / Bottom
        public string? Active { get; set; }             // Tool: 当前页面板 Id
        public List<string>? Tools { get; set; }        // Tool: 面板 Id 顺序
        public List<LayoutNode>? Children { get; set; } // Proportional 子项
    }

    public sealed class LayoutFile
    {
        public int Version { get; set; } = 1;
        public LayoutNode? Root { get; set; }
        public List<string>? PinnedLeft { get; set; }
        public List<string>? PinnedRight { get; set; }
        public List<string>? PinnedTop { get; set; }
        public List<string>? PinnedBottom { get; set; }
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ── 捕获 ──
    public static LayoutFile? Capture(DCore.IDock? layout)
    {
        if (layout is not DMC.RootDock root || root.VisibleDockables == null || root.VisibleDockables.Count == 0) return null;
        var node = CaptureNode(root.VisibleDockables[0]);
        if (node == null) return null;
        return new LayoutFile
        {
            Root = node,
            PinnedLeft = Ids(root.LeftPinnedDockables), PinnedRight = Ids(root.RightPinnedDockables),
            PinnedTop = Ids(root.TopPinnedDockables), PinnedBottom = Ids(root.BottomPinnedDockables),
        };
    }

    private static List<string>? Ids(IList<DCore.IDockable>? list)
        => list == null || list.Count == 0 ? null : list.OfType<DMC.Tool>().Select(t => t.Id).ToList();

    public static LayoutNode? CaptureNode(DCore.IDockable d)
    {
        switch (d)
        {
            case DMC.ProportionalDockSplitter:
                return new LayoutNode { Kind = "Splitter" };
            case DMC.ProportionalDock p:
                return new LayoutNode
                {
                    Kind = "Proportional", Orientation = p.Orientation.ToString(), Proportion = Prop(p.Proportion),
                    Children = (p.VisibleDockables ?? new List<DCore.IDockable>()).Select(CaptureNode).Where(x => x != null).Select(x => x!).ToList(),
                };
            case DMC.ToolDock t:
                return new LayoutNode
                {
                    Kind = "Tool", Alignment = t.Alignment.ToString(), Proportion = Prop(t.Proportion), Active = t.ActiveDockable?.Id,
                    Tools = (t.VisibleDockables ?? new List<DCore.IDockable>()).OfType<DMC.Tool>().Select(x => x.Id).ToList(),
                };
            case DMC.DocumentDock dd:
                return new LayoutNode { Kind = "Document", Proportion = Prop(dd.Proportion) };
            default:
                return null;
        }
    }

    private static double? Prop(double v) => double.IsNaN(v) || double.IsInfinity(v) || v <= 0 ? null : v;
    private static double PropOr(double? v, double fallback) => v is double d && d > 0 && !double.IsInfinity(d) ? d : fallback;

    // ── 重建 ──
    /// <summary>
    /// 由记录重建顶层容器。<paramref name="required"/> 里的面板必须都出现、文档区必须恰好一处, 否则抛 InvalidDataException。
    /// <paramref name="pinned"/> 回报记录里钉住且确实在布局中的面板。
    /// </summary>
    public static DCore.IDock Rebuild(LayoutFile lf, DCore.IFactory f, IReadOnlyDictionary<string, DMC.Tool> tools,
        DMC.DocumentDock docDock, IEnumerable<string> required, out List<(DCore.Alignment side, string id)> pinned)
    {
        if (lf.Root == null) throw new InvalidDataException("空布局");
        var used = new HashSet<string>(); int docs = 0;
        var top = BuildNode(lf.Root, f, tools, docDock, used, ref docs) as DCore.IDock;
        if (top == null) throw new InvalidDataException("顶层不是容器");
        if (docs != 1) throw new InvalidDataException($"文档区出现 {docs} 次");
        foreach (var must in required)
            if (!used.Contains(must)) throw new InvalidDataException($"缺少不可关面板 {must}");
        var pins = new List<(DCore.Alignment side, string id)>();
        void Pin(List<string>? ids, DCore.Alignment side) { if (ids != null) foreach (var id in ids) if (used.Contains(id)) pins.Add((side, id)); }
        Pin(lf.PinnedLeft, DCore.Alignment.Left); Pin(lf.PinnedRight, DCore.Alignment.Right);
        Pin(lf.PinnedTop, DCore.Alignment.Top); Pin(lf.PinnedBottom, DCore.Alignment.Bottom);
        pinned = pins;
        return top;
    }

    private static DCore.IDockable? BuildNode(LayoutNode n, DCore.IFactory f, IReadOnlyDictionary<string, DMC.Tool> tools,
        DMC.DocumentDock docDock, HashSet<string> used, ref int docs)
    {
        switch (n.Kind)
        {
            case "Splitter":
                return new DMC.ProportionalDockSplitter();
            case "Document":
                docs++;
                docDock.Proportion = PropOr(n.Proportion, docDock.Proportion);
                return docDock;
            case "Tool":
            {
                var list = new List<DCore.IDockable>();
                foreach (var id in n.Tools ?? new List<string>())
                    if (tools.TryGetValue(id, out var t) && used.Add(id)) list.Add(t);   // 未知/重复 Id 跳过
                if (list.Count == 0) return null;                                        // 空工具组连同相邻分隔条一起丢(见下)
                var td = new DMC.ToolDock
                {
                    Alignment = Enum.TryParse<DCore.Alignment>(n.Alignment, out var al) ? al : DCore.Alignment.Unset,
                    Proportion = PropOr(n.Proportion, double.NaN),
                    VisibleDockables = f.CreateList<DCore.IDockable>(list.ToArray()),
                };
                td.ActiveDockable = list.FirstOrDefault(x => x.Id == n.Active) ?? list[0];
                return td;
            }
            case "Proportional":
            {
                var kids = new List<DCore.IDockable>();
                foreach (var c in n.Children ?? new List<LayoutNode>())
                {
                    var k = BuildNode(c, f, tools, docDock, used, ref docs);
                    if (k != null) kids.Add(k);
                }
                // 分隔条只能夹在两个实体之间：去掉首尾与连续重复的分隔条(空工具组被丢后会留下这种)
                var clean = new List<DCore.IDockable>();
                foreach (var k in kids)
                {
                    bool sp = k is DMC.ProportionalDockSplitter;
                    if (sp && (clean.Count == 0 || clean[^1] is DMC.ProportionalDockSplitter)) continue;
                    clean.Add(k);
                }
                while (clean.Count > 0 && clean[^1] is DMC.ProportionalDockSplitter) clean.RemoveAt(clean.Count - 1);
                if (clean.Count == 0) return null;
                if (clean.Count == 1 && clean[0] is DCore.IDock only && only is not DMC.DocumentDock)
                {
                    // 只剩一个子容器: 直接提上来(比例沿用本层的), 免得多包一层
                    only.Proportion = PropOr(n.Proportion, only.Proportion);
                    return only;
                }
                return new DMC.ProportionalDock
                {
                    Orientation = Enum.TryParse<DCore.Orientation>(n.Orientation, out var o) ? o : DCore.Orientation.Horizontal,
                    Proportion = PropOr(n.Proportion, double.NaN),
                    VisibleDockables = f.CreateList<DCore.IDockable>(clean.ToArray()),
                };
            }
            default:
                return null;
        }
    }

    /// <summary>一行文字描述排布(自检/诊断)：每个工具组 [方位 比例 面板…*当前]、文档区 [Doc 比例]。</summary>
    public static string Describe(LayoutFile? lf)
    {
        if (lf?.Root == null) return "(空)";
        var parts = new List<string>();
        void Walk(LayoutNode n)
        {
            if (n.Kind == "Tool")
                parts.Add($"[{n.Alignment} {n.Proportion?.ToString("0.##") ?? "-"} {string.Join("/", (n.Tools ?? new()).Select(t => t == n.Active ? t + "*" : t))}]");
            else if (n.Kind == "Document") parts.Add($"[Doc {n.Proportion?.ToString("0.##") ?? "-"}]");
            foreach (var c in n.Children ?? new()) Walk(c);
        }
        Walk(lf.Root);
        return string.Join(" ", parts);
    }
}
