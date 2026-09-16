using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>图层：名称 + 颜色 + 开关/冻结/锁定 + 线宽/透明度/打印/说明。对应原版「图层特性管理器」一行（托管重实现）。</summary>
public sealed class Layer
{
    public string Name;
    public float Cr, Cg, Cb;
    public bool Visible = true;    // 关闭=不显示
    public bool Frozen;            // 冻结=不显示且不参与(比关闭更强)
    public bool Locked;            // 锁定=显示但不可选/改

    /// <summary>图层线宽(DXF LineWeightType 值: -3=默认, 0..211=0.01mm)。实体线宽为 -1(随层) 时取这条。
    /// 注: 图层本身不能"随层", 故不收 -1。</summary>
    public short LineWeight = -3;

    /// <summary>图层透明度(0=不透明, 1..90=百分比)。实体透明度为 -1(随层) 时取这条。
    /// **口径**: 与 Kylin 实体「透明度」特性同为百分比 —— 原版这一列存的是 0..255 原始 alpha,
    /// 但随层解析必须与实体同单位，否则算出来的不透明度没有意义。</summary>
    public short Transparency;

    /// <summary>是否打印(不打印的层仍上屏, 只是出图/导出时略过)。</summary>
    public bool Plottable = true;

    /// <summary>说明(自由文本)。</summary>
    public string Description = "";

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
        name ??= NextAvailableName();
        var c = Palette[_layers.Count % Palette.Length];
        var l = new Layer(name, c.r, c.g, c.b);
        _layers.Add(l);
        Current = l;
        return l;
    }

    /// <summary>按用户输入新建图层：名称去首尾空白，空名、默认层名和重名均拒绝。</summary>
    public bool TryNew(string? name, out Layer? layer)
    {
        string normalized = name?.Trim() ?? "";
        if (normalized.Length == 0 || normalized == "0" || Get(normalized) != null)
        {
            layer = null;
            return false;
        }
        layer = New(normalized);
        return true;
    }

    /// <summary>取一个当前图层表中尚未使用的默认名称。</summary>
    public string NextAvailableName()
    {
        int i = 1;
        while (Get($"图层{i}") != null) i++;
        return $"图层{i}";
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

    /// <summary>随层解析 —— 实体透明度 -1(随层) 时取所在图层的透明度; 否则用实体自己的。
    /// 未知图层按不透明处理。结果一律夹到 0..90（同实体特性口径）。</summary>
    public short EffectiveTransparency(short entityTransp, string layerName)
    {
        if (entityTransp >= 0) return Math.Clamp(entityTransp, (short)0, (short)90);
        var l = Get(layerName);
        return l == null ? (short)0 : Math.Clamp(l.Transparency, (short)0, (short)90);
    }

    /// <summary>随层解析 —— 实体线宽 -1(随层) 时取所在图层的线宽; 否则用实体自己的。
    /// 未知图层返 -3(默认)。-2(随块) 原样返回：Kylin 无块表, 不臆造解析。</summary>
    public short EffectiveLineWeight(short entityLw, string layerName)
    {
        if (entityLw != -1) return entityLw;
        var l = Get(layerName);
        return l?.LineWeight ?? (short)-3;
    }

    /// <summary>该图层是否打印（未知图层按打印处理）。</summary>
    public bool IsPlottable(string name) { var l = Get(name); return l == null || l.Plottable; }

    /// <summary>该图层上的实体是否上屏（未知图层按显示处理）。</summary>
    public bool IsShown(string name) { var l = Get(name); return l == null || l.Shown; }
    /// <summary>该图层上的实体是否可拾取/编辑（未知图层按可选处理）。</summary>
    public bool IsSelectable(string name) { var l = Get(name); return l == null || l.Selectable; }

    /// <summary>全部打开：所有层开且解冻（锁定保持）。</summary>
    public void AllOn() { foreach (var l in _layers) { l.Visible = true; l.Frozen = false; } }

    /// <summary>全部关闭：所有层不显示（锁定/冻结状态不变）。忠实原版"全关"(层可见性关)。</summary>
    public void AllOff() { foreach (var l in _layers) l.Visible = false; }

    /// <summary>图层隔离：只显示 name 层，其余全部关闭(Visible=false)。返回被关闭层数。取消用 AllOn。忠实原版"图层隔离"(LAYISO)。</summary>
    public int Isolate(string name)
    {
        int n = 0;
        foreach (var l in _layers)
        {
            if (l.Name == name) l.Visible = true;
            else if (l.Visible) { l.Visible = false; n++; }
        }
        return n;
    }

    /// <summary>用持久化的图层状态整表恢复（打开 .pmx 新格式用）。空列表则不动（交调用方按实体回退重建）。</summary>
    public void Restore(IReadOnlyList<SceneIO.LayerState> layers, string current)
    {
        if (layers == null || layers.Count == 0) return;
        _layers.Clear();
        foreach (var ls in layers)
            _layers.Add(new Layer(ls.Name, ls.Cr, ls.Cg, ls.Cb)
            {
                Visible = ls.Visible, Frozen = ls.Frozen, Locked = ls.Locked,
                LineWeight = ls.LineWeight, Transparency = ls.Transparency,
                Plottable = ls.Plottable, Description = ls.Description ?? "",
            });
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
