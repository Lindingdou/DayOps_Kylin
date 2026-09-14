using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 对象管理器 ViewModel —— 忠实移植原版 <c>FileTreeViewModel</c>：三类对象分根管理。
///
/// 顶层结构（按需出现，空集合时该根不显示）：
///   ┌─ 🏗️ CAD 对象
///   │   └─ 📘 [当前文件名]
///   │       ├─ 🎨 图层 X (☑ 可见性)        ← 线/圆/多段线/文字/点云… 只管理到图层级，不展开实体
///   │       └─ ...
///   ├─ ⛰️ 面模型
///   │   ├─ 🎨 图层 Y (☑ 可见性)
///   │   │   ├─ 🔷 三角网 A                 ← 面(三角网)到对象级，可双击选中 / 右键删除模型
///   │   │   └─ ...
///   │   └─ ...
///   └─ 🧱 块体模型
///       └─ 🎲 ● <BlockModel> (N 块)
///
/// 分类策略（同原版 "AcDbTriangleMesh → 面模型；其它 → CAD 对象"）：
///   - <see cref="MeshEntity"/> → 进"面模型"组
///   - 其它场景实体 → 进"CAD 对象 / [文件名]"组
///   - 块体模型 → 由调用方传入块体仓的模型列表；块体仓渲出来的格网实体(挂在块体显示层)不算面模型
///
/// 性能：Refresh 是差量 diff-and-patch，保留 IsExpanded / IsContentVisible / IsSelected 状态。
/// 纯逻辑(不碰界面)，单测直接跑。
/// </summary>
public sealed class ObjectTreeViewModel
{
    public ObservableCollection<ObjectTreeNode> RootNodes { get; } = new();

    // 根节点名称常量（diff 时按 Name 查找）
    public const string CadRootName   = "CAD 对象";
    public const string MeshRootName  = "面模型";
    public const string BlockRootName = "块体模型";

    // ─────────────────────────────────────────────────────────────────────
    // 后端回调（由主窗口注入）：图层可见性 → 图层表 + 重绘；块体可见性 → 块体仓
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>图层可见性改了：(图层名, 是否可见)。批处理中(<see cref="InBatch"/>)调用方只改表、不重绘。</summary>
    public Action<string, bool>? LayerVisibilityChanged { get; set; }

    /// <summary>块体模型可见性改了：(模型, 是否可见)。</summary>
    public Action<BlockModelMeta, bool>? BlockVisibilityChanged { get; set; }

    /// <summary>一次批处理(顶层组总开关 / 刷新时的跟随父组)结束，且期间至少改过一个图层 —— 统一重绘一次。</summary>
    public Action? BatchEnded { get; set; }

    /// <summary>正处于批处理中（顶层组总开关一次改 N 层）。</summary>
    public bool InBatch => _batchDepth > 0;

    private int _batchDepth;
    private bool _batchDirty;
    private bool _refreshing;

    internal void OnLayerVisibilityChanged(string layer, bool visible)
    {
        if (InBatch) _batchDirty = true;
        LayerVisibilityChanged?.Invoke(layer, visible);
    }

    internal void OnBlockVisibilityChanged(BlockModelMeta m, bool visible) => BlockVisibilityChanged?.Invoke(m, visible);

    internal void BeginBatch() => _batchDepth++;

