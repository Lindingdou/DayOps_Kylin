// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IEntityCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 实体能力域：模型空间实体的批量查询与变更。
    ///
    /// 设计原则（终态 P-2 架构）：
    ///   - 第三方插件（仅 Platform.Public）通过此接口访问受控的实体能力
    ///   - 所有"可能批量"的操作都有 ulong[] 批量重载，避免 N 次 P/Invoke
    ///   - 不暴露 P/Invoke 细节；实现由 Host 在 Platform.Public 边界做转换
    ///
    /// 与 IModuleContext.Engine（IEngineService 80 方法）的关系：
    ///   - IEngineService 是内核模块的"无门槛"深度访问
    ///   - IEntityCapability 是第三方插件的"受控"标准 API
    ///   - 长期：内核模块也应迁到 capability API，IEngineService 退役
    /// </summary>
    public interface IEntityCapability
    {
        /// <summary>当前 ModelSpace 中的实体总数。</summary>
        int Count { get; }

        /// <summary>列举 ModelSpace 中所有实体的 handle。批量场景下避免 N 次 P/Invoke。</summary>
        ulong[] GetAllHandles();

        /// <summary>按图层名筛选实体。空图层名等价于"任意图层"。</summary>
        ///
        /// <para><b>⚠ 图层多时不可靠,要按图层分类实体请用 <see cref="GetEntityLayers"/>。</b>
        /// 本方法先从对象树采样 JSON(<b>每图层 ≤20 条</b>)里找一条该层的样本反查图层句柄,
        /// 采样里没出现该层就退回同样受限的采样结果 —— 返回空。
        /// 一次排土放坡会建 27 级 × 2 = 54 个台阶线图层、每层实体很少,采样根本覆盖不到,
        /// 于是"图上明明有台阶线,逐层去取却一条都取不到"。实测踩过。</para>
        ulong[] GetHandlesByLayer(string? layerName);

        /// <summary>
        /// 批量取实体所在的**图层名**。返回数组与输入对齐;取不到的位置为空串。
        ///
        /// <para>这是"把全图实体按图层分类"的正路:一次 <see cref="GetAllHandles"/> +
        /// 一次本方法,不受任何采样限制。<see cref="GetHandlesByLayer"/> 那条按层名逐层去问的路
        /// 在图层一多就会静默漏掉整层(见上)。</para>
        /// </summary>
        string[] GetEntityLayers(ulong[] handles);

        /// <summary>
        /// 批量删除实体。成功删除的数量返回。已不存在的 handle 静默跳过。
        /// </summary>
        int DeleteEntities(ulong[] handles);

        /// <summary>
        /// 批量查询实体的 typeId（与 AcDbEntityType 对齐的整数）。
        /// 返回数组顺序与输入对齐；不存在的实体位置为 0 (Unknown)。
        /// </summary>
        int[] GetEntityTypes(ulong[] handles);

        /// <summary>
        /// 取实体的世界 AABB。outMin / outMax 是 double[3]。
        /// 返回 false 表示 handle 不存在或实体没有合法 bbox（如顶点为 0 的退化 polyline）。
        /// 用途：约束块体的"从选中实体导入"、属性 painter 的范围算等。
        /// </summary>
        bool TryGetEntityAABB(ulong handle, out double[] min, out double[] max);

        /// <summary>当前视口中已选中的实体 handle 数组。空数组 = 无选中。</summary>
        ulong[] GetSelectedHandles();

        /// <summary>
        /// 批量设实体可见性。返回成功设置的数量；不存在的 handle 静默跳过。
        /// 用途：实体转块体 / 体素格网体积生成块体后隐藏源网格（块体在体内被不透明源面遮挡）。
        /// </summary>
        int SetEntitiesVisible(ulong[] handles, bool visible);

        /// <summary>
        /// 取实体的三角网世界坐标几何。
        /// 成功返回 true 且 worldVerts = [x0,y0,z0, x1,y1,z1, ...] (3·V doubles)，
        /// triangleIndices 长度为 3·F；handle 不存在 / 不是 mesh / 几何空时返回 false。
        ///
        /// 用途：约束块体的"Mesh 几何精确切"。BlockModelLib 据此对每个 cell 中心做
        /// 点-in-mesh 测试，决定保留 / 删除。
        /// </summary>
        bool TryGetMeshGeometry(ulong handle, out double[] worldVerts, out int[] triangleIndices);

        /// <summary>
        /// 取 AcDbPolyline 的世界顶点（扁平 [x0,y0,z0,...]，长度 3·V）+ 是否闭合。
        /// handle 不存在 / 不是多段线 / 顶点 &lt; 2 时返回 false。
        /// 用途："读取约束线原始几何"的场景，如侧面三角网（放样：顶线 + 底线 → 侧壁三角网）。
        /// </summary>
        bool TryGetPolylineWorldVertices(ulong handle, out double[] worldXyz, out bool closed);

        // ── 几何构造（LasLib 散点 TIN） ─────────────────────────────
        /// <summary>
        /// 从当前选集中的多段线顶点构建 2.5D TIN（散点 Delaunay）。
        /// 返回 PMCB 二进制载荷；空数组 = 失败。详见 PMCB 布局注释。
        /// </summary>
        byte[] BuildConstrainedDelaunayFromSelectionBinary();

        // ── 异步约束 Delaunay（防 UI 假死）：采集(UI线程)→计算(后台线程)→取结果(UI线程) ──
        // 同步 BuildConstrainedDelaunayFromSelectionBinary 会把数十秒级的三角化压在调用线程，
        // 大/病态输入下冻结整个 UI。UI 触发请改走下面三联：
        //   1) CdtCollectSelection（UI 线程）：快照选中多段线，返回 token；0 = 无文档/异常。
        //   2) CdtComputeToken（可在后台 Task.Run 调）：对 token 跑三角化（耗时段，不碰 AcDb）。
        //   3) CdtTakeResultBinary（UI 线程）：取走 PMCB 二进制并删 token；空数组 = 无结果。
        //   CdtDiscard（取消时调）：释放 token。

        /// <summary>异步 CDT 第 1 步：UI 线程快照选中多段线 → token（0=无文档/异常）。</summary>
        ulong CdtCollectSelection();

        /// <summary>查 token 输入点数（UI 线程）：用于决定是否弹「抽稀/用点」选择框；0=无效 token。</summary>
        int CdtJobPointCount(ulong token);

        /// <summary>异步 CDT 第 2 步：对 token 跑三角化（耗时，<b>可在后台线程调</b>，不碰 AcDb）。
        /// 大数据(点多)自动走 LasLib 2.5D 无约束快路径；小数据走约束 CDT。</summary>
        void CdtComputeToken(ulong token);

        /// <summary>异步 CDT 第 3 步：UI 线程取走 PMCB 二进制并删 token；空数组=无结果。</summary>
        byte[] CdtTakeResultBinary(ulong token);

        /// <summary>异步 CDT 取消：释放 token 暂存的多段线/结果。</summary>
        void CdtDiscard(ulong token);

        // ── 批量点 / 节点高程 / 点样式（MeshLib 展点 / 赋节点高程 / 修改点样式）──

        /// <summary>
        /// 批量在 ModelSpace 新建 AcDbPoint。xyzFlat 长度必须是 3 的倍数 [x0,y0,z0,...]。
        /// 通过 AcDbBatchAddEntityCommand 入库，整批一步 Undo。
        /// 返回实际新建的点数。
        /// </summary>
        int AddPointsBatch(double[] xyzFlat);

        /// <summary>
        /// 对当前选集中的 Polyline / Point 实体按公式 Z = a·X + b·Y + c 赋节点高程。
        /// 用 PolylineReplaceCommand / PointPositionCommand 包装入 CompositeCommand，一步 Undo。
        /// 返回被修改的实体数。
        /// </summary>
        int AssignZByFormulaToSelection(double a, double b, double c);

        /// <summary>
        /// 批量修改 AcDbPoint 的显示样式 (Cross=0 / X=1 / Dot=2) 与大小。
        /// 不参与 Undo（v1 视为视觉调整）。返回实际修改数。
        /// </summary>
        int SetPointStyle(ulong[] handles, int style, double size);

        // ── 二进制批量实体导入（PMBI 通道） ─────────────────────────────
        /// <summary>
        /// 把一段 PMBI 二进制载荷（图层定义 + Line/Polyline/Point/Text/MText/Hatch/TriangleMesh
        /// 实体）整批导入 ModelSpace，一步 Undo。载荷用 <see cref="PitMine.Platform.Geometry.PmbiWriter"/>
        /// 构造，格式见其文档；引用的图层须在 buffer 中先于实体写出。
        ///
        /// 与 <see cref="AddPointsBatch"/> 的区别：后者只接点；本方法接任意混合实体（含三角网与文字），
        /// 适合"程序化生成几何（钻孔柱状图 / 等值线 / 标注）一次性入图"的场景。
        /// 返回 true 表示引擎成功导入；false 表示载荷为空或 native 失败（不抛异常）。
        /// </summary>
        bool ImportEntitiesBinary(byte[] pmbi);

        /// <summary>
        /// 煤层露头着色·完整面：克隆一张现状面三角网（handle=<paramref name="srcHandle"/>），把
        /// <paramref name="vertexIndices"/> 指定的顶点改成 <paramref name="vertexRgb"/>（0x00RRGGBB）对应色，
        /// 其余顶点保留源网原颜色（源网无逐顶点色则退白），整片抬升 <paramref name="lift"/> 米避 z-fight，
        /// 落到名为 <paramref name="layerName"/> 的图层（不存在则建），入库支持 Undo。
        /// vertexIndices 与 vertexRgb 等长；返回新实体 handle（0=失败）。
        /// 供「按节点着色」把露头带整合进一张完整现状面。
        /// </summary>
        ulong BuildRecoloredMeshOnLayer(ulong srcHandle, uint[] vertexIndices, uint[] vertexRgb, double lift, string layerName);

        /// <summary>
        /// 煤层露头着色·交线细分面：用调用方给的几何直接建一张逐顶点着色的三角网。
        /// <paramref name="worldXyz"/> = 世界坐标扁平 [x,y,z,...]，<paramref name="triangles"/> = 顶点索引（每 3 个一面），
        /// <paramref name="vertexRgb"/> = 每顶点 0x00RRGGBB（<c>0xFFFFFFFF</c> = 用 <paramref name="srcHandle"/> 源面的生效色，
        /// 单色面即那一个实体色）。basePoint/实体色/透明度取自源面，整片抬升 <paramref name="lift"/> 米避 z-fight，
        /// 落到 <paramref name="layerName"/> 图层（不存在则建），并挂「逐顶点原色」着色模式使专题色不受全局着色模式影响。
        /// 入库支持 Undo；返回新实体 handle（0=失败）。
        /// 供「沿顶/底板交线细分现状网后逐面着色」这条精确露头线路径。
        /// </summary>
        ulong BuildColoredMeshOnLayer(ulong srcHandle, double[] worldXyz, uint[] triangles, uint[] vertexRgb, double lift, string layerName);
    }
}
