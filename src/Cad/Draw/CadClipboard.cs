using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 实体剪贴板 —— COPYCLIP/CUTCLIP 存入选中实体的克隆快照，PASTECLIP/PASTEORIG 克隆取出。
/// 克隆用 `Apply(Affine2)`（复用每实体的变换语义），存/取都是深拷贝，互不影响。纯逻辑、可单测。
/// </summary>
public sealed class CadClipboard
{
    private readonly List<SceneEntity> _items = new();

    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    /// <summary>存入：把实体克隆一份快照（原位 Apply），后续场景改动不影响剪贴板内容。</summary>
    public void Set(IEnumerable<SceneEntity> entities)
    {
        _items.Clear();
        foreach (var e in entities) _items.Add(e.Apply(Affine2.Translate(0, 0)));
    }

    /// <summary>取出：克隆剪贴板内容并整体平移 (dx,dy)。多次粘贴互不影响。</summary>
    public List<SceneEntity> Paste(double dx, double dy)
    {
        var res = new List<SceneEntity>();
        foreach (var e in _items) res.Add(e.Apply(Affine2.Translate(dx, dy)));
        return res;
    }
}
