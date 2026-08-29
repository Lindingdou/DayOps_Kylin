# PitMine3D → 麒麟/Linux 完整移植任务队列

> **执行方式**：`/loop` 逐一重构。每轮取第一个未完成任务，完整实现 + 验证 + 提交，再取下一个。
> **源仓库**：`C:\Users\cFore\coder\PitMine3D`（Windows 版：WPF + D3D11 + MSVC）
> **目标/骨架**：`C:\Users\cFore\coder\DayOps_Kylin`（麒麟版：Avalonia + OpenGL + CMake）
> **配套总览页**：Artifact「PitMine3D 麒麟移植清单」（可视化 dashboard，人看用）
> **数据库国产化**：目标 **达梦 DM8**（Oracle 兼容）。现状 SQLite + Dapper（无 EF Core），经 `ISqlService` / `ConnectionFactory` 抽象层切换——切换点集中。详见 P0-5 / P5-1 / P6-7~9。

标记：`(S)` 小 · `(M)` 中 · `(L)` 大 ｜ 🔴 雷区/高隐藏成本 ｜ ⛳ 需人拍板 ｜ 🏁 里程碑

---

## 给 /loop 的执行规则（每轮必读）

1. 取第一个状态为 `[ ]` 的任务作为本轮目标。**严格按文件顺序，不跳序**——顺序即依赖序。
2. 任务标 **⛳** 的：停下向用户提问，不要擅自决定。
3. 任务大到一轮做不完：先在其下用两空格缩进的 `- [ ]` **拆出子任务**，本轮只做第一个子任务，其余留给后续轮。
4. **完成判据（Done）全部满足前不得勾选 `[x]`**。做不到就如实说明卡点，不要假装完成。
5. 每完成一项：确保相关**构建/测试通过** → `git` 提交，消息格式 `migrate(<ID>): <标题>`。
6. **原生腿（P1/P2/P3）与托管腿（P4/P5）可并行**。若开两条 loop，各自取本腿第一个未完成项。交汇点是 P3。
7. 完成后回写本文件（勾选、必要时追加新发现的子任务）。本文件是唯一进度真相。

**规模基线（实测自源仓库，非估算）**：314 个 `PitMine_*` C API · 318 个 P/Invoke · 290K 行 C#（+41K XAML）· 70 个 MSVC 原生工程 · xllAcGi 渲染器 ~12K 行 · 8 个 HLSL / 18 入口 · DirectXMath 波及 233 文件/7 库 · 187 个 code-behind · ~167 对话框/窗口 · 20 个 `net8.0-windows` 工程 · 9 业务模块（5 实装 / 4 骨架）。

---

## P0 · 立项与基线（共享）

- [ ] **P0-1 (S) 麒麟测试机 + 工具链就位**
  - 范围：麒麟 SP10 x64 + arm64 各一台（含独显与无 GPU 两类）；装 .NET 8 SDK、GCC/Clang（C++20）、CMake ≥ 3.20
  - Done：`dotnet --info` 与 `g++ -std=c++20` 均可用；骨架 `dotnet run` 在真机跑起来（软渲染兜底 `LIBGL_ALWAYS_SOFTWARE=1`）
  - 依赖：—
- [ ] **P0-2 (S) ⛳ 决策：数学库路线 DirectXMath → GLM**
  - 范围：定「换 GLM」还是「移植 DirectXMath-Linux」。影响 233 文件/7 库、几何↔渲染器数值约定（行列主序、handedness）
  - Done：路线写入本文件 P2-4；给出类型映射表（XMFLOAT3→vec3 等）
  - 依赖：—
- [ ] **P0-3 (S) ⛳ 决策：视口宿主策略**
  - 范围：定内核 GL 如何进 Avalonia——(a) `OpenGlControlBase` 直渲（骨架路线，无 airspace）｜(b) 离屏 FBO + 共享纹理 ｜(c) X11/EGL 子窗口
  - Done：策略确定；同时确定 `PitMine_Initialize/CreateView` 的 HWND 参在 Linux 用什么替代（见 P3-3）
  - 依赖：—
- [ ] **P0-4 (M) CI 最小闭环**
  - 范围：Linux 上一条流水线同时 build 原生（CMake）+ 托管（`dotnet publish -r linux-x64`）
  - Done：CI 绿；产出可运行的骨架自包含包
  - 依赖：P0-1
