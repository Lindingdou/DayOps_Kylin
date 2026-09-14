using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点选 / 框选对选择集的更新规则（忠实原 <c>Picking::DoPointPick</c> / <c>UpdateBoxSelection</c>）：
/// <list type="bullet">
/// <item>点选：普通点击 = 替换为命中者、点空清空；<b>Ctrl = 切换</b>（未选→加选，已选→减选），Ctrl 点空处保持不清；
///   Shift 点选原版不特殊处理（同普通点击）。</item>
/// <item>框选：普通 = 替换；<b>Ctrl = 并入</b>；<b>Shift = 剔除</b>。</item>
/// <item>编辑命令的「选择对象」阶段（accumulate）：点选切换、框选并入、点空保持——Kylin 既有语义（AutoCAD 命令内选集累加）。</item>
/// </list>
/// 纯逻辑、按引用相等，可单测。
/// </summary>
public static class SelectionOps
{
    public enum Modifier { None, Ctrl, Shift }

    /// <summary>点选后更新 sel。hit=null 表示点在空处。</summary>
    public static void ApplyPick<T>(List<T> sel, T? hit, Modifier mod, bool accumulate) where T : class
    {
        if (hit == null)
        {
            if (!accumulate && mod != Modifier.Ctrl) sel.Clear();   // 原版：无 Ctrl 点空 → 清空；Ctrl 点空 → 保持
            return;
        }
        if (accumulate || mod == Modifier.Ctrl)
        {
            if (!sel.Remove(hit)) sel.Add(hit);   // 切换
            return;
        }
        sel.Clear(); sel.Add(hit);                // 替换
    }

    /// <summary>框选后更新 sel。matched 为框内命中的实体（顺序保留、去重）。</summary>
    public static void ApplyBox<T>(List<T> sel, IEnumerable<T> matched, Modifier mod, bool accumulate) where T : class
    {
        if (accumulate || mod == Modifier.Ctrl)
        {
            foreach (var e in matched) if (!sel.Contains(e)) sel.Add(e);
            return;
        }
        if (mod == Modifier.Shift)
        {
            foreach (var e in matched) sel.Remove(e);
            return;
        }
        sel.Clear();
        foreach (var e in matched) if (!sel.Contains(e)) sel.Add(e);
    }
}
