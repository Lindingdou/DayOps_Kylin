using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 文件管理器面板 —— 忠实移植原版 <c>fileSystemTree</c>：
///   · 两个根：「📂 工作目录 (N)」(用户常用文件夹, 默认展开) 与「🖥 我的电脑」(驱动器, 默认折叠)
///   · 目录懒加载, 只显示系统能打开/导入的格式, 文件按格式分组(「dxf文件 (3)」)
///   · 右键：刷新(F5) / 添加根目录… / 添加到工作目录 / 从工作目录移除 / 在资源管理器中打开
///   · 双击：目录·分组 = 展开折叠, 文件 = 按扩展名打开(.pmx/.pmb)或导入(.dxf/.dwg/.3dm/…)
///   · 工作目录经 <see cref="PitMine3D.Kylin.UserSettings"/> 持久化, 关闭即落盘, 下次启动照旧
/// </summary>
public partial class MainWindow
{
    /// <summary>工作目录持久化 key(同原版键名, 便于两边配置对照)。</summary>
    internal const string KeyWorkingDirs = "FileSystem.WorkingDirectories";

    private FileSystemViewModel? _fsVm;

    /// <summary>建文件管理器树：从配置恢复工作目录 → 建树 → 挂右键选中/按键。</summary>
    private void InitFileTree()
    {
        _fsVm = new FileSystemViewModel();
        var dirs = PitMine3D.Kylin.UserSettings.Current.Get<string[]?>(KeyWorkingDirs, null);
        if (dirs != null)
            foreach (var d in dirs)
                _fsVm.AddWorkingDirectory(d);
        _fsVm.Refresh();
        if (FileTree == null) return;
        FileTree.ItemsSource = _fsVm.RootNodes;

        // 右键先把鼠标下的节点选中：否则菜单项对的是上一次选中的节点(原版 PreviewMouseRightButtonDown 同义)
        FileTree.AddHandler(InputElement.PointerPressedEvent, OnFileTreePointerPressed, RoutingStrategies.Tunnel);
        // 点选节点时 TreeView(AutoScrollToSelectedItem)把整个容器「滚到可见」—— 容器宽是整条路径文字,
        // ScrollContentPresenter 就把横向偏移挪到该行左缘, 一点图标横向滚动条就跟着跳(实测偏移 0→17)。
        // RequestBringIntoView 只冒泡, 挂在 TreeView 上已经晚于 presenter; 挂到模板里的 ItemsPresenter(在 presenter 之内、
        // 冒泡路径上先到)才截得住 —— 只做竖向对齐, 横向纹丝不动(横向滚动条留给用户自己拖)。
        FileTree.TemplateApplied += (_, _) => FileTree.Presenter?.AddHandler(Control.RequestBringIntoViewEvent, OnFileTreeBringIntoView);
    }