- [ ] **P0-5 (S) ⛳ 决策：达梦落地方式（库已定 = 达梦 DM8）**
  - 范围：DM8 已定，还需拍板 ① **部署模型**：每机装本地 DM 实例 vs 连中心 DM 服务器 ② **合规范围**：全量切 DM，还是保留 SQLite 作本地缓存（多 provider）③ **DM8 兼容模式**：Oracle / MySQL / SQLServer——选 **MySQL 模式**可显著减少方言翻译 ④ **授权采购** + **龙芯 .NET 驱动**可用性向厂商确认
  - Done：①②③ 写入本文件；确认 `DmProvider`（.NET 8）在 x64/arm64(/龙芯) 到位
  - 依赖：—

## P1 · 原生内核 · 跨平台构建（原生腿）

> 目标：70 个内核库在 Linux 编译链接通过（**暂用渲染桩**，GL 实现留到 P2）。

- [ ] **P1-1 (M) 顶层 CMake + toolchain 文件**
  - 范围：根 `CMakeLists.txt` + `cmake/toolchain-{x64,arm64,loongarch64}.cmake`；定义各库 target 与依赖图（AcGi→AcDb；AcEd→AcGi/AcDb）
  - Done：`cmake -B build` 配置通过（子库可暂空）
  - 依赖：P0-1
- [ ] **P1-2 (M) CMake：xllAcRx（基础运行时）+ Windows.h/__declspec 隔离**
  - 范围：`Kernel/xllAcRx/`；`Windows.h`（全仓 16 文件）用平台宏隔离；`__declspec`（58 文件）→ `__attribute__((visibility("default")))` + `-fvisibility=hidden`
  - Done：`libxllAcRx.a` GCC 编译通过，无 Windows.h 依赖
  - 依赖：P1-1
- [ ] **P1-3 (M) CMake：xllAcGe（几何）**
  - 范围：`Kernel/xllAcGe/`（此处含 13 处 DirectXMath，先保留，P2-4 统一换）
  - Done：`libxllAcGe.a` 编译通过
  - 依赖：P1-2
- [ ] **P1-4 (M) CMake：xllAcDb（数据库/实体/符号表）**
  - 范围：`Kernel/xllAcDb/`
  - Done：`libxllAcDb.a` 编译通过
  - 依赖：P1-3
- [ ] **P1-5 (M) CMake：LasLib + MeshLib + PitDesignLib**
  - 范围：`Kernel/{LasLib,MeshLib,PitDesignLib}/`
  - Done：三个 `.a` 编译通过（LasLib 的 mmap 见 P1-6）
  - 依赖：P1-2
- [ ] **P1-6 (S) LasLib 文件映射 POSIX 化**
  - 范围：`Kernel/LasLib/src/mmap_file.cpp`：`CreateFileMapping`/`MapViewOfFile` → POSIX `mmap`；`win_path.hpp` 路径分隔符
  - Done：LAS 读写单测在 Linux 通过
  - 依赖：P1-5
- [ ] **P1-7 (S) 原生插件加载器移植**
  - 范围：`Kernel/xllAcRx/src/Plugin/PluginManager.cpp`：`LoadLibrary`/`GetProcAddress`/`FreeLibrary` → `dlopen`/`dlsym`/`dlclose`
  - Done：`.so` 插件在 Linux 可加载
  - 依赖：P1-2
- [ ] **P1-8 (M) SSE 内联的架构分路**
  - 范围：`_mm_*`/`__m128`（14 文件）；x64 保留 SSE，arm64/loongarch 加 NEON 或标量兜底（可用 `simde` 或 `#ifdef` 分支）
  - Done：arm64 上编译通过且数值一致（对拍 x64）
  - 依赖：P1-3
- [ ] **P1-9 (S) 清理 MSVC 专属 pragma**
  - 范围：`#pragma detect_mismatch`（`Directory.Build.props` 的 LNK2038 防护）等 → 跨平台等价或移除
  - Done：GCC/Clang 无 pragma 警告
  - 依赖：P1-2
