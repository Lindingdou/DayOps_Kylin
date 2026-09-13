using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 撤销快照的"重数据仓"：点云那种几百万条的列表(点 / 逐点色 / 真实色 / 法向)不进快照 JSON，
/// 快照里只记一个 key，列表本体按引用存在这里。同一个列表对象无论被多少份快照引用都只登记一次
/// (按引用同一性去重)；撤销栈两头都不再引用的条目由 <see cref="UndoManager"/> 在下一次 Push 时清掉。
///
/// 前提：登记进来的列表对象在创建后**不再原地改动**——点云的 Pts/Colors/RgbColors/Normals 都是整份替换
/// (着色 = 换一个新 List、单色 = 置 null)，代码里没有 Pts.Add/Colors[i]= 这类原地写，所以"引用"就等于"当时的内容"。
/// 任何新加的原地改点云列表的代码都会破坏这一前提 —— 要改就先 Clone 再改。
/// </summary>
public sealed class SnapshotHeavyStore
{
    private readonly Dictionary<long, object> _byKey = new();
    private readonly ConditionalWeakTable<object, StrongBox<long>> _keyOf = new();
    private long _seq;

    public int Count => _byKey.Count;

    /// <summary>登记一个列表对象，返回它的 key；同一对象重复登记返回同一个 key（即使中间被清扫过再回来也沿用）。</summary>
    public long Put(object heavy)
    {
        if (_keyOf.TryGetValue(heavy, out var box))
        {
            _byKey[box.Value] = heavy;      // 清扫掉过的条目回归：沿用旧 key，老快照引用仍然对得上
            return box.Value;
        }
        long k = ++_seq;
        _keyOf.Add(heavy, new StrongBox<long>(k));
        _byKey[k] = heavy;
        return k;
    }

    public T? Get<T>(long key) where T : class => _byKey.TryGetValue(key, out var o) ? o as T : null;

    public bool Contains(long key) => _byKey.ContainsKey(key);

    /// <summary>只保留 live 里的 key，其余释放（撤销栈两头都不引用的重数据不该一直占着内存）。</summary>
    public void Retain(HashSet<long> live)
    {
        if (_byKey.Count == 0) return;
        var dead = new List<long>();
        foreach (var k in _byKey.Keys) if (!live.Contains(k)) dead.Add(k);
        foreach (var k in dead) _byKey.Remove(k);
    }

    public void Clear() => _byKey.Clear();
}
