using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // OpenGL 上下文就绪后，把真实后端版本显示到视口与状态栏
        Viewport.GlReady += backend =>
        {
            GlInfo.Text = $"渲染后端: {backend}";
            StatusMsg.Text = $"OpenGL 就绪 · {backend}";
        };

        // 视口交互：在宿主 Panel（可命中）上收指针事件，转发到相机。
        // OpenGlControlBase 自身无背景时命中测试不可靠，直接在其上收事件在部分后端收不到，
        // 故统一在 ViewportHost（Background=Transparent → 全区可命中）上处理。
        ViewportHost.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(ViewportHost).Properties;
            _lastPointer = e.GetPosition(ViewportHost);
            _pressPos = _lastPointer;

            // 测距模式：左键取点（第一/第二点）
            if (_measure != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null)
                {
                    var first = _measure.First;
                    var d = _measure.AddPoint(wp.Value.x, wp.Value.y);
                    if (d == null)
                        StatusMsg.Text = "测距：点第二点";
                    else
                    {
                        StatusMsg.Text = $"距离 = {d:0.###}";
                        if (first != null)
                            Viewport.SetHighlight(new float[]
                            {
                                (float)first.Value.x, (float)first.Value.y, 0, 0, 0, 0,
                                (float)wp.Value.x,    (float)wp.Value.y,    0, 0, 0, 0
                            });
                        _measure = null;
                    }
                }
                return;
            }

            // 编辑（移动/复制/镜像）：左键取点（与命令行坐标共用 FeedPoint）
            if (_editMode != EditMode.None && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null) FeedPoint(wp.Value.x, wp.Value.y);
                return;
            }

            // 修剪/延伸：点目标线 → 其近端点移到与边界线的交点
            if (_trimActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null && _selected.Count == 1 && _selected[0] is LineEntity boundary)
                {
                    var hit = _scene.Pick(wp.Value.x, wp.Value.y, SnapTolWorld(_lastPointer) * 3, _layers.IsSelectable);
                    if (hit is LineEntity target && !ReferenceEquals(target, boundary))
                    {
                        var isect = LineMath.IntersectInfinite(target.X0, target.Y0, target.X1, target.Y1,
                                                               boundary.X0, boundary.Y0, boundary.X1, boundary.Y1);
                        if (isect != null)
                        {
                            var nl = (LineEntity)target.Apply(Affine2.Translate(0, 0));   // 克隆
                            double d0 = Dist2(wp.Value, target.X0, target.Y0), d1 = Dist2(wp.Value, target.X1, target.Y1);
                            if (d0 < d1) { nl.X0 = isect.Value.x; nl.Y0 = isect.Value.y; }
                            else { nl.X1 = isect.Value.x; nl.Y1 = isect.Value.y; }
                            BeginChange();
                            _scene.Replace(target, nl);
                            StatusMsg.Text = "已修剪/延伸";
                        }
                        else StatusMsg.Text = "与边界平行，无法修剪/延伸";
                    }
                    else StatusMsg.Text = "未点中目标直线";
                    RefreshScene();
                }
                _trimActive = false;
                return;
            }

            // 打断：取两点 → 移除选中实体两点间的一段
            if (_breakActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null && _selected.Count == 1)
                {
                    _breakPts.Add((wp.Value.x, wp.Value.y));
                    if (_breakPts.Count < 2) StatusMsg.Text = "打断：指定第二点";
                    else
                    {
                        var parts = _selected[0].Break(_breakPts[0].x, _breakPts[0].y, _breakPts[1].x, _breakPts[1].y);
                        if (parts != null)
                        {
                            BeginChange();
                            _scene.Remove(_selected[0]);
                            foreach (var pe in parts) _scene.Add(pe);
                            _selected.Clear(); Viewport.SetHighlight(null);
                            StatusMsg.Text = $"已打断（剩 {parts.Count} 段）";
                        }
                        else StatusMsg.Text = "该实体暂不支持打断（记录：仅直线，多段线/圆弧待做）";
                        _breakActive = false; _breakPts.Clear();
                        RefreshScene();
                    }
                }
                return;
            }

            // 偏移：点击一侧 → 偏移选中实体（保留原实体颜色/图层）
            if (_offsetActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null && _selected.Count == 1)
                {
                    var off = _selected[0].Offset(wp.Value.x, wp.Value.y);
                    if (off != null) { BeginChange(); _scene.Add(off); StatusMsg.Text = "已偏移"; }
                    else StatusMsg.Text = "该实体暂不支持偏移（记录：多段线/圆弧偏移待做）";
                    RefreshScene();
                }
                _offsetActive = false;
                return;
            }

            // 滑动多段线：按住左键开始，拖动自动采样，松开成线
            if (_slideActive && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                e.Pointer.Capture(ViewportHost);
                _slideDragging = true;
                _slidePts.Clear();
                var wp = Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null) { _slidePts.Add((wp.Value.x, wp.Value.y)); _slideLastScreen = _lastPointer; }
                StatusMsg.Text = "滑动多段线：按住拖动…松开结束";
                return;
            }

            // 绘制工具：左键喂点（与命令行坐标共用 FeedPoint）
            if (_tool != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null) FeedPoint(wp.Value.x, wp.Value.y);
                return;
            }

            if (props.IsMiddleButtonPressed)
                _nav = NavMode.Pan;                                       // 中键拖拽 = 平移
            else if (props.IsLeftButtonPressed)
                _nav = Viewport.Is2DView ? NavMode.Pan : NavMode.Orbit;   // 2D 左键平移 / 3D 左键旋转
            else
                _nav = NavMode.None;                                      // 右键留给上下文菜单
            if (_nav != NavMode.None) e.Pointer.Capture(ViewportHost);
        };
        ViewportHost.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            var w = Viewport.ScreenToWorld(p.X, p.Y);

            // 滑动多段线：拖动中按像素间距采样
            if (_slideDragging)
            {
                double dpx = p.X - _slideLastScreen.X, dpy = p.Y - _slideLastScreen.Y;
                if (dpx * dpx + dpy * dpy >= 36 && w != null)   // 移动 ≥6px 采一点
                { _slidePts.Add((w.Value.x, w.Value.y)); _slideLastScreen = p; }
                CoordText.Text = w != null ? $"X {w.Value.x:0.00}  Y {w.Value.y:0.00}  [滑动 {_slidePts.Count}]" : "";
                RefreshScene();
                _lastPointer = p;
                return;
            }

            // 对象捕捉：吸附到最近顶点
            _snapWorld = null;
            if (w != null && SnapToggle.IsChecked == true && _lastImport != null)
            {
                double tol = SnapTolWorld(p);
                _snapWorld = SnapPoints.FindNearest(_lastImport.LineVertices, w.Value.x, w.Value.y, tol);
                if (_snapWorld != null)
                {
                    Viewport.SetSnapMarker(SnapCross(_snapWorld.Value.x, _snapWorld.Value.y, tol * 0.6));
                    _snapShown = true;
                }
                else if (_snapShown) { Viewport.SetSnapMarker(null); _snapShown = false; }
            }
            else if (_snapShown) { Viewport.SetSnapMarker(null); _snapShown = false; }

            var shown = _snapWorld ?? w;
            CoordText.Text = shown != null
                ? $"X {shown.Value.x:0.00}  Y {shown.Value.y:0.00}{(_snapWorld != null ? "  [捕捉]" : "")}"
                : $"视口 px  X {p.X:0}  Y {p.Y:0}";

            _cursorWorld = shown;
            if (_tool != null && _nav == NavMode.None) RefreshScene();   // 橡皮筋预览随光标刷新

            if (_nav == NavMode.Pan)
                Viewport.Pan(_lastPointer.X, _lastPointer.Y, p.X, p.Y);
            else if (_nav == NavMode.Orbit)
                Viewport.Orbit((p.X - _lastPointer.X) * 0.01, (p.Y - _lastPointer.Y) * 0.01);
            _lastPointer = p;
        };
        ViewportHost.PointerReleased += (_, e) =>
        {
            var rel = e.GetPosition(ViewportHost);

            // 滑动多段线：松开 → 采样点成多段线
            if (_slideDragging)
            {
                _slideDragging = false;
                e.Pointer.Capture(null);
                if (_slidePts.Count >= 2)
                {
                    BeginChange();
                    var pl = new PolylineEntity { Points = new List<(double, double)>(_slidePts) };
                    AssignLayer(pl); _scene.Add(pl);
                    StatusMsg.Text = $"滑动多段线完成（{_slidePts.Count} 点，共 {_scene.Count}）";
                }
                else StatusMsg.Text = "滑动多段线：点数不足，已取消";
                _slidePts.Clear();
                RefreshScene();
                return;
            }

            // 无拖动 + 非绘制/测距 → 视为点选
            bool wasClick = _nav != NavMode.None && _tool == null && _measure == null
                && System.Math.Abs(rel.X - _pressPos.X) < 4 && System.Math.Abs(rel.Y - _pressPos.Y) < 4;
            _nav = NavMode.None;
            e.Pointer.Capture(null);
            if (wasClick) PickAt(rel);
        };
        ViewportHost.PointerWheelChanged += (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            Viewport.ZoomAt(p.X, p.Y, e.Delta.Y > 0 ? 0.9 : 1.1);        // 朝光标缩放
        };
        ViewportHost.DoubleTapped += (_, _) =>
        {
            if (_tool != null && _tool.IsMultiPoint)                      // 双击结束多段线
            {
                var e = _tool.Finish();
                if (e != null) { BeginChange(); AssignLayer(e); _scene.Add(e); }
                RefreshScene();
                StatusMsg.Text = $"多段线完成（已画 {_scene.Count}）";
            }
            else Viewport.ZoomExtents();                                  // 否则 = 范围缩放
        };

        // 对象树选类型 → 视口高亮该类型几何
        ObjectTree.SelectionChanged += OnObjectTreeSelect;

        // ESC：退出当前绘制/测量
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _tool = null;
                _measure = null;
                _editMode = EditMode.None;
                _editPts.Clear();
                _offsetActive = false;
                _trimActive = false;
                _breakActive = false; _breakPts.Clear();
                _slideActive = false; _slideDragging = false; _slidePts.Clear();
                _selected.Clear();
                Viewport.SetSnapMarker(null);
                Viewport.SetHighlight(null);
                _snapShown = false;
                RefreshScene();          // 清除进行中的预览
                StatusMsg.Text = "就绪";
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected();
            }
            else if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
            {
                DoUndo();
            }
            else if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
            {
                DoRedo();
            }
        };
    }

    private enum NavMode { None, Orbit, Pan }
    private NavMode _nav;
    private Avalonia.Point _lastPointer;
    private DxfImportService.ImportResult? _lastImport;
    private MeasureState? _measure;
    private (double x, double y)? _snapWorld;   // 当前捕捉到的世界点
    private (double x, double y)? _cursorWorld; // 当前光标世界点(橡皮筋预览用)
    private (double x, double y)? _lastInputPoint; // 上一取点(命令行相对坐标 @ 的基点)
    private bool _snapShown;                     // 捕捉标记是否已显示
    private bool _slideActive;                   // 滑动多段线：已激活(等待按下)
    private bool _slideDragging;                 // 滑动多段线：正在按住拖动
    private readonly List<(double x, double y)> _slidePts = new();   // 滑动采样点
    private Avalonia.Point _slideLastScreen;     // 上次采样的屏幕点(控制采样密度)
    private readonly Scene _scene = new();       // 托管绘制场景
    private readonly LayerTable _layers = new();  // 图层表
    private readonly UndoManager _undo = new();   // 撤销/重做
    private DrawTool? _tool;                      // 当前激活的绘制工具
    private readonly List<SceneEntity> _selected = new();   // 选择集
    private List<SceneEntity> _prevSelected = new();         // 上次选择集
    private Avalonia.Point _pressPos;             // 按下位置（区分点击/拖拽）
    private enum EditMode { None, Move, Copy, Mirror, Rotate, Scale }
    private EditMode _editMode = EditMode.None;
    private readonly List<(double x, double y)> _editPts = new();   // 编辑取的点（基点/目标点/参照…）
    private bool _offsetActive;                    // 偏移：等待点击一侧
    private bool _trimActive;                       // 修剪/延伸：等待点目标线
    private bool _breakActive;                      // 打断：等待取两点
    private readonly List<(double x, double y)> _breakPts = new();   // 打断的两点

    // Ribbon 按钮 → 「导入」走真实 DXF 导入；其余暂回显命令（证明整条 UI 已接线）
    private async void OnRibbonCommand(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is string cmd)
        {
            if (cmd == "新建") { NewScene(); return; }
            if (cmd == "打开") { await OpenSceneAsync(); return; }
            if (cmd == "保存") { await SaveSceneAsync(); return; }
            if (cmd == "撤销") { DoUndo(); return; }
            if (cmd == "重做") { DoRedo(); return; }
            if (cmd == "导入") { await ImportDxfAsync(); return; }
            if (cmd == "另存为") { await ExportDxfAsync(); return; }
            if (cmd == "工具") { new NodeEditorWindow().Show(); StatusMsg.Text = "打开节点编辑器"; return; }
            if (cmd == "删除") { DeleteSelected(); return; }
            if (cmd == "全部选择") { SelectAll(); return; }
            if (cmd == "最后") { SelectLast(); return; }
            if (cmd == "上次") { SelectPrevious(); return; }
            if (cmd == "分解") { ExplodeSelected(); return; }
            if (cmd == "新建图层") { var l = _layers.New(); StatusMsg.Text = $"新建图层「{l.Name}」并置为当前"; return; }
            if (cmd == "图层特性管理器") { var l = _layers.CycleCurrent(); StatusMsg.Text = $"当前图层「{l.Name}」 显示{( l.Shown?"开":"关")}/{(l.Locked?"锁":"解锁")}（再点循环切换）"; return; }
            if (cmd == "冻结") { FreezeCurrentLayer(true); return; }
            if (cmd == "解冻") { FreezeCurrentLayer(false); return; }
            if (cmd == "锁定") { LockCurrentLayer(true); return; }
            if (cmd == "解锁") { LockCurrentLayer(false); return; }
            if (cmd == "图层全开") { LayersAllOn(); return; }
            if (cmd == "移动") { StartEdit(EditMode.Move, "移动"); return; }
            if (cmd == "复制") { StartEdit(EditMode.Copy, "复制"); return; }
            if (cmd == "镜像") { StartEdit(EditMode.Mirror, "镜像"); return; }
            if (cmd == "旋转") { StartEdit(EditMode.Rotate, "旋转"); return; }
            if (cmd == "缩放") { StartEdit(EditMode.Scale, "缩放"); return; }
            if (cmd == "偏移") { StartOffset(); return; }
            if (cmd == "修剪" || cmd == "延伸") { StartTrim(); return; }
            if (cmd == "打断") { StartBreak(); return; }
            if (cmd == "滑动多段线") { StartSlide(); return; }
            if (ActivateDrawTool(cmd)) return;
            StatusMsg.Text = $"命令: {cmd}";
            CommandInput.Text = cmd;
            CommandInput.CaretIndex = cmd.Length;
        }
    }

    // DXF 导入：文件对话框 → DxfImportService → 视口显示 + 范围缩放
    private async Task ImportDxfAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 DXF 图纸",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DXF 图纸") { Patterns = new[] { "*.dxf" } }
            }
        });
        if (files.Count == 0) return;
        ImportPath(files[0].Path.LocalPath);
    }

    // 导出：把当前显示的线段几何写回 .dxf
    private async Task ExportDxfAsync()
    {
        if (_lastImport == null || _lastImport.LineVertices.Length == 0)
        {
            StatusMsg.Text = "无可导出的几何（先导入图纸）";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 DXF",
            DefaultExtension = "dxf",
            SuggestedFileName = "export.dxf",
            FileTypeChoices = new[] { new FilePickerFileType("DXF 图纸") { Patterns = new[] { "*.dxf" } } }
        });
        if (file == null) return;
        try
        {
            int n = DxfExportService.Export(_lastImport.LineVertices, file.Path.LocalPath);
            StatusMsg.Text = $"已导出 {Path.GetFileName(file.Path.LocalPath)} · {n} 段";
        }
        catch (System.Exception ex)
        {
            StatusMsg.Text = $"导出失败：{ex.Message}";
        }
    }

    // ---------- 文件：新建 / 打开 / 保存（绘制场景内部格式）----------
    private void NewScene()
    {
        if (_scene.Count > 0) BeginChange();
        _scene.Clear();
        _selected.Clear();
        Viewport.SetHighlight(null);
        RefreshScene();
        StatusMsg.Text = "新建图形";
    }

    private async Task SaveSceneAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存图形",
            DefaultExtension = "pmx",
            SuggestedFileName = "drawing.pmx",
            FileTypeChoices = new[] { new FilePickerFileType("PitMine 图形") { Patterns = new[] { "*.pmx" } } }
        });
        if (file == null) return;
        try
        {
            File.WriteAllText(file.Path.LocalPath, SceneIO.Save(_scene));
            StatusMsg.Text = $"已保存 {Path.GetFileName(file.Path.LocalPath)} · {_scene.Count} 实体";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"保存失败：{ex.Message}"; }
    }

    private async Task OpenSceneAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开图形",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PitMine 图形") { Patterns = new[] { "*.pmx" } } }
        });
        if (files.Count == 0) return;
        try
        {
            var loaded = SceneIO.Load(File.ReadAllText(files[0].Path.LocalPath));
            _scene.Clear();
            foreach (var e in loaded.Entities) _scene.Add(e);
            _selected.Clear();
            Viewport.SetHighlight(null);
            RefreshScene();
            StatusMsg.Text = $"已打开 {Path.GetFileName(files[0].Path.LocalPath)} · {_scene.Count} 实体";
        }
        catch (System.Exception ex) { StatusMsg.Text = $"打开失败：{ex.Message}"; }
    }

    // 共享导入逻辑：加载 → 视口显示 → 对象树 → 状态回报
    private void ImportPath(string path)
    {
        StatusMsg.Text = $"正在导入 {Path.GetFileName(path)} …";
        var r = DxfImportService.Load(path);
        if (!r.Success)
        {
            StatusMsg.Text = $"导入失败：{r.Error}";
            return;
        }
        _lastImport = r;
        Viewport.ShowImportedLayers(r.LayerGeometry, r.Bounds);
        PopulateObjectTree(r, Path.GetFileName(path));
        PopulateLayers(r);
        StatusMsg.Text = $"已导入 {Path.GetFileName(path)} · {r.EntityCount} 实体 · {r.SegmentCount} 线段 · {r.LayerOrder.Count} 图层";
    }

    // 图层管理器：列出图层复选框，勾选控制显隐
    private void PopulateLayers(DxfImportService.ImportResult r)
    {
        var items = new List<CheckBox>();
        foreach (var name in r.LayerOrder)
        {
            var cb = new CheckBox
            {
                Content = $"{name}（{r.LayerCounts.GetValueOrDefault(name)}）",
                IsChecked = true,
                Tag = name,
                FontSize = 12
            };
            cb.IsCheckedChanged += OnLayerToggle;
            items.Add(cb);
        }
        LayerList.ItemsSource = items;
    }

    private void OnLayerToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string layer)
            Viewport.SetLayerVisible(layer, cb.IsChecked == true);
    }

    // 文件管理器：选文件夹 → 列出该目录 .dxf
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择图纸文件夹",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;

        string dir = folders[0].Path.LocalPath;
        FileFolderLabel.Text = dir;
        var items = CadFileBrowser.ListDxf(dir);
        FileList.ItemsSource = items
            .Select(x => new ListBoxItem { Content = x.Name, Tag = x.Path })
            .ToList();
        StatusMsg.Text = items.Count == 0 ? "该文件夹无 .dxf 文件" : $"{items.Count} 个 .dxf（双击打开）";
    }

    // 双击文件列表项 → 导入该图纸
    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FileList.SelectedItem is ListBoxItem { Tag: string path })
            ImportPath(path);
    }

    // 对象管理器：按图元类型列出导入的实体
    private void PopulateObjectTree(DxfImportService.ImportResult r, string fileName)
    {
        ObjectTreeHint.IsVisible = false;
        var root = new TreeViewItem { Header = $"{fileName}（{r.EntityCount} 实体）", IsExpanded = true };
        foreach (var kv in r.TypeCounts)
            root.Items.Add(new TreeViewItem { Header = $"{kv.Key} × {kv.Value}", Tag = kv.Key });
        ObjectTree.ItemsSource = new[] { root };
    }

    // 对象树选中类型 → 高亮该类型几何；选根/无 → 清除
    private void OnObjectTreeSelect(object? sender, SelectionChangedEventArgs e)
    {
        if (_lastImport != null && ObjectTree.SelectedItem is TreeViewItem { Tag: string type }
            && _lastImport.TypeGeometry.TryGetValue(type, out var geom))
            Viewport.SetHighlight(geom);
        else
            Viewport.SetHighlight(null);
    }

    // ---------- 右键上下文菜单 ----------
    private void OnCtxZoomExtents(object? s, RoutedEventArgs e) => Viewport.ZoomExtents();
    private void OnCtx2D(object? s, RoutedEventArgs e) { Viewport.SetViewMode(true); StatusMsg.Text = "视图: 2D 平面"; }
    private void OnCtx3D(object? s, RoutedEventArgs e) { Viewport.SetViewMode(false); StatusMsg.Text = "视图: 3D 轨道"; }
    private void OnCtxGrid(object? s, RoutedEventArgs e) => Viewport.ToggleGrid();
    private void OnCtxClearHighlight(object? s, RoutedEventArgs e) => Viewport.SetHighlight(null);

    // 对象捕捉容差：约 12px 换算到世界单位
    private double SnapTolWorld(Avalonia.Point p)
    {
        var a = Viewport.ScreenToWorld(p.X, p.Y);
        var b = Viewport.ScreenToWorld(p.X + 12, p.Y);
        if (a == null || b == null) return 0;
        double dx = b.Value.x - a.Value.x, dy = b.Value.y - a.Value.y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    // 捕捉标记：绿色十字几何（P3_C3）
    private static float[] SnapCross(double x, double y, double s)
    {
        const float g0 = 0.2f, g1 = 1f, g2 = 0.4f;
        return new float[]
        {
            (float)(x - s), (float)y, 0, g0, g1, g2,  (float)(x + s), (float)y, 0, g0, g1, g2,
            (float)x, (float)(y - s), 0, g0, g1, g2,  (float)x, (float)(y + s), 0, g0, g1, g2
        };
    }

    // 激活绘制工具（Home 绘制命令/按钮；识别中文按钮名与英文命令）。
    private bool ActivateDrawTool(string cmd)
    {
        string u = cmd.Trim().ToUpperInvariant();

        // 正多边形：可带边数，如 "POLYGON 5" / "POL8" / "正多边形6"（须排除 POLYLINE）
        string letters = new string(u.TakeWhile(char.IsLetter).ToArray());
        if (letters == "POLYGON" || letters == "POL" || cmd.Trim().StartsWith("正多边形"))
        {
            int sides = 6;
            string digits = new string(cmd.Where(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out int n) && n >= 3 && n <= 120) sides = n;
            _tool = new PolygonTool { Sides = sides };
            _measure = null; Viewport.SetSnapMarker(null); _snapShown = false; _lastInputPoint = null;
            StatusMsg.Text = _tool.Prompt + "（ESC 退出）";
            return true;
        }

        DrawTool? t = u switch
        {
            "LINE" => new LineTool(),
            "CIRCLE" => new CircleTool(),
            "CIRCLE2P" or "C2P" => new Circle2PTool(),
            "CIRCLE3P" or "C3P" => new Circle3PTool(),
            "ARC" => new ArcTool(),
            "RECTANG" or "RECT" => new RectTool(),
            "PLINE" or "POLYLINE" => new PolylineTool(),
            "POINT" or "PO" => new PointTool(),
            _ => cmd.Trim() switch
            {
                "直线" => new LineTool(),
                "圆" => new CircleTool(),
                "圆2P" or "圆(2点)" => new Circle2PTool(),
                "圆3P" or "圆(3点)" => new Circle3PTool(),
                "圆弧" => new ArcTool(),
                "矩形" => new RectTool(),
                "多段线" => new PolylineTool(),
                "点" => new PointTool(),
                _ => (DrawTool?)null
            }
        };
        if (t == null) return false;
        _tool = t;
        _measure = null;                              // 退出测距
        Viewport.SetSnapMarker(null); _snapShown = false; _lastInputPoint = null;
        StatusMsg.Text = t.Prompt + "（ESC 退出）";
        return true;
    }

    // 撤销/重做：改动前记快照
    private void BeginChange() => _undo.Push(SceneIO.Save(_scene));

    private void LoadSceneFrom(string json)
    {
        var loaded = SceneIO.Load(json);
        _scene.Clear();
        foreach (var e in loaded.Entities) _scene.Add(e);
        _selected.Clear();
        Viewport.SetHighlight(null);
        RefreshScene();
    }

    private void DoUndo()
    {
        var s = _undo.Undo(SceneIO.Save(_scene));
        if (s == null) { StatusMsg.Text = "无可撤销"; return; }
        LoadSceneFrom(s); StatusMsg.Text = "已撤销";
    }

    private void DoRedo()
    {
        var s = _undo.Redo(SceneIO.Save(_scene));
        if (s == null) { StatusMsg.Text = "无可重做"; return; }
        LoadSceneFrom(s); StatusMsg.Text = "已重做";
    }

    // 新实体归当前图层（名称 + 图层色）
    private void AssignLayer(SceneEntity e)
    {
        e.LayerName = _layers.Current.Name;
        e.Cr = _layers.Current.Cr; e.Cg = _layers.Current.Cg; e.Cb = _layers.Current.Cb;
    }

    // 重绘场景（含当前工具进行中的预览：已点的段 + 到光标的橡皮筋）
    private void RefreshScene()
    {
        var list = new List<float>(_scene.BuildGeometry(_layers.IsShown));
        _tool?.AppendPreview(list, _cursorWorld);
        if (_slideDragging && _slidePts.Count > 1)     // 滑动多段线拖动预览
        {
            var pv = new PolylineEntity { Points = _slidePts, Cr = 0.55f, Cg = 0.62f, Cb = 0.70f };
            pv.Tessellate(list);
        }
        Viewport.SetSceneGeometry(list.ToArray());
    }

    // 点选：命中则单选(再点取消)，未命中清空
    private void PickAt(Avalonia.Point rel)
    {
        var w = _snapWorld ?? Viewport.ScreenToWorld(rel.X, rel.Y);
        if (w == null) return;
        SaveSel();
        double tol = SnapTolWorld(rel);
        var hit = _scene.Pick(w.Value.x, w.Value.y, tol, _layers.IsSelectable);
        if (hit == null) _selected.Clear();
        else if (_selected.Contains(hit)) _selected.Remove(hit);
        else { _selected.Clear(); _selected.Add(hit); }
        HighlightSelection();
        StatusMsg.Text = _selected.Count > 0 ? $"已选 {_selected.Count} 个实体" : "未选中";
    }

    private void HighlightSelection()
    {
        if (_selected.Count == 0) { Viewport.SetHighlight(null); return; }
        var o = new List<float>();
        foreach (var e in _selected) e.Tessellate(o);
        Viewport.SetHighlight(o.ToArray());
    }

    private void DeleteSelected()
    {
        if (_selected.Count == 0) { StatusMsg.Text = "未选中实体"; return; }
        BeginChange();
        foreach (var e in _selected) _scene.Remove(e);
        int n = _selected.Count;
        _selected.Clear();
        Viewport.SetHighlight(null);
        RefreshScene();
        StatusMsg.Text = $"已删除 {n} 个实体";
    }

    // ---------- 选择命令（全选/最后/上次）+ 分解 ----------
    private void SaveSel() => _prevSelected = new List<SceneEntity>(_selected);

    private void SelectAll()
    {
        SaveSel();
        _selected.Clear();
        _selected.AddRange(_scene.Entities);
        HighlightSelection();
        StatusMsg.Text = $"全选 {_selected.Count} 个";
    }

    private void SelectLast()
    {
        if (_scene.Count == 0) { StatusMsg.Text = "无实体"; return; }
        SaveSel();
        _selected.Clear();
        _selected.Add(_scene.Entities[_scene.Count - 1]);
        HighlightSelection();
        StatusMsg.Text = "已选最后创建的实体";
    }

    private void SelectPrevious()
    {
        var tmp = new List<SceneEntity>(_selected);
        _selected.Clear();
        _selected.AddRange(_prevSelected);
        _prevSelected = tmp;
        HighlightSelection();
        StatusMsg.Text = $"恢复上次选择 {_selected.Count} 个";
    }

    private void ExplodeSelected()
    {
        var explodable = _selected.FindAll(e => e.Explode() != null);
        if (explodable.Count == 0) { StatusMsg.Text = "无可分解实体（矩形/多段线）"; return; }
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (var e in explodable)
        {
            var parts = e.Explode()!;
            _scene.Remove(e);
            foreach (var p in parts) { _scene.Add(p); newSel.Add(p); }
        }
        _selected.Clear();
        _selected.AddRange(newSel);
        HighlightSelection();
        RefreshScene();
        StatusMsg.Text = $"已分解为 {newSel.Count} 段";
    }

    // 进入编辑（移动/复制/镜像）：需已有选择
    private void StartEdit(EditMode mode, string name)
    {
        if (_selected.Count == 0) { StatusMsg.Text = $"{name}：请先选实体"; return; }
        _editMode = mode; _editPts.Clear(); _tool = null; _measure = null; _lastInputPoint = null;
        StatusMsg.Text = mode == EditMode.Mirror ? $"{name}：指定镜像线第一点" : $"{name}：指定基点";
    }

    private void StartOffset()
    {
        if (_selected.Count != 1) { StatusMsg.Text = "偏移：请先选中一个实体"; return; }
        _offsetActive = true; _tool = null; _measure = null; _editMode = EditMode.None;
        StatusMsg.Text = "偏移：点击偏移到的一侧";
    }

    private void StartTrim()
    {
        if (_selected.Count != 1 || _selected[0] is not LineEntity)
        { StatusMsg.Text = "修剪/延伸：请先选一条作为边界的直线"; return; }
        _trimActive = true; _tool = null; _measure = null; _editMode = EditMode.None; _offsetActive = false;
        StatusMsg.Text = "点击要修剪/延伸的直线（近端点移到与边界的交点）";
    }

    private void StartBreak()
    {
        if (_selected.Count != 1 || _selected[0] is not LineEntity)
        { StatusMsg.Text = "打断：请先选一条直线（多段线/圆弧打断待做）"; return; }
        _breakActive = true; _breakPts.Clear();
        _tool = null; _measure = null; _editMode = EditMode.None; _offsetActive = false; _trimActive = false;
        StatusMsg.Text = "打断：指定第一点（两点间的一段将被移除）";
    }

    private void StartSlide()
    {
        _tool = null; _measure = null; _editMode = EditMode.None; _editPts.Clear();
        _offsetActive = false; _trimActive = false;
        _slideActive = true; _slideDragging = false; _slidePts.Clear();
        Viewport.SetSnapMarker(null); _snapShown = false;
        StatusMsg.Text = "滑动多段线：在视口按住左键拖动采样，松开成线（ESC 退出）";
    }

    // 图层特性：作用于当前图层（当前层可用 图层特性管理器 循环切换）
    private void FreezeCurrentLayer(bool freeze)
    {
        _layers.Current.Frozen = freeze;
        _selected.RemoveAll(en => !_layers.IsSelectable(en.LayerName));   // 冻结层的选中失效
        HighlightSelection();
        RefreshScene();
        StatusMsg.Text = $"图层「{_layers.Current.Name}」{(freeze ? "已冻结（隐藏且不可选）" : "已解冻")}";
    }
    private void LockCurrentLayer(bool locked)
    {
        _layers.Current.Locked = locked;
        _selected.RemoveAll(en => !_layers.IsSelectable(en.LayerName));
        HighlightSelection();
        StatusMsg.Text = $"图层「{_layers.Current.Name}」{(locked ? "已锁定（可见不可选）" : "已解锁")}";
    }
    private void LayersAllOn()
    {
        _layers.AllOn();
        RefreshScene();
        StatusMsg.Text = "所有图层已打开（解冻）";
    }

    // 命令行精确坐标：绘制/编辑取点时把 "x,y" / "@dx,dy" / "@d<ang" 当作一次点击
    private bool TryCoordinateInput(string cmd)
    {
        if (_tool == null && _editMode == EditMode.None) return false;   // 仅取点态接受坐标
        var pt = ParseCoord(cmd, _lastInputPoint);
        if (pt == null) return false;
        FeedPoint(pt.Value.x, pt.Value.y);
        return true;
    }

    /// <summary>解析坐标：x,y=绝对直角；@dx,dy=相对；d&lt;ang=绝对极(角度°)；@d&lt;ang=相对极。无法解析返回 null。</summary>
    internal static (double x, double y)? ParseCoord(string s, (double x, double y)? last)
    {
        s = s.Trim();
        bool rel = s.StartsWith("@");
        if (rel) s = s.Substring(1).Trim();

        int lt = s.IndexOf('<');
        if (lt > 0)   // 极坐标 距离<角度
        {
            if (double.TryParse(s.Substring(0, lt).Trim(), out double dist) &&
                double.TryParse(s.Substring(lt + 1).Trim(), out double ang))
            {
                double rad = ang * System.Math.PI / 180.0;
                double dx = dist * System.Math.Cos(rad), dy = dist * System.Math.Sin(rad);
                if (!rel) return (dx, dy);
                return last == null ? null : (last.Value.x + dx, last.Value.y + dy);
            }
            return null;
        }

        int comma = s.IndexOf(',');
        if (comma > 0)   // 直角 x,y
        {
            if (double.TryParse(s.Substring(0, comma).Trim(), out double x) &&
                double.TryParse(s.Substring(comma + 1).Trim(), out double y))
            {
                if (!rel) return (x, y);
                return last == null ? null : (last.Value.x + x, last.Value.y + y);
            }
        }
        return null;
    }

    // 把一个世界点喂给当前取点态(编辑/绘制)，等效一次点击
    private void FeedPoint(double x, double y)
    {
        _lastInputPoint = (x, y);
        if (_editMode != EditMode.None)
        {
            _editPts.Add((x, y));
            if (_editPts.Count >= EditPointCount(_editMode))
            {
                ApplyEditTransform(BuildEditTransform(), _editMode == EditMode.Copy);
                _editMode = EditMode.None; _editPts.Clear();
                StatusMsg.Text = "编辑完成";
            }
            else StatusMsg.Text = EditPrompt(_editMode, _editPts.Count);
            return;
        }
        if (_tool != null)
        {
            var ent = _tool.AddPoint(x, y);
            if (ent != null) { BeginChange(); AssignLayer(ent); _scene.Add(ent); }
            RefreshScene();
            StatusMsg.Text = $"{_tool.Prompt}（已画 {_scene.Count}）";
        }
    }

    private static double Dist2((double x, double y) p, double x, double y)
        => (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y);

    private static int EditPointCount(EditMode m) => m == EditMode.Scale ? 3 : 2;

    private static string EditPrompt(EditMode m, int have) => (m, have) switch
    {
        (EditMode.Mirror, 1) => "镜像：指定镜像线第二点",
        (EditMode.Rotate, 1) => "旋转：指定旋转角参照点",
        (EditMode.Scale, 1) => "缩放：指定参考长度点",
        (EditMode.Scale, 2) => "缩放：指定新长度点",
        _ => "指定目标点"
    };

    private Affine2 BuildEditTransform()
    {
        var p = _editPts;
        switch (_editMode)
        {
            case EditMode.Move:
            case EditMode.Copy:
                return Affine2.Translate(p[1].x - p[0].x, p[1].y - p[0].y);
            case EditMode.Mirror:
                return Affine2.MirrorLine(p[0].x, p[0].y, p[1].x, p[1].y);
            case EditMode.Rotate:
                return Affine2.Rotate(System.Math.Atan2(p[1].y - p[0].y, p[1].x - p[0].x), p[0].x, p[0].y);
            case EditMode.Scale:
            {
                double refLen = System.Math.Sqrt((p[1].x - p[0].x) * (p[1].x - p[0].x) + (p[1].y - p[0].y) * (p[1].y - p[0].y));
                double newLen = System.Math.Sqrt((p[2].x - p[0].x) * (p[2].x - p[0].x) + (p[2].y - p[0].y) * (p[2].y - p[0].y));
                double f = refLen < 1e-9 ? 1 : newLen / refLen;
                return Affine2.Scale(f, p[0].x, p[0].y);
            }
            default: return Affine2.Translate(0, 0);
        }
    }

    // 对选择集施加仿射变换；copy=true 则加副本，否则替换原实体
    private void ApplyEditTransform(Affine2 m, bool copy)
    {
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (var e in _selected)
        {
            var e2 = e.Apply(m);
            if (copy) _scene.Add(e2); else _scene.Replace(e, e2);
            newSel.Add(e2);
        }
        _selected.Clear();
        _selected.AddRange(newSel);
        RefreshScene();
        HighlightSelection();
    }

    // 命令行回车 → 命令分发（已实装的走功能，其余回显）
    private void OnCommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb || string.IsNullOrWhiteSpace(tb.Text)) return;

        string cmd = tb.Text.Trim();
        tb.Text = string.Empty;

        if (TryCoordinateInput(cmd)) return;   // 绘制/编辑取点时优先当坐标

        switch (cmd.ToUpperInvariant())
        {
            case "2D":
                Viewport.SetViewMode(true);
                StatusMsg.Text = "视图: 2D 平面（正交俯视，拖拽平移）";
                break;
            case "3D":
            case "3DVIEW":
            case "3DORBIT":
                Viewport.SetViewMode(false);
                StatusMsg.Text = "视图: 3D 轨道";
                break;
            case "NODEEDITOR":
            case "节点编辑器":
                new NodeEditorWindow().Show();
                StatusMsg.Text = "打开节点编辑器";
                break;
            case "ZE":
            case "ZOOM":
            case "ZOOMEXTENTS":
                Viewport.ZoomExtents();
                StatusMsg.Text = "范围缩放";
                break;
            case "GRID":
                Viewport.ToggleGrid();
                StatusMsg.Text = "切换网格显示";
                break;
            case "EXPORTDXF":
            case "导出":
                _ = ExportDxfAsync();
                break;
            case "DIST":
            case "DI":
                _measure = new MeasureState();
                _tool = null;
                StatusMsg.Text = "测距：点第一点";
                break;
            case "ERASE":
            case "E":
                DeleteSelected();
                break;
            case "ALL":
                SelectAll();
                break;
            case "LAST":
                SelectLast();
                break;
            case "PREVIOUS":
            case "P":
                SelectPrevious();
                break;
            case "EXPLODE":
            case "X":
                ExplodeSelected();
                break;
            case "MOVE":
            case "M":
                StartEdit(EditMode.Move, "移动");
                break;
            case "COPY":
            case "CO":
                StartEdit(EditMode.Copy, "复制");
                break;
            case "MIRROR":
            case "MI":
                StartEdit(EditMode.Mirror, "镜像");
                break;
            case "ROTATE":
            case "RO":
                StartEdit(EditMode.Rotate, "旋转");
                break;
            case "SCALE":
            case "SC":
                StartEdit(EditMode.Scale, "缩放");
                break;
            case "OFFSET":
            case "O":
                StartOffset();
                break;
            case "TRIM":
            case "TR":
            case "EXTEND":
            case "EX":
                StartTrim();
                break;
            case "PLDRAG":
            case "SPL":
                StartSlide();
                break;
            case "BREAK":
            case "BR":
                StartBreak();
                break;
            case "LAYFRZ":
                FreezeCurrentLayer(true);
                break;
            case "LAYTHW":
                FreezeCurrentLayer(false);
                break;
            case "LAYLCK":
                LockCurrentLayer(true);
                break;
            case "LAYULK":
                LockCurrentLayer(false);
                break;
            case "LAYON":
                LayersAllOn();
                break;
            case "NEW":
                NewScene();
                break;
            case "UNDO":
            case "U":
                DoUndo();
                break;
            case "REDO":
                DoRedo();
                break;
            case "LAYER":
            case "LA":
                { var l = _layers.CycleCurrent(); StatusMsg.Text = $"当前图层「{l.Name}」"; }
                break;
            case "OPEN":
                _ = OpenSceneAsync();
                break;
            case "SAVE":
                _ = SaveSceneAsync();
                break;
            default:
                if (!ActivateDrawTool(cmd)) StatusMsg.Text = $"执行: {cmd}";
                break;
        }
    }
}