- [ ] **P1-10 (M) CMake：xllAcGi 以渲染桩编译通过**
  - 范围：`Kernel/xllAcGi/`；D3D11 实现用 `#ifdef` 隔离，GL 侧先留空桩，保证链接
  - Done：`libxllAcGi.a` 编译链接通过（渲染为 no-op）
  - 依赖：P1-4
- [ ] **P1-11 🏁 里程碑：全部内核库在 Linux 编译链接通过（headless）**

## P2 · 渲染层 · D3D11 → OpenGL + Skia（原生腿）

- [ ] **P2-1 (L) GL 设备层：上下文/交换链/上下文 + FBO**
  - 范围：`Graphics/D3D11/{Device,SwapChain,RenderTarget}.cpp`；重建 26 个 `ID3D11*`/`IDXGI*` 接口与 57 个 `D3D11_*` 结构到 GL 对象/状态/FBO
  - Done：能创建 GL context、清屏、present
  - 依赖：P1-11、P0-3
- [ ] **P2-2 (S) 运行时着色器加载器**
  - 范围：`Graphics/D3D11/Shader.cpp`：`D3DCompileFromFile` → `glCompileShader`/`glLinkProgram`（本就运行时编译，无离线 fxc）
  - Done：能加载并链接一个 GLSL 程序
  - 依赖：P2-1
- [ ] **P2-3 (M) 8 个 HLSL → GLSL**
  - 范围：`Kernel/xllAcGi/shader/*.hlsl`（Line/ThickLine/Solid/Lit/Mesh/PointCloud/Voxel/Oblique，18 入口含 2 GS）；b/s/t 寄存器 → UBO/sampler/texture 绑定；行主序 → 列主序
  - Done：8 个程序全部编译链接通过（可用离屏三角形逐个验证）
  - 依赖：P2-2
- [ ] **P2-4 (L) 🔴 DirectXMath → GLM（跨 7 库）**
  - 范围：2936 处 / 233 文件；**按库分子任务推进**：AcGe(13) → AcDb(41) → AcGi(47) → AcEd(105) → PitDesignLib(23) → 其余。按 P0-2 决策的映射表
  - Done：每库替换后编译通过 + 几何单测数值一致；全部完成后勾选
  - 依赖：P2-1、P0-2
- [ ] **P2-5 (M) 渲染器：Line / ThickLine / Solid**
  - 范围：对应 3 个 VS/PS；输入布局 → VAO；draw → `glDrawArrays/Elements`
  - Done：线/粗线/实体在 GL 视口正确出图
  - 依赖：P2-3、P2-4
- [ ] **P2-6 (M) 渲染器：Mesh / Lit（三角网 + 光照）**
  - Done：着色实体正确渲染（法线/光照一致）
  - 依赖：P2-5
- [ ] **P2-7 (M) 渲染器：PointCloud**
  - Done：点云在 GL 视口渲染（配 P5-6）
  - 依赖：P2-5
- [ ] **P2-8 (M) 渲染器：Voxel instancing**
  - 范围：`Graphics/Voxel/VoxelRenderer.cpp` per-instance 布局（CENTER/SIZE/COLOR）→ `glDrawElementsInstanced`；**保持 `AcVxInterop` ABI 不变**（C# BlockModelLib 无感）
  - Done：块体模型实例化渲染出图
  - 依赖：P2-6
- [ ] **P2-9 (M) 渲染器：Oblique / OSGB LOD 瓦片**
  - Done：倾斜摄影/OSGB 瓦片渲染
  - 依赖：P2-6
- [ ] **P2-10 (M) Grid/Camera/Viewport 三趟 pass 重接**
  - 范围：`Viewport.cpp` 的 `RenderGridPass → RenderScenePass → RenderOverlayPass` 挂到 GL 渲染器
  - Done：网格+场景+叠加三趟正确合成
  - 依赖：P2-6
- [ ] **P2-11 (M) D2DOverlay → Skia**
  - 范围：`Graphics/D2D/D2DOverlay.{cpp,h}`（406 行）：`DrawText/DrawDimension/DrawDashedLine`；DirectWrite → Skia 文本；DXGI-surface 互操作 → Skia-GL 表面
  - Done：文字/标注/虚线叠加层在 GL 上正确绘制
  - 依赖：P2-10
