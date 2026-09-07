using System;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>视口左/中键按下要做什么。</summary>
public enum ViewportPress
{
    None,        // 不接管(右键 → 上下文菜单)
    BoxSelect,   // 拖=框选, 不拖=点选
    Orbit,       // 拖=轨道旋转, 不拖=点选
    Pan          // 拖=平移
}

/// <summary>
/// 视口鼠标绑定(纯逻辑, 可单测)：
///   · 2D —— 左键=框选/点选, 中键=平移。
///   · 3D —— 左键=轨道旋转(不拖则点选), 中键=平移;
///           开「选择模式」或按住 Shift 时左键改为框选/点选(不旋转)。
/// </summary>
public static class NavBinding
{
    /// <param name="selectable">左键当前可用于选择(非绘制/测量/编辑取点等占用态)。</param>
    public static ViewportPress OnPress(bool left, bool middle, bool shift, bool is2D, bool selectMode, bool selectable)
    {
        if (left && selectable && (is2D || selectMode || shift)) return ViewportPress.BoxSelect;
        if (middle) return ViewportPress.Pan;
        if (left) return ViewportPress.Orbit;
        return ViewportPress.None;
    }

    /// <summary>松开时位移小于阈值 = 单击(按点选处理)，否则算拖拽。</summary>
    public static bool IsClick(double dx, double dy, double tolPx = 4)
        => Math.Abs(dx) < tolPx && Math.Abs(dy) < tolPx;
}
