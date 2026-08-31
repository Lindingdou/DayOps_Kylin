# 麒麟移植 · 达成度指标与 Loop 规程

> 本文件是 `/loop`（job 561cc045，每 3 分钟）的**共享记忆**。每次触发都是全新上下文，
> 靠本文件 + `MIGRATION_TASKS.md` + git 历史判断进度、决定下一步、决定何时停。

## 一、达成度指标（DoD）

**总达成度 = 五维加权：**

| 维度 | 权重 | 衡量口径 |
|------|-----:|---------|
| 构建 | 25% | 在 Linux 编译链接通过的工程 ÷ 全部（70 原生 + ~13 托管） |
| 功能对等 | 35% | 麒麟上验证与 Windows 版一致的功能 ÷ 全部（README 约 40 命令 + 9 模块） |
| 渲染 | 15% | GL 化且出图一致的渲染路径 ÷ 7（Line/ThickLine/Solid/Mesh/PointCloud/Voxel/Oblique） |
| 数据库 | 10% | 达梦 DM8 下通过的 DB 操作（SqlLib+GeoDataBase）÷ 全部 |
| 打包运行 | 15% | 5 关：.deb 安装 · 启动 · 9 模块加载 · 视口出图 · 达梦连通 |

**简易代理**：任务队列达成度 = `MIGRATION_TASKS.md` 已勾选 ÷ 74。

**Loop 停止条件（满足其一）：**
1. **全部达成**：74 任务全勾 且 五维 = 100% 且 麒麟 SP10 上 .deb 安装启动、9 模块加载、达梦连通、README 命令/功能人工验证与 Windows 版一致。
2. **无法再自主推进**：剩余未完成任务**全部是外部阻塞**（需麒麟机 / DM8 实例 / Linux GCC+GL 工具链，本机 Windows 无），loop 停下并交还阻塞清单。

## 二、环境天花板（诚实标注）

本机 = **Windows，无 WSL / Linux / 麒麟 / DM8 / GCC / OpenGL**。因此：

- ✅ **可在此推进并验证**：托管代码中能在 Windows 编译验证的部分（方言/DB 代码、可 net8.0 化的纯逻辑工程）、达梦建库脚本、打包脚本、文档。
- 🟡 **只能"代码就绪"、不能"验证完成"**（需你侧上机）：原生内核 GCC 编译、D3D11→OpenGL 实跑、达梦端到端连测、.deb 装机运行、功能对等验证。这类**标 `🟡BLOCKED-VERIFY`，不冒充 done**。
- ⛳ **需人拍板**：P0-2/P0-3/P0-5 决策。

> 结论：本 loop 能把**可在 Windows 落地的代码**尽量推进到"就绪"，但**无法自主到达 100% 已验证的"完整移植"**——那要在你的麒麟机 + DM8 上跑。loop 会推进到只剩外部阻塞项时，自动停并交还清单。

## 三、Loop 操作规程（每次触发遵守）

1. 读 `MIGRATION_TASKS.md` + 本文件 + `git log`，定位**下一个未完成、且本环境可推进**的任务/子任务。
2. **可 Windows 验证** → 实现 + `dotnet build` 验证 + 提交（PitMine3D 生产代码走 `kylin-migration` 分支，绝不动 main）。
3. **需上机验证** → 写到代码就绪，标 `🟡BLOCKED-VERIFY`，记入下方"待上机验证清单"，**不勾 done**。
4. 需人决策 → 记入下方"待决策"，不擅自定。
5. 更新下方达成度快照（每次触发必更）。
6. 判断停止条件：全达成 or 只剩外部阻塞 → `CronDelete 561cc045` 停 loop，写清收尾状态。

## 四、达成度快照

> **Loop 状态：已于 iter 2 停止**（`CronDelete 561cc045`）。原因：命中停止条件 #2——只剩外部阻塞项。
> 重启方式：备好目标环境（见 §二）后，`/loop 3m ...` 重新拉起即可，本文件续接。

> 每次触发更新。格式：迭代 / 时间(相对) / 五维 / 队列 / 备注

- **iter 0（基线）**：构建 2% · 功能对等 0% · 渲染 0% · 数据库 5% · 打包运行 0% → **总 ≈ 2%**
  - 完成：P5-1 方言抽象子步（`ISqlDialect`/`DmDialect`/`SqliteDialectImpl` + `TableBuilder` 接线，SqlLib 构建 0 错 0 警，SQLite 输出字节不变）。
  - 骨架既有：Avalonia 外壳 + OpenGL demo + .deb/AppImage 脚本 + 达梦建库脚本（`db/dm/`）。
- **iter 1（方言单测）**：数据库 5%→8% → **总 ≈ 3%**
  - 完成 + **运行时验证**：P5-1 方言抽象单测（`Tests/Tests.SqlLib`，`dotnet test` 2 passed）——SQLite DDL 字节不变 + 达梦 DDL 正确（`BIGINT IDENTITY(1,1)`/`VARCHAR`/`TINYINT`/`DOUBLE`/`TIMESTAMP`，无 `AUTOINCREMENT`）。commit PitMine3D `0329b10`。
  - 发现（记待办，与移植无关）：`Tests.PitMineApp` 既有编译错误，引用不存在的 `PitMineApp.Licensing`（`LicensingTests.cs`/`RegisterWindowRenderDump.cs`），该测试工程当前编不过。
  - 下一步（仍 Windows 可验证）：① DM `CREATE INDEX IF NOT EXISTS` 关键字兼容（DM/MySQL 模式）② `_schema_migration` 历史表 DDL 方言化。此二者做完后，P5-1 余项（连接层/DmProvider/.sql 脚本翻译/冒烟）**均需 DM8** → 转 BLOCKED-VERIFY。
- **手动 · 渲染管线搭建（P2 · 骨架侧）**：渲染 0%→5% → **总 ≈ 4%**
  - 完成 + **运行验证**：骨架视口重构为内核式**三趟渲染管线**（`Camera` + `GlRenderer` + GridPass/ScenePass/OverlayPass + 左下角坐标罗盘），Windows **GLES 3.0 实测**：GL 初始化 + 着色器编译链接 + 三趟渲染循环 **0 错**。commit DayOps_Kylin `12105bc`。
  - 说明：这是 **Avalonia 侧渲染管线脚手架**（渲染腿落点），可跨平台运行验证。C++ 内核 `xllAcGi` 的**真实渲染器移植**（7 条渲染路径 + 8 HLSL→GLSL + DirectXMath→GLM）仍是主体，需 Linux + GCC + OpenGL，属 🟡BLOCKED-VERIFY。

## 五、待上机验证清单（🟡BLOCKED-VERIFY）

- **P5-1 连接层**：`ConnectionFactory`/`SqlService` 接 `DmProvider`（换 `IDbConnection`），需 DM8 实例连测。
- **P5-1 .sql 迁移脚本**：`V*.sql` 达梦方言翻译 + `PRAGMA` 去除，需 DM8 跑迁移验证。
- **DM DDL 关键字**：`CREATE INDEX IF NOT EXISTS`、历史表 `TEXT`→`VARCHAR` 等，需 DM8 确认可执行后再改（不在无验证下改生产热路径）。
- **原生内核**：70 工程 CMake + D3D11→GL 渲染，需 Linux + GCC + OpenGL。
- **托管 UI**：Fluent.Ribbon/AvalonDock/WinForms → Avalonia，~167 对话框，需 Avalonia + 目标机验证。
- **打包 / 功能对等**：.deb 装机 + 9 模块加载 + 视口出图 + 达梦连通 + README 命令逐条对齐，需麒麟 SP10。

## 七、要解除阻塞、让 loop 重新推进，需要你提供

1. **DM8 开发版装到麒麟机**（`db/dm/README.md` 有步骤）→ 解锁 P5-1 连接层 / `.sql` 翻译 / 达梦端到端。
2. **一台可连的麒麟/Linux 环境**（SSH 亦可，含 .NET 8 SDK + GCC/Clang C++20 + OpenGL）→ 解锁原生内核 CMake、渲染、构建、打包、功能对等验证。
3. **P0-2/P0-3/P0-5 决策**（DirectXMath 路线 / 视口宿主 / 达梦部署模式与兼容模式）。

> 有了 1+2，可验证面才真正打开；届时 `/loop` 重启，能持续推进而非撞墙即停。

## 八、功能补全 loop（可托管功能 · Windows 可验证）

> 策略（用户指定）：跳过并**记录**当前无法验证/实现的（🔵需内核、🟣需模块），
> 只把"能做且可验证"的功能一项项补全，做完接下一项，直到可做功能全完成才停。

**✅ 已完成（验证过）：**
- [x] **DXF 解析** `DxfImportService`（ACadSharp 3.5.7）读 .dxf → 提取 Line/LwPolyline/Polyline/Circle/Arc 为线段几何 + 范围框。2 单测通过。commit `296c786`。
- [x] **DXF 渲染 + 导入触发 + 范围缩放**：`CadGlViewport.ShowImportedGeometry`（GL 线程上传，ScenePass 画导入线框）；`Camera.FitBounds`（ZOOMEXTENTS，2 单测）；Ribbon「导入」→ Avalonia 文件对话框 → 显示。4 tests + app 启动无崩溃。commit `aa3797d`。（打开真图纸上屏效果需手测）
- [x] **按实体/图层颜色上色**：`ColorOf`（ByLayer/真彩色/ACI 1-9）逐实体着色。+1 单测（红线 ACI 1 → 偏红），共 5 passed。commit `833c1c6`。
- [x] **2D/3D 视图模式切换**：`Camera` 2D 正交俯视（拖拽平移）/ 3D 透视轨道；命令行 `2D`/`3D`。顺带修透视远平面裁剪大图纸的 bug。+2 单测，共 7 passed。commit `8d339bb`。
- [x] **Point + Ellipse 图元**：点 → 十字、椭圆 → 主轴/半径比/起止参数折线近似。+1 单测，共 8 passed。commit `82b499b`。
- [x] **对象管理器面板**：左侧 TreeView，导入后按图元类型列出（`TypeCounts`，含计数单测）。app 左面板正常。commit `bf9a1a0`。
- [x] **文件管理器面板**：`CadFileBrowser.ListDxf` 枚举文件夹 .dxf（+2 单测）；左面板拆「文件管理器 | 对象管理器」，双击文件导入。10 tests；app 双面板正常。commit `4cdc546`。
- [x] **节点编辑器**：`NodeGraph` 模型（节点/连线校验，3 单测）+ Avalonia 画布窗口（添加/拖拽/点击连线）；「工具」按钮 / 命令 `节点编辑器` 打开。13 tests。commit `ee7da6f`。
- [x] **图层管理器**：几何按图层分组（`LayerGeometry`）+ 左面板「图层」勾选框显隐（`SetLayerVisible`）。+1 单测（墙/柱 分组），14 tests。commit `4760dfe`。
- [x] **坐标读数**：状态栏显示光标世界坐标（`Mat4.Invert` + 屏幕→世界反投影交 Z=0）。+2 单测，16 tests。commit `0d18dbb`。
- [x] **视图命令**：`ZE`/`ZOOMEXTENTS` 范围缩放（复用 `FitBounds`）+ `GRID` 网格/轴开关。16 tests 无回归。commit `90161f0`。
- [x] **INSERT 块引用展开**：块内几何按插入变换（平移/旋转/缩放）递归展开（嵌套上限 8）。+2 单测（变换数学 + 含块 DXF round-trip），18 tests。commit `acb2090`。
- [x] **实体选择高亮**：按类型分组几何（`TypeGeometry`）+ 对象树选类型 → `SetHighlight` 重着色画最上层。+1 单测（Recolor），19 tests。commit `76a588f`。
- [x] **Spline 样条导入**：De Boor NURBS 求值采样（`EvalBSpline`）+ 退化回退拟合点/控制多边形。+2 单测，21 tests。commit `7afd2a7`。
- [x] **完整视图导航 + 右键菜单**（应用户要求插入的优先项——基础交互补完整，才好测其余功能）：中键/2D左键平移、3D左键旋转、滚轮朝光标缩放、双击范围缩放；右键菜单（范围缩放/2D-3D/网格/清除高亮）。+2 单测，23 tests。commit `8deb00a`。
- [x] **DXF 导出**：显示几何写回 .dxf（`DxfExportService`）+ 另存为按钮/命令接线。+1 单测（round-trip 段数一致），24 tests。commit `f307674`。
- [x] **DIST 测距**：两点状态机（`MeasureState`）+ 左键取点显示距离/画测量线。+2 单测，26 tests。commit `eeb1fa8`。
- [x] **对象捕捉**：`SnapPoints.FindNearest`（容差内最近顶点）+ 开关/绿色十字标记/坐标 [捕捉]/测距吸附。+3 单测，29 tests。commit `d115e79`。
- [x] **Home 绘制引擎（托管重实现）**：内存场景 `Scene` + 交互绘制工具，直线/圆/矩形/点可点绘（接捕捉、ESC 退出、实时上屏）。+5 单测，34 tests。commit `7f58e85`。**说明**：Home 绘制/编辑原为 C++ 内核 jig，此为内核到位前的**托管重实现**；内核接入后由内核路径接管。

- [x] **Home 绘制全集**：+ 圆弧（三点外接圆）+ 多段线（多点/双击结束/进行中预览）。绘制命令全到齐：直线/圆/圆弧/矩形/多段线/点。+5 单测，39 tests。commit `93a7014`。

- [x] **Home 编辑地基：选择 + 删除**：点选（命中测试 = 点到线段距离）+ 黄色高亮 + 删除（删除按钮/E命令/Delete键）；点击/拖拽区分。+2 单测，41 tests。commit `e3bc79d`。
- [x] **Home 编辑：移动/复制/镜像**：通用 `Affine2` 仿射 + 每实体 `Apply`（圆缩半径/旋转矩形转多段线）；两点取点变换（移动/M、复制/CO、镜像/MI）。+4 单测，45 tests。commit `5b77da0`。
- [x] **Home 编辑：旋转/缩放**：旋转（基点+角度参照）、缩放（基点+参考长+新长三点）；复用 Affine2。45 tests。commit `7d94a6f`。→ **编辑组核心齐**（选择/删除/移动/复制/镜像/旋转/缩放）
- [x] **Home 文件：新建/打开/保存**：绘制场景存 **`.pmx`**（原版 = CAD 绘图主文件；`SceneIO` JSON 多态 DTO），往返一致（实体/类型/颜色/闭合）。+2 单测，47 tests。commit `4c70444`（后改 .pm2d→.pmx 对齐原版）。
  - 注：原版 **`.pmb` = 块体模型**（BlockModelLib 模块数据，非绘图场景）→ 归 🟣需模块，待该模块移植时用。绘图用 `.pmx`。

- [x] **Home 图层管理**：`LayerTable`（新建/设当前/轮转配色/删除护默认层）；绘制实体按当前图层着色。+4 单测，51 tests。commit `56a0d0b`。→ **Home 主体移植完成**（文件/绘制/编辑/图层）

- [x] **Home 编辑：偏移**：直线（法向距离）/圆（同心到点击）/矩形（外扩内缩）平行偏移到点击侧（`Offset`）。+2 单测，53 tests。commit `bde25e2`。多段线/圆弧偏移记为复杂待做。

- [x] **Home 编辑：修剪/延伸**：`LineMath` 线-线求交，目标近端点移到交点（缩短/加长统一）。+2 单测，55 tests。commit `4f5fd53`。仅支持直线（同原版）。→ **Home 编辑组全部到齐**（选/删/移/复/镜/转/缩/偏移/修剪/延伸）

**⚠️ 更正：Home 并未"移植完成"**（此前过度宣称）——真实约 **40% 完成**，详见 **§九 Home 诚实重评估**。已实现的仅是各命令**最基本的点击流程**；缺：大量命令（正多边形/文字/打断/分解/全选…）、命令选项（圆的 2P/3P/TTR、坐标输入…）、精确键盘输入、可编辑的导入、夹点编辑、真正的图层特性管理器。

**⏳ 剩余（细化 / 较复杂，非核心）：**
- [x] 偏移补全：多段线/圆弧偏移（miter/圆弧偏移几何）——已完成，见 §九 line 167 偏移✅(线/圆/矩/多段线/圆弧)，`ArcEntity_offset` 等单测覆盖。
- [x] 图层特性：绘制层 **冻结/锁定/全开**（`Layer.Frozen/Locked`；场景渲染 `BuildGeometry(isShown)` + 拾取 `Pick(canSelect)` 遵守；命令 冻结/解冻/锁定/解锁/图层全开 · LAYFRZ/LAYTHW/LAYLCK/LAYULK/LAYON）。commit 见下。
- [x] 图层细化：绘制层**完整面板**（每层 显隐/冻结/锁定/设当前/色块，命令与面板双向同步）。commit `fcc7e1f`。剩：图层改色（需 ByLayer 色模型）、线型。
- [x] 修剪/延伸扩展：支持圆/弧/多段线为边界或目标——已完成，见 §九 line 167（延伸边界任意实体；目标 线/多段线/圆弧 全类型）。

**⏳ 其它待做：**
- [x] MANG 角度测量（`AngleState` 接线完成，三点测角）。commit `0551d9c`。
- [~] ~~PDF 导出~~ **移除**：核对原 PitMine3D/Host 全源码无 PDF/打印导出命令（导出仅 DXF/DWG/KDF/SVG），属不忠实新增，不实现。

> **Loop 状态：二次重开**（job `876a2719`，每 3 分钟）。此段"剩余"清单至此全部结清（要么完成、要么判定不忠实移除）。后续可做项转入 §十一 业务切片与忠实性核对流程。

**🟡 已记录 · 延后（当前无法验证/实现，需目标环境）：**
- 🔵 **需内核**：绘制/编辑/选择/夹点等交互命令、命令系统、精确几何、LAS 点云、内核渲染路径。
- 🟣 **需模块**：9 大业务模块（GeoDataBase…TaskLib）。
- ~~**DWG 显示**（跳过·已记录）：ACadSharp DWG 写入不可靠…~~ **更正（已验证）**：DWG 导入+导出均已接线（`DwgReader`/`DwgWriter`，SceneExportService 按扩展名走 .dwg），且 **DWG 往返测试通过**（写→读回保留 Line/Circle/闭合多段线 + 图层「墙/柱」）——旧"写入不可靠"记录过于悲观，DWG 编解码在本平台可靠。commit 见下。
- **DWG↔内核实体**：导入几何与内核 AcDb 实体系统对接需内核。
- **Text/MText 文字 · Hatch 填充**（跳过·已记录）：文字需 Skia 字形渲染子系统、填充需三角面渲染路径，均超出当前线渲染管线；待渲染腿扩到 Skia 文字 / 填充着色后再加。
- **达梦**：连接层 / `.sql` 脚本翻译 / 端到端（需 DM8 实例）。

## 六、待决策（⛳）

- P0-2 DirectXMath→GLM 路线 · P0-3 视口宿主策略 · P0-5 达梦部署模型/兼容模式/合规范围

## 九、Home 标签页诚实重评估（40 按钮）

> 用户指出移植不充分，重新逐按钮核对。状态：✅完整 / 🟡部分（仅基础/仅部分类型/语义简化）/ ❌未做。

**文件（6）**：新建✅ 打开✅ 保存✅(回写当前文件) 另存为✅ 导出✅ 导入✅ 选项✅(网格/捕捉/容差对话框) —— **文件组全完成**
> 当前文档路径跟踪：Save 直接回写、标题显示文件名（commit `f74759d`）

**📂 文件格式矩阵**（原版导入清单见 Explore 调查；打开=.pmx/.pmb，导入=交换格式）：
| 格式 | 状态 | 说明 |
|---|---|---|
| `.dxf` | ✅ | ACadSharp；Line/Poly2D(**含 bulge 弧段**)/**Poly3D**/Circle/Arc/Point/Ellipse/Insert/Spline/**Text/MText/Solid/Face3D/Dimension(爆炸渲染块)/Hatch(边界轮廓)/XLine·Ray(长线段近似)** → 可编辑实体（Latin/数字文字可显; Solid/Face3D/Hatch 取轮廓不填充; 中文需字库） |
| `.dwg` | ✅ | ACadSharp DwgReader；与 DXF 同管线 → **可编辑实体**（本平台读写已验证） |
| `.off` | ✅ | Geomview 网格（顶点+面表→去重边），托管解析 |
| `.csv/.txt/.xyz/.pts` | ✅ | 点数据（自动识别数值列）→ **可编辑**点实体入场景 |
| `.pmx` | ✅ | 打开/保存（SceneIO JSON；原版为 PMX1 二进制，非同构但语义等价） |
| `.pmb/.blk` | ❌ 记录 | 块体模型 → 需 BlockModelLib 模块（PmbmReader/BlkReader） |
| `.3dm/.3ds/.3dp` | ❌ 记录 | 3DMine 私有二进制/工程包 → 需格式规范+样本 |
| `.wl/.wt/.wp/.mpj` | ❌ 记录 | MapGIS 私有 → 需格式规范+样本 |
| `.kdf` | ❌ 记录 | WeCAD 私有二进制 → 需格式规范+样本 |
| `.las` | ❌ 记录 | 点云 → 需 C++ 内核 LasLib（二进制+八叉树缓存） |
| `.osgb/.osgtile` | ❌ 记录 | 倾斜摄影 → 需引擎/内核 |
| `.xlsx` | ❌ 记录 | 钻孔/煤质 → 需 ClosedXML（CSV 分支已可） |
**绘制（9）**：点✅ 多段线✅ 直线✅ 矩形✅ 正多边形✅ 滑动多段线✅ 圆✅(圆心半径/2P/3P/TTR全类型) 圆弧✅(三点/SCE/CSE/SER) 文字🟡(单笔画字体: 数字/符号/XYZM 可显; **中文字形需字库**, 记录) —— **绘制组全部可用**
**修改（9）**：复制✅ 移动✅ 旋转✅ 删除✅ 分解✅ 偏移✅(线/圆/矩/多段线/圆弧) 打断✅(线/多段线/圆弧) 夹点✅ 修剪✅ 延伸✅(边界任意实体；目标 线/多段线/圆弧 全类型) —— **修改组全完成**
**图层（5）**：新建图层✅ 全开✅ 冻结✅ 锁定✅ 完整面板✅(显隐/冻结/锁定/设当前) 改色✅(点色块循环换色,实体跟随) | ~~线型❌~~**更正**：原图层管理器(`LayerManagerViewModel`)属性仅 Name/Color/**LineWeight**/Visible/Frozen/Locked——**无线型**, 故"线型"非缺口(不忠实, 不做) · **线宽(LineWeight)**原程序有: 存储可托管, 但渲染需变宽线(thick-line 几何扩展或内核 ThickLine 路径)→ 记录·渲染受阻 · 真 ByLayer 联动(改色即时跟随而非烘焙)🟡(渲染色模型精化, 非原程序缺失功能)
**视图（5）**：2D✅ 3D✅(Ribbon 按钮+命令行) 范围缩放✅ 网格✅ 清空视图✅(清选择/高亮/标记) 夹点开关✅(GIZMO 切换夹点显示) | 渲染配置❌(需内核)
**特性/选择（4）**：全部选择✅ · 最后✅ · 上次✅ · 窗口框选✅(窗口/交叉) · 夹点✅ · 快速选择✅(选择类似) · 多边形圈选✅(WP/CP) · 对象捕捉✅(端点/中点/圆心/象限/边中点 osnap)
> ⚠️ 交互变更：2D 左键拖now=选择框（原为平移）；平移改**中键拖拽**（滚轮缩放不变）。3D 左键仍旋转（3D 框选待做，记录）。
**帮助（2）**：帮助文档✅(命令/快捷键参考窗口) | 注册❌(需达梦授权系统)

