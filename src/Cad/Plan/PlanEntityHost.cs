using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>实体类型码（原 AcDbEntityType 的两个规划侧用到的值）。</summary>
public static class PlanEntityType
{
    public const int TriangleMesh = 6;
    public const int Polyline = 4;
}

/// <summary>规划窗口要落地的一批实体（原 PmbiWriter 载荷的托管等价）：图层 + 线段 + 三角网。</summary>
public sealed class PlanEntityBatch
{
    public string Layer = "0";
    public (byte r, byte g, byte b) LayerColor = (20, 184, 166);
    public readonly List<(double x0, double y0, double z0, double x1, double y1, double z1, byte r, byte g, byte b)> Lines = new();
    /// <summary>闭合环（逐环一条三维多段线，比散段可整条选）。</summary>
    public readonly List<(double[] x, double[] y, double z, byte r, byte g, byte b)> Rings = new();
    public readonly List<(string name, List<(double x, double y, double z)> verts, List<(int a, int b, int c)> tris, byte r, byte g, byte b)> Meshes = new();
}

/// <summary>
/// 规划模块（境界优化 / 采区划分 / 中长远 / 短期）对宿主图纸的依赖 —— 原 <c>IEntityCapability</c> + <c>ISelectionCapability</c>
/// + <c>IPitDesignCapability</c> 里被 PlanLib 用到的那一小截，由 <c>MainWindow.Plan.cs</c> 实现。
/// 全部按 handle 说话（<see cref="Draw.EntityHandles"/>），几何不复制。
/// </summary>
public interface IPlanEntityHost
{
    /// <summary>取多段线世界坐标顶点 [x,y,z,...] 与闭合标志。非多段线/不存在返回 false。</summary>
    bool TryGetPolylineWorldVertices(long handle, out double[] xyz, out bool closed);
    /// <summary>取实体包围盒（mn/mx 各 3 元）。</summary>
    bool TryGetEntityAabb(long handle, out double[] mn, out double[] mx);
    /// <summary>取三角网（顶点 + 三角索引）。非三角网返回 false。</summary>
    bool TryGetMesh(long handle, out List<(double x, double y, double z)> verts, out List<(int a, int b, int c)> tris);
    /// <summary>枚举图纸里某类实体（handle, 图层, 名称）。</summary>
    IReadOnlyList<(long handle, string layer, string name)> ListEntities(int wantType);
    long[] GetHandlesByLayer(string layer);
    void DeleteEntities(IEnumerable<long> handles);
    /// <summary>落地一批实体（一次入库一步 Undo）；返回新实体 handle。</summary>
    long[] Import(PlanEntityBatch batch);
    /// <summary>在视口交互拾取一个实体（wantType 限类型；Esc 取消返回 null）。</summary>
    Task<long?> PickInViewportAsync(int wantType, string prompt);
    /// <summary>在视口取一点（Esc 取消返回 null）。</summary>
    Task<(double x, double y)?> PickPointAsync(string prompt);
    /// <summary>视口高亮某实体。</summary>
    void SelectByHandle(long handle, bool addToSelection = false);
    /// <summary>激活块体模型（无返回 null）。</summary>
    BlockModelMeta? ActiveBlockModel { get; }
    /// <summary>已连接的数据库连接（未连接返回 null；规划窗口不自动弹连接流程，只提示）。</summary>
    System.Data.Common.DbConnection? Db { get; }
    /// <summary>命令行/状态栏回显。</summary>
    void Echo(string text, bool warn = false);
    /// <summary>刷新视口。</summary>
    void Refresh();
}
