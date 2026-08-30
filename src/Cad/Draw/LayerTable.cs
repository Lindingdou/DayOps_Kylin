using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>图层：名称 + 颜色 + 可见。对应 Home 图层管理（托管重实现）。</summary>
public sealed class Layer
{
    public string Name;
    public float Cr, Cg, Cb;
    public bool Visible = true;
    public Layer(string name, float r, float g, float b) { Name = name; Cr = r; Cg = g; Cb = b; }
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
}
