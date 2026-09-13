// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IPitDesignCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 露天台阶设计能力 — 由 AlgoCore/MeshLib + PitDesignLib 桥接。
    ///
    /// 所有方法都从当前选集抓 1 条 polyline 作为境界线,其余参数走签名。
    /// 返回 PMxx 二进制 buffer,由 MineAssLib.Models.ExpandBenchReport.ParseSafe 解析。
    ///
    /// 参数枚举见 [[mineass-expand-bench]]:
    ///   lineRole:   0=Toe, 1=Crest
    ///   direction:  0=Up, 1=Down
    ///   side:       0=Left, 1=Right (仅非闭合)
    ///   endCapMode: 0=自然外延, 1=切平, 2=按距离延伸 (仅非闭合)
    ///   stopMode:   0=到地表(需要 terrainMeshHandle), 1=到标高(targetElevation)
    /// </summary>
    public interface IPitDesignCapability
    {
        /// <summary>
        /// 查询当前选集是否恰好 1 条 polyline。成功返回 true 并填出参,用于对话框预填。
        /// 选 0 / 多条 / 非 polyline 时返回 false,out 参数填默认值(0/0.0/0)。
        /// </summary>
        bool TryGetSelectedSinglePolylineMeta(out bool closed, out double startZ, out int pointCount);

        /// <summary>
        /// 采矿模型·选中 2 条台阶线(坡顶 + 坡底,内部按均 Z 高者为坡顶) → CarveStrip 生成「斜面前脸的
        /// 实心抽屉」watertight 实体并入库。stripWidth=采掘带宽度/抽屉深(m),layerDipDeg=分层倾角
        /// (0=顶底水平),resampleN=重采样点数(&lt;=0 取 24)。返回新实体 handle(0=失败),message 给提示。支持 Undo。
        /// </summary>
        ulong CarveStripFromSelection(double stripWidth, double layerDipDeg, int resampleN, out string message);

        /// <summary>
        /// 采矿模型·按区域整片生成：以一个作业区域(<see cref="MineableRegionInfo"/>)为单位，自动读区域内
        /// 全部台阶线、配对坡顶/坡底成幅，逐幅 CarveStrip 建出该区所有采掘带体。采/排极性由区域 Category
        /// 自动判定(pit/mineable=采场往里向下；*_dump=排土场往外向上，几何镜像)。stripWidth=采掘带宽度(m)，
        /// layerDipDeg=分层倾角(0=顶底水平)，resampleN=重采样点数(&lt;=0 取 24)。返回成功建出的体数；
        /// message 汇总(建出 N 体 / 跳过 M 对未配对)。每幅各自支持 Undo。
        /// </summary>
        int CarveStripModelForRegion(string regionName, double stripWidth, double layerDipDeg, int resampleN, out string message);

        /// <summary>
        /// 采矿模型·按标准水平整片生成：按调用方【已配好对】的坡顶/坡底 handle 逐幅 CarveStrip 建体。
        ///
        /// 与 <see cref="CarveStripModelForRegion"/> 的分工：那个自己在区域内按"Δz 窗 + 最近质心"配对，
        /// 配不上的线成黑账；本方法【不配对】，由上层（<c>StandardLevelModel</c>：先归标准水平、
        /// 再只在相邻两级之间配）把成对的 handle 送进来，所以幅数与台阶级数对得上账。
        ///
        /// <paramref name="crestHandles"/> / <paramref name="toeHandles"/> 必须等长。
        /// <paramref name="layerNames"/> 逐幅指定入库图层（煤台阶 / 岩台阶分层管理），
        /// null 或某项为空 = 该幅入当前图层；图层不存在会自动建。调用结束恢复原当前图层。
        /// <paramref name="layerDipDegs"/> 是【逐幅】分层倾角：煤台阶走倾斜分层，倾角由上层从煤层底板
        /// 实测（各幅不同，正 = 沿推进方向往高墙里抬升，与内核 <c>CarveStripInput.layerDipDeg</c> 同号）；
        /// 岩台阶传 0 = 顶底水平。传 null = 全部 0。所以这里不能是一个全局标量。
        /// <paramref name="outVolumes"/> 回填各幅体积 m³（长度 = 幅数，失败幅为 0），供分煤/岩汇总方量与剥采比。
        /// 返回成功建出的体数；每幅各自支持 Undo。
        /// </summary>
        /// <param name="seamPerPair">
        /// 逐幅的地质面（沿底板开采 / 煤台阶用）：顶盖贴顶板并被现状面压低、底盖贴底板。
        /// null 或某幅为 null = 该幅走 <paramref name="layerDipDegs"/> 的常数角路径（岩台阶就该这样）。
        /// </param>
        int CarveStripByLevelPairs(ulong[] crestHandles, ulong[] toeHandles, string[]? layerNames,
                                   double stripWidth, double[]? layerDipDegs, int resampleN, bool isDump,
                                   SeamSurfaceGeometry?[]? seamPerPair,
                                   out double[] outVolumes, out string message);

        /// <summary>
        /// 采矿模型·按【露头带】建体（煤台阶的正路）。
        ///
        /// 与 <see cref="CarveStripByLevelPairs"/> 的分工：那个从图上台阶线配对，判据换过三版
        /// （最近质心 → 环大小 → 间距+闸门）每版都在真实图纸上配错 —— 根子是"猜哪两条是一对"不可靠。
        /// 本方法吃的是【算出来的】露头线：坡顶 = 顶板 ∩ 现状面，坡底 = 底板 ∩ 现状面，
        /// 两条线天然同属一层煤、天然一一对应，错配从根上不存在。
        ///
        /// crestXyz/toeXyz 逐带一条，扁平世界坐标 [x,y,z,...]，两数组必须等长（带数相同）。
        /// seamPerBand 逐带的顶/底板 + 现状面几何：顶盖贴顶板并被现状面压低、底盖贴底板。
        /// outVolumes 回填各带体积 m³。返回成功建出的体数；每带各自支持 Undo。
        /// </summary>
        int CarveStripByOutcropBands(double[][] crestXyz, double[][] toeXyz, string? layerName,
                                     double stripWidth, SeamSurfaceGeometry?[]? seamPerBand,
                                     out double[] outVolumes, out string message);

        /// <summary>
        /// 排土模型·按【排土条带】建壳子体（排土场的正路，极性 = 堆排）。
        ///
        /// 与 <see cref="CarveStripByOutcropBands"/> 的分工：那个是采场的（往高墙里向下剥，
        /// 上下界是煤层顶/底板）；本方法是排土场的 —— **往坡脚外向上堆**，几何与前者镜像
        /// （内核 <c>CarveStripInput.isDump</c> 管的就是这件事：推进方向反向 + 分层 dz 符号翻
        /// + 三角缠绕反转保法向朝外）。
        ///
        /// 上下界不是地质面而是【两个水平面】：排土台阶按标准水平分级，同一级的坡顶标高、
        /// 坡底标高固定不变 —— 这正是"排土比采场规整"的本义。两个平面直接从轨自带的 Z 取
        /// （<c>crestXyz[i]</c> 的 Z = 该级坡顶标高，<c>toeXyz[i]</c> 的 Z = 坡底标高），
        /// 调用方不必再传一遍。
        ///
        /// 【为什么必须喂面而不是传 null 让内核走常数角退路】岩台阶那轮实测过：传 null 时
        /// 1,262 条【全部】建失败，改成喂两片平面网格后立刻通。内核那条路要面，就给它面。
        ///
        /// 【不喂现状面】采场要用现状面压顶（现状以上的煤早剥掉了），排土场正相反 ——
        /// 排土是堆到现状面【之上】，压顶会把体压没。
        ///
        /// crestXyz/toeXyz 逐带一条，扁平世界坐标 [x,y,z,...]，两数组必须等长（带数相同）；
        /// 同一条带的坡顶/坡底轨须【逐点对应】（调用方负责方向与起点对齐，见 <c>DumpStripPlanner</c>）。
        /// outVolumes 回填各带体积 m³（占容方口径），outHandles 回填各带的实体 handle（失败带为 0，
        /// 供位置清单指回图上的体）。返回成功建出的体数；每带各自支持 Undo。
        /// </summary>
        /// <param name="stripWidths">
        /// 【逐格的推进宽度】，与 <paramref name="crestXyz"/> 一一对应。
        ///
        /// 不能全场一个 W —— 凹弯把推进夹窄的位置有效宽会远小于名义 W（实测最窄 6.7m / 名义 40m）。
        /// 按名义宽建出来的体会推到自交处以外、压到邻幅头上：实测 1279 个位置里 67 个方量与清单
        /// 差 >5%、最差 6.56 倍，而调用方还会把这个过建的体积回填成库容 —— 幽灵库容。
        /// </param>
        int CarveDumpStripsByRails(double[][] crestXyz, double[][] toeXyz, string? layerName,
                                   double[] stripWidths, out double[] outVolumes, out ulong[] outHandles,
                                   out string message);

        /// <summary>
        /// 采矿模型·体积算量：对当前选中的三角网实体逐个算体积并汇总。
        /// out count = 参与汇总的实体数(体积&gt;0 才计)。返回总方量(m³)。
        /// </summary>
        double SumSelectedMeshVolume(out int count);

        /// <summary>
        /// 选集中前 2 个 AcDbPoint 位置(道路 jig 简化版)。
        /// outArr 长度必须 ≥ 6,返回填入的点数(0/1/2)。
        /// 1 个 → 起点;2 个 → 起点 + 第二点(用户算方位角+长度)。
        /// </summary>
        int TryGetSelectedPointsForRamp(double[] outArr6);

        /// <summary>
        /// 把视口客户区屏幕像素坐标转世界坐标。供"屏幕拾取"jig 用。
        /// 失败时 wx/wy/wz 为 0(底层 PitMine_ScreenToWorld 无 success 码)。
        /// </summary>
        void ScreenToWorld(int sx, int sy, out double wx, out double wy, out double wz);

        /// <summary>
        /// 进入"屏幕取点"模式:之后每次视口左键 → onPicked(已反算到几何表面的世界坐标),
        /// 且【吞掉该点击不选中实体】;右键 / Esc → onCancel(已取的点保留)。
        /// 这是"屏幕拾取"jig 的正确实现 —— 视口是 WinForms/原生面板,WPF 鼠标钩子收不到其点击,
        /// 必须在宿主接视口原生点击处拦截。返回 false = 当前宿主未提供(非交互宿主)。
        /// 用完(取够点 / 取消)务必调 <see cref="EndScreenPointPick"/>。
        ///
        /// <para><b>Z 从哪来</b>：对<b>可见三角网</b>射线求交(<c>PickWorldOnGeometry</c>)。本重载
        /// (<c>allowMissPlanePick=false</c>) <b>只在命中三角网时回调</b>，所以拿到的 XYZ 三个分量都是真的。
        /// 图上没有地形面/实景面(纯线框、只有中心线与台阶线的视图)时点击**不产生回调** ——
        /// 这不是 bug，是"没面就没标高"。要在那种视图下也能取点，用带
        /// <c>allowMissPlanePick=true</c> 的重载，并且**自己解决 Z**。</para>
        ///
        /// <para>2026-08-17 之前引擎在未命中时也返回命中，这个闸门形同虚设：所有调用方都在
        /// allowMiss 模式下跑，Z 悄悄变成投影面值(通常 0)。矿区标高在 1128~1515m，
        /// 「点对点寻径」因此报出"离最近路面 1239m"。已在 <c>PitMine_PickWorldOnGeometry</c> 改回。</para>
        /// </summary>
        bool BeginScreenPointPick(System.Action<double, double, double> onPicked, System.Action onCancel);

        /// <summary>
        /// 同 <see cref="BeginScreenPointPick(System.Action{double,double,double},System.Action)"/>，但
        /// <paramref name="allowMissPlanePick"/>=true 时：即使未命中视口几何也回调（参数为 ScreenToWorld
        /// 反算的平面世界 XY，Z 为投影面值）。供"调用方自带后端高程模型、不依赖视口可见网格"的取点
        /// （如台阶平盘标高查询用隐藏 TIN 自算 Z）。
        ///
        /// <para><b>开了它就等于接管了 Z</b>：回调里的 Z 可能是投影面值(通常 0)，而且**没有任何标志**
        /// 告诉你这一次是命中还是兜底。所以开它的调用方必须三选一：① 只用 XY（剖面两点、虚拟钻孔、
        /// 中线点选都是这类）；② 自己按后端模型采 Z（<c>SampleZ</c> / 铺贴地形）；③ 判定 Z 可信与否
        /// 再决定用不用（见 <c>RoadGraph.NearestEdgeForPick</c>：Z 落在路网标高带内才拿它分辨上下台阶）。
        /// <b>把回调 Z 直接写进台账/落库是第四种，不允许</b> —— 那个 0 和真实的 0m 标高在库里分不开
        /// （同 mineable_region 的 z=0 陷阱）。</para>
        /// </summary>
        bool BeginScreenPointPick(System.Action<double, double, double> onPicked, System.Action onCancel,
                                  bool allowMissPlanePick);

        /// <summary>
        /// 同上，另可关掉"橡皮筋预览"(<paramref name="showRubberBand"/>=false)。
        /// 橡皮筋 = 把已取点和光标连成青色折线预览，只对"取一串点连成线"(道路中线/剖面线/坡道)有意义；
        /// 逐个取独立点的场景(见煤点/虚拟钻孔/放置点)应关掉：一来图上会多出一条无意义的连线，
        /// 二来预览要在【每次鼠标移动】都对全场三角网做射线求交，大网面(实景三维)下拾取会明显发卡。
        /// 默认(其它重载)= true，保持既有行为不变。
        /// </summary>
        bool BeginScreenPointPick(System.Action<double, double, double> onPicked, System.Action onCancel,
                                  bool allowMissPlanePick, bool showRubberBand);

        /// <summary>
        /// 同上，另可把橡皮筋换成<b>单线</b>(<paramref name="plainRubberBand"/>=true)。
        /// <para>默认那条橡皮筋是<b>坡道</b>预览：按 6m 路宽外扩出左右两条绿边线，并把第 2→3 点之间
        /// 拧成"回头弧"。画坡道正合适，画<b>道路中心线</b>就全是噪声 —— 图上凭空多出三条平行线、
        /// 手点的拐点被自动抹圆，看不出自己到底画了条什么线。</para>
        /// <para>单线模式只把"已取点 + 当前光标"连成一条青线，不外扩、不改点。</para>
        /// 默认(其它重载)= false，保持既有行为不变。
        /// </summary>
        bool BeginScreenPointPick(System.Action<double, double, double> onPicked, System.Action onCancel,
                                  bool allowMissPlanePick, bool showRubberBand, bool plainRubberBand);

        /// <summary>
        /// "只取屏幕点、不解算曲面"的取点模式:视口左键 → onPickedScreen(视口内像素 sx, sy),
        /// 【点击时不做射线求交】,也不画落点标记/橡皮筋;右键 / Esc → onCancel。
        /// 曲面真实落点由调用方在需要时(如弹窗点「确定」后)自己调
        /// <see cref="TryPickSurfacePoint"/> 解算 —— 因为射线求交是逐三角暴力算,
        /// 实景三维那种百万级网面下放在点击路径上会让"点一下"明显发顿。
        /// 期间不要移动相机,否则同一屏幕点对应的曲面点会变。用完同样调 <see cref="EndScreenPointPick"/>。
        /// </summary>
        bool BeginScreenPointPickRaw(System.Action<int, int> onPickedScreen, System.Action onCancel);

        /// <summary>
        /// 屏幕点(视口内像素) → 曲面真实落点(对可见三角网射线求交,取沿射线最近的一层)。
        /// 未命中任何三角网返回 false(此时 out 全 0)。相机不可用/非交互宿主也返回 false。
        /// </summary>
        bool TryPickSurfacePoint(int screenX, int screenY, out double wx, out double wy, out double wz);

        /// <summary>退出"屏幕取点"模式,恢复正常的点选/框选。幂等。</summary>
        void EndScreenPointPick();

        /// <summary>清除屏幕拾取的落点 × 标记(对话框确认/取消/关闭时调,免留残迹)。幂等。</summary>
        void ClearScreenPickMarkers();

        /// <summary>
        /// 进入"拖拽框选矩形范围"模式：视口左键按下定起点 → 拖动实时显示半透明矩形选框 → 抬起完成，
        /// onComplete(minX, minY, maxX, maxY 世界坐标)。右键/Esc → onCancel。完成后选框 overlay 保留
        /// (供持续显示范围)，调用方在清除/关闭时调 <see cref="EndRangeBoxPick"/> 移除。非交互宿主返回 false。
        /// </summary>
        bool BeginRangeBoxPick(System.Action<double, double, double, double> onComplete, System.Action onCancel);

        /// <summary>退出"拖拽框选矩形"模式并清除选框 overlay（对话框关闭/取消时调）。幂等。</summary>
        void EndRangeBoxPick();

        /// <summary>
        /// 启动台阶交互设计 jig(spike)。需先在场景中选 1 条【闭合】境界线作为采场地表开口。
        /// 进入后:移动鼠标实时调整开采深度/台阶层数并预览坡面,点击或回车确认落地,Esc 取消。
        /// 返回 true=成功进入 jig;false=选集 ≠ 恰好 1 条闭合多段线 / 无活动文档。
        /// </summary>
        bool StartBenchDesignJig();

        /// <summary>
        /// 启动台阶调整工具(#5,联动版)。需先选【任意一条】已生成的台阶线。
        /// 进入后:拖该线夹点 → 指定的一侧(上覆/下伏)台阶整体联动重算、固定侧原样,
        /// 原地替换整组(一步 Undo)。回车落地、Esc 取消。
        /// </summary>
        /// <param name="propagateUp">true=比该线高的台阶(上覆)跟随;false=比该线低的台阶(下伏)跟随。</param>
        /// 返回 true=成功进入;false=选集 ≠ 恰好 1 条多段线 / 无活动文档。
        bool StartBenchAdjust(bool propagateUp = true);

        /// <summary>
        /// 可编辑台阶面:选【台阶坡面 mesh 或任意台阶线】→ 经其所属「工程位置」(持久,存盘
        /// 重开仍在)→ 整组台阶线转夹点 → 拖任意一条按约束联动实时重算,确认落地并同步回工程位置。
        /// </summary>
        /// <param name="propagateUp">true=比该线高的台阶(上覆)跟随;false=比该线低的台阶(下伏)跟随。</param>
        /// 返回 true=成功进入;false=选集无台阶组 / 无对应工程位置 / 无活动文档。
        bool StartEditBenchFace(bool propagateUp = true);

        // ── 处理尖灭(对已建台阶组施加尖灭,线面同步、一步 Undo) ──

        /// <summary>
        /// 从当前选集解析台阶组键(选中【坡面 mesh 或任意台阶线】,取首个带 benchGroupKey 者)。0=未找到。
        /// 「处理尖灭」在进入取点 / 换选(煤层模式要选顶/底板)前先抓住组键,避免选集变化丢失目标。
        /// </summary>
        ulong ResolveBenchGroupKey();

        /// <summary>当前选集所有实体句柄(供煤层尖灭逐一拾取顶板面 / 底板面 mesh)。空选集返回空数组。</summary>
        ulong[] GetSelectedHandles();

        /// <summary>
        /// 尖灭台阶(手动·楔尖点):对组键 groupKey 施加,所选点为楔尖端,整组台阶沿弧长全程收口到该点
        /// (坡顶坡底交汇)。收口后的新台阶线 + 重缝坡面同步落地、回写工程位置、一步 Undo。返回 true=成功。
        /// </summary>
        bool ApplyManualPinch(ulong groupKey, double tipX, double tipY, double tipZ);

        /// <summary>
        /// 尖灭台阶(手动·定向楔形):按【被点坡顶线 handle】定位所属工程位置组 + 该台阶,以 (tip) 为楔尖点
        /// 对该台阶 crest 收成楔形、(propagateUp 时)上部台阶联动收口。crestHandle 的 benchGroupKey 直接定组,
        /// 不依赖预先选集。返回 true=成功。
        /// </summary>
        bool ApplyManualPinchAtCrest(ulong crestHandle, double tipX, double tipY, double tipZ, bool propagateUp);

        /// <summary>
        /// 尖灭台阶(手动·全取点版,绕开选集):tip=楔尖点;(crest..)=在坡顶线附近点的一点,内核扫模型空间
        /// 找离它最近的台阶线当目标台阶,该台阶 crest 收成楔形、(propagateUp 时)上部台阶联动。返回 true=成功。
        /// </summary>
        bool ApplyManualPinchAtPoint(double tipX, double tipY, double tipZ, double crestX, double crestY, double crestZ, bool propagateUp);

        /// <summary>
        /// 处理尖灭·手动(原生两步拾取命令):进入后 ① 红十字光标拾取尖灭点(坡面上)、② 方框光标点击坡顶线
        /// → 该台阶 crest 收成楔形 + 上部台阶位置联动,自动落地。命令期间不高亮面。返回 true=已进入(有活动文档)。
        /// </summary>
        bool StartManualPinch(bool propagateUp);

        /// <summary>
        /// 尖灭台阶(煤层·顶/底板双面夹持):crest 的 Z 夹到顶板面、toe 的 Z 夹到底板面,逐顶点按煤层
        /// 包络高(顶板−底板)占参考台阶高之比收口,煤层缺失处尖灭。minFaceFrac&lt;=0 用默认阈值。返回 true=成功。
        /// </summary>
        /// <param name="mode">
        /// 1=SeamClamp:整组台阶都夹进煤层包络(适合"这组本来就全是采煤台阶")。
        /// 2=SeamInterval:先逐级判在不在煤层区间内 —— 顶板之上不动 / 底板之下删掉 /
        ///   区间内才夹持尖灭。含上覆剥离级的整坑用这个,否则剥离级会被压扁进煤厚里。
        /// </param>
        bool ApplySeamPinch(ulong groupKey, ulong roofMeshHandle, ulong floorMeshHandle, double minFaceFrac,
                            byte mode = 1);

        /// <summary>
        /// 采场批量台阶扩帮(向内挖)。返回 PMEP。
        /// 注:Ribbon 的「批量扩坑」按钮走的是 <see cref="BuildSeamPit"/>,不是本方法。
        /// 最终帮平台体系:<paramref name="bermWidth"/> = 每级都留的【保安平台】宽;每隔
        /// <paramref name="haulBermInterval"/> 级把该级平盘改留 <paramref name="haulBermWidth"/> 宽的
        /// 【运输平台】(道路本身仍由 Ramp 系列单独生成)。宽度或间隔 &lt;=0 → 不留运输平台。
        /// <paramref name="stopMode"/>=3(到煤层底板)时用 <paramref name="seamFloorMeshHandle"/> 逐顶点
        /// 收口:底板起伏,触底处收成楔尖(自动尖灭)、未触底处继续往下扩。
        /// </summary>
        byte[] ExpandPitBenches(
            byte   lineRole,
            byte   direction,
            byte   side,
            byte   endCapMode,
            double endExtendDist,
            byte   stopMode,
            double targetElevation,
            ulong  terrainMeshHandle,
            double benchHeight,
            double faceAngleDeg,
            double bermWidth,
            byte   pinchPolicy = 0,    // 尖灭策略:0=接受, 1=截断
            double[]? levelTable = null,   // 逐级剖面表(扁平,每行 7 值:count,H,α,W,mergeCount,dipDeg,rockType);null=统一 H/α/W
            byte   elevationMode = 1,      // 高程处理:0=拍平(水平台阶) 1=随线起伏(台阶跟着境界线的 Z 走,默认)
            double haulBermWidth = 0,      // 运输平台宽 (m);<=0=不留
            int    haulBermInterval = 0,   // 每隔几级留一条运输平台;<=0=不留
            int    haulBermPhase = 0,      // 首条运输平台的级号(1-based);<=0 → 取 interval
            ulong  seamFloorMeshHandle = 0, // 煤层底板三角网句柄(stopMode=3 时生效)
            // 煤岩分参:推进面落到 seamRoofMeshHandle 之下换用煤台阶那套 H/α/平盘(同 ExpandDumpBenches)
            ulong  seamRoofMeshHandle = 0,
            double coalBenchHeight = 0,
            double coalFaceAngleDeg = 0,
            double coalBermWidth = -1);

        /// <summary>
        /// 批量扩坑:选 1 条【闭合】境界线 + 煤层顶/底板三角网,按煤层层位分层放坡。返回 PMEP。
        ///   ① 顶板以上:水平岩台阶,逐级向下,每级按顶板裁剪(只留顶板低于本级的部分),
        ///      断口处坡顶线与坡底线外推交汇 → 尖灭于点;
        ///   ② 顶板↔底板:【唯一一级】倾斜煤台阶,crest 贴顶板 / toe 贴底板,
        ///      水平进尺 = 煤厚/tan(α_煤),煤厚→0 处自然尖灭于点;
        ///   ③ 底板以下不出台阶。
        /// </summary>
        byte[] BuildSeamPit(
            ulong  roofMeshHandle,       // 煤层顶板
            ulong  floorMeshHandle,      // 煤层底板
            double benchHeight,          // 岩台阶 H
            double faceAngleDeg,         // 岩台阶 α
            double bermWidth,            // 岩台阶保安平盘
            double haulBermWidth = 0,    // 运输平盘宽;<=0=不留
            int    haulBermInterval = 0, // 每隔几级一条;<=0=不留
            int    haulBermPhase = 0,    // 首条落在第几级(1-based);<=0=取 interval
            double coalFaceAngleDeg = 65,  // 煤台阶 α
            double coalBenchHeight = 0,    // 煤台阶 H:剩余煤厚够出整台阶,不够出半台阶;<=0 用内核默认
            double coalBermWidth = -1);    // 煤台阶平盘;<0 用内核默认

        /// <summary>
        /// 批量扩坑(顶/底板直接给几何)。语义与 <see cref="BuildSeamPit"/> 完全相同,
        /// 区别只在顶底板的来路:不走场景实体句柄,而是直接吃顶点/三角数组。
        ///
        /// 用途:顶底板存在地质库里(virtual_drill_surface,按煤层名分 roof/floor),
        /// 调用方用 IVirtualDrillService.TryGetGeometry 取出来直接传进来 ——
        /// 于是不再要求用户先把那两张面显示到场景里、再手工点选,库里有哪层就能算哪层。
        ///
        /// verts = 扁平 [x,y,z,...];tris = 三角索引。任一为空 → 返回空数组。
        /// </summary>
        /// <summary>
        /// 批量扩坑(多煤层)。一次连续下降:剥岩 → 经过每一层出倾斜煤台阶 → 继续剥岩,
        /// 【坑底 = 最后一层的底板】,中间各层只是途中经过、不是止点。
        ///
        /// seams 必须【自上而下】排好(顶板标高降序),每项需带齐 Roof/Floor 的顶点与索引;
        /// 只有一层时等价于 <see cref="BuildSeamPitFromGeometry"/>。空表 → 返回空数组。
        ///
        /// minCoalThickness / maxCoalLevels,以及 <see cref="SeamSurfaceGeometry"/> 上的
        /// CoalBenchHeight / CoalFaceAngleDeg / CoalBermWidth / MinCoalThickness(逐层覆盖),
        /// 一律"&lt;=0 = 沿用全局值"。内核对逐层煤参数本来就支持,这条通路补齐之前
        /// 它们传不下去,只能吃内核默认值(最小可采煤厚 1m 是现场口径,必须能改)。
        /// </summary>
        byte[] BuildSeamPitMultiSeam(
            SeamSurfaceGeometry[] seams,
            double benchHeight, double faceAngleDeg, double bermWidth,
            double haulBermWidth = 0, int haulBermInterval = 0, int haulBermPhase = 0,
            double coalFaceAngleDeg = 65, double coalBenchHeight = 0, double coalBermWidth = -1,
            double minCoalThickness = 0, int maxCoalLevels = 0);

        byte[] BuildSeamPitFromGeometry(
            double[] roofVerts,  int[] roofTris,
            double[] floorVerts, int[] floorTris,
            double benchHeight, double faceAngleDeg, double bermWidth,
            double haulBermWidth = 0, int haulBermInterval = 0, int haulBermPhase = 0,
            double coalFaceAngleDeg = 65, double coalBenchHeight = 0, double coalBermWidth = -1);

        /// <summary>排土场批量扩堆(向外堆)。返回 PMED。平台体系/底板止点排土一般不用,传 0 即可。</summary>
        byte[] ExpandDumpBenches(
            byte   lineRole,
            byte   direction,
            byte   side,
            byte   endCapMode,
            double endExtendDist,
            byte   stopMode,
            double targetElevation,
            ulong  terrainMeshHandle,
            double benchHeight,
            double faceAngleDeg,
            double bermWidth,
            byte   pinchPolicy = 0,    // 尖灭策略:0=接受, 1=截断
            double[]? levelTable = null,   // 逐级剖面表(扁平,每行 7 值:count,H,α,W,mergeCount,dipDeg,rockType);null=统一 H/α/W
            byte   elevationMode = 1,      // 高程处理:0=拍平(水平台阶) 1=随线起伏(台阶跟着境界线的 Z 走,默认)
            double haulBermWidth = 0,
            int    haulBermInterval = 0,
            int    haulBermPhase = 0,
            ulong  seamFloorMeshHandle = 0,
            // 煤岩分参:推进面落到 seamRoofMeshHandle 之下换用煤台阶那套 H/α/平盘
            ulong  seamRoofMeshHandle = 0,
            double coalBenchHeight = 0,
            double coalFaceAngleDeg = 0,
            double coalBermWidth = -1);

        /// <summary>
        /// 局部台阶(类型化局部台阶):选 1 条开口线 → 坡面+平盘单台阶。
        /// taper:1=弧长楔形收口/0=均匀。useStopLevel:1=改用"到标高"收口(尖灭线落在 stopLevel)。
        /// side:0=左,1=右。返回 PMEP。
        /// </summary>
        byte[] LocalWedge(double benchHeight, double faceAngleDeg, double bermWidth, byte side, byte taper,
                          byte useStopLevel = 0, double stopLevel = 0.0);

        /// <summary>
        /// 连接台阶线:把首尾相接(端点落在 tolerance 内)的台阶线段按连接关系串成整条,
        /// 串完首尾重合的收成闭合环。新线继承原线 图层/颜色/工程位置绑定(benchGroupKey),
        /// 整步一次 Undo。与通用「连接多段线」的差别就在于保住这些台阶语义。返回 PMBJ。
        /// </summary>
        /// <param name="tolerance">端点重合容差(m);&lt;=0 用 1e-3。</param>
        /// <param name="scopeAll">false=只连当前选集里的线;true=扫模型空间全部台阶线。</param>
        /// <param name="autoClose">串完首尾重合时是否收成闭合环。</param>
        /// <param name="sameLayerOnly">只在同图层内连(图层名含级号与坡顶/坡脚 → 等价于"同级同类才连")。</param>
        byte[] JoinBenchLines(double tolerance, bool scopeAll, bool autoClose, bool sameLayerOnly);

        /// <summary>
        /// 平盘联络斜坡道(非交互·两点):P1 投到坡脚线得 a、P2 投到坡顶线得 d(横穿同一片坡面),
        /// a→d 等宽带;XY 吸线、Z 取台阶线标高。只出三维线(中线+左右边线,无路面 mesh)。
        /// 返回 PMRD(格式同直道)。交互单点版见 <see cref="StartBenchConnectorRampJig"/>。
        /// </summary>
        /// <summary>
        /// 设【台阶线来源白名单】(RS2,见 docs/斜坡道现状数据适配_口径与验收.md)。
        /// <para>现状(实测)台阶线既没有 benchGroupKey、也不落在「坡顶线/坡脚线」图层上,内核收台阶线的老口径
        /// 一条都认不出来。把 <c>BenchLineSourceResolver</c> 按**用户选集**认下的那批句柄灌进来,
        /// 之后 <c>CollectBenchContours</c> 只认这批(层名/组键一律不看)。</para>
        /// <para>null / 空数组 = 清空,回到老口径(设计线档)。返回内核实际记下的条数。</para>
        /// <para><b>进程内状态</b>:每次布线/落地前显式设一次 —— 否则上一次的现状档会悄悄影响下一次,
        /// 而且不报任何错(现状线被删掉之后白名单里全是失效句柄,表现为"突然一条台阶线都找不到")。</para>
        /// </summary>
        int SetBenchContourSource(ulong[]? handles);

        /// <summary>当前白名单条数(0 = 老口径)。供调用方自检本次走的哪一档。</summary>
        int GetBenchContourSourceCount();

        /// <summary>
        /// 【台阶线自检】内核现场重扫模型空间,回传认下的条数 + 一句人话说明:
        /// 走的哪一档(白名单/全图口径)、折线总数(分母)、认下条数(分子),为 0 时还带上**未认下的层名样本**。
        /// <para>用途:凡"要台阶线却一条都没有"的功能(平盘联络道 jig、逐面切链、台阶线连接),
        /// 失败时拿它替掉「无活动文档,或场景里还没有台阶线」那句把四种死法合并成一句的提示 ——
        /// 无活动文档 / 白名单指向的线已不在图上 / 图上没有折线 / 有折线但层名与组键都不符,
        /// 要做的下一步完全不同。</para>
        /// </summary>
        (int Count, string Message) DescribeBenchContourSource();

        /// <summary>
        /// 设【被切面白名单】(RS16)——落地切挖填方时**只切这批面**。
        /// <para>老口径是把模型空间里所有非道路三角网都拿来试切:现状面、实景三维、块体表面、别的设计面,
        /// 只要路面足迹碰到就被切开,而且不报一个字。给了白名单就只切点名的那几张。</para>
        /// <para>null / 空数组 = 清空,回老口径。返回内核实际记下的条数。</para>
        /// </summary>
        int SetCarveTargets(ulong[]? handles);

        /// <summary>
        /// 取上一次切坡面的三分统计(RS18):<b>真切了 / 足迹没碰到 / 几何核失败</b>。
        /// 老代码这三种都是静默 continue —— "路落地了但一片坡面都没切开"在界面上看不出来。
        /// </summary>
        (int Touched, int Missed, int Failed) GetLastCarveStats();

        /// <summary>
        /// 设【白名单为空时】的兜底口径(RS16)。<c>0</c>=切模型空间所有非道路三角网(老口径);
        /// <c>1</c>=白名单为空就一片都不切,只出路面。
        /// <para>为什么是开关而不是直接改默认:设计线落地流程本来就靠"切全部"把路切进各片设计坡面,
        /// 全局改默认会一次打断那条链。所以由各入口自己表态,并把表的态回显给用户。</para>
        /// </summary>
        void SetCarveFallback(int mode);

        /// <summary>
        /// 平盘联络道交互式 jig(单点 O → 斜插坡面的矩形路面)。对话框配好 类型 + 路宽 + 纵坡% + 镜像 后进入:
        /// 移动鼠标到坡面 = O → 按该处真实台阶段(坡顶/坡脚线、台阶高、坡面宽)+ 纵坡自动解出斜矩形路面
        /// (A 顶上平盘、C 落坡顶、B 落坡脚、D 顶下平盘,C-B=零线·挖填分界),实时预览(移动即滑动);
        /// 左键点一下即落地(一步 Undo),Esc/右键取消。mirror=true 取另一镜像方向。全程 XY 吸线、Z 取台阶线标高。
        /// 返回 true=已进入 jig;false=无活动文档 / 无台阶线 / 非交互宿主。
        /// </summary>
        bool StartBenchConnectorRampJig(byte slopeType, double roadWidth, double gradePct, bool mirror);

        /// <summary>
        /// 插入螺旋运输道路 — 固定半径圆螺旋,绕 turns 圈下降。返回 PMRD layout。
        /// </summary>
        /// <param name="ccw">0=CW(顺时针), 1=CCW(逆时针)</param>
        byte[] InsertRampSpiral(
            byte   slopeType,
            double centerX, double centerY, double startZ,
            double radius, double startAngleDeg, double turns,
            byte   ccw, double gradePct, double roadWidth);

        /// <summary>
        /// 插入折返(回头曲线)运输道路 — 外凸折返台:直线腿 + 180° 回头弧往返,逐腿下降。返回 PMRD layout。
        /// </summary>
        /// <param name="azimuthDeg">第一条腿前进方位,0=+X,90=+Y,逆时针正</param>
        /// <param name="turnSide">+1=回头弧向左(逆时针)甩出, -1=向右(顺时针)甩出</param>
        /// <param name="legs">直线腿数(>=2)</param>
        /// <param name="curveGradePct">回头弧纵坡 %(减坡,通常 &lt; gradePct)</param>
        /// <param name="curveWiden">弯道加宽:弧段额外总宽 m(&lt;=0 自动估)</param>
        /// <param name="superElevPct">回头弧超高 %(外侧抬高;&lt;=0 不设)</param>
        /// <param name="bermHeight">安全挡墙/路埂高 m(弧+过渡段外侧路肩;&lt;=0 不设)</param>
        byte[] InsertRampSwitchback(
            byte   slopeType,
            double startX, double startY, double startZ,
            double azimuthDeg, int turnSide, int legs,
            double legLength, double gradePct, double curveGradePct,
            double radius, double roadWidth, double curveWiden, double superElevPct,
            double bermHeight);

        /// <summary>
        /// 交互画道路中线落地:把屏幕取点累积的世界点(每点已吸坡面、Z 精确;扁平 [x,y,z,...])
        /// → Z 线性匀坡(逐渐爬升)→ 放样路面带 → 自动切进台阶坡面(贴合)。返回 PMRD。
        /// </summary>
        /// <param name="keepZ">
        /// 非 0 = <b>贴面模式</b>:传进来的 Z 原样用,内核不再拧回头弧、不再把 Z 拉成首末匀坡。
        /// 供调用方先用 <c>RoadSurfaceProfiler</c> 逐站采现状面 + 按 i_max 整平(RS13–RS15)后再放样。
        /// 0 = 老行为(第2→3点自动成回头弧 + 首末线性匀坡),没指定现状面时用。
        /// </param>
        /// <param name="stationWidths">
        /// 逐站路面宽 m(= 基宽 + 弯道加宽),长度须 == 中线点数;null = 等宽。
        /// 由调用方按中线曲率算好(<c>RoadCrossSection.ComputeAlong</c>)。长度对不上一律按 null 处理,
        /// <b>不截断、不补齐</b> —— 错位套用会把加宽加到直线段上,比不加宽更难查。
        /// </param>
        /// <param name="stationSuperPct">
        /// 逐站超高横坡 %,长度须 == 中线点数;null = 不设超高。外侧是哪一侧由内核按中线转向自定,
        /// 调用方不传方向(传了就会和几何打架)。
        /// </param>
        /// <param name="cutSlopeDeg">挖方边坡角(°,自路缘向外向上放到与地形相交)。≤0 = 用内核内置默认。</param>
        /// <param name="fillSlopeDeg">填方边坡角(°,自路缘向外向下放到与地形相交)。≤0 = 用内核内置默认。</param>
        byte[] CreateRoadCenterline(byte slopeType, double[] xyzFlat, double roadWidth, byte keepZ = 0,
            double[]? stationWidths = null, double[]? stationSuperPct = null,
            double cutSlopeDeg = 0, double fillSlopeDeg = 0);

        /// <summary>
        /// 直线坑线·逐面切链:给一串 O 点(扁平 [x,y,z,...],各落在不同台阶坡面上 = 布线中线顶点),
        /// 逐 O 调几何核切进【该片坡面】(L=ΔH/i 一个台阶高,挖填方边坡 + 路面 deck),链成从坑底到地表的整条坑线。
        /// 复用平盘联络道几何核(BuildBenchFaceRamp);放不下/点不到坡面的段自动跳过。返回 PMRD(各段合并)。
        /// </summary>
        byte[] InsertChainedFaceRamps(byte slopeType, double[] oPtsXyzFlat, double roadWidth, double gradePct, byte mirror);

        // ── 工作线（可交互推进方向对象 AcDbWorkLine） ──

        /// <summary>
        /// 选集中恰好 1 条 polyline → 转化为「工作线」(替换原线,整步可 Undo),选中后拖箭头即改推进方向。
        /// 工作线为独立可交互对象,给场景中的工作线推进方式提供方向。
        /// </summary>
        /// <param name="mode">推进方式:0=平行推进, 2=扇形推进 (1=已取消的 L 型,内核归一为平行)</param>
        /// <param name="dirMode">方向模式:0=统一方向(一个控制箭头), 1=逐段独立(每段一个箭头)</param>
        /// <param name="arrowLen">箭头/夹点显示长度;&lt;=0 用默认(基线包围盒对角线的 ~6%)。</param>
        /// 返回 true=已转化;false=选集 ≠ 恰好 1 条多段线 / 无活动文档。
        bool CreateWorkLineFromSelection(byte mode, byte dirMode, double arrowLen = 0.0);

        /// <summary>
        /// 手工绘制的点串 → 直接建「工作线」(不依赖选集,不需要预先有多段线)。
        /// 「创建工作线」按钮走这条路径:先选类型(直线/扇形),再在视口手工点两点定直线基线。
        /// 新线落「工作线」图层(缺失自动建),一步 Undo,建后自动选中显示箭头/夹点。
        /// </summary>
        /// <param name="xyz">扁平点串 [x,y,z,...],至少 2 点;首末点 XY 重合(定不出方向)会被拒。</param>
        /// <param name="mode">推进方式:0=直线(平行推进), 2=扇形推进。</param>
        /// <param name="dirMode">方向模式:0=统一方向, 1=逐段独立(两点基线只有一段,恒等价于统一)。</param>
        /// <param name="arrowLen">箭头/夹点显示长度;&lt;=0 用默认(基线包围盒对角线的 ~6%)。</param>
        /// 返回 true=已创建;false=点不足 2 / 退化 / 无活动文档。
        bool CreateWorkLineFromPoints(double[] xyz, byte mode, byte dirMode = 0, double arrowLen = 0.0);

        /// <summary>
        /// 选集中 ≥2 条工作线 → 组合为「工作线组」(替换原线,整步可 Undo,选中新组)。
        /// 各成员保留自己的推进方式与控制句柄。**接缝走软连接**:端点不要求重合,组按最近端点
        /// 把各成员串成一条链,缺口处自动现算「软连接段」桥接(点划虚线 + 从属箭头;不是实几何、
        /// 不出夹点,只做连续性表达与逐段方向汇总,拖成员端点时桥自己伸缩)。
        /// 缺口 ≤ 接缝容差的算焊死接缝,沿用夹点硬联动(拖一个,重合的一起动)。
        /// 返回 true=已组合并填 memberCount 与 softLinkCount(需架桥的缺口数,0=各段本就首尾相接);
        /// false=选集 &lt;2 条工作线 / 无活动文档。
        /// </summary>
        bool CreateWorkLineGroupFromSelection(out int memberCount, out int softLinkCount);

        /// <summary>改选中工作线的推进方式(0=平行 2=扇形;1=已取消的 L 型,内核归一为平行)。需恰好选中 1 条工作线。</summary>
        bool SetWorkLineAdvanceMode(byte mode);

        /// <summary>改选中工作线的方向模式(0=统一 1=逐段独立)。需恰好选中 1 条工作线。</summary>
        bool SetWorkLineDirMode(byte dirMode);

        // 扇形旋向无专用 setter/getter:工作线只表征方向、不设距离,旋向由视口「旋转手柄」夹点拖拽实时设定,回转中心同走夹点。

        /// <summary>查询选中工作线状态:恰好 1 条工作线时返回 true,填 mode/dirMode/closed/段数。</summary>
        bool TryGetSelectedSingleWorkLineMeta(out byte mode, out byte dirMode, out bool closed, out int segCount);

        /// <summary>选中唯一工作线的基线长度(m):成功返回 true 并填 lengthMeters。供「驱动开采模板」自动带出工作线长 L。</summary>
        bool TryGetSelectedWorkLineLength(out double lengthMeters);

        /// <summary>
        /// 导出选中工作线几何(基线点 + 逐段方向)二进制('PMWL';空选返回空数组)。
        /// 由 MineAssLib 的 WorkLineGeometryReader 解析,供「驱动开采模板」引擎按工作线方向投影/构斜面。
        /// </summary>
        byte[] GetSelectedWorkLineGeometry();

        /// <summary>
        /// 按 handle 直接导出工作线几何('PMWL';不依赖选集,隐藏实体也能读;找不到/非工作线返回空数组)。
        /// 供非模态对话框在确认时按 handle 重读活实体最新推进方向,避免"加载时缓存滞后于后来的箭头调整"。
        /// </summary>
        byte[] GetWorkLineGeometryByHandle(ulong handle);

        // ── 可采区域边界 overlay（确定可采区域圈画 / 自动识别采场排土场结果） ──

        /// <summary>
        /// 用 overlay 显示一组可采区域边界环（每个 double[] = 一环的扁平 [x,y,z,...]）。
        /// rgbPerRing 为各环颜色 0xRRGGBB（与 rings 等长，空/越界用默认绿）——件一自动识别按
        /// 采橙/外蓝/内青着色，手动圈画用绿。先清同组再整批画并请求重绘；点数 &lt; 2 的环自动跳过。
        /// 非交互宿主/内核未导出时静默忽略。
        /// </summary>
        void ShowMineableAreaOverlay(System.Collections.Generic.IReadOnlyList<double[]> rings,
            System.Collections.Generic.IReadOnlyList<uint>? rgbPerRing = null);

        /// <summary>清除可采区域边界 overlay 并请求重绘。幂等。</summary>
        void ClearMineableAreaOverlay();

        // ── 扩帮方向箭头 overlay（「批量台阶扩帮 / 批量扩坑」对话框） ──

        /// <summary>
        /// 用 overlay 画一组扩帮方向箭头折线（每个 double[] = 一段折线的扁平 [x,y,z,...]，
        /// 箭杆与倒钩各是一段）。先清同组再整批画并请求重绘；点数 &lt; 2 的段自动跳过。
        /// 走 overlay 而非建实体：切换侧向要反复重画，建实体会污染 Undo 栈。
        /// rgb 为 0xRRGGBB。非交互宿主/内核未导出时静默忽略。
        /// </summary>
        void ShowExpandDirectionOverlay(System.Collections.Generic.IReadOnlyList<double[]> polylines,
            uint rgb = 0xFFAA00);

        /// <summary>清除扩帮方向箭头 overlay 并请求重绘。幂等。</summary>
        void ClearExpandDirectionOverlay();

        // ── 动态 overlay 批量通道（三维时序模拟的车流动点 / 流向箭头，逐帧重画）──────────
        //
        // 上面两组 overlay 是「摆好了看」的静态图；本组是**动画通道**，差别只有一个但很致命：
        // **一次调用换掉一整组**。既有口是「一条环一次 P/Invoke」——画 200 个动点就是
        // 每帧 200 次跨托管边界，顶穿帧预算；本组的帧代价与图元数量无关。
        //
        // 三条纪律（都不是约定，是构造上做死的）：
        //  ① 组名由调用方给，内核侧统一加 "X:" 前缀 ⇒ **撞不上**内置组
        //     （MINEABLE_AREA / EXPAND_DIR / RAMP_PREVIEW …），也因此够不到它们。
        //     所以本组 API 与 SimOverlayComposer 那条「谁后调谁擦掉别人」的坑天然无关，
        //     各动画舞台各占一个组名即可，不需要仲裁者。
        //  ② 只画 overlay，**不建实体**：不入库、不占 Undo 栈、不进 mesh 缓存 ——
        //     这正是逐帧动画唯一走得通的路（实体路没有变换矩阵，显隐路不 MarkRenderDirty）。
        //  ③ argb = 0xAARRGGBB，**alpha 不做「0 视为不透明」的补正**：0 就是全透明。
        //     补正过一次，「看不见」到底是没画还是透明就再也说不清。

        /// <summary>
        /// 整组替换线段（流向箭头 / 高亮路径等）。<paramref name="xyzFlat"/> 每段 6 个 double
        /// （x0,y0,z0,x1,y1,z1）；<paramref name="argbPerSeg"/> 逐段 0xAARRGGBB，可空取白不透明。
        /// <para><paramref name="segCount"/> 显式给 —— 数组允许比实际用量长（动画侧按上限一次分配、
        /// 逐帧只填前 n 段，避免每帧新建数组）。超出数组能供的段数会被夹住，不静默按 0 处理。</para>
        /// <para><paramref name="segCount"/> &lt;= 0 = 抹掉该组（「本帧这组什么都不画」的正规写法）。</para>
        /// 非交互宿主 / 内核未导出时返回 false（不抛）。
        /// </summary>
        bool SetOverlayLines(string group, double[]? xyzFlat, uint[]? argbPerSeg, int segCount);

        /// <summary>
        /// 整组替换点标记（车流动点 / 设备位置等）。<paramref name="xyzFlat"/> 每点 3 个 double；
        /// <paramref name="argbPerPt"/>/<paramref name="pixelSizePerPt"/>/<paramref name="stylePerPt"/>
        /// 逐点，均可空取缺省（白 / 5px / 实心方块）。
        /// <para>style = 0×/1+/2□/3○/4■；尺寸是**屏幕像素**（缩放时大小不变），不是世界尺寸。</para>
        /// <para><paramref name="count"/> 语义同 <see cref="SetOverlayLines"/>。</para>
        /// 非交互宿主 / 内核未导出时返回 false（不抛）。
        /// </summary>
        bool SetOverlayMarkers(string group, double[]? xyzFlat, uint[]? argbPerPt,
                               float[]? pixelSizePerPt, byte[]? stylePerPt, int count);

        /// <summary>
        /// 整组替换**世界锚点文字**（设备铭牌等跟着目标跑的标签）。始终朝屏幕、
        /// 按世界字高投影出像素字号（随缩放变大变小，&lt;6px 省绘、&gt;72px 钳住）。
        /// <para><paramref name="texts"/> 与 <paramref name="xyzFlat"/> 的第 i 个点一一对应；
        /// 空串占位不画（这样中间有空串也不会整体错位）。</para>
        /// <para>★ 与图上文字实体（billboard）是两条路：那条随场景刷新整表重建、逐帧改不了；
        /// 本条走 overlay 组，能逐帧整组替换 —— 动画铭牌只能走这条。</para>
        /// </summary>
        bool SetOverlayLabels(string group, double[]? xyzFlat,
                              System.Collections.Generic.IReadOnlyList<string>? texts,
                              uint[]? argbPerPt, float[]? heightMPerPt,
                              byte[]? hAlignPerPt, byte[]? vAlignPerPt, int count);

        /// <summary>抹掉一个动态 overlay 组（点 + 线 + 文字一起）并请求重绘。幂等。</summary>
        void ClearOverlayGroup(string group);

        /// <summary>
        /// 请求重绘一帧。<b>动画通道必须由调用方在一帧末尾显式调一次</b> ——
        /// Set* 内部刻意不 render（一帧推几组就 render 几次是纯浪费）。
        /// </summary>
        void RequestOverlayRender();

        /// <summary>
        /// 读取项目级已圈画/识别的全部作业区域（「确定可采区域 / 区域识别」存的 MineableRegion），
        /// 转成轻量 <see cref="MineableRegionInfo"/>（Name/Category + 扁平 XY 环）返回。
        /// 供 PointCloudLib 等不引用 GeoDataBase 的模块把坡顶/坡底线裁剪到区域「内/外」。
        /// GeoDataBase 未就绪 / 无区域 / 非交互宿主 → 返回空列表（不抛）。
        /// </summary>
        System.Collections.Generic.IReadOnlyList<MineableRegionInfo> GetMineableRegions();

        /// <summary>
        /// 进入选区笔刷编辑（PS 式，对【选中的一个区域】当选区涂改）：视口左键按住涂改——
        /// 按住 Alt=把笔刷圆覆盖范围【并入】该区域(扩大)，普通=【移出】(缩小)；笔刷圆圈跟随光标、
        /// 边界实时更新。targetRing=被编辑区域，contextRings=其它区域(只显示背景，一一对应 contextColors)。
        /// 完成 / 取消由 <see cref="EndRegionBrushEdit"/> 触发：onCommit 收回改后的选区环（擦空回空数组→删该区域），
        /// onCancel 弃改。返回 false = 非交互宿主 / 目标环无效。
        /// </summary>
        bool BeginRegionBrushEdit(
            double[] targetRing, uint targetColor,
            System.Collections.Generic.IReadOnlyList<double[]> contextRings,
            System.Collections.Generic.IReadOnlyList<uint> contextColors,
            System.Action<double[]> onCommit,
            System.Action onCancel);

        /// <summary>结束区域笔刷编辑：commit=true 回调改动后的环并落地；false 弃改。幂等。</summary>
        void EndRegionBrushEdit(bool commit);

        /// <summary>设置区域笔刷半径(屏幕 px，面板 +/- 调)。返回钳后的实际值（无交互宿主返回 0）。</summary>
        int SetRegionBrushRadiusPx(int px);
    }

    /// <summary>
    /// 一幅采掘带要贴的地质面（沿底板开采 / 煤台阶）。扁平世界坐标：Verts = [x,y,z,...]，Tris = 三角索引。
    ///
    /// 为什么是几何数组而不是实体 handle：这几张面存在库里（<c>virtual_drill_surface.geometry_b64</c>），
    /// 不是图上的实体；为了建个煤台阶先把它们导进图纸纯属污染。
    /// 某一项留 null = 该面不参与（例如没有现状面就只贴顶/底板）。
    /// </summary>
    public sealed class SeamSurfaceGeometry
    {
        /// <summary>煤层顶板：顶盖跟它走。</summary>
        public double[]? RoofVerts; public int[]? RoofTris;

        /// <summary>煤层底板：底盖跟它走。</summary>
        public double[]? FloorVerts; public int[]? FloorTris;

        /// <summary>现状面：顶盖取 min(顶板, 现状面) —— 现状以上的煤已经剥掉了，不该再算进煤台阶体。</summary>
        public double[]? ClipVerts; public int[]? ClipTris;

        /// <summary>这幅贴的是哪层煤（日志 / 报表溯源）。</summary>
        public string SeamName = "";

        // ── 这一层【自己的】煤台阶参数（批量扩坑用；其它接口忽略）────────────────
        // 各层煤的厚度、产状、可采性都不一样，煤台阶参数本来就该逐层给。
        // 一律「&lt;=0（平盘宽 &lt;0）= 沿用全局那套」，全不填 = 行为与以前完全一致。
        public double CoalBenchHeight;          // 该层煤台阶高 H
        public double CoalFaceAngleDeg;         // 该层煤坡面角 α
        public double CoalBermWidth = -1;       // 该层煤平盘宽
        public double MinCoalThickness;         // 该层最小可采煤厚

        /// <summary>顶底板至少有一张才有意义；两张都空则等价于不传。</summary>
        public bool HasAny => (RoofTris is { Length: > 0 }) || (FloorTris is { Length: > 0 });
    }
}
