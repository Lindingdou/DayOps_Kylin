using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>对象管理器树（同原版 FileTreeViewModel）：面/线分根、图层级、差量刷新保状态、可见性总开关、块体模型根。</summary>
public class ObjectTreeViewModelTests
{
    private static MeshEntity Mesh(string name, string layer)
    {
        var m = new MeshEntity(name,
            new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) },
            new List<(int a, int b, int c)> { (0, 1, 2) });
        m.LayerName = layer;
        return m;
    }

    private static LineEntity Line(string layer) => new() { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, LayerName = layer };

    private static ObjectTreeNode Root(ObjectTreeViewModel vm, string name) => vm.RootNodes.Single(r => r.Name == name);

    [Fact]
    public void 空文档也有CAD对象到文件到图层0的骨架_面与块体根不显示()
    {
        var vm = new ObjectTreeViewModel();
        vm.Refresh(new List<SceneEntity>(), new LayerTable(), null, "视图1");

        Assert.Single(vm.RootNodes);
        var cad = Root(vm, ObjectTreeViewModel.CadRootName);
        Assert.Equal(ObjectTreeKind.Root, cad.Kind);
        Assert.True(cad.ShowVisibilityCheckbox);        // 顶层组 = 总开关
        var file = Assert.Single(cad.Children);
        Assert.Equal("视图1", file.Name);            // 未保存 → 视图名(同原版 视图{N})
        Assert.Equal(ObjectTreeKind.CadFile, file.Kind);
        Assert.False(file.ShowVisibilityCheckbox);
        var l0 = Assert.Single(file.Children);
        Assert.Equal("图层: 0", l0.Name);
        Assert.Equal("0", l0.LayerName);
        Assert.True(l0.ShowVisibilityCheckbox);
    }

    [Fact]
    public void 三角网进面模型根到对象级_线只到图层级_文件名取路径()
    {
        var layers = new LayerTable();
        layers.New("台阶线"); layers.New("地表");
        var ents = new List<SceneEntity> { Line("台阶线"), Line("台阶线"), Mesh("地表面", "地表"), Mesh("煤层顶", "地表") };
        var vm = new ObjectTreeViewModel();
        vm.Refresh(ents, layers, @"D:\mine\采剥图.pmx", "视图1");

        Assert.Equal(2, vm.RootNodes.Count);
        var cad = Root(vm, ObjectTreeViewModel.CadRootName);
        var file = Assert.Single(cad.Children);
        Assert.Equal("采剥图.pmx", file.Name);
        // CAD 组：图层 0(空) + 台阶线；"地表"层只有三角网 → 只在面模型组出现，不在 CAD 组重复挂空节点
        Assert.Equal(new[] { "0", "台阶线" }, file.Children.Select(c => c.LayerName).ToArray());
        Assert.All(file.Children, c => Assert.Empty(c.Children));   // 线类不展开实体

        var mesh = Root(vm, ObjectTreeViewModel.MeshRootName);
        Assert.Equal(1, vm.RootNodes.IndexOf(mesh));                // 顺序：CAD → 面模型
        var lyr = Assert.Single(mesh.Children);
        Assert.Equal("图层: 地表", lyr.Name);
        Assert.Equal(new[] { "地表面", "煤层顶" }, lyr.Children.Select(c => c.Name).ToArray());
        Assert.All(lyr.Children, c => { Assert.Equal(ObjectTreeKind.MeshEntity, c.Kind); Assert.False(c.ShowVisibilityCheckbox); });
        Assert.Same(ents[2], lyr.Children[0].Entity);
    }

    [Fact]
    public void 差量刷新保留节点实例与折叠状态_删掉的网从树里消失()
    {
        var layers = new LayerTable(); layers.New("地表");
        var a = Mesh("A", "地表"); var b = Mesh("B", "地表");
        var ents = new List<SceneEntity> { a, b };
        var vm = new ObjectTreeViewModel();
        vm.Refresh(ents, layers, null, null);
        var meshRoot = Root(vm, ObjectTreeViewModel.MeshRootName);
        var lyr = meshRoot.Children[0];
        lyr.IsExpanded = false;                                     // 用户折叠了
        var nodeA = lyr.Children[0];

        ents.Remove(b);
        vm.Refresh(ents, layers, null, null);

        Assert.Same(meshRoot, Root(vm, ObjectTreeViewModel.MeshRootName));   // 根/层/叶实例都保住
        Assert.Same(lyr, meshRoot.Children[0]);
        Assert.False(lyr.IsExpanded);
        Assert.Same(nodeA, Assert.Single(lyr.Children));

        ents.Remove(a);
        vm.Refresh(ents, layers, null, null);
        Assert.DoesNotContain(vm.RootNodes, r => r.Name == ObjectTreeViewModel.MeshRootName);   // 没网了 → 面模型根撤掉
    }

    [Fact]
    public void 图层勾选打到图层表_总开关一次全关且只重绘一次()
    {
        var layers = new LayerTable(); layers.New("A"); layers.New("B");
        var vm = new ObjectTreeViewModel();
        var changed = new List<(string, bool)>();
        int batchEnded = 0;
        vm.LayerVisibilityChanged = (n, v) => { layers.Get(n)!.Visible = v; changed.Add((n, v)); };
        vm.BatchEnded = () => batchEnded++;
        vm.Refresh(new List<SceneEntity>(), layers, null, null);
        var file = Root(vm, ObjectTreeViewModel.CadRootName).Children[0];

        file.Children.Single(c => c.LayerName == "A").IsContentVisible = false;   // 单层
        Assert.False(layers.Get("A")!.Visible);
        Assert.Equal(new[] { ("A", false) }, changed.ToArray());
        Assert.False(vm.InBatch);

        Root(vm, ObjectTreeViewModel.CadRootName).IsContentVisible = false;       // 总开关：剩下 0/B 一起关，A 已关不重复
        Assert.All(layers.Layers, l => Assert.False(l.Visible));
        Assert.Equal(new[] { ("A", false), ("0", false), ("B", false) }, changed.ToArray());
        Assert.Equal(1, batchEnded);                                              // 三层只统一重绘一次
        Assert.All(file.Children, c => Assert.False(c.IsContentVisible));

        Root(vm, ObjectTreeViewModel.CadRootName).IsContentVisible = true;        // 全开
        Assert.All(layers.Layers, l => Assert.True(l.Visible));
        Assert.Equal(2, batchEnded);
    }

    [Fact]
    public void 图层表在外面改了开关_刷新把勾选拉齐且不回打后端()
    {
        var layers = new LayerTable(); layers.New("A");
        var vm = new ObjectTreeViewModel();
        int calls = 0;
        vm.LayerVisibilityChanged = (_, _) => calls++;
        vm.Refresh(new List<SceneEntity>(), layers, null, null);
        var file = Root(vm, ObjectTreeViewModel.CadRootName).Children[0];
        var nodeA = file.Children.Single(c => c.LayerName == "A");
        Assert.True(nodeA.IsContentVisible);

        layers.AllOff();                                            // 功能区「全关」
        vm.Refresh(new List<SceneEntity>(), layers, null, null);
        Assert.False(nodeA.IsContentVisible);
        Assert.Equal(0, calls);                                     // 只拉齐树，不回打图层表
    }

    [Fact]
    public void 父组已关时新出现的图层跟随父组关掉()
    {
        var layers = new LayerTable();
        var vm = new ObjectTreeViewModel();
        vm.LayerVisibilityChanged = (n, v) => layers.Get(n)!.Visible = v;
        int batchEnded = 0; vm.BatchEnded = () => batchEnded++;
        vm.Refresh(new List<SceneEntity>(), layers, null, null);
        Root(vm, ObjectTreeViewModel.CadRootName).IsContentVisible = false;
        Assert.Equal(1, batchEnded);

        layers.New("新层");                                          // 父组 ☐ 之后新建的层
        vm.Refresh(new List<SceneEntity>(), layers, null, null);
        var node = Root(vm, ObjectTreeViewModel.CadRootName).Children[0].Children.Single(c => c.LayerName == "新层");
        Assert.False(node.IsContentVisible);
        Assert.False(layers.Get("新层")!.Visible);                   // 表里也关了，不会"父组 ☐、子图层 ☑"
        Assert.Equal(2, batchEnded);                                // 刷新期间的改动攒到最后重绘一次
    }

    [Fact]
    public void 块体模型根按模型列表构造_显示产物层不进面模型()
    {
        var layers = new LayerTable(); layers.New("块体模型"); layers.New("地表");
        var m1 = BlockModelMeta.CreateRegular("采场", 0, 0, 0, 10, 10, 10, 2, 2, 2);
        var m2 = BlockModelMeta.CreateRegular("排土场", 0, 0, 0, 10, 10, 10, 3, 1, 1);
        m2.IsVisible = false; m1.IsActive = true;
        var cell = Mesh("块体格网", "块体模型");                    // 块体仓渲出来的格网(挂在块体显示层)
        var ents = new List<SceneEntity> { cell, Mesh("地表面", "地表") };
        var vm = new ObjectTreeViewModel();
        var blkChanged = new List<(BlockModelMeta, bool)>();
        vm.BlockVisibilityChanged = (m, v) => { m.IsVisible = v; blkChanged.Add((m, v)); };

        vm.Refresh(ents, layers, null, null, new[] { m1, m2 }, "块体模型");

        Assert.Equal(new[] { ObjectTreeViewModel.CadRootName, ObjectTreeViewModel.MeshRootName, ObjectTreeViewModel.BlockRootName },
                     vm.RootNodes.Select(r => r.Name).ToArray());
        var meshRoot = Root(vm, ObjectTreeViewModel.MeshRootName);
        Assert.Equal(new[] { "地表" }, meshRoot.Children.Select(c => c.LayerName).ToArray());   // 格网不算面模型
        Assert.DoesNotContain(Root(vm, ObjectTreeViewModel.CadRootName).Children[0].Children, c => c.LayerName == "块体模型");

        var blk = Root(vm, ObjectTreeViewModel.BlockRootName);
        Assert.Equal(new[] { "● 采场  (8 块)", "○ 排土场  (3 块)" }, blk.Children.Select(c => c.Name).ToArray());
        Assert.True(blk.Children[0].IsContentVisible);
        Assert.False(blk.Children[1].IsContentVisible);
        Assert.Same(m2, blk.Children[1].BlockModelRef);

        blk.Children[1].IsContentVisible = true;                    // 勾上 → 块体仓
        Assert.Equal(new[] { (m2, true) }, blkChanged.ToArray());

        vm.Refresh(ents, layers, null, null, new[] { m1 }, "块体模型");   // 删掉一个模型
        Assert.Single(blk.Children);
        vm.Refresh(ents, layers, null, null, null, "块体模型");           // 全删 → 根撤掉
        Assert.DoesNotContain(vm.RootNodes, r => r.Name == ObjectTreeViewModel.BlockRootName);
    }

    [Fact]
    public void 切文档换文件名_旧文件节点被换掉_图层按名排序()
    {
        var layers = new LayerTable(); layers.New("b"); layers.New("a");
        var vm = new ObjectTreeViewModel();
        vm.Refresh(new List<SceneEntity>(), layers, @"x\一.pmx", null);
        vm.Refresh(new List<SceneEntity>(), layers, @"x\二.pmx", null);
        var cad = Root(vm, ObjectTreeViewModel.CadRootName);
        var file = Assert.Single(cad.Children);
        Assert.Equal("二.pmx", file.Name);
        Assert.Equal(new[] { "0", "a", "b" }, file.Children.Select(c => c.LayerName).ToArray());
        Assert.Null(vm.FindEntityNode(Mesh("x", "0")));                            // 不在树里的实体找不到
    }
}
