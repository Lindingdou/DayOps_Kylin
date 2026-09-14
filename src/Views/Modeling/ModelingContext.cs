using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using System.Data.Common;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 三维地质建模页面(原 MeshEditLib / BlockModelLib 各窗口)与主窗口之间的服务契约：
/// 场景里的三角网/点/多段线读写、块体模型读写、视口拾取、数据库、文件对话框、状态栏、转派命令。
/// 页面只依赖它, 不直接碰 MainWindow。
/// </summary>
public sealed class ModelingContext
{
    public required Window Owner { get; init; }
    public required Action<string> Status { get; init; }
    /// <summary>地质/生产数据库(§四/§八 SQLite, 补勘钻孔/现状点等表在此); 打开失败为 null。</summary>
    public required Func<DbConnection?> Conn { get; init; }

    // ── 场景对象 ──
    public required Func<IReadOnlyList<MeshEntity>> Meshes { get; init; }
    public required Func<IReadOnlyList<MeshEntity>> SelectedMeshes { get; init; }
    public required Func<IReadOnlyList<PointEntity>> Points { get; init; }
    public required Func<IReadOnlyList<PointEntity>> SelectedPoints { get; init; }
    public required Func<IReadOnlyList<PolylineEntity>> Polylines { get; init; }
    public required Func<IReadOnlyList<PolylineEntity>> SelectedPolylines { get; init; }
    public required Func<IReadOnlyList<string>> LayerNames { get; init; }
    /// <summary>加三角网入场景(可撤销), fit=true 缩放到其范围。</summary>
    public required Action<MeshEntity, bool> AddMesh { get; init; }
    /// <summary>用新网替换场景中的旧网(同图层/颜色, 可撤销)。</summary>
    public required Action<MeshEntity, MeshEntity> ReplaceMesh { get; init; }
    /// <summary>加任意实体入场景: (实体, 图层名或 null=当前层, 缩放范围 [minX,minY,maxX,maxY] 或 null)。</summary>
    public required Action<IReadOnlyList<SceneEntity>, string?, double[]?> AddEntities { get; init; }
    public required Action<IReadOnlyList<SceneEntity>> RemoveEntities { get; init; }
    public required Func<string, int> RemoveLayerEntities { get; init; }
    public required Action RefreshScene { get; init; }
    /// <summary>设置选择集(高亮)。</summary>
    public required Action<IReadOnlyList<SceneEntity>> Select { get; init; }

    // ── 视口 ──
    /// <summary>一次性拾取世界坐标(左键; Esc/取消 → null)。</summary>
    public required Func<string, Task<(double x, double y)?>> PickPointAsync { get; init; }
    /// <summary>一次性拾取实体(左键点到的最近实体, 可按谓词过滤; 未点中/取消 → null)。</summary>
    public required Func<string, Func<SceneEntity, bool>?, Task<SceneEntity?>> PickEntityAsync { get; init; }
    /// <summary>当前视口可见世界范围 [minX,minY,maxX,maxY]。</summary>
    public required Func<double[]?> ViewBounds { get; init; }
    public required Action<double[]> FitBounds { get; init; }

    // ── 块体模型(当前文档一份, 原 BlockModelLib 浏览器语义) ──
    public required Func<List<BlockModel.Block>?> Blocks { get; init; }
    public required Func<Dictionary<string, double[]>?> BlockAttrs { get; init; }
    /// <summary>设置块体模型并重渲(blocks, 逐块属性表或 null, 品位范围)。</summary>
    public required Action<List<BlockModel.Block>?, Dictionary<string, double[]>?> SetBlocks { get; init; }
    /// <summary>只重渲给定子集(筛选/切面显示), 不改模型。</summary>
    public required Action<IReadOnlyList<BlockModel.Block>> ShowBlocks { get; init; }
    /// <summary>
    /// 块体仓重渲专用的一次到位：设块体数据(不另出主窗口的品位方块) + 换掉图层里的块体网 + 只刷一次场景(+可选缩放)。
    /// 拆成 SetBlocks / ShowBlocks / RemoveLayerEntities / AddEntities 四步走时，一次导入要建两遍网、
    /// 刷四遍场景、还往撤销栈压两份整场景 JSON —— 311 万块的模型上单份快照就近 1 GB。
    /// 参数：(未删块, 逐块属性表, 已配色的块体网实体, 图层名, 缩放范围 [minX,minY,maxX,maxY] 或 null)。
    /// </summary>
    public required Action<List<BlockModel.Block>?, Dictionary<string, double[]>?, IReadOnlyList<SceneEntity>, string, double[]?> SetBlockDisplay { get; init; }

    // ── 文件/命令 ──
    public required Func<string, string, string, Task<string?>> SaveTextAsync { get; init; }
    public required Func<string, string[], Task<string?>> OpenFileAsync { get; init; }
    public required Func<string, string[], Task<IReadOnlyList<string>>> OpenFilesAsync { get; init; }
    public required Action<string> RunCommand { get; init; }
}

/// <summary>建模页面单例注册(同 GeoDbWindows)。</summary>
public static class ModelingWindows
{
    private static readonly Dictionary<Type, Window> Open = new();
    public static Window? Last { get; private set; }

    public static T Show<T>(ModelingContext ctx, Func<T> create) where T : Window
    {
        if (Open.TryGetValue(typeof(T), out var w) && w is T existing) { existing.Activate(); return existing; }
        var win = create();
        Open[typeof(T)] = win;
        Last = win;
        win.Closed += (_, _) => Open.Remove(typeof(T));
        win.Show(ctx.Owner);
        return win;
    }
    public static int OpenCount => Open.Count;
    public static void CloseAll() { foreach (var w in new List<Window>(Open.Values)) w.Close(); Open.Clear(); }
}
