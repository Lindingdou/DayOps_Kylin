using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「日常生产组织」页签的开窗通路 —— 对应原 <c>TaskLibPlugin.OpenWindow(key, factory)</c>：
/// 统一 modeless 单例开窗，同一个窗已开则前置，否则用 factory 造新窗；出错只写状态栏不抛。
///
/// <para>窗口本身取数一律走 <see cref="Data.EquipmentDataContext"/> 静态门面（GeoDatabase 打开时注入），
/// 所以这里先 <c>EnsureGeoDb()</c> 把库拉起来再开窗 —— 否则窗里所有走库的路径都静默退到兜底。</para>
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<Type, Window> _taskWindows = new();

    /// <summary>自检用的一盘样例（两面采装 + 一台钻机 + 12:00 一炮），只为核对甘特画法；不进任何台账。</summary>
    private static PitMine3D.Kylin.TaskLib.Engine.ExploderResult TaskLibSelftestPlan()
    {
        var cfg = new PitMine3D.Kylin.TaskLib.Engine.ExploderConfig
        {
            DateLabel = "2026-06-17 周三", IdPrefix = "D", NowHour = 10.5, BlastStart = 12, BlastEnd = 12.5,
            Shifts = { new("早", 0, 8), new("中", 8, 16), new("夜", 16, 24) },
            Blend = new PitMine3D.Kylin.TaskLib.Engine.BlendStandard { MaxAshPct = 12.5 },
        };
        cfg.Faces.Add(new PitMine3D.Kylin.TaskLib.Engine.FaceInput
        {
            Zone = "4煤南", Process = PitMine3D.Kylin.TaskLib.Domain.ProcessType.Load, DayTargetM3 = 4800, MaterialCode = "coal",
            DestinationName = "1号破碎站", DestinationKind = PitMine3D.Kylin.TaskLib.Domain.SinkKind.Crusher, HaulDistanceKm = 2.6,
            Group = new PitMine3D.Kylin.TaskLib.Domain.EquipmentGroup { MainEquipment = "WK-35A", GroupCapacityM3PerH = 350, RecommendedTrucks = 5, Trucks = { "T1", "T2", "T3", "T4", "T5" } },
        });
        cfg.Faces.Add(new PitMine3D.Kylin.TaskLib.Engine.FaceInput
        {
            Zone = "4煤北", Process = PitMine3D.Kylin.TaskLib.Domain.ProcessType.Load, DayTargetM3 = 3600, MaterialCode = "rock",
            DestinationName = "内排土场", DestinationKind = PitMine3D.Kylin.TaskLib.Domain.SinkKind.InternalDump, HaulDistanceKm = 1.4,
            Group = new PitMine3D.Kylin.TaskLib.Domain.EquipmentGroup { MainEquipment = "PH2800", GroupCapacityM3PerH = 300, RecommendedTrucks = 4, Trucks = { "T6", "T7", "T8" } },
        });
        cfg.Drills.Add(new PitMine3D.Kylin.TaskLib.Engine.DrillInput { EquipId = "ZJ-01", Zone = "4煤南", Start = 0, End = 6 });
        return PitMine3D.Kylin.TaskLib.Engine.TaskExploder.Explode(cfg);
    }

    /// <summary>样例盘子的设备行（照 ProductionPlanContext.Roster 的排法：主设备一行、卡车紧跟自己的铲）。</summary>
    private static List<PitMine3D.Kylin.TaskLib.Domain.RosterEntry> TaskLibSelftestRoster(PitMine3D.Kylin.TaskLib.Engine.ExploderResult plan)
    {
        static string Cat(string id) => id.StartsWith("ZJ") || id.StartsWith("DR") ? "钻机" : id.StartsWith("T") ? "卡车" : id.StartsWith("BD") ? "推土机" : "电铲";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<PitMine3D.Kylin.TaskLib.Domain.RosterEntry>();
        foreach (var t in plan.Tasks)
        {
            string main = t.Group?.MainEquipment ?? "";
            if (main.Length == 0) { if (seen.Add(t.Process.ToString())) list.Add(new(t.Process + "（未指定执行人）", t.Process + "（未指定执行人）", "爆破", false)); continue; }
            if (seen.Add(main)) list.Add(new(main, $"{main} {Cat(main)}", Cat(main), false));
            foreach (var tr in t.Group!.Trucks) if (seen.Add(tr)) list.Add(new(tr, $"{tr} 卡车", "卡车", true));
        }
        return list;
    }

    // ── 宿主能力注入（原 TaskLibPlugin.Initialize 里的 SimHost.Inject(context.Capabilities)）──
    private KylinViewCapability? _taskViewCap;
    private void EnsureTaskLibHost()
    {
        if (_taskViewCap != null) return;
        _taskViewCap = new KylinViewCapability(this);
        PitMine3D.Kylin.TaskLib.Simulation.SimHost.View = _taskViewCap;
    }

    /// <summary>给能力层用：按功能区命令名派发（DispatchRibbon 是主窗私有）。</summary>
    internal void RunRibbonCommand(string cmd) => DispatchRibbon(cmd);

    /// <summary>给能力层用：场景重绘。</summary>
    internal void RequestSceneRefresh() => RefreshScene();

    /// <summary>正射底图贴到场景（原 IViewCapability.SetOrthophoto 在 Kylin 的落点：逐顶点上色）。</summary>
    internal string ApplyOrthoSampler(Cad.OrthoBasemap.ISampler sampler)
    {
        var targets = Cad.OrthoBasemap.TargetsOf(_scene.Entities);
        if (targets.Count == 0) return "场景里没有三角网或点云 —— 底图是贴在面上的，先加载地表数据（如「加载点云」「2.5D TIN」）再贴。";
        BeginChange();
        var r = Cad.OrthoBasemap.Apply(sampler, targets);
        RefreshScene();
        StatusMsg.Text = r.Message + (r.Notes.Count > 0 ? "　◆ " + string.Join("；", r.Notes) : "");
        return StatusMsg.Text;
    }

    internal string ClearOrthoSampler()
    {
        var targets = Cad.OrthoBasemap.TargetsOf(_scene.Entities);
        if (targets.Count == 0) return "场景里没有贴过底图的对象";
        BeginChange();
        int n = Cad.OrthoBasemap.Clear(targets);
        RefreshScene();
        StatusMsg.Text = $"已清除影像底图：{n} 个对象恢复原色";
        return StatusMsg.Text;
    }

    private void OpenTaskWindow<T>(Func<T> factory) where T : Window
    {
        try
        {
            // ★ EnsureGeoDb 首次返回 null（连接还没建好），命令会被重新派发一次；窗只在库就绪后开。
            if (EnsureGeoDb() == null) return;
            EnsureTaskLibHost();
            if (_taskWindows.TryGetValue(typeof(T), out var existing) && existing.IsVisible)
            {
                existing.Activate();
                StatusMsg.Text = $"{existing.Title} 已在前台";
                return;
            }
            var win = factory();
            _taskWindows[typeof(T)] = win;
            win.Closed += (_, _) => _taskWindows.Remove(typeof(T));
            GeoDb.GeoDbWindows.NoteLast(win);   // 让 @页面截图 自检拍得到
            win.Show(this);
            // 自检：台账为空时甘特一行都没有，拍不出条形 —— 带「甘特样例」字样时喂原版 DailyGanttModel.Sample() 核对画法
            if (win is Views.TaskLib.DailyGanttWindow gw
                && (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") ?? "").Contains("甘特样例"))
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var plan = TaskLibSelftestPlan();
                    gw.SetPlan(plan, "2026-06-17 周三", 10.5, 12, 12.5, TaskLibSelftestRoster(plan));
                });   // Show 里 Opened 已同步触发过，挂事件晚了
            // 自检：当日无任务时演示面板是空的 —— 带「面板样例」字样时喂合成图元核对 SimPanelOverlay 的画法
            if (win is Views.TaskLib.DynamicAdjustWindow dw
                && (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") ?? "").Contains("面板样例"))
                Avalonia.Threading.Dispatcher.UIThread.Post(dw.SelftestPanelSample, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            StatusMsg.Text = $"[日常生产组织] 打开 {typeof(T).Name} 失败: {ex.Message}";
        }
    }
}