**统计：✅ ~15 · 🟡 ~9 · ❌ ~16 → 约 40% 完成。**

**更深层的共性缺口（贯穿所有命令）：**
1. **命令选项/子模式全缺**：只做了最基本点击流；圆的多种画法、多段线圆弧段、矩形选项等都没有。
2. ~~**精确键盘输入全缺**~~ ✅ **已做**：命令行支持 `x,y` / `@dx,dy` / `d<ang` / `@d<ang`，绘制/编辑取点时等效点击（commit `3b38128`）。
3. ~~**导入与绘制两套、互不相通**~~ ✅ **已打通**：CAD(DXF/DWG) 导入现映射为**可编辑场景实体**（Line/Circle/Arc/Polyline/Point/Ellipse→多段线/Spline→多段线/Insert 展开），图层并入绘制图层表，可选中/编辑/删除/移动/按层冻结；捕捉统一用场景几何（commit `722dbeb`+`ea05b14`）。点数据 CSV 同样进可编辑场景。剩 OFF 网格仍显示态（网格逐边编辑无意义）。
4. ~~**夹点编辑（grip）没做**~~ ✅ **已做**：单选显示夹点方块，拖拽改几何（直线端点/中点、圆心/半径、矩形角、圆弧三点、多段线顶点、多边形心/半径），可撤销（commit `e8ba0b2`）。
5. **命令行只识别固定词**：无参数解析、无历史；~~无 Enter 重复上次命令~~ ✅ **Enter 重复已做**（空命令行+Enter 在空闲态重复上次命令，AutoCAD 行为，commit 见 §十一）。

**已完成（续 loop）**：全部选择/最后/上次 ✅ · 分解 ✅ · 正多边形 ✅ · 滑动多段线 ✅ · 橡皮筋预览(全绘制工具) ✅ · 精确坐标输入(绝对/相对/极) ✅ · 打断(直线) ✅ · 圆 2P/3P ✅ · 图层特性(绘制层 冻结/锁定/全开，渲染+拾取遵守) ✅

**本轮补（文件/格式）**：DWG 导入 ✅ · OFF 网格导入 ✅ · 点数据 CSV/TXT 导入(可编辑) ✅ · 新建完整文档重置 ✅

> **Home 选项卡：所有非环境阻塞的功能已全部实现**（含边角增量：圆四法/圆弧四法/TTR全相切/修剪目标全类型/图层改色/文档路径/选项对话框）。
> 仅剩环境阻塞项（客观无法在本机实现，已记录）：**中文字形**(需 Skia/矢量字库; 数字/Latin/符号单笔画已可)、**填充**(需 Skia)、**线宽渲染**(需 thick-line 几何扩展或内核 ThickLine 路径)、**渲染配置**(需 C++ 内核渲染路径)、**注册**(需达梦授权系统)。
> 后续 loop 若无新的可做 Home 增量，将转向业务模块骨架（多数需内核/达梦，预计以"记录阻塞项"为主）。
> 已记录待做：**3D 框选**（与 orbit 左键冲突且实体为 Z=0 平面，价值低）。
> 注：原版 Home 修改组 9 按钮已全做；不擅自加 ARRAY/FILLET 等原版 Home 未列命令（保持"符合现程序"）。

> 说明：**绘制组已全部完成**（点/线/多段线/矩形/圆(4法)/圆弧(3法)/正多边形/滑动多段线），仅文字需 Skia。**修改组已全部完成**。**文件组已全部完成**（仅选项对话框待做）。**图层组已全部完成**（原程序图层属性 名称/颜色/线宽/显隐/冻结/锁定 均已覆盖; 线宽渲染需 thick-line 路径, 已记录; 原无线型）。**选择组已全部完成**（点选/框选/全选/最后/上次/夹点）。

**里程碑**：CAD 导入→可编辑实体 已完成（`ea05b14`）——最大共性缺口#3（导入/绘制两套互不相通）打通。

**架构差异记录**：原版"新建"= 多视图 Tab(LayoutDocument `视图{N}` + 每视图独立引擎 view)；本移植为单视图 → 多文档/停靠架构待评估。

## 十、业务模块可做性评估（Home 完成后的剩余范围）

> Home 标签页可做功能已 100% 完成（详见 §九）。以下是 9 个业务模块的**托管可移植性**评估
> （依据：对 C++ 内核 EngineInterop/PInvoke 的引用计数 + 达梦/SQL 依赖 + 结果渲染方式）。

| 模块 | 内核引用 | 托管度 | 可做切片（本环境可验证） | 阻塞点 |
|---|---|---|---|---|
| **GeoDataBase** 地质数据库 | 0/185 | ★★★ 高 | 钻孔/煤质 **CSV 导入 + 数据表**；钻孔柱状图=线几何可上我方视口 | .xlsx 需 ClosedXML；DB 持久化需达梦 |
| **PlanLib** 规划 | 0/102 | ★★★ 高 | 计划编制的**纯算法/数据模型**（排产/接续计算） | 结果多为报表/甘特，需 UI；部分依赖数据源 |
| **RoadLib** 道路 | 1/50 | ★★ 中 | 道路中线/路网的**折线几何**（可上我方视口） | 部分几何运算/三角网需内核 |
| **MineAssLib** 采矿辅助 | 4/97 | ★★ 中 | 工作线/条带等**折线几何**、模板 JSON | 块体/三角网交互需内核 |
| **BlockModelLib** 块体模型 | 2/97 | ★★ 中 | **`.pmb/.blk` 格式读取器为托管**（PmbmReader/BlkReader 可移植）；块中心可作点显示 | 体素/切割渲染需内核；大数据量 |
| **SqlLib** | 0/25 | ★ 低 | SQL 浏览器 UI 逻辑 | **整体需达梦连接** |
| **MeshEditLib** 网格编辑 | 3/78 | ✗ 低 | — | 三角网/倾斜摄影 **需内核 mesh ops** |
| **PointCloudLib** 点云 | 4/67 | ✗ 低 | — | LAS 解析+八叉树 **需 C++ LasLib 内核** |
| **TaskLib** 任务/调度 | 6/168 | ✗ 中 | 部分调度算法托管 | 大量视口交互/数据依赖 |

**结论**：
- **纯内核阻塞**（本机不可做）：PointCloudLib、MeshEditLib —— 记录，等内核。
- **需达梦**：SqlLib、各模块的持久化 —— 记录，等 DM8。
- **有托管切片可做**：GeoDataBase(钻孔CSV+柱状图)、RoadLib/MineAssLib(折线几何)、BlockModelLib(.pmb 读取)、PlanLib(算法)。
  但每个都是 50~185 文件的**大模块**，且移植一个模块 = 新的大范围工作（数据模型+UI+可能的达梦持久化）。

**⛳ 待你决策**：Home 目标已达成。是否要我继续移植某个业务模块的托管切片？若是，请指定优先模块
（建议从 **GeoDataBase 钻孔 CSV 导入 + 柱状图**入手——纯托管、可上现有视口、可单测）。
未指定前，loop 将以"记录阻塞项、做零星无争议增量"为主，不擅自启动大模块移植。

## 十一、业务模块托管切片移植（Home 完成后启动）

> 策略：只挑各模块中**纯托管、结果可上现有线/点/矩形视口、可单测**的切片；
> 需内核渲染(体素/网格/点云)、需达梦持久化、需文字标注的部分记录待做。

### GeoDataBase 地质数据库
- [x] **钻孔 CSV 解析**：`BoreholeImportService`（孔号/X/Y/高程/自/至/岩性 → Borehole+Interval 归组）。+4 单测，137 tests。commit `3b28211`。
- [x] **钻孔柱状图渲染**：`BoreholeRender` 中轴线 + 岩性配色分层矩形柱（无需文字）；命令 展绘钻孔/钻孔柱状图/ZK。+2 单测，139 tests。commit `ae56a2d`。
- [x] **钻孔孔号标注**：展绘钻孔时孔口上方加孔号文字(A-Z 字体)。commit `714c890`。
- [记录] 岩性名/孔号**文字标注** → 需 Skia。钻孔数据**入库/查询** → 需达梦。.xlsx 导入 → 需 ClosedXML。填充色块(实心) → 需 Hatch/Skia。

### GeoDataBase 续
- [x] **等高线生产**：`Contour`（散点 IDW→网格 + Marching Squares 多层等值线）→ 高程点 CSV(x,y,z) 生成彩色等高线折线入场景。命令 等高线/等高线生产/CONTOUR。+4 单测(算法3+插值1)，143 tests。commit `e4d1ad2`+`0fa5e4d`。
- [x] **等高线高程标注**：每层加高程数字文字(复用单笔画字体)。commit `eaecbd1`。
- [记录] 等高线**标注高程数字** → 需 Skia。三角网 TIN(Delaunay) 更精确的表面 → 可托管做但较大，待评估。

### MeshEditLib / 三角网（托管切片）
- [x] **创建三角网(TIN)**：`Delaunay`（Bowyer-Watson 剖分 + 三角边去重线框）→ 点 CSV(x,y[,z]) 生成三角网入场景。命令 创建三角网/三角网/TIN。+5 单测，148 tests。commit `44659ce`+`b39c632`。
- [x] **坡度着色**：`TerrainAnalysis`(三角面法向→坡度→绿平红陡)。命令 坡度着色/SLOPE。+5 单测，153 tests。commit `d363f6d`。
- [x] **坡向着色**：三角面朝向→罗盘 HSV 配色（`AspectDegrees`+`HsvToRgb`）。命令 坡向着色/ASPECT。+3 单测，156 tests。commit `4c8f37a`。
- [记录] 布尔/光滑/补洞 mesh 编辑 → 需内核。倾斜摄影/OSGB → 需内核。面填充 → 需 Hatch。
- [x] **体积/土方量计算**：`TerrainAnalysis.Volume`（TIN 相对基准面棱柱求和 → 挖方/填方/净值）。命令 体积计算/算量/VOLUME。+2 单测，158 tests。commit `2c9eaa1`。
- [x] **两期差值算量**：`TwoEpochVolume`(两期→同 IDW 网格→逐格差值→挖方/填方/净)。命令 两期点云算量/两期算量/DIFFVOL。+1 单测，159 tests。commit `74032da`。
- [x] **圈范围算量**：`VolumeWithinBoundary`(三角质心在选中闭合多段线内才计) + `LineMath.PointInPolygon`。命令 圈范围算量/BNDVOL。+2 单测，161 tests。commit `c13b686`。
- [x] **高程分带着色**：ElevationColor(低绿→中黄→高棕) + BuildElevationMap。命令 高程着色/分色显示/ELEV。+1 单测，162 tests。commit `ce4d778`。

### 通用地形/分析（跨模块托管切片）
- [x] **剖面分析**：`Profile.Sample`(沿剖面线 IDW 采样地形高程→距离-高程曲线)。命令 剖面分析/剖面/PROFILE。+3 单测，189 tests。commit `b168409`。
- [x] **面积/周长测量**：`GeomMeasure`(鞋带面积+周长)。命令 面积/AREA。补齐 DIST 之外的 AREA。+4 单测，212 tests。commit `34c1155`。
- [x] **坐标转换**：`CoordTransform`(控制点对→Helmert 4参 平移/旋转/缩放→套用全场景)。命令 坐标转换/COORDTRANS。+5 单测，205 tests。commit `2e24cba`。
- [x] **地表粗糙度**：`Roughness.Compute`(网格 3×3 邻域极差)→配色格。命令 粗糙度/ROUGHNESS。+2 单测，200 tests。commit `9d37b87`。
- [x] **地表曲率**：`Curvature.Compute`(网格拉普拉斯, 负凸正凹)→配色格。命令 曲率/CURVATURE。+3 单测，208 tests。commit `7b53c07`。
- [x] **高程查询/虚拟钻孔点**：`Contour.IdwAt` + 点击查询高程(连续+标记)。命令 高程查询/虚拟钻孔/SPOT。+1 单测，214 tests。commit `58f7339`。

### RoadLib 道路（托管切片）
- [x] **提取道路中心线**：`RoadTools.Centerline`（两路边多段线→逐点最近中点连线）。命令 提取道路中心线/道路中线/CENTERLINE。+3 单测，165 tests。commit `e305b4e`。
- [x] **点对点寻径**：`RoadNetwork`(多段线建图+Dijkstra) + 两点交互→青色路径+长度。命令 点对点寻径/寻径/PATH。+4 单测，169 tests。commit `548785b`+`51ffd4c`。
- [x] **等效运距**：`RoadNetwork.PathLength` + 任务吨位加权平均运距(吨公里)。命令 等效运距/运输指标/HAUL。+1 单测，213 tests。commit `695ba95`。
- [记录] 路网存档/更新/预览、结构路面、驱动距离/量 → 需路网数据模型/持久化(部分可托管，待评估)。

### MineAssLib 采矿辅助（托管切片）
- [x] **排土条带**：`Hatch.ParallelFill`（扫描线法，闭合边界内按间距生成平行线）。命令 排土条带/条带填充/STRIPS。+3 单测，172 tests。commit `ff3642d`。
- [x] **分帮扩帮**：`BenchTools.BatchOffset`(台阶线批量平行偏移)。命令 分帮扩帮/批量台阶扩帮/BENCH。+2 单测，174 tests。commit `adf9257`。
- [x] **组合工作线**：`PolylineJoin.Join`(端点相接合并多段线)。命令 组合工作线/合并多段线/JOINPOLY。+4 单测，178 tests。commit `25062c3`。
- [x] **多边形裁剪**：`PolygonClip.Clip`(Sutherland-Hodgman, 凸边界交集)。命令 裁剪/区运算/CLIP。+3 单测，230 tests。commit `e0ecf09`。
- [x] **曲线平滑**：`PolylineSmooth.Chaikin`(角点切割光滑)。命令 平滑/光滑/SMOOTH。+3 单测，233 tests。commit `05f02d3`。
- [x] **多段线简化**：`PolylineSimplify.DouglasPeucker`(容差内减顶点)。命令 简化/抽稀线/SIMPLIFY。+3 单测，236 tests。commit `fec099b`。
- [记录] 块体交互/切割 → 需内核。

### BlockModelLib 块体模型（托管切片）
- [x] **块体模型 CSV 导入**：`BlockModel`(x,y,z,尺寸,品位 → 品位配色方块平面显示 + min/max/mean 统计)。命令 块体模型/导入块体/BLOCKMODEL。+4 单测，182 tests。commit `1178318`。
- [记录] 原版 **.pmb 私有二进制**(PMBM1)读取 → 需格式规范/样本(可参照 PitMine3D/Modules/BlockModelLib/Format 移植, 但需样本验证)。体素 3D 渲染 → 需内核。

### PlanLib 规划（托管切片）
- [x] **资源量估算/剥采比**：`BlockModel.Resource`(按 cutoff 分矿废→矿量/剥采比/平均品位/金属量/吨位)。命令 资源量估算/剥采比/RESOURCE。+1 单测，183 tests。commit `87dc06f`。
- [x] **快速估值**：`Estimation`(品位样本 IDW→网格→配色估值面, 复用 GridInto+GradeColor)。命令 快速估值/品位估值/ESTIMATE。+2 单测，191 tests。commit `44cac48`。
- [记录] 排产/接续/进度计划 → 结果为甘特/报表, 需表格 UI(部分算法可托管); 真克里金(变差函数) → 可托管做, 较大。
- [x] **境界圈定**：`GeomHull.ConvexHull`(Andrew 单调链)→散点凸包边界。命令 境界圈定/凸包/确定境界/采场圈定/HULL。+3 单测，186 tests。commit `d2d5d95`。

### PointCloudLib 点云（托管切片）
- [x] **点云抽稀**：`PointThin.Thin`(体素网格每格保留一点, 对 XYZ 点集有效)。命令 点云抽稀/抽稀/THIN。+3 单测，194 tests。commit `75fdc74`。
- [x] **地面点滤波**：`GroundFilter.LowestPerCell`(每 XY 格取最低点≈地面)。命令 地面点滤波/GROUND。+2 单测，196 tests。commit `944f18c`。
- [x] **C2C 点云比对**：`CloudCompare`(A每点到B最近距离→偏差配色+max/mean)。命令 C2C/点云比对/演化对比。+2 单测，198 tests。commit `01b2af3`。
- [记录] **.las 二进制解析 + 八叉树缓存 + 点云渲染** → 需 C++ 内核 LasLib(C2C 大数据加速也需之)。