    // 自检探针：找名字含 prefix 的节点, 记其标题行屏幕中心与树的滚动偏移, 然后把它设为选中(同点选的选中路径),
    // 随后每秒记一次偏移(共 12 次) —— 核对"选中后横向滚动条动没动"。
    private void SelftestFileTreeProbe(string prefix)
    {
        var sv = FileTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        FileSystemNode? Find(System.Collections.Generic.IEnumerable<FileSystemNode> ns)
        {
            foreach (var n in ns) { if (n.Name.Contains(prefix)) return n; var c = Find(n.Children); if (c != null) return c; }
            return null;
        }
        var node = _fsVm == null ? null : Find(_fsVm.RootNodes);
        var tvi = node == null ? null : FileTree.TreeContainerFromItem(node) as TreeViewItem;
        var hdr = tvi?.HeaderPresenter;
        string pos = "未找到";
        if (hdr != null)
        {
            var c = hdr.PointToScreen(new Point(Math.Min(20, hdr.Bounds.Width / 2), hdr.Bounds.Height / 2));   // 点在图标附近
            pos = $"屏幕({c.X},{c.Y}) 行宽{hdr.Bounds.Width:0} 视口宽{sv?.Viewport.Width:0} 内容宽{sv?.Extent.Width:0}";
        }
        PitMine3D.Kylin.CrashLog.Write("文件树", $"节点「{node?.Name}」 {pos} 偏移=({sv?.Offset.X:0},{sv?.Offset.Y:0})");
        if (node != null) FileTree.SelectedItem = node;   // 同点选: 选中即触发框架的 AutoScrollToSelectedItem → BringIntoView
        int n = 0;
        var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            if (++n > 12) { t.Stop(); return; }
            PitMine3D.Kylin.CrashLog.Write("文件树", $"+{n}s 偏移=({sv?.Offset.X:0},{sv?.Offset.Y:0}) 选中「{(FileTree.SelectedItem as FileSystemNode)?.Name}」");
        };
        t.Start();
    }

    /// <summary>树节点的「滚到可见」只管竖向：目标行顶出视口就顶回来、底出视口就底回来, 横向偏移原样保留。</summary>
    private void OnFileTreeBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;   // 拦下 ScrollContentPresenter 默认的横竖一起滚(它的处理器不收已处理事件)
        if (e.TargetObject is not Visual target) return;
        var sv = FileTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv == null) return;
        // 目标若是整个 TreeViewItem, 其 Bounds 连展开的子树一起算, 只对齐它的标题行
        var rect = e.TargetRect;
        if (target is TreeViewItem tvi && tvi.HeaderPresenter is Visual header) { target = header; rect = new Rect(header.Bounds.Size); }
        if (target.TransformToVisual(sv) is not Matrix m) return;
        var r = rect.TransformToAABB(m);   // 目标行在视口坐标里的位置
        double dy = 0;
        if (r.Bottom > sv.Viewport.Height) dy = r.Bottom - sv.Viewport.Height;
        if (r.Top - dy < 0) dy = r.Top;    // 行比视口还高时以顶边为准(同框架的取舍)
        if (dy != 0) sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + dy));
    }

    private void OnFileTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FileTree).Properties.IsRightButtonPressed) return;
        var node = NodeAt(e.Source);
        if (node != null) FileTree.SelectedItem = node;
    }

    /// <summary>命中链路向上找承载节点(找不到返回当前选中项)。</summary>
    private FileSystemNode? NodeAt(object? source)
    {
        var item = (source as Visual)?.FindAncestorOfType<TreeViewItem>();
        return item?.DataContext as FileSystemNode ?? FileTree?.SelectedItem as FileSystemNode;
    }

    private void OnFileTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        var node = NodeAt(e.Source);
        if (node == null) return;
        if (node.IsDirectory || node.IsGroup) node.IsExpanded = !node.IsExpanded;
        else if (!string.IsNullOrEmpty(node.FullPath)) OpenOrImportFile(node.FullPath);
    }

    private void OnFileTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5) return;
        RefreshFileSystem(FileTree?.SelectedItem as FileSystemNode);
        e.Handled = true;
    }

    // ── 右键菜单 ────────────────────────────────────────────────────

    private void OnFileTreeMenuOpening(object? sender, CancelEventArgs e)
    {
        var node = FileTree?.SelectedItem as FileSystemNode;
        bool hasNode = node != null;
        bool isDir = hasNode && node!.IsDirectory && !string.IsNullOrEmpty(node.FullPath);

        MiFsAddRoot.IsVisible = hasNode && node!.IsWorkingDirRoot;                 // 「📂 工作目录」根 → 添加根目录…
        MiFsRemoveFromWork.IsVisible = hasNode && node!.IsWorkingDir;              // 工作目录子节点 → 移除
        // 任意可枚举目录 → 添加到工作目录(已在工作目录里的、两个容器根 除外)
        MiFsAddToWork.IsVisible = isDir && !(node!.IsWorkingDir || node.IsWorkingDirRoot || node.IsMyComputer);
        MiFsOpenInExplorer.IsVisible = hasNode && !string.IsNullOrEmpty(node!.FullPath);

        // 「刷新」恒在; 其后的分隔符只在下面还有可见项时才显示, 免得悬空一条线
        bool anyBelow = MiFsAddRoot.IsVisible || MiFsAddToWork.IsVisible || MiFsRemoveFromWork.IsVisible;
        MiFsSepAfterRefresh.IsVisible = anyBelow || MiFsOpenInExplorer.IsVisible;
        MiFsSepBeforeExplorer.IsVisible = anyBelow && MiFsOpenInExplorer.IsVisible;
    }

    private void OnFsRefreshClick(object? sender, RoutedEventArgs e)
        => RefreshFileSystem(FileTree?.SelectedItem as FileSystemNode);

    /// <summary>
    /// 刷新文件管理器(解决"往文件夹里加了文件却不显示")。规则同原版：
    ///   · 选中普通目录/驱动器 → 刷新该目录(及其已展开子树)
    ///   · 选中文件/分组       → 刷新其所在目录
    ///   · 选中容器根          → 刷新其下已展开的子树
    ///   · 无选中              → 刷新所有根下已展开的子树
    /// 折叠着的目录不强制读盘(下次展开自然读最新)。
    /// </summary>
    private void RefreshFileSystem(FileSystemNode? node)
    {
        try
        {
            if (node == null)
            {
                if (_fsVm != null)
                    foreach (var root in _fsVm.RootNodes)
                        foreach (var child in root.Children)
                            if (child.IsExpanded) child.Refresh();
                StatusMsg.Text = "已刷新文件管理器";
                return;
            }

            if (node.IsWorkingDirRoot || node.IsMyComputer)
            {
                foreach (var child in node.Children)
                    if (child.IsExpanded) child.Refresh();
                StatusMsg.Text = $"已刷新: {node.Name}";
            }
            else if (node.IsDirectory && !string.IsNullOrEmpty(node.FullPath))
            {
                node.Refresh();
                StatusMsg.Text = $"已刷新: {node.FullPath}";
            }
            else
            {
                var dir = node.Parent;   // 文件/分组 → 向上找最近的真实目录
                while (dir != null && !(dir.IsDirectory && !string.IsNullOrEmpty(dir.FullPath))) dir = dir.Parent;
                if (dir != null) { dir.Refresh(); StatusMsg.Text = $"已刷新: {dir.FullPath}"; }
            }
        }
        catch (Exception ex) { StatusMsg.Text = $"刷新失败: {ex.Message}"; }
    }

    private async void OnFsAddRootClick(object? sender, RoutedEventArgs e)
    {
        if (_fsVm == null) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择要添加到工作目录的文件夹",
                AllowMultiple = true,
            });
            int added = folders.Count(f => _fsVm.AddWorkingDirectory(f.Path.LocalPath));
            if (added > 0)
            {
                SaveWorkingDirs();
                _fsVm.Refresh();
                StatusMsg.Text = $"已添加 {added} 个工作目录";
            }
            else StatusMsg.Text = folders.Count == 0 ? "未添加工作目录" : "未添加新工作目录（路径无效或已存在）";
        }
        catch (Exception ex) { StatusMsg.Text = $"添加工作目录失败: {ex.Message}"; }
    }

    private void OnFsAddToWorkClick(object? sender, RoutedEventArgs e)
    {
        if (_fsVm == null) return;
        if (FileTree?.SelectedItem is not FileSystemNode node || !node.IsDirectory) return;
        if (string.IsNullOrEmpty(node.FullPath)) return;
        if (_fsVm.AddWorkingDirectory(node.FullPath))
        {
            SaveWorkingDirs();
            _fsVm.Refresh();
            StatusMsg.Text = $"已添加到工作目录: {node.FullPath}";
        }
        else StatusMsg.Text = $"该路径已在工作目录中或路径无效: {node.FullPath}";
    }

    private void OnFsRemoveFromWorkClick(object? sender, RoutedEventArgs e)
    {
        if (_fsVm == null) return;
        if (FileTree?.SelectedItem is not FileSystemNode node || !node.IsWorkingDir) return;
        if (_fsVm.RemoveWorkingDirectory(node.FullPath))
        {
            SaveWorkingDirs();
            _fsVm.Refresh();
            StatusMsg.Text = $"已从工作目录移除: {node.FullPath}";
        }
    }

    private void OnFsOpenInExplorerClick(object? sender, RoutedEventArgs e)
    {
        if (FileTree?.SelectedItem is not FileSystemNode node || string.IsNullOrEmpty(node.FullPath)) return;
        try
        {
            if (!node.IsDirectory && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // 文件：在资源管理器里定位并选中(仅 Windows 有 /select)
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{node.FullPath}\"",
                    UseShellExecute = true,
                });
                return;
            }
            // 目录(或麒麟上的文件)：交给桌面的默认文件管理器开其所在目录
            string target = node.IsDirectory ? node.FullPath : (Path.GetDirectoryName(node.FullPath) ?? node.FullPath);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo { FileName = "xdg-open", Arguments = target, UseShellExecute = false });
        }
        catch (Exception ex) { StatusMsg.Text = $"打开失败：{ex.Message}"; }
    }

    // ── 双击打开/导入 ────────────────────────────────────────────────

    /// <summary>按扩展名分发：打开(.pmx/.pmb)或导入(.dwg/.dxf/.3dm/.3ds/.wl/.wt/.wp/.mpj/.kdf/.off)。</summary>
    private void OpenOrImportFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { StatusMsg.Text = $"文件不存在：{path}"; return; }
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pmx": OpenScenePath(path); break;
            case ".pmb": _ = ImportPmbAsBlockModel(path); break;
            case ".3dp":
                // 原版有 3DMine 工程包(.3dp, CAB) 导入; 本系统尚未移植, 如实告知而不是装作打开了
                StatusMsg.Text = "3DMine 工程包(.3dp) 尚未移植：请解包后导入其中的 .3dm(网格) / .3ds(折线)";
                break;
            default: _ = ImportPath(path); break;
        }
    }

    /// <summary>
    /// 双击 .pmb → 走「导入块体」那条路(读文件 → 建元数据 → 入场景), 不弹对话框, 同原版双击直接载入。
    /// </summary>
    private async Task ImportPmbAsBlockModel(string path)
    {
        string name = Path.GetFileName(path);
        StatusMsg.Text = $"正在后台读取 {name}…界面可继续操作";
        try
        {
            var r = await Task.Run(() => PmbImportService.Load(path));
            if (!r.Success) { StatusMsg.Text = $"导入块体模型：{r.Error}"; return; }
            if (r.Blocks.Count == 0) { StatusMsg.Text = "导入块体模型：文件中没有块体"; return; }

            string baseName = string.IsNullOrWhiteSpace(r.ModelName) ? Path.GetFileNameWithoutExtension(path) : r.ModelName;
            string uniq = Modeling.BlockModelStore.UniqueName(baseName);
            var attrs = r.AllAttrs.Count > 0 ? r.AllAttrs : null;
            var meta = await Task.Run(() => Cad.BlockModelMeta.FromBlocks(uniq, r.Blocks, attrs));
            meta.DisplayStyle.FillColor = Cad.BlockDefaultPalette.Next(Modeling.BlockModelStore.Models.Count);
            if (meta.PropertySchema.Count > 0) { meta.ActiveColormapAttribute = meta.PropertySchema[0].Name; meta.ColormapRange = null; }
            Modeling.ImportBlockModelWindow.ApplyPmbMetadata(meta, r);   // 属性表/删除 cell/推荐着色 随文件恢复
            var err = await Modeling.BlockModelStore.CreateAsync(MdlCtx(), meta);
            if (err != null) { StatusMsg.Text = $"导入块体模型：{err}"; return; }
            StatusMsg.Text = $"已导入块体模型 {meta.Name} · {meta.BlockCount:N0} 块 · 网格 {r.Nx}×{r.Ny}×{r.Nz} · 着色「{meta.ActiveColormapAttribute}」";
        }
        catch (Exception ex) { StatusMsg.Text = $"导入块体模型失败：{ex.Message}"; }
    }

    /// <summary>工作目录列表落配置(增删即写, 关闭时再 Flush 一次)。</summary>
    private void SaveWorkingDirs()
    {
        if (_fsVm == null) return;
        PitMine3D.Kylin.UserSettings.Current.Set(KeyWorkingDirs, _fsVm.WorkingDirectories.ToArray());
    }

    /// <summary>
    /// 自检用：选中指定类型的节点并直接弹出右键菜单(不模拟鼠标) —— 截图核对菜单项的显隐规则。
    /// which: 工作目录根 / 工作目录 / 目录 / 文件 / 我的电脑
    /// </summary>
    private void SelftestOpenFileTreeMenu(string which)
    {
        if (_fsVm == null || FileTree == null || FileTreeMenu == null) return;
        var work = _fsVm.RootNodes.FirstOrDefault(r => r.IsWorkingDirRoot);
        var wdir = work?.Children.FirstOrDefault(c => c.IsWorkingDir);
        FileSystemNode? n = which switch
        {
            "工作目录根" => work,
            "我的电脑" => _fsVm.RootNodes.FirstOrDefault(r => r.IsMyComputer),
            "工作目录" => wdir,
            "目录" => wdir?.Children.FirstOrDefault(c => c.IsDirectory),
            "文件" => wdir?.Children.FirstOrDefault(c => c.IsGroup)?.Children.FirstOrDefault(),
            _ => null,
        };
        if (n == null) { StatusMsg.Text = $"自检文件树菜单：找不到「{which}」节点"; return; }
        FileTree.SelectedItem = n;
        OnFileTreeMenuOpening(FileTreeMenu, new CancelEventArgs());   // 走真实的显隐规则
        FileTreeMenu.Open(FileTree);
        var shown = new System.Collections.Generic.List<string> { "刷新" };
        if (MiFsAddRoot.IsVisible) shown.Add("添加根目录…");
        if (MiFsAddToWork.IsVisible) shown.Add("添加到工作目录");
        if (MiFsRemoveFromWork.IsVisible) shown.Add("从工作目录移除");
        if (MiFsOpenInExplorer.IsVisible) shown.Add("在资源管理器中打开");
        StatusMsg.Text = $"自检文件树菜单：{which} →「{n.Name}」菜单项 [{string.Join(" · ", shown)}]";
    }

    /// <summary>自检用：直接把某个文件夹加进工作目录并展开(不弹文件夹对话框)。</summary>
    private void SelftestAddWorkingDir(string dir)
    {
        if (_fsVm == null) { StatusMsg.Text = "自检工作目录：文件管理器未就绪"; return; }
        if (!_fsVm.AddWorkingDirectory(dir)) { StatusMsg.Text = $"自检工作目录：未添加（路径无效或已存在）{dir}"; return; }
        SaveWorkingDirs();
        _fsVm.Refresh();
        int files = _fsVm.RootNodes.Count > 0
            ? _fsVm.RootNodes[0].Children.SelectMany(c => c.Children).Where(g => g.IsGroup).Sum(g => g.Children.Count)
            : 0;
        StatusMsg.Text = $"自检工作目录：已加入 {dir}（可导入文件 {files} 个）";
    }
}