- [ ] **P2-12 (M) IRenderService 指向 GL 后端**
  - 范围：`xllAcEd/.../RenderServiceImpl` + `xllAcRx/.../IRenderService.h` 切 GL
  - Done：服务层统一走 GL，D3D11 路径 `#ifdef` 关闭
  - 依赖：P2-10
- [ ] **P2-13 🏁 里程碑：内核 GL 离屏/上屏渲染出图**

## P3 · Interop 桥 · 重接 .so（交汇）

- [ ] **P3-1 (L) xllAcEd 重建为 libxllAcEd.so**
  - 范围：`Kernel/xllAcEd/`；314 个 `extern "C"` 导出保留；`__declspec(dllexport)` → visibility；产出 `libxllAcEd.so`
  - Done：`.so` 生成，`nm -D` 可见全部 `PitMine_*` 符号
  - 依赖：P2-13
- [ ] **P3-2 (S) DllImport 名称解析**
  - 范围：`Host/PitMineApp/Interop/EngineInterop.cs` + `Modules/BlockModelLib/Native/AcVxInterop.cs`（2 处硬编码 `"xllAcEd.dll"`）；加 `NativeLibrary.SetDllImportResolver` 映射到 `libxllAcEd.so`
  - Done：C# 在 Linux 能解析并调用一个 `PitMine_*`（如 `PitMine_Initialize` 冒烟）
  - 依赖：P3-1
- [ ] **P3-3 (L) 🔴 HWND 视口嵌入替换**
  - 范围：`PitMine_Initialize`/`PitMine_CreateView` 的 HWND 参 + `MainWindow.xaml.cs`/`PanelEvents`/`FocusSafety` 的 WindowsFormsHost 承载；按 P0-3 换 Avalonia 表面 / X11 handle / 共享纹理
  - Done：内核渲染进 Avalonia 视口控件，无 airspace
  - 依赖：P3-2、P0-3
- [ ] **P3-4 (M) 移除 Win32 P/Invoke**
  - 范围：8 个 `user32`/`kernel32` `[LibraryImport]` + 1 处 `WndProc`（视口/焦点相关）→ Avalonia 等价或桩
  - Done：无 Win32 P/Invoke 残留；焦点/输入正常
  - 依赖：P3-3
- [ ] **P3-5 (S) 调用约定归一 StdCall → Cdecl**
  - 范围：2 个回调委托（`PitMine_RegisterEventCallback` JSON + `RegisterBinaryEventCallback` 二进制）的 `UnmanagedFunctionPointer(StdCall)` → `Cdecl`
  - Done：两个回调在 Linux x64 正确回调，无栈错乱
  - 依赖：P3-2
- [ ] **P3-6 (S) 编组回归**
  - 范围：UTF-8/16 字符串、JSON、65 个 `*Bin` 二进制缓冲、`'PMCB'`/PMxx 头协议的往返验证
  - Done：属性查询/事件/二进制缓冲往返数据一致
  - 依赖：P3-5
- [ ] **P3-7 🏁 里程碑：C# 经 .so 驱动内核，视口在麒麟出图**

## P4 · C# 宿主外壳 · Avalonia 化（托管腿）

> 可与 P1/P2 并行，但 P4-4 视口宿主依赖 P3。`PitMine.Platform` 接缝干净（34 文件仅 1 碰 WPF）。

- [ ] **P4-1 (M) 20 工程 net8.0-windows → net8.0**
  - 范围：全部 `*.csproj`；`UseWPF`/`UseWindowsForms` 关闭；WPF 具体实现移到 `PitMine.Platform.Wpf` 接缝之后（对外契约不动）
  - Done：非 UI 工程在 `net8.0` 编译通过（UI 报错留给后续任务）
  - 依赖：—
- [ ] **P4-2 (L) Ribbon 外壳重建**
  - 范围：`Fluent.Ribbon`（12 文件/343 处）→ 自研 Avalonia Ribbon（骨架 `Styles/Ribbon.axaml` 已原型）；接线真实命令到命令行
  - Done：九大分组按钮全出，点击回显命令
  - 依赖：P4-1
- [ ] **P4-3 (L) 停靠布局 AvalonDock → Dock**
  - 范围：`Dirkster.AvalonDock`（20 文件）→ Dock（wieslawsoltes）；左文件树/对象树 + 右属性 + 底命令行布局还原
  - Done：可拖拽停靠布局工作，布局可持久化
  - 依赖：P4-1
