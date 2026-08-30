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
- [x·部分] **演化对比 误接修复(运输 §六)**：已从点云 C2C 别名移除 `演化对比`(commit `8894be6`)——止损其错误执行点云比对。**真实现待专项移植**：`RoadLib.Evolution.RoadEvolutionAnalyzer`(323 行) 依赖深链 `GeometryIndex`(106 行)→`SegmentGrid`→`Point3d` + RoadGraph/RoadEdge + 结果/选项类型, 共 500+ 行 RE1-RE8 逐段匹配规则, 脱离原测试夹具难忠实验证——记录为大型待办(需两期中线输入设计 + 全链移植)。
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