// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IMeshOpsCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 三角网编辑算子能力（AlgoCore/MeshLib 桥接）。
    ///
    /// 所有方法都遵循"从当前选集自动抓 mesh / polyline"模式 ——
    /// 模块层无需自己处理几何抽取与写回，native 端一气呵成。
    ///
    /// 返回字符串为 native 端写入的 JSON 报告；模块层用对应的解析类拿强类型。
    /// 失败情况下返回的也是 JSON {"success":false,"error":"..."}，绝不抛异常
    /// （[[feedback-error-handling]]）。
    /// </summary>
    public interface IMeshOpsCapability
    {
        /// <summary>
        /// 格网质量检测（只读，不改 mesh）。返回 PMDR buffer，由 MeshLib.Models.DiagnoseReport.ParseSafe 解析。
        /// </summary>
        byte[] DiagnoseMeshFromSelection(double tolerance = 1e-6);

        /// <summary>
        /// 拓扑修复（去退化 / 焊接 / 补洞 / 拆非流形）。原位替换 mesh 几何（保留 handle）。
        /// 返回 PMRR buffer，由 RepairReport.ParseSafe 解析。
        /// </summary>
        byte[] RepairMeshFromSelection(double tolerance = 1e-6);

        /// <summary>
        /// 补充空洞 (#4)：对选中的三角网用 MeshRepair 仅补洞（带面积阈值，不动孤立点/非流形），
        /// 原位替换 mesh 几何（保留 handle，带 Undo）。maxHoleArea = 只填面积≤此值的洞(m²，避免填满大空洞)。
        /// 返回 PMFH buffer，由 PointCloudLib.Repair.FillHoleResult.Parse 解析。
        /// </summary>
        byte[] FillHolesFromSelection(double maxHoleArea = 100.0, double tolerance = 1e-6);

        /// <summary>
        /// 移除障碍物 (#4)：对选中的 2.5D TIN 用 filter_tin_obstacles 剔除车辆/植被/高Z倒刺，
        /// 原位替换 mesh 几何（保留 handle，带 Undo）。返回 PMOF buffer，由 PointCloudLib.Repair.ObstacleFilterResult.Parse 解析。
        /// </summary>
        byte[] FilterTinObstaclesFromSelection(double heightAboveGround = 1.5, double slopeDegMax = 60.0);

        /// <summary>
        /// 等高线生产 (#8)：对选中的 2.5D TIN 用 extract_contours 生成等高线，逐条入库为多段线（图层"等高线"）。
        /// spacing = 等高距(m)。返回 PMCT buffer，由 PointCloudLib.Contour.ContourResult.Parse 解析。
        /// </summary>
        byte[] ExtractContoursFromSelection(double spacing = 5.0);

        /// <summary>
        /// 两期填挖方 (#7)：选 2 个三角网（先现状面 A，后基准面 B），逐 A 三角在 B 上采样分挖/填，
        /// 各生成子TIN（挖方红 / 填方蓝）并累计体积。返回 PMVS buffer，由 PointCloudLib.Volume.VolumeSplitResult.Parse 解析。
        /// </summary>
        byte[] VolumeColorSplitFromSelection();

        /// <summary>
        /// 剖面采样 (#12)：选 1 个三角网 + 1 条剖面多段线，沿线在 TIN 上按 step(m) 采样高程。
        /// 返回 PMPF buffer（(距离,高程) 序列），由 PointCloudLib.Profile.ProfileResult.Parse 解析。
        /// </summary>
        byte[] SampleProfileFromSelection(double step = 1.0);

        /// <summary>
        /// 顶点焊接 + 压缩孤立顶点。原位替换 mesh 几何（保留 handle）。
        /// 返回 PMWR buffer，由 WeldReport.ParseSafe 解析。
        /// </summary>
        byte[] WeldMeshFromSelection(double tolerance = 1e-6);

        /// <summary>
        /// 三角网边界提取（AABB / 凸包 / 真实边界环）。只读，不改 mesh。
        /// 返回 PMBR buffer，由 BoundaryReport.ParseSafe 解析。
        /// </summary>
        byte[] BoundaryMeshFromSelection();

        /// <summary>
        /// 沿多段线分割三角网（贯穿）。选集需 1 mesh + 1 polyline。
        /// inner 替换原 mesh handle，outer 新建。返回 PMSL buffer，由 CutMeshReport.ParseSafe 解析。
        /// </summary>
        byte[] SplitMeshByPolylineFromSelection(double tolerance = 1e-6);

        /// <summary>
        /// 闭合多段线裁剪三角网（2.5D）。选集需 1 三角网 + 1 闭合 polyline。
        /// keepInside=true 保留圈内（内裁剪），false 保留圈外（外裁剪）；保留侧替换原 mesh（可 Undo）。
        /// 返回 PMCL buffer（keptVerts/keptTris 放在 inner 字段，outer 恒为 0）。
        /// </summary>
        byte[] ClipMeshByPolylineFromSelection(double tolerance = 1e-6, bool keepInside = true);

        /// <summary>
        /// 闭合线裁剪三角网（交互式）：弹窗选内/外后调本接口，kernel 进入两步拾取
        /// （① 闭合多段线裁刀 → ② 三角网）；保留侧替换原三角网（可 Undo），结果异步经 CommandLineMessage 回显。
        /// </summary>
        void StartMeshClipByLoopInteractive(double tolerance, bool keepInside);

        /// <summary>
        /// 生成三角网边界（交互式）：调本接口后 kernel 进入单步面拾取（黄框光标），
        /// 用户点选三角网 → 提取最大边界环（开放→真实边界最大环；封闭→凸包外轮廓）→
        /// 生成闭合多段线入库（图层"边界"）；结果异步经 CommandLineMessage 回显。
        /// </summary>
        void StartBoundaryInteractive();

        /// <summary>格网质量检测（交互式）：进入单步面拾取，点选三角网 → 通知前端弹检测对话框。</summary>
        void StartDiagnoseInteractive();

        /// <summary>
        /// 沿多段线分割三角网（交互式）：两步拾取（先线后面）→ 按 XY 投影把三角网
        /// 切成左右两片（左红 / 右青）成为新实体，原 mesh 删除；一键 Ctrl+Z 还原。
        /// 切割算法走 2D 网格 + 逐三角 CDT 严格沿 polyline 细分，效率为 O(N) + 切口三角少量 CDT。
        /// 闭合三角网会按"切口环 ⇒ CDT 在 (弧长,Z) 平面三角化 ⇒ 反向缠绕贴回左右两片"自动封口。
        /// 结果异步经 CommandLineMessage 回显。
        /// </summary>
        void StartSplitMeshAlongPolylineInteractive(double tolerance);

        /// <summary>
        /// 删除三角面（交互式）：两步拾取（先 mesh，再反复点选 / 取消选面）→ 右键 / 回车确认。
        /// 选中面用黄色描边高亮；确认后删除并清孤立顶点，原 mesh 几何被替换（保留 handle / 图层 / 颜色），
        /// 一键 Ctrl+Z 还原。Esc 取消整个命令。结果异步经 CommandLineMessage 回显。
        /// </summary>
        void StartDeleteMeshFacesInteractive();

        /// <summary>
        /// 按面着色（交互式）：把对话框设好的渲染配置写回选中三角面的"逐面渲染覆盖"。
        /// 两步拾取（先 mesh，再点选面）→ 右键 / 回车确认 → 立即重绘，随工程持久化、可 Ctrl+Z 无关（直接写实体）。
        /// mode=着色模式; vmin/vmax=属性区间; colormapRGBA=256×4 色带 LUT(null=用全局 LUT)。
        /// </summary>
        void StartSetFaceRenderInteractive(int mode, float vmin, float vmax, byte[]? colormapRGBA);

        /// <summary>清除选面渲染覆盖（交互式）：两步拾取后把选中面退回全局着色。</summary>
        void StartClearFaceRenderInteractive();

        /// <summary>
        /// 面交线（交互式）：进入两步拾取，依次点选两个三角网 → 逐三角求交 + 连通拼接成交线 →
        /// 逐条建成多段线入库（图层"交线"）；结果异步经 CommandLineMessage 回显，一键 Ctrl+Z 还原。
        /// </summary>
        void StartIntersectMeshesInteractive();

        /// <summary>
        /// 刀切闭合实体（交互式）：两步拾取——先选刀(开放面)、再选闭合实体 → 保留一侧
        /// （keepBelow=true 留刀下方/删刀上方；false 留刀上方/删刀下方）、用刀在体内补片封口 →
        /// 新闭合体替换原实体（marching 无 CDT，水密）；结果异步经 CommandLineMessage 回显，一键 Ctrl+Z 还原。
        /// </summary>
        void StartCutSolidByKnifeInteractive(bool keepBelow = true);

        /// <summary>
        /// 两期三角网算量·填挖封闭体（交互式）：两步拾取——先选第一期(原始/较早)、再选第二期(现状/较新)
        /// 三角网 → 逐三角棱柱体积 &lt; minPrismVol 的丢弃；挖/填各按连通块生成独立水密封闭体入库
        /// （挖=cutRGB/图层"挖方"，填=fillRGB/图层"填方"），整批一个 Ctrl+Z。结果异步经 CommandLineMessage 回显。
        /// RGB 分量各 0..255。
        /// renderCell：渲染格网(m)。&gt;gridCell 时渲染体在粗格上构建以降三角提帧率，体积仍按 gridCell 细格算
        /// （精度不变）；&lt;=gridCell 则不解耦（几何=体积同分辨率）。
        /// </summary>
        void StartVolumeSplitClosedInteractive(double gridCell, double renderCell, double minDz, int openRadius, double minBenchH,
            byte fillR, byte fillG, byte fillB, byte cutR, byte cutG, byte cutB);

        /// <summary>
        /// 两期填挖方·封闭体（非交互，按句柄）：hA=第一期(原始/较早)、hB=第二期(现状/较新) 三角网句柄，
        /// 与 <see cref="StartVolumeSplitClosedInteractive"/> 同算法（格网法生成挖/填独立水密封闭体入库、隐藏两期原面、一个 Ctrl+Z），
        /// 但不进视口拾取 —— 供「两期点云填挖方」从已建 TIN 直接算量。RGB 各 0..255。
        /// 返回 PMVC buffer，由 PointCloudLib.Volume.VolumeSplitClosedResult.Parse 解析。
        /// </summary>
        byte[] VolumeSplitClosedByHandles(ulong handleA, ulong handleB,
            double gridCell, double renderCell, double minDz, int openRadius, double minBenchH,
            byte fillR, byte fillG, byte fillB, byte cutR, byte cutG, byte cutB);

        /// <summary>
        /// 两期填挖方·封闭体「点云直接栅格化」（非交互，按点云索引）：idxA=第一期(原始/较早)、idxB=第二期(现状/较新)
        /// 点云数据集索引 → 取两期原始点【直接栅格化】(跳过建 TIN) → 与 <see cref="VolumeSplitClosedByHandles"/> 同
        /// 封闭体装配/入库/Ctrl+Z，但高程来源是点云本身。供「两期点云直接算量」按钮。
        /// aggMode：0=min-Z(默认，取地面、抗植被/车辆/扬尘等地面以上离群点) / 1=平均；fillRadius：有界小洞填充格数(0=不填)。
        /// RGB 各 0..255。返回 PMVC buffer，由 PointCloudLib.Volume.VolumeSplitClosedResult.Parse 解析（与按句柄版通用）。
        /// </summary>
        byte[] VolumeSplitClosedFromClouds(uint idxA, uint idxB,
            double gridCell, double renderCell, double minDz, int openRadius, double minBenchH,
            int aggMode, int fillRadius,
            byte fillR, byte fillG, byte fillB, byte cutR, byte cutG, byte cutB);

        /// <summary>
        /// 两期填挖方·封闭体「按来源」：每期来源二选一——idx≥0 用【已加载点云数据集】；idx&lt;0 则直接读 LAS 文件路径(path)
        /// 取点（<b>跳过 octree 构建 / GPU 上传 / 入场景</b>，故浏览文件算量很快，不必走渲染加载的重机制）。
        /// 文件读出的点仅用于本次算量，不加入场景（只入库挖红/填蓝封闭体）。pathA/pathB 在对应 idx≥0 时可传 null。
        /// maxPointsPerFile≤0 读全部。其余参数同 <see cref="VolumeSplitClosedFromClouds"/>。返回 PMVC buffer（解析器通用）。
        /// </summary>
        byte[] VolumeSplitClosedFromSources(int idxA, string? pathA, int idxB, string? pathB,
            double gridCell, double renderCell, double minDz, int openRadius, double minBenchH,
            int aggMode, int fillRadius, int maxPointsPerFile,
            byte fillR, byte fillG, byte fillB, byte cutR, byte cutG, byte cutB);

        /// <summary>
        /// 两期填挖方·异步算量【计算阶段】（点云来源版，<b>可在后台线程调用</b>：只读点云/读 LAS 文件 + 纯几何，不碰 AcDb/渲染）。
        /// 每期 idx≥0 用已加载点云数据集 / idx&lt;0 直接读 LAS 文件路径(path)；maxPointsPerFile≤0 读全部。
        /// 返回 token（计算成败都返回有效 token，由 <see cref="VolumeSplitCommitToken"/> 统一回报）。随后须在 UI 线程 Commit 入库。
        /// </summary>
        /// <param name="clipRingsXy">可选作业区域裁剪：各环 x,y 连续拼接（[x0,y0,x1,y1,...] 多环连续）。null/clipMode=0 → 整体算。</param>
        /// <param name="clipRingSizes">每环顶点数（与 clipRingsXy 配套）。</param>
        /// <param name="clipMode">0=整体 / 1=仅区域内 / 2=仅区域外。</param>
        /// <param name="regionRingsXy">分项体积·作业区域分组：各区域各环 x,y 连续拼接（按区域→环顺序）。null/regionCount=0 → 不分项。</param>
        /// <param name="regionRingSizes">每环顶点数（按区域→环顺序，长度=Σ regionRingCounts）。</param>
        /// <param name="regionRingCounts">每区域含几个环（长度=regionCount）。</param>
        /// <param name="regionNames">每区域名（长度=regionCount，UTF-8）。</param>
        /// <param name="regionCats">每区域类别（长度=regionCount，UTF-8）。</param>
        /// <param name="regionCount">区域数；≤0 不做区域分项。</param>
        ulong VolumeSplitFromSourcesComputeAsync(int idxA, string? pathA, int idxB, string? pathB,
            double gridCell, double renderCell, double minDz, int openRadius, double minBenchH,
            int aggMode, int fillRadius, int maxPointsPerFile,
            byte fillR, byte fillG, byte fillB, byte cutR, byte cutG, byte cutB,
            double[]? clipRingsXy = null, int[]? clipRingSizes = null, int clipMode = 0,
            double[]? regionRingsXy = null, int[]? regionRingSizes = null, int[]? regionRingCounts = null,
            string[]? regionNames = null, string[]? regionCats = null, int regionCount = 0,
            double minPatchArea = 0, int[]? regionDirs = null);

        /// <summary>
        /// 两期填挖方·异步算量【计算阶段】（常规 TIN 句柄版，<b>可在后台线程调用</b>）：hA/hB=两期已建 2.5D TIN 句柄。
        /// 返回 token；随后 UI 线程 <see cref="VolumeSplitCommitToken"/> 入库（提交时隐藏两期原面）。
        /// clip* 同上：可选作业区域裁剪。region* 同源版：作业区域分组（分项体积）。
        /// </summary>
        ulong VolumeSplitFromHandlesComputeAsync(ulong handleA, ulong handleB,
            double gridCell, double renderCell, double minDz, int openRadius, double minBenchH,
            byte fillR, byte fillG, byte fillB, byte cutR, byte cutG, byte cutB,
            double[]? clipRingsXy = null, int[]? clipRingSizes = null, int clipMode = 0,
            double[]? regionRingsXy = null, int[]? regionRingSizes = null, int[]? regionRingCounts = null,
            string[]? regionNames = null, string[]? regionCats = null, int regionCount = 0,
            double minPatchArea = 0, int[]? regionDirs = null);

        /// <summary>
        /// 两期填挖方·异步算量【提交阶段】（<b>必须在 UI/渲染线程调用</b>）：取走 token 的几何 → 建实体入库 → 重绘。
        /// 返回 PMVC buffer，由 PointCloudLib.Volume.VolumeSplitClosedResult.Parse 解析。token 失效/计算失败时返回错误 PMVC。
        /// </summary>
        byte[] VolumeSplitCommitToken(ulong token);

        /// <summary>两期填挖方·异步算量【取消】：释放 token 暂存的几何（用户在计算期间取消时调用）。</summary>
        void VolumeSplitDiscardToken(ulong token);

        /// <summary>显示某类诊断错误的 overlay 标记。errorType: 0=开放边 1=非流形 2=退化 3=自相交 4=孤立点 5=重复点 6=反向面。</summary>
        void ShowDiagnosticMarkers(int errorType);

        /// <summary>清除全部诊断 overlay 标记（不影响其它业务 overlay）。</summary>
        void ClearDiagnosticMarkers();

        /// <summary>
        /// 两三角网求交线。选集需 2 mesh。返回 PMIC buffer，由 IntersectCurvesReport.ParseSafe 解析。
        /// </summary>
        byte[] IntersectMeshesFromSelection(double tolerance = 1e-6);

        /// <summary>
        /// 布尔运算。opType: 0=交 / 1=并 / 2=差 / 3=补。选 2 个封闭 mesh。
        /// 结果新建实体 + 删除原 2 mesh。返回 PMBO，由 BooleanReport.ParseSafe 解析。
        /// </summary>
        byte[] BooleanMeshesFromSelection(int opType, double tolerance = 1e-6);

        /// <summary>
        /// 分割地质体：选 1 closed solid + 1 open surface（顺序即角色）。
        /// left 替换 solid，right 新建。返回 PMSS，由 SplitBySurfaceReport.ParseSafe 解析。
        /// </summary>
        byte[] SplitMeshBySurfaceFromSelection(double tolerance = 1e-6);

        /// <summary>
        /// 多段线嵌入三角网：1 mesh + 1 polyline → 替换原 mesh 几何（细分）。
        /// 返回 PMEM，由 EmbedReport.ParseSafe 解析。
        /// </summary>
        byte[] EmbedPolylineToMeshFromSelection(double tolerance = 1e-6);

        /// <summary>
        /// 两期三角网算量：选 2 mesh 各算体积后做差（A - B）。
        /// 返回 PMVD，由 VolumeDiffReport.ParseSafe 解析。
        /// </summary>
        byte[] VolumeDiffFromSelection();

        /// <summary>
        /// 体积计算（单面对基准高程）：选 1 个 2.5D TIN，逐三角棱柱积分到基准平面 z=baseZ。只读不改几何。
        /// 返回 PMVB buffer，由 PointCloudLib.Volume.VolumeToBaseResult.Parse 解析。
        /// </summary>
        byte[] VolumeToBaseFromSelection(double baseZ);

        /// <summary>
        /// 坡顶/坡底线提取 (#13，自动)：选 1 个 2.5D TIN，逐三角坡度分类(带去噪)→提坡面分界边→
        /// 链成多段线入库(图层"坡顶线"/"坡底线")。slopeThreshDeg = 坡面判定阈值(°)。
        /// 返回 PMTB buffer，由 PointCloudLib.SlopeLine.SlopeLinesResult.Parse 解析。
        /// </summary>
        byte[] ExtractSlopeLinesFromSelection(double slopeThreshDeg = 30.0);

        /// <summary>
        /// 表面属性 (#14)：选 1 个 2.5D TIN，逐顶点算粗糙度(Z起伏RMS)+曲率(高度Laplacian)，存回 mesh
        /// 逐顶点属性（经 GPU uv 通道→Lit.hlsl mode 16 粗糙度/17 曲率 着色）。返回各自 min/max 供 colormap 区间。
        /// 返回 PMSA buffer，由 PointCloudLib.Shading.SurfaceAttribResult.Parse 解析。
        /// </summary>
        byte[] ComputeSurfaceAttribsFromSelection();
    }
}
