using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PitMine3D.Kylin.Views;
using Xunit;
using DMC = Dock.Model.Mvvm.Controls;
using DCore = Dock.Model.Core;

namespace PitMine3D.Kylin.Tests;

/// <summary>页面布置记录：捕获 → JSON → 重建 往返一致；坏记录被拒绝；空组/多余分隔条清掉。</summary>
public class DockLayoutStoreTests
{
    private static readonly string[] Required = { "Props", "Assistant", "Info" };

    private static (DMC.RootDock root, Dictionary<string, DMC.Tool> tools, DMC.DocumentDock doc, Dock.Model.Mvvm.Factory f) DefaultLayout()
    {
        var f = new Dock.Model.Mvvm.Factory();
        var tools = new Dictionary<string, DMC.Tool>();
        foreach (var id in new[] { "File", "Objects", "Props", "Assistant", "Info" }) tools[id] = new DMC.Tool { Id = id, Title = id };
        var doc = new DMC.DocumentDock { Proportion = 0.70, VisibleDockables = f.CreateList<DCore.IDockable>() };
        var left = new DMC.ToolDock { Alignment = DCore.Alignment.Left, Proportion = 0.14, ActiveDockable = tools["File"], VisibleDockables = f.CreateList<DCore.IDockable>(tools["File"], tools["Objects"]) };
        var right = new DMC.ToolDock { Alignment = DCore.Alignment.Right, Proportion = 0.16, ActiveDockable = tools["Props"], VisibleDockables = f.CreateList<DCore.IDockable>(tools["Props"], tools["Assistant"]) };
        var main = new DMC.ProportionalDock { Orientation = DCore.Orientation.Horizontal, Proportion = 0.84, VisibleDockables = f.CreateList<DCore.IDockable>(left, new DMC.ProportionalDockSplitter(), doc, new DMC.ProportionalDockSplitter(), right) };
        var bottom = new DMC.ToolDock { Alignment = DCore.Alignment.Bottom, Proportion = 0.16, ActiveDockable = tools["Info"], VisibleDockables = f.CreateList<DCore.IDockable>(tools["Info"]) };
        var vert = new DMC.ProportionalDock { Orientation = DCore.Orientation.Vertical, VisibleDockables = f.CreateList<DCore.IDockable>(main, new DMC.ProportionalDockSplitter(), bottom) };
        var root = new DMC.RootDock { Id = "Root", ActiveDockable = vert, DefaultDockable = vert, VisibleDockables = f.CreateList<DCore.IDockable>(vert) };
        return (root, tools, doc, f);
    }

    private static string Json(DockLayoutStore.LayoutFile lf) => JsonSerializer.Serialize(lf, DockLayoutStore.Json);

    [Fact]
    public void Capture_serialize_rebuild_round_trips_exactly()
    {
        var (root, tools, doc, f) = DefaultLayout();
        var lf = DockLayoutStore.Capture(root)!;
        Assert.Equal("[Left 0.14 File*/Objects] [Doc 0.7] [Right 0.16 Props*/Assistant] [Bottom 0.16 Info*]", DockLayoutStore.Describe(lf));
        string json = Json(lf);
        Assert.DoesNotContain("NaN", json);                   // 未定比例存 null, 不能写出 NaN

        var (_, tools2, doc2, f2) = DefaultLayout();
        var back = JsonSerializer.Deserialize<DockLayoutStore.LayoutFile>(json, DockLayoutStore.Json)!;
        var top = DockLayoutStore.Rebuild(back, f2, tools2, doc2, Required, out var pinned);
        Assert.Empty(pinned);
        var root2 = new DMC.RootDock { VisibleDockables = f2.CreateList<DCore.IDockable>(top) };
        Assert.Equal(json, Json(DockLayoutStore.Capture(root2)!));   // 重建后再捕获, 与记录逐字一致
    }

