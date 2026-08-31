using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>图层：名称 + 颜色 + 开关/冻结/锁定。对应 Home 图层管理（托管重实现）。</summary>
public sealed class Layer
{
    public string Name;
    public float Cr, Cg, Cb;
    public bool Visible = true;    // 关闭=不显示
    public bool Frozen;            // 冻结=不显示且不参与(比关闭更强)
    public bool Locked;            // 锁定=显示但不可选/改
    public Layer(string name, float r, float g, float b) { Name = name; Cr = r; Cg = g; Cb = b; }

    /// <summary>是否上屏（开且未冻结）。</summary>
    public bool Shown => Visible && !Frozen;
    /// <summary>是否可拾取/编辑（上屏且未锁定）。</summary>
    public bool Selectable => Shown && !Locked;
}

/// <summary>图层表：默认层 "0"，可新建/删除/设为当前；新层轮转配色。纯逻辑，可单测。</summary>
public sealed class LayerTable
{
    private static readonly (float r, float g, float b)[] Palette =
    {
        (0.86f, 0.90f, 0.60f), (0.90f, 0.50f, 0.40f), (0.50f, 0.80f, 0.95f), (0.70f, 0.85f, 0.50f),
        (0.90f, 0.75f, 0.40f), (0.75f, 0.60f, 0.90f), (0.55f, 0.90f, 0.70f), (0.90f, 0.60f, 0.75f)
    };

    private readonly List<Layer> _layers = new();
    public IReadOnlyList<Layer> Layers => _layers;
    public Layer Current { get; private set; }

    public LayerTable()
    {
        var l0 = new Layer("0", Palette[0].r, Palette[0].g, Palette[0].b);
        _layers.Add(l0);
        Current = l0;
    }

    public Layer New(string? name = null)
    {
        name ??= $"图层{_layers.Count}";
        var c = Palette[_layers.Count % Palette.Length];
        var l = new Layer(name, c.r, c.g, c.b);
        _layers.Add(l);
        Current = l;
        return l;
    }

    public Layer? Get(string name) => _layers.Find(x => x.Name == name);

    /// <summary>确保存在名为 name 的图层（导入时并入）：已存在返回原层，否则用给定色新建（不改当前层）。</summary>
    public Layer EnsureImported(string name, float r, float g, float b)
    {
        var l = Get(name);
        if (l != null) return l;
        l = new Layer(name, r, g, b);
        _layers.Add(l);
        return l;
    }

    public bool SetCurrent(string name)
    {
        var l = Get(name);
        if (l == null) return false;
        Current = l;
        return true;
    }

    /// <summary>循环把当前层切到下一层（简易"设为当前"）。</summary>
    public Layer CycleCurrent()
    {
        int i = _layers.IndexOf(Current);
        Current = _layers[(i + 1) % _layers.Count];
        return Current;
    }

    public bool Remove(string name)
    {
        if (name == "0") return false;         // 默认层不删
        var l = Get(name);
        if (l == null) return false;
        _layers.Remove(l);
        if (Current == l) Current = _layers[0];
        return true;
    }

    /// <summary>重命名图层(就地改名, 保色/开关/冻结/锁定)。默认层 "0" 不可改; 新名空/与旧同/已存在则失败。返回成功。
    /// 注: 实体的 LayerName 由调用方另行 Scene.ReassignLayer 同步。忠实原版"图层命名/重命名"。</summary>
    public bool Rename(string oldName, string newName)
    {
        if (oldName == "0") return false;                        // 默认层不改名
        newName = newName?.Trim() ?? "";
        if (newName.Length == 0 || oldName == newName) return false;
        if (Get(newName) != null) return false;                  // 目标名已存在(合并另走)
        var l = Get(oldName);
        if (l == null) return false;
        l.Name = newName;
        return true;
    }

    /// <summary>该图层上的实体是否上屏（未知图层按显示处理）。</summary>
    public bool IsShown(string name) { var l = Get(name); return l == null || l.Shown; }
    /// <summary>该图层上的实体是否可拾取/编辑（未知图层按可选处理）。</summary>
    public bool IsSelectable(string name) { var l = Get(name); return l == null || l.Selectable; }

    /// <summary>全部打开：所有层开且解冻（锁定保持）。</summary>
    public void AllOn() { foreach (var l in _layers) { l.Visible = true; l.Frozen = false; } }

    /// <summary>用持久化的图层状态整表恢复（打开 .pmx 新格式用）。空列表则不动（交调用方按实体回退重建）。</summary>
    public void Restore(IReadOnlyList<SceneIO.LayerState> layers, string current)
    {
        if (layers == null || layers.Count == 0) return;
        _layers.Clear();
        foreach (var ls in layers)
            _layers.Add(new Layer(ls.Name, ls.Cr, ls.Cg, ls.Cb) { Visible = ls.Visible, Frozen = ls.Frozen, Locked = ls.Locked });
        if (_layers.Count == 0) _layers.Add(new Layer("0", Palette[0].r, Palette[0].g, Palette[0].b));
        Current = Get(current) ?? _layers[0];
    }

    /// <summary>重置为仅默认层 "0"（新建文档）。</summary>
    public void Reset()
    {
        _layers.Clear();
        var l0 = new Layer("0", Palette[0].r, Palette[0].g, Palette[0].b);
        _layers.Add(l0);
        Current = l0;
    }
}
