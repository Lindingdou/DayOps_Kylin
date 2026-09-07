using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 地质与工程信息数据库页面(§四/§八, 原 GeoDataBase 插件 24 个窗口)与主窗口之间的服务契约。
/// 页面只依赖它: 数据库连接 + 状态栏 + 场景写入/拾取 + 文件对话框 + 转派中文命令。
/// </summary>
public sealed class GeoDbContext
{
    /// <summary>已建库(含 50 迁移 + 种子)的 SQLite 连接(会话级共享)。</summary>
    public required SqliteConnection Conn { get; init; }
    /// <summary>所属主窗口(作 Owner)。</summary>
    public required Window Owner { get; init; }
    /// <summary>写主窗口状态栏。</summary>
    public required Action<string> Status { get; init; }
    /// <summary>
    /// 把实体加入主场景: (实体, 图层名, 可选范围 [minX,minY,maxX,maxY] 用于缩放到范围)。
    /// 内部 BeginChange(可撤销) → 设图层 → Add → RefreshScene → FitBounds。
    /// </summary>
    public required Action<IReadOnlyList<SceneEntity>, string, double[]?> AddToScene { get; init; }
    /// <summary>按图层名删除场景实体(重绘前清旧柱), 返回删除数。</summary>
    public required Func<string, int> RemoveLayerEntities { get; init; }
    /// <summary>主视口一次性拾取: 下一次左键点击返回世界坐标; 用户按 Esc/取消返回 null。</summary>
    public required Func<string, Task<(double x, double y)?>> PickPointAsync { get; init; }
    /// <summary>场景图层名列表。</summary>
    public required Func<IReadOnlyList<string>> SceneLayerNames { get; init; }
    /// <summary>取某图层全部线段端点(x,y,z=实体标高)——用于从场景三角网(边线)重建三角网面。</summary>
    public required Func<string, IReadOnlyList<(double x, double y, double z)>> LayerVertices { get; init; }
    /// <summary>保存文本文件对话框: (标题, 建议文件名, 内容) → 保存路径或 null。</summary>
    public required Func<string, string, string, Task<string?>> SaveTextAsync { get; init; }
    /// <summary>打开文件对话框: (标题, 扩展名模式如 *.csv) → 路径或 null。</summary>
    public required Func<string, string[], Task<string?>> OpenFileAsync { get; init; }
    /// <summary>选择文件夹对话框: 标题 → 路径或 null。</summary>
    public required Func<string, Task<string?>> OpenFolderAsync { get; init; }
    /// <summary>把中文命令转派给主窗口命令处理(复用已有文本报表/分析命令)。</summary>
    public required Action<string> RunCommand { get; init; }
}

/// <summary>页面单例注册表: 每种窗口最多一个实例, 再次打开即前置(原插件 _xxxWindow 字段 + Activate 同义)。</summary>
public static class GeoDbWindows
{
    private static readonly Dictionary<Type, Window> Open = new();

    public static T Show<T>(GeoDbContext ctx, Func<T> create) where T : Window
    {
        if (Open.TryGetValue(typeof(T), out var w) && w is T existing)
        {
            existing.Activate();
            return existing;
        }
        var win = create();
        Open[typeof(T)] = win;
        Last = win;
        win.Closed += (_, _) => Open.Remove(typeof(T));
        win.Show(ctx.Owner);
        return win;
    }

    public static int OpenCount => Open.Count;
    /// <summary>最近一次 Show 的页面(自检/测试用)。</summary>
    public static Window? Last { get; private set; }

    public static void CloseAll()
    {
        foreach (var w in new List<Window>(Open.Values)) w.Close();
        Open.Clear();
    }
}
