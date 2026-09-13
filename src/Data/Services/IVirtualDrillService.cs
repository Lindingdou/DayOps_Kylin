// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IVirtualDrillService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 虚拟钻孔地质模型服务:持久化 / 读取「地表 + 各煤层顶底板」三角网(节点 + 连接顺序),
/// 供虚拟钻孔在任意位置竖直求交算顶/底板高程。几何存 <see cref="VirtualDrillSurface.GeometryB64"/>。
/// </summary>
public interface IVirtualDrillService
{
    /// <summary>全部已捕获面(地表 + 各煤层顶/底板),按 seam_order、role 排序。</summary>
    IReadOnlyList<VirtualDrillSurface> AllSurfaces();

    /// <summary>按业务键取一张面(地表用 role='surface', seamName 传空)。不存在返回 null。</summary>
    VirtualDrillSurface? GetByKey(string role, string seamName);

    /// <summary>库中是否已配置任何地质模型面(决定「虚拟钻孔」是否需先引导配置)。</summary>
    bool HasAnySurface();

    /// <summary>
    /// 保存一张面(按 (role, seamName) upsert:已存在则覆盖几何/颜色/排序)。
    /// verts = 扁平 [x,y,z,...](3·V);tris = 三角索引(3·F,指向 verts)。自动算 bbox 与计数并打包。
    /// 返回该面的行 id。verts/tris 为空或非法则不写、返回 0。
    /// </summary>
    long SaveSurface(string role, string seamName, int seamOrder, string colorHex,
                     string sourceLayer, double[] verts, int[] tris);

    /// <summary>取某面的几何:解包 base64 → 顶点 + 三角索引。失败(缺行 / 坏数据)返回 false。</summary>
    bool TryGetGeometry(long id, out double[] verts, out int[] tris);

    /// <summary>改某煤层(顶 + 底同步)的地质排序与层位显示色 #RRGGBB。返回受影响行数。</summary>
    int UpdateSeamMeta(string seamName, int seamOrder, string colorHex);

    /// <summary>删除一张面(按 id)。</summary>
    void Delete(long id);

    /// <summary>清空全部虚拟钻孔地质模型面(重新配置用)。返回删除行数。</summary>
    int ClearAll();
}
