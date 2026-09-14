using System;
using Avalonia.Controls;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 用户配置的读取与落盘 —— 同原版「关闭时记录配置」(MainWindow.OnWindowClosing → FlushPersistence)：
/// 退出时把工作目录、选项(栅格/对象捕捉/容差/十字光标/功能区缩放/主题/视口配色/夹点)、状态栏开关(正交/栅格/栅格捕捉/捕捉模式)
/// 与窗口尺寸写进 <c>config.json</c>，页面布置写进 <c>dock-layout.json</c>，下次启动照原样恢复。
///
/// 配置文件：用户数据目录 ~/.local/share/PitMine3D.Kylin/config.json（与 crash.log 同目录，见 <see cref="PitMine3D.Kylin.UserSettings"/>）。
/// 早先按项散落的 cursor-size.txt / ribbon-scale.txt 仍会被读一次(迁移)，此后统一走 config.json。
/// </summary>
public partial class MainWindow
{
    // 键名沿用原版 Options.* 命名，两边配置一眼能对上
    private const string KeyCursorSize   = "Options.Display.CursorSize";      // double 2..100
    private const string KeyRibbonScale  = "Options.Display.RibbonScale";     // double 0=自动 / 目标屏宽
    private const string KeyGridOn       = "Options.Display.Grid";            // bool
    private const string KeySnapOn       = "Options.Snap.Enabled";            // bool 对象捕捉
    private const string KeySnapTolPx    = "Options.Snap.PickBox";            // double 屏幕像素
    private const string KeySnapMask     = "Options.Snap.ExtraMask";          // int 交点/垂足/最近 位掩码
    private const string KeyOrthoOn      = "Options.Draft.Ortho";             // bool 正交
    private const string KeyGridSnapOn   = "Options.Draft.GridSnap";          // bool 栅格捕捉
    private const string KeyGridStep     = "Options.Draft.GridStep";          // double 栅格步长
    private const string KeyWinMaximized = "Window.Maximized";                // bool
    private const string KeyWinWidth     = "Window.Width";                    // double
    private const string KeyWinHeight    = "Window.Height";                   // double

    private static PitMine3D.Kylin.UserSettings Cfg => PitMine3D.Kylin.UserSettings.Current;

    /// <summary>
    /// 上次退出时不是最大化 → 按记下的尺寸开窗，返回 true（否则维持默认的最大化）。
    /// 只记尺寸不记位置：多屏/换分辨率后按旧坐标开窗会跑到屏幕外，用户会以为"程序没起来"。
    /// </summary>
    private bool RestoreWindowGeometry()
    {
        try
        {
            if (Cfg.Get<bool>(KeyWinMaximized, true)) return false;
            double w = Cfg.Get<double>(KeyWinWidth, 0), h = Cfg.Get<double>(KeyWinHeight, 0);
            if (w < 900 || h < 600 || w > 20000 || h > 20000) return false;   // 明显异常的记录不认
            WindowState = WindowState.Normal;
            Width = w; Height = h;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return true;
        }
        catch { return false; }
    }

    /// <summary>启动时套用上次的配置(界面控件已建好, 视口相关的推到 Opened 再套)。</summary>
    private void LoadUserSettings()
    {
        try
        {
            _snapTolPx = Clamp(Cfg.Get<double>(KeySnapTolPx, _snapTolPx), 2, 60);
            _snapStep = Clamp(Cfg.Get<double>(KeyGridStep, _snapStep), 1e-6, 1e6);
            _snapExtraMask = Cfg.Get<int>(KeySnapMask, _snapExtraMask);
            _orthoOn = Cfg.Get<bool>(KeyOrthoOn, _orthoOn);
            _snapOn = Cfg.Get<bool>(KeyGridSnapOn, _snapOn);

            _syncToggle = true;   // 直接改开关的选中态, 不要触发"正交: 开"这类回显
            if (OrthoToggle != null) OrthoToggle.IsChecked = _orthoOn;
            if (GridSnapToggle != null) GridSnapToggle.IsChecked = _snapOn;
            if (SnapToggle != null) SnapToggle.IsChecked = Cfg.Get<bool>(KeySnapOn, SnapToggle.IsChecked == true);
            _syncToggle = false;

            // 栅格要动视口(ToggleGrid), 视口是懒建的 —— 等窗口显示后再套
            bool gridWanted = Cfg.Get<bool>(KeyGridOn, _gridOn);
            if (gridWanted != _gridOn) Opened += (_, _) => SetGrid(gridWanted);
        }
        catch (Exception ex)
        {
            _syncToggle = false;
            PitMine3D.Kylin.CrashLog.Write("配置", $"套用上次配置失败: {ex.Message}");
        }
    }

    /// <summary>关闭时把当前配置整份写下并同步落盘(同原版 FlushPersistence, 毫秒级, 不拖慢关窗)。</summary>
    private void SaveUserSettings()
    {
        try
        {
            SaveWorkingDirs();
            Cfg.Set(KeyCursorSize, _cursorSizePct);
            Cfg.Set(KeyRibbonScale, _ribbonScaleSetting);
            Cfg.Set(KeyGridOn, _gridOn);
            Cfg.Set(KeySnapOn, SnapToggle?.IsChecked == true);
            Cfg.Set(KeySnapTolPx, _snapTolPx);
            Cfg.Set(KeySnapMask, _snapExtraMask);
            Cfg.Set(KeyOrthoOn, _orthoOn);
            Cfg.Set(KeyGridSnapOn, _snapOn);
            Cfg.Set(KeyGridStep, _snapStep);
            bool max = WindowState == WindowState.Maximized;
            Cfg.Set(KeyWinMaximized, max);
            if (!max) { Cfg.Set(KeyWinWidth, Width); Cfg.Set(KeyWinHeight, Height); }
            // 选项·显示/选择集(主题/视口配色/夹点/Gizmo)按当前生效状态整份记下(见 MainWindow.Options.cs)
            SaveDisplaySettings();
            Cfg.Set(KeyTheme, IsDarkTheme ? "Dark" : "Light");
            Cfg.Flush();
            SaveDockLayout();   // 页面布置(面板排布/比例/当前页/钉住)——见 MainWindow.DockLayout.cs
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("配置", $"保存配置失败: {ex.Message}"); }
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
