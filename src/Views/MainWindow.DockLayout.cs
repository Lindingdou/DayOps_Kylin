using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DMC = Dock.Model.Mvvm.Controls;
using DCore = Dock.Model.Core;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 页面布置（停靠面板排布）持久化 —— 对应原版 <c>Infrastructure/Docking/DockLayoutPersistence</c>
/// （AvalonDock XmlLayoutSerializer 落 dock-layout.xml：启动恢复 / 变动 1s 延迟写 / 关闭同步写 / 读坏了删文件回默认）。
///
/// Dock.Avalonia 这边不用框架自带的整树序列化（跨版本脆，原版也吃过这亏），只记 <see cref="DockLayoutStore"/> 里那点骨架。
/// 恢复时校验：文档区恰好一处、不可关的三块(属性/AI 助手/信息栏)都在；不满足或解析异常 → 回默认布局并删掉坏文件。
/// 「选项 → 显示 → 恢复默认页面布置」= 删文件 + 本次关闭不再写，下次启动即默认。
/// </summary>
public partial class MainWindow
{
    private const string DockLayoutFileName = "dock-layout.json";
    private string DockLayoutFile => Path.Combine(Cfg.ConfigDir, DockLayoutFileName);
    private static readonly string[] RequiredTools = { "Props", "Assistant", "Info" };   // CanClose=false 的三块

    private Avalonia.Threading.DispatcherTimer? _dockSaveTimer;
    private bool _dockLayoutResetRequested;   // 用户点了「恢复默认」：本次关闭不写, 免得又把当前布局记回去
    private bool _dockLayoutRestored;         // 本次启动是否用了记录的布局(状态栏/自检回显用)

    // ── 落盘 ──
    private void SaveDockLayout()
    {
        if (_dockLayoutResetRequested) return;
        try
        {
            var lf = DockLayoutStore.Capture(Dock?.Layout);
            if (lf == null) return;
            string tmp = DockLayoutFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(lf, DockLayoutStore.Json));
            File.Move(tmp, DockLayoutFile, overwrite: true);   // 原子替换: 中途断电保留旧布局
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("布局", $"写页面布置失败: {ex.Message}"); }
    }

    /// <summary>面板拖动/关闭/钉住等变动后 1s 静默期落一次盘(同原版 debounce)；分隔条比例在关闭时一并记。</summary>
    private void ScheduleDockLayoutSave()
    {
        if (_dockSaveTimer == null)
        {
            _dockSaveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _dockSaveTimer.Tick += (_, _) => { _dockSaveTimer.Stop(); SaveDockLayout(); };
        }
        _dockSaveTimer.Stop(); _dockSaveTimer.Start();
    }

    private void HookDockLayoutPersistence(Dock.Model.Mvvm.Factory f)
    {
        f.DockableMoved += (_, _) => ScheduleDockLayoutSave();
        f.DockableSwapped += (_, _) => ScheduleDockLayoutSave();
        f.DockableClosed += (_, _) => ScheduleDockLayoutSave();
        f.DockableRemoved += (_, _) => ScheduleDockLayoutSave();
        f.DockablePinned += (_, _) => ScheduleDockLayoutSave();
        f.DockableUnpinned += (_, _) => ScheduleDockLayoutSave();
        f.ActiveDockableChanged += (_, e) => { if (e.Dockable is DMC.Tool) ScheduleDockLayoutSave(); };
    }

    /// <summary>「恢复默认页面布置」：删记录 + 本次关闭不再写；下次启动即默认排布。</summary>
    private void ResetDockLayout()
    {
        _dockLayoutResetRequested = true;
        try { if (File.Exists(DockLayoutFile)) File.Delete(DockLayoutFile); }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("布局", $"删页面布置记录失败: {ex.Message}"); }
    }

    // ── 恢复 ──
    /// <summary>
    /// 按记录重建布局骨架。返回顶层容器；文件不存在/校验不过/异常 → null(调用方走默认布局)，坏文件顺手删掉。
    /// </summary>
    private DCore.IDock? TryRestoreDockLayout(Dock.Model.Mvvm.Factory f, IReadOnlyDictionary<string, DMC.Tool> tools,
        DMC.DocumentDock docDock, out List<(DCore.Alignment side, string id)> pinned)
    {
        pinned = new();
        string path = DockLayoutFile;
        if (!File.Exists(path)) return null;
        try
        {
            var lf = JsonSerializer.Deserialize<DockLayoutStore.LayoutFile>(File.ReadAllText(path), DockLayoutStore.Json)
                     ?? throw new InvalidDataException("空布局");
            var top = DockLayoutStore.Rebuild(lf, f, tools, docDock, RequiredTools, out pinned);
            _dockLayoutRestored = true;
            return top;
        }
        catch (Exception ex)
        {
            PitMine3D.Kylin.CrashLog.Write("布局", $"页面布置记录不可用, 回默认: {ex.Message}");
            try { File.Delete(path); } catch { }
            pinned = new();
            return null;
        }
    }

    /// <summary>一行文字描述当前排布(自检/诊断)。</summary>
    private string DescribeDockLayout() => DockLayoutStore.Describe(DockLayoutStore.Capture(Dock?.Layout));

    /// <summary>布局初始化完成后把记录里钉住(自动隐藏)的面板重新钉上。</summary>
    private static void ApplyPinned(Dock.Model.Mvvm.Factory f, IReadOnlyDictionary<string, DMC.Tool> tools, List<(DCore.Alignment side, string id)> pinned)
    {
        foreach (var (_, id) in pinned)
            if (tools.TryGetValue(id, out var t) && !f.IsDockablePinned(t)) { try { f.PinDockable(t); } catch { /* 钉不上就保持停靠 */ } }
    }
}