    [Fact]
    public void Rearranged_layout_restores_tool_placement_active_page_and_proportions()
    {
        var (root, tools, doc, f) = DefaultLayout();
        // 对象管理器挪到右侧组、右侧当前页切到 AI 助手、左栏加宽
        f.InitLayout(root);   // Owner 由 InitLayout 填, 之前是 null
        var left = (DMC.ToolDock)tools["Objects"].Owner!; var right = (DMC.ToolDock)tools["Props"].Owner!;
        f.MoveDockable(left, right, tools["Objects"], null);
        f.SetActiveDockable(tools["Assistant"]);
        left.Proportion = 0.25;
        var lf = DockLayoutStore.Capture(root)!;
        Assert.Equal("[Left 0.25 File*] [Doc 0.7] [Right 0.16 Props/Objects/Assistant*] [Bottom 0.16 Info*]", DockLayoutStore.Describe(lf));

        var (_, tools2, doc2, f2) = DefaultLayout();
        var top = DockLayoutStore.Rebuild(lf, f2, tools2, doc2, Required, out _);
        var root2 = new DMC.RootDock { VisibleDockables = f2.CreateList<DCore.IDockable>(top) };
        Assert.Equal(DockLayoutStore.Describe(lf), DockLayoutStore.Describe(DockLayoutStore.Capture(root2)));
        Assert.Same(doc2, FindDoc(top));                       // 文档区用的是本次启动新建的那个
    }

    [Fact]
    public void Closed_panel_stays_closed_and_pins_are_reported()
    {
        var (root, tools, doc, f) = DefaultLayout();
        f.InitLayout(root);
        f.CloseDockable(tools["Objects"]);                     // 用户关掉了对象管理器
        var lf = DockLayoutStore.Capture(root)!;
        lf.PinnedRight = new List<string> { "Assistant" };     // 记录里 AI 助手钉在右侧
        var (_, tools2, doc2, f2) = DefaultLayout();
        var top = DockLayoutStore.Rebuild(lf, f2, tools2, doc2, Required, out var pinned);
        var back = DockLayoutStore.Capture(new DMC.RootDock { VisibleDockables = f2.CreateList<DCore.IDockable>(top) })!;
        Assert.DoesNotContain("Objects", DockLayoutStore.Describe(back));
        Assert.Single(pinned);
        Assert.Equal(("Assistant"), pinned[0].id);
        Assert.Equal(DCore.Alignment.Right, pinned[0].side);
    }

    [Fact]
    public void Missing_required_panel_or_duplicate_document_is_rejected()
    {
        var (root, tools, doc, f) = DefaultLayout();
        var lf = DockLayoutStore.Capture(root)!;
        // 抹掉信息栏
        var bottom = lf.Root!.Children![2]; bottom.Tools!.Clear();
        var (_, tools2, doc2, f2) = DefaultLayout();
        Assert.Throws<InvalidDataException>(() => DockLayoutStore.Rebuild(lf, f2, tools2, doc2, Required, out _));

        var lf2 = DockLayoutStore.Capture(root)!;
        lf2.Root!.Children!.Add(new DockLayoutStore.LayoutNode { Kind = "Document" });   // 第二个文档区
        Assert.Throws<InvalidDataException>(() => DockLayoutStore.Rebuild(lf2, f2, tools2, doc2, Required, out _));
    }

    [Fact]
    public void Unknown_ids_and_empty_groups_are_dropped_with_their_splitters()
    {
        var (root, tools, doc, f) = DefaultLayout();
        var lf = DockLayoutStore.Capture(root)!;
        var main = lf.Root!.Children![0];
        main.Children![0].Tools = new List<string> { "Ghost", "File" };            // 未知面板 Ghost 跳过
        main.Children![4].Tools = new List<string> { "Nope" };                     // 右侧组全是未知 → 整组连同分隔条丢掉
        lf.Root.Children[2].Tools = new List<string> { "Info", "Props", "Assistant" };   // 不可关的挪到底部, 校验仍过
        var (_, tools2, doc2, f2) = DefaultLayout();
        var top = DockLayoutStore.Rebuild(lf, f2, tools2, doc2, Required, out _);
        var back = DockLayoutStore.Capture(new DMC.RootDock { VisibleDockables = f2.CreateList<DCore.IDockable>(top) })!;
        Assert.Equal("[Left 0.14 File*] [Doc 0.7] [Bottom 0.16 Info*/Props/Assistant]", DockLayoutStore.Describe(back));
        var mainBack = back.Root!.Children![0];
        Assert.Equal(new[] { "Tool", "Splitter", "Document" }, mainBack.Children!.Select(c => c.Kind));   // 尾部悬空分隔条已去
    }

    private static DMC.DocumentDock? FindDoc(DCore.IDockable d)
    {
        if (d is DMC.DocumentDock dd) return dd;
        if (d is DCore.IDock dk && dk.VisibleDockables != null)
            foreach (var c in dk.VisibleDockables) { var r = FindDoc(c); if (r != null) return r; }
        return null;
    }
}
