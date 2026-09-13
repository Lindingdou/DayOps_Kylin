// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IViewCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 视图能力域：视图、工具、命令执行的高层入口。
    ///
    /// 终态设计（P-2）：
    ///   - 替代 IEngineService 中 80 方法里 ~30 个 Start*Tool 方法
    ///   - 用泛化 StartTool(name) + ExecuteCommand(line) 取代逐工具方法
    ///   - 工具/命令的有效名集合由 GetAvailableToolNames() / GetAvailableCommandNames() 列出
    ///   - 第三方插件不需要知道每个工具的具体 Start* 方法签名
    /// </summary>
    public interface IViewCapability
    {
        /// <summary>启动指定工具。返回 true 表示成功，false 表示工具不存在或当前不能启动。</summary>
        bool StartTool(string toolName);

        /// <summary>列出当前可用工具名（包括内置 + 已注册插件提供的）。</summary>
        string[] GetAvailableToolNames();

        /// <summary>执行命令行（与命令栏 Enter 等价）。</summary>
        bool ExecuteCommand(string commandLine);

        /// <summary>
        /// 取消当前活跃的交互命令（jig / 状态机编辑命令 / 工具），让视口回到空闲态并清空选择集
        /// —— 等价于用户按下 Esc（复用引擎同一条取消通路，含焦点回收）。已是空闲态时为无副作用操作。
        ///
        /// 用途：模块里"读当前选择集、一步产出结果"的【终结型】命令（如 MeshEditLib「创建三角网」）
        /// 收尾时调用，保证绘制结束后视口立即可用 —— 右键菜单 / 后续命令不再被"命令进行中"状态挡住，
        /// 用户不必先按 Esc。⚠ 交互式命令（点按钮后才开始取点 / 拖拽的那种）【不要】调用，否则会把
        /// 刚启动的命令一并取消。
        /// </summary>
        void CancelActiveCommand();

        // ── 视图状态（与 IEngineService 同款，但作为受控 API 重新暴露）────────
        bool IsOrthoEnabled { get; }
        void SetOrthoEnabled(bool enabled);

        bool IsSnapEnabled { get; }
        void SetSnapEnabled(bool enabled);

        bool Is3DViewEnabled { get; }
        void Set3DViewEnabled(bool enabled);

        bool IsFillModeEnabled { get; }
        void SetFillModeEnabled(bool enabled);

        // ── 三角网/网格着色 (#5 TIN着色)：全局着色模式，GPU 逐像素，零网格重建 ──
        // 0=平面 1=平滑 10=等高线 11=坡度 12=坡向 13=剖切 14=高程(属性色带)。
        /// <summary>当前全局着色模式。</summary>
        int GetShadingMode();
        /// <summary>设置全局着色模式（与 AcGi::ShadingMode 数值对齐）。改后需 RequestRender。</summary>
        void SetShadingMode(int mode);
        /// <summary>等高线间距（世界单位，&lt;=0 用默认 5）；仅 mode=10 有效。</summary>
        void SetContourSpacing(float spacing);
        /// <summary>高程/属性色带区间 [minV,maxV]；mode=14 按 (z-min)/(max-min) 采样色带。</summary>
        void SetValueRange(float minV, float maxV);
        /// <summary>
        /// 设置色带 LUT（256×4 RGBA8 = 1024 字节）。被高程(14)/粗糙度(16)/曲率(17) 等
        /// "属性分级"着色模式按归一化值采样。改后需 RequestRender。
        /// </summary>
        void SetColormap(byte[] rgba);
        /// <summary>请求重绘一帧（改着色/参数后调用使其立即生效）。</summary>
        void RequestRender();

        // ── 正射影像贴图（着色模式 5）：把带地理配准的影像按世界 XY 贴到 2.5D 三角网 ──
        /// <summary>
        /// 上传正射影像（全局唯一一张，所有着色模式 5 的三角面共用）。
        /// bgra 为 BGRA8 像素（width*height*4，宿主解码后传入，须自行按 GPU 上限降采样）；
        /// minX/minY/maxX/maxY 为影像四至的**绝对世界坐标**（米，与实体同坐标系，
        /// 由 GeoTIFF 的 ModelTiepoint + ModelPixelScale 或 .tfw 换算）。
        /// 影像为内存态、不随工程持久化；逐面的模式 5 由三角网自身保存。
        /// 返回 false 表示参数非法（像素为空 / 四至退化）或引擎未就绪。改后需 <see cref="RequestRender"/>。
        /// </summary>
        bool SetOrthophoto(byte[] bgra, int width, int height,
                           double minX, double minY, double maxX, double maxY);

        /// <summary>清除已加载的正射影像（模式 5 的面退回素色光照）。</summary>
        void ClearOrthophoto();

        /// <summary>当前是否已加载正射影像。</summary>
        bool HasOrthophoto();

        /// <summary>
        /// 本机显卡单张纹理的边长硬上限（D3D 功能级别决定：11_x = 16384，10_x = 8192）。
        /// 影像超过它必须先降采样 —— 这是 GPU 的硬限制，不是软件设的闸。
        /// 引擎未就绪时返回一个保守的兜底值。
        /// </summary>
        int GetMaxOrthophotoDimension();

        // ── 实时曲面坐标读出（三角网 hover）──────────────────────────────
        // 开启后，鼠标在三维视口内移动时，宿主对光标位置做射线求交所有可见三角网
        // (AcDbTriangleMesh)，在光标旁浮动气泡里实时显示落点的真实三维坐标(X/Y/Z)。
        // 与状态栏 ScreenToWorld(投影平面Z)不同，这里给的是曲面上的真实高程。
        /// <summary>实时曲面坐标读出是否已开启。</summary>
        bool IsSurfaceCoordinateReadoutEnabled { get; }
        /// <summary>开/关实时曲面坐标读出（光标旁气泡显示鼠标在三角网上的真实 XYZ）。</summary>
        void SetSurfaceCoordinateReadoutEnabled(bool enabled);
    }
}
