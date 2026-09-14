using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 文件管理器的一个节点 —— 忠实移植原版 <c>PitMineApp.Infrastructure.FileTree.FileSystemNode</c>:
/// 「📂 工作目录」/「🖥 我的电脑」两个容器根 + 磁盘目录懒加载 + 文件按格式分组(「dxf文件 (3)」)。
/// 纯逻辑(不碰界面), 单测直接跑。
/// </summary>
public class FileSystemNode : INotifyPropertyChanged
{
    private string _name = "";
    private string _icon = "📁";
    private bool _isExpanded;
    private bool _isSelected;
    private bool _childrenLoaded;
    private bool _isWorkingDir;

    /// <summary>工作目录子节点：用户加入「工作目录」列表的某个文件夹，右键菜单据此显示"从工作目录移除"。</summary>
    public bool IsWorkingDir
    {
        get => _isWorkingDir;
        set { _isWorkingDir = value; OnPropertyChanged(nameof(IsWorkingDir)); }
    }

    /// <summary>「📂 工作目录」父节点：顶层容器，子节点为所有配置的工作目录；不走文件系统枚举。</summary>
    public bool IsWorkingDirRoot { get; set; }

    /// <summary>「🖥 我的电脑」父节点：顶层容器，子节点为所有就绪的驱动器/挂载点；不走文件系统枚举。</summary>
    public bool IsMyComputer { get; set; }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string FullPath { get; set; } = "";

    public bool IsDirectory { get; set; }

    /// <summary>分组节点：按文件格式聚合的虚拟节点(非磁盘目录, 无 FullPath, 不参与打开/导入)。</summary>
    public bool IsGroup { get; set; }

    public string Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(nameof(Icon)); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
            if (_isExpanded && IsDirectory && !_childrenLoaded) LoadChildren();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    public ObservableCollection<FileSystemNode> Children { get; } = new();

    public FileSystemNode? Parent { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>放一个占位子节点，确保树上显示展开箭头(内容等展开时才读盘)。</summary>
    public void EnsureExpanderVisible()
    {
        if (IsDirectory && Children.Count == 0 && !_childrenLoaded)
            Children.Add(new FileSystemNode { Name = "加载中...", Icon = "⏳" });
    }

    // 单节点最多展示的子目录/子文件数; 超出用"…还有 N 个"占位避免:
    //   ① 系统目录含数万子项时逐个 Add 触发数万次集合变更, 界面线程被淹没 → 卡死
    //   ② 极端目录撑爆视觉, 用户也无法浏览
    private const int MaxChildrenPerNode = 5000;

    /// <summary>
    /// 系统支持打开/导入的文件格式 —— 文件树只显示这些(文件夹始终显示以便导航)。同原版清单:
    ///   打开: .pmx(CAD 场景) / .pmb(块体模型)
    ///   导入: .dwg/.dxf(AutoCAD) / .3dm(3DMine网格)·.3ds(3DMine折线)·.3dp(3DMine工程包)
    ///        / .wl/.wt/.wp(MapGIS) / .mpj(MapGIS工程) / .kdf(WeCAD) / .off(Geomview)
    /// </summary>
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pmx", ".pmb",
        ".dwg", ".dxf", ".3dm", ".3ds", ".3dp", ".wl", ".wt", ".wp", ".mpj", ".kdf", ".off",
    };

    /// <summary>文件分组顺序 —— 每种实际出现的格式建一个分组节点(标签即"&lt;后缀&gt;文件")。</summary>
    private static readonly string[] FileGroupOrder =
    {
        ".pmx", ".pmb", ".dwg", ".dxf", ".3dm", ".3ds", ".3dp",
        ".wl", ".wt", ".wp", ".mpj", ".kdf", ".off",
    };

    public void LoadChildren()
    {
        if (!IsDirectory || _childrenLoaded) return;
        // 容器根(工作目录/我的电脑)的 Children 由 ViewModel 直接填, 不能按 FullPath 去枚举文件系统
        if (IsWorkingDirRoot || IsMyComputer) { _childrenLoaded = true; return; }
        _childrenLoaded = true;

        Children.Clear();

        // 跳过软链接/系统项: 避免顺着软链绕回去无限递归
        var dirOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var fileOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        bool dirCapHit = false, fileCapHit = false;
        int fileCount = 0;

        try
        {
            var dirs = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(FullPath, "*", dirOpts))
            {
                if (dirs.Count >= MaxChildrenPerNode) { dirCapHit = true; break; }
                dirs.Add(d);
            }
            dirs.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
            {
                var child = new FileSystemNode
                {
                    Name = new DirectoryInfo(dir).Name,
                    FullPath = dir,
                    IsDirectory = true,
                    Icon = "📁",
                    Parent = this,
                };
                child.EnsureExpanderVisible();
                Children.Add(child);
            }
            if (dirCapHit)
                Children.Add(new FileSystemNode { Name = $"… 子目录超过 {MaxChildrenPerNode} 项, 其余未列出", Icon = "⚠️" });

            // 文件按"支持的格式"分桶, 每种出现的格式建一个分组节点, 文件挂在分组之下(分组默认展开)
            var filesByExt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(FullPath, "*", fileOpts))
            {
                string ext = Path.GetExtension(f);
                if (!SupportedExtensions.Contains(ext)) continue;   // 只显示支持的格式
                if (fileCount >= MaxChildrenPerNode) { fileCapHit = true; break; }
                if (!filesByExt.TryGetValue(ext, out var bucket)) filesByExt[ext] = bucket = new List<string>();
                bucket.Add(f);
                fileCount++;
            }

