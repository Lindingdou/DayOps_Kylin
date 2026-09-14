using System;
using Avalonia;
using Avalonia.Controls;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 把窗口尺寸钳进屏幕工作区。
///
/// <b>为什么需要它</b>：<c>Window.Width/Height</c> 是**逻辑像素**，界面缩放不是 1 时（本机 0.77）
/// 落到屏上会放大一截；内容超出时窗口还会继续往下长。两件事叠起来的结果是
/// **窗口底边连同「确定/保存」按钮一起被推出屏幕外，用户连点都点不到**。
///
/// 照抄原版 WPF 的尺寸也不保险 —— Avalonia 的控件行高比 WPF 高一截，同样的行数就是装不下。
/// 所以尺寸不能靠拍脑袋，得按**当前这块屏的工作区**反算。
///
/// 页脚仍应 <c>DockPanel.Dock=Bottom</c> + 内容区包 <c>ScrollViewer</c>：
/// 本类保证窗口不超出屏幕，那两条保证超出的部分是滚动而不是裁掉。
/// </summary>
internal static class WindowFit
{
    /// <summary>留给任务栏/窗口装饰的余量（工作区的比例）。</summary>
    private const double Margin = 0.92;

    /// <summary>
    /// 在窗口显示时把它钳进屏幕工作区。<paramref name="w"/> 的 Width/Height 当作**期望值**，
    /// 放得下就照给，放不下就缩到工作区以内。
    /// </summary>
    internal static void ClampToScreen(Window w)
    {
        if (w == null) return;
        w.Opened += (_, _) =>
        {
            try
            {
                var screen = w.Screens?.ScreenFromWindow(w) ?? w.Screens?.Primary;
                if (screen == null) return;

                // WorkingArea 是**物理像素**，窗口尺寸是逻辑像素 —— 必须按 Scaling 折一次，
                // 少折这一步在缩放≠1 的机器上就白钳了。
                double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
                double maxW = screen.WorkingArea.Width / scale * Margin;
                double maxH = screen.WorkingArea.Height / scale * Margin;
                if (maxW <= 0 || maxH <= 0) return;

                w.MaxWidth = maxW;
                w.MaxHeight = maxH;
                if (w.Width > maxW) w.Width = maxW;
                if (w.Height > maxH) w.Height = maxH;

                // ★ 只钳尺寸不够：窗口是相对宿主居中的，尺寸合法照样可能整体偏下、
                // 把页脚顶出屏幕底边（本窗实测就是这样 —— 150% 缩放下 600 逻辑 = 900 物理）。
                // 尺寸合规之后再把**位置**拉回工作区内。
                var wa = screen.WorkingArea;
                int hPx = (int)Math.Round(w.Height * scale);
                int wPx = (int)Math.Round(w.Width * scale);
                var pos = w.Position;
                int x = pos.X, y = pos.Y;
                if (y + hPx > wa.Y + wa.Height) y = wa.Y + wa.Height - hPx;
                if (x + wPx > wa.X + wa.Width) x = wa.X + wa.Width - wPx;
                if (y < wa.Y) y = wa.Y;
                if (x < wa.X) x = wa.X;
                if (x != pos.X || y != pos.Y) w.Position = new PixelPoint(x, y);
            }
            catch { /* 拿不到屏幕信息就维持原尺寸, 不该为此开不了窗 */ }
        };
    }
}