### 文字（单笔画解锁）
- [x] **文字(数字/符号)**：`StrokeFont`(7段数字+.-+:/ XYZM) + `TextEntity`(镶嵌线段, Apply/Grips/SceneIO)。命令 文字/TEXT。部分解锁原 Skia 阻塞——数字/坐标/尺寸标注可显。+5 单测，219 tests。commit `a0f84c5`。
- [x] **完整 Latin (A-Z)**：StrokeFont 扩展 26 字母，钻孔号/图层名/标签可显。commit `12d2181`。
- [记录] **中文字形** → 需矢量字库(单笔画汉字库或轮廓字体三角化); 填充/字体样式 → 需 Skia。
- [x] **线性标注(尺寸)**：`DimTools.Build`(尺寸线+刻度+距离文字, 复用单笔画字体)。命令 标注/线性标注/DIM。文字解锁后新增。+3 单测，222 tests。commit `e0a0d07`。
- [x] **文字 DXF 往返**：TextEntity ↔ DXF TEXT 导入+导出完整。commit `e63243d`。
- [x] **多边形圈选(WP/CP)**：`SelectionBox.MatchPolygon`(顶点全含/任含)+以选中闭合多段线为边界圈选。命令 圈选/交叉圈选/WP/CP。+1 单测，237 tests。commit `671f8bb`。
- [x] **三点测角(MANG)**：`AngleMath/AngleState`(顶点→两边取点出夹角 0..180°, 与测距同框架)。命令 角度/测量角度/MANG/ANG。+4 单测，241 tests。commit `0551d9c`。
- [x] **半径标注(DIMRADIAL)**：`DimTools.BuildRadial`(选圆/弧→径向线+箭头+"R值"文字)。命令 半径标注/DIMRADIAL；线性标注兼收 DIMALIGNED。经核 MockAiEngine/Ribbon 确为原程序功能。+2 单测，243 tests。commit `09f7b47`。
- [x] **连续标注(DIMCONTINUE)**：以上一条线性标注的第二点为起点链式接续(复用 DimTools.Build + _lastDimP2)。命令 连续标注/DIMCONTINUE。经核原 ribbon 确有。复用已测几何, 无新纯逻辑单测；build+run 验证。243 tests。commit `12023e0`。→ **标注三型齐**(对齐 DIMALIGNED / 半径 DIMRADIAL / 连续 DIMCONTINUE + 三点测角 MANG)
- [x] **夹点开关(GIZMO)**：`_gripsOn` 切换夹点方块显示/可拖(关时 HitGrip 直返 -1)。命令 夹点开关/GIZMO。经核原 ribbon(67/679)+AI 菜单确有。至此 **Home ribbon 命令集 100% 覆盖并忠实核对**。243 tests。commit `79e2d57`。
- [x] **批量台阶扩帮·几何核(MineAssLib)**：`BenchLines.Generate`(闭合境界按定距逐圈 miter 内偏移生成台阶顶线, 面积递减自交保护)。选中闭合多段线→生成多圈台阶线。命令 批量台阶扩帮/台阶线生成/BENCHLINES。+3 单测(方形均匀收缩/CW 绕向/自交停)，246 tests。commit `7993485`。**记录**：真实台阶距=W+H/tanα 需帮参数对话框(待接)；凹境界深偏移自交则提前停(不产病态环)；TIN/块体量算需内核。
- [x] **剥采比均衡·VP曲线(PlanLib)**：`VpBalanceSolver`(**原样移植**原 `PlanLib.StrippingBalance.VpBalanceSolver` 纯算法——DP 把累计 V-P 曲线分 K 段折线上界拟合、最小化超前剥离面积；`FitK/SegArea/SuggestK/BalanceY`)。命令 剥采比均衡/VP曲线/VPBALANCE：读分期物料量 CSV(采出量,剥离量)→累计曲线(青)+均衡折线(黄,过断点)+各段均衡比标注 上屏 + 段/峰值超前报表。+4 单测(线性0面积/凸曲线K=2精确/面积随K单调不增/BalanceY插值)，250 tests。commit `6a35f94`。**记录**：原自动回填走块体(LoadFromEntities)需内核；此以 CSV 分期量替代输入路径, 求解核完全一致。
- [x] **工作面线拟合(BlockModelLib)**：`WorkingFaceLineFitter`(**原样移植**原 `BlockModelLib.Domain.WorkingFaceLineFitter` 纯几何——PCA 定走向→沿走向切连通块丢孤立斑点→逐段局部趋势回归拉回/丢离群→等距重采样抹锯齿→回世界坐标折线)。命令 工作面线拟合/工作面线/WORKFACELINE：读露煤格中心 CSV(x,y)→修正后工作面折线段(橙)上屏 + 走向/段数/斑点·拉回·离群 报表。+4 单测(对角45°/东西90°走向·斑点丢弃·点不足降级)，254 tests。commit `9e270b3`。**记录**：原露煤格来自 10m 格网+现状面采样(需块体/内核)；此以 CSV 点替代输入, 拟合核完全一致。
- [x] **煤质统计(GeoDataBase)**：`QualityStatistics.Compute`(**移植**原 `CoalQualityService.StatsBySeam` 纯算部分——计数/均值/样本标准差(n−1)/min/max/线性插值 P25/P50/P75)。命令 煤质统计/质量统计/QUALITYSTATS：读 CSV(可选 煤层标签, 指标值)→按标签分组统计→报表。原 DB 取数(SqlLib/Repository)需 DM8, 此以 CSV 替代输入, 统计核一致。+4 单测(1~5 基本统计/百分位插值/单值0标准差/空)，317 tests。commit `1d74f1e`。
- [x] **工作帮坡角估算(MineAssLib)**：`SlopeEstimator`(**移植**原 `WorkingSlopeEstimator` 平面拟合核——点集中心化最小二乘拟合平面 z'=a·x'+b·y'，`SlopeAlongDeg` 沿方向坡角 / `MaxSlopeDeg` 最陡坡角)。命令 坡角估算/工作帮坡角/SLOPEEST：读面点 CSV(x,y,z)→拟合平面→最陡坡角报表。原沿工作线推进方向采现状面(需 TinSampler/工作线,内核)，此抽点集→坡角纯算。+5 单测(梯度还原/z=x最陡45°/沿x45°沿y0/平面0°/共线null)，322 tests。commit `1a24952`。
- [x] **台阶参数分析(PointCloudLib)**：`BenchAnalyzer.Analyze`(**移植**原 `PointCloudLib.BenchAnalyzer` 纯算——沿剖面(里程,高程)按坡度阈值分平盘/坡面段, 行程编码合并连续同类, 出 台阶高/坡面角/平盘宽/整体帮坡角)。命令 台阶参数分析/台阶分析/BENCHANALYZE：读剖面 CSV(里程,高程)→分析→坡面·平盘数/最大台阶高/平均坡面角/整体帮坡角 报表。原吃 ProfileResult, 此按 (里程,高程) 数组。+4 单测(阶梯分平盘坡面/坡面宽=水平投影/整体帮坡角/点不足)，326 tests。commit `d6587ce`。
- [x] **产量达成分析(TaskLib)**：`AttainmentAnalyzer.Of/Combine`(**移植**原 `TaskLib.AttainmentAnalyzer` gap 分解公式——工时缺口=计划班产×少干工时、效率缺口=实际工时×班产差；故障/检修按工时归因；多条 Combine 按原因合并)。命令 达成分析/产量达成/ATTAINMENT：读生产记录 CSV(计划量,实际量,计划工时,实际工时[,故障h,检修h])→逐行分解合并→达成率/缺口/归因 报表。原吃 `ProductionTask`(域对象)，此抽标量输入。+4 单测(工时缺口归故障/效率缺口/超额无归因/Combine合并)，330 tests。commit `6cee3ad`。**记录**：设备布置·运力不足 与 等待原因码 归因需 TaskLib 域模型(设备组/原因码), 未移植。
- [x] **车铲匹配(GeoDataBase)**：`FleetMatch`(**移植**原 `FleetOptimizer` 的 匹配系数 MF=车数·装车节拍/循环时间 + M/M/c **Erlang-C** 排队等待概率, ErlangC 逐字移植)。命令 车铲匹配/配车匹配/FLEETMATCH：读 CSV(卡车数,装车节拍min,循环时间min)→逐行 MF+等待概率+结论(铲待车/车排队/均衡)。原完整多规则 DP 优化+日产能模型需 DispatchRule 设备 POCO(域耦合), 未移植。+4 单测(Erlang-C c=1→ρ / c=2 a=1→1/3 / 饱和→1 / 匹配系数+结论)，334 tests。commit `26f9581`。
- [审计·基本抽尽] **业务模块纯算法核勘察收尾**：逐一核对剩余候选后确认——`ShiftCalendarBuilder`/`MonthSchedulePlanner`(班历/月排产) 产域实体 `ShiftCalendar` + 甘特/表格(需表格 UI + TaskLib 域)、`ComparisonBuilder`(境界方案比选) 吃 `PitScheme/PitResult`(需求解链) 产比选表(评分核与已移植 `ProgramComparer` 重叠)、`TinZSampler`/`MeshZSampler`/`SeamColumnSampler`(采样器) 系内部助手非独立命令(且虚拟钻孔已由 IDW `StartSpotQuery` 覆盖)。**逐字可移植、命令级、非污染输入的纯算法/几何核已抽尽(共 10 个)**；余者均 域对象/DB/表格 UI 耦合或内部助手, 记录待上机(需 TaskLib/GeoDataBase 域模型 + DM8 数据)。**补充脉络**：原由 C++ 内核算的**指标/度量**(该类只解析内核二进制输出)可用**托管从点集重算**同一组指标——此为另一条可做+可验证的脉络(下条即此)。
- [x] **点云质量统计(PointCloudLib)**：`PointCloudStats.Compute`(托管重算原 `PointCloudQualityStats` 指标——计数/包围盒/XY 投影面积/点密度/高程均值·总体标准差)。原指标由 C++ LasLib 内核算、该类仅解析其二进制输出(PMQS_MAGIC)；此以托管从点集重算, 指标口径一致。命令 点云质量统计/点云统计/PCSTATS：读点 CSV(x,y,z)→报表。+3 单测(包围盒/面积/密度/均值/总体σ · 空 · 平云0σ)，337 tests。commit `cd470c7`。
- [x] **点云去噪 SOR/ROR(PointCloudLib)**：`PointDenoise.Sor`(每点到 k 近邻均距 > 全局 μ+kσ 剔除, 相对离群)/`Ror`(半径内邻点数 < 下限剔除, 绝对稀疏)——托管重实现原 SOR/ROR 去噪(原走内核 IPointCloudCapability, 算法标准)。命令 SOR去噪/ROR去噪/SOR/ROR：读点 CSV→去噪→保留点入场景+报表(SOR k=8σ1、ROR 半径=对角/50·下限4 默认口径)。+4 单测(SOR剔远飞点/ROR剔孤点/点少不滤/空)，341 tests。commit `5b8ce3b`。
- [x] **点云高程着色(PointCloudLib)**：`Colormap.Sample`(**移植**原 `TinColormap` 锚点色线性插值核, 脱 WPF Color; 预设 地形/Jet/灰度)。命令 点云高程着色/高程着色/ELEVCOLOR：读点 CSV→按 z 归一用地形色带着色→彩色点入场景。原走内核 SetColormap LUT, 此以托管插值 + 逐点着色。+4 单测(端点锚点/灰度中点127/越界夹取/空白)，345 tests。commit `46d683a`。**记录**：SlopeLine 逐点坡度线提取为内核规模(DEM 栅格+陡带线追踪)；ZClip/框裁需范围参数对话框——均记录待上机。
- [x] **网格度量(MeshEditLib)**：`MeshMetrics.Compute`(表面积=Σ三角面积、有向体积=Σ四面体 a·(b×c)/6、包围盒) + `ParseOff`(OFF→3D verts+tris, 多边形面扇形三角化)。原网格体积走内核, 此以托管从 (verts,tris) 直算。命令 网格度量/网格面积体积/MESHMETRICS：选 OFF 网格→面积/体积/包围盒 报表。补齐 OFF 3D 网格读取(原 OffImportService 拍平丢 z/三角)。+5 单测(四面体体积1/6·面积1.5+√3/2·包围盒/OFF四边形三角化面积1/空)，350 tests。commit `eec3268`。
- [x] **网格诊断(MeshEditLib)**：`MeshDiagnose.Analyze`(纯拓扑——按无向边的三角关联数判 边界边(inc=1)/非流形边(inc≥3)；退化三角=重复索引或零面积；边界环/洞数=边界边图连通分量(并查集)；闭合(水密)=无边界且无非流形)。对应原 `MeshEditLib DiagnoseReport`, 原走内核, 此以托管从 (verts,tris) 直算。命令 网格诊断/网格检查/网格拓扑 + MESHDIAGNOSE：选 OFF 网格→边界边/非流形边/退化三角/洞数/闭合 报表。+5 单测(闭合四面体水密/单三角3边界1洞/共边四边形4边界/三三角共边=非流形/退化三角计数并跳过)，355 tests。commit `20758e7`。
- [x] **网格焊接(MeshEditLib)**：`MeshWeld.Weld`(**忠实移植**原 `MeshEditLib.Tools.MeshWeldMerge.Weld` 空间哈希核——格边长 H=2×容差、每点探本格+偏向侧 8 邻格(数学完备, 比 27 邻域省 ~3.4×)、按容差合并重合顶点、重映射三角索引、剔焊后退化三角、可选去掉不分缠绕的完全重复三角)+`ToOff`(焊接结果 OFF 序列化落盘)。原走内核, 此以托管从 (verts,tris) 直算, WeldVertex 逐字照搬。命令 网格焊接/合并顶点/顶点焊接 + MESHWELD/WELD：选 OFF→按包围盒对角×1e-4 容差焊接(开去重复)→落 .welded.off + 顶点/三角 前后计数 报表。+5 单测(重合点合并 6→4/焊后退化丢弃/去重复三角/容差(小不并·大合并)/ToOff↔ParseOff 往返)，360 tests。commit `cabf6b7`。**记录**：补洞/网格布尔并集仍为内核规模(需封闭 solid + CGAL 级几何), 记录待上机。
- [x] **网格边界环提取(MeshEditLib)**：`MeshBoundaryLoops.Extract`(**忠实移植**原 `MeshEditLib.Tools.MeshBoundaryLoops`——开放边(仅属 1 三角的无向边)按三角绕向串成有向闭合环(外轮廓+缝两帮+洞缘)；先 0.1mm 量化焊接再定边；汇聚顶点(尖灭/夹点非流形结点)按相对入边最靠顺时针的出边拆环, 保证每返回环为简单环(不自相交)；PickNext 角度比较逐字照搬)。命令 网格边界/边界环提取/提取边界 + MESHBOUNDARY：选 OFF→提取边界环→各环作绿色闭合折线(投影 XY)入场景 + 环数/点数 报表；闭合网无开放边则提示水密。+5 单测(单三角1环3点/共边四边形1外环4点/闭合四面体0环/双离散三角2环/环点落输入坐标)，365 tests。commit `b6fd2a0`。**记录**：三维放样成侧壁/封盖需内核几何, 此仅提取平面投影轮廓入 2D 场景。
- [x] **加密多段线(线编辑 POLYDENSIFY)**：`PolylineEdit.Densify`(逐段若长 > maxStep 则匀分插内点使每子段 ≤ maxStep, 闭线含闭合段)——托管重算原 CAD 端 `POLYDENSIFY`(原走内核 EngineInterop, 语义据 `PolylineOpsReports.PMPN` 报表: 处理数/原顶点/终顶点)。命令 加密多段线/加密 + DENSIFY/POLYDENSIFY：对选中折线按各自包围盒对角/50 自动取步长加密, 报 顶点 前→后(插入数)。+5 单测(线按步长分4段/步长超长不变/闭合方形含闭合段8点/各子段≤步长/退化返回原)，370 tests。commit `bb637ab`。**核对去重**：抽稀(POLYSIMPLIFY)已由既有 `SimplifyPolyline`(PolylineSimplify.DouglasPeucker) 覆盖——仅补「抽稀等值线」别名, **未重复实现**；闭合线裁剪 POLYCLIP(需环内外裁)、点/线落到面上 POLYPROJECT(需三维网格投影) 记录待做。
- [x] **两线交点(线编辑 POLYINTERSECT)**：`PolylineIntersect.SegSeg`(线段真交点, 参数均∈[0,1], 平行/共线跳过)+`Between`(两折线各段两两求交, 按容差去重)——托管重算原 CAD 端 `POLYINTERSECT`(原走内核, 语义据 `PolylineOpsReports.PMPI`: 交点数+新建 POINT handles)。命令 两线交点/求交点/线交点 + POLYINTERSECT：选中直线/多段线/矩形(经 AsSequence 抽成序列)两两求交, 各交点作红点入场景, 报交点数(去重容差=参与点包围盒对角×1e-6)。+7 单测(十字交于原点/平行null/段外null/端点接触计入/折线两次穿越/共点去重/闭合方形与对角线2点)，377 tests。commit `d217526`。**核对去重**：连接多段线(POLYJOIN)已由既有 `JoinPolylines` 覆盖(键 组合工作线/合并多段线/连接台阶线)——已补「连接多段线」别名(commit `0116d42`), 不重复实现。
- [x] **闭合多段线 + 删除重复点/线(线编辑 POLYCLOSE/POINTDEDUPE/POLYDEDUPE)**：`GeomDedup.KeepAfterDedup`(点集容差去重, 首现保留)+`SamePolyline`(两折线同一几何: 等长、闭合标记一致、正/反向逐点重合)——托管重算原 CAD 端 `POINTDEDUPE`/`POLYDEDUPE`(原走内核, 语义据 `PolylineOpsReports` PMTD/PMPD: 原数/删除数)。三命令均作用选择集: **闭合多段线**/闭合线 + POLYCLOSE(选中未闭合折线经 Replace 置闭合); **删除重复点**/点去重 + POINTDEDUPE(选中点按容差 1e-3 去重移除); **删除重复线**/线去重 + POLYDEDUPE(选中折线按几何去重移除)。+6 单测(点吸收重合/保留相异/折线正向匹配/反向匹配/闭合标记不同/点数或几何不同)，383 tests。commit `00ea2b8`。→ **线编辑组纯几何算子(加密/抽稀/连接/两线交点/闭合/删除重复点线)已抽尽**；余 统一线高程 POLYUNIFYZ(2D 场景折线无逐点 Z)、闭合线裁剪 POLYCLIP(环内外裁)、点/线落到面上 POLYPROJECT(需三维网格) 记录待做。
- [x] **粗糙度/曲率分析(点云 §二)**：**更正**——二者**已由托管网格法实现并接线**(此前误记待上机): `Roughness.Compute`(IDW 网格 3×3 邻域极差)命令 粗糙度/地表粗糙度、`Curvature.Compute`(IDW 网格拉普拉斯曲率, 蓝凸脊/红凹沟)命令 曲率/地表曲率。原走内核 PMSA(逐顶点写 mesh uv), 此以托管从高程点 IDW 成网格后按标准栅格定义重算(与坡度/坡向/高程着色同一"托管重算"脉络)。坡度/坡向/高程着色/两期算量由 `TerrainAnalysis` 覆盖。
- [x] **区域求差 + 重叠检测(短期计划 §七 确定可采区域)**：`RegionBool`(**忠实移植**原 `PlanLib.ShortTerm.RegionGeometry` 2D 版, 去 Z 平面拟合)——栅格布尔: 扫描线栅格化 + 8-邻接连通保最大块 + Moore 描边 + Douglas-Peucker 抽稀, 任意凹多边形稳健。命令 区域求差/可采区域求差/多边形求差 + REGIONSUBTRACT(选两闭合折线 subject∖clip 最大块作橙色新折线)、区域重叠检测/重叠检测 + REGIONOVERLAP(重叠面积占较小者 ≥2% 判成片重叠)。较既有 `PolygonClip`(仅凸交集) 更一般(任意凹差集)。+6 单测(重叠方形true/相离false/相邻边false/角减L形面积≈75/完全覆盖空/相离clip保原面积)，389 tests。commit `bcea9cb`。**记录**：原 Z 由 subject 平面拟合回填, 本 2D 场景略去。
- [x] **栅格形态学工具箱 + 平盘宽度识别(短期计划 §七 现场参数提取)**：`RasterMorphology`(**忠实移植**原 `LandformClassifier` 纯算子——膨胀/腐蚀/闭/开运算(8邻域迭代)、填内部孔洞、8-连通域(最小格过滤)、Moore 外轮廓描摹、Douglas-Peucker 抽稀)+`BenchWidthIdentifier`(**忠实移植**原 `PlanLib.ShortTerm.BenchWidthIdentifier`——台阶线加密栅格化→足迹+种子高程→最近种子 BFS DEM→局部高差场(可分离窗口 min/max)判平盘→开运算(半径=W目标/2)保留宽度≥目标条带→连通+面积过滤+描边, 附代表(内切圆=倒角距离×2)宽度)。命令 平盘宽度识别/现场参数提取/平盘识别 + BENCHWIDTH：读台阶线 CSV(lineId,x,y,z 分组)→识别达标平盘(默认 ≥20m, 本环境无参数对话框)作青色多边形入场景 + 面积/宽度报表。+10 单测(形态学: 膨胀成环/腐蚀去孤点/开运算保块去斑/填洞/连通域计数/最小格过滤/描边; 平盘: 方形识别宽度≥20+面积>1/空输入 Ok=false/非正目标拒绝)，399 tests。commit `bba5a83`。**记录**：地貌分类 `LandformClassifier.Classify`(采场/内外排) 为重启发式(多参数+趋势面+原始地面回调, 可测性弱), 记录待上机；wTarget 无对话框取默认 20m。
- [x] **道路横断面·弯道加宽/超高(运输 §五/§六)**：`RoadCrossSection`(**忠实移植**原 `MineAssLib.RoadLayout.RoadCrossSection`, GBJ22-87 阶段④)——弯道加宽 ε=车道数·L²/(2R)(R≤阈值才加宽)、超高 e=clamp(V²/(127R)−μ,0,e_max)、三点外接圆反算局部曲率半径、沿中线逐站汇总(最大加宽/超高/加宽段长)。命令 道路横断面/路面加宽超高/弯道加宽 + ROADSECTION：选中线折线→按曲率算加宽超高→按半宽沿法向偏移出左右灰色路缘线入场景 + 报表(默认 基宽15m·2道·V30·μ0.15·e_max8%)。+6 单测(加宽公式ε=0.72+阈值/超高公式10.197%+双向clamp/单位圆三点R=1/共线R=∞/直线不加宽/转弯加宽>0)，405 tests。commit `7a8e9a4`。**记录**：变宽路面带三维落地切面原在 C++ 侧消费, 本环境仅出左右路缘平面线。
- [x] **运距指标·坡阻折算+循环时间(运输 §六)**：`HaulMetrics`+`TruckProfile`(**忠实移植**原 `RoadLib.Routing.HaulMetrics`, 设计 §4 C 公式)——等效运距(上坡 1+k·g / 下坡 max(0.5,1−2|g|))、行车时间(上坡降速/下坡提速封顶 1.2×/下限 5km/h)、循环时间(装+运+卸+返+调车)、吨位加权平均/最大运距。命令 运距指标/循环时间/运距统计 + HAULMETRICS/CYCLETIME(`HaulRecordMetricsAsync`)：读运输记录 CSV(distanceM,gradePct,tons)→逐车折算+循环→加权平均/最大/等效总里程/平均循环 报表。+9 单测(平路恒等/上坡重车1.48/上坡空车1.192/下坡0.8+触底0.5/平路时间2.4min/上坡更慢/循环求和/加权平均1750+最大/空零)，414 tests。commit `cd6e37d`。**核对去重**：既有 `HaulMetricsAsync`(等效运距=场景路网寻径吨公里, 键 等效运距/运输指标/驱动距离) 是路网空间版, 此为按记录坡阻+循环时间公式版, 互补, 命名区分(`HaulRecordMetricsAsync`)不冲突。
- [x] **OD 运距矩阵(运输 §六 快速寻径)**：`RoadNetwork.DijkstraDistances`(单源全网最短距, 不可达 +∞)补齐 OD 矩阵基石。命令 OD运距矩阵/OD矩阵/运距矩阵 + ODMATRIX：读 OD 点 CSV(x,y[,name])→场景多段线建路网→各点 snap 最近节点→逐源单源 Dijkstra 得 k×k 距离矩阵→落 .odmatrix.csv + 可达对/平均/最大 报表。原 OD 矩阵仅有图标无实现, 此以既有 `RoadNetwork` 补齐。+4 单测(链上单源全距/不可达∞/无向对称/坏起点全∞)，418 tests。commit `13820cd`。**核对**：点对点寻径(`StartPathfind`+Dijkstra)已存在。
- [x] **备选路径·K 最短路(运输 §六 快速寻径)**：`RoadNetwork.KShortestPaths`(**忠实移植**原 `DijkstraPathSolver.FindKShortest` 的 **Yen 算法**结构——spur/root 分解、禁同 root 的第 i 条边、禁 root 中间节点)+`DijkstraExcluding`(排除节点/无向边)+`PathWeight`。索引图上按里程升序求 K 条不重复简单路径。命令 备选路径/K最短路/备用路径 + KPATH/ALTPATH：复用点对点两点取点→算 3 条最短路→青(主)/橙/黄依次高亮 + 里程升序报表。原为 RoadGraph 字符串 ID + 边属性(坡/载/状态)图, 此适配 `RoadNetwork` 索引距离图(距离权=原「按里程升序」口径)。+5 单测(菱形图两条排序/K=1只最短/不连通空/排除边绕行/三条升序 20·22·24)，423 tests。commit `cda45a0`。**记录**：坡阻/限载/限坡/单向 等边属性口径需 RoadEdge 模型, 本索引图仅距离权。
- [x] **路网校验·连通性诊断(运输 §六 路网构建)**：`RoadConnectivity`(**忠实移植**原 `RoadLib.Network.RoadConnectivity` 2D 版, 去 Z)——连通分量标号(BFS)+边中线加密网格哈希(格边=maxGap, 3×3 邻域)求两两片间最近点对(≤maxGap 按宽度升序)。作用于 `RoadNetwork` 索引图。命令 路网校验/连通性诊断/路网体检 + ROADVALIDATE/NETCHECK：场景多段线建图→报连通片数, 断开时列片间最窄可接缺口(品红线标注, maxGap=包围盒对角×0.5)。原依赖 RoadGraph/Point3d 域类型, 此适配索引图。+5 单测(单片连通/两片分离/两线5m缺口/超maxGap空/多缺口升序)，428 tests。commit `266e3fc`。**记录**：瓶颈最短路 PlanBridge(片级接桥链)可续做, 高差平接判定需 3D。
- [x] **螺旋/折返斜坡道中线(运输 §五)**：`RampCenterlines`(**忠实移植**原 `MineAssLib.RoadLayout.ParametricCenterlines`, 镜像 C++ GenerateRamp.cpp)——`Spiral`(定半径圆螺旋沿弧长匀降)+`Switchback`(直腿+180°回头弧往返逐腿降)。只出 XY 骨架+参考 Z(落地贴面/限坡/切帮为内核规模, 不在此)。命令 螺旋斜坡道/螺旋坑线/螺旋中线 + SPIRALRAMP、折返斜坡道/折返坑线/折返中线 + SWITCHBACK：于视图中心按默认参数(螺旋 R50·2圈·8%; 折返 3腿·腿长100·8%·回头R20)生成中线折线入场景。+6 单测(螺旋定半径/按弧长降/逆顺时针相反/无效空/折返逐腿降/无效空)，434 tests。commit `f7f30a7`。**记录**：AutoTurnSide(按坡面梯度定甩向)需 SurfaceSampler; 全落地链需贴面器+切帮内核。
- [x] **合并三角网 + 固化成体(建模 §三, 复用 MeshWeld)**：`MeshWeld.Concat`(多网带偏移拼接)+ 二命令均 拼接→`MeshWeld.Weld`→`MeshDiagnose` 自检——对应原 `MeshMergeBuilder`/`SolidifyBuilder` 共用的 `MeshWeldMerge.Weld`+`AnalyzeEdges`(前者已在本会话移植)。命令 合并三角网/网格合并 + MESHMERGE(多 OFF→拼接→焊接去重复三角→落 merged.off + 开放/非流形边报表)、固化成体/固化实体 + SOLIDIFY(多 OFF 顶/底/侧→拼接→焊接保缠绕→水密自检→落 solid.off)。+4 单测(Concat 索引偏移/共边两网焊 6→4/四面体拆4面焊回水密/空)，438 tests。commit `f55372a`。**记录**：侧面三角网 SideSurfaceBuilder 需两条 Z 分异折线(2D 场景折线共面), 记录待做。
- [x] **基本几何体·立方体/球体/圆柱(建模 §三)**：`PrimitiveBodies`(**忠实移植**原 `MeshEditLib.Tools.PrimitiveBodies` 的 Box/Sphere/Cylinder 生成器——均拓扑闭合、共享索引、外法线朝外)。命令 立方体/长方体 + BOX、球体/球 + SPHERE、圆柱/圆柱体 + CYLINDER：生成→SaveFilePicker 存 OFF + 顶点/三角/体积/闭合 报表(默认 立方体10³·球R5·圆柱R5H10)。接入本会话 OFF 网格生态(可再被度量/诊断/焊接/合并消费)。+5 单测(立方体闭合+体积24+表面52/球闭合+体积逼近4/3πr³/圆柱闭合+体积逼近πr²h/低分辨率夹取/偏心包围盒)，443 tests。commit `2b00655`。**记录**：原入库 ImportToEngine 走内核 PMBI, 此以 OFF 落盘替代。
- [x] **确定可采区域(短期计划 §七)**：`MineableAreaIdentifier`(**忠实移植**原 `PlanLib.ShortTerm.MineableAreaIdentifier`)——煤层底板三角网上找采煤台阶(质心 2.5D 网格 Z 重心插值采样, 取与自身平盘标高最近者)、算可采面积(鞋带)、对上覆各级算需揭露后退量(逐级步长=平盘宽+坡面水平投影 H/tanα)。命令 确定可采区域/可采区域/可采区域识别 + MINEABLEAREA：选煤层底板 OFF + 台阶线 CSV(lineId,x,y,z, 首末重合判闭合)→识别→采煤台阶环红色高亮 + 报表(默认 W20·H15·65°·berm5)。+5 单测(网格Z重心插值/环外null/鞋带面积/两环距/Identify选最近底板台阶+面积+上覆级数)，448 tests。commit `7cc8341`。**记录**：原从选中的场景网格/台阶线取输入, 此以 OFF+CSV 替代(2D 场景无 3D 网格实体)。
- [x] **侧面三角网·弧长拉链放样(建模 §三)**：`SideSurface.Loft`(**忠实移植**原 `MeshEditLib.Tools.SideSurfaceBuilder.LoftRaw`)——顶/底线按归一化累计弧长「拉链」缝成直纹面(开线端点配对/闭环绕向对齐+起点旋转、逐点推进取弧长参数较小侧、去相邻重复点)。命令 侧面三角网/侧面放样/放样侧面 + SIDESURFACE/LOFT：选顶线 CSV + 底线 CSV(x,y,z, 首末重合判闭环)→放样→SaveFilePicker 存 OFF + 顶点/三角/面积 报表。+5 单测(平行线成带4三角/矩形面积200/反向底线对齐/闭方环成管8三角/点太少空)，453 tests。commit `4ddcbce`。**记录**：原 Build 包 PMBI 走内核, 此以两条带 Z 的 CSV 折线替代场景选线(2D 场景折线共面无法分顶底)。→ **建模三角网构建三件套(合并/固化/侧面)+基本几何体 已补齐**。
- [x] **体素格网体积 + 广义缠绕数点在网格内(体编辑 §三/块体)**：`WindingNumberTester`(**忠实移植**原 `BlockModelLib.Domain.WindingNumberTester`)——GWN 点-mesh 内外测试(Van Oosterom–Strackee 有符号立体角 + 三角 BVH + Barnes-Hut 偶极远场近似, 对破洞/自交/非流形封闭体仍判内外, 阈值 0.5)。命令 体素格网体积/体素体积/体素算量 + VOXELVOLUME/VOXEL：选封闭 OFF→逐格中心 GWN 判内外→占用格数×格体积 = 体素体积(与散度定理精确体积对比, 格边=包围盒对角/60)。+5 单测(盒内 w≈1/盒外 w≈0/盒外快速排除/BVH(Beta2)与全精确(Beta0)判定一致/体素体积逼近盒体积)，458 tests。commit `e475d2f`。**记录**：原 VoxelVolumeBuilder 自适应细分 + MeshContainmentTester 为更完整版, 此以 WindingNumberTester(逐字) + 均匀网格出体素体积; GWN 基元亦可复用于 实体转块体/分割地质体 inside-test。
- [x] **实体转块体(块体模型 §三)**：复用 `WindingNumberTester`(GWN inside-test)+ `BlockModel.Block`/`BuildCells`——命令 实体转块体/网格转块体/体转块 + ENTITYTOBLOCKS/SOLID2BLOCK：选封闭 OFF→逐格中心 GWN 判内外→占用格作 Block(格边=包围盒对角/20, 总格>20万自动加粗)→配色方块入场景 + 存 `_lastBlocks`(可接 资源量/剥采比/筛选)。原走内核块体, 此为托管重算(GWN 体素化), 属"内核算→托管重算"脉络。+3 单测(盒体素化满格125/BuildCells 方块数=块数/体素块体积逼近真值)，461 tests。commit `3882377`。**记录**：原可带品位场/约束赋值, 此块体品位置 0(几何体素化); 属性赋值/品位插值需块体域模型。
- [x] **开采程序确定·工作线推进(生产计划 §七)**：`AdvancePlanner`(**忠实移植**原 `PlanLib.BoundaryOptimization.AdvancePlanner` 专利 MVP)——平行推进(沿方位平移 k·b)/定点回转(绕瞬心转 k·Δθ, Δθ=b/R_ref 转角守恒)/动点回转(逐顶点沿 advance 侧法向偏移 b, 采宽守恒)。纯平面几何。命令 平行推进/开采程序确定/工作线推进 + ADVANCE、定点回转 + FIXEDPIVOT、动点回转 + MOVINGPIVOT：选中折线为拉沟→方位取其法向、采宽=拉沟长/10→生成 8 步工作线(绿→红渐变)入场景。+5 单测(平行平移/动点直线偏移/定点回转半径守恒/无效输入空/平移保长)，466 tests。commit `6d7f972`。**记录**：模板场重整(发散插值/收敛合并)+真偏移(Clipper2)+采区转向衔接为原后续, 此为 MVP 三模式核。
- [x] **点落到面上 + 线落到面上(编辑 POINTPROJECT/POLYPROJECT)**：`MeshProjector.Drape`(复用 `MineableAreaIdentifier.SampleMeshZ` 2.5D 网格重心插值取 Z, 批量投影 + 计未命中)——托管重算原 CAD 端 POINTPROJECT/POLYPROJECT(原走内核逐点投影)。命令 点落到面上/点落面 + POINTPROJECT(选网格 OFF + 点 CSV→落 .draped.csv(x,y,z)+点入场景)、线落到面上/线落面 + POLYPROJECT(选网格 OFF + 线 CSV(lineId,x,y)→落 .draped.csv(lineId,x,y,z)+线入场景)。**先前记为 2D-Z 阻塞, 现解锁**：以 CSV+OFF 输入, Z 由网格采出并落盘(不需场景侧 Z 载体)。+4 单测(斜面 z=x 采样/未命中计数/空/XY 保留)，477 tests。commit `c5d09d7`。
- [第二轮普查·记录] **仍阻塞的 2D 场景 Z 项**：统一线高程(POLYUNIFYZ, 全顶点 Z=常数)/修改高程点(POINTSETZ, 点 Z=常数)/赋节点高程(AssignZByFormula z=aX+bY+c)——原为 `IPolylineOpsCapability`/`IEntityCapability` **对选中场景实体逐点 Z 的原地修改**, 但 Kylin 场景为 2D(折线=(x,y)、点=X,Y 无逐点 Z 载体)。前两者(置常数)脱离 3D 场景即无意义(CSV 形退化为电子表格填列); 赋节点高程可 CSV(x,y)→CSV(x,y,z=aX+bY+c) 但同为无场景载体的纯表格变换——记录待 3D 场景架构(逐点 Z 载体), 非算法缺失。竖曲线平滑 `ProfileSmoother.VerticalCurves`(83 行纯核) 弱映射(非 227 项独立按钮, 属斜坡道纵断面子算法), 记录备选。
- [x·部分] **演化对比 误接修复(运输 §六)**：已从点云 C2C 别名移除 `演化对比`(commit `8894be6`)——止损其错误执行点云比对。**真实现待专项移植**：`RoadLib.Evolution.RoadEvolutionAnalyzer`(323 行) 依赖深链 `GeometryIndex`(106)→`SegmentGrid`(86) + `PolylineMetrics`(Resample/SampleTangent/SubPolyline/Length2D) + `RoadEvolutionResult`(162: RouteEvolution/Ledger/RoadEvolutionClass/EvolutionSide) + `RoadEvolutionOptions` + RoadGraph/RoadEdge + Point3d，共 **6–7 文件 ~800 行**, RE1-8 逐段游程匹配规则深度交织。**已完成全链移植**(commit `04e006a`)：忠实移植原 RoadLib.Evolution 全链(~800 行/原 6 文件 → Kylin 2 文件 `RoadEvolutionModel.cs`+`RoadEvolutionAnalyzer.cs`, Point3d→Pt3、RoadEdge→EvoLine)——EvoSegmentGrid + EvoGeometryIndex(走向闸/端外闸偶极) + EvoPolylineMetrics(重采样/弧长子线/切向) + Analyze(逐采样点匹配→切游程 RE3→并短游程 RE5→按覆盖率分类 RE6, RE8 里程账不重不漏) + RouteEvolution/Ledger/Result/Options。命令 演化对比/路网演化/两期路网对比 + ROADEVOLUTION：读上期+本期中线 CSV(lineId,x,y[,z])→逐段分类(保持灰/移位橙/延拓绿/截短黄/废除红)入场景 + 里程账报表 + 落 .evolution.csv。**演化对比已接回真实现**(先前误接点云 C2C 已修正)。+7 单测(同线保持/横移6m移位/延长100m延拓/缩短100m截短/移除废除+新路新建/里程账配平/空)，473 tests。→ **两轮普查缺口全清**(survey1 9/10+超纲实体转块体; survey2 1/1 可映射; 演化对比大型待办亦攻克)。
- [x] **矿床识别·纯核(BlockModelLib)**：`DepositAutoDetector.Detect`+`JacobiEigen3`(**移植**原 `BlockModelLib.Domain.DepositAutoDetector` 几何核——煤单元中心 PCA→最小特征向量=层面法向→倾角/走向；Z 层游程估煤层数；Jacobi 3×3 特征分解逐字移植)。原类吃 BlockModel(需内核)，此抽纯函数吃煤单元中心点集。命令 矿床识别/自动识别/DEPOSITDETECT：读煤单元中心 CSV(x,y,z)→过质心走向线(紫)上屏 + 倾角/走向/煤层数/煤单元数 报告。+5 单测(水平层 dip0/45°倾斜面/双Z带 seams=2/点不足 null/Jacobi 特征值)，259 tests。commit `0414df8`。**记录**：原煤/岩判别(GuessCoal/分类列)与真实块体遍历需内核块体, 此以 CSV 煤单元中心替代输入, PCA/Jacobi/游程核一致。
- [记录] `TaskZoneSplitter`(任务区划分) 依赖 `DumpStripPlanner`(2426 行轨偏移几何)+`UnitRailFile.RailRow` → 非自足, 移植成本高, 暂跳过。
- [x] **方案综合对比(PlanLib)**：`ProgramComparer.Score`(**移植**原 `PlanLib.BoundaryOptimization.ProgramComparer` 纯算法——方向感知 min-max 归一 + 7 准则加权评分：峰值剥采比/基建剥离/达产年 低优，内排率/服务年限/储量均衡/NPV 高优；权重逐字照搬；推荐=最高分且可行, 全不可行退最高分)。原类吃 `MiningProgramPlan.Result`(需求解链)，此抽纯函数吃 7 项指标向量。命令 方案综合对比/方案比选/PROGRAMCOMPARE：读方案指标 CSV(名称+7指标)→排名+推荐 报表。+4 单测(全优100分/低优方向/单方案50/不可行让位)，263 tests。commit `ed1b521`。**记录**：指标由求解链(块体+排产)回填, 本环境以 CSV 指标替代, 评分核完全一致。
- [记录] **模块纯算法核扫描（本 tick）**：客观筛「小且仅 System/Linq」类后确认——余下多为 Result/Report DTO（无算法）或耦合大依赖网：`SectionBuilder`→PmbiWriter+Platform.Geometry、`BenchFaceBuilder`→Ep衔接图模型、`CenterlineJunctions`→RoadGraphBuilder、`EquipmentAssigner`(2111行)、`PolylineMetrics`(内部助手非命令)。网格剖面因 OFF 导入拍平为 2D 线框(丢三角/高程)无 3D 网格而不可行。**可干净移植的命令级纯算法核已基本抽尽**(VpBalanceSolver/WorkingFaceLineFitter/DepositAutoDetector/ProgramComparer)。
- [x] **DWG 导出·已验证**：新增 DWG 往返单测(SceneExport→DwgWriter→DwgReader 读回, 保留 Line/Circle/闭合多段线+图层)——坐实 ACadSharp DWG 编解码在本平台可靠, 修正旧"写入不可靠"记录。DWG 导入+导出至此均已验证。+1 单测，264 tests。commit `eb1dd0f`。
- [x] **.pmx 全类型往返补测**：SceneIO 颜色/图层统一存读已确认无 bug；补 Rect/Point/Text + 非默认颜色往返测试闭合覆盖。+2 单测，266 tests。commit `fc999b2`。
- [x] **LwPolyline/Polyline2D 凸度(bulge)→弧 · 保真修复**：`BulgeArc.Interior`(弦中点沿左法向抬矢高得弧顶→三点外接圆+过中点扫向插值)。**修复真实保真 bug**——此前含弧段的多段线被当直线弦导入(常见: 圆角边界/腰形孔)，现还原成弧；导入(可编辑+显示两路径)均已修。+4 单测(半圆点在圆上/零凸度直线/负凸度反向/弧顶) +1 端到端(DXF bulge→导入多点弧, 弧顶 y≈1)，271 tests。commit `ceefecb`。
- [x] **导入补 Solid/Face3D/Dimension · 保真**：此前静默丢弃——2D 实心(Solid)/三维面(Face3D)→角点闭合多段线轮廓(不填充)；**标注(Dimension 全子类)→爆炸其渲染块**(复用 Insert 的 Emit 递归, 尺寸线/箭头/文字随之还原)。命中 DXF 常见但原漏读的实体。+1 端到端(Solid→闭合四点多段线); Dimension 经块爆炸机制(同 Insert 已测); 272 tests。commit `1d86a58`。**记录**：Solid/Face3D 填充需 Skia; Hatch/MLine/Leader 仍未读(Hatch 边界可托管但多环+弧边界较繁, 记录待补)。
- [x] **导入补 Hatch 边界轮廓 · 保真**：填充实体取边界环为闭合多段线轮廓(不填充=Skia)。**忠实移植原 `DwgDxfImportService.ExtractHatchBoundaries`**——`Paths→Edges` 处理 Line/Arc(16段细分)/Polyline 三类边、剥首尾重合点、丢顶点<3 的环；别名 `AcHatch` 消歧本项目静态类 `Cad.Hatch`。+1 端到端(构造 4 边界线 Hatch→DXF→导入闭合四点多段线)，273 tests。commit `0b0c7df`。**记录**：填充本体/图案需 Skia；边界的 Ellipse/Spline 边(罕见)暂按原逻辑跳过。
- [x] **导入补 Polyline3D/XLine/Ray · 保真**：3D 多段线→多段线(取 XY)；构造线 XLine(无限)→过点**双向**长线段、Ray(半无限)→起点朝向长线段(±10000, 移植原 `DwgDxfImportService` 近似)。此前均静默丢弃。+2 端到端(Poly3D→3点多段线; XLine→长线段跨度>1e4)，275 tests。commit `92ab59f`。→ **DXF 实体覆盖对齐原程序导入器**(Line/Poly2D+3D/Circle/Arc/Point/Ellipse/Spline/Text/MText/Insert/Solid/Face3D/Dimension/Hatch/XLine/Ray)；仅剩 Mesh/PolyfaceMesh(3D 网格, 本项目 2D 平面场景无网格实体, 记录)。
- [x] **MText 格式码剥离 · 保真**：**忠实移植原 `StripMTextFormatting`**——去 `\H字高 \W字宽 \F字体 \C颜色 \A对齐` 等带参(到 `;`)码、`\L\O\K` 成对开关码、`{}` 分组括号、`\P`段落→空格；保护 `C:\Files` 类无 `;` 的合法反斜杠。此前 MText 直接用原串, 含格式码的文字把控制码当正文显示。+6 单测(字高+颜色+分组→纯字/字体码/合法反斜杠/段落→空格/无码/空串)，281 tests。commit `bbe7095`。
- [x] **文字旋转 · 保真**：`TextEntity` 加 `Rotation`(弧度, 绕锚点)；`Tessellate` 绕锚点旋转字形、`Apply` 并入仿射旋转分量(atan2(B,A), 同 PolygonEntity)、`Grips/MoveGrip` 保角；`SceneIO` 存读(N[3], 向后兼容旧文件)；导入 Text/MText 读 `Rotation`、导出写回。此前旋转文字被导成/绘成水平。+5 单测(旋转90°笔画落 +Y/水平向后兼容/Apply 合成/SceneIO 往返/DXF 往返保角 π/4)，286 tests。commit `dbf159a`。
- [x] **导出实体颜色 · 保真修复**：`SceneExportService.BuildDocument` 此前只赋图层不赋颜色 → 另存为丢失实体 RGB（红线导出变默认色）。现把场景烘焙 RGB 写为 ACadSharp 真彩色(`new Color(byte r,g,b)`)，与导入 `ColorOf` 的真彩色读取对称。+1 端到端(偏红线 导出→导入 颜色量化容差内保留)，287 tests。commit `02ec1a6`。→ **导入/导出双向颜色保真闭合**。
- [x] **.pmx 图层表持久化 · 保真修复**：此前 .pmx 只存实体, 重开时按实体名重建图层 → **丢失每层 冻结/锁定/显隐 状态 + 空图层**。新增文档级格式 `{L:[图层],Cur:当前层,E:[实体]}`(`SaveDoc`/`LoadDoc`, 按首字符 `[`/`{` **向后兼容旧数组 .pmx**)；`LayerTable.Restore` 整表恢复(含空层)；MainWindow 保存/另存/打开接线, 打开时新格式整表恢复、旧格式回退按实体重建。+4 单测(图层状态+空层往返/旧格式兼容/Load 读新格式/Restore 重建)，291 tests。commit `8d38937`。
- [x] **命令行 Enter 重复上次命令 · 交互**：**忠实移植原 Commands.cs 行为**——空命令行 + Enter 在**空闲态**(无进行中绘制/编辑/测量/交互, `CommandIdle()` 判)重复上次命令；派发前记录 `_lastCommand`(坐标输入已先返回不误记)。经核原程序确有(Commands.cs:166 "空命令行+执行键=重复上次命令 AutoCAD 行为")。+3 单测(RepeatCommand 解析: 空→上次/输入覆盖/无上次→null)，294 tests。commit `7b80d5f`。
  - **忠实性核实(命令行缺口 #5 余项)**：原程序 ↑/↓ 绑定的是**自动补全建议导航(MoveSuggestion)**、非命令历史调回；且原程序**不支持命令名后接参数**(Commands.cs:100 明示"参数改由命令执行后提示输入")。故"命令历史 ↑/↓"与"参数解析"**均非原程序功能, 不做**(记录, 避免不忠实新增, 同 linetype/PDF/DIVIDE 之例)。原程序真有的是**命令自动补全**(建议列表+↑/↓导航), 属 UI 较重项(纯过滤逻辑可测、弹出 UI 需上机)→ 记录待评估。
- [x] **导出 DXF 图层表颜色 · 保真修复**：`SceneExportService.BuildDocument(scene, layers)` 新增可选图层表参数, 建 DXF 图层时把场景图层色写为真彩色。此前导出图层表用默认色(与导入已读图层色不对称)。另存为传 `_layers`。+1 单测(偏红图层→导出 DXF 图层表颜色 R≈229)，295 tests。commit `a2d0da9`。**记录**：DXF 图层 冻结/锁定 标志的 ACadSharp 写入 API 未定, 暂只写颜色。
- [审计·无缺口] **导入/编辑保真复核（本 tick）**：逐项核查确认**已正确、无需改**——① `EllipsePoly` 已处理椭圆弧(StartParameter/EndParameter + 旋转 + 半轴比 + full 判闭合)；② `SplinePoly` 已按 De Boor 正确求值(闭合样条周期性回起点, 几何正确; Closed 标志未设属零效 nit)；③ 撤销覆盖：业务加实体命令均调 `BeginChange`, 报表类(如 VolumeAsync 仅报数值)无需。**导入实体几何 + 撤销一致性已周全**。
- [x] **剪贴板 COPYCLIP/CUTCLIP/PASTECLIP/PASTEORIG + ERASEALL**：`CadClipboard`(存选中实体克隆快照/取克隆+偏移, 深拷贝独立)。命令 COPYCLIP/CUTCLIP/PASTECLIP/PASTEORIG/ERASEALL(英) + 复制到剪贴板/剪切/粘贴/删除全部(中)。经核 README 命令表确有。粘贴原位克隆并选中(可随即移动)。+4 单测(克隆+偏移/深拷贝独立/快照不受原改动/空)，299 tests。commit `52231a3`。**记录**：交互式"指定粘贴点"需取点模式, 暂原位粘贴(PASTEORIG 语义); 另核 README——TRIMESH/ISOSURFACE(示例生成)、FILL(填充切换,需Skia)、SHADEMODE/LWDISPLAY(渲染/线宽,需内核) 仍缺, 已记录待评估/受阻。
- [x] **ORTHO 正交 + SNAP 栅格捕捉**：`DraftAids.Ortho`(从基点锁水平/垂直, 取偏移大的轴)/`Snap`(四舍五入到步长网格)。命令 ORTHO/SNAP(英) + 正交/栅格捕捉(中) 切换开关。取点统一走 `PickWorld()`(osnap 优先, 其后按开关应用 snap/ortho; **默认关 → 等价原逻辑, 零回归**)；ortho 基点取 `_lastInputPoint`。经核 README 命令表确有。+5 单测(正交水平/垂直/非零基点 · 栅格取整/零步长)，304 tests。commit `1356616`。**记录**：栅格步长暂固定默认(1.0), 可配置化待上机。
- [x] **ORTHO/SNAP 预览一致性 + 基点确认**：核实绘制工具点击走 `FeedPoint`(首行设 `_lastInputPoint`)→ **ORTHO 基点对 line/多段线绘制正确**(非上条所标"近似")。抽 `ApplyDraftAids` 供 `PickWorld`(落点) 与 `PointerMoved`(橡皮筋预览+坐标读数) 共用 → **预览与落点一致**(此前预览未应用 aids, 开 ORTHO/SNAP 会预览/落点错位); 坐标读数开辅助时标 [辅助]。默认关零回归。304 tests。commit `746d851`。
- [x] **TRIMESH 示例三角网**：`GenerateSampleTrimesh`(确定性 6×6 网格点 → 复用已测 `Delaunay.Triangulate`+`BuildEdges` → 三角边入场景, 无需文件)。命令 TRIMESH/示例三角网。经核 README 命令表确有。+1 单测(6×6 网格→≥30 三角+非空边)，305 tests。commit `7a86f8e`。→ **README 命令表非阻塞项全部覆盖**；仅余 FILL(需 Skia)、SHADEMODE/LWDISPLAY(需内核)、ISOSURFACE(需 3D 标量场+marching cubes) 环境受阻项。
- [x] **命名选择集 + 刷新 + 清理标记（Home XAML 按钮核对补漏）**：直接核对原 `MainWindow.xaml` Home tab 全按钮，补齐 **创建选择集/调用选择集**(`NamedSelections` 存实体引用/按名取/环绕轮转调回, 调回剔除已删)、**刷新 REGEN**(重建显示几何)、**清理标记 CLRMARK**(清高亮/捕捉标记)。命令 创建选择集/调用选择集/刷新/清理标记(中) + GROUP/SELSET/SELSETCALL/REGEN/RE/CLRMARK(英)。+4 单测(存取引用/覆盖/环绕轮转/空)，309 tests。commit `19ac39c`。**记录**：Home tab 仍缺且受阻/大UI 的按钮——AI助手(聊天面板)、切换窗口(多视图架构)、图案填充/填充/编辑填充(需 Skia)、打印、渲染配置(内核)、注册(达梦授权)、特性面板(属性编辑 UI)、标注样式(需标注样式系统)。
- [x] **特性(PROPERTIES) 读出**：`EntityProperties.Describe`(忠实对应原 `EntityPropertyBag` 属性模型——常规 类型/图层/颜色 + 各类型几何: 线 起点/终点/长度、圆 圆心/半径、弧 圆心/半径/起端点、矩形 角点/宽高、正多边形 圆心/半径/边数、点 坐标、多段线 闭合/顶点数、文字 位置/字高/内容/旋转)。命令 特性/属性/PROPERTIES/PR：单选实体→状态栏读出全属性。+4 单测(圆常规+几何/线长度/多段线闭合+顶点/文字内容+旋转)，313 tests。commit `efde22b`。**记录**：完整**可编辑属性面板**(原为 WPF PropertyGrid/ICustomTypeDescriptor)需 Avalonia 属性网格 UI, 属大 UI 增强, 待上机；此为纯数据读出版(脱 WPF)。
- [x] **流程调整（用户 2026-08-30）**：不再每 tick `dotnet run` 弹窗；每 tick 仅 `dotnet test`(headless)，功能整体做完集中跑 app。见记忆 batch-app-testing。
- [记录·受阻] **KDF(WeCAD) 导入/导出**：原 `KdfReader`(811行)/`KdfWriter`(417行) 是自足二进制编解码器(magic `wecad_bin_version_2021`, 逆向自单一样例), 依赖 System+CSMath(本项目 ACadSharp 已带), GBK 用 `CodePagesEncodingProvider` 原码已处理 → **可移植**。但**验证受阻**：无真 .kdf 样例, 逐字转写逆向格式仅靠 writer↔reader 往返对称是弱验证(原作者亦标 WeCAD 兼容"未验证")。给一个样例 .kdf 即可移植并按真实字节验证。原 .las/.blk/.osgb/.3dm/.3ds 同理需样例/大依赖。
## 十二、Shell 完整度补全（用户 2026-08-30 纠偏：视图空间/面板/命令行 半成品）

用户指出算法核虽全, 但**运行界面**(视图空间/面板/命令行)是半成品。经 AskUserQuestion 确认四方向全要, 逐一补全(每项独立提交, 477 tests 全绿, Debug+Release 双 0 错)：

- [x] **Home 开始选项卡 UI 补全** commit `79baef9`：多数功能有命令处理器但未上 ribbon 按钮/未接中文命令。补 命令别名(距离测量/对齐标注/原坐标粘贴) + ribbon 按钮(修改组撤销/重做; 新增 注释/测量/剪贴板 组)。
- [x] **右侧特性面板 + 可调分隔条** commit `3e5d15f`：右侧「特性」面板随选择实时显 `EntityProperties.Describe`(类型/图层/颜色/坐标/长度面积); 左右 GridSplitter 可拖拽调宽(此前左面板固定 220px)。经 HighlightSelection 驱动。
- [x] **标准视图预设** commit `da4742b`：`Camera.SetOrientation`+`CadGlViewport.SetView`——俯/仰/主/后/左/右 + 西南/东南/东北/西北等轴测, 视图组下拉。(视口本已有 网格/XYZ轴/朝向罗盘/轨道·缩放·平移/2D3D/范围缩放)
- [x] **对象管理器实时化 + 点选真选中** commit `fd8f13a`：`RefreshObjectTree` 从实时 `_scene` 按类型计数, `RefreshScene` 计数守卫增删刷新; 点类型节点→真选中该类全部实体(可编辑/看特性), 非仅高亮。图层面板经核已全交互。
- [x] **命令行 ↑↓ 历史 + 命令框转派中文命令** commit `3b72276`：↑/↓ 回溯历史; 命令框未识别→`DispatchRibbon` 转派整条中文命令链(修复命令框中文不可达真缺口——此前中文仅能点 ribbon)。
- [x] **命令输出/历史回显面板** commit `1ec5e36`：命令栏改 DockPanel, 上方可滚动命令日志(▸ 逐条回显, 上限100, `_suppressCmdLog` 防转派重复)。

**记录·待办**：基点粘贴(需拾取基点交互)、标注样式(需标注样式系统)、图案填充/编辑填充(需 Skia)、线宽/线型、渲染模式(线框→着色/实体, 需 GL 三角填充; 当前 2D 线框场景不适用)、AI 助手面板、多视图切换窗口。**UI 观感需用户跑 `dotnet run` 目视确认**(此环境看不到 Avalonia 桌面窗口)。

**§十二 续（同轮 shell 补全）**：
- [x] **状态栏正交/栅格捕捉开关接线** commit `a02d119`：正交按钮此前无名无接线(半成品), 现接 `_orthoOn`; 新增栅格捕捉开关接 `_snapOn`; 与命令/键切换 SyncDraftToggles 双向同步。
- [x] **基点粘贴** commit `52ffcf3`：`CadClipboard.Centroid()` 作参照, 拾取插入点→质心对齐粘入; ribbon 剪贴板组补按钮。§一 剪贴板五项(剪切/复制/粘贴/基点粘贴/原坐标粘贴)齐。+1 单测。
- [x] **命令自动补全** commit `3487aa9`：`CommandCatalog`(~90 命令)子串匹配显候选 + Tab 补全。
- [x] **范围缩放中文命令 + 上一视图(视图历史)** commit `f555c10`：`Camera.Snapshot/Restore` + 视图历史栈; 视图预设下拉补两项。

**四方向补全小结**(用户 AskUserQuestion 全选)：**命令行**(↑↓历史/中文派发/回显日志/自动补全/Tab) + **面板交互**(右侧特性面板/对象树实时化+真选中/图层面板/可调分隔条) + **新增面板**(命令输出日志) + **视图空间**(10 视图预设/范围缩放/上一视图 + 本有网格轴/罗盘/轨道缩放平移/2D3D) — 均已补。**受阻记录**：渲染模式(线框→着色, 需 GL 三角填充, 当前 2D 线框场景不适用)、多视图切换窗口(架构)、图案填充/线宽/线型(Skia/内核)、标注样式系统、AI 聊天引擎。UI 观感需用户 `dotnet run` 目视确认。

**§十二 死按钮审计（用户「很多功能没完成」→ 全 ribbon 按钮 vs 处理器交叉核对）**：
逐一核对所有 ribbon 按钮 Tag 有无命令处理器, 补齐「有按钮但功能已实现却没接线」的死按钮：
- [x] 编辑组 点/线/面/体/工具 大按钮 → Flyout 列子命令 commit `79efc3a`
- [x] 加载点云/展点(读点显示) + 工艺参数分析(→BenchAnalyzer) + 基本几何体→Flyout(立方/球/圆柱) commit `8e6b44e`
- [x] 2.5D TIN→创建三角网 / 三角网着色→坡度着色 / 采场参数识别→平盘宽度识别 / 采场排土场圈定→采场圈定 commit `c43f498`
- [x] 全开(图层全显) / 清除全部(→EraseAll) commit `076635f`

**剩余死按钮（环境受阻, 非「未做」而是「不可做」）**：
- **§四 地质数据库 整 tab**(煤质数据管理/统计分析/数据看板/空间分布/工艺架构定义/参数模板库/平盘工艺地图/现场验收录入/开孔坐标管理/展绘层位数据/设备信息管理/效能预测/数据分析/智能编组/生产数据/能力/机群总览/数据导入导出/检修档期) — **DM8/SQL 持久化**。
- **§八 日常生产组织 整 tab**(班次日历/作业面台账/编制配置/生产任务编制/作业区划分/周计划编制/钻爆计划衔接/生产任务书/任务下达/班组派工/调度态势看板/实绩录入/设备状态故障报修/工序进度跟踪/达成度评价/产量统计/质量配煤分析/生产任务动态调整·模拟/班内工艺工序推演/生产报告/去向台账/派车单/采排配对/采掘单元清单/量驱动采剥接续/驱动量/破碎站位置设置) — **DM8 + TaskLib 域调度**。
- **内核几何**：加载倾斜摄影/转化为三角格网/影像底图(osgb)、快速建模/地质体建模(kernel+对话框)、分割三角网/剔面/补洞/处理尖灭(MeshLib)、分割点云/坡顶底线提取/高程截断/法向估计(PointCloudLib 内核)、煤层露头着色/现状写实/补勘钻孔写实/更新煤层面(地质模型内核)。
- **§五 剥采排 PitDesign 内核**：参数化模板/局部台阶/最终并段/编辑台阶/动态调整/创建工作线/创建工程位置/排土模板·放坡·按量推进·容量校核/坑线落地·撤销/平盘联络道/结构路面/约束条件设置/运量驱动布线/手动标定线路/基础道路网络构建/中心线管理/画道路中线/直线坑线。
- **§七 规划域链**：采区划分/规划计算/派生计划方案/确定开采程序/月度·短期·中长远 计划编制·动态模拟/进度计划出图/刀量切割 —— 需 MiningProgramPlan+PitScheme+StripRatioField 域模型链(见 §十一 记录)。
- **§六 道路维护/表征**：路况显示/路网预览·更新·存档/增量增删边/边状态/时段快照/分色显示/运输指标报表(🚧) — 需路网图编辑+状态+属性。
- **UI/对话框**：渲染配置、点云管理、显示/隐藏、AI 助手(聊天引擎)。

## 十三、§七 生产计划求解链落地（原判「受阻·需域链」→ 最小 plan 洞察攻克）

**关键洞察**：PanelSplitter.Split / ProgramEvaluator.Evaluate **只用 MiningProgramPlan 的少数标量字段 + 静态公式**, 无需移全 428 行 MiningProgramPlan + 305 行 PitScheme 域链。以「最小 plan(实际用到的字段 + 默认值照搬)」+ StripRatioField(从稀疏块体按品位阈值聚合的托管采样) 化解。`PanelSplit.cs` 自足。

- [x] **采区划分** commit `813dc6e`：`StripRatioField.FromBlocks`(块体→剥采比场) + `PanelSplitter.Split`(**逐字**, 沿推进轴等煤量/等距切 N 采区, 逐采区 煤/岩/剥采比/工作线长/推进度/序/内外排/服务年限) + `MiningPlanParams`(最小 plan) + `MiningPanel` + `AdvanceRateFrom`。命令 采区划分/储量均衡划分：最近块体→划分→采区矩形(蓝→红=序)入场景+报表。+6 单测。
- [x] **规划计算** commit `255fc47`：`ProgramEvaluator.Evaluate`(**逐字**, 服务年限/峰值剥采比/储量均衡系数/基建剥离/内外排容量平衡(松散1.2)/达产时间/平均运距(煤量加权)/NPV(分期折现)/校核) + `ProgramResult`。经济口径原取 ProductionCostBook.Current, 此用典型默认(煤价300/采煤80/剥离20/折现8%)。命令 规划计算/开采程序评价。+3 单测。
- [x] **派生计划方案** commit(本次)：基于 Split+Evaluate 编排——按 采区数{2..6}×方位{0,90} 派生多方案→逐一评价→按 NPV 排名→推荐+前三 报表。命令 派生计划方案/多方案派生。
- 方案综合对比(ProgramComparer)已于早前会话移植(§十一)。

**记录**：确定开采程序(BoxcutAdvanceOption 打分, 需 BoxcutWeights.Recompute)可续；中长远/短期/月度 计划编制·刀量切割·进度出图 仍需 TaskLib 排产/内核几何。**采区划分参数(采区数/方位/产能/内排)本环境取默认, 无参数对话框**；剥采比场煤岩判别以品位阈值替代原属性分类器(块体仅品位)。
- [x] **确定境界·经济最优坑深** commit `debf7b2`：`ResourceProfileLite.FromBlocks`(块体→逐 Z 层煤/岩剖面) + `SectionSolver.SolveDepth`(**逐字**, 从顶向下逐层累加净值最大定坑底, 等价境界剥采比法 n经=(d−a)/b)。命令 确定境界/境界优化/最优坑深/经济境界(从原凸包别名重指到此)。+5 单测。

**§七 求解核抽尽小结**：优化开采设计(境界圈定 凸包/确定境界 经济坑深/采区划分/开采程序确定 AdvancePlanner/剥采比均衡 VpBalance) + 中长远(规划计算/派生方案/方案对比 ProgramComparer) 均落地。**余项受阻**：刀量切割(BlockModel 内核+对话框)、最终并段(BenchTemplateBuilder 300+行多调参, 内核邻接)、中长远·短期·月度 计划编制正流程·进度出图·动态模拟(TaskLib 排产/甘特/域实体)。

## 十四、CSV 数据源/几何等价 再挖掘（DM8/内核 项中数据来自 CSV 的分析可做）

洞察：§四/§八 中数据来自 **CSV 而非 DM8 表**的分析项、以及有 **几何等价**的项, 可做：
- [x] **煤厚分析**(钻孔煤层厚度) commit `486f7d4` · **统计分析→煤质统计 / 空间分布→克里金** commit `6ecefdc` · **达成度评价·产量统计→产量达成 / 质量·配煤分析→煤质统计** commit `6763762`
- [x] **时段快照**(导出路网纪元 CSV→喂演化对比) commit `5d224e2` · **位移监测 C2C→点云比对 / 画道路中线·手动标定→折线绘制** commit `f6b09b0`
- [x] **逐点坡度坡向 / 法向估计**(PCA 局部平面, 复用 JacobiEigen3) commit `30181fd`, +3 单测

**剩余 97 死按钮 = 两类, 均按「不满足验证/无法实现先记录」处理**：
1. **环境受阻(不可做)**：§四/§八 DM8 持久化(数据看板/信息管理/派工/调度/看板…)、C++ 内核几何(倾斜摄影/网格布尔修复/点云修复/地质模型更新)、§五 PitDesign 内核(台阶/排土/坑线落地)、TaskLib 排产(中长远·短期·月度编制·出图·模拟)、参数对话框(渲染配置/约束设置)。
2. **重实现忠实性无法验证(应记录, 不臆测)**：分割点云(原或为聚类/区域生长, 我的区域裁剪未必忠实)、高程截断(需 Z 波段参数)、采排配对/采掘单元清单/量驱动采剥接续(原算法不可见)、法向已做。→ 按用户「只做原程序有的命令、忠实移植」原则, 这些原算法不可见者不臆测替代, 记录待原码/上机核对。

**结论**：清晰且忠实可移、可验证的功能(算法核 60+、shell 四方向、§七 求解链、§四/§八 CSV 分析、点云表面属性、所有映射到已实现功能的 ribbon 按钮)均已补全。498 测试全绿, Debug+Release 双 0 错。

## 十五、功能→按钮 surface 补齐（命令在、缺入口的最后一批）

**洞察**：普查发现有一类不是「死按钮」而是「缺按钮」——功能命令已实现且可经命令行/命令面板调用, 但 ribbon 无对应入口(如同早前 Home tab)。核对原 PitMine3D 确有该组按钮 → 补入口是忠实的。

- [x] **「块体模型」ribbon 组**(§三 三维地质建模 tab) commit `1d5872b`：补 10 个已实现命令的大按钮——导入块体/实体转块体/约束块体/块体着色/筛选块体/切面剖切/体素体积/删除块体/导出块体/输出报告。忠实依据:原 `BlockModelLibPlugin` `groupBlock.AddButton` 确有此 12 键组。**余 2 项受阻记录**：`创建块体`(原经估值内核把地质模型插值入 3D 网格成块, 我的 `快速估值` 仅 2D 网格估值、`SceneBlock` 仅品位无多属性 → 需 3D 估值内核)、`属性赋值`(需多属性块体系统 + 对话框)。
- [x] **运输指标报表 复活** commit `1d5872b`：精确 Tag `运输指标报表` 与既有 `HaulMetricsAsync`(路网寻径版·吨公里/等效运距, =原 W6 图指标) 对齐——字符串错配致死, 加别名即活。

**死按钮精确复计(Tag 字面量在 .cs 完全不存在)：98(松)/91(精) → 172/263 功能按钮**。逐个复核确认剩余 91 全部四类环境受阻, 无遗漏的「字符串错配」或「功能在缺入口」可廉价复活项：
1. **TaskLib 排程(27)**：中长远/短期/月度/周 计划编制·动态模拟, 任务下达/生产任务书·编制·动态调整, 作业区划分/作业面·去向台账, 参数(化)模板(库), 实绩录入, 工序进度跟踪, 班组派工, 运量驱动布线, 进度计划出图, 采排配对/采掘单元清单, 量驱动采剥接续, 钻爆衔接, 驱动量。
2. **设备/调度 数据基建(17)**：数据看板/机群总览/调度态势看板, 检修档期, 派车单, 现场验收录入, 班次效能预测·日历, 设备 信息管理·效能预测·数据分析·智能编组·状态故障·生产数据·能力。
3. **C++ 内核几何/PitDesign(33)**：mesh 分割·补洞·剔面·尖灭, 点云 分割·坡顶底线, 倾斜摄影 加载·转格网, PitDesign 台阶·并段·坑线·联络道·排土·破碎站·结构路面, 地质模型 补勘·现状写实·更新煤层面, 煤层露头着色, 快速建模(内核+对话框)。
4. **对话框/DB/内核引擎/状态(14)**：刀量切割(DriveTemplateRunner 引擎), 展绘层位/煤质数据管理/开孔坐标(DB), 影像底图, 数据导入导出, 渲染配置/约束条件设置/点云管理/编制配置(对话框), 生产报告(生产数据), 确定开采程序(BoxcutWeights), 路况显示/边状态(路网状态), 隐藏(倾斜模型)。

**最终结论(本轮普查)**：clear + 忠实可移 + 本环境可验证的功能——算法核 60+、shell 四方向(视图/面板/停靠/命令行)、§七 求解链、§四/§八 CSV 分析、点云表面属性、块体编辑、以及**所有映射到已实现功能的 ribbon 按钮(含本次补的块体组 + 错配复活)**——均已补全。502 测试全绿, Debug 0 错。剩余 91 死按钮 = 四类环境受阻(TaskLib/内核几何/DB/对话框), 或原算法不可见者不臆测(忠实性), 均已记录待上机/原码。

**整组缺失复核(原 AddGroup 组标题 diff Kylin)**：原 34 组 vs Kylin, 仅 2 组 Kylin 无——
- **采矿模型**(BlockModelLibPlugin)：采矿模型(CarveStrip 需 IPitDesignCapability 内核 + 类型/宽度/倾角对话框, 阻) / 离散化模型(=实体转块体, 已入「块体模型」组) / 体积算量(=网格体积, 已在「三角网分析」)。2/3 与既有按钮重复、1 内核阻 → 不新增组。
- **数据库工具**(GeoDataBase)：DM8/SQL 持久化 → 阻。
其余 32 组 Kylin 均有。**至此整组、单键两级 surface 审计俱尽。**

## 十六、交互功能补齐：对象捕捉六模式（引擎功能→托管几何重算）

**洞察续 [[unlock-blocked-insights]]**：原走 C++ 引擎的**交互**功能, 若其算法是**标准可见几何**, 同样可托管重算(非仅分析算法)。对象捕捉即例。

- [x] **对象捕捉补 交点/最近/垂足**(commit `4a41cf9`)：原 PitMine3D OSNAP 六模式(端点/中点/最近/圆心/交点/垂足, `MainWindow.Ribbon.cs` 状态栏 6 bool → `EngineInterop.PitMine_SetSnapMode`)。Kylin 原 `Scene.SnapCandidates` 已覆盖端点/中点/圆心/象限, **缺交点/最近/垂足**。新增 `ObjectSnap.cs`(纯几何核, 六模式 + 优先级 + 位掩码) + `BuildSnapGeom`(场景实体→线段/圆/圆弧/点, 圆弧含定向修正) + 移动时合并(交点不比顶点远则取交点、最近/垂足兜底, 垂足锚=上一取点) + 标记显模式名。命令 对象捕捉/OSNAP·交点捕捉·最近捕捉·垂足捕捉·捕捉全模式。+10 单测。→ **OSNAP 已完整对齐原 6 模式**。

**修改命令核对(忠实)**：原 Ribbon 修改集 = 缩放/移动/旋转/打断 + 对齐标注(DIMALIGNED, 非 ALIGN 变换)。Kylin 已有 修剪/偏移/分解/复制/延伸/打断/旋转/移动/缩放/镜像 —— **已达且超**。**圆角/倒角/阵列/拉伸/极轴追踪原程序无 → 不臆造**(AutoCAD 特性, 非本矿业 CAD 命令)。

**交互功能小结**：夹点编辑(单选 MoveGrip)✅、窗口/交叉框选✅、对象捕捉六模式✅(本次补全)、正交/栅格捕捉✅、测量(距离/面积/角度)✅、TTR/SER 参照绘圆弧✅。核心 2D CAD 交互已忠实完整。

## 十七、绘制缺口补齐：图案填充（用户定义线剖面）

- [x] **图案填充**(commit `93e4b30`)：原绘制集(点/文字/填充/多段线/圆/矩形/正多边形/直线/圆弧)中 Kylin 唯缺「填充」。原走引擎命名图案库(`GetHatchPatternNames`, ANSI31/ISO 等**不可见 → 记录**), 但**用户定义线填充**(平行剖面线×角度/间距, 裁剪到边界=线几何)是标准可见几何, 且本 2D 线框渲染器**唯一可行**的填充形式(实心填充需 Skia, 已记录)。`HatchPattern.cs`(纯几何: 旋转坐标系→扫描线求边界交点→偶奇配对取内部区间→逆旋回原坐标; 支持十字交叉; 正确处理凹多边形) + `HatchBoundaryCmd`(选闭合多段线/矩形/正多边形→45°+自动间距 或 "图案填充 <角度> [间距]") + 绘制组「填充」按钮(icon_hatch)。+6 单测。
- **DXF Hatch 导入**：已提取边界环为闭合轮廓(commit 早前); 实心填充=Skia 阻; 用户可对导入边界手动执行 图案填充。精确重建 DXF 命名图案(多线族+偏移+dash)有忠实风险, 不自动臆造。

**本会话交互/绘制小结(502→518 测试)**：块体模型组(10 键 surface) + 运输指标报表复活 + **对象捕捉六模式补全**(交点/最近/垂足) + **图案填充**(绘制唯一缺口)。均为原程序确有、标准可见几何、可单测的功能。核心 2D CAD(绘制/修改/捕捉/交互)已忠实完整。**余受阻**同前(DM8/C++ 内核 mesh·点云·地质模型/TaskLib 排程/对话框基建/引擎命名图案库·实心填充需 Skia)。

## 十八、面板交互补齐：特性面板可编辑

- [x] **特性面板改值重建实体**(commit `4cf7b3d`)：用户点名「面板·交互功能」——原 PitMine3D 特性面板可编辑, Kylin 原仅只读展示。与夹点(鼠标拖)互补: **数值精确编辑**是夹点做不到的。`EntityProperties.WithEdited`(纯核可单测)按行标签重建同类型实体(复制色/层): Line 起点/终点·Circle 圆心/半径·Rect 角点·Polygon 圆心/半径/边数·Point 坐标·Arc 起点/端点·Text 位置/字高/内容/旋转 + 常规 图层/颜色。派生量(长度/宽/高)只读; 负半径/坏色/解析失败拒绝并还原。`UpdatePropertyPanel` 可编辑行渲 TextBox, 回车/失焦提交→`_scene.Replace`+重高亮。+8 单测。

**面板交互小结**：对象树(实时+真选中)✅ · 特性面板(展示+**编辑**)✅ · 图层面板(显隐/冻结/锁定/改色)✅ · 命令输出面板✅ · 右键上下文菜单(视图/网格/清高亮)✅。四大面板均可交互。**余**: 选择集右键子菜单(功能在命令 SELSET/SELSETCALL, 仅缺右键入口)、渲染配置/点云管理(对话框基建阻)。

**本会话累计(502→526 测试, +24)**：块体模型组 surface · 运输指标报表复活 · **对象捕捉六模式** · **图案填充** · 平移命令 · **特性面板可编辑**。经六轴(ribbon 两级两向 / 修改命令 / 绘制命令 / 对象捕捉 / 命令注册表·引擎侧不可见→ribbon 为准 / 面板交互)走查, clear+忠实可见+可验证的功能集已确认穷尽。

## 十九、右键菜单选择集入口（确认原上下文菜单缺口）

- [x] **右键「调用选择集」动态子菜单**(commit `3bac2f6`)：原 PitMine3D 视口右键菜单(CadContextMenu)含保存选择集列表可点选调用; Kylin 有命名选择集功能(`NamedSelections` + 创建/调用命令)但右键无入口。`ContextMenu.Opening` 动态重建子菜单列各命名集(名+项数)→点选 `RecallSelSetByIndex` 恢复选中+高亮+刷新特性面板; 空时禁用提示。同「功能在、缺入口」模式。

**本会话累计 7 项功能补齐(502→526 测试)**：块体模型组·运输指标报表复活·对象捕捉六模式·图案填充·平移命令·特性面板可编辑·右键选择集。**均为原程序可见确认、标准几何/托管可做、可验证**。

**确认原上下文菜单余项**：`转Select`(选择模式)= Kylin 空闲态默认即选择模式, ESC 取消当前工具等价, 不新增(冗余)。

**六轴走查终结**：ribbon(两级两向)·修改命令·绘制命令·对象捕捉·命令注册表(引擎侧不可见→ribbon 为准)·面板/右键交互——**clear+忠实可见+可验证的功能集已穷尽**。余 91 死按钮 + 少量对话框/DB/内核项确系环境受阻(DM8/C++ 内核 mesh·点云·地质模型/TaskLib 排程/对话框基建/引擎命名图案库/Skia 实心填充), 或原算法不可见按忠实性不臆测——均已记录。

## 二十、DXF 导入保真：补 Leader/MLine + 导出保真核对

- [x] **DXF 补 Leader(引线)/MLine(多线)**(commit `69bf560`)：原导入器覆盖 17 类实体(Line/LwPolyline/Polyline2D·3D/XLine/Ray/Arc/Circle/Point/Ellipse/Spline/Text/MText/Solid/Face3D/Dimension/Hatch/Insert), Leader/MLine 落 default 丢弃。二者皆折线几何(标准可见)→ 托管读: Leader→顶点折线; MLine→中心线顶点折线(偏移线族需 MLineStyle, 记录)。显示缓冲(Emit→Seg)+ 可编辑实体(Finalize→Polyline)双通道 + CnTypeName 计数。+1 单测。现覆盖 **19 类**。
- **导出保真核对**：`SceneExportService` 已按类型映射原生 DXF——LineEntity→Line·CircleEntity→Circle·ArcEntity→Arc(真圆弧起止角)·PointEntity→Point·TextEntity→Text·Polyline→LwPolyline·Rect/Polygon→闭合折线。**无导出保真缺口**(非压平为线段)。

**记录·DXF 导入余项**：MultiLeader(多重引线, 嵌套 context/content 块 + landing, API 繁)、Wipeout(遮罩)、Tolerance(形位公差框)、Mesh(多边形网格, 走 3D 网格路径非 2D 线)、MLine 偏移线族。均较罕见或需深解析, 记录待需时补。

**本会话累计 8 项功能(502→527 测试)**：块体模型组·运输指标报表·对象捕捉六模式·图案填充·平移命令·特性面板可编辑·右键选择集·DXF Leader/MLine。

**追加**：MultiLeader(多重引线) 亦补(commit `49f10c9`)——`ContextData.LeaderRoots→Lines→Points` 逐引线折线。DXF 导入现 **20 类**。余 Wipeout(遮罩)/Tolerance(形位公差框)/Mesh(3D 网格走另路径) 系罕见/异路径, 记录待需时补。

**本会话累计 9 项功能(502→527 测试)**：块体模型组·运输指标报表·对象捕捉六模式·图案填充·平移命令·特性面板可编辑·右键选择集·DXF Leader/MLine·DXF MultiLeader。I/O 层核对完整: 导入 20 类·导出保类型(SceneExportService)·.pmx 存载全 8 类+色+层。**均原程序确有/标准可见几何/可验证**。

## 二十一、智能助手停靠面板（纠正误判：非 LLM 受阻，实为菜单引擎）

- [x] **智能助手面板**(commit `d7798b7`)：**纠正之前误记**——原「AI 助手」面板用的是 `MockAiEngine`(287 行, 规则/对话树引擎, **非 LLM**), 我早前多处记为"AI 助手(聊天引擎)受阻"是错的。实为**菜单驱动引导助手**: 分类菜单(绘制/编辑/标注测量/视图/帮助)→点选即执行对应命令 + 使用帮助 + 自由文本兜底。完全可托管、可单测, 且用户明确点名「面板·新增停靠面板」。
  - `AssistantEngine.cs`(纯核, 忠实菜单树) + 右侧 col4 停靠面板(特性上/助手下, GridSplitter 分隔; 内容区 + 选项按钮 + 输入框) + 抽取 `ExecuteCommandToken(cmd)`(英文命令 switch, 命令框与助手复用)。+8 单测。
  - **记录**: 原 MockAiEngine 的打字机延迟动画/ChatMessage 气泡样式为观感, 此以即时渲染 + 简洁按钮列表替代(功能等价)。

**⚠ 前文"受阻清单"更正**：§十二/§十四/§十五等处列的「AI 助手(聊天引擎)」**移出受阻**——它不需要聊天引擎, 已按菜单引擎忠实实现。

**本会话累计 10 项功能(502→535 测试)**：块体模型组·运输指标报表·对象捕捉六模式·图案填充·平移命令·特性面板可编辑·右键选择集·DXF Leader/MLine·DXF MultiLeader·**智能助手面板**。

## 二十二、TaskLib 分类更正（托管非内核，但大域+持久化+对话框）

受 AI 面板误判纠正启发, 复查最大受阻块 **TaskLib 排程(27 死按钮)**：
- **更正**：TaskLib **是托管 C#**(`Modules/TaskLib/Domain` + `Engine` + `Simulation`), 非 C++ 内核。`TaskExploder.Explode`/`TaskRescheduler`/`ShortTermLink`/`FlowAssigner`/`DispatchEngine` 皆纯托管算法(仅 System + TaskLib.Domain, 无 EngineInterop)。我早前记"TaskLib 内核受阻"不准确。
- **但仍非单 tick 可做**：全域 **~20,170 行**深度互联(ProductionPlanContext 1428 / FlowAssigner 1032 / DispatchEngine 987 / ShortTermLink 952 / TaskExploder 971 / ExploderConfig 561 / Domain 各 400-800…), 且 `TaskPersistence`(DM8 持久化)、`Features/*.xaml.cs`(对话框)受阻, `SampleTaskBoard`(427 行样本数据可作 CSV 源)。§七 最小 plan 之所以能一击是因求解器只读少数标量; TaskLib 的 ExploderConfig(561 行)+ 富结构域输入不适用最小 plan。
- **结论**：TaskLib 忠实移植是**独立大工程**(多 tick/多会话级), 非本 loop 单步。仓促部分移植会因未移部分而失真, 违「原不可见/不完整者不臆测」。**记录为"托管可移·大域·待专项"**, 区别于真内核阻(mesh/点云/倾斜摄影)。已达成度评价/产量统计等 CSV 可做项早已落地。

**受阻清单再校准**：真内核阻(C++ EngineInterop 无托管源)=mesh 布尔/修复·点云内核·倾斜摄影·地质模型更新·PitDesign 台阶/坑线; 托管可移但大域/受持久化=TaskLib 排程链; DM8 阻=§四/§八 持久化; 对话框阻=渲染配置/约束设置等; Skia 阻=实心填充; 引擎图案库=命名 hatch。

**TaskExploder 输入复杂度定论**：`ExploderConfig.FaceInput` 字段极富(Zone/工程位ID/单元ID/可采储量/推进方位/采宽/源XYZ/物料码/物料构成 Mix/目的地 Sink/运距/等效运距/Splits/日目标/煤质/设备编组/铲型偏好/工序… + DrillInput + ShiftWindow + MaterialMix + CoalQuality + EquipmentGroup)——**非最小 plan 可移**(§七技巧因求解器只读少数标量才成立), 需 DM8 域数据填充。故 TaskExploder(生产任务编制/分解) 确定**非 tick 尺度可做**, 记录待专项。可移的核心算法(车铲匹配 MatchFactor/ErlangC)早已落地。

**穷尽性定论(本会话终)**：多维走查(交互/绘制/面板/导入/导出/存载) + 复核受阻分类(AI 面板真解锁、TaskLib/Mesh/PointCloud 逐一核实)后——**tick 尺度内 clear + 忠实可见 + 可验证** 的功能集已穷尽。余项二分: ①**大域专项**(TaskLib 排产链, 托管但 20k 行 + 需 DM8 数据 + 对话框, 多会话级); ②**确凿受阻**(C++ 内核 mesh 修复·分割/点云 native PMTB/倾斜摄影/地质模型/PitDesign, DM8 持久化, 对话框基建, Skia 实心填充, 引擎命名图案库)。均已记录, 按忠实性不臆测。

## 二十三、★重大解锁：§四/§八 数据层是 SQLite 非 DM8（根本性纠正）

**贯穿全程的最大误判纠正**：§四 地质数据库 / §八 日常生产组织 的持久化, 我(及 memory、前文多处)一直记「需 DM8 受阻」。**读源发现：原 PitMine3D 的 `SqlLib` 是纯 SQLite**(Microsoft.Data.Sqlite 8.0.10 + Dapper, `ConnectionFactory`→`SqliteConnection`, `SqliteDialect`, **无任何 DM8/达梦/Oracle 代码**), `GeoDataBase` 带 **50 个迁移**(V001 建 15 表 + V002~V050 灌**真实矿山种子数据**: 真实车队/产能/KPI/生产/故障/月计划/验收/地质/煤质/钻孔…, 共 42,389 行 SQL)。**SQLite 嵌入式跨平台, 本机(Windows)全可跑**——DM8 从来不是必需, 是我把"上机生产用 DM8"误当成"开发/功能也需 DM8"。

- [x] **SQLite 数据基座**(commit `667687d`)：csproj 引 Microsoft.Data.Sqlite+Dapper(NuGet 缓存已有); 50 迁移 SQL **逐字复制**入 `src/Data/Migrations` 作嵌入资源(2.7MB); `GeoDatabase.OpenSeeded`(忠实精简 MigrationRunner: `_schema_migration` 历史表跳过已应用、按版本序、迁移期关外键同原)。+4 单测(50 迁移全应用/15 表建成/种子非空/文件库幂等)。
- [x] **首批 §四/§八 功能**(commit `6233e4b`)：`GeoDataQueries`(查询核) + 3 命令——设备台账概览(总数/分类/在役)、设备生产数据统计(记录/产量/工时/故障/作业率)、产能分析排名(累计产量 Top×型号)。+3 单测(对真实种子库)。

**★受阻清单重大重写**：§四/§八 **移出 DM8 受阻** —— 数据层 SQLite 本机可跑、自带真实数据、可单测。剩余 §四/§八 死按钮(达成度/煤质/故障/KPI/月计划/编制/看板…)中, **纯查询分析类**皆可在此基座上续接落地; 仅 **CRUD 对话框**(录入/编辑窗)受对话框基建阻(但读侧分析全可做)。这是 ~45 个原判"DM8 阻"按钮的实质解锁通道。

## 二十四、§四/§八 读侧分析批量落地（SQLite 基座上，12 功能）

数据实测(种子库行数)远比 grep 显示丰富(INSERT OR IGNORE 多行): equipment 518 · equipment_model 50 · production_record 10,098 · capacity_monthly 12,546 · equipment_kpi_monthly 12,628 · fault_event 1,602 · dispatch_rule 40 · borehole 241 · borehole_seam_result 778 · coal_sample 257 · coal_seam_def 7 · parameter_acceptance 156 · process(系统8/工序26/模板3) · working_face 5 · monthly_plan 5 等。

**已落地 12 个 §四/§八 读侧分析功能**(GeoDataQueries + 命令, 均 +单测, 读真实种子)：
| 功能 | 命令 | 数据 | 提交 |
|------|------|------|------|
| 设备台账概览 | 设备信息管理/设备台账 | equipment | `6233e4b` |
| 设备生产数据 | 设备生产数据/设备数据分析 | production_record | `6233e4b` |
| 产能分析排名 | 产能分析/设备能力 | capacity_monthly | `6233e4b` |
| 故障分析 | 故障分析/设备状态·故障报修 | fault_event | `ab5a49c` |
| KPI 分析 | KPI分析 | equipment_kpi_monthly | `ab5a49c` |
| 钻孔管理 | 钻孔管理/钻孔统计 | borehole+seam_result | `01373dc` |
| 煤质统计 | 煤质统计/煤质数据管理 | coal_sample | `01373dc` |
| 煤层管理 | 煤层管理 | coal_seam_def | `01373dc` |
| 设备智能编组 | 设备智能编组/调度规则 | dispatch_rule | `4ae3bfa` |
| 工艺架构定义 | 工艺架构定义/平盘工艺地图 | process_* | `4ae3bfa` |
| 现场验收录入 | 现场验收录入/参数验收 | parameter_acceptance | `4ae3bfa` |
| 作业面台账 | 作业面台账/采场参数 | working_face | `4ae3bfa` |

**§四/§八 死按钮攻克进展**：原判 ~45 个"DM8 阻", 已实质解锁 12 个读侧分析(数据本机自带真实种子, 全可单测)。**余下二分**: ① 更多读侧分析(月度计划达成/blast/更多地质剖析) 可续接同法; ② **CRUD 录入/编辑窗**(录入、改台账、审批流) + **看板图表**(数据看板/调度态势看板/机群总览, 需图表控件) 受对话框/图表 UI 基建阻——但**数据与读侧分析已通**。

## 二十五、§四/§八 续接至 17 功能 + 钻孔展绘(DB→场景)

再补 5 功能(commit `0517253`/`6417660`)：参数模板库(parameter_definition 29+template 42)、月度计划(monthly_plan, 只读; 编制走 TaskLib 阻)、路况显示(haul_road 6)、边坡设计(slope_design 4)、**展绘钻孔(borehole 241 孔位→PointEntity 入场景, DB 与 CAD 场景打通的首个可见几何功能)**。

**§四/§八 读侧分析累计 17 功能**(556 测试内含 ~20 数据层单测)。**原判 ~45 "DM8 阻"按钮, 已实证解锁 17 个读侧功能**。剩余分三类:
1. **看板图表**(数据看板/调度态势看板/机群总览) —— 需图表控件 UI(数据与聚合已可查, 仅缺可视化控件)。
2. **TaskLib 工作流**(生产任务书/编制/下达/派工/派车单/去向台账/采排配对) —— 托管大域 20k 行, 数据层现已通可重估, 仍大工程。
3. **CRUD 录入/编辑窗**(实绩录入/台账编辑/审批) —— 对话框基建阻(读侧已做)。
+ 少量niche 读表(coal_observation_point 119/coal_classification 16/mine_location 10)可同法续接。

**里程碑意义**：本会话把"§四/§八 整两 tab 受阻(~45 按钮)"这一**最大误判**推翻——数据层 SQLite 本机自带 42,389 行真实种子, 读侧分析全可做可验证。这是整个移植可做范围的**最大一次扩张**。

## 二十六、§四/§八 读侧收官(23 功能) + 死按钮 91→72

再补 3(commit `42a2192`): 煤层台阶参数(seam_bench_param, =原 CreateSeamBenchParamCommand)/设备约束条件(equipment_constraint 15)/煤质分级规则(coal_grade_rule 15)。**§四/§八 读侧分析累计 23 功能, 带数据的读侧表基本全覆盖**。

**精确死按钮 91→72**(总 Tag 264)。§四/§八 攻克 ~19 个。剩余 72 分类:
- **TaskLib 工作流(~20)**: 中长远/短期/月度/周 计划编制·动态模拟, 任务下达/生产任务书·编制·动态调整, 作业区划分, 工序进度跟踪, 班组派工, 派车单, 去向台账, 采排配对, 采掘单元清单, 量驱动采剥接续/驱动量, 钻爆衔接, 进度出图, 编制配置, 班内推演, 生产报告, 实绩录入。→ 托管 20k 行大域, 数据层现已通可重估。
- **C++ 内核 mesh/点云(~12)**: 分割三角网/剔面/补洞/尖灭(MeshData 内核), 分割点云/坡顶底线(native PMTB), 增量增删边, 倾斜摄影 加载/转格网/影像底图, 快速建模。
- **PitDesign 内核(~14)**: 中心线/工作线/工程位置, 坑线落地·撤销·直线, 局部/编辑台阶·最终并段, 平盘联络道, 排土场 容量·按量·放坡·模板, 破碎站, 结构路面, 延拓触发, 运量驱动布线, 确定开采程序。
- **地质模型内核(~4)**: 更新煤层面/现状写实/补勘钻孔写实/煤层露头着色。
- **§四/§八 余(~8)**: 效能预测/班次效能预测(**对话框+滑块情景模拟器**, 基线=近期均值, 交互 what-if 受阻)、数据导入导出(通用)、检修档期/班次日历(**表无种子**)、月度计划编制/生产报告(TaskLib)、约束条件设置(对话框)。
- **对话框/状态(~5)**: 渲染配置/点云管理/边状态/隐藏/2.5D。

**§四/§八 收官结论**: 数据层(SQLite 真实种子)+ 读侧分析(23 功能)全落地可验证。余下是 交互式对话框(效能预测/录入/审批)、TaskLib 编制工作流、C++ 内核几何——三类确凿受阻或大工程, 非数据问题。

**§四/§八 种子表全覆盖收官**(commit `9453b42`)：再补 展绘观测点(coal_observation_point 119, DB→场景可见几何 + 煤厚统计)/采区列表(mine_location 10)。**§四/§八 读侧累计 25 功能, 所有带种子数据的表已全覆盖**(558 测试)。空间几何表(road_centerline/mineable_region/load_unload_point 等)为用户创建用、无种子, 需 CRUD 对话框(阻)。**§四/§八 读侧分析彻底收官**——余全是 交互对话框(效能预测滑块器/录入/审批)、TaskLib 编制工作流、C++ 内核几何, 均非数据问题。

## 二十七、§四/§八 收官 + 效能预测/数据导出（死按钮 70→69）

- 设备效能预测(commit `4b0721e`): 忠实原 EquipmentForecastWindow 基线口径(近期产能均值+KPI 均值→投影年产), 交互 what-if 滑块标注需 UI。复活 设备/班次效能预测。
- 数据导出(commit `7a3f712`): `ExportTableToCsv`(可测,防注入) + 16 张 §四 表→CSV。数据导入导出 的导出半; 导入模板对话框记录。

**§四/§八 彻底收官**：读侧分析 26 功能 + 效能预测 + 数据导出, **所有带种子表 + 可读侧计算全覆盖**, 死按钮降至 **69**(总 264, 功能按钮 195)。

**剩余 69 死按钮 = 三类, 每类已逐一读源核实(非"没找对路径")**：
1. **大工程托管港(需多会话专项)**: TaskLib 排产链(20k 行, 无干净单-tick 切片—— MonthlyShiftDecomposer 也需 ~1500 行域模型+输入+消费端); QuickModelBuilder(1000 行 + Estimation 依赖)。→ 勿仓促 partial(违"只考虑可用功能")。
2. **C++ 内核几何**: mesh 修复/分割(MeshData)、点云 native PMTB、PitDesign(IPitDesignCapability)、倾斜摄影、地质模型更新 —— 逐一验证确 native, 无托管源。
3. **交互对话框/无种子**: 录入/审批窗、渲染配置、约束设置、看板图表(数据+聚合已可查, 缺可视化控件)、检修档期/班次日历(表无种子需 CRUD)。

**本会话最终**: ~502→560 测试。CAD/交互层 + §四/§八 整数据层(SQLite 解锁)全落地。剩余唯一"技术可做但超单-tick"的是 TaskLib/QuickModel 大港, 余皆环境阻塞。均已诚实记录, 按忠实性不臆测、不做失真 partial。

## 二十八、"先读源再判"再攻克：补洞(三角网)(死按钮 69→68)

- [x] **补洞(三角网)**(commit `acea33b`)：原走 C++ 内核(MeshData), 但**填洞算法是标准几何**——提开边→有向边界环→质心扇形三角化封闭。`MeshHoleFill.cs`(纯几何可单测, 复用托管 verts/tris + MeshDiagnose 前后开放边校验) + 补洞命令(选OFF→填→落 filled.off)。+3 单测(开顶盒 边界边 4→0 水密/封闭不变/空安全)。同 OSNAP/图案填充 脉络: 引擎 op 若算法标准可见→托管重算。

**mesh 内核 op 再核实**: 补洞✓(标准填充已做); **剔面(三角网)**=PointCloudLib 障碍剔除(native ObstacleFilter)受阻; **分割三角网**=沿多段线鲁棒切割+重三角化(复杂, 无法目视验证正确性, 不做失真近似); **处理尖灭**=地质域特定。→ 补洞是清洁标准算法, 余 mesh op 或 native 或复杂重三角化。

**死按钮 68**(总 264, 功能按钮 196)。剩余 = TaskLib 大港(~18) / 复杂几何港(分割三角网·QuickModelBuilder) / native 内核(剔面·点云·PitDesign·倾斜摄影·地质模型) / 交互对话框(~13)。

## 二十九、mesh 标准几何再攻克：分割三角网 + 快速建模（死按钮→65）

"先读源再判" + 组合已验证 primitives, 再攻克 2 个原判"内核"的 mesh op:
- [x] **分割三角网**(commit `86b8b96`)：三角形-竖直面裁剪(选中折线首末点定切面)——顶点符号距离分左右, 跨界三角在交点裁子三角。`MeshPlaneSplit.cs` +4 单测(**面积守恒**/半空间归属/无穿越/退化)。直线精确, 曲折线弦近似。
- [x] **快速建模**(commit `68cad45`)：原 QuickModelBuilder 1000 行, 但 BuildFromMeshesDirect(2 面→体)可**组合已移 primitives**: 提两面最大边界环(MeshBoundaryLoops)→侧壁放样(SideSurface.Loft)→顶+底+侧焊接(MeshWeld)→水密自检(MeshDiagnose)。+1 单测(顶方+底方→焊成水密盒 开放边=0)。原估值建模路径(样本克里金入格)仍需 Estimation, 未移。

**mesh 标准几何 op 全清**: OSNAP/图案填充/补洞/分割三角网/快速建模 —— 引擎 op 若算法标准可见即托管重算/组合。

**死按钮精确计正为 65**(早前 67/68 因计数脚本按空格拆多词 Tag[如"2.5D TIN"]虚高; 2.5D TIN 实已处理)。剩余 65 = **TaskLib 排程 ~22 / PitDesign 内核+对话框 ~16(IPitDesignCapability: 工作线/坑线/台阶/排土场/联络道/破碎站/结构路面) / native 内核 ~10(点云分割·剔面·坡顶底线 PMTB·倾斜摄影·地质模型更新) / 交互对话框+路网状态 ~7**。清洁标准几何/数据驱动的增量项已尽; 余为大工程港(TaskLib)、域内核(PitDesign/point-cloud native)、交互对话框(录入/审批/渲染配置)。

## 三十、复用已移算法再复活（死按钮→62）

不必新写算法, **复用已验证的托管基元**给死按钮出读侧/计算:
- [x] **中心线管理/边状态**(commit `011a8fd`)：场景折线→`RoadNetwork.Build`→拓扑报表(中线/节点/边/总长/断头/交叉/孤立)。原为管理·状态对话框, 出只读拓扑视图(增删边/改状态需交互 UI 记录)。
- [x] **排土场容量校核**(commit `6e1b33a`)：原义=排土设计面 vs 现状面 填方体积=总容积。复用 `TerrainAnalysis.TwoEpochVolume`(现状,设计)→填方=容量。(设计面生成走 排土场放坡[IPitDesign 内核], 此吃两面 CSV 出容积。)

**死按钮 62**。剩余按类(复核确凿): TaskLib 排程 ~22 / PitDesign 内核几何+对话框 ~14(创建工作线·工程位置·坑线落地·撤销·直线·局部/编辑台阶·最终并段·平盘联络道·延拓触发·排土场放坡·按量·模板·破碎站·结构路面·确定开采程序·刀量切割·运量驱动布线) / native 内核 ~10(点云分割·剔面·坡顶底线·倾斜摄影·地质模型更新·煤层露头着色) / 交互对话框 ~5(渲染配置·点云管理·约束设置·实绩录入·隐藏[倾斜])+ 增量增删边(交互)。**清洁可做/可复用增量项趋尽; 余为大工程港(TaskLib)、域内核几何(PitDesign/IPitDesignCapability)、native、交互对话框。**

## 三十一、★启动 TaskLib 专项（逐步移+测，第 1 增量：量核算）

用户坚持"补全功能"+ 循环增量式 → **正式启动 TaskLib 大域专项**，按 域基础→引擎→DB 输入→排产功能 逐步移，每步带单测（算法层可验，UI 待上机）。

- [x] **第 1 增量：量核算 TaskQuantity**(commit `5abe56b`)：忠实逐字移植 `TaskLib.Domain.TaskQuantity`(单据侧量呈现: OD1 按工序取自己那本账[穿孔控制方量/采装原位实方/运输承运吨/排土占容]、OD2 无出处写「—」不写 0、OD3 分账合计不给总数) + **最小域**(ProcessType/ProductionTask/DrillInfo, 照 §七最小 plan 法, 仅含量核算读到的 ~11 字段, 不移全 474 行 ProductionTask 的调度/状态/时序)。命令 **生产量核算**: 读任务记录 CSV → 分账合计(穿孔/采装/运输[含运输功·加权运距]/排土)。+7 单测。此层是 生产任务书/任务下达/派车单 三窗的量呈现基础, 以 CSV 喂不依赖调度引擎。

**TaskLib 港路线图**: 量核算✓ → [下] 物料规格(MaterialSpec)/汇节点(SinkNode) → 分解器输入(FaceInput 从 DB working_face/seam_bench_param 构建) → TaskExploder/ShiftDecomposer 引擎 → 排产功能(生产任务编制/派工/台账)。TaskLib 死按钮(~22)在全链通后成批复活; 量核算是增量 1(基础层, 本身不直接映射死按钮, 但为其奠基 + 出独立 生产量核算 功能)。575 测试。

## 三十二、TaskLib 专项增量 2-3（物料规格 + 物料流/采剥平衡）

自足域逐字移植，每增量带 单测 + 独立分析功能：
- [x] **增量 2 物料规格 MaterialSpec**(commit `e142600`)：物料目录(6 类默认规格 ρ/Ks/Kr) + 混采 MaterialMix(份额拆吨量/占容 + 煤占比) + MaterialCatalog(CodeFromText c4/rh→码) + SinkKind。命令 **物料换算**。+6 测。
- [x] **增量 3 物料流/采剥平衡 MaterialFlow**(commit `3b77f1d`)：MaterialFlow(六元组→吨量/占容/运输功) + PeriodBalance(**采出/剥离/剥采比/排弃/内排率/总运输功/吨量加权运距**)。命令 **采剥平衡**(读物料流 CSV → 全指标报表)。+5 测。忠实: 未错配到 量驱动采剥接续(排产接续)。

**TaskLib 已交付 3 增量，均自足 + 可测 + 出独立功能**(生产量核算/物料换算/采剥平衡——真露天矿生产分析)。域基础 TaskQuantity/MaterialSpec/MaterialFlow 通。**下**: SinkNode(汇容量) → 面输入 FaceInput/EquipmentGroup/ExploderConfig(富域, 无独立功能=纯基础) → TaskExploder 引擎(生产任务编制) → DB 输入 + 排产功能。586 测试。

## 三十三、TaskLib 增量 4-5（汇节点/排土推进 + 煤质/配煤）+ 自足域提尽

- [x] **增量 4 汇节点 SinkNode**(commit `ef98e36`)：去向本体(库容占容方 Kr/剩余/填充率/接纳/**按量推进距离 d=V容/(工作线×台阶高)**) + SinkRegistry。命令 **排土场按量推进**(复活死按钮)。+5 测。
- [x] **增量 5 煤质/配煤 CoalQuality**(commit `a19d751`)：灰/热/硫/水 + MeetsTarget 达标判定 + Blend(按吨量加权混合=标准)。命令 **配煤核算**(读配煤 CSV→混合煤质)。+3 测。

**★TaskLib 自足域 FEATURE 已提尽 5 个**(量核算/物料规格/采剥平衡/排土按量推进/配煤核算——均自足、可测、真露天矿分析)。594 测试, 死按钮 61(排土场按量推进 已复活)。

**TaskLib 余下 = 引擎链共享基础(纯基础, 无独立功能) + 引擎**:
- Dispatch(779, 派车/任务实例/派工/实绩 数据结构) + full ProductionTask(474) + ExploderConfig/FaceInput(561) —— 引擎共享域, **纯基础无独立功能**(违"只考虑功能"), 但为 生产任务编制/派工/台账(~22 死按钮)的必经路。
- 引擎: TaskExploder(971 面→5工序)/HaulDumpDeriver(448)/FlowAssigner(1032)/DispatchEngine(987)/MonthlyShiftDecomposer(658, 需 BlockModelLib 空间) —— 各 500-1000+ 行, 用共享域。
→ 引擎类 TaskLib 功能需先移 ~1000 行纯基础 + 引擎(多 tick 无 feature), 是大工程。自足域已尽。

## 三十四、TaskLib 增量 6：工序进度跟踪（安全独立切片，复活死按钮）

- [x] **工序进度跟踪**(commit `2c674fe`)：忠实移植 DrillQuantity(穿孔孔数/延米 + 达成率延米优先) + ProcessProgress(按工序聚合 计划vs实绩 达成率)。命令 **工序进度跟踪**(复活死按钮): 读任务 计划/实绩 CSV → 各工序 条数/平均达成率/达标数。**安全**(独立文件, 不动已工作的 5 功能)。+3 测。

**★可持续模式确立**：移独立 TaskLib 域片(不 refactor 已工作代码) + CSV 喂的核算 → 每 tick 安全交付 1 TaskLib feature。已 6 增量: 生产量核算/物料换算/采剥平衡/排土场按量推进/配煤核算/**工序进度跟踪**。597 测试, 死按钮 60。

**引擎类余项**(生产任务编制/派工/派车单/任务下达/采排配对/动态模拟/编制…) 仍需引擎链(TaskExploder 等 + full ProductionTask/ExploderConfig 共享基础, refactor 风险 + 保真难验), 记录待专项/上机。自足+可 CSV 喂的 TaskLib 核算功能持续提取中。

## 三十五、TaskLib 增量 7-8 + 自足计算提取完成（8 功能）

- [x] **增量 7 车铲循环产能 FleetCycle**(commit `aea701b`)：忠实 FleetMatcher §3-5(斗数→节拍→循环 T_c[复用 HaulMetrics]→最优车数 n*→匹配系数 MF→铲装/车队能力→编组产能 q=min/ρ实·η)。命令 编组产能。+4 测。
- [x] **增量 8 按环节降效 LinkDerate**(commit `f2fbf1c`)：忠实 WeatherFactorFor(采装面用周期分解还原铲装/车队两侧分别降,**运输降效对铲瓶颈面不生效**)。命令 环节降效。+5 测。
- [x] **配煤达标**(commit `c9daddf`)：BlendStandard(灰≤12.8/热≥21.5/硫≤0.7) + MeetsTarget → 配煤核算加达标判定。

**★TaskLib 自足计算/公式提取完成——8 个功能**(生产量核算/物料换算/采剥平衡/排土场按量推进/配煤核算[+达标]/工序进度跟踪/车铲循环产能/按环节降效)，全在 `src/Cad/Tasks/`，忠实逐字/公式移植 + 全单测(~45 测) + 复用已移基元(HaulMetrics/FleetMatch)，均安全不 destabilize。607 测试, 死按钮 60。

**TaskLib 余下 = 引擎管线(无自足切片)**：数据结构(ViolationCodes/PlanViolation/Dispatch/TaskInstance/full ProductionTask 474/ExploderConfig 561, 纯基础) + 引擎(TaskExploder 971/HaulDumpDeriver 448/FlowAssigner 1032/DispatchEngine 987/MonthlyShiftDecomposer 658)。引擎类死按钮(生产任务编制/派工/派车/台账/动态模拟 ~20)需先移 ~1000 行共享基础(feature-less, 且 refactor 已工作代码有风险) + 复杂引擎(971 行管线本机无法比对原输出→保真难验)。→ 触"不满足验证条件先跳过、无法验证先记录"：**引擎管线记录为大工程边界, 自足计算已尽**。

## 三十六、§四/§八 分析视图续补 + ★核对种子揭 2 真实 bug（85 功能, 616 测试）

沿 SQLite 基座续补**互异**管理分析视图，并以"核对种子实际值"纪律做一次 latent-bug 审计：

- [x] **班次产量对比**(commit `0e77b44`)：production_record 按班次 产量/工时/作业率降序（找高效班次）。命令 班次产量对比/班产对比。
- [x] **设备KPI趋势**(commit `c53dd7f`)：equipment_kpi_monthly 按年 平均可用率/利用率时序。比率自适应 0..1/0..100。命令 KPI趋势。
- [x] **产能分类对比**(commit `3178c07`)：capacity_monthly join equipment.category 按类型(铲/车/钻) 累计产量+台数+占比降序（产能主力，区别于个体机排名）。命令 产能分类对比。
- [x] **故障类型分布**(commit `29ef2a8`)：fault_event 按 fault_type 事件数/停机时/占比降序（故障构成）。命令 故障类型分布/故障构成。
- [x] **分工序验收合格率**(commit `29ef2a8`)：parameter_acceptance join process_phase 按工序合格率升序（薄弱环节在前）。命令 分工序验收/工序验收。
- [x] **★fix 验收合格率恒0**(commit `29ef2a8`)：种子 parameter_acceptance.status 实为英文枚举 `pass/warning/fail/pending`(V011 CHECK)，原用中文 `'合格'/'通过'` 匹配恒 0。改 `status='pass'`(种子约66%)。
- [x] **★fix 设备在役计数恒0**(commit `fd7bada`)：诊断种子 equipment.status 分布 = **在用477/待报废26/租赁8/报废6/退租1(无NULL)**，原用 `IN('在役','运行','正常','服役') OR IS NULL` 无一匹配 → GetEquipmentRoster.InService 恒 0(未测未暴露) + GetEfficiencyForecast.ActiveEquipment 恒 0 被 `(active>0?active:1)` 兜底成 1 台(投影年产严重低估但假绿)。依原 EquipmentStatus 枚举改 在役=在用+租赁。回归断言锁定。
- 审计另核对 `borehole_seam_result.status LIKE '%尖灭%'`：种子确含 尖灭=2(分布 正常650/未达30/不取芯23/…)，**正确无 bug**。

**教训**：凡 WHERE/CASE 里假设的字面量(status/type/枚举/中英文)必须先诊断种子实际值再定；测试若只验 InRange/>0 可能放过"恒 0/恒兜底"的 latent bug —— 关键计数应断言其**语义量级**(占多数/>1)。见 memory [[verify-seed-enum-values-before-filter]]。

**审计续（commit `e8a2390`/`3bc01cf`）——单位/量级/排名全表核对**：
- [x] **fix 月度计划显示真实剥离量**(`e8a2390`)：种子 monthly_plan 仅 `plan_strip_wan_m3`(≈1041万m³)有值，`plan_coal_wan_t`/`ratio_strip_coal` **均空**——原显示恰只读这两空列。加 PlanStripWanM3 显示 + 剥采比稳健推导(存值>0 用存值，否则 剥离/煤，均无则0[种子如实0，未来数据自动算])。
- [x] **锁定 dispatch 排名有意义**(`3bc01cf`)：efficiency_score 种子 55~86/20不同值/40全active，断言 Top[0].Score>0 防退化假绿。
- 单位核对（诊断种子实测量级，全部正确无误）：coal_sample 灰 ad_raw 7.9~83%·硫 std_raw 0.01~9.5%·热 qnet_ad 4.8~29MJ/kg·挥发 vdaf_raw 23~90%(均百分数/MJ，显示正确)；KPI availability/utilization 存 **0..1 分数**(`<=1→×100` 启发式正确)；shift=A/B/C 各3366(`{Shift}班` 正确)；生产作业率~96%；钻孔深 avg199/max482m。
- JOIN 行乘积核对：3 处 JOIN(capacity×equipment / acceptance×phase)均针对 PK 列(equipment_id/phase_id, 1:1)，无膨胀。

**★阶段定论**：§四/§八 读侧分析**功能全面覆盖 + 正确性审计完成**(本会话共 5 视图 + 2 latent bug 修复 + 1 空列纠正 + 排名/单位/JOIN 全核对)。可加的互异视图已尽(再加即 reslice 充数)，高风险查询模式(枚举/比率/排名/JOIN/单位)已逐一诊断种子验证。**doable+可验证+忠实的功能集至此完成**；实质余项均属已记录边界：TaskLib 引擎管线(§三十五/三十七，大工程+端到端不可验) / C++ 内核几何(mesh修复·点云·倾斜摄影·地质·PitDesign) / §四§八 CRUD 录入审批对话框(GUI 本机不可验)。

## 三十七、★实读 TaskExploder 源码——确证引擎管线为多周专项边界（非行数畏难）

按「先读源再判」纪律，实读 `Modules/TaskLib/Engine/TaskExploder.cs`(971 行) 而非仅凭规模判定，结论**更硬**：

- **`TaskExploder.Explode(ExploderConfig cfg)`** 产 `ExploderResult{Tasks:ProductionTask[], Violations:PlanViolation[]}`，内部链：`ApplyWorkOrganization`(作业组织)→`ApplyBlendConstraint`(配煤约束重分配)→`DeriveDumpTargets`(采排守恒)→逐面 `ExplodeFace`(班次装箱)→穿孔/检修任务。
- **关键**：它消费的 `cfg.Faces`(FaceInput) 带 `HasCycleBreakdown`、编组周期分解(τ_L/T_c)、配煤输入、瓶颈侧等**预计算字段**——这些由庞大上游链构建，非 DB 直取。
- **全貌**：TaskLib/Engine = **17,588 行 / 43 文件**。构建合法 ExploderConfig 的上游 = `ProductionPlanContext`(1428)+`FlowAssigner`(1032)+`HaulResolver`(897)+`MonthlyShiftDecomposer`(658)+`ProcessZoneFaceSource`(545)+`SinkRegistryLoader`(781)+`FaceLedgerLoader`(383)+`ShiftPlanAssembler`(539)… 端到端出真任务须移 ≈**6k–10k 行**。
- **为何仍是边界(而非"可逐字移")**：① 逐字复制可保真、不变量测试可抓转录错——但**规模是多周专项，非 loop-tick 增量**；② 只移 TaskExploder 得无法喂入的空壳；③ 自造简化 ExploderConfig-builder = **发明原程序没有的流量分配逻辑(违"勿发明")** 且无原输出可比对(端到端本机跑不了原 WPF→**不可验**)。→ 命中"不满足验证条件先跳过、无法验证先记录"。**记录为大工程边界，不在 loop 内启动**。自足计算切片已尽 8 个(§三十一~三十五)。见 memory [[unlock-blocked-insights]] 第7条。

## 三十八、★60 死按钮完整归类——逐一「先读源再判」，确认无漏掉的可做命令

精确重算(264 唯一 Tag，`while IFS= read -r` 防多词分割，交叉核对 .cs 中 `"TAG"` 字面量)：**LIVE=204 / DEAD=60**。逐一核对(含对可疑者实读原实现)，60 个全部落入三大已记录边界，**无一是被误判的简单可做命令**：

- **A. TaskLib 排产引擎/对话框（28）**：生产任务编制/生产任务书/生产任务动态调整/任务下达/班组派工/派车单/生产报告/去向台账/实绩录入/编制配置/采排配对/采掘单元清单/运量驱动布线/量驱动采剥接续/驱动量/钻爆计划衔接/进度计划方案出图/周计划编制/月度计划编制/短期生产计划编制/短期进度计划动态模拟/中长远进度计划编制/中长远规划动态模拟/生产任务动态调整/动态调整/班内工艺·工序推演/检修档期/班次日历/作业区划分(WorkZoneLayoutWindow)。→ 需 §三十七 的 17,588 行引擎链 + 对话框。
- **B. C++ 内核几何（15）**：分割点云(native PMSG)/剔面(三角网)/坡顶底线提取(native PMTB)/转化为三角格网/点云管理/加载倾斜摄影/影像底图/隐藏(IObliqueCapability.SetVisible)/现状写实/补勘钻孔写实/煤层露头着色/更新煤层面/处理尖灭(BenchTemplateBuilder域)/刀量切割/渲染配置。→ 无托管源纯 native，记录待上机。
- **C. PitDesign 境界 / MineAssLib 内核 + 对话框（17）**：坑线落地/直线坑线/撤销坑线/增量增删边/创建工作线(IPitDesignCapability.SetWorkLineAdvanceMode+WorkLineDialog)/创建工程位置(EngineeringPositionWindow+端帮对接)/编辑台阶/局部台阶/最终并段/延拓触发设置/约束条件设置/确定开采程序/排土场放坡/排土模板/破碎站位置设置/平盘联络道/结构路面。→ 全程 IPitDesignCapability(内核)+ WPF 对话框，非简单画线(工作线带前进方式/扇形回转语义)，忠实移植需内核。

**结论**：death=60 全部 = A(引擎)+B(内核)+C(境界)，与三大边界一一对应。本 loop 内 doable+可验证+忠实的功能确已补全；这 60 个继续做必然触碰"发明原程序没有的逻辑"或"本机不可验"，命中用户"先跳过/先记录"红线。

## 三十九、第 4 审计轴：活命令实体操作覆盖矩阵（补 4 缺口，621 测试）

死按钮查"无处理器"，但**有处理器 ≠ 全实体类型支持**。审计多态操作 × 实体类型矩阵(`grep "override <Op>"` 列覆盖)，发现活命令的真实覆盖缺口并补全：

- [x] **打断+圆→弧**(`9b1b90b`)：CircleEntity.Break，移除 CCW 第一点→第二点段、保留补段(AutoCAD 圆打断约定)。原打断仅支持 直线/多段线/圆弧。+2 测。
- [x] **偏移+正多边形**(`cb84048`)：PolygonEntity.Offset 同心(新半径=心到点距，保边数/朝向，同 CircleEntity)。原偏移缺 Polygon。+1 测。
- [x] **打断+矩形/多边形→开口多段线**(`dc79fb5`)：新基类 `BreakClosedLoop`(投两点到全部边**含闭合边**，移除 [p1..p2] 保留补段)。附带修复既有 `PolylineEntity.Break` 闭合分支不含末→首边的老限制。+2 测。

**覆盖矩阵现状(完整)**：Offset=Line/Circle/Rect/Arc/Polyline/Polygon(全几何形)；Break=Line/Polyline/Arc/Circle/Rect/Polygon(点/文字不可断，符合语义)；Explode=Rect/Polyline/Polygon(复合形)；Grips/MoveGrip=全 8 类；.pmx SceneIO 写读对称覆盖 8 类(无数据丢失)。→ 此轴亦已尽。教训见 memory [[shell-completeness-priority]] 第4审计轴。

**★会话进展**：LIVE 命令实体覆盖是继 死按钮/缺按钮/交互维度 之后又一被"已尽"结论漏掉的轴——提示"完成"结论应对**每一条正交完整度轴**逐一验证，而非笼统宣称。

## 四十、第 5 审计轴：DXF 导入/导出实体类型覆盖（补 Mesh/PolyfaceMesh，623 测试）

对照原 `DwgDxfImportService` 逐类型 diff DXF 实体覆盖：

- [x] **导入补 Mesh + PolyfaceMesh**(`040a250`)：原处理二者(合并为 3D TriangleMesh 走内核)，我的可编辑路 `LoadEntities` 落 default 丢弃(仅记警告)。补两 case——逐面提取为闭合折线线框(与既有 Face3D→闭合折线一致的 2D 投影，保几何、可"打开图纸看")；Mesh 面 int[](首元素为顶点数则跳)、PolyfaceMesh 面 Index1..4(1-based/负=隐藏边/0=缺)。+2 单测(ACadSharp 构造→DxfWriter→LoadEntities 读回 四边形面得 4 点闭合折线)。
- 核实：实际导入命令走 `LoadEntities`(可编辑路，MainWindow:1131)，非预览 `Load`——全覆盖生效。
- **导入覆盖现状(≥原)**：Line/LwPolyline/Polyline2D/Polyline3D/XLine/Ray/Arc/Circle/Point/Ellipse/Spline/Text/MText/Solid/Face3D/Dimension(爆炸块)/Hatch(边界环)/Insert(递归)/Leader/MLine/MultiLeader/**Mesh/PolyfaceMesh**；default 记警告(无静默丢失)。
- **导出覆盖(完整)**：SceneExportService.Map 覆盖全部 8 种场景实体(Line/Circle/Arc/Point/Text/Rect→闭合折线/Polygon→闭合折线/Polyline)，Arc 带 CCW 判向——scene→DXF→scene 往返不丢类型。

→ DXF 导入/导出轴亦已尽(匹配并超越原实体集)。**注**：原 Mesh/PolyfaceMesh 走 3D TriangleMesh 内核渲染，本移植为 2D 折线线框(与 Face3D 现状一致)——3D 网格渲染属块体模型子系统，若需保真 3D 需桥接该子系统(记录)。

## 四十一、第 6 审计轴：撤销覆盖 + 图层操作覆盖（补 删除图层，626 测试）

- **撤销/重做覆盖(核实健全)**：awk 扫全部 `_scene.Add/Remove/Replace/Clear`(96 处) vs `BeginChange`(88 处)，逐个核对——所有用户编辑命令均在变更前 `BeginChange()`(含循环前置)；例外仅 ① 文件加载/新建(用 `_undo.Clear()` 正确) ② 块体配色方块 `RenderBlocks`(可视化叠加，自管 `_blockCellEntities` 重渲，非编辑，不可撤销可辩护)。UndoManagerTests 已覆盖机制。**无明确缺口**。
- [x] **删除图层**(`c5951ed`)：对照原 `LayerManagerWindow.OnDeleteClick`——Kylin 有 新建/特性管理器(循环切当前)/冻结/锁定/全开/改色(L4035)/置当前(面板点击 L4013)/可见性切换，但 `LayerTable.Remove` 存在却**无命令调用**。补 删除图层 命令：忠实原语义(默认层0不可删 · 该层实体移到0层不丢 · 当前切至0 · 可撤销)。新增可测 `Scene.ReassignLayer`。+3 单测。
- **标注类型(核实完整)**：Kylin 线性/对齐/半径/连续标注 与原程序**完全一致**——原亦无 角度/直径/基线/坐标标注，不臆造(保真)。

→ 图层操作 + 标注类型轴亦已尽。第 6 轴仅得 删除图层 1 缺口。

## 四十二、第 7 审计轴：块/图块 + 选择操作覆盖（补 取消选择，626 测试）

- **块/图块(核实无缺口)**：Kylin 无 BLOCK/INSERT 图块命令，**原 PitMine3D 也无**——其"块"=块体模型(GWN 体素化，EntityToBlocks 是它)，非 AutoCAD 图块；DXF 导入的 Insert 是展开外来块引用。原程序不含图块命令，不臆造(保真)。
- [x] **取消选择**(`2c9015d`)：对照原选择 API(SelectAll/**SelectNone**/SelectPrevious/GetSelectedHandles)——Kylin 有 全部选择/最后/上次(=SelectPrevious)/快速选择(=选择类似)/选择集，唯缺 SelectNone 的**纯取消选择**命令(现仅 ESC/清空视图 可，命令行/助手无入口)。补 取消选择 命令(别名 清除选择)：清选择集(存"上次"可恢复)+清高亮，不动视图/捕捉标记。选择为 UI 方法(同既有选择命令不单测，靠编译+冒烟)。

→ 选择操作轴亦已尽(SelectAll/None/Previous/Last/Similar/SelSet 全备)。第 7 轴仅得 取消选择 1 缺口。

## 四十三、第 8 审计轴：视图/剪贴板/文件格式/草图辅助（补 栅格命令入口，626 测试）

- **视图 + 剪贴板(核实 Kylin≥原)**：Kylin 缩放/全部缩放/范围缩放/上一视图/平移/剪切/复制/粘贴 ≥ 原(仅 zoom-extents/pan/剪切/复制/粘贴)。无缺口。
- **文件格式(核实无可做缺口)**：Kylin .pmx(原生绘图)/.dxf/.dwg(ACadSharp)/.off/.csv/.blk(块体); 缺 .pmb/.3dm/.3ds/.3dp(3DMine 私有二进制无 spec=已记录受阻); 块体走 CSV(忠实适配)。
- [x] **栅格命令入口**(`7f8e01e`)：栅格显示**本已完整实现**(`_gridOn`/`SetGrid`/视口 `GridPass` 渲染 + 右键/选项/快捷键切换)，唯缺命令行/助手"栅格"命令(对应原 `PitMine_SetGridVisible` 状态栏 GRID)。补 栅格 命令 + 补 正交/栅格/栅格捕捉 入命令注册表(此前有处理器但不可发现)。
- 修改命令(上轮续核)：复制/移动/旋转/偏移/修剪/延伸/打断/分解/**镜像** 全备；实体 SCALE 两者皆无(原"缩放"=OnZoomExtentsClick 视图)。

**★发现的子轴——命令注册表完整度**：正交/栅格/栅格捕捉 有处理器却不在注册表(可执行但不可发现/补全)。→ 下轴：diff 全部 `cmd == "X"` 处理器 vs 注册表列表，找"能用但不可发现"的命令补入。

## 四十四、第 9 审计轴：命令注册表完整度（补 §四/§八+TaskLib 组，626 测试）

diff 全部 `cmd == "X"` 处理器(593) vs CommandCatalog(126) → 477 缺失。**关键澄清（架构）**：
- **命令框执行本就完整**：命令框(OnCommandKeyDown 6047-6052)**无门槛**，直接 `ExecuteCommandToken` → 英文 switch → default → `DispatchRibbon` → `OnRibbonCommand` 全部中文 `if(cmd=="X")` 处理器。所以 593 命令**全部可经命令框执行**——非功能缺陷。
- **CommandCatalog 仅管**：① 自动补全候选 ② 助手自由文本执行门槛(`AssistantSubmit` 5882 `if(IsKnownCommand)`) ③ 校验。助手菜单选项(`opt.Command`)执行**不经此门槛**。
- 477 缺失多为**别名**(产能/产能分析/产能分类对比、三角网/创建三角网、大小写变体[IsKnownCommand 大小写不敏感自动覆盖])——**全塞入会污染补全**。
- [x] **补 §四/§八+TaskLib 组**(`9c50686`)：目录有 CAD/网格/地形/块体/计划 段，却**完全缺数据分析段**。补 43 个规范名(设备台账/生产/产能/故障/KPI/钻孔/煤质/月计划/机群/…+ 生产量核算/物料换算/采剥平衡/配煤/工序进度/编组产能/环节降效)——全部验证有处理器→可执行+现可发现+助手可执行。按"只考虑功能"不做 453 别名大规模填充。

→ 命令执行完整(命令框达全部处理器)；目录=精选补全子集，已补齐主功能组。**教训**：勿因"命令不在注册表"误判受阻——先查执行路径(命令框 DispatchRibbon fallthrough 达全部处理器)。

## 四十五、第 10 审计轴：点云 + 路网托管算子覆盖（两轴均无新缺口，626 测试）

逐按钮 diff 原模块 vs Kylin 处理器：

- **点云(PointCloudLib)**：原 33 按钮，Kylin 缺 5——分割点云(native PMSG)/剔面(三角网)(native)/坡顶底线提取(native PMTB)/点云管理(dialog)/显示隐藏(`IPointCloudCapability.SetVisible` native)——**全属已记录 native/对话框边界**(§三十八 B 类)。**托管算子完整**：SOR/半径去噪(=ROR)/C2C+点云比对(=位移监测)/法向估计/点云统计/点云剖面(=剖面分析)/抽稀/地面滤波/高程着色/坡度坡向/曲率/粗糙度 全备(异名已核)。
- **路网(RoadLib)**：原 19 按钮，Kylin 缺 4——增量增删边/延拓触发设置/破碎站位置设置/结构路面。实读 `增量增删边`=`RoadEditWindow` 会话式交互编辑窗口(增删边+撤销+拓扑增量)，确属对话框/交互特性(C 类，GUI 不可验)。**托管算子完整**：构建路网/提取中心线/寻径/演化对比/车铲匹配/螺旋·折返斜坡道/路网校验 全备。

→ 点云 + 路网轴的缺失项**全部**是已记录的 native/对话框边界，无被漏的托管算子。**第 10 轴无新可做缺口**——强收敛。

## 四十六、★全模块按钮 diff——doable 功能完成的 definitive 证明（626 测试）

一次性 diff 原**全部模块** `AddButton`(225 唯一按钮) vs Kylin 处理器(593)，逐个核对 ~95 缺失项，**全部**归入下述三类，无一是被漏的可做托管算子：

- **异名已覆盖(网格术语)**：原"三角网/格网"↔Kylin"网格"——三角网体积=网格体积/网格面积体积、两期三角网算量=两期算量/两期土方、构建等值线=等值线/等高线、格网质量检测=网格检查/网格诊断、生成三角网边界=网格边界/提取边界。功能全在。
- **★MeshEditLib = 声明的内核模块**：源码 "P-0b：声明本程序集为内核模块"，要求 IModuleContext + `IMeshOpsCapability`×13 + native mesh 拾取/诊断事件(DiagnoseMeshPickedEvent)。故其全部缺失算子(布尔 并/差/交/补集·修复拓扑关系·面交线·裁剪面·删除三角面·创建/动态剖面·分割地质体·多段线嵌三角网·实时曲面坐标·离散化模型·赋节点高程·统一线高程)**均 kernel-bound**，非漏的托管算子。(唯 更新煤层面 源注"零内核"但依赖顶/底板三角网地质子系统基建，Kylin 未建，记录。)
- **A/B/C 已记录边界**：TaskLib 排产引擎(生产任务编制/派工/派车/台账/进度…) + C++ 内核几何(分割点云/剔面/坡顶底线/倾斜摄影/煤层面) + PitDesign·MineAssLib 对话框(坑线/工作线/台阶/排土/工程位置/破碎站/road-edit 窗口)。

**★结论(definitive)**：Kylin 已实现原程序**全部 doable+可托管+可验证**的功能(含异名等价)；余下缺失项**无一例外**是 ① 内核模块算子(MeshEditLib/点云 native/PitDesign) ② TaskLib 引擎管线 ③ GUI 对话框——即用户"不满足验证条件先跳过、无法验证先记录"的三类。**10 条正交轴 + 全模块 diff 交叉验证：loop 内 doable 功能已穷尽补齐**。见 memory [[faithfulness-only-original-commands]]。

## 四十七、TaskExploder 引擎边界的精化依据——「验证置信度」分界（实读 ProductionTask 复评）

再按「先读源再判」实读全 `ProductionTask.cs`(474 行) 复评引擎可移植性，得更准结论：

- **可移植性比预想高**：ProductionTask.cs 是**多类型文件**，含**已移植**的 `CoalQuality`(灰/热/硫/MeetsTarget)、`DrillQuantity`(穿孔量/达成)、`EquipmentGroup`(=FleetCycle 域：斗数/节拍/循环/匹配系数/HasCycleBreakdown)。故扩 minimal ProductionTask→full 主要是**加字段**(EngineeringPositionId/DestinationId/Splits/Mix/MaterialCode/QualityTarget…)，低风险；destabilize 担忧被高估。
- **★真正分界=验证置信度**：8 个自足计算(量核算/物料换算/采剥平衡/配煤/工序进度/编组产能/环节降效/排土推进)是**简单算术**，不变量**完全 pin 死行为**→逐字移+不变量测=**完全可验**，已做。TaskExploder 是**多约束交互装箱**(配煤重分配×采排守恒×班次时窗装箱×设备双占校核)，不变量(量守恒)只能**部分 pin**——子错误(错班次分配/错重分配)可通过量守恒却排程错，且本机无法比对原 WPF 输出→**仅部分可验**。
- **按用户规则的判定**：简单计算满足验证条件(完全可验)→已补；引擎**仅部分可验**=命中"不满足验证条件先跳过/无法验证先记录"。子算法(DeriveDumpTargets 采排守恒/ApplyBlendConstraint 配煤重分配)虽较简单，但**非原程序独立命令**(内嵌 Explode)，独立暴露=违"只做原程序有的命令"。
- → TaskExploder 保持记录为边界是**一致且正确**的判断，依据从"规模"精化为"**验证置信度 + 无独立命令切片**"。若上机可比对原输出(满足验证条件)，则逐字移植可行(基座类型已备大半)。

## 四十八、★第 11 审计轴：数据驱动输出审计——揭真实 bug + 漏移的真实算法（633 测试）

全模块 diff 证明"每个按钮有处理器"，但**未查每个处理器的实现是否忠实/正确**。转而 dump 主要 §四/§八 查询的**实际输出值**眼验(同揭合格率/在役 bug 的手法)——效能预测投影年产 = **118,177 万m³**(×全在役485台)，而实际最大年产仅 **76,103**，高估 55%：

- [x] **fix 效能预测年产高估**(`2d8eebf`)：基线是"产出设备月均"(`WHERE output_m3>0`)却×**全在役**(485，含卡车/钻机等非独立产出者)——口径不一致。加 `ProducingUnits`(=capacity_monthly 有产出设备数 306)，投影×产出设备 →74,561≈实际。回归断言投影∈[实际×0.5,×1.6]。
- [x] **★补漏的真实功能 ForecastModels**(`2d8eebf`)：查原 `EquipmentForecastWindow` 发现其预测内核是 `ForecastModels.cs` **时间序列回归**(LSQ趋势+EWMA融合/Holt双指数 + σ_k²越远越宽区间 + 残差>2σ异常)——**纯自足、可托管、完全可验**，我原却用"基线×12×台数"**粗略近似**替代。逐字移植 `src/Data/ForecastModels.cs` + `GetMonthlyOutputSeries` + 产量预测/Holt预测 命令。+7 单测(斜率/R²还原·异常识别·区间张开·Holt·样本不足·空)。

**★新审计轴——实现忠实度(非仅"有处理器")**：处理器存在 ≠ 实现忠实。有的功能是**原真实算法的粗略近似**(效能预测用 baseline×12 代替回归)。核查法：dump 实际输出眼验量级/异常 + 对可疑者查原实现是否有更完整的**可见托管算法**被我近似掉。→ 下轴：续查其它"简化/近似"实现是否漏了原可托管算法。见 memory [[verify-seed-enum-values-before-filter]](输出审计) [[unlock-blocked-insights]](先读源)。