            foreach (var ext in FileGroupOrder)
            {
                if (!filesByExt.TryGetValue(ext, out var bucket) || bucket.Count == 0) continue;
                bucket.Sort(StringComparer.OrdinalIgnoreCase);
                string groupIcon = GetFileIcon(ext);

                var group = new FileSystemNode
                {
                    Name = $"{ext.TrimStart('.')}文件 ({bucket.Count})",
                    IsGroup = true,
                    Icon = groupIcon,
                    IsExpanded = true,
                    Parent = this,
                };
                foreach (var file in bucket)
                    group.Children.Add(new FileSystemNode
                    {
                        Name = Path.GetFileName(file),
                        FullPath = file,
                        Icon = groupIcon,        // 同组同图标(= 该格式图标)
                        Parent = group,
                    });
                Children.Add(group);
            }
            if (fileCapHit)
                Children.Add(new FileSystemNode { Name = $"… 文件超过 {MaxChildrenPerNode} 项, 其余未列出", Icon = "⚠️" });
        }
        catch (UnauthorizedAccessException)
        {
            Children.Add(new FileSystemNode { Name = "(访问被拒绝)", Icon = "⚠️" });
        }
        catch (PathTooLongException)
        {
            Children.Add(new FileSystemNode { Name = "(路径过长, 无法枚举)", Icon = "⚠️" });
        }
        catch (Exception ex)
        {
            Children.Add(new FileSystemNode { Name = $"(错误: {ex.Message})", Icon = "⚠️" });
        }
    }

    /// <summary>
    /// 重新枚举该目录, 拾取磁盘上新增/删除的内容(解决"往文件夹里加了文件却不显示")。
    /// 保留刷新前的展开层级: 已展开的子目录刷新后自动重新展开并递归同步; 未展开的保持折叠(下次展开再读盘)。
    /// 容器根(工作目录/我的电脑)由 ViewModel 维护, 此处直接跳过。须在界面线程调用。
    /// </summary>
    public void Refresh()
    {
        if (!IsDirectory || IsWorkingDirRoot || IsMyComputer) return;
        var expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpandedDirs(expandedDirs);   // 必须在清空前收集, 否则重建后无从得知旧展开状态
        ReloadRecursive(expandedDirs);
    }

    private void CollectExpandedDirs(HashSet<string> set)
    {
        foreach (var c in Children)
            if (c.IsDirectory && c.IsExpanded && !string.IsNullOrEmpty(c.FullPath))
            {
                set.Add(c.FullPath);
                c.CollectExpandedDirs(set);
            }
    }

    private void ReloadRecursive(HashSet<string> expandedDirs)
    {
        _childrenLoaded = false;
        Children.Clear();
        LoadChildren();

        foreach (var c in Children)
            if (c.IsDirectory && !string.IsNullOrEmpty(c.FullPath) && expandedDirs.Contains(c.FullPath))
            {
                // 直接置展开位(绕过 setter, 免得它再触发一次 LoadChildren 造成双重读盘);
                // 内容由紧接着的递归 ReloadRecursive 统一加载。
                c._isExpanded = true;
                c.OnPropertyChanged(nameof(IsExpanded));
                c.ReloadRecursive(expandedDirs);
            }
    }

    /// <summary>按格式给图标 —— 同原版清单, 每类一个可区分的 emoji。</summary>
    public static string GetFileIcon(string extension) => extension.ToLowerInvariant() switch
    {
        // 原生
        ".pmx" => "✏️",   // CAD 场景(主文件, 绘图)
        ".pmb" => "🧱",   // 块体模型
        // AutoCAD
        ".dwg" => "🅰️",   // AutoCAD 原生(A 标识)
        ".dxf" => "📐",   // 图形交换格式(制图)
        // 3DMine
        ".3dm" => "🔺",   // 网格(三角网)
        ".3ds" => "🖋️",   // 折线(等高线/边界)
        ".3dp" => "📦",   // 工程包
        // MapGIS
        ".wl" => "📈",    // 线
        ".wt" => "📍",    // 点/注记
        ".wp" => "🟩",    // 区/面
        ".mpj" => "🗺️",   // 工程
        // 其它 CAD / 几何
        ".kdf" => "📏",   // WeCAD(让出 📐 给 dxf)
        ".off" => "🔷",   // Geomview 网格
        // 通用兜底
        ".txt" or ".md" or ".csv" => "📄",
        ".json" or ".xml" => "📋",
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => "🖼️",
        ".exe" or ".dll" => "⚙️",
        ".zip" or ".rar" or ".7z" => "🗜️",
        _ => "📄"
    };
}
