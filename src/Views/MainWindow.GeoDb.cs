using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.GeoDb;

namespace PitMine3D.Kylin.Views;

/// <summary>主窗口 ↔ 地质与工程信息数据库页面(§四/§八)桥接: 上下文构建 + 一次性视口拾取。</summary>
public partial class MainWindow
{
    private GeoDbContext? _geoCtx;
    private Action<double, double>? _oneShotPick;   // 视口一次性拾取回调(NaN,NaN = 取消)

    /// <summary>构建(或复用)页面上下文; 数据库打开失败返回 null(状态栏已报)。</summary>
    private GeoDbContext? GeoCtx()
    {
        var db = EnsureGeoDb();
        if (db == null) return null;
        return _geoCtx ??= new GeoDbContext
        {
            Conn = db.Connection,
            Owner = this,
            Status = s => StatusMsg.Text = s,
            AddToScene = (ents, layer, bounds) =>
            {
                if (ents.Count == 0) return;
                BeginChange();
                if (!string.IsNullOrEmpty(layer))
                {
                    var l = _layers.Get(layer) ?? _layers.EnsureImported(layer, 0.3f, 0.75f, 0.95f);
                    foreach (var e in ents) e.LayerName = l.Name;
                }
                else foreach (var e in ents) AssignLayer(e);
                foreach (var e in ents) _scene.Add(e);
                RefreshScene();
                if (bounds != null && bounds.Length == 4 && bounds[2] > bounds[0] && bounds[3] > bounds[1]) Viewport.FitBounds(bounds);
            },
            RemoveLayerEntities = layer =>
            {
                int n = _scene.Entities.RemoveAll(e => e.LayerName == layer);
                if (n > 0) { _selected.RemoveAll(e => e.LayerName == layer); RefreshScene(); }
                return n;
            },
            PickPointAsync = prompt =>
            {
                var tcs = new TaskCompletionSource<(double x, double y)?>();
                _oneShotPick = (x, y) => tcs.TrySetResult(double.IsNaN(x) ? null : (x, y));
                StatusMsg.Text = string.IsNullOrEmpty(prompt) ? "在视口中点击拾取位置（Esc 取消）" : prompt;
                Activate();
                return tcs.Task;
            },
            SceneLayerNames = () => { var r = new List<string>(); foreach (var l in _layers.Layers) r.Add(l.Name); return r; },
            LayerVertices = layer =>
            {
                var r = new List<(double x, double y, double z)>();
                foreach (var e in _scene.Entities)
                {
                    if (e.LayerName != layer) continue;
                    if (e is LineEntity ln) { r.Add((ln.X0, ln.Y0, ln.Elevation)); r.Add((ln.X1, ln.Y1, ln.Elevation)); }
                    else if (e is PolylineEntity pl) foreach (var p in pl.Points) r.Add((p.x, p.y, pl.Elevation));
                    else if (e is PointEntity pt) r.Add((pt.X, pt.Y, pt.Elevation));
                }
                return r;
            },
            SaveTextAsync = async (title, name, content) =>
            {
                string ext = System.IO.Path.GetExtension(name).TrimStart('.');
                if (string.IsNullOrEmpty(ext)) ext = "csv";
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = title, DefaultExtension = ext, SuggestedFileName = name,
                    FileTypeChoices = new[] { new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } } }
                });
                if (file == null) return null;
                try
                {
                    System.IO.File.WriteAllText(file.Path.LocalPath, content, new System.Text.UTF8Encoding(true));
                    return file.Path.LocalPath;
                }
                catch (Exception ex) { StatusMsg.Text = $"{title}：写入失败 {ex.Message}"; return null; }
            },
            OpenFileAsync = async (title, patterns) =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title, AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType(string.Join("/", patterns)) { Patterns = patterns } }
                });
                return files.Count == 0 ? null : files[0].Path.LocalPath;
            },
            OpenFolderAsync = async title =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
                return folders.Count == 0 ? null : folders[0].Path.LocalPath;
            },
            RunCommand = cmd => DispatchRibbon(cmd),
        };
    }

    /// <summary>
    /// Ribbon「地质与工程信息数据库」页 24 个功能项 → 各自独立页面(原 GeoDataBase 插件 24 窗口)。
    /// 只按按钮 Tag 精确匹配; 其它中文别名仍走原文本报表命令。返回 true 表示已由页面接管。
    /// </summary>
    private async Task<bool> TryOpenGeoDbPageAsync(string cmd)
    {
        switch (cmd)
        {
            // ── 钻孔管理 ──
            case "导入钻孔数据": Open(c => new BoreholeImportWindow(c)); return true;
            case "展绘钻孔": { var c = GeoCtx(); if (c != null) await BoreholeSelectDialog.DrawBoreholesAsync(c); return true; }
            case "虚拟钻孔": Open(c => new VirtualDrillWindow(c)); return true;
            case "开孔坐标管理": Open(c => new BoreholeDataWindow(c)); return true;
            case "原始钻孔柱状图": Open(c => new OriginalBoreholeColumnWindow(c)); return true;
            case "展绘层位数据": { var c = GeoCtx(); if (c != null) await HorizonPointDialog.DrawHorizonPointsAsync(c); return true; }
            // ── 煤质管理 ──
            case "煤质数据管理": Open(c => new CoalQualityDataWindow(c)); return true;
            case "空间分布": Open(c => new CoalQualitySpatialWindow(c)); return true;
            case "数据看板": Open(c => new CoalQualityDashboardWindow(c)); return true;
            case "统计分析": Open(c => new CoalQualityStatsWindow(c)); return true;
            case "钻孔柱状图": Open(c => new CoalQualityBoreholeColumnWindow(c)); return true;
            // ── 工艺参数管理 ──
            case "工艺架构定义": Open(c => new ProcessArchitectureWindow(c)); return true;
            case "平盘工艺地图": Open(c => new LocationProcessMapWindow(c)); return true;
            case "参数模板库": Open(c => new ParameterTemplateWindow(c)); return true;
            case "现场验收录入": Open(c => new ParameterAcceptanceWindow(c)); return true;
        }
        return false;

        void Open<T>(Func<GeoDbContext, T> make) where T : Avalonia.Controls.Window
        {
            var c = GeoCtx(); if (c == null) return;
            GeoDbWindows.Show(c, () => make(c));
            StatusMsg.Text = $"已打开「{cmd}」页面（地质与工程信息数据库）";
        }
    }

    /// <summary>视口左键按下时若有一次性拾取挂起, 消费之; 返回 true 表示已处理。</summary>
    private bool ConsumeOneShotPick(double sx, double sy)
    {
        if (_oneShotPick == null) return false;
        var cb = _oneShotPick; _oneShotPick = null;
        var wp = Viewport.ScreenToWorld(sx, sy);
        if (wp == null) cb(double.NaN, double.NaN);
        else { cb(wp.Value.x, wp.Value.y); StatusMsg.Text = $"已拾取 ({wp.Value.x:0.##}, {wp.Value.y:0.##})"; }
        return true;
    }

    /// <summary>Esc 取消挂起的一次性拾取; 返回 true 表示有取消动作。</summary>
    private bool CancelOneShotPick()
    {
        if (_oneShotPick == null) return false;
        var cb = _oneShotPick; _oneShotPick = null;
        cb(double.NaN, double.NaN);
        StatusMsg.Text = "已取消拾取";
        return true;
    }
}