    internal void EndBatch()
    {
        if (--_batchDepth > 0) return;
        _batchDepth = 0;
        if (!_batchDirty) return;
        _batchDirty = false;
        BatchEnded?.Invoke();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 公共 API
    // ─────────────────────────────────────────────────────────────────────

    /// <param name="entities">当前文档场景实体。</param>
    /// <param name="layers">当前文档图层表（空图层也要列出，与图层特性管理器一致）。</param>
    /// <param name="currentFilePath">当前文档路径；用于 CAD 对象组的文件节点名。</param>
    /// <param name="unsavedLabel">未保存(无路径)时文件节点的回退名。多文档下传当前视图名(如"视图1")；null → "(未保存文档)"。</param>
    /// <param name="blockModels">块体仓里的模型列表；null/空 → 不显示块体模型根。</param>
    /// <param name="blockDisplayLayer">块体仓渲染格网所挂的图层名；该层实体是块体的显示产物，不进 CAD/面模型组，该层本身也不列。</param>
    public void Refresh(IReadOnlyList<SceneEntity> entities, LayerTable layers,
                        string? currentFilePath, string? unsavedLabel,
                        IReadOnlyList<BlockModelMeta>? blockModels = null, string? blockDisplayLayer = null)
    {
        if (_refreshing) return;   // 跟随父组关层 → 重绘 → 又来刷树：树已是最新，跳过
        _refreshing = true;
        BeginBatch();              // 刷新期间若有节点跟随父组改了图层，攒到最后重绘一次
        try
        {
            var (cadLayers, meshLayers) = Snapshot(entities, layers, blockDisplayLayer);

            int rootIdx = 0;
            rootIdx = SyncCadRoot(currentFilePath, unsavedLabel, cadLayers, layers, rootIdx);
            rootIdx = SyncMeshRoot(meshLayers, layers, rootIdx);
            rootIdx = SyncBlockRoot(blockModels, rootIdx);
        }
        finally
        {
            EndBatch();
            _refreshing = false;
        }
    }

    /// <summary>找某实体对应的叶节点（面模型组里的三角网）；不在树里返回 null。</summary>
    public ObjectTreeNode? FindEntityNode(SceneEntity e)
    {
        foreach (var root in RootNodes)
            foreach (var layer in root.Children)
                foreach (var leaf in layer.Children)
                    if (ReferenceEquals(leaf.Entity, e)) return leaf;
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 数据快照：分为 CAD 和 mesh 两组，各组内按图层名分桶
    // ─────────────────────────────────────────────────────────────────────

    private static (SortedDictionary<string, List<SceneEntity>> cad, SortedDictionary<string, List<SceneEntity>> mesh)
        Snapshot(IReadOnlyList<SceneEntity> entities, LayerTable layers, string? blockDisplayLayer)
    {
        var cad  = new SortedDictionary<string, List<SceneEntity>>(StringComparer.Ordinal);
        var mesh = new SortedDictionary<string, List<SceneEntity>>(StringComparer.Ordinal);

        foreach (var e in entities)
        {
            string layer = string.IsNullOrEmpty(e.LayerName) ? "0" : e.LayerName;
            if (blockDisplayLayer != null && layer == blockDisplayLayer) continue;   // 块体显示产物 → 归块体模型根
            // 面模型 = 三角网；其它一律走 CAD 桶
            var target = e is MeshEntity ? mesh : cad;
            if (!target.TryGetValue(layer, out var list))
            {
                list = new List<SceneEntity>();
                target[layer] = list;
            }
            list.Add(e);
        }

        // 把图层表里的【所有图层】(含默认图层 "0")都纳入对象管理器：
        // 没有任何实体的图层补一个空桶进 CAD 组，让默认图层/空图层也始终可见(与图层管理器一致)。
        // 已经在 mesh 组出现的图层不再重复加，避免同名图层既挂空 CAD 节点又挂 mesh 节点。
        foreach (var l in layers.Layers)
        {
            if (string.IsNullOrEmpty(l.Name)) continue;
            if (blockDisplayLayer != null && l.Name == blockDisplayLayer) continue;
            if (!cad.ContainsKey(l.Name) && !mesh.ContainsKey(l.Name))
                cad[l.Name] = new List<SceneEntity>();
        }
        return (cad, mesh);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 根节点同步：每根独立 add/remove + diff 子树，保持 RootNodes 顺序稳定
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CAD 对象根：**始终显示**（哪怕当前文档没有任何实体，也保留"🏗️ CAD 对象 → 📘 文件名"两层骨架）。
    /// 让用户启动时立即看到树结构而不是空白；后续添加实体时图层节点会自动差量插入。
    /// 返回下一个 root 索引（恒为 rootIdx + 1）。
    /// </summary>
    private int SyncCadRoot(string? currentFilePath, string? unsavedLabel,
                            SortedDictionary<string, List<SceneEntity>> cadLayers, LayerTable layers, int rootIdx)
    {
        var cadRoot = EnsureRoot(CadRootName, "🏗️", rootIdx);   // 🏗️ = 工程/建造，对应矿区工程图 / CAD 设计语境

        // 单文档 → 单个文件子节点；未保存(无路径)时用当前视图名(unsavedLabel)而非"(未保存文档)"
        string fileLabel = !string.IsNullOrEmpty(currentFilePath)
            ? System.IO.Path.GetFileName(currentFilePath)
            : (!string.IsNullOrEmpty(unsavedLabel) ? unsavedLabel : "(未保存文档)");

        // 移除非当前文件的旧节点
        for (int i = cadRoot.Children.Count - 1; i >= 0; --i)
            if (cadRoot.Children[i].Name != fileLabel) cadRoot.Children.RemoveAt(i);

        ObjectTreeNode fileNode;
        if (cadRoot.Children.Count == 0)
        {
            fileNode = new ObjectTreeNode
            {
                Name = fileLabel,
                Icon = "📘",   // 蓝色书本：明确"项目/文档"层级
                IsExpanded = true,
                Kind = ObjectTreeKind.CadFile,
                Parent = cadRoot,
                Owner = this,
            };
            cadRoot.Children.Add(fileNode);
        }
        else fileNode = cadRoot.Children[0];

        DiffLayers(fileNode, cadLayers, layers, isMeshLayer: false);
        return rootIdx + 1;
    }

    /// <summary>面模型根：空 → 移除；有内容 → 图层 → 三角网实体。</summary>
    private int SyncMeshRoot(SortedDictionary<string, List<SceneEntity>> meshLayers, LayerTable layers, int rootIdx)
    {
        if (meshLayers.Count == 0)
        {
            int existing = FindRootIndex(MeshRootName);
            if (existing >= 0) RootNodes.RemoveAt(existing);
            return rootIdx;
        }
        var meshRoot = EnsureRoot(MeshRootName, "⛰️", rootIdx);   // ⛰️ = 山形，正合矿业"地表面 / 矿体面"语义
        DiffLayers(meshRoot, meshLayers, layers, isMeshLayer: true);
        return rootIdx + 1;
    }

    /// <summary>块体模型根：按块体仓模型列表构造块体节点（按对象身份 diff）。</summary>
    private int SyncBlockRoot(IReadOnlyList<BlockModelMeta>? models, int rootIdx)
    {
        if (models == null || models.Count == 0)
        {
            int existing = FindRootIndex(BlockRootName);
            if (existing >= 0) RootNodes.RemoveAt(existing);
            return rootIdx;
        }
        var root = EnsureRoot(BlockRootName, "🧱", rootIdx);   // 🧱 = 砖块，直观表达"块体"概念

        var oldByRef = new Dictionary<BlockModelMeta, ObjectTreeNode>(ReferenceEqualityComparer.Instance);
        foreach (var c in root.Children) if (c.BlockModelRef != null) oldByRef[c.BlockModelRef] = c;
        var newSet = new HashSet<BlockModelMeta>(ReferenceEqualityComparer.Instance);
        int idx = 0;
        foreach (var m in models)
        {
            newSet.Add(m);
            if (oldByRef.TryGetValue(m, out var existingNode))
            {
                existingNode.Name = BuildBlockNodeLabel(m);
                // 同步可见性：块体仓那边改了 → 把树勾选状态拉齐（只改树，不回打后端，避免回弹）
                existingNode.SyncVisible(m.IsVisible);
                EnsureAtIndex(root.Children, existingNode, idx);
            }
            else
            {
                var node = new ObjectTreeNode
                {
                    Name = BuildBlockNodeLabel(m),
                    Icon = "🎲",   // 立方体（骰子）：单个块体模型，与根的 🧱"堆叠"形成层级感
                    BlockModelRef = m,
                    Parent = root,
                    Kind = ObjectTreeKind.BlockModel,
                    Owner = this,
                };
                node.SyncVisible(m.IsVisible);
                root.Children.Insert(idx, node);
            }
            idx++;
        }
        for (int i = root.Children.Count - 1; i >= 0; --i)
            if (root.Children[i].BlockModelRef is { } r && !newSet.Contains(r)) root.Children.RemoveAt(i);

        return rootIdx + 1;
    }

    private static string BuildBlockNodeLabel(BlockModelMeta m)
    {
        // 可见性不写入标签后缀（已挪到行首 CheckBox 表达）
        string prefix = m.IsActive ? "● " : "○ ";
        return $"{prefix}{m.Name}  ({m.BlockCount:N0} 块)";
    }

    /// <summary>确保名为 name 的根存在且位于 rootIdx（不存在则建，位置错则挪）。</summary>
    private ObjectTreeNode EnsureRoot(string name, string icon, int rootIdx)
    {
        int existing = FindRootIndex(name);
        if (existing == rootIdx) return RootNodes[rootIdx];
        if (existing >= 0) { var r = RootNodes[existing]; RootNodes.Move(existing, rootIdx); return r; }
        var root = new ObjectTreeNode { Name = name, Icon = icon, IsExpanded = true, Kind = ObjectTreeKind.Root, Owner = this };
        RootNodes.Insert(rootIdx, root);
        return root;
    }

    private int FindRootIndex(string name)
    {
        for (int i = 0; i < RootNodes.Count; i++)
            if (RootNodes[i].Name == name) return i;
        return -1;
    }

    /// <summary>
    /// 在 parent 节点下做"图层 → 实体"的差量同步。
    /// parent 是 CAD 文件节点 或 面模型根；isMeshLayer 决定子节点 Kind。
    /// </summary>
    private void DiffLayers(ObjectTreeNode parent, SortedDictionary<string, List<SceneEntity>> newLayers,
                            LayerTable layers, bool isMeshLayer)
    {
        // 1) 索引现有图层节点
        var oldByName = new Dictionary<string, ObjectTreeNode>(StringComparer.Ordinal);
        foreach (var child in parent.Children)
            if (child.Kind == ObjectTreeKind.Layer)
                oldByName[child.LayerName ?? string.Empty] = child;

        // 2) 移除不在新快照中的旧图层
        for (int i = parent.Children.Count - 1; i >= 0; --i)
        {
            var child = parent.Children[i];
            if (child.Kind != ObjectTreeKind.Layer) continue;
            var key = child.LayerName ?? string.Empty;
            if (!newLayers.ContainsKey(key))
            {
                parent.Children.RemoveAt(i);
                oldByName.Remove(key);
            }
        }

        // 3) 遍历新快照，更新已存在的、追加新增的（保持 SortedDictionary 顺序）
        int insertIndex = 0;
        foreach (var kv in newLayers)
        {
            if (oldByName.TryGetValue(kv.Key, out var existingLayer))
            {
                // 图层表那边(功能区 全开/全关、图层特性管理器)改了开关 → 拉齐到树；只改树不回打
                existingLayer.SyncVisible(layers.Get(kv.Key)?.Visible ?? true);
                // CAD 对象只管理到图层级，不展开每个实体；面模型(网格)才到对象级
                if (isMeshLayer) DiffEntities(existingLayer, kv.Value);
                EnsureAtIndex(parent.Children, existingLayer, insertIndex);
            }
            else
            {
                var newLayer = CreateLayerNode(kv.Key, kv.Value, parent, layers, isMeshLayer);
                parent.Children.Insert(insertIndex, newLayer);
            }
            ++insertIndex;
        }
    }

    /// <summary>把 newEntities 与 layerNode.Children 做差量比对：按实体引用索引。</summary>
    private void DiffEntities(ObjectTreeNode layerNode, List<SceneEntity> newEntities)
    {
        var oldByRef = new Dictionary<SceneEntity, ObjectTreeNode>(ReferenceEqualityComparer.Instance);
        foreach (var child in layerNode.Children)
            if (child.Entity != null) oldByRef[child.Entity] = child;

        var newSet = new HashSet<SceneEntity>(ReferenceEqualityComparer.Instance);
        foreach (var e in newEntities) newSet.Add(e);

        // 移除：旧但不在新中
        for (int i = layerNode.Children.Count - 1; i >= 0; --i)
        {
            var en = layerNode.Children[i].Entity;
            if (en == null || !newSet.Contains(en)) layerNode.Children.RemoveAt(i);
        }

        // 追加：新但不在旧中（按当前场景顺序追加；不强制全局排序）；已有的同步名字(三角网可改名)
        foreach (var e in newEntities)
        {
            if (oldByRef.TryGetValue(e, out var node)) node.Name = EntityLabel(e);
            else layerNode.Children.Add(CreateEntityNode(e, layerNode, isMeshLayer: true));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 节点构造与辅助
    // ─────────────────────────────────────────────────────────────────────

    private ObjectTreeNode CreateLayerNode(string layerName, List<SceneEntity> entities, ObjectTreeNode parent,
                                           LayerTable layers, bool isMeshLayer)
    {
        var layerNode = new ObjectTreeNode
        {
            Name = $"图层: {layerName}",
            Icon = "🎨",   // 调色板：表达"图层"概念（每层一种调子），且左对齐 checkbox 旁视觉舒服
            IsExpanded = true,
            LayerName = layerName,
            Parent = parent,
            Kind = ObjectTreeKind.Layer,
            Owner = this,
        };
        // 可见性 = 图层表里的真实状态，但【父组已整体关掉时跟随父组】——否则树一刷新(重建子节点)，
        // 新节点只读表、不看父组，就会出现"父组 ☐ 取消了、子图层却全是 ☑"的不一致。
        // CAD 图层挂在文件节点下、面模型图层直接挂在根下 —— 一律看所属顶层组的总开关。
        bool tableVisible = layers.Get(layerName)?.Visible ?? true;
        var group = parent; while (group != null && group.Kind != ObjectTreeKind.Root) group = group.Parent;
        bool parentOff = group != null && !group.IsContentVisible;
        if (tableVisible && parentOff) layerNode.IsContentVisible = false;   // 走 setter：表里也关掉(批处理内，结束统一重绘)
        else layerNode.SyncVisible(tableVisible);

        // CAD 对象只到图层级(不展开实体);面模型/网格才到对象级
        if (isMeshLayer)
            foreach (var e in entities)
                layerNode.Children.Add(CreateEntityNode(e, layerNode, isMeshLayer));
        return layerNode;
    }

    private ObjectTreeNode CreateEntityNode(SceneEntity e, ObjectTreeNode parent, bool isMeshLayer)
    {
        return new ObjectTreeNode
        {
            Name = EntityLabel(e),
            Icon = isMeshLayer ? "🔷" : "📄",   // 面模型组用统一蓝色菱形
            Entity = e,
            LayerName = parent.LayerName,
            Parent = parent,
            Kind = isMeshLayer ? ObjectTreeKind.MeshEntity : ObjectTreeKind.Entity,
            Owner = this,
        };
    }

    /// <summary>叶节点标签：原版突出 handle 作唯一 id(Mesh #1A2B)；Kylin 三角网有名字(建模/剖面各窗口都按名认)，直接用名。</summary>
    private static string EntityLabel(SceneEntity e) => e is MeshEntity me ? me.Name : e.GetType().Name;

    /// <summary>确保 node 在 collection 中位于 desiredIndex；只在位置错误时调用 Move。</summary>
    private static void EnsureAtIndex(ObservableCollection<ObjectTreeNode> collection, ObjectTreeNode node, int desiredIndex)
    {
        int currentIndex = collection.IndexOf(node);
        if (currentIndex >= 0 && currentIndex != desiredIndex && desiredIndex < collection.Count)
            collection.Move(currentIndex, desiredIndex);
    }
}