- [ ] **P4-4 (L) 视口宿主 → Avalonia GL 控件**
  - 范围：`WindowsFormsHost`（2 文件）→ 骨架 `Controls/CadGlViewport.cs`，接 P3 的 `.so`
  - Done：主窗口视口显示内核渲染，鼠标轨道/缩放/平移通
  - 依赖：P4-3、P3-7
- [ ] **P4-5 (M) PropertyGrid 替换**
  - 范围：`PropertyTools.Wpf`（8 文件）→ Avalonia PropertyGrid；实体属性绑定 `EntityPropertyBag`
  - Done：单选实体右侧属性面板显示并可改
  - 依赖：P4-3
- [ ] **P4-6 (M) 移除 WinForms interop**
  - 范围：14 文件：`FolderBrowserDialog`、`Keys`/`MouseEventArgs`、`.Integration` → Avalonia 等价
  - Done：无 `System.Windows.Forms` 引用
  - 依赖：P4-1
- [ ] **P4-7 (M) WebView2 + Emoji.Wpf 替换**
  - 范围：帮助/Markdown（`Microsoft.Web.WebView2`，2 文件）+ AI Chat 表情（`Emoji.Wpf`）→ 跨平台 WebView 或原生 Markdown 渲染
  - Done：帮助与 AI Chat 面板在麒麟可用
  - 依赖：P4-1
- [ ] **P4-8 (L) 🔴 宿主 code-behind → MVVM**
  - 范围：`Host/PitMineApp` 的 `.xaml.cs`（占 187 全仓中的一部分）；**按窗口逐个解耦**为 ViewModel（用 `CommunityToolkit.Mvvm`）
  - Done：主窗口 + 核心弹窗（LayerManager/NodeEditor）走 MVVM；先拆子任务再逐个做
  - 依赖：P4-2、P4-3
- [ ] **P4-9 🏁 里程碑：宿主外壳在麒麟跑起来、视口联动**

## P5 · 业务模块迁移 · 9 个（托管腿）

> 每个模块任务的 **Done 统一含**：① `net8.0` 编译过 ② XAML → Avalonia ③ code-behind 解耦到 MVVM ④ 图表/3D 库替换 ⑤ 冒烟通过。逻辑代码（约 185K 行纯 `.cs`）基本原样不动。**大模块先在其下拆窗口级子任务**。

- [ ] **P5-1 (M) SqlLib：SQLite → 达梦 DM8**　2.3K 行；换库核心，**先在其下拆三个子任务**：
  - `- [ ] provider 切换`（小）：`ConnectionFactory.cs` 换 `DmProvider`（`Dm` 命名空间）+ 连接串；Dapper 84 处调用基本不动。**建议抽成多 provider**（SQLite/DM 可切），保留开发/离线用 SQLite 的能力
  - `- [ ] 方言翻译`（中）：14 表 DDL → DM 类型（VARCHAR2/NUMBER/TIMESTAMP/CLOB）；304 处 `AUTOINCREMENT` → `IDENTITY`/序列；7 处 `last_insert_rowid` → `RETURNING`/`@@IDENTITY`；14 处 `sqlite_master` → DM 数据字典（`ALL_TABLES` 等）；67 处 `PRAGMA` 去除；分页 `LIMIT/OFFSET` → DM 等价（选 MySQL 兼容模式可少改）
  - `- [ ] 冒烟`：建表 / 增删改查 / 取自增主键 全过 DM8
  - 依赖：P4-1、P0-5
