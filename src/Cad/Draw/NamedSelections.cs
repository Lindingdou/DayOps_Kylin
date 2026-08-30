using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 命名选择集（创建选择集 / 调用选择集）——把一组实体按名存起，之后按名或轮转调回。
/// 存的是实体引用(选择集指向同一批实体)；调回后由调用方剔除已删除的。纯逻辑、可单测。
/// </summary>
public sealed class NamedSelections
{
    private readonly List<(string name, List<SceneEntity> ents)> _sets = new();

    public int Count => _sets.Count;

    /// <summary>存入/覆盖同名选择集（快照当前引用列表）。</summary>
    public void Store(string name, IEnumerable<SceneEntity> entities)
    {
        var list = new List<SceneEntity>(entities);
        int i = _sets.FindIndex(s => s.name == name);
        if (i >= 0) _sets[i] = (name, list); else _sets.Add((name, list));
    }

    /// <summary>按名取；无则 null。</summary>
    public List<SceneEntity>? Get(string name)
    {
        int i = _sets.FindIndex(s => s.name == name);
        return i >= 0 ? _sets[i].ents : null;
    }

    /// <summary>按序号(环绕)取，供轮转调用；空则 null。</summary>
    public (string name, List<SceneEntity> ents)? At(int index)
    {
        if (_sets.Count == 0) return null;
        int i = ((index % _sets.Count) + _sets.Count) % _sets.Count;
        return _sets[i];
    }

    public bool Has(string name) => _sets.FindIndex(s => s.name == name) >= 0;
}
