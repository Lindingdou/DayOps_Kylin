using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using DMC = Dock.Model.Mvvm.Controls;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 对象管理器面板 —— 忠实移植原版 <c>objectBrowserTree</c>（左面板第二页，与文件管理器并列）：
///   · 三根：🏗️ CAD 对象 → 📘 文件 → 🎨 图层(线/圆/多段线/文字…只到图层级)；⛰️ 面模型 → 🎨 图层 → 🔷 三角网；🧱 块体模型 → 🎲 模型
///   · 图层 / 块体模型 / 顶层组 行首 ☑ = 可见性开关（顶层组 = 该组总开关，一键全开/全关）
///   · 双击：三角网 = 视口选中；块体模型 = 设为活动 + 缩放至
///   · 右键：图层节点 → 删除图层(连同其中对象, 可撤销)；三角网 → 删除模型(可撤销)
///   · 当前图层改在功能区「当前图层」下拉里选（同原版 comboCurrentLayer）；冻结/锁定/颜色等在图层特性管理器
/// 树的数据逻辑在 <see cref="ObjectTreeViewModel"/>（纯逻辑，可单测），这里只做接线。
/// </summary>
public partial class MainWindow
{
    private ObjectTreeViewModel? _objVm;
    private bool _isRefreshingLayerCombo;

    /// <summary>建对象管理器树：注入可见性回调 → 绑数据 → 挂右键选中/双击；块体仓变化也刷树。</summary>
    private void InitObjectManager()
    {
        _objVm = new ObjectTreeViewModel
        {
            // 图层 ☑ → 图层表；总开关一次改 N 层时(InBatch)只改表，BatchEnded 统一重绘一次
            LayerVisibilityChanged = (name, visible) =>
            {
                var l = _layers.Get(name);
                if (l == null) return;
                l.Visible = visible;
                if (!_objVm!.InBatch) AfterLayerStateChange();
            },
            BatchEnded = AfterLayerStateChange,
            // 块体 ☑ → 块体仓(同步 IsVisible + 重渲)
            BlockVisibilityChanged = (m, visible) => Modeling.BlockModelStore.SetVisibility(MdlCtx(), m, visible),
        };
        if (ObjectTree == null) return;
        ObjectTree.ItemsSource = _objVm.RootNodes;

        // 右键先把鼠标下的节点选中：否则菜单项对的是上一次左键选中的节点(弹错菜单 / 删错对象)；原版 PreviewMouseRightButtonDown 同义
        ObjectTree.AddHandler(InputElement.PointerPressedEvent, OnObjectTreePointerPressed, RoutingStrategies.Tunnel);

        // 块体增删 / 改名 / 活动切换 / 可见性变化都触发树刷新（同原版反射订阅 BlockModelService）
        Modeling.BlockModelStore.Models.CollectionChanged += (_, _) => RefreshObjectManager();
        Modeling.BlockModelStore.ActiveChanged += (_, _) => RefreshObjectManager();
        Modeling.BlockModelStore.DisplayChanged += (_, _) => RefreshObjectManager();
    }

    /// <summary>差量刷新对象管理器（保留展开/勾选状态）。场景增删、图层增删改、切文档后都走这里。</summary>
    private void RefreshObjectManager()
    {
        if (_objVm == null || _active == null) return;
        _objVm.Refresh(_scene.Entities, _layers, _currentPath, _active.Title,
                       Modeling.BlockModelStore.Models, Modeling.BlockModelStore.LayerName);
    }