- [ ] **P5-2 (M) RoadLib**　14.7K 行；0% 界面纠缠（仅 1 XAML）；`OxyPlot.Wpf` → `OxyPlot.Avalonia`。依赖 P4-1
- [ ] **P5-3 (M) BlockModelLib**　31K 行；保持 `AcVxInterop` ABI（instancing 在原生侧 P2-8）；`LiveChartsCore...WPF` → `.Avalonia`。依赖 P4-4
- [ ] **P5-4 (M) MineAssLib**　40.7K 行；多为骨架（仅"批量台阶扩帮"实装），UI 面小。依赖 P4-1
- [ ] **P5-5 (M) MeshEditLib**　16K 行；展点/赋高程/Kriging 估值对话框重写。依赖 P4-5
- [ ] **P5-6 (M) PointCloudLib**　11K 行；分割/去噪/抽稀/剖面/等高线/着色；配 P2-7 点云渲染器。依赖 P4-4
- [ ] **P5-7 (L) PlanLib**　31.5K 行；生产计划；`HelixToolkit.Wpf` → Avalonia/SkiaSharp 变体（API 有别）。依赖 P4-4
- [ ] **P5-8 (L) TaskLib**　75.5K 行（最大模块，日常生产组织）；量大，**先拆窗口子任务**再逐个做。依赖 P4-3
- [ ] **P5-9 (L) GeoDataBase**　25K 行；44% code-behind + HelixToolkit 3D，最难；**先拆子任务**。另：84 处 Dapper 查询的大头在此，随 P5-1 换库按 DM 方言逐一回归。依赖 P4-4、P4-5、P5-1
- [ ] **P5-10 🏁 里程碑：5 实装模块可用，4 骨架模块接线**

## P6 · 平台适配与本地化（共享）

- [ ] **P6-1 (M) 中文字体 → fontconfig 兜底**
  - 范围：84 处硬编码 Windows 字体（微软雅黑/宋体，77 XAML）→ 思源/文泉驿 + fontconfig；原生 `StrokeFontRegistry.cpp`/`GdiGlyphProvider.cpp`（GDI 取字形）→ FreeType/Skia
  - Done：Ribbon 与视口中文正确显示，无豆腐块
  - 依赖：P4-2
- [ ] **P6-2 (M) 🔴 中文输入法 fcitx/ibus**
  - 范围：命令行输入 + `TEXT`/`MTEXT` 文字实体录入的 IME 链路回归
  - Done：fcitx/ibus 下中文可正常输入到命令行与文字实体
  - 依赖：P4-9
- [ ] **P6-3 (S) 许可：注册表 → 配置文件**
  - 范围：`Licensing/`（3 文件，含 `MachineCode.cs` 的 `kernel32` 机器码）；`Registry`/`Microsoft.Win32` → 配置/密钥文件；机器码换 Linux 稳定标识（machine-id/MAC）
  - Done：授权校验在麒麟工作
  - 依赖：P4-1
- [ ] **P6-4 (M) 文件对话框 → Avalonia StorageProvider**
  - 范围：`Microsoft.Win32` `OpenFileDialog`/`SaveFileDialog`（22 文件多为此）→ Avalonia 存储 API
  - Done：导入/导出/打开/保存的选文件对话框工作
  - 依赖：P4-1
- [ ] **P6-5 (S) System.Drawing 去除**
  - 范围：4 文件 → SkiaSharp / ImageSharp
  - Done：无 `System.Drawing` 引用
  - 依赖：P4-1
- [ ] **P6-6 (S) 文件格式 I/O 验证**
  - 范围：DWG/DXF 走 `ACadSharp`（纯托管，已跨平台 ✓）——`Cad/Import/DwgDxfImportService.cs`；LAS 走 LasLib（mmap 已在 P1-6 处理）
  - Done：导入一个 .dxf 与一个 .las 成功渲染
  - 依赖：P4-4、P1-6
- [ ] **P6-7 (M) 达梦驱动打包 + 连接配置**
  - 范围：`DmProvider` 托管/原生依赖随发布包分发；新增连接配置（服务器/端口/库名/账号）界面或配置文件；若本地实例模式，DM 服务随装或前置说明
  - Done：首次启动可配置并连上 DM8
  - 依赖：P5-1、P7-1
- [ ] **P6-8 (M) 存量数据 ETL：SQLite → 达梦**
  - 范围：现场已有 SQLite 数据的一次性迁移脚本/工具；字段类型映射与一致性校验
  - Done：样本库全量迁移、数据核对一致
  - 依赖：P5-1
- [ ] **P6-9 (S) 数据库合规核验**
  - 范围：确认合规范围内无残留 SQLite 依赖（除非 P0-5 允许作本地缓存）；DM 驱动/版本满足信创目录
  - Done：受管持久化全部走 DM8
  - 依赖：P5-1
- [ ] **P6-10 🏁 里程碑：中文输入/字体/文件对话框 + 达梦连通在麒麟就绪**

