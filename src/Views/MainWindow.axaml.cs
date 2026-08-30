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

            // 编辑（移动/复制/镜像）：左键取两点
            if (_editMode != EditMode.None && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null)
                {
                    _editPts.Add((wp.Value.x, wp.Value.y));
                    if (_editPts.Count >= EditPointCount(_editMode))
                    {
                        ApplyEditTransform(BuildEditTransform(), _editMode == EditMode.Copy);
                        _editMode = EditMode.None; _editPts.Clear();
                        StatusMsg.Text = "编辑完成";
                    }
                    else StatusMsg.Text = EditPrompt(_editMode, _editPts.Count);
                }
                return;
            }

            // 绘制工具：左键喂点（凑齐一个实体则加入场景并重绘）
            if (_tool != null && props.IsLeftButtonPressed)
            {
                _nav = NavMode.None;
                var wp = _snapWorld ?? Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
                if (wp != null)
                {
                    var ent = _tool.AddPoint(wp.Value.x, wp.Value.y);
                    if (ent != null) _scene.Add(ent);
                    RefreshScene();
                    StatusMsg.Text = $"{_tool.Prompt}（已画 {_scene.Count}）";
                }
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

            if (_nav == NavMode.Pan)
                Viewport.Pan(_lastPointer.X, _lastPointer.Y, p.X, p.Y);
            else if (_nav == NavMode.Orbit)
                Viewport.Orbit((p.X - _lastPointer.X) * 0.01, (p.Y - _lastPointer.Y) * 0.01);
            _lastPointer = p;
        };
        ViewportHost.PointerReleased += (_, e) =>
        {
            var rel = e.GetPosition(ViewportHost);
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
                if (e != null) _scene.Add(e);
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
        };
    }

    private enum NavMode { None, Orbit, Pan }
    private NavMode _nav;
    private Avalonia.Point _lastPointer;
    private DxfImportService.ImportResult? _lastImport;
    private MeasureState? _measure;
    private (double x, double y)? _snapWorld;   // 当前捕捉到的世界点
    private bool _snapShown;                     // 捕捉标记是否已显示
    private readonly Scene _scene = new();       // 托管绘制场景
    private DrawTool? _tool;                      // 当前激活的绘制工具
    private readonly List<SceneEntity> _selected = new();   // 选择集
    private Avalonia.Point _pressPos;             // 按下位置（区分点击/拖拽）
    private enum EditMode { None, Move, Copy, Mirror, Rotate, Scale }
    private EditMode _editMode = EditMode.None;
    private readonly List<(double x, double y)> _editPts = new();   // 编辑取的点（基点/目标点/参照…）

    // Ribbon 按钮 → 「导入」走真实 DXF 导入；其余暂回显命令（证明整条 UI 已接线）
    private async void OnRibbonCommand(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is string cmd)
        {
            if (cmd == "新建") { NewScene(); return; }
            if (cmd == "打开") { await OpenSceneAsync(); return; }
            if (cmd == "保存") { await SaveSceneAsync(); return; }
            if (cmd == "导入") { await ImportDxfAsync(); return; }
            if (cmd == "另存为") { await ExportDxfAsync(); return; }
            if (cmd == "工具") { new NodeEditorWindow().Show(); StatusMsg.Text = "打开节点编辑器"; return; }
            if (cmd == "删除") { DeleteSelected(); return; }
            if (cmd == "移动") { StartEdit(EditMode.Move, "移动"); return; }
            if (cmd == "复制") { StartEdit(EditMode.Copy, "复制"); return; }
            if (cmd == "镜像") { StartEdit(EditMode.Mirror, "镜像"); return; }
            if (cmd == "旋转") { StartEdit(EditMode.Rotate, "旋转"); return; }
            if (cmd == "缩放") { StartEdit(EditMode.Scale, "缩放"); return; }
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
        DrawTool? t = u switch
        {
            "LINE" => new LineTool(),
            "CIRCLE" => new CircleTool(),
            "ARC" => new ArcTool(),
            "RECTANG" or "RECT" => new RectTool(),
            "PLINE" or "POLYLINE" => new PolylineTool(),
            "POINT" or "PO" => new PointTool(),
            _ => cmd.Trim() switch
            {
                "直线" => new LineTool(),
                "圆" => new CircleTool(),
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
        Viewport.SetSnapMarker(null); _snapShown = false;
        StatusMsg.Text = t.Prompt + "（ESC 退出）";
        return true;
    }

    // 重绘场景（含当前工具进行中的预览，如多段线已点的段）
    private void RefreshScene()
    {
        var list = new List<float>(_scene.BuildGeometry());
        _tool?.AppendPreview(list);
        Viewport.SetSceneGeometry(list.ToArray());
    }

    // 点选：命中则单选(再点取消)，未命中清空
    private void PickAt(Avalonia.Point rel)
    {
        var w = _snapWorld ?? Viewport.ScreenToWorld(rel.X, rel.Y);
        if (w == null) return;
        double tol = SnapTolWorld(rel);
        var hit = _scene.Pick(w.Value.x, w.Value.y, tol);
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
        foreach (var e in _selected) _scene.Remove(e);
        int n = _selected.Count;
        _selected.Clear();
        Viewport.SetHighlight(null);
        RefreshScene();
        StatusMsg.Text = $"已删除 {n} 个实体";
    }

    // 进入编辑（移动/复制/镜像）：需已有选择
    private void StartEdit(EditMode mode, string name)
    {
        if (_selected.Count == 0) { StatusMsg.Text = $"{name}：请先选实体"; return; }
        _editMode = mode; _editPts.Clear(); _tool = null; _measure = null;
        StatusMsg.Text = mode == EditMode.Mirror ? $"{name}：指定镜像线第一点" : $"{name}：指定基点";
    }

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
            case "NEW":
                NewScene();
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
