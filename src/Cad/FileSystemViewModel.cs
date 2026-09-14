using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 文件管理器的树数据 —— 忠实移植原版 <c>FileSystemViewModel</c>:
/// 两个顶层根「📂 工作目录 (N)」(用户加的常用文件夹, 默认展开) 与「🖥 我的电脑」(驱动器/挂载点, 默认折叠)。
/// 工作目录列表由主窗口经 <see cref="PitMine3D.Kylin.UserSettings"/> 持久化, 下次启动照旧。
/// </summary>
public class FileSystemViewModel
{
    public ObservableCollection<FileSystemNode> RootNodes { get; } = new();

    /// <summary>用户配置的工作目录列表(0..N 个)，挂在「📂 工作目录」父节点下。</summary>
    public List<string> WorkingDirectories { get; } = new();

    /// <summary>添加工作目录(去重/规范化); 成功返回 true。</summary>
    public bool AddWorkingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm;
        try { norm = Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return false; }
        if (norm.Length == 0) norm = Path.GetFullPath(path);          // 根目录 "/" 被 TrimEnd 削没了 → 还原
        if (!Directory.Exists(norm)) return false;
        if (WorkingDirectories.Any(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase))) return false;
        WorkingDirectories.Add(norm);
        return true;
    }

    /// <summary>从工作目录列表移除(大小写不敏感); 成功返回 true。</summary>
    public bool RemoveWorkingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm;
        try { norm = Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return false; }
        if (norm.Length == 0) norm = Path.GetFullPath(path);
        int idx = WorkingDirectories.FindIndex(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        WorkingDirectories.RemoveAt(idx);
        return true;
    }

    /// <summary>重建整棵树(工作目录变动/首次加载时调)。</summary>
    public void Refresh()
    {
        RootNodes.Clear();

        // ── 父节点 1: 📂 工作目录 ─────────────────────────────────────
        // 始终展示, 即使列表为空(让用户能右键「添加根目录…」)。
        var workRoot = new FileSystemNode
        {
            Name = $"工作目录 ({WorkingDirectories.Count})",
            IsDirectory = true,
            Icon = "📂",
            IsWorkingDirRoot = true,
        };
        foreach (var wd in WorkingDirectories)
        {
            try
            {
                if (!Directory.Exists(wd)) continue;
                var info = new DirectoryInfo(wd);
                var child = new FileSystemNode
                {
                    Name = $"⭐ {info.Name}  ({info.FullName})",
                    FullPath = info.FullName,
                    IsDirectory = true,
                    Icon = "📁",
                    IsWorkingDir = true,
                    Parent = workRoot,
                };
                workRoot.Children.Add(child);
                child.IsExpanded = true;   // 默认展开工作文件夹(触发 LoadChildren 载入内容)
            }
            catch { /* 单个路径失效跳过, 不影响其他 */ }
        }
        workRoot.IsExpanded = true;
        RootNodes.Add(workRoot);

        // ── 父节点 2: 🖥 我的电脑 ─────────────────────────────────────
        var myComputer = new FileSystemNode
        {
            Name = "我的电脑",
            IsDirectory = true,
            Icon = "🖥",
            IsMyComputer = true,
        };
        try
        {
            foreach (var drive in ListDrives())
            {
                var driveNode = new FileSystemNode
                {
                    Name = drive.Label,
                    FullPath = drive.Path,
                    IsDirectory = true,
                    Icon = "💿",
                    Parent = myComputer,
                };
                driveNode.EnsureExpanderVisible();
                myComputer.Children.Add(driveNode);
            }
        }
        catch (Exception ex)
        {
            myComputer.Children.Add(new FileSystemNode { Name = $"无法枚举驱动器: {ex.Message}", Icon = "⚠️" });
        }
        myComputer.IsExpanded = false;   // 默认折叠, 工作目录优先可见
        RootNodes.Add(myComputer);
    }

    /// <summary>
    /// 驱动器/挂载点清单。Windows 上就是 C:\ D:\；麒麟(Linux)上 DriveInfo 会把 tmpfs/proc 之类
    /// 伪文件系统也报出来(几十条), 故只留真正能浏览的固定/可移动/网络盘, 名字直接用挂载路径。
    /// </summary>
    private static List<(string Label, string Path)> ListDrives()
    {
        var list = new List<(string, string)>();
        foreach (var d in DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool ready;
            try { ready = d.IsReady; } catch { continue; }
            if (!ready) continue;
            if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.CDRom)) continue;

            string label = "";
            try { label = d.VolumeLabel ?? ""; } catch { /* 部分挂载点取不到卷标 */ }
            string name = d.Name.Length > 1 ? d.Name.TrimEnd('\\', '/') : d.Name;   // "C:\" → "C:"; "/" 保留
            list.Add((string.IsNullOrEmpty(label) ? $"本地磁盘 ({name})" : $"{label} ({name})", d.Name));
        }
        return list;
    }
}
