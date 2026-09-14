using System.Collections.ObjectModel;
using System.ComponentModel;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>对象管理器节点类型（同原版 <c>TreeNodeKind</c>）；界面据此决定显示 CheckBox / 图标 / 右键菜单项。</summary>
public enum ObjectTreeKind
{
    Root,           // CAD 对象 / 面模型 / 块体模型 三个顶层组
    CadFile,        // CAD 对象组下"文件"节点
    Layer,          // 图层节点（CAD 文件下 或 面模型组下，含可见性 checkbox）
    Entity,         // 普通实体叶节点（预留：原版 CAD 组只管理到图层级，不展开实体）
    MeshEntity,     // 三角网叶节点（面模型组下，可双击选中 / 右键删除模型）
    BlockModel,     // 块体模型叶节点
}

/// <summary>
/// 对象管理器的一个节点 —— 忠实移植原版 <c>PitMineApp.Infrastructure.FileTree.FileTreeNode</c>。
/// 原版按实体 handle 认对象、可见性直接打到引擎；Kylin 场景是托管对象，这里按引用认实体，
/// 可见性经 <see cref="ObjectTreeViewModel"/> 注入的回调落到 图层表 / 块体仓。纯逻辑，可单测。
/// </summary>
public class ObjectTreeNode : INotifyPropertyChanged
{
    private string _name = "";
    private string _icon = "📄";
    private bool _isExpanded = true;
    private bool _isSelected;
    private bool _isContentVisible = true;
    private ObjectTreeKind _kind = ObjectTreeKind.Entity;

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(nameof(Icon)); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    public ObjectTreeNode? Parent { get; set; }

    public ObservableCollection<ObjectTreeNode> Children { get; } = new();

    /// <summary>图层节点的图层名；实体节点则为所在图层名。</summary>
    public string? LayerName { get; set; }

    /// <summary>实体叶节点持有的场景实体（原版是 ulong handle；Kylin 直接持引用）。</summary>
    public SceneEntity? Entity { get; set; }

    /// <summary>块体模型节点持有的模型。</summary>
    public BlockModelMeta? BlockModelRef { get; set; }

    /// <summary>所属 ViewModel：可见性开关经它分发到后端（图层表 / 块体仓）。</summary>
    internal ObjectTreeViewModel? Owner { get; set; }

    /// <summary>节点类型（决定渲染与交互）。</summary>
    public ObjectTreeKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(ShowVisibilityCheckbox));
        }
    }

    /// <summary>
    /// 节点前面是否显示可见性 CheckBox。
    /// 图层 + 块体模型节点显示；顶层组(Root)也显示 —— 作为该组的总开关,
    /// 勾/去勾 = 组内全部打开/全部关闭(面模型组即"所有面一键显隐")。
    /// 其它（文件 / 实体叶子）隐藏。
    /// </summary>
    public bool ShowVisibilityCheckbox
        => _kind == ObjectTreeKind.Layer || _kind == ObjectTreeKind.BlockModel || _kind == ObjectTreeKind.Root;

    /// <summary>
    /// 统一的可见性属性（图层 / 块体模型 / 顶层组共用）。
    /// 写入时根据 Kind 分发到相应后端：
    ///   - Layer       → 图层表该层 Visible（经 ViewModel 回调，调用方负责重绘）
    ///   - BlockModel  → 块体仓 SetVisibility(model, value)
    ///   - Root        → 总开关:递归下发到组内所有带开关的子孙(图层/块体模型),一键全开/全关
    /// 后端失败仅吞，UI 保持新状态以避免回弹。
    /// </summary>
    public bool IsContentVisible
    {
        get => _isContentVisible;
        set
        {
            if (_isContentVisible == value) return;
            _isContentVisible = value;
            OnPropertyChanged(nameof(IsContentVisible));

            switch (_kind)
            {
                case ObjectTreeKind.Layer:
                    if (!string.IsNullOrEmpty(LayerName))
                    {
                        try { Owner?.OnLayerVisibilityChanged(LayerName!, value); }
                        catch { }
                    }
                    break;
                case ObjectTreeKind.BlockModel:
                    if (BlockModelRef != null)
                    {
                        try { Owner?.OnBlockVisibilityChanged(BlockModelRef, value); }
                        catch { }
                    }
                    break;
                case ObjectTreeKind.Root:
                    // 一次总开关会改 N 个图层：批处理期间回调只改表不重绘，结束后由 ViewModel 统一重绘一次
                    Owner?.BeginBatch();
                    try { PropagateVisibility(this, value); }
                    finally { Owner?.EndBatch(); }
                    break;
            }
        }
    }

    /// <summary>只改树上的勾选状态、不碰后端（刷新时把表里的真实状态拉齐到树用）。</summary>
    public void SyncVisible(bool visible)
    {
        if (_isContentVisible == visible) return;
        _isContentVisible = visible;
        OnPropertyChanged(nameof(IsContentVisible));
    }

    /// <summary>
    /// 顶层组总开关下发：递归把子树里所有带可见性开关的节点（图层/块体模型）
    /// 一起设为 visible。走各节点自己的 IsContentVisible setter,后端调用与
    /// 单独点选完全同路;值未变的节点由 setter 自身短路,不重复调后端。
    /// </summary>
    private static void PropagateVisibility(ObjectTreeNode node, bool visible)
    {
        foreach (var c in node.Children)
        {
            if (c._kind == ObjectTreeKind.Layer || c._kind == ObjectTreeKind.BlockModel)
                c.IsContentVisible = visible;
            PropagateVisibility(c, visible);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