    private void OnObjectTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ObjectTree).Properties.IsRightButtonPressed) return;
        var node = ObjectNodeAt(e.Source);
        if (node != null) ObjectTree.SelectedItem = node;
    }

    /// <summary>命中链路向上找承载节点(找不到返回当前选中项)。</summary>
    private ObjectTreeNode? ObjectNodeAt(object? source)
    {
        var item = (source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>();
        return item?.DataContext as ObjectTreeNode ?? ObjectTree?.SelectedItem as ObjectTreeNode;
    }

    /// <summary>双击：三角网 → 视口选中(替换选择集)；块体模型 → 设为活动 + 缩放至（同原版 MouseDoubleClick）。</summary>
    private void OnObjectTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        var node = ObjectNodeAt(e.Source);
        if (node == null) return;
        if (node.Kind == ObjectTreeKind.BlockModel && node.BlockModelRef != null)
        {
            var m = node.BlockModelRef;
            Modeling.BlockModelStore.Active = m;
            Modeling.BlockModelStore.ZoomTo(MdlCtx(), m);
            StatusMsg.Text = $"对象管理器：块体模型「{m.Name}」已设为活动并缩放至";
            e.Handled = true;
        }
        else if (node.Entity != null)
        {
            var en = node.Entity;
            if (!_scene.Entities.Contains(en)) { RefreshObjectManager(); return; }
            // 同视口点选规则(Scene.Pick 的 canSelect)：关闭/冻结/锁定层上的对象、隐藏对象不可选 —— 否则选中一个看不见的东西只剩一团青色高亮
            if (!en.Visible || !_layers.IsSelectable(en.LayerName))
            {
                StatusMsg.Text = $"对象管理器：「{node.Name}」所在图层已关闭/冻结/锁定或对象已隐藏，不可选中（先在 ☑ 或图层特性管理器里打开）";
                e.Handled = true;
                return;
            }
            _selected.Clear(); _selected.Add(en); HighlightSelection();
            if (en is MeshEntity me)
            {
                var bb = me.Bounds;
                StatusMsg.Text = $"对象管理器：选中三角网「{me.Name}」 {me.VertexCount} 顶点 / {me.TriangleCount} 三角 · Z {bb.minZ:0.#}~{bb.maxZ:0.#}";
            }
            else StatusMsg.Text = $"对象管理器：选中 {node.Name}";
            e.Handled = true;
        }
    }

    /// <summary>
    /// 按当前节点类型决定显示哪些删除项：图层节点 → 删除图层；网格/实体叶节点 → 删除模型/对象。
    /// 都不适用（根 / 文件 / 块体节点）时取消弹出，避免弹一个空菜单。
    /// </summary>
    private void OnObjectTreeMenuOpening(object? sender, CancelEventArgs e)
    {
        var node = ObjectTree?.SelectedItem as ObjectTreeNode;
        var kind = node?.Kind;
        bool isLayer  = kind == ObjectTreeKind.Layer && !string.IsNullOrEmpty(node!.LayerName);
        bool isEntity = (kind == ObjectTreeKind.MeshEntity || kind == ObjectTreeKind.Entity) && node!.Entity != null;

        MiObjDeleteLayer.IsVisible  = isLayer;
        MiObjDeleteEntity.IsVisible = isEntity;
        if (isEntity)
            MiObjDeleteEntity.Header = kind == ObjectTreeKind.MeshEntity ? "🗑 删除模型" : "🗑 删除对象";

        if (!isLayer && !isEntity)
            e.Cancel = true;   // 无可删项 → 不弹菜单
    }

    /// <summary>删除单个模型/对象（网格或实体叶节点）。进 Undo 栈（Ctrl+Z 可恢复）。</summary>
    private void OnObjTreeDeleteEntityClick(object? sender, RoutedEventArgs e)
    {
        if (ObjectTree?.SelectedItem is not ObjectTreeNode node || node.Entity == null) return;
        string what = node.Kind == ObjectTreeKind.MeshEntity ? "模型" : "对象";
        try
        {
            bool ok = _scene.Entities.Contains(node.Entity);
            if (ok)
            {
                BeginChange();
                _scene.Remove(node.Entity);
                _selected.Remove(node.Entity);
                HighlightSelection();
                RefreshScene();
            }
            EditEcho(ok ? $"已删除{what}：{node.Name}" : $"删除{what}失败：{node.Name}", ok ? EchoLevel.Info : EchoLevel.Warn);
        }
        catch (Exception ex)
        {
            EditEcho($"删除{what}异常：{ex.Message}", EchoLevel.Error);
        }
        RefreshObjectManager();
    }

    /// <summary>删除图层及其中的全部对象（面模型层 → 连同网格；CAD 层 → 连同实体），并移除图层记录（默认层"0"只清空、不删记录）。</summary>
    private async void OnObjTreeDeleteLayerClick(object? sender, RoutedEventArgs e)
    {
        if (ObjectTree?.SelectedItem is not ObjectTreeNode node
            || node.Kind != ObjectTreeKind.Layer || string.IsNullOrEmpty(node.LayerName)) return;
        string layer = node.LayerName!;
        await DeleteLayerWithEntitiesAsync(layer);
    }

    private async Task DeleteLayerWithEntitiesAsync(string layer)
    {
        int cnt = _scene.Entities.Count(en => en.LayerName == layer);
        bool go = await Modeling.BlockMsgBox.ConfirmAsync(this, "删除图层",
            $"确定删除图层「{layer}」及其中的 {cnt} 个对象吗？\n（可用 Ctrl+Z 撤销）");
        if (!go) return;

        try
        {
            int removed = 0;
            if (cnt > 0)
            {
                BeginChange();
                removed = _scene.Entities.RemoveAll(en => en.LayerName == layer);
                _selected.RemoveAll(en => en.LayerName == layer);
                HighlightSelection();
            }
            // 删空后移除图层记录（默认层"0"由图层表拒绝，忽略其返回值即可；当前层被删时图层表自动把当前切到"0"）
            _layers.Remove(layer);
            RefreshScene();
            EditEcho($"已删除图层「{layer}」及其中 {removed} 个对象");
        }
        catch (Exception ex)
        {
            EditEcho($"删除图层「{layer}」异常：{ex.Message}", EchoLevel.Error);
        }
        PopulateDrawingLayers();
    }

    // ── 自检（PITMINE_SELFTEST）：不模拟鼠标, 直设状态后截图核对 ──

    private DMC.Tool? _objectsTool;

    /// <summary>@对象管理器 [菜单 前缀 | 双击 前缀 | 勾 前缀 开|关]：切左面板到对象管理器页, 再按需对某节点弹菜单/双击/勾选。</summary>
    private void SelftestObjectManager(string args)
    {
        if (_objectsTool != null && _dockFactory != null) _dockFactory.SetActiveDockable(_objectsTool);
        RefreshObjectManager();
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length < 2 || _objVm == null) { StatusMsg.Text = $"自检对象管理器：根 {_objVm?.RootNodes.Count} 个 [{string.Join(" · ", _objVm?.RootNodes.Select(r => r.Name) ?? Array.Empty<string>())}]"; return; }
        ObjectTreeNode? Find(System.Collections.Generic.IEnumerable<ObjectTreeNode> ns)
        {
            foreach (var n in ns) { if (n.Name.StartsWith(a[1])) return n; var c = Find(n.Children); if (c != null) return c; }
            return null;
        }
        var node = Find(_objVm.RootNodes);
        if (node == null) { StatusMsg.Text = $"自检对象管理器：找不到「{a[1]}」开头的节点"; return; }
        ObjectTree.SelectedItem = node;
        switch (a[0])
        {
            case "菜单":
                OnObjectTreeMenuOpening(ObjectTreeMenu, new CancelEventArgs());
                ObjectTreeMenu.Open(ObjectTree);
                StatusMsg.Text = $"自检对象管理器：「{node.Name}」菜单 [{(MiObjDeleteLayer.IsVisible ? MiObjDeleteLayer.Header : "")} {(MiObjDeleteEntity.IsVisible ? MiObjDeleteEntity.Header : "")}]";
                break;
            case "双击":
                if (node.Kind == ObjectTreeKind.BlockModel && node.BlockModelRef != null)
                { Modeling.BlockModelStore.Active = node.BlockModelRef; Modeling.BlockModelStore.ZoomTo(MdlCtx(), node.BlockModelRef); }
                else if (node.Entity != null) { _selected.Clear(); _selected.Add(node.Entity); HighlightSelection(); }
                StatusMsg.Text = $"自检对象管理器：双击「{node.Name}」 选中 {_selected.Count} 个";
                break;
            case "勾":
                node.IsContentVisible = a.Length > 2 && a[2] == "开";
                StatusMsg.Text = $"自检对象管理器：「{node.Name}」☑={node.IsContentVisible} 图层表 [{string.Join(" ", _layers.Layers.Select(l => l.Name + (l.Visible ? "开" : "关")))}]";
                break;
        }
    }

    // ── 功能区「当前图层」下拉（同原版 comboCurrentLayer / RefreshRibbonLayerCombo / OnRibbonLayerChanged）──

    /// <summary>用图层表重填下拉并选中当前层；填充期间的 SelectionChanged 不回打图层表。</summary>
    private void RefreshRibbonLayerCombo()
    {
        if (LayerCombo == null) return;
        _isRefreshingLayerCombo = true;
        try
        {
            var names = _layers.Layers.Select(l => l.Name).ToList();
            LayerCombo.ItemsSource = names;
            int idx = names.IndexOf(_layers.Current.Name);
            LayerCombo.SelectedIndex = idx >= 0 ? idx : (names.Count > 0 ? 0 : -1);
        }
        finally
        {
            _isRefreshingLayerCombo = false;
        }
    }

    private void OnRibbonLayerChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingLayerCombo) return;
        if (LayerCombo?.SelectedItem is not string name || name == _layers.Current.Name) return;
        if (_layers.SetCurrent(name)) StatusMsg.Text = $"当前图层「{name}」";
    }
}