## P7 · 打包与分发（共享）

- [ ] **P7-1 (S) self-contained 发布 x64/arm64**
  - 范围：骨架 `build/publish-linux.sh`；纳入原生 `libxllAcEd.so` 及依赖 + `Data/` 资产
  - Done：`dist/linux-x64` 目标机免装 .NET 直接运行
  - 依赖：P3-7
- [ ] **P7-2 (M) .deb 打包（麒麟桌面）**
  - 范围：骨架 `build/package-deb.sh`；装到 `/opt/pitmine3d`，注册 `/usr/bin/pitmine3d` + `pitmine3d.desktop`
  - Done：`sudo dpkg -i` 安装后菜单可启动
  - 依赖：P7-1
- [ ] **P7-3 (M) AppImage 单文件**
  - 范围：骨架 `build/package-appimage.sh`
  - Done：AppImage 双击即运行
  - 依赖：P7-1
- [ ] **P7-4 (M) Inno Setup 退役**
  - 范围：`setup/PitMine3D.iss`（出 ~80MB .exe）的打包资产/依赖清单迁到 Linux 打包脚本
  - Done：Windows 安装器不再是发布路径，资产完整迁移
  - 依赖：P7-2
- [ ] **P7-5 🏁 里程碑：麒麟 SP10 可安装 .deb / AppImage**

## P8 · 多架构 · 信创整机（共享）

- [ ] **P8-1 (S) x64 基线固化**——官方 .NET + SkiaSharp 原生齐全，作参照。依赖 P7-5
- [ ] **P8-2 (M) arm64（鲲鹏/飞腾）**——SSE→NEON 验证（P1-8）+ .NET arm64 + SkiaSharp 原生（官方）。依赖 P8-1
- [ ] **P8-3 (L) 🔴 loongarch64（龙芯）**——.NET 社区/openEuler 运行时 + SkiaSharp 原生**自编译**（两颗雷）。依赖 P8-1
- [ ] **P8-4 🏁 里程碑：三架构均产出可安装产物**

---

## 六处雷区（决定工期的隐藏成本）

| 雷区 | 位置 | 为什么阴 |
|------|------|---------|
| DirectXMath → GLM | 2936 处 / 233 文件 / 7 库 | 是几何↔渲染器的通用词汇，非局部；波及类型签名与数值约定 |
| code-behind → MVVM | 187 文件 / 仅 6 已用 MVVM | UI 逻辑深埋 `.xaml.cs`（~65K 行），Avalonia 化必须边搬边解耦 |
| HWND 视口嵌入 | `PitMine_Initialize/CreateView` + 3 宿主文件 | Linux 无 WindowsFormsHost 对应，需全新宿主表面策略 |
| 70 工程 CMake 化 | 70 vcxproj / 0 CMakeLists | 原生腿第一道门，卡住则 `.so` 无从产出 |
| 中文输入法 | 命令行 + TEXT/MTEXT 录入 | fcitx/ibus 联动是 CAD 交互核心，需专门回归 |
| loongarch64 运行时 | 龙芯专属 | .NET 运行时 + SkiaSharp 自编译 + **达梦 .NET 驱动** 三处不确定，可后置 |

## 省力项（降低总量）

- **185K 行纯逻辑几乎不动**——迁移方式是把各模块 View 从 WPF 改到 Avalonia，业务逻辑原样。
- **DWG/DXF 已跨平台**——`ACadSharp` 纯托管，无 ODA/Teigha。
- **数据访问已抽象**——Dapper 与库无关 + `ISqlService`/`ConnectionFactory` 集中，换达梦切换点集中（非直迁，见 P5-1）；QuestPDF、ClosedXML、OpenXml、Markdig、CommunityToolkit.Mvvm 跨平台不动。
- **着色器编译无离线步骤**——运行时 `D3DCompileFromFile` → `glCompileShader` 直迁。
- **骨架已就位**——Avalonia 外壳 + 自研 Ribbon + OpenGL 视口原型 + .deb/AppImage/publish 脚本 + llvmpipe 放行。

---
*进度以本文件勾选状态为准。源规模数字实测自 `PitMine3D`（131 csproj · 70 vcxproj），已修正 README 两处偏差（C API 299→314、HLSL 40→8）。*
