using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 夹点表 —— 忠实移植原 PitMine3D 内核 xllAcEd 的 GripManager。
///
/// 选集变化时按实体重建夹点(各实体 <see cref="SceneEntity.Grips"/>)；超过 <see cref="ObjLimit"/> 个实体则不出夹点
/// (原版 kGripObjLimit=100：大量选中时避免渲染混乱与性能问题)。
///
/// 多夹点选择键位与原版/AutoCAD 一致：Ctrl = 逐个加减，Shift = 沿同一实体取区间(闭合多段线取短弧)。
/// 锚点 = 最近一次"点选"的夹点，Shift 区间以它为起点；重建后失效。纯逻辑，可单测。
/// </summary>
public sealed class GripTable
{
    /// <summary>选中实体超过此数不出夹点(原版 kGripObjLimit)。</summary>
    public const int ObjLimit = 100;

    public readonly struct Grip
    {
        public readonly SceneEntity Owner;   // 所属实体
        public readonly int Index;           // 在 Owner.Grips() 中的序号
        public readonly double X, Y;         // 世界坐标
        public readonly bool OwnerClosed;    // 所属实体是否闭合环(只有多段线有"环"的概念，同原版)
        public readonly int OwnerGripCount;  // 所属实体夹点总数(取短弧要用它算环长)
        public Grip(SceneEntity owner, int index, double x, double y, bool ownerClosed, int ownerGripCount)
        { Owner = owner; Index = index; X = x; Y = y; OwnerClosed = ownerClosed; OwnerGripCount = ownerGripCount; }
    }

    private readonly List<Grip> _grips = new();
    private readonly List<bool> _selected = new();
    private int _anchor = -1;

    public IReadOnlyList<Grip> Grips => _grips;
    public int Count => _grips.Count;
    public int Anchor => _anchor;

    /// <summary>按当前选集重建夹点表；选择集必然失效(索引全变)→ 统一清掉。</summary>
    public void Rebuild(IReadOnlyList<SceneEntity> selection)
    {
        _grips.Clear(); _selected.Clear(); _anchor = -1;
        if (selection == null || selection.Count == 0 || selection.Count > ObjLimit) return;
        foreach (var e in selection)
        {
            var gs = e.Grips();
            bool closed = e is PolylineEntity pl && pl.Closed;
            for (int i = 0; i < gs.Count; i++)
            {
                _grips.Add(new Grip(e, i, gs[i].x, gs[i].y, closed, gs.Count));
                _selected.Add(false);
            }
        }
    }

    public void Clear() { _grips.Clear(); _selected.Clear(); _anchor = -1; }

    /// <summary>命中测试：返回容差内离 (wx,wy) 最近的夹点序号；无则 -1。</summary>
    public int HitTest(double wx, double wy, double tol)
    {
        int best = -1; double bestD = tol * tol;
        for (int i = 0; i < _grips.Count; i++)
        {
            double dx = _grips[i].X - wx, dy = _grips[i].Y - wy, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ── 多夹点选择 ────────────────────────────────────────────────────

    public void ClearSelection()
    {
        for (int i = 0; i < _selected.Count; i++) _selected[i] = false;
        _anchor = -1;
    }

    /// <summary>清空后只选 idx，并设为锚点。</summary>
    public void SelectOnly(int idx)
    {
        ClearSelection();
        if (idx < 0 || idx >= _grips.Count) return;
        _selected[idx] = true;
        _anchor = idx;
    }

    /// <summary>Ctrl：加/减单个；选中时设为锚点，取消选中则锚点失效(下一次 Shift 不该从没亮的点起算)。</summary>
    public void ToggleGrip(int idx)
    {
        if (idx < 0 || idx >= _grips.Count) return;
        _selected[idx] = !_selected[idx];
        _anchor = _selected[idx] ? idx : -1;
    }

    /// <summary>Shift：把 [锚点, idx] 之间【同一实体内】的夹点整段加入。锚点无效或跨实体 → 退化为 SelectOnly。闭合环取短弧。锚点保持不动。</summary>
    public void SelectRangeTo(int idx)
    {
        if (idx < 0 || idx >= _grips.Count) return;
        if (_anchor < 0 || _anchor >= _grips.Count || !SameOwner(_grips[_anchor], _grips[idx]))
        {
            SelectOnly(idx);
            return;
        }
        var refG = _grips[idx];
        int a = _grips[_anchor].Index, b = refG.Index, n = refG.OwnerGripCount;
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        bool InRange(int gi)
        {
            bool direct = gi >= lo && gi <= hi;
            if (!refG.OwnerClosed || n <= 2) return direct;
            int spanDirect = hi - lo, spanWrap = n - spanDirect;
            if (spanDirect <= spanWrap) return direct;
            return gi <= lo || gi >= hi;   // 跨接缝更短 → 取补集(含两端)
        }
        for (int i = 0; i < _grips.Count; i++)
            if (SameOwner(_grips[i], refG) && InRange(_grips[i].Index)) _selected[i] = true;
    }

    public bool IsSelected(int idx) => idx >= 0 && idx < _grips.Count && _selected[idx];

    public int SelectedCount()
    {
        int n = 0;
        foreach (var s in _selected) if (s) n++;
        return n;
    }

    public List<int> SelectedIndices()
    {
        var o = new List<int>();
        for (int i = 0; i < _selected.Count; i++) if (_selected[i]) o.Add(i);
        return o;
    }

    private static bool SameOwner(Grip a, Grip b) => ReferenceEquals(a.Owner, b.Owner);
}
