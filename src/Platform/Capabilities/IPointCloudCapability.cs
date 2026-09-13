// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IPointCloudCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 点云能力域：LAS 加载、最近错误查询、LasLib 快路径 TIN 构建。
    ///
    /// 设计目的：
    ///   - 把 IEngineService 上 3 个 PointCloud 方法收敛到独立 capability，
    ///     与 Document / Entity / Selection / View / UiExtensionPoint 同级
    ///   - 第三方插件无需引用 Platform.Internal，只通过 ctx.Capabilities.TryGet&lt;T&gt; 访问
    ///   - 失败语义：方法本身不抛异常；Host 实现会吞 P/Invoke 异常并返回保守值
    /// </summary>
    public interface IPointCloudCapability
    {
        /// <summary>从指定路径加载 LAS 点云；返回 false 时调 <see cref="GetLastError"/> 取详细原因。</summary>
        bool LoadPointCloud(string path);

        /// <summary>
        /// 【后台线程安全】仅构建/校验该 LAS 的 `.octree` 缓存（首次大文件可能很慢），<b>不触碰引擎状态</b>。
        /// 用于在 <c>Task.Run</c> 后台线程承担慢构建，避免大文件首次导入冻死 UI；成功后再在 UI 线程调
        /// <see cref="LoadPointCloud"/>（此时命中缓存只剩秒级 mmap）。返回 false 时调 <see cref="GetLastError"/> 取原因。
        /// </summary>
        bool EnsurePointCloudCache(string path);

        /// <summary>最近一次 LoadPointCloud 返回 false 时的人类可读错误（成功时空字符串）。</summary>
        string GetLastError();

        /// <summary>
        /// 从已加载点云构建 2.5D TIN，走 LasLib 散点快路径。
        /// </summary>
        /// <param name="datasetIndex">已加载点云索引（通常 0 = 最近加载）</param>
        /// <param name="maxInputPoints">0 表示使用全部点；&gt;0 时按等间隔下采样上限</param>
        /// <returns>JSON：{"success":bool,"error":string,"handle":ulong,"verts":int,"tris":int,"build_ms":double,"basePoint":[x,y,z]}</returns>
        string BuildTinFromPointCloud(uint datasetIndex, ulong maxInputPoints);

        /// <summary>
        /// 同 <see cref="BuildTinFromPointCloud"/>，但接受完整 LasLib TinOptions 参数；
        /// 用于 UI 参数面板暴露细节给用户。
        /// </summary>
        /// <param name="datasetIndex">已加载点云索引（通常 0）</param>
        /// <param name="maxInputPoints">0 = 全部点</param>
        /// <param name="voxelSize">体素下采样尺寸 (m)；0 = 不下采样</param>
        /// <param name="maxEdgeVoxels">剔除最长边超过此倍 voxel 的三角形；0 = 不剔除</param>
        /// <param name="adaptive">自适应抽稀（平地大体素 / 坡面小体素）</param>
        /// <param name="fineVoxelRatio">adaptive=true 时精细体素比例</param>
        /// <param name="flatZRange">adaptive=true 时"平地"判定阈值 (m)</param>
        /// <returns>JSON 同 <see cref="BuildTinFromPointCloud"/></returns>
        string BuildTinFromPointCloudEx(
            uint datasetIndex, ulong maxInputPoints,
            float voxelSize, float maxEdgeVoxels, bool adaptive,
            float fineVoxelRatio, float flatZRange);

        /// <summary>
        /// 异步 TIN 第 1 步（计算）：纯计算几何，<b>可在后台线程调用</b>（不触碰 AcDb/渲染），
        /// 返回 token。随后必须回 UI 线程调 <see cref="BuildTinCommit"/> 入库。
        /// 参数语义同 <see cref="BuildTinFromPointCloudEx"/>。计算成败都返回有效 token，由 Commit 回报。
        /// </summary>
        ulong BuildTinComputeAsync(
            uint datasetIndex, ulong maxInputPoints,
            float voxelSize, float maxEdgeVoxels, bool adaptive,
            float fineVoxelRatio, float flatZRange);

        /// <summary>
        /// 异步 TIN 第 2 步（提交）：<b>必须在 UI 线程调用</b>。取走 token 对应几何装入场景并重绘。
        /// </summary>
        /// <returns>JSON 同 <see cref="BuildTinFromPointCloud"/></returns>
        string BuildTinCommit(ulong token);

        /// <summary>异步 TIN：用户取消时调用，释放 token 暂存的几何（token 失效）。</summary>
        void BuildTinDiscard(ulong token);

        /// <summary>
        /// 切换点云全局可见性。false = 隐藏渲染但保留 GPU 数据，再切回零成本。
        /// </summary>
        void SetVisible(bool visible);

        /// <summary>当前是否可见</summary>
        bool IsVisible();

        /// <summary>
        /// 设置点云着色模式 (#2)：mode 0=真实RGB（逐点） / 1=任意单色(r,g,b ∈ [0,1])。
        /// 内部走 shader cbuffer 覆盖，零 VB 重建，切换即时生效。
        /// </summary>
        void SetColorMode(int mode, float r, float g, float b);

        /// <summary>当前点云着色模式：0=真实RGB / 1=单色。</summary>
        int GetColorMode();

        /// <summary>
        /// 统计已加载点云质量：点数/包围盒/密度/高程分布(均值·标准差·直方图)/
        /// 强度·分类(仅 LasReader 源点路径)。返回二进制 PMQS 缓冲(见 EngineInterop)；
        /// 失败返回空数组，调用方按空判定并提示。
        /// </summary>
        /// <param name="datasetIndex">已加载点云索引（通常 0）</param>
        byte[] GetQualityStats(uint datasetIndex);

        /// <summary>
        /// 点云抽稀 (#10)：对已加载点云抽稀（mode 0=体素 / 1=随机 / 2=距离 / 3=自适应保特征），
        /// 结果作为新点云数据集加入场景（非破坏式，原点云保留）。返回二进制 PMDC，失败返回空数组。
        /// voxelSize=体素/自适应体素(m)；ratio=随机保留比例(0-1)/距离最小间距(m)。
        /// </summary>
        byte[] DecimatePointCloud(uint datasetIndex, int mode, double voxelSize, double ratio);

        /// <summary>
        /// SOR 统计去噪 (#4)：对已加载点云做统计离群点剔除，结果作为新点云数据集加入场景（非破坏）。
        /// kNeighbors=邻域点数(默认16)；stdDevFactor=拒绝阈值 μ+k·σ(默认2.0)。返回二进制 PMSR，失败返回空数组。
        /// </summary>
        byte[] SorFilterPointCloud(uint datasetIndex, int kNeighbors, double stdDevFactor);

        /// <summary>
        /// 点云分割 (#9)：用选中的 1 条闭合多段线裁剪点云（keepInside 圈内/圈外），
        /// 结果作为新点云数据集加入场景（非破坏）。返回二进制 PMSG，失败返回空数组。
        /// </summary>
        byte[] CropPointCloudByPolygon(uint datasetIndex, bool keepInside);

        /// <summary>
        /// 圈范围向下算量：用选中的 1 条闭合多段线圈定范围 + 向下深度 <paramref name="depth"/>（m），
        /// 在数据集 <paramref name="datasetIndex"/> 的点云上直接栅格化（免建 TIN）算「顶部 depth 米」方量 ——
        /// 底面 = 圈内最高地表 − depth，体积 = Σ_圈内格 max(格高程 − 底面, 0)·cell²。
        /// 只读点云、不落盘、不入场景。返回二进制 PMVP（由 Volume.VolumeInPolygonResult 解析），失败返回空数组。
        /// </summary>
        /// <param name="depth">向下深度 (m，&gt;0)</param>
        /// <param name="cellSize">格网尺寸 (m)；&lt;=0 自动取范围/200</param>
        /// <param name="aggMode">点云→格取高程：0=最低点 min-Z(取地面，抗地面以上离群点) / 1=平均</param>
        byte[] VolumeInPolygonFromCloud(uint datasetIndex, double depth, double cellSize, int aggMode);

        /// <summary>
        /// 圈范围向下算量·交互版：多边形范围由调用方交互取点得到（<paramref name="ringXy"/>=[x0,y0,x1,y1,…]，
        /// 至少 3 个顶点），口径同 <see cref="VolumeInPolygonFromCloud"/>。<paramref name="drawBoundary"/>=true 时，
        /// 算完把边界各顶点<b>落到点云地表高程</b>、作为闭合多段线画到图层「点云_算量范围」（可 Undo）——
        /// 这就是「圈定后线自动落到点云高程」。返回二进制 PMVP（由 Volume.VolumeInPolygonResult 解析），失败返回空数组。
        /// </summary>
        byte[] VolumeInPolygonFromCloudRing(uint datasetIndex, double[] ringXy, double depth, double cellSize, int aggMode, bool drawBoundary);

        /// <summary>
        /// 一键清除场景中【全部】点云数据集（破坏式但<b>可 Ctrl+Z 撤销</b>，引擎内压入 CommandStack）。
        /// 无需任何选择/对话框，点击即清。返回二进制 PMPC（清除的数据集数 + 总点数），失败返回空数组。
        /// </summary>
        byte[] ClearAllPointClouds();

        /// <summary>
        /// 高程截断 (#14)：保留 Z 在 [zMin,zMax] 区间内的点，结果作为新点云数据集加入场景（非破坏）。
        /// 返回二进制 PMZC，失败返回空数组。
        /// </summary>
        byte[] ZClipPointCloud(uint datasetIndex, double zMin, double zMax);

        /// <summary>
        /// 坐标转换 (#14)：对点云绕中心 (cx,cy) 旋转 rotDeg° 并平移 (dx,dy,dz)，
        /// 结果作为新点云数据集加入场景（非破坏）。返回二进制 PMTF，失败返回空数组。
        /// </summary>
        byte[] TransformPointCloud(uint datasetIndex, double rotDeg, double cx, double cy, double dx, double dy, double dz);

        /// <summary>
        /// 坡顶/坡底线提取 (#13) — 计算阶段（线程安全，应在后台线程调用以免卡死 UI）：
        /// 直接在已加载点云上栅格化 DEM，用安全挡墙(白顶帽)提坡顶线、坡面下沿提坡底线。
        /// 只读点云、不碰场景，返回 token；随后须在 UI 线程调 <see cref="CommitBenchLines"/> 入库。
        /// 任一参数传 &lt;=0 表示用算法默认值。返回 0 视为失败。
        /// </summary>
        /// <param name="datasetIndex">已加载点云索引（通常 0）</param>
        /// <param name="cellSize">DEM 网格尺寸 (m)；越大越快越粗</param>
        /// <param name="bermRadius">挡墙顶帽结构元半径（格）</param>
        /// <param name="bermMinHeight">挡墙最小高 (m)，低于此不算挡墙</param>
        /// <param name="slopeSteepDeg">陡坡面阈值 (°)</param>
        /// <param name="minRiserHeight">最小台阶高 (m)，矮于此的坡面忽略</param>
        /// <param name="minLineLen">最短输出线长 (m)，短于此丢弃</param>
        /// <param name="clipRingsXy">作业区域裁剪环：各环扁平 XY 顺序拼接 [x,y,x,y,...]（与 <paramref name="clipRingSizes"/> 配套）；空=不裁剪</param>
        /// <param name="clipRingSizes">各环顶点数（逐环），Σ·2 == clipRingsXy.Length；空=不裁剪</param>
        /// <param name="clipMode">裁剪模式：0=全图(不裁剪)、1=仅区域内、2=仅区域外。提取后按区域并集把坡顶/坡底线裁到对应一侧（边界处 Z 线性插值）</param>
        ulong ExtractBenchLinesComputeAsync(uint datasetIndex,
            double cellSize, double bermRadius, double bermMinHeight,
            double slopeSteepDeg, double minRiserHeight, double minLineLen,
            double[] clipRingsXy, int[] clipRingSizes, int clipMode);

        /// <summary>
        /// 坡顶/坡底线提取 (#13) — 提交阶段（必须在 UI/渲染线程调用）：取走 token 几何，
        /// 作为多段线一步 Undo 加入图纸(图层 点云_坡顶线/点云_坡底线)。
        /// 返回二进制 PMTB(crestCount/toeCount/summary)，失败返回空数组。
        /// </summary>
        byte[] CommitBenchLines(ulong token);

        /// <summary>坡顶/坡底线提取 (#13) — 取消：释放 token 暂存几何。</summary>
        void DiscardBenchLines(ulong token);

        // ═══════════════════════════════════════════════════════════════════
        // 点云原生算子族
        //
        // 这一族与上面的 SOR/抽稀/裁剪的区别在于两点：
        //   1) 全部走 ComputeAsync/Commit/Discard 三段式。它们都是全量点级计算
        //      （整场 DEM + 多轮形态学 / 10^8 次 kNN PCA / 10^8 次最近邻查询），
        //      同步调用必然把 UI 冻死几十秒到几分钟。
        //   2) 结果格式统一为 PMCO：{新数据集索引[] , 数值[] , 直方图[] , 摘要}。
        //      所有算子的产出本质都是这四样，不再每个算子发明一种 payload。
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 点级地面滤波（渐进形态学 PMF）— 计算阶段（<b>应在后台线程调用</b>）。
        /// 在【点级】剔除车辆/设备/植被/临时堆料，而不是建完 TIN 再丢三角面 ——
        /// 后者原始点一个没少，重建 TIN 障碍物就全回来了，且丢面留洞还得再补洞。
        /// 产出地面点数据集，<paramref name="keepNonGround"/> 时另出一份非地面点供人工核对
        /// （形态学滤波对"尺寸接近窗口"的物体本质上无法判别，结果必须可回看、可改参重跑）。
        /// nums = [输入点数, 地面点数, 剔除点数, 其中低于地面点数]。返回 0 视为失败。
        /// </summary>
        /// <param name="cellSize">DEM 格网 (m)，&lt;=0 自动</param>
        /// <param name="maxWindowM">最大待剔物体尺寸 (m)：植被~5 / 轻车~8 / 矿卡~15 / 电铲~25</param>
        /// <param name="terrainSlopeDeg">地形坡度允许 (°)，须 ≥ 真实最陡台阶坡面角，否则陡壁会被剔掉</param>
        /// <param name="initElevThresh">初始高差阈值 (m)，兼作点级容差</param>
        /// <param name="belowGroundThresh">低于地面多少米算井下/多路径噪声 (m)，&lt;=0 关闭</param>
        ulong GroundFilterComputeAsync(uint datasetIndex, double cellSize, double maxWindowM,
                                       double terrainSlopeDeg, double initElevThresh,
                                       double belowGroundThresh, bool keepNonGround);

        /// <summary>
        /// 半径离群去噪 ROR — 计算阶段（<b>应在后台线程调用</b>）。
        /// 与 SOR 互补：SOR 判"相对离群"（邻距超过全局 μ+kσ），成片飞点会把 μ 抬起来于是
        /// 谁都不算离群；ROR 判"绝对稀疏"（半径内邻点数不足），对孤立飞点/飞鸟/扬尘/配准
        /// 鬼影更干净。nums = [输入点数, 保留点数, 剔除点数, 实际采用半径]。
        /// </summary>
        /// <param name="radius">搜索半径 (m)，&lt;=0 自动取 3× 平均点间距</param>
        /// <param name="minNeighbors">半径内至少邻点数，少于此则剔除</param>
        ulong RorFilterComputeAsync(uint datasetIndex, double radius, int minNeighbors);

        /// <summary>
        /// 逐点法向估计（kNN PCA）— 计算阶段（<b>应在后台线程调用</b>）。
        /// 结果写入点云旁 <c>normals.bin</c> sidecar，<b>不产生新数据集</b>；之后的坡度/坡向/
        /// 曲率分析直接复用，不必每个按钮各跑一遍分钟级的 kNN PCA。
        /// nums = [输入点数, 成功点数, 退化点数, 耗时ms]。
        /// </summary>
        ulong EstimateNormalsComputeAsync(uint datasetIndex, int kNeighbors);

        /// <summary>
        /// 逐点坡度/坡向/曲率着色 — 计算阶段（<b>应在后台线程调用</b>）。
        /// 由法向导出，故在垂直壁面与反坡上同样有效 —— 2.5D TIN 连这类几何都表达不了，
        /// 更算不出它们的坡度。产出一份着色点云新数据集（不改渲染管线：点云顶点缓冲是
        /// points.bin 的 mmap 零拷贝直传，往顶点里塞标量通道就会废掉那条快路径）。
        /// nums = [点数, 区间下限, 区间上限, 均值, attr, 是否复用了缓存法向]，hist = 32 档。
        /// </summary>
        /// <param name="attr">0=坡度(°) / 1=坡向(罗盘°) / 2=曲率(表面变异度)</param>
        /// <param name="colormap">0=地形色 / 1=灰阶 / 2=分歧色(蓝-白-红)</param>
        /// <param name="clampLo">显示区间下限；hi&lt;=lo 时按 attr 取默认（坡度 0-90 / 坡向 0-360 / 曲率 p99）</param>
        /// <param name="clampHi">显示区间上限</param>
        ulong PointAttribShadeComputeAsync(uint datasetIndex, int attr, int colormap,
                                            double clampLo, double clampHi);

        /// <summary>
        /// C2C 两期点云差异（边坡位移监测）— 计算阶段（<b>应在后台线程调用</b>）。
        /// 逐点最近邻距离；<paramref name="signedByZ"/> 时按 Δz 定正负（隆起为正 / 沉降为负）。
        /// 与「两期算量」的根本区别：格网法要求每个 (x,y) 只有一个 Z，垂直壁面与反坡在
        /// 2.5D 栅格里无法表达、位移也就量不出来；C2C 直接量 3D 位移。
        /// nums = [A点数, 匹配, 未匹配, 均值, 绝对均值, 标准差, RMS, 最小, 最大, P95, 直方图下限, 上限]。
        /// </summary>
        /// <param name="maxDist">忽略超过此距离的匹配 (m)，&lt;=0 不限。超限点计为未匹配，
        /// 避免两期不重叠时伪造出巨大位移。</param>
        ulong CloudToCloudDistanceComputeAsync(uint idxA, uint idxB, double maxDist,
                                                bool signedByZ, int colormap);

        /// <summary>
        /// 点云算子族 — 提交阶段（<b>必须在 UI 线程调用</b>）：把计算阶段落盘的结果挂成
        /// 新点云数据集并重绘。返回二进制 PMCO（新数据集索引 / 数值 / 直方图 / 摘要），
        /// 失败返回空数组。
        /// </summary>
        byte[] CloudOpCommit(ulong token);

        /// <summary>点云算子族 — 取消：释放 token 暂存的落盘结果。</summary>
        void CloudOpDiscard(ulong token);

        /// <summary>
        /// 点云直接剖面：用选中的 1 条多段线在【点云】上开缓冲带取点成图。
        /// 不建面、不插值 —— 缓冲带内没点的站点回 NaN，而不是像 TIN 剖面那样被长边桥接
        /// 出一段根本不存在的平滑地面。返回二进制 PMPR，失败返回空数组。
        /// </summary>
        /// <param name="halfWidth">缓冲带半宽 (m)</param>
        /// <param name="step">站点间距 (m)</param>
        /// <param name="agg">0=最低点(地面) / 1=均值 / 2=最高点</param>
        byte[] SampleCloudProfile(uint datasetIndex, double halfWidth, double step, int agg);

        /// <summary>
        /// 已加载点云清单（索引 / 点数 / 包围盒 / 后端 / 可见性 / 是否已有法向 / 名称 / 路径）。
        /// 所有算子的数据源下拉框与点云管理面板都读这里；返回二进制 PMLS，失败返回空数组。
        /// 取代了"对 datasetIndex 0..15 逐个调质量统计去探测"的老做法 —— 那样慢、上限写死
        /// 16 个、而且拿不到名字和可见性。
        /// </summary>
        byte[] ListPointClouds();

        /// <summary>
        /// 单个点云的管理操作。返回二进制 PMDM，失败返回空数组。
        /// 单数据集显隐靠"从视口摘除/挂回"表达 —— <see cref="SetVisible"/> 是全局开关，
        /// 拿它做单个点云的显隐会把别的点云一起关掉。
        /// </summary>
        /// <param name="action">0=移除 / 1=显示 / 2=隐藏 / 3=重命名（用 <paramref name="newName"/>）</param>
        byte[] ManagePointCloud(uint datasetIndex, int action, string? newName);

        /// <summary>
        /// 点云精确拾取：屏幕点 → 最近的【真实点】。返回二进制 PMPK，失败返回空数组。
        /// 引擎既有的 SceneHitFn 只打 octree leaf 包围盒，而一个 leaf 最多 20 万点、可以
        /// 几十米宽 —— 拿它当落点去测距误差能到几十米，这也是"在裸点云上量距离不可用"的根因。
        /// </summary>
        /// <param name="radiusWorld">射线周围的搜索圆柱半径 (m)</param>
        byte[] PickPointCloudPoint(int screenX, int screenY, double radiusWorld);
    }
}
