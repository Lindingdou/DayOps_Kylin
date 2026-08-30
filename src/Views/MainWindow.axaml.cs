using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;

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
            _dragging = true;
            _lastPointer = e.GetPosition(ViewportHost);
            e.Pointer.Capture(ViewportHost);
        };
        ViewportHost.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(ViewportHost);
            var w = Viewport.ScreenToWorld(p.X, p.Y);
            CoordText.Text = w != null
                ? $"X {w.Value.x:0.00}  Y {w.Value.y:0.00}"
                : $"视口 px  X {p.X:0}  Y {p.Y:0}";
            if (_dragging)
            {
                Viewport.Orbit((p.X - _lastPointer.X) * 0.01, (p.Y - _lastPointer.Y) * 0.01);
                _lastPointer = p;
            }
        };
        ViewportHost.PointerReleased += (_, e) =>
        {
            _dragging = false;
            e.Pointer.Capture(null);
        };
        ViewportHost.PointerWheelChanged += (_, e) =>
            Viewport.Zoom(e.Delta.Y > 0 ? 0.9 : 1.1);

        // 对象树选类型 → 视口高亮该类型几何
        ObjectTree.SelectionChanged += OnObjectTreeSelect;
    }

    private bool _dragging;
    private Avalonia.Point _lastPointer;
    private DxfImportService.ImportResult? _lastImport;

    // Ribbon 按钮 → 「导入」走真实 DXF 导入；其余暂回显命令（证明整条 UI 已接线）
    private async void OnRibbonCommand(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is string cmd)
        {
            if (cmd == "导入") { await ImportDxfAsync(); return; }
            if (cmd == "工具") { new NodeEditorWindow().Show(); StatusMsg.Text = "打开节点编辑器"; return; }
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
            default:
                StatusMsg.Text = $"执行: {cmd}";
                break;
        }
    }
}
