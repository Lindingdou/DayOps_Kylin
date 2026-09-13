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
  - [x] **节点求值引擎 + 补全节点类型**(`本次`)：**对比原 NodeEditor 发现 Kylin 仅 4 类型且是死画布(无求值，产不出几何)**。补齐至**原 11 类型**(参数 Number/String/Bool/Point + 几何 Line/Circle/Arc/Rectangle/Polygon/Polyline + Bake)，并实现**忠实的 pull-based 求值**(原 NodeModel.Evaluate，原产 native 句柄→此产托管 SceneEntity=几何等价)：参数节点输出值，几何节点按输入(连线取上游/否则默认值)产实体，Bake 汇总。UI 补全部按钮 + 「▶求值到场景」(EvaluateBakes→主绘图场景，走 AssignLayer) + 参数节点双击改值 + 卡片显示值。演示图 数字(50)→圆半径→烘焙。+8 单测(参数求值/圆默认半径10/数字连半径拉取/点连圆心/各几何类型产实体/圆弧起终点几何/Bake端到端/环保护)。→ **节点编辑器从结构画布升级为可求值的可视化脚本(节点图→几何→入场景)**。703 tests。
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

**实现忠实度轴续（commit `56cc298`/`8a072c5`）——再补 2 真实算法 + 记录 1 native**：
- [x] **台阶扩帮真实台阶距**(`56cc298`)：`BenchLines` 源注"真实台阶距=W+H/tanα, 此处定距近似"——原用境界短边/10。补 `BenchLines.BenchDistance`(平盘宽+台阶高/tan坡面角) + 命令 "台阶扩帮 帮宽 台阶高 坡面角" 用真实距(缺省回落几何默认)。+1测。
- [x] **★FleetOptimizer 智能编组优化**(`8a072c5`)：我的 设备智能编组 只**读**预计算 dispatch_rule 表，而原 `FleetOptimizer.cs`(211行**可见托管**)是**真优化**——物理产能子模型(M/M/c 排队论内生匹配系数 MF=n·T_load/T_cyc, 取 min(铲装,车运)瓶颈侧) + **无界 DP 最小卡车数达标** + Erlang-C 排队概率 + 在籍台数修复。逐字移植 `src/Data/FleetOptimizer.cs`(复用已移 FleetCycle 物理) + `GetFleetDispatchRules`(join equipment_model 载重/斗容) + 编组优化 命令。+6测(Erlang-C边界/单调·优化达标·在籍约束·空规则·种子跑通)。
- **PMF 地面滤波(记录 native)**：原 地面点滤波 用 **渐进形态学 PMF**(button 描述明示)，但经 `IPointCloudCapability.GroundFilterComputeAsync`(参数 CellSize/MaxWindowM/TerrainSlopeDeg/InitElevThresh)——**算法在 native capability 不可见**。PMF 虽 published 但原变体不可见+变体敏感(不同 PMF→不同地面分类→下游 DEM/体积)。按忠实规则"原算法不可见→不臆测替代"(区别 ForecastModels/FleetOptimizer 源码可见→逐字移；ObjectSnap 平凡无歧义→可移)记录为 native 边界；现最小高程滤波是可用简单托管占位。

**★实现忠实度轴产出丰**：ForecastModels(回归)+FleetOptimizer(编组优化)+BenchLines(台阶距) 三个原**可见托管真实算法**被我用粗略近似/只读表代替，现已逐字补齐。→ 下轴续：CoalQualityEstimator/Analytics 等 GeoDataBase 可见算法服务是否也被近似。

**实现忠实度轴续（commit `f11f18a`）——普通克里金 OK（名实不符修复）**：
- [x] **克里金估值实为 IDW → 移植真 OK 克里金**：命令名"克里金估值"却做 IDW(`Estimation.cs` 注"IDW 插值")，而原 `CoalQualityEstimator.cs`(336行)有真**普通克里金 OK**——球状变差函数自动拟合(sill=样本方差/range=95%sill滞后/nugget首箱) + 解 (k+1) 阶克里金方程组 + **克里金方差**。逐字移植 OK 核 `src/Cad/OrdinaryKriging.cs`(Variogram 球状 γ(h)/FitVariogram/Krige 方程组/高斯消元 Solve) + 克里金估值命令改用真 OK(逐格 EstimateAt·半径外回落 IDW 免留洞·出平均克里金方差)；IDW 保留为 快速估值。+7 单测(控制点精确内插方差0·球状变差 γ(0)=0/γ(≥range)=sill·方差非负·半径外 null·线性场贴近·单点·空)。
- **★实现忠实度轴累计 4 真实算法**：ForecastModels(时序回归)/FleetOptimizer(编组优化 DP)/BenchLines(台阶距 W+H/tanα)/OrdinaryKriging(OK 克里金)——皆原**可见托管算法**被我用粗略近似(baseline×12/只读表/短边/10/IDV替克里金)代替，现逐字补齐 + 不变量验证。→ 下候选：CoalQualityAnalytics(647行·洗选/商品煤符合性/用途/品位-储量/分标高/离群QC)。

**实现忠实度轴续（commit `6f89014`）——商品煤符合性（CoalQualityAnalytics 首个分析）**：
- [x] **商品煤符合性 Evaluate**：原 `CoalQualityAnalytics.cs`(647行) 有 6 大煤质深度分析(洗选/商品煤符合性/用途/品位-储量/分标高/离群QC)，我的煤质功能只有描述统计(均值)。逐字移植首个高价值+易验的**商品煤符合性**为自足 `src/Data/CoalAnalytics.cs`(逐化验段判 Ad≤/St≤/Q≥/Vdaf∈区间 → 达标率 + 按煤层 + 超标清单带坐标, 数据不足显式跳过不臆造) + `GetCoalSamples`(coal_sample join borehole 坐标) + 商品煤符合性命令(可传限值, 缺省 Ad≤30/St≤1/Qgr≥21)。+4 单测。
- **实现忠实度轴累计 5 真实算法**(ForecastModels/FleetOptimizer/BenchLines/OrdinaryKriging/CoalCompliance)。→ CoalQualityAnalytics 余 5 分析(品位-储量曲线/分标高煤质/离群QC/洗选提质/用途适宜性)可续移(数据 coal_sample 齐)。651 测试。

**实现忠实度轴续（commit `dfc09b6`）——CoalQualityAnalytics 再移 3 分析**：
- [x] **品位-储量曲线 GradeTonnage**：厚度×密度质量代理，灰/硫累计"≤限值"(低者优)·热量累计"≥"，返回累计曲线(单调)。命令 品位储量曲线 <指标>。(byLevel GB 分级依赖参考服务，跳过。)
- [x] **分标高煤质 ByElevation**：按开采标高带做厚度(×密度)加权均值。命令 分标高煤质 <指标> <带高>。
- [x] **离群 QC DetectOutliers**：Tukey IQR 1.5×IQR 栅栏 + 严重度(超几个 IQR)降序 + 线性插值分位数。命令 煤质离群 <指标>。
- CoalSample 加 厚度/密度(可选默认不破坏既有构造)，GetCoalSamples 补两列。+4 单测(累计单调/厚度加权均值/IQR 识别极值/小样本跳过)。
- **实现忠实度轴累计 7 真实算法**(ForecastModels/FleetOptimizer/BenchLines/OrdinaryKriging + CoalAnalytics 的 商品煤符合性/品位-储量/分标高/离群QC)。CoalQualityAnalytics 6 分析已移 4，余 2(洗选提质[需 raw+clean 对，数据齐]/用途适宜性[需 GB 分级参考规则])。655 测试。

**实现忠实度轴续（commit `6ef3a16`）——CoalQualityAnalytics 全 6 分析补齐**：
- [x] **洗选提质 WashingBySeam**：成对原煤↔浮煤 → 降灰率(raw-clean)/raw·脱硫率·挥发变化·浮煤回收率 + 全矿汇总。命令 洗选提质。
- [x] **用途适宜性 UtilizationBySeam**：动力煤评价(灰/硫/热分级综合→优良中差) + 炼焦评价(粘结指数 G→炼焦价值)。煤类名用样本存的 GB/T5751 `coal_type` 替代原 `_ref.ResolveCoalType`(参考服务)——**忠实适配**(用已存分类而非重推)。命令 用途适宜性。
- CoalSample 加 回收率/G/胶质Y/煤类(可选默认)，GetCoalSamples 补 4 列。+4 单测。
- **★CoalQualityAnalytics(647行) 全 6 分析补齐**：商品煤符合性/品位-储量曲线/分标高煤质/离群QC/洗选提质/用途适宜性。**实现忠实度轴累计 9 真实算法**。659 测试。

**实现忠实度轴续（commit `b2e3323`）——GeoDataBase 算法 sweep 收官**：
- [x] **展绘层位数据 名实不符修复**：原映射到 DrawBoreholesCmd(画钻孔开孔)，而原 `HorizonPointBuilder`(136行) 是分煤层提取顶/底板高程点(底=floor_elevation, 顶=底+采用厚度, 按煤层×顶/底分层)。补 `GetHorizonPoints` + HorizonPointsCmd，与展绘钻孔拆分。+1 单测。**实现忠实度轴累计 10 真实算法**。
- **记录 VirtualDrillEngine(964行) 为边界**：多煤层顶/底板 TIN 竖直采样合成钻孔柱，依赖地质模型的煤层面 TIN 基建——Kylin 只有 §四/§八 见煤数据、无煤层面 TIN(建面属地质建模管线，未建)。我的 虚拟钻孔=单面高程点查询(简化占位)。→ 依赖 Kylin 缺失基建，记录。
- 核实 **矿床识别=DepositAutoDetector 已忠实移植**(PCA 倾角/走向 + Z 层煤层数)。
- **★GeoDataBase 可见算法 sweep 收官**：CoalQualityAnalytics(6)/ForecastModels/FleetOptimizer/HorizonPointBuilder/DepositAutoDetector 全移；VirtualDrillEngine 记录(缺地质建模基建)。→ 该模块(§四/§八 approximation 集中地)实现忠实度已收口。660 测试。

**实现忠实度轴续（commit `608ed31`）——其它模块 sweep + 网格体积分级容错**：
- [x] **网格体积分级容错**(忠实原 `MeshVolume`)：我的 `MeshMetrics` 体积仅基础散度 `|Σa·(b×c)|/6`，水密时精确但**非水密(开放曲面)给无意义值**。补 `MeshMetrics.RobustVolume`：① 水密(`MeshDiagnose.IsClosed`)→散度严密；② 非水密→`MeshWeld` 焊接 + `MeshHoleFill` 扇形补洞封盖→散度绝对值(复用已移零件)。Compute 改用之。+2 单测(水密四面体 1/6 精确·缺面开放四面体补洞恢复 1/6)。**实现忠利度轴累计 11 真实算法**。
- **其它模块 sweep 核实**：MineAssLib 剩余算法(SeamOutcrop/Incline/斜面体积/MonthlyMineSchedule/CoupledMine/TautString)在 境界·PitDesign·地质建模·TaskLib 排产 记录边界域；RoadCrossSection(弯道加宽曲率法)/SlopeEstimator/DepositAutoDetector/RoadNetwork/mesh 基元 早前已忠实移。DumpAdvanceByVolume(依赖 dump_strip 块模型台账)记录，我的 SinkNode 是 TaskLib 容量模型忠实移植。
- **★实现忠实度轴总结**：**11 真实算法逐字补齐**(GeoDataBase 10 + MeshVolume 分级)；其它模块 approximation 已 sweep 无残留(covered/recorded)。→ 该轴收口。662 测试。

## 四十九、实现忠实度轴——全模块 sweep 收官 + 多维种子审计

- **RoadLib sweep(全覆盖)**：HaulMetrics/RoadConnectivity/PolylineMetrics/SegmentGrid 已逐字移；原 `TransportIndicators`(W6 几何网络指标: 节点/边/总里程/连通/OD; 吨量成本原亦留桩)由 Kylin 中心线管理(节点/边/总长/断头/交叉/孤立)+路网体检(连通/分量)+运距指标(加权运距/等效里程/循环)覆盖；CenterlineJunctions 交叉检测由中心线管理覆盖。
- **全模块 sweep 结论**：GeoDataBase(10 移)/RoadLib(全覆盖)/MineAssLib(RoadCrossSection·SlopeEstimator·DepositAutoDetector 已移, 余在境界/地质/TaskLib 记录边界)/BlockModelLib(MeshVolume 分级·DepositAutoDetector 已移, DumpAdvanceByVolume/VirtualDrill 记录)/TaskLib(自足计算 8 已移, 引擎记录)。**实现忠实度轴全模块收口**。
- **多维种子输出审计(两轮皆干净)**：本会话新功能 8 项(编组优化/煤质分析×5/层位/预测) + 旧 §四/§八 查询 12 项，在真实种子上眼验量级/异常——全部合理，无恒0/空/量级错(验收合格率 67.3% 确认早前 bug 修复)。
- **★六维交叉验证完成**：① 按钮/命令(全模块 diff) ② 输出正确性(修 3 bug + 两轮种子审计) ③ 实现忠实度(11 算法全模块 sweep) ④ .pmx 往返(类型+LayerName+颜色+图层表, 已测) ⑤ shell 冒烟 ⑥ 撤销/图层/选择/视图/DXF 导入导出。→ **doable+可验证+忠实(含实现是原真算法)的功能集系统性完成**；余项均已记录边界。662 测试。

## 五十、分析结果导出能力（补真实能力缺口，664 测试）

发现真实能力缺口：我移的煤质分析只在状态栏显**截断摘要**，而原程序能**导出结果供现场定位处置**（超标段/离群段带坐标）。补两个 actionable 结果导出（CSV 序列化可测 + 标准 SaveFilePicker）：

- [x] **商品煤符合性导出**(`9a368e5`)：`CoalAnalytics.ComplianceToCsv`（逐化验段: 孔号/煤层/坐标/各指标/判定/超标原因）+ 导出符合性 命令。+1 测。
- [x] **煤质离群 QC 导出**(`fca2b23`)：`OutliersToCsv`（离群段: 孔号/煤层/值/标高/方向/严重度IQR + 元信息行）+ 导出离群 命令。+1 测。
- → 两个可"定位处置"的煤质分析结果(超标段/离群段)均可导出带坐标 CSV。导出模式(ToCsv 纯函数可测 + 异步 SaveFilePicker)已建，余分析(品位-储量曲线/分标高)如需可循此补。664 测试。
- [x] **补齐全 6 煤质分析结果导出**(`8fb8613`)：续补 品位-储量曲线/分标高/洗选/用途 的 ToCsv + 导出命令，加通用 `SaveCsvAsync` 辅助。至此 CoalQualityAnalytics **全 6 分析结果均可导出 CSV**(符合性/离群/品位-储量/分标高/洗选/用途)。+1 单测(4 序列化)。
- [x] **编组优化 + 产量预测结果导出**(`f4bdd19`)：`FleetOptimizer.ToCsv`(逐编组方案: 铲型/铲数/车型/组日产/匹配/瓶颈 + 汇总/说明) + `ForecastModels.PathToCsv`(元信息 + 历史序列 + 未来 12 期 + 95% 区间) + 导出编组/导出预测 命令。+2 单测。
- **★结果导出脉络完成**：**8 个结构化计算结果均可导出 CSV**(煤质 6 + 编组优化 + 产量预测)——含逐段明细/曲线/编组方案/预测路径+区间，供现场定位处置/绘图/计划。此脉络三轮前发现(结果只显截断摘要)，逐步补齐完备。§四/§八 聚合类结果(产能/故障排名等)较简单，状态栏文本 + 既有 ExportTableToCsv(原始表)已足。667 测试。

## 五十一、数据导入脉络（DataImportCenter 全 5 类 CSV 入库，670 测试）

新脉络(与结果导出配对——原是"导入导出中心")：原 `DataImportCenter` 有 5 类设备数据 CSV 入库(表头映射 + 逐行 insert/update/skip)，我的 Kylin 只有几何导入(点/点云/钻孔/块体)。补全 5 类(均 CSV 基础、可验证[导入后查库行]、参数化防注入、FK 约束正确生效)：

- [x] **生产班次记录**(`1aa7493`)：`ImportProductionRecords`(设备+日期+班次 upsert)。
- [x] **月度产能 + 故障记录**(`a64b9af`)：`ImportCapacityMonthly`(设备+年+月 upsert) + `ImportFaultEvents`(事件插入型)。加通用 `ImportCsvToDbAsync` 辅助 + `ParseCsvRows`。
- [x] **月度KPI + 设备台账**(`dd1f40e`)：`ImportKpiMonthly`(9列 upsert) + `ImportEquipmentLedger`(equipment_id upsert, model FK→equipment_model 正确校验)。
- 命令：导入生产记录/导入月度产能/导入故障记录/导入月度KPI/导入设备台账。+3 单测(插入/更新/跳过/坏行错误/FK 约束)。
- **★"导入导出中心"补齐**：导入侧(5 类设备数据 CSV 入库) + 导出侧(8 结构化结果 CSV + ExportTableToCsv 原始表)——原 DataImportCenter 的导入导出功能essence 均托管落地。用户可"改自己数据(导入)→跑分析→导出结果处置"闭环。670 测试。
- [x] **地质数据导入**(`6f7a7d9`/`1d3dad4`)：`ImportCoalSamples`(忠实 CoalQualityExcelIo，hole_id→borehole.id 查找，按 孔+煤层+起深 upsert，15+ 分析相关列) + `ImportObservationPoints`(忠实 CurrentStatePointExcelIo，point_id+seam_code upsert，直接 x/y)。命令 导入煤质/导入观测点。+2 单测。→ **闭合煤质数据生命周期**：导入煤质→跑分析(符合性/品位储量/洗选/用途/离群)→导出结果处置。
- [x] **月度计划导入**(`8cb49e1`)：`ImportMonthlyPlans`(按 year+month upsert)。**特殊价值**：种子 monthly_plan 大半空(煤量/剥采比缺，见§三十六)，导入用户计划数据填充后 月度计划/达成度评价(计划 vs 实际)才有意义——**解锁了本受种子数据限制的计划分析**。+1 单测(导入后读回煤量/剥采比非空，种子做不到)。
- **★数据导入脉络完成(9 类)**：设备域 5(DataImportCenter: 生产记录/产能/故障/KPI/台账) + 地质 3(煤质化验/观测点/见煤成果) + 计划 1(月度计划)，均 CSV 基础、可验证(导入后查库行)、参数化防注入、FK 约束正确生效。**覆盖全部可分析数据**：地质钻孔(煤质/观测点/见煤成果)配对 见煤统计/层位展点/可采区域/煤质分析；设备域配对 台账/产能/KPI/故障统计;计划配对 达成度。**数据导入通用框架(ParseCsvRows + ImportCsvToDbAsync + ImportXxx)完备**。674 测试。
- [x] **导入模板生成**(`d0414d3`)：完成原 DataImportCenter"**模板/导入/导出**"三件套(我原做了导入 + 结果导出，缺模板)。`ImportTemplate`(9 类各带表头 + 示例行的模板 CSV) + 导入模板命令。用户闭环：下载模板→按格式填数据→导入入库→跑分析→导出结果。+1 单测(9 类非空/未知 null/表头含键列/**模板示例行往返可导入自洽**)。→ **DataImportCenter 三件套完整落地**。675 测试。
- [x] **CSV 解析可测化 + 往返验证**(`c346530`)：`ParseCsvRows`→`GeoDataQueries.ParseCsv`(公开可测)；往返验证 `ExportTableToCsv`→`ParseCsv`→`ImportProductionRecords`(按列名忽略 id/created_at)既有键全部更新 0 错误。+2 单测。677 测试。
- [x] **§四/§八 聚合结果导出**(`384dbb2`)：**诚实纠偏**——我一度判聚合导出"边际"拟跳过，但按用户指令"补全功能"，聚合导出满足验证+可实现，应做非跳过。加通用 `RecordsToCsv<T>`(反射 record 属性→CSV) + 导出分析 <类型> 命令，一次覆盖 11 聚合结果(产能排名/故障排名/见煤统计/分层煤质/年度产量/KPI趋势/产能分类/故障类型/班次产量/月度计划/作业面)。+1 单测(反射表头/值/空列表)。→ **导出能力完整**：8 结构化分析结果 + 11 聚合结果 + 原始表 ExportTableToCsv。678 测试。
- [x] **设计数据导入(路况/边坡)**(`8d86e99`)：**贯彻同一诚实纠偏**——曾判"纯展示数据价值低"拟跳过，但与已导入的观测点(配对展绘)同理，路况/边坡也配对显示功能(路况显示/边坡设计)，用户会载入自有设计数据，故应补非跳过。`ImportHaulRoads`(按 road_id upsert，road_type CHECK main/branch/dump/temp) + `ImportSlopeDesigns`(插入型无自然键，side_type CHECK working/final/transition)。命令 导入路况/导入边坡，模板 运输道路/边坡设计。+2 单测(插入/更新/跳过/缺必填错误/**违反 CHECK 约束→错误**)。→ **导入侧覆盖全部可载入表(操作域5+地质3+计划1+设计2=11)**。680 测试。

## 五十二、全 Ribbon 命令面差集探查（新正交角度：不看模块，逐命令核对原始 MainWindow.xaml 全部按钮 header）

**方法**：提取原始 `MainWindow.xaml` 全部 Ribbon 按钮 header(~110)，逐条对 Kylin 源码做覆盖 grep；再对每个"缺失"项回原始查其真实实现，判 真功能/桩/架构受阻。此前各轮按"模块/数据脉络"探，本轮按"原始逐命令"探——抓模块视角漏掉的散命令。

- [x] **AI 助手命令面全覆盖核对**：原 `MockAiEngine.cs`(官方 AI 助手"点一下直接执行"命令集)24 个 CAD token(CIRCLE/RECTANG/LINE/PLINE/POLYGON/POINT/MOVE/COPY/ROTATE/SCALE/MIRROR/OFFSET/TRIM/ERASE/DIMALIGNED/DIMRADIAL/DIST/MANG/ZOOMEXTENTS/PAN/3DORBIT/GIZMO/3DVIEW/toggle3d)**逐一 grep 确认 24/24 已实现**。AI 助手核心命令面完整。
- [x] **隐藏/隔离三件套**(`fa6f8ad`)：Ribbon 逐命令 diff 抓到此前缺的 隐藏对象/隐藏同一图层对象/结束隐藏(原 `MainWindow.ContextMenu.cs` OnCtxHideObject/HideLayer/ShowAll)。SceneEntity 加 `Visible` 标志(BuildGeometry/Pick/SnapCandidates 均跳过)，Scene.HideEntities/ShowAllHidden/HiddenCount 辅助，`_hiddenLayers` 跟踪层恢复。会话瞬态(不入 SceneIO)与原一致。+1 单测。681 测试。
- **⛔ 本轮 5 个记录边界(逐一回原始核实非疏漏)**——根因同源：**托管场景在创建/导入时即把复合体炸开为图元、把网格化为边线、且线段渲染**；原始这些命令均走 **native 引擎**保留态(句柄/逐面)，托管重实现刻意不建该架构：
  1. **编辑填充(HATCHEDIT)**——**原始自身即桩**：`OnHatchEditClick` 仅 `AppendToHistory("编辑填充（待引擎实现 HATCHEDIT 命令）")`。原程序未实现→按忠实原则不发明。**并非疏漏，是原始留白**。
  2. **保存选择集**——实为"导出选中三角网→OFF 文件"(非命名集；命名集是 创建/调用选择集，已有)。Kylin 的 OFF 导入被解析为**边线段**(非保留三角网)，场景无可选网格实体→无导出源。需一等 MeshEntity + 面表(原绑 native AcDbIds)。
  3. **标注样式(DIMSTYLE)**——**部分解锁**(见 §五十四)：原 `DimensionStyleWindow` 有两层——①**文档级 DIM 变量**(文字高/小数位/箭头比等，影响新建标注) ②编辑**选中既有标注**实体属性(经 native `PitMine_SetDimensionProperty(handle)`)。**①是核心且可做**：全局 DimStyle 影响新建标注=忠实 DIM 变量，已补(§五十四)。**②仍受阻**：Kylin 标注创建即炸为 线+文字，无保留复合标注实体可编辑既有——记录。
  4. **采剥演示**——`BlockModelLib.Simulation.MiningSimController` 加载 .blk 逐条带推演，坐落于**整个 32,107 行 BlockModelLib 子系统**(块体浏览器/创建/导入/煤质属性/体素体积/仿真)。**整模块级，超"补单命令"范围**。
  5. **渲染配置**——**部分解锁**：原设 ①**全局着色模式(线框/实体)** ②逐对象色覆盖。**②逐对象色可做**(经 `特性 颜色 <#RRGGBB>` 改选中实体色，见 §五十五)。**①仍阻**：Kylin 托管场景为线段渲染，无实体面着色管线可切换。
- **结论**：Ribbon 逐命令差集探查**收敛**——可做+可验证+忠实者(隐藏/隔离)已补；余 5 项均**回原始核实**为 原始留白(1) / 托管架构刻意边界(2-3,5) / 整子系统级(4)，非命令级疏漏。**命令面覆盖到此为托管重实现的忠实上界**。681 测试。
- [x] **内部一致性修复：CommandCatalog 死项**(`d69c8bb`)：另开**内部一致性**角度(脚本核 207 目录项在处理器全文出现次数)，抓到 3 个**声明但不解析**的死项——目录写裸名但处理器用长名：`坡度`(处理器仅 `坡度着色`)、`坡向`(仅 `坡向着色`)、`资源量`(仅 `资源量估算/报告`)。点自动补全/目录这些裸项**静默无反应**(真 bug)。修：各加为既有处理器别名(坡度/坡向→对应着色，资源量→资源量估算 ResourceReport)。脚本复验 **207/207 全解析**。→ 命令目录与分派**完全自洽**，无悬空项。

## 五十三、导入格式补全：MapGIS 6.x .WL/.WT（新脉络：原有其他导入格式 reader）

**方法**：探原始 `Cad/Import/` 目录, 发现 Kylin 缺原有的额外导入格式 reader——**KdfReader**(811行, WeCAD KDF 二进制)、**MapGisWlReader**(363行, .WL 线)、**MapGisWtReader**(233行, .WT 点注记)。均为**逆向文档化的纯托管二进制解析器**(有格式规格 MD, 非 native), 且 `AlgoCore/GISLib/T01_0036/` 有**真实样本文件**可作夹具→ 可做+可验证+忠实。

- [x] **MapGIS WL/WT 导入**(`本次`)：`MapGisImportService.cs` 忠实移植 WL+WT 二进制解析(magic `WMAP\`D2`, section index @0x291, WL: obj0 属性表57B/record + obj1 顶点池16B + obj2 等高线Z识别; WT: Section0 属性表93B/record + Section1 GBK字符串池, 字高/旋转/颜色)。产出可编辑 `PolylineEntity`(WL 线)/`TextEntity`(WT 注记), 复用 `EntityImportResult` 走既有可编辑导入通道(`ApplyEntityImport` 抽出 DXF/MapGIS 共享)。MapGIS colorId→RGB 表移自原。文件选择器 + `ImportPath` 加 `.wl/.wt` 分派。加 `System.Text.Encoding.CodePages` 包(.NET Core 默认无 GBK 中文代码页)。
  - **验证**：真实样本(`剖面方向.WL`/`A1煤层.WL`/`图例.WT`)入 `TestData/mapgis/` 作夹具(二进制逆向格式无法内联生成)。+5 单测：WL 线顶点**落在文件 bbox 内**(防错位读出天文坐标)、煤层 WL 解析、WT 注记**GBK 中文解码非空 + 位置在 bbox 内**(至少一条含 CJK——证 GBK 生效)、扩展名分派、坏 magic/缺文件报错不崩。**端到端对真实矿区地质图数据验证**。686 测试。
- [x] **MapGIS WP(区/面) + MPJ(工程) 导入**(`本次`)：补齐 MapGIS 三元 + 工程编排。
  - **WP(区)**：忠实移植原 `MapGisWpReader` 的 **arc 级提取**(Section 0 arc表 57B/record +0x0E=顶点偏移, 相邻差=arc长; Section 1 顶点池; Section 6/10 独立顶点池)。各 arc 作 PolylineEntity(地质图斑边界轮廓——原自陈 MVP 目标)。**region 环拓扑重建**(Section 3 arc-node DFS 串环)**原始自陈不完整**(未闭合丢弃/色映射不全)且弱可验, 按验证置信度纪律**暂记不移**。
  - **MPJ(工程)**：忠实移植 `MapGisProjectReader`——magic `WMAP\`D2:`, GBK 解码全文, 正则抓 `.\\xxx.WL/WT/WP` 成员(去重), 相对 mpj 目录解析。`LoadProject` 逐个加载存在成员 → 各成员一图层合并入一结果(缺失/失败计数报警)。用户可一键开整个 MapGIS 工程载全部图层。
  - 文件选择器 + `ImportPath` 加 `.wp/.mpj`。+3 单测：WP 边界 arc **顶点落 bbox 内**、MPJ **对真实 T01_0036.mpj 抽成员清单**(WL/WT/WP 扩展名+去重)、MPJ **合成工程端到端**(合成 mpj + 真实成员同置临时目录→加载合并出 Polyline+Text 两成员两图层)。689 测试。
  - **结论**：Kylin 导入格式 = DXF/DWG/OFF/CSV/XYZ/PTS/BLK/PMX + **MapGIS WL/WT/WP/MPJ 全族**。均托管、可验证(真实矿区地质图夹具)、忠实移植逆向格式。
- [x] **KDF(WeCAD 地质地形图) 导入**(`本次`)：**曾判"无样本暂缓"，扩大搜索在 `Desktop/2026年6月测试文件/平朔数据/` 找到 8MB 真样本 `01_地质地形图.kdf` + 参考解析器 kdf_parse2.py + 期望输出 kdf_export.dxf → 解锁**(印证 insight #9"有样本时可循同法移")。`KdfImportService.cs` 忠实移植 KdfReader(811行): magic `wecad_bin_version_2021\n`, 逐实体扫描(1B长+"AcDb*"类名→分派), 公共头 ReadCommon(图层+BGR色), ParsePolyline(tag 0c 顶点池, 33B/vertex 含属性串)/ParseText/ParseMText(tag 02高度旋转+tag 03 GBK文本)/ParseHatch(边界环)/ParseLayerRecord。产 PolylineEntity/TextEntity。文件选择器 + ImportPath 加 .kdf。
  - **★强参照校验**：解析真实 8MB 地质图 → **5062 实体(3615 折线 + 564 填充 + 883 注记), 18 图层, bounds=[424666,4974680]–[438112,4984406](真实 UTM 坐标)**。**折线数 3615 vs 原始 kdf_export.dxf 的 3611 LWPOLYLINE —— 差 4(0.1%), 证解析忠实**。+2 单测(真样本折线数∈[3000,5000]/注记含中文/UTM 坐标域/顶点落 bbox；坏 magic 报错不崩)。样本仓库外(8MB), **skip-if-absent** 本机端到端验(大二进制不入库, CI 自动跳)。
  - **记录**：LAS(LiDAR 点云)——原 AlgoCore LasLib 为 **native 无托管 reader**, 记录。WP region 环拓扑重建弱可验暂记。
- [x] **3DMine .3dm 二进制网格导入**(`本次`)：**又一"无样本→扩大搜索解锁"**——TDM 族原以为无样本, 但发现 **TDM 用 `.3dm` 扩展名**(magic `3DMine_2011_Bin`), 测试目录 23 个 .3dm 中 **17 个是 3DMine 二进制**(其余 Rhino/变体)! `TdmImportService.cs` 忠实移植 TdmReader(271行): 锚点扫描(`solid`+09), AcDbFace 类名→标签(GBK)+色, nVerts + 顶点段(25B/顶点=3double+1B) + 三角段(16B/三角)。**网格→去重三角边线框(保留 Z, 3D 曲面)** → 复用 ImportResult 走 OFF 式网格显示通道。文件选择器 + ImportPath 加 .3dm(仅识 3DMine_2011_Bin, Rhino/文本报错)。
  - **★强 Euler 校验**：解析真实煤层底板 TIN(`4-2底面.3dm` 1.2MB) → **43018 三角 · 65213 去重边 · UTM 坐标域[615500,4374400]–[622500,4381600]**。**边数 65213 ≈ 3×顶点−边界(Euler 拓扑自洽), 且 三角数<边数<3×三角数**——证网格解析忠实。+3 单测(真样本 skip-if-absent Euler 合理性/保留非零 Z/坏输入报错/magic 探测)。
- [x] **3DMine Solid 文本格式(.3dm)导入**(`本次`)：测试目录 6 个变体 .3dm 是 **3DMine Solid File 文本**(file_version=3DMine_2009, 6.5MB 实体煤层模型)。`TdmImportService` 加 `ReadSolidTextMeshes` 忠实移植 TdmSolidReader: 顶点块(X,Y,Z 含小数)→solids 尾标(名+归一化 RGB)→面块(整数三元组)→重复→End; **面块中出现浮点行=下一实体顶点块起始**。`Parse` 自动分派 二进制/Solid 文本/报错(同原 TdmImportService)。+1 单测(真样本 `9煤底.3dm`)。
  - **发现**：该 solid 为**三角汤**(顶点不按索引共享)→ 去重边=恰 3×三角(vs 二进制 index-shared 网格边<3×三角), 两种拓扑均正确, 测试断言相应放宽为 ≤3×。
  - **记录**：TdmStringReader(3DMine 字符串/线)暂缓——测试目录无对应样本; 源可见有样本可移。

## 五十四、标注样式(DIM 变量文档级设置)——重审"保留复合体"边界后部分解锁

**重审**：§五十二 曾整体记 标注样式 为"需保留复合标注实体"边界。但**拆解原 `DimensionStyleWindow` 发现两层**：①**文档级 DIM 变量**(DIMTXT 文字高 / DIMDEC 小数位 / DIMASZ 箭头 …，AutoCAD/原程序里是文档设置，影响新建标注) ②编辑**选中既有标注**属性。**①无需保留实体即可做**(就是新建标注时读样式)，只有②需保留 DimEntity。前判"create-time≠原"过严——DIM 变量本就是 create-time 文档设置。

- [x] **标注样式 文档级 DIM 变量**(`本次`)：`DimStyle` 类(TextHeight/DecimalPlaces/TickRatio/ArrowRatio/TextOffsetRatio，DIM 变量语义) + `DimTools.Build/BuildRadial` 接受 DimStyle(文字高 0=自动随缩放/>0 固定；小数位控距离文字格式；箭头/刻度比)。MainWindow `_dimStyle` 影响新建 线性/半径 标注。命令 `标注样式 <文字高> [小数位] [箭头比]`(无参显示当前)。+4 单测(小数位控格式/固定字高覆盖/箭头比缩放/**默认保留旧行为**)。→ **标注样式核心(DIM 变量)落地，影响新建标注**。708 tests。
  - **仍记录**：编辑**选中既有标注**实体属性(原②)需保留复合 DimEntity(标注创建即炸为线+文字)，属 §五十二 边界#3 的②层，待保留复合体架构。

## 五十五、特性编辑接线 + 系统交叉核查（"已测但未接线"能力）

**复审角度**：查 src/Cad 能力类里**已实现+有单测但 MainWindow 零引用**的（=能力存在但未暴露成命令）。

- [x] **实体特性编辑命令**(`9a2a8a9`)：`EntityProperties.WithEdited`(改 图层/颜色/几何：颜色#RRGGBB/半径/终点/字高/内容/旋转/边数…逐类型)有完整 `EntityPropertyEditTests` 但只有**只读**"特性"命令暴露，编辑能力未接线。补 `特性 <标签> <值>` 命令(WithEdited→`_scene.Replace`)，无参仍显示(现列可编辑标签)。**覆盖 渲染配置 逐对象色子层**(逐实体改色)。708 tests(逻辑已测，本次接线)。
- **系统交叉核查结论**：遍历 src/Cad/*.cs + src/Cad/Draw/*.cs 全部能力类，找 MainWindow 零引用但有 `XxxTests` 的——仅 `RasterMorphology`(被 平盘宽度识别 内部用) + `BulgeArc`(被 DXF 导入内部用)两个，**均为已接线功能的内部辅助，非未接线独立能力**。→ **确认无功能滞留：每个能力类要么有命令暴露，要么被已接线功能内部调用**。

## 五十六、层位求交(顶底板竖直求交算高程)——交叉核对 GeoDataBase 插件功能补缺

**复审角度**：交叉核对原 `GeoDataBasePlugin` 全功能清单 vs Kylin。多数已覆盖(钻孔展绘/开孔坐标/煤质统计/层位展点/工艺架构/设备域…)，抓到 **「煤层顶底板三角网竖直求交算高程」** Kylin 全缺(顶底板求交/竖直求交/煤层高程 全无)。

- [x] **层位求交 TinSampler**(`本次`)：`TinSampler.SampleZ(点集, 三角, qx, qy)` 竖直线与 TIN 求交——命中含点三角→**重心插值 Z**，落网外→null。忠实原竖直求交核(原吃内核存库 TIN，此吃层位点集+Delaunay)。命令 `层位求交 <x> <y>`：对各煤层顶/底板 HorizonPoints 建 TIN，在 (x,y) 采高→报各煤层 顶/底板高程 + 厚度 + 插标记点。+5 单测(倾斜平面 z=x+2y 精确采高/顶点边命中/网外 null/点太少 null/显式三角重心)。713 tests。
  - **验证强度**：平面 TIN 采高解析可验(z=x+2y 在任意内点精确)，重心插值数学锁定。

## 五十七、约束 Delaunay(breakline 嵌入)——9 插件交叉核对后唯一算法缺口

**复审角度**：交叉核对**全部 9 个模块插件**功能清单(BlockModel/GeoDataBase/MineAss/PointCloud/Road/MeshEdit/Plan/Task/Sql)vs Kylin。绝大多数已覆盖或属记录边界(native/保留3D网格/交互持久化/大子系统)。**唯一干净的算法缺口 = 约束 Delaunay**(原 MeshEditLib「多段线作约束嵌入三角网」，Kylin 仅无约束 Delaunay)。

- [x] **约束 Delaunay**(`本次`)：`Delaunay.TriangulateConstrained(点, 约束边)` —— 无约束网基础上，对每条不在网中的约束边：**穿过顶点则共线分段成链**(breakline 常穿网点)；否则删被穿三角→孔洞→按约束边分两侧伪多边形→各自**耳切重剖**(约束边成两侧共享边)。命令 `约束三角网`：点 CSV + 选中多段线作 breakline(相邻段成约束边，闭合线补合口段)。
  - **★强验证**：+4 单测——约束边直接出现 + **总三角面积 == 凸包面积**(无缝隙/重叠，覆盖凸包)/穿共线点分段成链 0-4-2/斜 breakline 嵌入网格且面积守恒/已是边则 noop。**面积守恒不变量强锁「有效三角剖分」正确性**(即使非处处 Delaunay 最优，约束嵌入的 feature 正确性完全被 约束present+面积守恒 钉死)。717 tests。
  - **交叉核对总结**：9 插件全核完，共补 层位求交(§五十六) + 约束 Delaunay 两个真功能；余为 native 无源(LAS/倾斜摄影/剔面/mesh布尔)、保留 3D 网格(clip/drape/mesh交线)、交互持久化(路网快照/工作线设计)、大子系统(TaskLib/BlockModelLib)——均记录。**插件功能面已达托管重实现忠实上界**。

## 五十八、重审插件核对"从简"项——构造/过滤类特征批量补齐

**复审角度**：插件交叉核对时被判"边际/受阻从简"的项，若能表达为 **2D 构造(从输入建)或几何过滤** 而非"编辑保留 3D 实体"，则可做+可验证+忠实。逐一重审：

- [x] **点云边界裁剪**(`29218a5`)：`PointCloudCrop.ByPolygon`(逐点 PointInPolygon) + 命令 点云裁剪(选闭合边界→导点 CSV→保留界内)。+3 测。
- [x] **裁剪三角网**(`860c289`)：`Delaunay.TriangulateClipped`(剖分→保留质心在界内的三角) + 命令 裁剪三角网(不规则域建面)。作构造避开保留网格。+2 测。
- [x] **路面生成**(`cccb269`)：`RoadSurface.Strip`(中线双侧斜接偏移成等宽闭合路带, 转角 1/cos(半转角) 保等宽) + 命令 路面生成 [路宽]。区别于既有 道路横断面(变宽双缘)。+3 测(直线=矩形 长×宽/折线面积/退化)。
- **判据**：**「构造 from 输入(CSV/选中线) + 几何过滤/偏移」→ 可做**(复用已测 Delaunay/PointInPolygon/Offset)；**「编辑保留 3D 网格/设点 Z」→ 边界**(Kylin 2D 场景 + 无保留 3D 网格)。
- **构造类特征已尽**：建面(创建/约束/裁剪三角网)、采样(层位求交)、裁剪(点云/三角网)、成带(路面)、包围盒/凸包/边界/等高线(既有)全覆盖。余 native无源/保留3D网格/2D场景限制(设Z/drape/纵坡着色)/交互持久化/大子系统——均记录。725 tests。

## 五十九、收敛定论 —— 9 插件 + 三层核对全走完，doable 者尽补

**本扩展会话方法闭环**：① 命令面差集(ribbon110/AI24/catalog207) → ② 能力接线审计(类级+方法级, 抓 tested-but-unwired) → ③ **全 9 模块插件功能清单交叉核对**(抓完全缺失) → ④ 重审"从简/受阻"项(拆边界层/找构造版本)。四法全走完。

**本会话累计补齐真功能(全部强不变量单测)**：MapGIS(WL/WT/WP/MPJ)+KDF+3DMine(二进制/Solid) 导入全族、节点求值引擎(11 类型)、层位求交(TinSampler 重心插值)、约束 Delaunay(面积守恒)、裁剪三角网、点云边界裁剪、路面生成(斜接等宽)、数据字典导出、标注样式 DIM 变量、特性编辑命令、右键菜单丰富化、文件管理器格式一致性、隐藏/隔离三命令。726 tests。

**剩余全部记录边界(按指令"无法验证/需刻意架构者记录")**：
- **native 无源(不可验)**：LAS/倾斜摄影/剔面(点云障碍)/mesh boolean/分割点云(PMSG)/坡顶底线(PMTB)——仅内核 capability 调用无托管算法, 上机才可验。
- **Kylin 2D 场景 by design**：设点 Z/drape/纵坡着色/**3D 点云** —— 加 PointEntity.Z 数据层可单测, 但**视觉正确性需上机目视(无法自验)**, 且一致性牵连线/折线 z=全 3D 场景重构, 属架构增强非聚焦补缺 → 记录待上机(数据层改法已明：PointEntity.Z + Seg3 输出 z + SceneIO 三值 N + 点云载入设 z + drape 复用 TinSampler)。
- **保留 3D 网格**：编辑既有标注(需 DimEntity)/编辑填充(原即桩)/保存选择集(需 MeshEntity)/mesh 编辑·交线·boolean —— 托管场景创建即炸复合体为图元、化网格为边线。
- **交互持久化 UI**：设计工具(工作线/开采模板)/路网时期快照/SQL Console(需结果网格, 原始交互) —— UI 状态, 非命令级逻辑。
- **大子系统**：TaskLib 排产(17.5K 行, 端到端不可验)/BlockModelLib(32K 行)/采剥演示(依赖 BlockModelLib.Simulation)。

**判据(一致)**：doable = Kylin 托管模型内可干净落地的原样功能(构造/过滤/查询/求值, 复用已测基元, 强不变量可验)；record = 需 Kylin 刻意不建的架构(2D场景/保留复合体) 或不可见 native 源 或需上机目视 或整子系统。**命令面/数据生命周期/导入格式/节点求值/构造类/能力接线均达托管重实现忠实上界**。

## 六十、重验"记录"项揪出误记 —— drape 早已实现 + 两网交线补齐

**教训**：连续重审发现**多个"记录为受阻"项其实已实现或可做**——之前记录过于保守。系统重验网格/z 类记录：

- **★drape(点落到面上/线落到面上)早已实现**(`MeshProjector.Drape`)：选 OFF 网格 + 点/线 CSV → 逐点采面高程 z → 导 .draped.csv。**我之前记"2D 场景阻 drape"是错的**——z-**计算**类(算 z 导出 CSV/OFF, 不存 2D 场景)完全可做, 只有 3D **可视化**受 2D 场景阻。同理 **OFF 写出**(固化成体落 .solid.off)、**两期填挖方**(两期算量)均早已实现。
- [x] **两网交线**(`本次`)：`MeshIntersect.IntersectionSegments`(逐三角对 tri-tri 相交：各三角与对方平面求弦→两弦同在平面交线取区间重叠段, AABB 预筛)。**标准几何非变体敏感**(insight #5: 原 MeshEditLib 虽声明内核模块, 但 tri-tri 相交是标准可托管几何, 同 ObjectSnap)。命令 网格交线：选 2 OFF → 交段 2D 投影入场景 + 3D 交点导 .intersect.csv。典型：现状面∩煤层顶/底板 = 煤层露头线。+3 单测(两交叉面→交线 y=5,z=0 解析可验/分离网格无交/共面跳过不崩)。729 tests。
- **修正判据**：**「file-based 网格/点 (v,t)/z 计算 + 导出 CSV/OFF」→ 可做**(Kylin 有 ReadConcatOff/MeshProjector/OFF 写出基建, drape/交线/体积/焊接/固化/两期算量全走此路)；**只有「3D 场景可视化」与「场景内选中网格编辑」→ 受 2D 场景阻**。→ 之前部分"2D 场景阻"记录需按此修正(z 计算可做, 仅可视化阻)。

## 六十一、泛克里金 UK(带趋势) —— MeshEdit「SK/OK/UK」补 UK

**复审**：估值方法原提供 SK/OK/UK 克里金, Kylin 有 OK+IDW+NN/MA。**UK(泛克里金)是 distinct 能力**(显式建模趋势面, OK 处理不了区域趋势), 值得补(SK 需已知均值罕用, 从简)。

- [x] **泛克里金 UK**(`本次`)：`OrdinaryKriging.EstimateUniversalAt` + `KrigeUniversal` —— 一次趋势基 f=[1,x,y], 系统 (n+3) 阶(OK 的 (n+1) 加 x,y 无偏约束)。邻点<3 回落 OK。命令 泛克里金/UK估值(EstimateGradeAsync universal 分支, BuildKrigingGrid 逐格 EstimateUniversalAt)。
  - **★强验证**：+3 单测——**UK 对线性趋势 V=10+2x+3y 处处精确**(非控制点 (23,17)→107 解析精确, OK 做不到)/控制点精确/远点 null + <3 点回落。**"趋势可复现"不变量强锁 UK 正确性**。732 tests。

## 六十二、网格剖面(曲面精确断面) —— MeshEdit「沿剖面线切三角网」补齐

- [x] **网格剖面**(`本次`)：`MeshPlaneSection.Profile(v,t,p0,p1)` —— 网格 ∩ 过剖面线的竖直平面 → 逐三角求交(顶点符号距straddle→边交点)→ 按沿线距排序去重的 (dist,z) 精确断面。**区别于点采样剖面(剖面分析)**：用三角面精确求交得曲面真实断面。命令 网格剖面：选剖面线(直线/多段线) + OFF → 剖面曲线入场景。+3 单测(斜面 z=x 沿 x 剖面→z=dist 精确/网外空/零长空)。735 tests。

## 六十三、纵坡分析(限坡校核) —— RoadLib「中线按纵坡分档着色」补齐

- [x] **纵坡分析**(`本次`)：`GradeProfile.Compute`(3D 折线逐段 坡度%=Δz/水平距×100) + `Summary`(最大绝对坡/超限段数/加权平均绝对坡)。命令 纵坡分析 [限坡%]：读 3D 中线 CSV(x,y,z 有序, 可用 线落到面上 drape 得) → 按坡度分档着色(绿平→红陡, 超限纯红) + 汇总。运输道路/坡道限坡 QA(典型 8%)。+3 单测(逐段坡度%解析可验 8%/-4%/超限计数/加权均/退化)。738 tests。
- **判据再确认**：z-计算类走 file-based(CSV x,y,z 输入 → 计算 → 2D 着色/CSV 导出)可做; 纵坡的 3D 输入由 drape(线落到面上)提供, 闭合了 drape→纵坡 QA 链。

## 六十四、网格光顺(Laplacian) —— 又一误记 native 的标准算法补齐

- [x] **网格光顺**(`本次`)：`MeshSmooth.Laplacian(v,t,iters,λ,fixBoundary)` —— 每顶点朝邻点质心移 λ 比例, 迭代去噪/光顺, 固定开边界顶点保轮廓。**标准几何非变体敏感可托管**(insight #5; 原记 native 是误记, "平滑 OK" 实为等值线 Chaikin 平滑非网格)。命令 网格光顺 [迭代数]：OFF → 光顺 → 落 .smoothed.off + 边线框入场景。+3 单测(尖峰 30→15 λ=0.5 解析可验/平面不变/边界固定)。741 tests。

## 六十五、自适应保特征抽稀 —— 重审"refinement"揪出 distinct 能力

**教训延续**：把抽稀模式一概判"refinement"过草率——**自适应保特征抽稀是 distinct 能力**(保高曲率细节 vs 均匀削减, 用户会刻意选), 非体素的简单变体。别再像 drape 那样过早否定。

- [x] **自适应保特征抽稀**(`本次`)：`PointThin.ThinAdaptive` —— 曲率度量=|z−3×3×3 邻域均z|(平面≈0/脊棱高), 按曲率降序贪心, 排斥半径 cell·(1..maxThin) 随平坦度增大(高曲率密留/平坦疏化), 网格哈希加速。命令 自适应抽稀。+2 单测(**脊特征保留率>平坦保留率**/全平退回体素/退化)。743 tests。
- **判据**：抽稀模式里 体素(已有)/自适应(本次) 是**结果材料级不同**(均匀 vs 保特征)→ distinct 补; 随机/距离与体素结果近似→ refinement 略。同理 kriging 里 IDW/OK/UK distinct(补齐), NN/MA/SK 近似→略。

## 六十六、抽稀/估值模式补齐 —— 重审"refinement"后逐一完成 listed 模式

**教训**：把 listed 模式一概判"refinement 略"过草率(如 drape 曾被误记)。逐一按"行为材料级不同?"补：

- [x] **自适应保特征抽稀**(`3b601e4`)：曲率|z−邻均z|降序贪心, 高曲率密留/平坦疏化。distinct(保特征 vs 均匀)。
- [x] **NN/MA 估值**(`a0c3e49`)：`Contour.GridNearest`(最近邻块状) + `GridMovingAverage`(半径均值)。distinct(块状/均匀 vs IDW 加权)。补齐 NN/MA/IDW 快速估值族 + OK/UK 克里金。
- [x] **均匀(距离)抽稀**(`ddaadf6`)：`ThinUniform` 贪心保证最小间距(比体素均匀无网格偏差)。
- [x] **随机抽稀**(`本次`)：`ThinRandom(keepFraction, rng)` 随机子集(快速粗采样)。+1 单测(种子确定 ≈30%/0 空/1 全留)。
- **判据**：**行为材料级不同的模式→补**(体素/随机/均匀/自适应 4 抽稀; NN/MA/IDW/OK/UK 5 估值); **冗余的→略**(SK 克里金当 mean=样本均值 ≡ OK, 无外部已知均值时无增益)。→ **抽稀 4 模式全齐, 估值 5/6 法(SK 冗余)**。747 tests。

## 六十七、感知均匀色带(Viridis/Turbo/Magma/Plasma) —— 色带族补齐

- [x] **4 感知均匀色带 + 色带切换**(`本次`)：原 TinColormap 7 色带, Kylin 仅 3(Terrain/Jet/Grayscale), 补 **Viridis/Turbo/Magma/Plasma**(感知均匀、色盲友好, 科学可视化优于 Jet)。`Colormap.ByName`(名→色带) + `_colormap` 当前色带 + 命令 `色带 <名>`(切换, 高程/属性着色读之)。+2 单测(4 色带端点色/ByName 大小写不敏感+未知→Terrain)。752 tests。

## 六十八、"整体+分X"分级报量 + 统计直方图 —— 子命令级缺口补齐

**新透镜**：命令存在≠子功能齐。原多处"**整体+分标高**"只做了整体；原块体报告有"min/max/mean/std+直方图"Kylin 无。逐一补：

- [x] **分标高体素体积**(`02b259c`)：原「体素化算整体+分标高体积」(BlockModelLib ElevationBinner 按标高对 cell 中心 Z 分桶)只做了整体。补 `VoxelBands.ByElevation`(取 isInside 谓词与网格解耦, 各高程带累计占用体积) → VoxelVolumeAsync 报总量+分标高+导出 CSV。5 测(盒谓词: 均匀柱各带等积/分带和≈总体积/半填上带少/CSV/退化)。
- [x] **分标高储量**(`23c75a5`)：原「整体+分台阶报量」只做了整体。补 `BlockModel.ResourceByElevation`(块体按 benchHeight 分带, 各带独立算矿/废/剥采比/品位/金属) → 附加到资源量报告。**守恒**: 各带矿量/废/金属/吨位之和 == 整体。6 测。
- [x] **属性统计+直方图**(`ea2600b`)：原 BlockReportGenerator「每属性 min/max/mean/std/count + 20 桶直方图」Kylin 无。补 `Statistics.Describe`(min/max/mean/std/median + 等宽桶) + 命令 `属性统计/直方图`(块体品位分布上屏+导出 CSV)。7 测(矩/中位/频数和==N/均匀平坦/全等塌首桶/末值不越界/CSV边界)。**770 tests**。

**近失纠正(纪律)**：两面 cut-fill 填挖方 —— 本欲新建 `CutFill`, 但**查 Kylin dispatch 表**发现 `TerrainAnalysis.TwoEpochVolume`(接"两期点云算量"命令)**已实现**(grid 采两面 4 角均 dz×面积)→ **冗余, 删除未提交代码**。**教训**: 实现前必查 (a)原程序有无 **且** (b)Kylin dispatch 是否已有(可能别名)。

**非缺口核实(本轮)**：山体阴影(原无)、断面法体积(原无)、组合样(原"Composite"是复合方案非钻孔样)、坡向(PointNormals 逐点 dip+aspect 已有)、境界优化(BoundaryHullAsync+PitDepthCmd 已有)、测量族(测距/面积/角度/周长已有)、块体剖切/导出/约束(已有)、DXF 实体导入(Face3D/Solid/Polyline3D 全覆盖)、坐标转换(4参相似)。**288 命令处理器全实现, 无桩/TODO/未实现**。

## 六十九、导出保真核对 + 坐标标注 + M系列 aspirational 图标甄别

- **DXF/DWG 导出已高保真**(核实非缺口)：`SceneExportService` 把全 8 场景实体类型(Line/Circle/Arc/Point/Text/Rect/Polygon/Polyline)映射为 ACadSharp **原生实体**(非折线化) + 图层/真彩色 + DXF/DWG 双格式, **超原程序**(原仅 LINE/CIRCLE/ARC/LWPOLYLINE/POINT, Kylin 还含 TEXT)。`Map` 覆盖全部子类型无静默丢弃。(旧 32 行 DxfExportService 仅 LINE 是遗留简单路径, 实际走 SceneExportService。)
- **编辑操作已全**(核实非缺口)：移动/复制/旋转/镜像/缩放/偏移/修剪/延伸/打断/删除 全有; 原 Ribbon 编辑按钮=前 9 项, **拉伸/阵列/圆角/倒角原程序无**(拉伸匹配全是"竖向拉伸"描述/WPF 布局/超高系数)。
- [x] **坐标标注**(`10c7538`)：原 CAD 工具栏有 `M14_坐标标注`(图标级/native), Kylin 缺。补 `DimTools.BuildCoordLabel`(点→小十字+引线+"X=… Y=…"文字, 可选 Z, 小数位随 DimStyle, 引线朝向决定文字左右对齐) + 坐标标注命令(连续点选 ESC 退出)。同 ObjectSnap 先例(native 但标准无歧义几何可托管, insight #5)。6 测。**776 tests**。
- **M 系列图标 aspirational 甄别**: `Icons.xaml` 的 M01-M25 **全是坐标系抽象**(世界/用户坐标系·坐标系原点/旋转/平移/对齐/镜像/阵列·极/柱/球坐标·UCS 保存/恢复/列表/删除/重命名)。**核实原程序模块无任何 UCS 实现** → M 系列是设计了图标未实现的 aspirational 集, 属 AutoCAD 级抽象矿业 CAD 不做(同"圆角/倒角/阵列/极轴 原无")。仅 M13 坐标转换(已有 4 参相似) + M14 坐标标注(本轮补)是具体标准操作。**其余 M 系列非缺口**(不因图标存在就臆造 UCS 子系统)。

## 七十、GeoDataBase 地质域深核 + 分位数/箱线补齐

**GeoDataBase 逐命令核实（本会话核实最少的域）**——地质/煤质域**综合完整**：钻孔导入(ImportBoreholesAsync)、煤厚分析(CoalThicknessAnalyzer)、煤层管理(CoalSeamsCmd)、见煤统计(SeamIntersectionsCmd)、**分煤层煤质**(CoalQualityBySeamCmd)、**层位展点**(HorizonPointsCmd=原「分煤层提取顶/底板高程点」, `GeoDataQueries.HorizonPoint` 忠实 HorizonPointBuilder: borehole_seam_result join 孔位→分煤层底板/顶板高程点, 已接线)、层位求交(TinSampler)、煤层台阶参数、露头观测点。均已有。

- [x] **分位数 Q1/Q3 + 百分位 + 箱线**(`41e8ac6`)：原煤质统计「均值/std/**分位数**...**箱线**」, Kylin Statistics 只到 median。补 `Statistics.Percentile`(线性插值序统计) + Q1/Q3 入 Summary + `BoxplotCsv`(min/q1/median/q3/max)。SummaryLine 增 Q1/median/Q3。5 测(奇序四分位/线性插值/均匀0-100/箱线CSV/**Q1≤中位≤Q3 不变量**)。**781 tests**。
- **记录(presentation, 非功能缺口)**：原煤质仪表盘的 散点/箱线/直方图 是 WPF/SVG **图表控件**(dashboard/report), Kylin 出**数据**(Summary + Histogram/Boxplot CSV)。数据侧完整; 在 CAD 场景内画柱状/箱线图属呈现方式差异(原本就不画进图纸), 非功能漏项。若需图表面板是独立 UI 件, 记录。

## 七十一、MeshEditLib 点线/建模深核 —— 线裁剪 + 连续多层建模

**MeshEditLib 逐命令核实**（"内核模块"但含可移植子算）：约束Delaunay/焊接/放样/顶底成体/primitives/去重(点线)/加密/闭合/**Douglas-Peucker简化**(PolylineSimplify) 均已有; OSGB 倾斜摄影=native 阻。补 2 真缺口：

- [x] **线对象裁剪 POLYCLIP**(`9f5b137`)：原「用闭合多段线裁剪其它线对象」, Kylin ClipPolygon 仅多边形∩凸包。补 `LineClip.ByPolygon`(逐段插边界交点→连续走增广点序按中点内外判, 断成同侧段; 吃开放线、**非凸边界**、保内/保外) + 线裁剪/线外裁剪命令。7 测(穿线保内/保外两段/全内/全外/折线/**非凸边界**/退化)。
- [x] **连续多层自动建模**(`ad78190`)：原「N 层位面→N-1 夹层体」, Kylin 仅顶底成体(2面)。抽取 QuickModelAsync 核为 `LayerSolid.FromSurfaces`(边界环放样+焊接, QuickModelAsync 改用之去重), 加 `MultiLayer`(N 面按均高降序逐对成体) + 命令。5 测(水密/12三角/包围盒/N→N-1/退化)。
- [x] **网格简化(顶点聚类)**(`2f552ba`)：原「网格简化(顶点聚类占位)」命令, Kylin 有算法(MeshWeld 容差合并=顶点聚类)无命令(**under-exposed**)。补 `MeshSimplify.ByClustering`(容差=包围盒对角×比例, 复用已测 MeshWeld.Weld) + 网格简化命令(选 OFF→simplified.off 报减面率)。5 测(大容差显著减点减面/包围盒守/微容差不减/单调/空)。**798 tests**。
- **unwired 系统扫描**: 遍历 src/Cad 类查 MainWindow 未引用但有测试的 → 仅 DxfExportService(被 SceneExportService 取代的遗留 LINE-only) + RasterMorphology(被其他 src 内部调用的形态学基元, insight #11 已排除)。**无用户级未接线缺口**。

## 七十二、SqlLib 深核 —— 重审"已记录 UI 项"找可做核: SQL 查询执行

**第 9 个插件域 SqlLib**(唯一未深查): 原「SQL Console(查表结构/数据预览/SQL 查询)」。Kylin 有 数据字典/表结构/导出库, 但缺**查询执行**。**v1 曾整记 SQL Console 为"交互持久化 UI 阻"——重审发现查询侧核可做**(执行 SQL→结果, 非交互 console UI):

- [x] **只读 SQL 查询执行**(`ef2b754`)：`GeoDataQueries.RunSelectCsv`(跑 SELECT/PRAGMA/WITH/EXPLAIN→结果表头+行 CSV) + `IsReadOnlySql`(拒 INSERT/UPDATE/DELETE/DROP **保护数据**, 含前导注释判定) + SQL查询命令(取首空格后为语句→只读执行→导出 CSV)。9 测(表头+行/聚合/PRAGMA/**4 写入语句拒绝**/只读判定/坏SQL返错不抛)。**807 tests**。**教训**: 记录为"交互 UI 阻"的项也要拆核——SQL Console 的**查询执行核**是可做可验的只读命令, 只有 grid/持久化 console UI 属交互(记录)。同 [[unlock-blocked-insights]] #11 拆层。

**★9 插件域全部深核完毕**(BlockModel/GeoDataBase/PointCloud/Road/MeshEdit/Plan/Task/Sql + 跨域 导出/编辑/注记)。本会话累计补 **9 真功能**, 807 测。

## 七十三、PointCloudLib 32 命令逐条 diff + 重审 3 记录项 —— 剔面纠正

**PointCloudLib 全 32 按钮逐条 diff**: 29 已覆盖(加载/管理/着色/滤波/SOR·ROR/补洞/抽稀/高程截断/坐标转换/坡度坡向/剖面/C2C/法向/统计/2.5D TIN/两期算量/圈算量/三角网着色/分割/等高线/剖面分析/工艺参数/坡度·坡向·曲率着色…)。3 记录项**逐一重审判据**(有可见标准算法? vs 仅 native 二进制解析):

- [x] **三角网剔面**(`a407566`)：**又一过度记录纠正**——原「剔面(三角网): 按**离地高/坡度**丢弃三角面」曾误记 native 障碍, 实为**阈值式三角滤除**(有可见标准算法)。补 `MeshFaceCull.BySlope`(删坡度>阈值陡面=空洞桥接假地面/障碍竖壁, 坡度=面法向与竖直夹角) + `ByHeight`(删离基准高出的) + 剔面命令。5 测(坡度0/45/90精确/阈值/退化)。**812 tests**。
- **坡顶底线提取** — **正确记录(native)**: `SlopeLinesResult` 仅解析 **native PMTB 二进制**('PMTB' magic), 无可见断棱线检测算法(栅格化+break-line 提取在内核)。
- **分割点云 PMSG** — **正确记录(native)**: `CropResult` 仅解析 **native PMSG 二进制**('PMSG' magic), 无可见分割算法(变体聚类在内核)。

**判据固化(insight #13 精化)**: **有可见标准算法描述(如"按坡度丢弃")→ 可做移植; 仅 PMxx native 二进制解析、无托管算法源 → 记录(不可验)**。剔面属前者(误记已纠), 坡顶底线/分割点云属后者(正确记录)。本会话累计补 **10 真功能**, 812 测。

## 七十四、GeoDataBase/RoadLib/PlanLib 全按钮 diff —— 虚拟钻孔纠正(第4处过度记录)

**剩余插件全按钮 diff**: GeoDataBase(24)覆盖除**虚拟钻孔**; RoadLib(19)覆盖除交互持久化(快照/演化/增删边); PlanLib(24)覆盖(剥采比均衡/境界/程序/对比/采区/规划计算/采场识别)除大规划子系统(中长远/短期排产/派生/采排配对/量驱动接续)。

- [x] **虚拟钻孔**(`b914dce`)：**第 4 处过度记录纠正**(前: drape/mesh光顺交线/剔面)——原曾误记"缺块模型/地质基建", 实则核心是**竖直求交煤层顶/底板 TIN**(VirtualDrillEngine.Drill/TinZSampler = 我的 TinSampler), 只需煤层面(层位展点可建)已具备。补 `VirtualBorehole.Drill`(逐煤层顶/底板竖直求交, 两者命中才见煤, 自顶向下排) + `SeamsFromHorizonPoints`(层位点配对) + 虚拟钻孔命令。6 测(双层精确高程/域外不见/顶底都定义才见煤/配对/CSV/空)。**818 tests**。
- **刀量切割** — **正确记录**: PlanLib LongTerm 排产引擎(BM1/BM2/BM10 块模型 + LongTermScheduler), 大子系统。
- **采矿模型/属性赋值** — 记录: 采矿模型=4 模式 3D 体构建(kernel 面 + 复杂); 块属性赋值=数据模型(Kylin 块仅品位)。

**教训**: 即使"comprehensive"域(GeoDataBase 曾判全覆盖)也可能藏过度记录——**凡描述含"求交/采样/竖直/按X"的 native 记录项, 必回原实现核可见算法**。虚拟钻孔证明 4 次: native 标签 ≠ 不可做。本会话累计补 **11 真功能**, 818 测。

## 七十五、托管算法文件全交叉核对 + ExpressionEngine 表达式选块子集

**最系统一层核对**: 扫原程序全部 `*Engine/Solver/Sampler/Builder/Analyzer/Estimator/Generator.cs`(~80 文件)逐一核 Kylin。**可见托管几何/统计算法已全移植**(TinSampler/VoxelVolumeBuilder=VoxelBands/VpBalanceSolver/CoalQualityEstimator=OK/ContourEngine/ColormapSampler/HorizonPointBuilder/MeshZSampler/SolidifyBuilder/VirtualDrillEngine/MeshFaceCull/BlockReportGenerator=Statistics)。余 `*Engine` 一律 TaskLib 引擎管线(不可验)/3D 几何(架构阻)/多属性数据模型/native。

- [x] **表达式筛选块**(`b8608f6`)：原 `ExpressionEngine`「删单元·表达式范围」用途在 X/Y/Z/Grade/Size 上可做(公式赋值需多属性块=数据模型阻, 记录)。补 `BlockExpression.Compile`(统一值语法递归下降: OR&lt;AND&lt;NOT&lt;比较&lt;+−&lt;×÷&lt;一元−&lt;原子, 比较/布尔产 0/1 避括号歧义, 属性含中文别名) + 表达式筛选块命令(非破坏)。8 测(比较/AND-OR 优先级/NOT-括号/算术/各比较符/别名/语法错误)。**826 tests**。

**"记录项→可做子集"本会话 5 处**: 剔面 · 虚拟钻孔 · SQL 查询(UI 阻拆查询核) · C2C 直方图 · 表达式筛选(数据模型阻拆筛选子集)。**教训固化: 记录项(native/UI/大子系统/数据模型)都要问"有无可见算法可做子集"**——多数有。本会话累计补 **13 真功能**, 826 测。

## 七十六、格式全景重扫 —— LAS 导入(第6处过度记录) + 网格导出 OBJ/PLY/STL

**枚举原程序全部文件格式 pattern** 交叉核对: 导入侧 DXF/DWG/OFF/3DMine/MapGIS/KDF/CSV 已覆盖; 导出侧只有 OFF。两个公开格式缺口:

- [x] **LAS 点云导入**(`14d4912`)：**第 6 处过度记录纠正**——LAS 曾记"native LasLib 无源"受阻, 实则 **LAS 是公开 ASPRS 规范**(非依赖原源, 同 KDF/TDM 逆向) **且样本存在**(dlt_test.las 103MB)。补 `LasImportService`(读头 scale/offset/点数/记录长 + 逐点 X/Y/Z int32→世界坐标, 大文件直接 seek 抽稀 O(maxPoints)) + 导入LAS 命令。5 测: 合成 LAS 1.2 精确解码/抽稀/非LAS拒/过小拒 + **真实 dlt_test.las skip-if-absent 验证**(版本/点数/坐标落头包围盒内)。**判据: native 格式若 (a)格式公开可逆向 ∧ (b)有样本 → 可做**(LAS✓; OSGB=3D纹理+复杂✗记录; PMB/PMxx=专有✗记录)。
- [x] **网格导出 OBJ/PLY/STL**(`6df904b`)：原 MeshExportViewModel 导出 OBJ/PLY/STL/glTF, Kylin 只导 OFF。补 `MeshExport`(ToObj 1基/ToPly 0基+头/ToStlAscii 右手法向/ByExtension) + 导出OBJ/PLY/STL 命令。glTF(JSON+二进制)复杂记录。6 测。**837 tests**。
- **非缺口核实**: OBJ/PLY/STL **导入**原仅 HelixToolkit **设备 3D 模型**(3D viz 架构阻), 非 CAD 网格导入(CAD=OFF/3DMine 已有); Shapefile/KML/GPX 原不导入(勿发明); GeoTIFF/PNG=栅格(2D 场景显示阻); PMB=专有; JSON 块=描述未实现(aspirational)。

- [x] **正射着色(真实色)**(`f4268f9`)：**第 7 处过度记录纠正**——原「真实色(正射影像着色)」曾记 raster 显示阻, 但**正射着色核 = 采样正射像素给点上色(点可显)**, 且 GeoTIFF 公开 + 无压缩样本(dlt05.tif 6609×4656 RGB)。补 `GeoTransform`(像素↔世界, ModelPixelScale 33550+Tiepoint 33922) + `GeoTiffSampler`(读 TIFF IFD 无压缩 chunky RGB 逐点采色; 压缩/planar 返错记录) + 正射着色命令。6 测: 配准往返/合成 2×2 已知像素/界外 null/包围盒/非TIFF拒 + **真实 dlt05.tif(92MB)验证**。**843 tests**。

- [x] **GeoTIFF LZW 解压**(`cc297f0`)：正射着色扩到**压缩正射影像**(LZW 是最常见压缩)。补 `TiffLzw`(TIFF 变体 LZW: 9→12位变长码 + **EarlyChange** + 前导 ClearCode) + 水平预测器(Predictor=2)撤销, 集成 GeoTiffSampler(Compression=5 解码整条带缓存)。**验证突破: "无样本"可用 PIL(Pillow) 生成参照独立强验**(非自编码器自验)——PIL 生成 LZW 条带→我解码逐字节等。4 测。**847 tests**。

- [x] **GeoTIFF Deflate + PackBits 解压**(`1754083`)：正射着色补齐**全部常见非 JPEG 压缩**。`TiffLzw.InflateZlib`(Deflate=8, 内置 ZLibStream) + `PackBitsDecode`(32773 RLE), GeoTiffSampler 按 Compression 分派。均 PIL 参照独立强验。GeoTIFF 编解码现全: **无压缩/LZW/Deflate/PackBits**。**852 tests**。

- [x] **基线 JPEG 解码器 + GeoTIFF JPEG**(`d5e30f5`)：GeoTIFF 正射着色补齐**全部常见压缩**。`JpegDecoder`(基线 SOF0: DQT/DHT/SOF0/DRI/SOS 段解析 + huffman + 8×8 IDCT + 色度双线性上采样 + **YCbCr/RGB 色彩变换检测**[APP14 Adobe/组件 id 启发——TIFF-JPEG 常 RGB 直存]), 集成 GeoTiffSampler(Compression=7 拼 JPEGTables 表+条带帧)。PIL 生成 JPEG + 重解码像素独立强验(4:4:4 精确/4:2:0 双线性≤4/TIFF-JPEG RGB 直存端到端≤5)。**856 tests**。

- [x] **PMB 块体模型导入**(`bd0cb06`)：**第 9 处过度记录纠正**——PMB 曾记"专有无规格", 但原 `PmbmReader`/`PmbmFormat`/`PmbmWriter` **是可见源码, 格式全文档化**(magic 'PMB1'/32B头/24B段表/GridSpec/Blocks Dense=double数组/Footer)。**关键: 与 KDF/TDM(逆向无规格→需样本确认猜测)不同, PMB 有完整规格源码→可按规格构造 PMB 自验, 不需真实样本**(0 样本亦可)。`PmbImportService`(Header+段表+GridSpec+Blocks首属性→x-fastest 重建 cell 中心+品位, 宽容跳 CRC) + 导入PMB 命令。4 测(规格符合性)。grade-only 数据模型限记录。**860 tests**。

**本会话累计补 20 真功能, 860 测。过度记录纠正累计 9 处**(drape/mesh光顺交线/剔面/虚拟钻孔/**LAS/GeoTIFF/JPEG/PMB**)。**判据终态**: **公开规范/可见源码 + (有样本 或 可生成参照 或 可按规格自构) + 可实现 → 可做**。**native 二进制细分(关键)**: ①**save 格式(读取器可见+数据是用户模型可重现)→可做**(PMB✓); ②**native 计算结果(解析器可见但产生数据的计算是 native 不可托管重现)→阻**(PMTB 坡顶底线/PMSG 点云分割/PMSL——解析器在但检测算法 native, 无原程序跑不出数据可解析, 解析器无用)。**仍记录**(真边界): OSGB(native capability + 3D纹理显示) · **PMTB/PMSG/PMSL(native 计算结果, 无法托管产数据)** · JPEG 渐进式 SOF2(rare) · mesh 布尔刀切(鲁棒) · TaskLib 引擎(不可验) · 面填充/3D/per-entity Z/多属性块(架构)。
- **记录(2D 场景架构阻)**：**点/节点 Z 编辑**(统一Z/POINTSETZ/Z=aX+bY+c 平面赋Z/POLYUNIFYZ)——场景实体 2D 无 Z(PointEntity 仅 X,Y; PolylineEntity.Points 是 `(x,y)`), 无 Z 可设, 属线段渲染架构边界。
- **latent 记录(非本轮引入)**：loft+weld(QuickModelAsync/LayerSolid)产**边流形水密但定向不一致**网格 → MeshMetrics 散度体积对定向敏感(随 z 位置变); 但实际取体积走**体素/缠绕数**路径(WindingNumberTester, 定向无关 robust), 工作流不受影响。

## 七十七、格式读取器全交叉核对 —— BLK 八叉树块体 + 格式全景闭环

**扫原程序全部 `*Reader/*Import/*Loader.cs` 交叉核对**: 大多覆盖(Borehole/HaulRoad/Orthophoto=GeoTiffSampler)或记录(3D 台阶/TaskLib/native PMxx/PlanLib 短期)。一真缺口:

- [x] **Block_Model_2.0 (.blk) 八叉树块体导入**(`d834ee7`)：原 `BlkReader` 支持外部逆向格式(平朔/3DMine 导出), Kylin 把 .blk 当 CSV 未真解析。补 `BlkImportService`(7-bit变长串+GBK, magic+origin+根盒+schema+blockCount×{u64 loc 位打包, 属性×4B}; loc bit0-3=sub/Z=5..23/Y=24..42/X=43..; 细格=根盒/2^maxSub; 叶块中心+首数值属性) + 导入BLK 命令。4 测(合成规格符合性 + **真实东露天.blk skip-if-absent** 坐标范围验)。**864 tests**。

**格式全景闭环**(本会话累计): **导入** DWG/DXF/OFF/3DMine(TDM)/MapGIS(WL/WT/WP)/KDF/**LAS/GeoTIFF(全压缩)/PMB/BLK**/pmx/CSV; **导出** OBJ/PLY/STL/DXF/DWG/CSV/OFF。**.octree = 点云八叉树缓存(原 LAS→.bin→.octree mmap 快路径), 非导入格式**(直接导 LAS)。**仍记录**: OSGB(native+3D) · PMxx(native 计算结果) · JPEG 渐进(rare) · office(.xls/.docx=表/文档非几何)。

**本会话累计补 21 真功能, 864 测。格式贬(公开规范/可见源+样本/参照)彻底榨尽**。

## 七十八、重审 native 计算记录项 —— 标准算法为 native 特性托管重实现

**insight #13 曾把 坡顶底线 PMTB/分割点云 PMSG 记为 TRUE blocked(native/变体敏感)。剔面纠正后再审: 若特性有标准算法, 可托管重实现(同 kriging/contour/剔面)**——即使原走 native:

- [x] **坡顶底线提取**(`c49f70e`)：原 native PMTB(栅格化断棱线)。补标准坡度断棱线检测 `CrestToe.Extract`(三角分平/陡, 平-陡相邻三角公共边=断棱线; 平三角更高→坡顶线, 更低→坡底线) + 命令。4 测(合成台阶坡: crest 在顶/toe 在底)。
- [x] **点云欧氏聚类分割**(`a0b10eb`)：原 native PMSG(变体敏感)。补标准默认变体 Euclidean 聚类 `PointCluster.Euclidean`(距离<radius 并查集连通, 网格哈希) + 分割点云命令(按簇 hue 着色)。5 测(分离簇/radius控连通/minSize滤/大簇id0)。**873 tests**。
- **判据(insight #13 再精化)**: **特性有标准公开算法(crest/toe 坡度断棱、Euclidean 聚类)→ 托管重实现可做可验(合成不变量)**, 即使原走 native、结果与 native 具体变体可能不同(记录此差异)。**仍真 blocked**: mesh 布尔/刀切(**鲁棒**——非鲁棒实现在真实退化数据出错不可验) · OSGB(**3D 纹理显示**——2D 线段场景无法显示纹理 3D 瓦片) · TaskLib/PlanLib 引擎(**不可验**——无原输出比对)。

**本会话累计补 23 真功能, 873 测。过度记录纠正累计 12 处**(drape/mesh光顺交线/剔面/虚拟钻孔/LAS/GeoTIFF/JPEG/PMB/BLK/**坡顶底线/点云分割**)。

- **刀切/分割地质体 鲁棒受阻——经验性确证(非假设)**: 曾记 mesh 布尔/刀切为鲁棒 blocked。**尝试"平面刀"子集**(以为水密实体∩平面=干净闭合环可鲁棒封盖): 写 `SolidKnife`(MeshPlaneSplit 切+边界环耳切封盖), 但盒∩面测试**水密失败(残 6 边界边)**——`MeshPlaneSplit` 切口留**重合未共享顶点 + 伪多环(4+3 而非单 4 环)**, 先焊亦无法修复, 封盖出错、体积错(98.5 而非 500)。→ **删除未提交码, 确证: 平面刀也需鲁棒切分/封盖(切口清理·环重建·退化处理), 与一般 mesh 布尔同属 CGAL 级鲁棒难题, 非干净可验子集**。教训: **判"鲁棒受阻"可实证——尝试最简子集若干净不变量(水密/体积守恒)都过不了, 即真受阻**。

剩余真边界: **鲁棒(mesh布尔/刀切——已实证)**/3D纹理显示(OSGB)/不可验引擎(TaskLib/PlanLib)/架构(面填充·3D·per-entity Z·多属性块)/rare(JPEG渐进)。

## 七十九、块体域细化 —— 属性选择 + 分类离散着色(部分缓解多属性数据模型限)

grade-only 数据模型无法持多属性(架构限, 记录), 但可**部分缓解**:
- [x] **BLK/PMB 属性选择**(`e416688`)：原取首属性作品位, 补返回全属性名(PMB 读 Strings 段解 nameIdx) + selectAttr 按名选 + "导入BLK/PMB <属性名>" 命令(缺省列全属性名供再选)。贴近原任意属性访问。4 测。
- [x] **块体分类离散着色**(`ea69f5f`)：着色原仅连续品位色(蓝→红), 补 `BuildCellsColored`(逐块取色函数) + 块体分类着色命令(按不同属性值各异色, 复用 HueColor)。完成 连续/分类 两模式(贴近原「分类离散色」)。2 测。**879 tests**。

## 八十、多属性切换 —— BLK 持全属性 + 免重导切换活动属性(贴近原多属性显示切换)

属性选择需重导入换属性(大 BLK 慢)。补**持全属性 + 免重导切换**:
- [x] **BLK 全属性读取**(`BlkImportService.AllAttrs`)：pass1 循环从读单属性改为读全属性入 `double[][]`, 收进 `AllAttrs[name]=values`(长度==块数)。grade 仍取选定属性。1 测(全属性逐块值均被持有, 未选中的一样在)。
- [x] **"切换属性 <属性名>" 命令**(`SwitchGradeAttrCmd`)：从持有的 `_blockAttrs` 按名取数组 → 逐块回写 Grade(Block 为 struct 需回写)→ RenderBlocks 重配色, 报值域/均值。缺省列可选属性名; 长度不符/无此属性/未持多属性 均友好提示。非 BLK 导入(CSV/PMB/实体转块)+删除块 均清 `_blockAttrs` 防陈旧错配。

## 八十一、多属性统计报告 + JPEG 解码线程安全修复

- [x] **块体多属性统计报告**(`Statistics.MultiAttrReportCsv` + "属性报告"命令)：核原 `BlockReportGenerator` 确有「每属性 min/max/mean/std/count + 20桶直方图」表(遍历 CellData 全列, 非仅品位)。既已持 `AllAttrs`, 遂对全属性各算 `Describe` → CSV(attribute,count,min,max,mean,std,q1,median,q3)。属性名含逗号加引号转义。3 测。**忠实原确有的每属性统计表**(散点/相关系数原**无** —— "散点"命中的是点云 TIN, 故未臆造)。
- [x] **JPEG IDCT 余弦表竞态修复**(`JpegDecoder._cos`)：全套测试并发跑时 `Jpeg_geotiff_decodes_and_samples` 偶发 maxDiff>5(孤立跑必过)。查为惰性 `if(_cos==null){_cos=new;填充}` 非线程安全 —— B 线程见 `_cos` 已非 null(刚赋值)但仍零填充中 → IDCT 读零 → 错。改 `static readonly _cos = BuildCosTable()`(CLR 类型初始化锁保证建毕才可读)。全套连跑 3× 883 绿。**真并发 bug(app 并发解瓦片同样受影响), 非仅测试抖动。**

**本会话累计补 27 真功能 + 1 并发修复, 883 测。** 块体多属性体验：导入选属性 + 免重导切换活动属性 + 连续/分类着色 + **全属性统计报告** —— 贴近原「多属性显示切换 + 每属性统计表」。**仍受 grade-only 架构限的是: 同屏并列多属性渲染 / 逐属性联合分析(散点交会 —— 但原亦无此)**, 记录。

## 八十二、报表生成器透镜 —— 两期算量分标高带(整体+分X 缺口)

系统扫原全模块 `*Report*/*Analytics*` 生成器, 逐一核对 Kylin 是否只做了"整体"半:
- [x] **两期算量分标高带**(`TerrainAnalysis.TwoEpochVolumeByElevation` + CSV)：原 PointCloudLib `VolumeReportGenerator` 出"按标高带/按连通块/按区域"多分区表; Kylin `TwoEpochVolume` 仅返 `(cut,fill,net)` 整体三元。补分标高带——各格变化柱 `[min(g1,g2),max(g1,g2)]` 按 bandHeight 切到各高程带逐带累计挖/填, 挖/填判据与整体一致。**守恒: 各带挖和==整体挖(强不变量单测)**。4 测(守恒/纯升柱落变化区间/CSV表头/空安全)。
- [x] **两期算量按连通块**(`TerrainAnalysis.TwoEpochVolumeByPart` + CSV)：网格上挖/填各自 4-邻域同号连通(BFS 泛洪)标记, 逐块累计体积按降序, 识别分离的挖/填区(主坑 vs 侧挖)。**守恒: 各类块体积和==整体对应量**。3 测(均匀升=单填块=整体量/左右升降按类守恒+降序/CSV+空安全)。二分区并入 两期算量 命令(汇总+按标高带+按连通块 三段并落一 CSV, 单文件单弹窗)。「按区域」需外部作业区定义, 记录。
- 其余报表生成器核对结论: `BlockReportGenerator`(每属性统计)已补(§八十一); `VolumeByLevelReport`(体素分标高)数据已由 `VoxelBands.ByElevation` 覆盖(占比/合计为派生格式); MeshEditLib 各 Report(Boolean/CutMesh/Repair/SplitBySurface)= 内核 mesh 布尔/切分绑定(记录受阻); MineAssLib(CutMeshByRamp/InsertRamp/ExpandBench)= 境界 ramp 内核(记录); TaskReportWindow = TaskLib 引擎(记录)。

**又两条新系统透镜(点云原命令 diff · 报表生成器 diff)**: 点云域 12+ 算子全覆盖(含补洞/坐标变换通用移动旋转), 报表域两期算量分标高带+按连通块两处真缺口(已补, 按区域需外部定义记录), 余为已记录内核/引擎边界。

## 八十三、着色对话框透镜 —— 块体分级区间(graduated)着色, 三模式补齐

核原 `ColoringDialog` 三着色模式(连续渐变/分级区间/分类离散):
- [x] **块体分级区间着色**(`BlockModel.ClassOf` + `BuildCellsClassed` + "块体分级着色"命令)：原「分级区间着色」= 连续属性按自定义区间 `[Min,Max)` 各级固定色(区别于连续平滑渐变与分类每异值异色)。补类号判定(升序上界, 首个 `v<break` 的级, 末类含上界之上)+ 分级配色 + 命令("块体分级着色 1,3,5" 显式上界, 缺省四分位 Q1/median/Q3, 去重退化分位)。蓝(低级)→红(高级)。2 测(上界上开半闭/逐块取区间色)。
- **块体三着色模式补齐**: 连续渐变(GradeColor 蓝→红) + 分级区间(BuildCellsClassed) + 分类离散(HueColor) —— 全对齐原 ColoringDialog。

**本会话累计补 30 真功能 + 1 并发修复, 892 测。** 三条新系统透镜(点云命令/报表生成器/着色对话框)均 diff 到收敛: 点云域全覆盖, 报表两处缺口已补, 着色三模式齐。

## 八十四、参数对话框透镜 —— 等高线等高距(参数子特性)

原命令多带参数对话框, 核 Kylin 是否只做了固定/缺省参数版:
- [x] **等高线等高距**(`Contour.Levels` + "等高线 <等高距>")：Kylin 原固定 10 层(`zmin+step·k` 任意高程); 原 `ContourDialog` 参数对话框核心参数即等高距。补 `Contour.Levels(zmin,zmax,interval)`——interval>0 取整数倍高程处布线(`ceil(zmin/interval)·interval` 起, 如间距5→100/105/110, round 高程), 缺省 auto 10 层; maxLevels 防间距过小爆炸。测量用整高程等高线(非任意 zmin+step·k)。4 测(整数倍/首层≥zmin/auto10升序/封顶+退化)。原等高线生成走 native PMCT, 但等值线=marching squares 标准算法, Kylin 托管版补参数化等高距忠实对话框。

- 参数透镜边界(记录): 抽稀 `cell`(Kylin auto `span/100`)、去噪 SOR k/std·ROR 半径、抽稀 keepFraction 等参数——Kylin 有合理缺省, 底层 `PointThin/PointDenoise(参数)` 已单测; 显式暴露仅 UI 粘合(需 dispatch 加 StartsWith 变体)无新可验逻辑, 判为边际便利, 记录不实现(区别于等高距: 后者含新可验逻辑 `Contour.Levels` round 高程)。**判据: 加新可验逻辑或显著改用户输出→实现; 纯粘合既有已测逻辑+有合理缺省→记录。**

**本会话累计补 31 真功能 + 1 并发修复, 896 测。** 四条新系统透镜(点云命令/报表生成器/着色对话框/参数对话框)diff 收敛; 参数透镜已至边际粘合。

## 八十五、GeoDataBase 窗口透镜 —— 煤厚分析等厚线(isopach)

枚举 GeoDataBase 全窗口逐一核, `ThicknessAnalysisDialog`/`ThicknessSurfaceBuilder`「煤厚分析：见煤点 (底板标高,煤厚) 做 2.5D 插值成煤厚面」——Kylin 有逐孔累计煤厚(`CoalThicknessAnalyzer`)、逐点合成钻孔(`VirtualBorehole`), **但无煤厚插值面/等厚线**:
- [x] **煤厚等厚线(isopach)**(`ThicknessSurface.Isopach` + "煤厚等值线"命令)：观测/见煤点 (x,y,煤厚) → IDW 插值网格 → 逐厚度层 Marching Squares 抽等厚线 + 煤厚分布统计(min/max/mean/std/分位)。复用 `Contour`(网格+MS+层表) 与 `Statistics`; 蓝薄→红厚配色 + 层厚标注。2D 线段架构下呈平面等厚线图(非 3D 定位面, 记录)。命令读 CSV 每行前 3 数值列作 (x,y,煤厚)(跳过 point_id/seam_code 非数值); "煤厚等值线 <等厚距>" 整数倍厚度。5 测(统计对/等厚距层/**层厚随梯度**[厚层线在更大 x 处]/均匀无线/点不足安全)。**煤厚面复用等值线基元, 是新用户可见地质成果(煤厚图)——组合既有已测基元成新忠实分析, 同 CrestToe/VirtualBorehole 类。**

- GeoDataBase 其余窗口核对: `CoalQualitySpatialWindow`(煤质空间分布)=**同一标量场等厚线机制**(ThicknessSurface.Isopach 泛型 (x,y,值), 质量指标同理), 但原走 3D 网格场渲染(viewport3D 架构阻)+ 需样本↔钻孔 x/y 关联(数据管线); 机制已交付, 不加近重复命令。`EquipmentStageAnalysisWindow`(工序分期)=21 班次工作模式+爆破频次排产+物料流箭头, TaskLib 引擎邻域(记录阻)+viz; 分期物料量侧 Kylin 剥采比均衡 VP 曲线已覆盖。`钻孔柱状图`=BoreholeRender 已有。

**本会话累计补 32 真功能 + 1 并发修复, 901 测。** 五条新系统透镜(点云/报表/着色/参数/GeoDataBase 窗口)diff; 各镜找到缺口都在分析/可视化子特性簇, 补后余覆盖/内核/3D/引擎。**煤厚等厚线 ThicknessSurface 是泛型标量场机制**(厚度/质量同理), 交付即覆盖煤质空间分布的 2D 子集。

## 八十六、标注类型透镜 —— 直径 + 角度标注(补齐标注型别)

核原标注型别(DIMALIGNED/DIMRADIAL/DIMCONTINUE/直径/角度/线性)vs Kylin(线性·对齐/半径/坐标/连续)。缺**直径/角度**:
- [x] **直径标注(DIMDIAMETER)**(`DimTools.BuildDiameter` + "直径标注"命令)：过圆心直径线(两端圆周点)+ 两端箭头 + "Ø值"(值=2·半径)。复用半径交互流(选圆/弧→指定方向)+ `_dimDiameter` 标志。2 测(过心线长=2r/两端箭头6实体+Ø文字)。
- [x] **角度标注(DIMANGULAR)**(`DimTools.BuildAngular` + "角度标注"命令)：顶点+两射线 → 劣弧(≤180°)+ 度数"n°"+ 两延长线。三点交互(顶点/边1点/边2点)。4 测(直角=90°/劣弧取 270→90·对射→180/弧点在半径上/字形)。
- [x] **字体补字形**(`StrokeFont`)：加 `Ø`(直径,六边≈圆+斜杠)、`°`(度,顶部小圈)、`=`(等号,顺带补——原坐标标注 "X=" 的等号一直缺字形不显)。1 测(三字形非空)。

**本会话累计补 34 真功能 + 1 并发修复, 907 测。** 六条新系统透镜(点云/报表/着色/参数/GeoDataBase 窗口/标注型别)diff; 标注型别现齐(线性·对齐/半径/直径/角度/坐标/连续 + 样式)。

## 八十七、绘图辅助/DXF 实体透镜 —— 全覆盖(含一次冗余提交纠正)

- **DXF 实体导入**: Kylin `DxfImportService` 已**超集覆盖**原全型别(Line/LwPolyline/Polyline2D3D/Circle/Arc/Ellipse/Spline/Hatch/Text/MText/Point/Insert/XLine/Ray/Solid/Face3D)且多 Leader/MLine/MultiLeader/Dimension。无缺。
- **绘图辅助全已有**: 正交(`_orthoOn` + `DraftAids.Ortho`, 命令"正交/正交开关", FeedPoint 落点 6551 应用)、栅格捕捉(`_snapOn` + `DraftAids.Snap`, 命令"栅格捕捉")、对象捕捉(六模式)全在。极轴追踪原本就无(非缺口)。
- **★冗余提交纠正(硬教训)**: 误判"Kylin 无正交"→实现并提交了并行冗余正交系统(`DrawTool.OrthoSnap`/`Anchor` + `_ortho` + 双重应用 + 死"正交"别名, commit 0f13673)→查栅格捕捉时撞见既有 `_orthoOn`/DraftAids→`git revert`(ab10fbf)全撤, 回 907 测。**根因: 实现前 grep "正交|ortho" 返回了无关行(ViewportHost)却据此断"无", 未核对确切标识符 `_orthoOn`/`DraftAids`/命令字面量。教训见 [[unlock-blocked-insights]]。**

## 八十八、质量硬化透镜 —— 修 LayerSolid 朝向不一致(记录 latent 项攻克)

不再找新命令(命令表已全覆盖), 转攻**有记录的 latent 未完成算法**:
- [x] **三角网朝向一致化 `MeshOrient.MakeConsistent`**(新算子): 原 LayerSolid(顶+底+侧壁 loft+weld)产「水密但朝向不一致」网格, 记忆记为 latent(体积 833≠500, 只能测拓扑不变量绕过)。补面邻接 BFS 传播——相邻两面公共边须反向遍历, 同向翻转邻面; 再按带符号体积规范外向。**接入 `LayerSolid.FromSurfaces` weld 后**, 使多层建模体朝向一致、散度体积可靠。`WindingNumberTester` 本就注明「前提: 朝向一致」, 此前不满足。
- 验证: MeshOrient 4 测(混乱朝向单位立方体→一致+外向体积6/翻一面纠正/幂等/空安全) + LayerSolidTests 加**精确体积断言**(10×10×5=500 → 6×带符号体积=3000, 此前因朝向不敢断言, 现绿)。917 测。
- **教训: 记忆里"记录为 latent/绕过"的项也要回头攻**——朝向一致是标准 BFS 算法, 非鲁棒难题, 早该做。区别于真鲁棒受阻(mesh 布尔/刀切 SolidKnife 已证伪)。

**本会话累计补 35 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克, 917 测。**

## 八十九、过度记录再纠正 —— 按闭合边界分割既有三角网(pc_tin_split 内/外片)

重审归为"鲁棒受阻"的 `pc_tin_split`(原「沿多段线切分为内/外两片」)。**区分三层**:
- 3D mesh 布尔/刀切 → 真鲁棒受阻(SolidKnife 已证伪);
- 单线左右精确切 → Kylin 已有(`MeshPlaneSplit`, 逐边精确);
- **闭合边界内/外分既有网格 → 缺, 且质心判别可做**(与 Kylin `裁剪三角网`=TriangulateClipped 同质心约定)。
- [x] **`MeshBoundarySplit.ByPolygon`** + "边界分割三角网"命令: 三角质心是否在闭合边界内 → 分内/外两片, 各重映射顶点为独立网格, 写 split_inside/outside.off。**守恒: 内+外三角数==原**。5 测(守恒/顶点重映射索引有效/内片质心均在界内/无边界全归外/空安全)。逐边精确切 straddling 三角(非质心粒度)属更难细化, 记录(与 Kylin 现有裁剪一致用质心)。

**教训: "鲁棒受阻"常是三层混叠——3D 布尔真受阻, 但 2.5D 质心分片可做且与既有约定一致。** 见 [[unlock-blocked-insights]]。

**本会话累计补 36 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 922 测。**

## 九十、网格修复流水线(修复拓扑关系常见修复)

新增 MeshOrient 后, 三修复算子(焊接/朝向/补洞)齐, 但缺原「修复拓扑关系」的**合并单操作**:
- [x] **`MeshRepair.Repair`** + "网格修复/修复拓扑"命令: 按**正确次序**串联——①焊接(合并重合顶点+去重三角, 得共享拓扑)→②朝向一致(MeshOrient, 需焊后传播)→③补边界洞(MeshHoleFill, 需焊后取边界环)。**次序关键: 焊接须最先**, 否则重合未共享的顶点使朝向/补洞失效。返回修复网格+前后诊断(顶点数/补洞数/开放边前后)。4 测(开口盒 4 开放边→补成水密0/重合顶点焊接减顶点/已净网格不变/退化安全)。非流形拆分·自交去除属内核级, 不含(记录)。
- 命令区别: 补洞/顶点焊接 为单步; 网格修复 为一键三步流水线(贴近原修复拓扑单操作)。

**本会话累计补 37 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 926 测。** 又两处「过度记录纠正」: 沿线分割(边界内外分片)+ 修复拓扑(合并流水线), 皆借新 MeshOrient 与既有算子组合而成。

## 九十一、剔局部高Z倒刺(pc_remove_obs 孤立尖刺, 坡度无关)

重审 pc_remove_obs「剔除车辆/设备/植被/**高Z倒刺**」。Kylin 已有: 地面滤波(`GroundFilter.LowestPerCell` 整格抽最低,治簇状物但抽稀)、绝对高度剔面(`MeshFaceCull.ByHeight`)、SOR/ROR(对称距离离群)。**缺: 孤立局部尖刺**(坡上局部隆起的车/倒刺,绝对高度漏检、地面滤波抽稀):
- [x] **`MeshFaceCull.BySpike`** + "剔倒刺"命令: 顶点 z 高于其**最高**边邻居 > 阈值 → 判孤立倒刺, 删含倒刺顶点的三角。**关键: 用"高于最高邻居"而非中位/均值才坡度无关**——均匀斜坡高处顶点其上坡邻居 ≥ 之, 不误判(单测 `BySpike_preserves_uniform_slope` 正是逮到中位法在坡沿误删2三角 → 改最高邻居修正)。3 测(平地单尖刺→删含刺6三角留2/全平不删/**均匀斜坡全留**)。
- 与既有互补: 簇状物(车/植被)→地面滤波; 绝对超高→ByHeight; 对称离群→SOR; **孤立坡上尖刺→BySpike**。

**本会话累计补 38 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 929 测。** 教训: 实现"局部异常"判据先想坡度无关性(中位/均值会随坡漂移, 用相对最高/最低邻居)。

## 九十二、补洞面积阈值(pc_fill_hole「避免填满矿坑大空洞」)

pc_fill_hole 原文「补充内部空洞（**带面积阈值，避免填满矿坑大空洞**）」。Kylin `MeshHoleFill.Fill` 此前补**全部**边界环——含大**外轮廓**(开放 TIN 的外边界也被当洞扇形封顶, 过度填充):
- [x] **补洞面积阈值**(`MeshHoleFill.Fill` 加 `maxLoopArea` 可选参数 + "补洞 <最大面积>"命令): 只补 XY 投影面积 ≤ 阈值的洞, 超阈(外轮廓/矿坑大空洞)跳过。缺省 MaxValue=补全部(保 MeshRepair 封闭网格行为不变, 低风险)。1 测(环形网 外轮廓100+内洞4: 缺省补2/阈值10只补内洞1/阈值1都跳0)。忠实原"避免填矿坑大空洞", 修开放 TIN 外轮廓被误封的过度填充。

**本会话累计补 39 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 930 测。** 又两处子特性纠正(倒刺剔除 + 补洞面积阈值), 皆"命令已有但缺子特性"型缺口——命令覆盖 ≠ 子特性齐, 逐子特性核。

## 九十三、LAS 真实色(RGB) —— pc_tin_color/pc_colorize「真实色」

pc_tin_color「**真实色**/高程/坡度/坡向/等高线」+ pc_colorize「恢复真实颜色(RGB)」需点云 RGB。Kylin `LasImportService` 此前只读 XYZ, LAS 捕获的真彩色无法显示:
- [x] **LAS RGB 读取**(`LasImportService` 加 `Colors` 列 + `RgbOffset` 表): 点格式 2/3/5/7/8 含 RGB(ASPRS 公开规范, 非依赖 native), 逐点读 3×uint16 归一化, Colors 与 Points 同长。2 测(合成格式2 LAS 全红→r=1/全绿→g=1; 格式0 无RGB→Colors=null)。
- [x] **真彩色显示**("LAS真彩色/点云真实色"命令): 载 LAS 用捕获 RGB 着点; 无 RGB 格式则灰显并提示。补齐"真实色"这一 TIN/点云着色子模式(此前 5 模式缺此 1)。
- 判据: 又一"公开规范可逆向 → 不依赖 native 内核"(同 LAS XYZ / GeoTIFF)。合成样本可验, 故实现非记录。

**本会话累计补 41 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 932 测。** 子特性纠正连补: 倒刺剔除/补洞面积阈值/裁剪圈外/LAS真实色——皆"命令已有但子特性/选项缺"型。

## 九十四、LAS 回波强度(intensity) + 强度着色

pc_quality 含「点数/密度/包围盒/高程分布/**强度分类**」, 点云常按强度分析/着色。所有 LAS 点格式偏移12 均有 intensity(uint16), Kylin 此前未读:
- [x] **intensity 读取**(`LasImportService.Intensity` 列): 紧接 XYZ(偏移12)读 uint16 原值, 与 Points 同长, 无需额外 seek。测(合成 LAS intensity=5000 读回正确)。
- [x] **强度着色**("LAS强度色/点云强度着色"命令): 按强度实际值域拉满灰阶(iMin~iMax→0~1)着点, 无变化则灰显。补 pc_quality 强度部分 + 强度可视化。
- LAS 属性读取渐全: XYZ(§七十) + RGB真实色(§九十三) + intensity(本节), 皆 ASPRS 公开规范逐字段偏移, 合成样本可验。

**本会话累计补 42 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 932 测。**

## 九十五、LAS 分类码(classification) —— 分类着色 + 语义剔除植被建筑

pc_remove_obs「剔除车辆/设备/**植被/建筑**」的语义部分 = LAS ASPRS 分类码(2地面/3-5植被/6建筑/7噪声)。补:
- [x] **分类码读取**(`LasImportService.Classification`): 偏移 15(格式0-5)/16(格式6-10)读 1 字节, 与 Points 同长。测(合成 LAS class=2 地面读回正确)。
- [x] **分类着色**("LAS分类着色"命令): 按分类码 HueColor 离散配色(地面/植被/建筑各异色)。
- [x] **语义剔除植被建筑**("点云剔除非地面/剔除植被建筑"命令): 跳过分类码 3/4/5/6/7(植被/建筑/噪声)点, 保留地面。**pc_remove_obs 语义法**(补几何法 BySpike/地面滤波之外的第三条路——预分类 LAS 精准剔除)。
- **LAS 属性读取完备**: XYZ(§七十)+RGB真实色(§九十三)+强度(§九十四)+分类(本节), 皆 ASPRS 公开规范字段偏移, 合成样本可验。功能字段齐(余 return号/GPS时间属专门元数据)。

**本会话累计补 43 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 932 测。** pc_remove_obs 三法齐: 几何(BySpike 孤立尖刺)+ 抽稀(地面滤波簇状)+ 语义(分类码植被建筑)。

## 九十六、LAS 质量报告(pc_quality 强度分类) —— LAS 域收官

pc_quality「点数/密度/包围盒/高程分布/**强度分类**」的强度/分类部分需 LAS 属性。既已读全属性:
- [x] **LAS 分类统计**(`LasQualityReport.ClassBreakdown` + "LAS分类统计"命令): 逐 ASPRS 分类码点数降序 + 常见码中文名(地面/植被/建筑/水…) + 强度分布(`Statistics.Describe`) → CSV(分类段+强度直方图)。4 测(计数降序守恒/ASPRS名映射/CSV占比/空安全)。
- **LAS 域收官**: 读(XYZ+RGB+强度+分类)→ 可视(真彩色/强度灰阶/分类离散色)→ 过滤(几何 BySpike/抽稀地面/语义剔植被建筑)→ 分析报告(分类统计+强度分布)。全链条从公开 ASPRS 规范托管实现, 合成样本可验。

**本会话累计补 44 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 936 测。** LAS/点云域子特性彻底补齐; 余 LAS return号/GPS时间(专门元数据)、GeoTIFF-DEM(原程序无, 非缺口)。

## 九十七、线型(CAD linetype) —— 虚线/点划线绘制

原程序支持线型(KdfReader.LinetypeDef 带 dashes), Kylin 此前只渲实线——导入的虚线显为实线, 也画不了虚线:
- [x] **线型虚线化**(`DashPattern.Dashes` + `SceneEntity.Dash` + `SegD`): 按样式[画,空,…世界单位]把线段切成"画"子段镶嵌; LineEntity/PolylineEntity 用 SegD; `Colored` 拷 Dash 保变换后线型不丢。`DashPattern.ByName`: 实线/虚线/点划线/点线/双点划线(可 scale)。5 测(切段/占空比/空样式单段/零长线/线型名+缩放)。
- [x] **"线型/实线/虚线/点划线/点线/双点划线"命令**: 设当前 `_currentDash`, 新画直线/多段线继承(FeedPoint 落点时赋 Dash)。默认 Dash=null 实线, 既有渲染不变。
- 记录: DXF 导入线型保真(导入的虚线显虚线)需把 `ent.LineType` 穿过递归 Emit + 名映射(自定义 LTYPE 精确 dash 需解 LTYPE 表), invasive 且近似, 记录为扩展; 圆/弧虚线同理可扩(现 scope 直线/多段线)。

**本会话累计补 45 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 941 测。**

## 九十八、文字对齐(hAlign/vAlign) —— 含 DXF 导入保真

原程序文字带 hAlign/vAlign(BinaryPayloadWriter Text 有对齐字段)。Kylin TextEntity 有 Rotation(已支持旋转)但缺对齐——导入的居中/右对齐文字错位, 也无法对齐:
- [x] **TextEntity 对齐**(`HAlign` 0左/1中/2右, `VAlign` 0底/1中/2顶): Tessellate 按文字宽(字数×0.8高)/高偏移锚点。默认 0/0 = 左/基线(向后兼容, 既有渲染不变)。Apply/MoveGrip 拷对齐。2 测(右对齐左移一宽/居中半宽·顶对齐下移一高/默认兼容)。
- [x] **DXF 导入文字对齐保真**: 读 ACadSharp `te.HorizontalAlignment/VerticalAlignment`(枚举名稳健映射)+ 非左/基线时锚点取 `AlignmentPoint`(而非 InsertPoint)。导入的对齐文字位置正确。
- 文字旋转本已有(TextEntity.Rotation, 双查确认非缺口)。

**本会话累计补 46 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 943 测。** CAD 属性透镜续: 线型(§九十七)+文字对齐(本节); 双查确认 文字旋转/栅格捕捉/正交 早已有。

## 九十九、多行文字 MText —— 逐行渲染保真

原程序 MText 多行。Kylin 此前 MText 导入把 `\P` 段落换行剥成**空格**→多行合并成一行(丢行结构):
- [x] **多行 MText 导入**(`DxfImportService.MTextLines` + MText case 逐行): 先按 `\P` 拆行(各行独立剥格式码), 再逐行 DrawText 下移(行高×1.4 行距, **旋转感知**: 行向沿垂直文字方向)。导入的多行注记正确分行显示。3 测(拆行/逐行剥格式/单行+空)。
- 与 §九十八 文字对齐、§九十七 线型 同属 CAD 属性/文字保真透镜。

**本会话累计补 47 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 946 测。**

## 一〇〇、线型导入保真 —— DXF 虚线线型解析(ByLayer)

§九十七 线型只做了绘制侧(记录导入为扩展)。现补导入保真:
- [x] **DXF 线型解析**(`DxfImportService.ResolveDash` + Emit 顶置 emitDash → Finalize 赋 `se.Dash`): 读 ACadSharp `ent.LineType?.Name`, **ByLayer 时取 `ent.Layer?.LineType?.Name`**(显式实体线型优先); 映射到 `DashPattern.ByName`。导入的虚线/点划线(显式或 ByLayer)正确显虚线。3 测(显式优先/ByLayer 解析图层线型/未知名实线)。
- 至此线型闭环: 绘制(§九十七 命令+_currentDash)+ 导入(本节 ByLayer 解析)。自定义 LTYPE 精确 dash 长(vs 按名近似)记录为扩展。

**本会话累计补 48 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 949 测。** CAD 属性/文字保真透镜产出: 线型(绘制+导入)/文字对齐(含导入)/多行 MText。

## 一〇一、文字字宽系数 + DXF 曲线保真确认

- [x] **文字字宽系数**(`TextEntity.WidthFactor` 默认1): Tessellate 按系数缩放字符 x(cursor 步进+字形 x)。默认1向后兼容。DXF 导入读 `te.WidthFactor`。Apply/MoveGrip 拷。1 测(系数2→水平尺寸加倍)。
- **DXF 曲线保真确认(双查)**: 椭圆导入已含起止参 `StartParameter/EndParameter`(椭圆弧, 非缺口); 样条已 **De Boor NURBS 采样**(控制点+度+节点, 退回拟合点); 弧起止角已有。**DXF 实体导入保真已综合完备**。

**本会话累计补 49 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 950 测。** 文字保真齐(旋转[本有]/对齐/多行/字宽系数); CAD 属性保真透镜近收敛(余线宽复杂渲染、字倾角次要)。

## 一〇二、文字倾斜角(oblique) —— 文字保真 100% 齐

- [x] **文字倾斜角**(`TextEntity.ObliqueAngle` 弧度, 默认0): Tessellate 斜切(x += 字形y·tanθ)——上部点右移成斜体。DXF 导入读 `te.ObliqueAngle`(度→弧度)。Apply/MoveGrip 拷。默认0向后兼容。1 测(倾斜增大水平范围)。
- **文字保真 100% 完整**: 旋转[本有] + 对齐(hAlign/vAlign) + 多行(MText \P) + 字宽系数(WidthFactor) + 倾斜角(ObliqueAngle)。全 DXF 导入保真 + 绘制默认向后兼容。

**本会话累计补 50 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 951 测。** CAD 属性保真透镜收敛: 文字五属性齐、线型闭环; 余线宽(变宽线渲染需改管线, 主打印价值)记录。

## 一〇三、文字属性导出 —— 完整 round-trip 保真

文字保真此前只在导入侧(§九十八/一〇一/一〇二)。导出侧(`SceneExportService`)只写 Rotation, 丢对齐/字宽/倾斜:
- [x] **文字属性导出**(SceneExport DrawText case): 设 ACadSharp `te.WidthFactor`、`te.ObliqueAngle`(弧度→度)、`HorizontalAlignment`/`VerticalAlignment`(枚举, 非左/基线时置 `AlignmentPoint`)。ACadSharp 垂直对齐枚举名为 `TextVerticalAlignmentType`(反射查证, 与水平 `TextHorizontalAlignment` 不一致命名)。
- [x] **完整 round-trip 验证**: Kylin 文字(对齐/字宽/倾斜/旋转)→ BuildDocument 导出 → MapDocument 导入 → 五属性全保留。1 round-trip 测。
- 文字保真现**导入+导出双向完整**。

**本会话累计补 51 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 952 测。**

## 一〇四、线型导出 —— 线型完整 round-trip

§一〇〇 只做了线型导入。导出侧补:
- [x] **线型导出**(`SceneExport` BuildDocument 加 `LineTypeFor`): 虚线样式 → ACadSharp `LineType`(段长±=画/空, `AddSegment`), 名用 `DashPattern.NameOf`(逆映射标准名 DASHED/DOTTED/DASHDOT, 让再导入的 ByName 可识别), 注册进 `doc.LineTypes`(同名复用防重), 设 `ent.LineType`。
- [x] **线型完整 round-trip 验证**: Kylin 虚线 → 导出(LineType 段长+标准名)→ 导入(ResolveDash 读名 ByName)→ 样式 [6,3] 保留。1 round-trip 测。
- **CAD 保真 round-trip 完整**: 文字五属性(§一〇三)+ 线型(本节)导入导出双向齐。

**本会话累计补 52 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 953 测。** CAD 属性/文字保真透镜彻底收敛(导入+导出+绘制全齐), 余线宽渲染(需 GL 管线改)记录。

## 一〇五、线宽数据 round-trip —— CAD 全属性往返闭环

变宽线**渲染**需改 GL 管线(变宽线=四边形三角化, 复杂)仍记录; 但线宽**数据** round-trip 可做且补齐往返全属性:
- [x] **线宽存值**(`SceneEntity.LineWeight` short, 默认 -1=ByLayer; `Colored` 拷贝): DXF `LineWeightType`(Int16: 0..211=0.01mm, -1=ByLayer, -3=Default)。
- [x] **导入**(`DxfImport` emitLW): 读 `ent.LineWeight` 存实体自身值(不解析 ByLayer, round-trip 保真)。
- [x] **导出**(`SceneExport`): `ent.LineWeight = (LineWeightType)e.LineWeight`。
- [x] **round-trip 验证**: W25(0.25mm)线 export→import 线宽值 25 保留。1 测。
- **CAD 属性往返闭环**: 图层/颜色/线型/文字五属性/线宽 —— 全部导入导出 round-trip 保真。余变宽线渲染(GL 四边形管线)记录, 数据不丢。

**本会话累计补 53 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 954 测。** CAD 保真透镜彻底收官(绘制+导入+导出+全属性往返)。

## 一〇六、图层状态 round-trip —— 开/冻结/锁定往返

图层**状态**(开关/冻结/锁定)此前导入导出均丢(层仅存名+色)。补:
- [x] **导出**(`SceneExport` LayerFor): 从 `LayerTable` 层写 ACadSharp `Layer.IsOn`(Visible) + `Flags`(Frozen/Locked 位)。
- [x] **导入**(`DxfImport` MapDocument 遍历 `doc.Layers`): 读 `IsOn`+`Flags` → `EntityImportResult.LayerStates`(名→开/冻结/锁定)。
- [x] **应用**(`ApplyEntityImport`): 导入后 `_layers.Get(名)` 恢复 Visible/Frozen/Locked(与 `.pmx` 持久化状态同源)。
- [x] **round-trip 验证**: 关闭+冻结+锁定层 export→import 三态保留。1 测。
- **CAD round-trip 全属性**: 实体(图层/色/线型/文字五属/线宽)+ 图层(色/开/冻结/锁定)全往返。

**本会话累计补 54 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 955 测。**

## 一〇七、逐实体隐藏状态 round-trip —— 往返审计闭环

`SceneEntity.Visible`(隐藏对象命令置)此前导出丢(重开全可见)。补:
- [x] **导出**: `ent.IsInvisible = !e.Visible`(DXF 实体不可见标志)。
- [x] **导入**(emitVisible): `se.Visible = !ent.IsInvisible`。
- [x] **round-trip 验证**: 隐藏线 export→import 仍隐藏。1 测。

**★往返审计系统闭环**: 逐一核 `SceneEntity` 全属性(色/层名/可见/线型/线宽/文字五属)+ `LayerTable` 全属性(名/色/开/冻结/锁定)—— **每一项都导入导出 round-trip 保真**。非临时发现, 是系统枚举证明。余实体透明度(不渲染+采矿罕用, 阈下)与 3D 厚度/标高(架构受阻)记录。

**本会话累计补 55 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 956 测。** CAD 往返保真透镜系统收官。

## 一〇八、原生 .pmx 属性持久化 —— 保存格式往返补全

系统往返审计的**关键跟进**: DXF 往返虽全, 但**原生 .pmx 保存格式**(SceneIO)此前只存 几何/色/层名/文字内容 —— 丢 线型/线宽/隐藏/文字对齐/字宽/倾斜。即"画虚线+设线宽+隐藏实体+文字对齐 → 存 .pmx → 重开全丢"。补:
- [x] **Dto 加属性字段**(缺省省略, `JsonIgnore WhenWritingDefault`): `D`线型 / `W`线宽(null=ByLayer) / `H`隐藏 / `Wf`字宽 / `Ob`倾斜 / `Ha`水平对齐 / `Va`垂直对齐。
- [x] **ToDtos/FromDtos** 双向读写全属性; 文字类型专属字段按 `is TextEntity` 分支。
- [x] **向后兼容**: 旧 .pmx(无新字段) 仍读, 属性回退默认(实线/ByLayer/可见/字宽1)。`JsonIgnore` 使无新特性的实体输出字节不变。
- [x] **2 测**: 属性 Save→Load 往返(线型/线宽/隐藏/文字五属) + 旧格式默认回退。
- **保存往返闭环**: DXF 往返(§一〇三-一〇七) + 原生 .pmx 往返(本节) —— 两条保存路径均全属性保真。

**本会话累计补 56 真功能 + 1 并发修复 + 1 潜伏字形 bug 修 + 1 latent 攻克 + GWN 硬化, 958 测。**

## 一〇九、变换/撤销 样式一致性 —— Colored 统一 + 撤销潜伏 bug 顺带修

原生 .pmx 修复(§一〇八)顺带发现两处同源问题:
- [x] **撤销/重做潜伏 bug 顺带修**: `BeginChange/DoUndo/DoRedo` 用 `SceneIO.Save` 快照 —— §一〇八 前, 任何编辑+撤销都会丢线型/线宽/隐藏/文字格式。SceneIO 修复后撤销亦全保真。
- [x] **`Colored<T>` 统一**: 变换深拷此前拷 色/线型/线宽 但漏 `Visible`, 使 `Apply` 变换后隐藏实体复现。补 `e.Visible = Visible` —— 全样式(色/线型/线宽/可见)随变换一致保留。1 测。
- **状态转移三路闭环**: 存盘(DXF+.pmx §一〇三-一〇八) + 撤销快照 + 变换深拷 —— 全属性一致保真。

**本会话累计补 56 真功能 + 1 并发修复 + 2 潜伏 bug 修(字形/撤销样式) + 1 latent 攻克 + GWN 硬化, 959 测。** 状态转移全路径审计收官。

## 一一〇、变换丢图层 —— 高影响潜伏 bug 修复(状态转移审计最大发现)

状态转移审计查第 5 路(剪贴板)时揪出**用户可见的正确性 bug**: `Colored<T>` 深拷不含 `LayerName` → `Apply` 结果落默认层 "0"。波及**所有变换**:
- `ApplyEditTransform`(移动/复制/旋转/镜像/缩放): `_scene.Replace(e, e.Apply(m))` → 实体被移到 "0" 层(变色、原层找不到)。
- `CoordTransformAsync`(坐标转换): 全场景实体 `.Apply(m)` → 统统落 "0" 层。
- 剪贴板 `Paste`: 克隆用 `Apply` → 粘贴物落 "0" 层。
- [x] **测先证 bug**: 变换后 `LayerName` 期望"开采境界"实得"0" → 失败, 确证。
- [x] **修**: `Colored` 加 `e.LayerName = LayerName`。DXF 块展开在 Apply 后覆盖层名, 故导入不受影响(35 导入测全绿)。
- [x] 变换保图层断言并入样式测。959 测无回归。

**★教训**: 深拷/克隆助手漏拷某属性 → 所有走该助手的操作静默丢该属性; 加字段或审计时须逐一核**深拷助手**覆盖的属性集(色/线型/线宽/可见/**层名**)。此 bug 高频高可见(每次移动都触发), 靠"每属性×每路径"矩阵审计才揪出。

**本会话累计补 56 真功能 + 1 并发修复 + 3 潜伏 bug 修(字形/撤销样式/变换丢层) + 1 latent 攻克 + GWN 硬化, 959 测。**

## 一一一、样式拷贝枢纽 CopyStyleFrom —— 6 处多段线编辑保全样式

§一一〇 教训推广: 新增 `SceneEntity.CopyStyleFrom(src)` 单一枢纽(色/线型/线宽/可见/层名), `Colored` 改经此。审全代码, 揪出 6 处"手工构造新多段线只拷色+层、丢线型/线宽/可见"的同类潜伏点并改用枢纽:
- 简化(DouglasPeucker) · 平滑(Chaikin/CatmullRom) · 线裁剪(ByPolygon) · 合并(join) · 加密(Densify) · 闭合(open→closed)。
- 均为"编辑几何、保实体身份"操作(多经 `_scene.Replace`), 结果理应保源全样式(如虚线简化后仍虚线)。
- 分析类新输出(等值线/标注/路径/热力, 计算色)故意另styling, 不动。
- 959 测无回归; 加样式字段今后只改 `CopyStyleFrom` 一处。

**本会话累计补 56 真功能 + 1 并发修复 + 3 潜伏 bug 修 + 6 样式保真点 + 1 latent 攻克 + GWN 硬化, 959 测。** 状态转移矩阵审计(4 存取路径 + 深拷枢纽 + 手工构造点)彻底收官。

## 一一二、属性编辑/修剪 保全样式 —— 枢纽推广至 3 处纯逻辑

CopyStyleFrom 审计续查 `.LayerName = 源.LayerName` 模式, 又揪 3 处纯逻辑潜伏点:
- [x] **`EntityProperties.Style`(特性面板编辑枢纽)**: 此前只拷色+层 → **每次改属性(改终点/半径/图层/颜色…)都丢线型/线宽/可见**; 文字改内容还丢对齐/字宽/倾斜。改用 `CopyStyleFrom` + 文字专属五属补拷。影响面大(所有编辑+改层改色都经此)。
- [x] **`TrimTools` 修剪/延伸(多段线+圆弧 2 处)**: 结果丢源线型/线宽/可见 → 改 `CopyStyleFrom`。
- [x] **2 测**: 编辑终点保线型/线宽/可见; 文字改内容保旋转/对齐/字宽/倾斜。961 测。

**枢纽统一收束点**: `Colored`(变换) + 手工构造(简化/平滑/裁剪/合并/加密/闭合/**修剪/属性编辑**)共 9 处 → 全走 `CopyStyleFrom`。样式属性集单点维护。

**本会话累计补 56 真功能 + 1 并发修复 + 4 潜伏 bug 修(字形/撤销/变换丢层/属性编辑丢样式) + 9 样式保真点统一 + 1 latent 攻克, 961 测。**

## 一一三、特性面板 线型/线宽 显示+编辑 —— 数据已通但未surfaced

子特性完整度: 线型/线宽 已能绘制/导入/导出/存盘/往返, 但**特性面板只显示 类型/图层/颜色/几何, 不显示线型线宽**, 用户看不到也改不了。核原版 `EntityPropertyBag.cs`(线宽=常规类可编辑 float; 线型 564 处), 确认原版特性面板就有 → 补(忠实, 非发明):
- [x] **显示**(`Describe` 常规加两行): 线型经 `DashPattern.DisplayName`(实线/虚线/点线/点划线/双点划线); 线宽经 `LineWeightUtil.Display`(随层/默认/0.25 mm)。
- [x] **可编辑**(`EditableLabels`+`WithEdited`): 线型 `DashPattern.IsKnownName` 校验→`ByName`; 线宽 `LineWeightUtil.TryParse`(mm→规整最近标准档)。经 `CloneShallow`(已走 CopyStyleFrom 保余样式)。
- [x] **新纯逻辑 `LineWeightUtil`**(DXF short↔mm, Snap 24 标准档, 随层/默认/随块): 3 直测。
- [x] 3 测: Describe 显示线型线宽 + 编辑线型线宽(点划线/0.5mm→50/未知名拒绝/实线→null) + LineWeightUtil。964 测。

**本会话累计补 57 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 964 测。**

## 一一四、特性面板 可见 属性 —— 原版 bag diff 续补

核原版 `EntityPropertyBag` 常规类 = layer/color/lineweight/**transparency/visible**。Kylin 缺后二:
- [x] **可见 (visible)**: Kylin 早有 `SceneEntity.Visible`(隐藏对象命令 + 渲染支持), 仅特性面板未 surfaced。补 `Describe`(是/否)+`EditableLabels`+`WithEdited`(是/否/显示/隐藏/true/false 解析)。完全可用(有渲染)。1 测。
- **透明度 (transparency)**: 原版有, 但 Kylin 渲染不透明(P3_C3 无 alpha), 面板给值却不视觉生效会误导 → **记录为渲染受阻**(需顶点格式加 alpha + 混合, 类同变宽线渲染); 其唯一价值即视觉, 故不做 store-only。
- 线型面板行: 原版 bag 无(线型在工具栏/命令, Kylin 亦有 线型 命令); 面板显示为合理 surfacing 既有数据, 保留。

**本会话累计补 58 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 965 测。** 特性面板 vs 原版 bag 已对齐(除渲染受阻的透明度)。

## 一一五、KDF 导出 —— 补齐导入导出不对称(原版有,Kylin 缺)

差异审计: 原版 `KdfExportService`+`KdfWriter`(WeCAD KDF 二进制), Kylin 只导入。补:
- [x] **`KdfExportService`**(忠实复刻原 KdfWriter 字节布局): 文件头(magic 23B)+ BlockTable/Model_Space 骨架 + LayerTable(记录含开/锁) + 实体。
- [x] **实体映射**: 直线/多段线/矩形/多边形/圆(72段)/圆弧(按弧长采样) → AcDb3DPolyline(折线化); 文字 → AcDbText(GBK, 高/字宽/旋转/倾斜 tag02)。点/图案填充无 KDF 对应 → 跳过(记录)。
- [x] **公共头精确**: u32字段数+tag06图层(GBK)+3B BGR+6B零+double1.0+2B flags+u32 —— 与 `KdfImportService.ReadCommon` 23B skip 对齐; 折线每顶点 33B(24 XYZ+8保留+1 strlen); 文字 tag02 idx0=高 idx2=旋转。
- [x] **验证 = 往返**(同 DXF 导出靠 ACadSharp 回读): `BuildBytes`→`KdfImportService.LoadBytes`(新增内存回读入口), 直线/闭合多段线(首点回写)/圆(72点半径3)/文字(内容位置高度)/图层 全保真。2 测。
- [x] 接入 `SaveAsAsync`(.kdf 选项)。
- **验证边界记录**: 往返经 Kylin 自家 reader(忠实移植原 KdfReader)证内部一致; 无 WeCAD/原版运行时不能证其读取, 但字节布局已逐字段对齐原 KdfWriter。

**本会话累计补 59 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 967 测。**

## 一一六、导入导出对称性审计 —— KDF 补齐, 余项记录

系统列原版 export/writer 类 vs Kylin, 判每项:
| 原版导出 | Kylin 状态 | 判定 |
|---|---|---|
| KdfExportService/KdfWriter | **已补(§一一五)** | ✓ 干净不对称(仅导入无导出), 契合, 往返可验 |
| DwgDxf(双向) | DxfExport/SceneExport ✓ | 已对称 |
| MeshExportViewModel | MeshExport(OBJ/PLY/STL) ✓ | 已对称 |
| PmbmWriter/PmbiWriter(PMB 块体) | 块体导出 CSV ✓(功能在) | **记录**: Kylin 块体=稀疏点(X,Y,Z,Size,Grade), PMB=密集网格(GridSpec+多属性数组), 表示不匹配; 且 CSV 功能等价已在。非缺功能, 强行 PMB 往返不净。 |
| PmxWriter(原生 PMX 二进制) | 自有 JSON .pmx(SceneIO) ✓ | **记录(不可验)**: Kylin 无 PMX-二进制 reader → 无往返验证路径; JSON .pmx 功能等价。 |
| DrillPlanWriter/MinePlanExport/MiningPlanExporter | — | **记录(引擎受阻)**: 属 TaskLib/PlanLib 计划引擎域(不可验)。 |
| BinaryPayloadWriter/OutcropDebugExporter | — | 内部/调试, 非用户功能。 |

**结论**: 用户级绘图/网格交换格式导出已对称(DXF/DWG/KDF/OBJ/PLY/STL)。余 PMB/PMX-二进制为原版原生格式互操作(Kylin 有 CSV/JSON 功能等价), 因表示不匹配/无 reader 不可净验, 记录。计划类导出属受阻引擎域。

**导出符合性审计收官。本会话累计补 59 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 967 测。**

## 一一七、3DMine String File(.3ds)导入 —— 导入符号性补缺

导入侧对称审计: 原版有 TdmReader/**TdmSolidReader**/**TdmStringReader** 三变体, Kylin 的 TdmImportService 原覆盖 二进制(3DMine_2011_Bin)+ Solid 文本, **缺 String File(.3ds 文本折线)**。补(忠实移植 TdmStringReader):
- [x] **`TdmImportService.LoadStrings`/`ParseStrings`**: GBK 文本, 跳首两行 header; 顶点行(code,X,Y,Z 去尾空恰4段) 累积成折线; "0,…" 边界行末3浮点=下条折线 RGB(0..1, 负=默认); DbSText 文字注记计数跳过; 首尾重合→Closed。产可编辑 PolylineEntity(EntityImportResult)。
- [x] 接入: `.3ds` → `ImportTdmStringEditable` → `ApplyEntityImport`(可选中/编辑); 文件选择器加 .3ds。
- [x] 3 测(合成 .3ds 按文档格式): 双彩色折线(红/绿 RGB)+ 非法拒绝 + 首尾重合闭合。970 测。
- **导入对称完整**: DXF/DWG · KDF · MapGIS(WL/WT/WP/MPJ工程) · OFF · 3DMine(二进制/Solid/**String**) · BLK · PMB · LAS · 点数据 全覆盖。余 PMX-二进制(无 reader 不可验)记录。

**本会话累计补 60 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 970 测。** 导入导出双向符号性审计收官。

### 一一七补：.3dp 工程包(CAB)记录
原版 `TdmProjectImportService` 导 .3dp = Microsoft Cabinet(MSCF)归档, 内含若干 .3dm/.3ds/config, 用 Windows `expand.exe` 解包再逐成员分派。**成员格式 Kylin 现已全支持**(二进制/Solid/String), 缺的仅 CAB 容器解包:
- `expand.exe` 是 Windows 专有, 不可移植到麒麟/Linux; 跨平台需 `cabextract`(运行时依赖不保证)或托管 CAB 解压器(MSZIP=deflate 可做, LZX 复杂)。
- 无 .3dp 样本, 且验证需配套 CAB 写入器 → **记录为容器解包受阻**(成员分派逻辑已具备, 补 CAB 解压即可接通)。

## 一一八、透明度 数据层 —— 特性面板与原版 bag 完全对齐

§一一四 记录透明度为渲染受阻。按规程"能补的功能补, 无法验的记录": 透明度**数据层**可做且可验(往返测), 与线宽数据层一致; 仅**视觉渲染(alpha 混合)**不可验(像素级无法自动验)记录。补:
- [x] **`SceneEntity.Transparency`**(short, -1=随层, 0..90=百分比; CopyStyleFrom 拷)。
- [x] **DXF 往返**: 导出 `ent.Transparency = -1?ByLayer:new Transparency(v)`; 导入 `IsByLayer?-1:Value`(emitTransp)。
- [x] **.pmx 往返**(SceneIO Dto.Tr, 缺省省略, 旧档回退 -1)。
- [x] **特性面板 显示+编辑**(透明度: 随层/不透明/N%; 解析 0..90 越界拒绝)。
- [x] 4 测: DXF 往返(40%) + 面板编辑(50%/不透明/越界拒绝) + .pmx 往返 + 旧档默认。972 测。
- **特性面板 vs 原版 `EntityPropertyBag` 完全对齐**: 常规 = 图层/颜色/线型/线宽/**透明度**/可见 全齐(线型为 Kylin 额外 surfacing)。
- **视觉渲染记录**: 透明度 alpha + 线宽变宽线 —— 两者均需改核心渲染管线(P3_C3→P3_C4+GL 混合 / 变宽线四边形化), 横跨所有 Tessellate, 像素不可自动验; 数据层已全保真不丢, 渲染待管线投入。

**本会话累计补 61 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 972 测。**

## 一一九、图层重命名 + 图层合并 —— 图层管理功能补缺

diff 透镜续查图层操作: 原版有 图层合并(42 处)/图层命名·重命名, Kylin 的 LayerTable 有 New/删除/设当前/开关/冻结/锁定 但缺 重命名/合并。补(忠实):
- [x] **图层重命名**(`LayerTable.Rename` 就地改名保色/状态 + `RenameCurrentLayer` 命令): 实体 `LayerName` 经 `Scene.ReassignLayer` 随迁; 默认层"0"/空名/重名/同名 拒绝。命令: 「重命名图层 <新名>」/「图层重命名 <新名>」/「图层命名 <新名>」。
- [x] **图层合并**(`MergeLayerIntoCurrent` 命令, 复用既有 `Scene.ReassignLayer`+`LayerTable.Remove`): 源层实体并入当前层后删源层; 源==当前/源不存在/空名 拒绝。命令: 「合并图层 <源层>」/「图层合并 <源层>」。
- [x] 3 测: 重命名保色/状态 + 重命名拒绝(默认/空/同/重名) + 合并(实体迁移+源层删除)。975 测。

**本会话累计补 63 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 975 测。**

## 一二〇、图层隔离 —— LAYISO 补缺

原版有 图层隔离(7 处), Kylin 有 隐藏对象/同层对象 但无图层隔离。补:
- [x] **`LayerTable.Isolate(name)`**: 只显示 name 层, 其余 Visible=false; 返回关闭数。取消隔离复用 `AllOn`。
- [x] 命令: 「图层隔离」/「隔离图层」(有选中→隔离选中实体的层, 否则当前层) + 「取消隔离」/「结束隔离」。入命令面板。
- [x] 1 测: 隔离只留目标层 + AllOn 恢复。976 测。

**本会话累计补 64 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 976 测。** 图层管理(新建/删除/重命名/合并/隔离/开关/冻结/锁定/全开)与原版对齐。

## 一二一、反选 —— 选择操作补缺

原版有 反选(9 处), Kylin 选择操作有 全选/快速选择/选择类似/取消/选择集 但缺反选。补:
- [x] **`InvertSelection`**(UI 方法, 同 SelectAll 风格): 新选择集 = 当前未选中的全部实体(HashSet 去当前 + 遍历补集), SaveSel 支持撤销选择。命令「反选」/「反向选择」/「反转选择」, 入命令面板。
- 验证: build + smoke(UI 交互方法, 与 SelectAll/SelectLast 同为无单测 UI 方法; 逻辑=集合补集平凡)。976 测无回归。

**本会话累计补 65 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 976 测。**

## 一二二、忠实性核查：圆角/倒角/阵列/拉伸 非原版通用命令（不新增）

diff 修改类命令时初判 Kylin 缺 圆角/倒角/阵列/拉伸(通用 AutoCAD 修改命令), 深查后**证伪**——这些词在原版是**无关上下文**, 非用户命令:
- **阵列**: `Hatch 图案中的一条阵列定义线` / `体素阵列`（内部数据结构, 非"阵列复制"命令）。
- **拉伸**: 全是`竖向拉伸`（2D→3D 棱柱挤出, 内核 StripPrism 口径, 3D 受阻域）, 非"拉伸夹点"命令。
- **圆角/倒角/圆弧倒角**: `Fillet` 代码在 BenchWidthIdentifier/中线（`RoundsToExactRmin`=道路/台阶拐点按最小转弯半径倒圆）; `圆弧倒角` 是坡道/道路段接头的拐点处理选项（多段线折接 vs 相切圆弧）, 属**内核规模道路落地/切帮布局**（Kylin RampCenterlines 明确"落地/切帮为内核规模, 不在此"已记录受阻）。
- **结论**: 原版无通用 圆角/倒角/阵列/拉伸 修改命令 → **不新增**（新增即发明原版没有的功能, 违忠实）。道路拐点倒圆属受阻内核域, 已记录。
- **教训（复用§忠实性）**: 词频高≠命令; 必查词的**实际上下文**（内部结构/3D内核/领域特定）再判缺口。此次差点凭"圆角76/阵列58"误加 4 个非原版命令。

**无代码改动（正确结果=不发明）。** 修改类命令 Kylin 覆盖(移动/复制/旋转/缩放/镜像/偏移/修剪/延伸/打断/分解)与原版对齐。

## 一二三、修改点样式 —— 系统命令 diff 补缺（点符号+大小）

**系统 diff**: 抽原版全部 257 个 AddButton 命令标签 vs Kylin 965 命令, 逐条判(同义/受阻/真缺)。真缺可做项之一 = **修改点样式**(原走 `PitMine_SetPointStyleBatch` 引擎, 但纯视觉符号几何→托管可做, 同"曾误记 native 实则可托管"判据):
- [x] **`PointEntity.Style`**(int, PDMODE: 低5位 0=点/2=加号/3=×/4=竖线; +32=外接圆 +64=外接方) + Tessellate 按样式绘符号; Apply/MoveGrip 拷 Style。
- [x] **持久化**: 点 Dto N 扩为 [X,Y,Size,Style](旧档 2 元回退 Size=0.5/Style=2)。此前点 Size 都未存, 一并补。
- [x] **特性面板**: 点大小/点样式 显示+编辑(样式 0-127 越界拒绝)。
- [x] **批量命令** 「修改点样式 <样式> [大小]」: 选中点批改, CopyStyleFrom 保色/层/透明。
- [x] 4 测: 符号 tessellation(加号/×/竖线/无/外接方圆) + .pmx 往返 + 旧档默认 + 面板编辑。980 测。
- **同批 diff 记录受阻**: 影像底图(需光栅贴图渲染, 线渲染器无) / 删除三角面(网格为显示态线框, 无可编辑网格模型) / 属性赋值(块体模型属性域); TaskLib/PlanLib 计划类(引擎受阻); 布尔/坑线落地/倾斜摄影(3D 内核)。

**本会话累计补 66 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克, 980 测。**

## 一二四、系统 257-标签命令 diff 收官 + 原版占位命令清单

抽原版全部 257 AddButton 标签 vs Kylin 965 命令, 112 非精确匹配逐条判决:
- **真缺可做 → 补**: 修改点样式(§一二三, 已补)。**仅此一项**。
- **原版占位(PlaceholderCommand, 原版自身未实现) → 不实现(忠实)**: 中长远规划动态模拟 / 创建工程位置 / 地质体建模 / 批量扩坑 / 按区域块体算路线 / 标识线序 / 标识起点 / 生产进度计划过程模拟 / 短期进度计划动态模拟 / 虚拟钻孔 / 运输路径浏览。**这 11 个原版即空壳, Kylin 补即超出原版, 永不实现。**
- **同义已覆盖**: CSV导入/三角网体积/体积算量/创建剖面/动态剖面/创建工作线/坐标系统转换/坡度分析/坡向分析/曲率分析/粗糙度分析/构建等值线/补充空洞/格网质量检测/生成三角网边界/转化为三角格网/点云管理/显示隐藏点云 等（Kylin 用近义命令名）。
- **受阻域(已记录)**: 计划/任务类(TaskLib/PlanLib 引擎不可验)~35项; 3D/内核/道路落地/地质(布尔/坑线/排土/斜坡道/倾斜摄影/尖灭/煤层面/离散化)~30项; 网格编辑(删三角面/增删边/裁剪面, 显示态线框无可编辑模型); per-entity-Z 高程(统一线高程/赋节点高程/修改高程点, 2D 架构); 块体模型编辑(属性赋值/创建块体); 影像底图(光栅贴图渲染)。

**结论**: Kylin 命令覆盖对原版 **实际可实现且忠实**的功能已完备——257 标签中真缺口仅"点样式"(已补), 余为同义/受阻/原版占位。系统命令 diff 彻底收官。

**本会话累计补 66 真功能 + 1 并发修复 + 4 潜伏 bug 修 + 9 样式保真点统一 + 1 latent 攻克 + 多处忠实性证伪(圆角/阵列/占位命令), 980 测。**

## 一二五、右键上下文菜单命令 diff —— 无忠实缺口，命令源全覆盖

补 diff 最后一个命令源(右键 ContextMenu.cs, 1037 行)。动作标签逐条判:
- 同义已覆盖: 创建/保存选择集、切换2D/3D/Orbit、粘贴/基点粘贴、线型/线宽/颜色。
- **填充颜色**: 原版注释「当前实装与普通颜色等价(写入选中实体)」= 实体颜色, Kylin 有(颜色)。非独立填充(面填充受阻)。
- **导出选中实体**: 原版注释「待引擎'导出选中实体'能力到位后接入(当前 PMX 仅支持整模型 SaveFromEngine)」——**原版自身未实装**(待引擎, 仅整模型)。Kylin 托管虽易做(导 _selected), 但原版无此工作功能→**属超出原版当前功能面, 同占位/增强类, 记录不自行新增**(遵 [[faithfulness]] 只迁原版确有的工作功能)。若用户要, 可秒接。

**命令源全覆盖收官**: 插件 AddButton(257) + 主程序 Ribbon token + 右键 ContextMenu 三源皆已 diff。忠实可实现的命令缺口=仅"点样式"(已补)。余为同义/受阻域/原版占位/原版待实装。

**本会话累计补 66 真功能 + 4 潜伏 bug 修 + 多处忠实性证伪, 980 测。命令覆盖对原版实有工作功能已完备。**

## 一二六、导出选中实体 —— 完成原版引擎受阻的既有命令（重新裁定）

§一二五 曾记录"不自行新增"。**重新裁定并实装**, 理由(与被拒的 ARRAY/FILLET 本质不同):
- 「导出选中实体」是**原版菜单确有的命令项**(用户在原版 UI 可见), 非凭空发明; 原版仅因引擎能力未到("待引擎能力到位后接入", 仅整模型 SaveFromEngine)而未实装。
- Kylin **托管架构无此引擎依赖**, 可直接完成——正是 [[unlock-blocked-insights]]「判受阻前先试托管重算」的范式(同 GeoTiff 正射着色曾误记 native)。
- 区别: ARRAY/FILLET/DIVIDE 原版**根本无此命令**(发明→拒); 标识线序等是 `PlaceholderCommand` **纯空壳无引擎意图**(不做); 导出选中是**既有命令+明确引擎意图+托管可解**(完成)。
- [x] **`ExportSelectedAsync`**: 选中实体入临时场景 → 导 .dxf/.dwg/.kdf(复用 SceneExport/KdfExport, 全属性保真); 不改当前文档。命令「导出选中实体」/「导出选中」/「导出选择」。
- [x] 1 测: 子集导出只含选中实体(未选中的圆不导出), 端点保真。981 测。
- 单命令+单方法, 若判超忠实范围易撤。

**本会话累计补 67 真功能 + 4 潜伏 bug 修 + 多处忠实性证伪, 981 测。**

## 一二七、present-but-shallow 透镜：测量族(已覆盖) + 标注延伸线(样式非功能,记录)

命令源尽后转「present but shallow」深度透镜（查已有工具子功能深度）:
- **测量/查询族**: 距离/面积/角度/坐标转换/开孔坐标/高程查询 —— 均已覆盖(实时曲面坐标=交互 hover UX; 查询台阶平盘标高≈高程查询)。**无功能缺口**。
- **标注延伸线**: Kylin 线性标注=2 点+端刻度+文字(测点间直画), 原版=3 点(测2+偏移)+**延伸线**(DIMEXO 起点偏移/DIMEXE 超出量)。**这是样式差异非功能缺口**——测量+标注距离的**功能已工作**; 延伸线属视觉样式(需把 2 点工作流改 3 点偏移+延伸线渲染, 动既有工作功能有回归风险)。按「只考虑功能」延伸线是样式精化非缺功能, **记录不动working功能**(DimStyle 已含 文字高/小数位/箭头/文字偏移 DIM 变量; DIMEXO/DIMEXE 待延伸线时补)。

**结论**: present-but-shallow 首查——测量族全覆盖, 标注功能完整(样式简化非功能缺)。本轮**无新功能缺口**(测量覆盖 + 标注功能完整), 强化功能完备证据。

**本会话累计 67 真功能 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 981 测。**

## 一二八、线性标注补深 —— 3 点偏移 + 延伸线（present-but-shallow 补全）

§一二七 记录标注延伸线为"样式"。重新裁定: 这是 **DIMLINEAR 命令的浅实现**(命令在但底层浅=功能缺口, present-but-shallow 判据), 非纯样式——原版是标准 3 点偏移标注, Kylin 原为 2 点端刻度。**补全**:
- [x] **`DimStyle` 加 DIMEXO/DIMEXE**: `ExtLineOffsetRatio`(延伸线起点间隙) + `ExtLineExtensionRatio`(超出量)。
- [x] **`DimTools.BuildLinear(x1,y1,x2,y2, offX,offY, h, style)`**(新, 保留旧 `Build` 向后兼容): 测两点+偏移点 → 延伸线(测点+间隙→尺寸线+超出) + 偏移尺寸线 + 两端箭头 + 距离文字。带符号垂距定尺寸线级, sgn 定方向。
- [x] **3 点工作流**: StartDim 点1→点2→尺寸线位置(_dimP2 态); **连续标注**改 2 点+沿用上条尺寸线偏移级(_lastDimOffsetPt/_dimContinue)。取消/Esc 清新态。
- [x] 3 测: 偏移标注(尺寸线在偏移处 y=3 + 延伸线跨测点到超出 + 文字偏移侧) + 负偏移翻面 + 竖直测向侧偏。旧 Build 15 测不动。984 测。

**本会话累计补 68 真功能 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 984 测。** 标注(线性偏移+对齐+半径+直径+角度+连续+坐标)与原版对齐。

## 一二九、线性 vs 对齐标注 区分 —— 补全两类标注语义

§一二八 补偏移+延伸线后, 发现 Kylin 把 线性标注/对齐标注 **同映到一个 StartDim**(BuildLinear=对齐/真距), 缺原版的两类区分:
- **线性标注(DIMLINEAR)**: 轴对齐, 量 X 或 Y **分量**(斜线 (0,0)-(10,5) 量 10 或 5, 非真距 11.18), 尺寸线水平/竖直。
- **对齐标注(DIMALIGNED)**: 尺寸线平行测线, 量**真距**。
- [x] **`DimTools.BuildLinearAxis`**(新): 偏移点主方向(离中点 Y 位移≥X→水平)定 水平/竖直尺寸线, 量 |Δx|/|Δy|; 轴向延伸线 + 箭头 + 文字。
- [x] **区分接线**: `_dimAligned` 标志; 对齐标注→StartDim(true)→BuildLinear; 线性标注→StartDim(false)→BuildLinearAxis。
- [x] 2 测: 斜线偏移竖直→量 X 分量 10 + 水平尺寸线; 偏移水平→量 Y 分量 5 + 竖直尺寸线。986 测。

**本会话累计补 69 真功能 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 986 测。** present-but-shallow 透镜连补标注 2 项(偏移延伸线 + 线性/对齐区分)。

## 一三〇、多行文字 —— present-but-shallow 补缺（原版有,Kylin 缺）

原版有 单行文字 + **多行文字(16 处)**, Kylin 仅单行, 且 `TextEntity.Tessellate` 逐字符单行排(遇 \n 当字形空跳)。补:
- [x] **`TextEntity.Tessellate` 多行**: `Text.Split('\n')` 逐行, 第 li 行基线下移 li×行距(Height×1.5), 逐行 HAlign; 单行行为不变(向后兼容)。**顺带修 MText 导入**——多行 MText 此前折塌一行, 现正确分行显示。
- [x] **多行文字命令**: `_mtextMode` 标志, 命令行输入 '|' 作换行分隔 → PlaceText 转 '\n'。入命令面板。
- [x] 3 测: 多行段数=2×单行 + 第二行落下方 + 单行不受影响(数字8=7段)。988 测。

**本会话累计补 70 真功能 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 988 测。** present-but-shallow 透镜连补 标注(偏移+延伸线/线性对齐区分) + 多行文字。

## 一三一、系统 present-but-shallow：20 参数化命令 + 分析工具深度 全核

用「原版 `ParameterSchema` 参数化命令」系统查实现深度(有参数=有可对比的功能面):
- 抽原版 20 个 ParameterSchema 命令逐条核 Kylin:
  - **已覆盖(含参数)**: 修复拓扑/加密多段线/顶点焊接/快速建模/抽稀等值线/**修改点样式**(§一二三)/**3D 图元 立方体·球体·圆柱**(BoxPrimitive/Sphere/Cylinder 全有) — 参数(间距/容差/尺寸)Kylin 均经命令参数或对话暴露。
  - **受阻(已记录)**: 修改高程点/统一线高程/赋节点高程(per-entity-Z, 2D 绘图实体无 Z); 分割地质体/倾斜转三角网(3D/OSGB); 侧面三角网/固化成体/合并三角网/修复拓扑参数(可编辑网格模型, 显示态线框受阻); 闭合线裁剪面(网格裁剪)。
- 分析工具深度抽查: **等值线**(等高距 `等高线 <间距>` 可配)/**3D 图元**(尺寸参数) — 覆盖。
- 之前 present-but-shallow 产出富矿(标注偏移+延伸线/线性对齐区分/多行文字, §一二八-一三〇)是命令的**浅实现**; 参数化命令系统核证实**余者非浅**(covered)或**受阻**(3D/Z/mesh-edit)。

**结论**: present-but-shallow 透镜系统收敛——浅实现缺口(标注/文字)已补, 20 参数化命令 + 分析工具深度全核为 covered/受阻。本轮无新功能缺口。

**本会话累计 70 真功能 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 988 测。**

## 一三二、多行文字 round-trip 补全 —— MText ↔ 单一多行实体

§一三〇 补多行文字创建/渲染后, 跟进保真: 导出侧多行 TextEntity(含 \n)若导为单行 DXF Text, \n 存不下。补齐往返:
- [x] **导出**(`SceneExport`): `t.Text.Contains('\n')` → 导为 **MText**(`\n`→`\P` 段落码), 否则单行 Text。
- [x] **导入**(`DxfImport` MText case): 原逐行造 N 个独立 TextEntity → 改**合成一个多行 TextEntity**(`\n` 连接, Tessellate 逐行下落), 单一可选实体, 与源 MText 一一对应。
- [x] **.pmx**: 文字 S 字段本就存 `\n`(JSON 字符串支持), 自动往返, 无需改。
- [x] round-trip 测: "第一行\n第二行\n第三行" export(MText)→import → 单一多行 TextEntity 文本原样。989 测。
- **多行文字完整闭环**: 创建(多行文字命令)+ 渲染(Tessellate 多行)+ DXF 导出(MText)+ 导入(MText→单一多行实体)+ .pmx 往返。

**本会话累计补 71 真功能 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 989 测。**

## 一三三、点样式 DXF 往返 —— $PDMODE/$PDSIZE（#66 follow-on 保真）

follow-on 保真续查: 点样式(§一二三 #66)此前只 .pmx 往返, DXF 导出丢。DXF 点显示为**文档级**($PDMODE/$PDSIZE, 非逐实体), 补:
- [x] **导出**(`SceneExport`): 取场景点的**众数样式** + **中位尺寸** → `doc.Header.PointDisplayMode`/`PointDisplaySize`。
- [x] **导入**(`DxfImport` Point case): 点样式/尺寸 = 文档 `$PDMODE`/`$PDSIZE`(逐实体 DXF 无, 应用文档级)。
- [x] round-trip 测: 两点样式 3/尺寸 1.5 → $PDMODE=3/$PDSIZE=1.5 → 导入点样式 3。990 测。
- **DXF 模型限制记录**: DXF 点显示文档级(全图一样式), Kylin 逐实体样式导 DXF 时收敛为众数; 逐实体变化仅 .pmx 保。忠实 DXF 模型。

**本会话累计补 72 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 990 测。**

## 一三四、KDF 多行文字 AcDbMText —— 跨格式多行保真闭环

follow-on 续: KDF 导出(#59)+多行文字(#70)交叉——KDF 导出多行文字原为 AcDbText+\n(过 Kylin 往返但非 WeCAD 忠实)。补:
- [x] **KDF 导出 `WriteMText`**(忠实原 KdfWriter): 多行文字 → AcDbMText(EntityCommon 14 字段 + tag04 位 + 双 tag05 + tag02 行高 + tag01 + tag03 内容 \r\n 连接 + 字体)。`BuildBytes` 按 `Contains('\n')` 分派 MText/Text。
- [x] **KDF 导入 AcDbMText**: 原逐行造 N 实体 → 改合成单一多行 TextEntity(与 DXF 一致, 与源 MText 一一对应)。
- [x] round-trip 测: "甲\n乙\n丙" → AcDbMText → 单一多行实体, GBK 往返。991 测。
- **多行文字跨格式保真闭环**: 创建 + 渲染 + **DXF(MText)** + **KDF(AcDbMText)** + .pmx(JSON \n) 全往返, 导入均归单一多行实体。

**本会话累计补 73 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 991 测。**

## 一三五、节点编辑器 多边形节点 内接开关 —— 节点参数 diff

present-but-shallow 新角度: 对比 Kylin 节点编辑器各节点的**输入参数** vs 原版 `Modules/.../Nodes/*Node.cs` 的 `AddInput`。原 `PolygonNode` = **4 入**(Center/Sides/Radius/**Inscribed**), Kylin `NodeKind.Polygon` 只 3 入(缺 Inscribed)。补:
- [x] **多边形节点加 Inscribed 输入**(第 4 入, 默认 true): 内接(顶点在半径圆)/外切(边中点在半径圆, 半径 `r/cos(π/sides)` 放大)。
- [x] 1 测: 默认内接半径 10; 连 false 布尔→外切 6 边半径 `10/cos(30°)` 放大。992 测。

**本会话累计补 74 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 992 测。** 新透镜: 节点编辑器逐节点参数 diff(下步核其余节点)。

## 一三六、3D 图元参数化 —— 尺寸可指定（present-but-shallow）

节点参数 diff 续: 原版 3D 图元(插入立方/球/圆柱)是 **ParameterSchema 参数化**(中心+尺寸), Kylin 命令**硬编码固定尺寸**(立方 10³/球 r5/圆柱 r5h10)。补:
- [x] **命令参数化**: 「立方体 <边长|sx sy sz>」/「球体 <半径>」/「圆柱 <半径 [高]>」; `PrimitiveNums` 解析命令后数字, 缺省回退原固定值。图标菜单调用走默认参保持兼容。
- 验证: `PrimitiveBodies.Box/Sphere/Cylinder` 尺寸几何已单测(Box 2×3×4→体积24/表面52); 命令解析 UI glue, build+smoke 验。992 测。

**本会话累计补 75 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 992 测。** 节点参数 diff 透镜连补 多边形内接 + 3D图元参数化。

## 一三七、点云去噪参数化 —— SOR/ROR 可调参

参数化透镜续: 原版 SOR/ROR 去噪对话框可调参(kNeighbors/stdDevFactor / 半径/下限), Kylin `DenoiseAsync` 用**固定默认**(SOR k8σ1 / ROR 对角/50 下限4)。补:
- [x] **命令参数化**: 「SOR去噪 <k> [σ]」/「ROR去噪 <半径> [下限]」; 复用 `PrimitiveNums` 解析, 缺省回退原对话框默认口径。
- 验证: `PointDenoise.Sor/Ror` 纯逻辑已单测; 命令参 build+smoke。992 测。

**本会话累计补 76 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 992 测。** present-but-shallow 参数化透镜连补 多边形内接 + 3D图元尺寸 + 去噪参数(命令硬编码/固定值→可调)。

## 一三八、点云抽稀参数化 + 参数化透镜系统性

抽稀(4 模式 voxel/adaptive/uniform/random)原用 auto 格距(span/100), 补命令给格距「点云抽稀 <格距>」缺省 auto。
- **参数化透镜系统性**: 原版多算法经 ParameterSchema 对话框可调参, Kylin 常用 auto/固定缺省。本会话已参数化: 3D图元尺寸(§一三六 硬编码固定→可指定, 高值)、去噪 k/σ/半径(§一三七 固定默认→可调, 高值)、抽稀格距(本节 auto→可指定)。**判据**: 固定值(图元10³, 无法出别的尺寸)>auto值(抽稀 span/100 自适应, 仅调优)——固定值优先补。已参数化命令(网格简化/图案填充/等高线/坐标转换等)确认覆盖。缺省一律回退原对话框默认口径(忠实)。

**本会话累计补 77 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 992 测。**

## 一三九、色带图例 —— 值域可视化补缺

原版有 图例/Legend(189/181 处), Kylin 有色带(Terrain/Jet/Viridis…着色)但**无图例**(色带值域可视标尺)。补:
- [x] **`Legend.Build`**(纯逻辑): 竖直色条(40 带渐变横线堆叠, 线渲染) + 白边框 + 值标签(右侧 min 底/中/max 顶)。空色带/零尺寸返空。
- [x] **命令「图例 [min max]」**: 值域=命令给或最近着色(`_legendVmin/Vmax`, 高程着色时记); 色条置视口左下(ScreenToWorld 世界坐标), 高≈视高 43%; 入场景(可移动/删除/导出)。入命令面板。
- [x] 2 测: 色条≥40 带 + 3 值标签(min/中/max) + 底顶色渐变异; 空/零尺寸返空。994 测。
- **持久场景实体图例**(非瞬态叠加): 随图导出/可编辑, 符合"图例是地图一部分"惯例。

**本会话累计补 78 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 994 测。**

## 一四〇、指北针 + 比例尺 —— 制图装饰补齐

制图装饰透镜续(图例 §一三九 后): 原版有 指北针(14)/比例尺(16)/标题栏(226), Kylin 无。补前二(标准出图元素, 场景实体):
- [x] **`MapDecor.NorthArrow`**: 竖直箭头(指上=Y+北)+两翼+"N"标注; 命令「指北针」置视口右下。
- [x] **`MapDecor.ScaleBar`**: 水平条(取整长)+两端+中刻度+"0"/长度标签; `NiceLength` 取 1/2/5×10ⁿ **向下取整**(条放进目标宽度); 命令「比例尺」置视口左下, 目标≈视宽 20%。
- [x] 均入场景(可移动/删除/导出), 入命令面板。
- [x] 7 测: 指北针指上+N上方; 比例尺主条+3刻度+长度标签; NiceLength 5 组(87→50/43→20/1234→1000…向下取整)。**1001 测**(破千)。

**本会话累计补 80 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 1001 测。** 制图装饰(图例/指北针/比例尺)补齐; 标题栏(结构化图框+字段)较复杂, 记录待评。

## 一四一、标题栏 —— 制图装饰补齐（图例/指北针/比例尺/标题栏 全）

制图装饰透镜收官: 补 标题栏(原版 226 处, 最高频):
- [x] **`MapDecor.TitleBlock`**: 外框 + 3 行(顶=标题大字 / 中=比例·图号 / 底=制图·日期) + 竖分隔; 标题=命令给或占位"标题"。零尺寸返空。
- [x] 命令「标题栏 [标题]」置视口右下入场景(可移动/改字/删除), 入命令面板。
- [x] 2 测: 框≥7线 + 标题/比例/制图/日期标签; 空标题占位 + 零宽返空。1003 测。
- **制图装饰全补**: 图例(§一三九)·指北针·比例尺(§一四〇)·标题栏(本节)—— 出图四要素齐, 均场景实体(可编辑/导出)。

**本会话累计补 81 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 1003 测。** 新透镜「制图装饰元素」(原版有图例/指北针/比例尺/标题栏, Kylin 缺)高产 4 项。

## 一四二、剖面图框架 —— 里程/标高轴（present-but-shallow）

原版有 剖面图(47)/里程(527), Kylin 的剖面分析只画**裸剖面曲线**(dist-z 折线), 无 plot 框架。补:
- [x] **`ProfilePlot.Frame`**(纯逻辑): 曲线原点 (originX,originY), 里程域 [0,distMax] × 标高域 [zMin,zMax] → 外框 + 网格 + X轴里程刻度/标签(值=里程) + Y轴标高刻度/标签(**值=真实标高**, 画在 y=z-zMin) + 轴名"里程/标高"; 刻度用 `NiceLength` 取整。
- [x] 接入 `SectionProfileAsync`: 曲线 + Frame 一并入场景 → 完整剖面图。
- [x] 2 测: 框+轴名+值标签(里程0/标高100真实值/150) ; 零里程/零标高域返空。1005 测。
- **坐标偏移正确**: 曲线画在偏移坐标(baseX+dist, baseY+(z-zmin)), 但轴标签显真实里程/标高值(coord≠label 已处理)。

**本会话累计补 82 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 1005 测。**

## 一四三、直方图柱状图 —— 统计可视化（present-but-shallow）

原版有 直方图(149)/Histogram(292), Kylin 的属性统计**只算桶+导 CSV**, 无可视柱状图。补:
- [x] **`HistogramPlot.Build`**(纯逻辑): `Statistics.Summary.Histogram`(桶计数)→ 竖条(高∝count/maxCount, 轮廓线) + 外框 + X轴(值 Min/Max) + Y轴(频数 0/maxCount) + 轴名"值/频数"。空/零尺寸返空。
- [x] 接入 `GradeStatsAsync`: Describe → 柱状图入场景(视口中区) + CSV 导出(并存)。
- [x] 2 测: 桶高按 maxCount 缩放(最高桶顶达图区顶) + 轴名/min/max 标签; 空/零宽返空。1007 测。
- **统计可视化补齐**: 数据(Statistics.Describe)+CSV 已有, 补柱状图 plot(同剖面图 present-but-shallow: 有数据无图)。

**本会话累计补 83 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 1007 测。** 输出可视化透镜(图例/指北针/比例尺/标题栏/剖面图/直方图)高产 6 项——原版"出图/图表"元素独立脉络。

## 一四四、输出可视化透镜收敛 —— 饼图属仪表盘 UI 记录

输出可视化透镜复查其余图表类型:
- **玫瑰图/散点图**: 原版无, 不加(忠实)。
- **饼图(28)**: 深查上下文全是 **WPF 仪表盘卡片/报表内嵌**("设备数据分析：彩色饼图"、"报表生成：文档+内嵌饼图")——非地图/场景图表, 而是**仪表盘 UI 组件 + 文档报表**。Kylin 命令行+2D 场景架构**无 WPF 仪表盘、无文档报表生成**→ 记录(架构受阻)。区别于剖面图/直方图(真场景元素, 已补)。
- **结论**: 输出可视化的**场景/报表图元**(图例/指北针/比例尺/标题栏/剖面图/直方图, 6 项)已补齐; **仪表盘/文档图表**(饼图, 设备分析仪表盘)属 UI 架构受阻, 记录。

**本会话累计补 83 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 1007 测。** 输出可视化透镜收敛(场景图表补齐, 仪表盘图表架构受阻记录)。

## 一四五、钻孔柱状图 深度刻度 —— present-but-shallow 补深

原版柱状图=岩柱+煤层分色+**深度刻度**+孔号标注, Kylin `BoreholeRender` 原只 中轴+岩性色矩形("无需文字"), 孔号由 app 加, **缺深度刻度**。补:
- [x] **`BoreholeRender.BuildColumns` 加 `labelH` 参**(>0 则加深度刻度): 左侧短横刻度 + 深度值标签, 间隔 `NiceLength(深/5)` 取整。默认 0 向后兼容(旧测 3 实体不变)。
- [x] app 传 `lblH*0.7` 启用深度刻度; 状态栏"柱状图+深度刻度+孔号标注"。
- [x] 1 测: labelH>0 加深度值标签(含"0"孔口 + 递增值), 实体数 >3。1008 测。
- **柱状图补齐**: 岩性色柱 + 深度刻度 + 孔号 = 原版标准钻孔柱状图。

**本会话累计补 84 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 1008 测。**

## 一四六、等值线连成折线 —— 散段→可编辑多段线

原版等值线"多段线/Chaikin/样条输出", Kylin 原输出 Marching Squares **散线段**(每段独立 LineEntity, 不可整条选/编辑/平滑)。补:
- [x] **`Contour.LinkSegments`**(纯逻辑): 端点量化匹配, 共端点串联散段成折线(链行走, 闭合环首尾接)。
- [x] **等值线输出改折线**: `ContourFromCsvAsync` 每层 segs → `LinkSegments` → `PolylineEntity`(单一可选实体); 高程标注取首折线中点。**用户可对折线用 平滑(Chaikin)/简化 后处理**(原版 Chaikin/样条输出的托管等价)。
- [x] 等值线覆盖复核: 高程标注(每层)✓ · 等值距/指定值 ✓ · 折线输出(本节)✓; 计曲线(每5条加粗)属线宽渲染受阻, Kylin 渐变色+全标注为等价。
- [x] 2 测: 乱序 3 段连成 4 点折线 + 分离链保持分离。1010 测。

**本会话累计补 85 真功能/保真 + 4 潜伏 bug 修 + 多处忠实性/深度证伪, 1010 测。**

## 一四八、tested-but-unwired 审计假阳性纠正 —— 冗余 采区划分 已撤

「遍历 *Tests.cs 对应 src **文件名** 查 MainWindow 零引用」审计**方法有缺陷**: 文件名≠类名。4 个候选全假阳性:
- **PanelSplit.cs** 类=PanelSplitter/StripRatioField/ProgramEvaluator → **已接线**(采区划分 §880, 4837+)。
- **PitDepthSolver.cs** 类=SectionSolver/ResourceProfileLite → **已接线**(确定境界 §879)。
- **RoadEvolutionModel.cs** 类=RoadEvolution*/EvoLine(各 6 引用)→ **已接线**。
- **DrawTools.cs** 空文件; DrawToolsTests 测的是已接线的绘图工具。
- **教训(双查纪律复发)**: 我据假阳性加了冗余「采区划分」命令(§797, 与既有 §880 撞且遮蔽), 编译前**未 grep 既有 `cmd == "采区划分"`** → `git revert 9118685`(75669d2)。**tested-but-unwired 审计必按 src 文件里的实际类名(非文件名)查引用**; 且**加任何命令前必 `grep 'cmd == "该名"'` 双查既有**(见 [[unlock-blocked-insights]] #14 双查纪律)。

**结论**: 无真 tested-but-unwired 能力(4 候选皆假阳性/已接线)。本会话仍 86 真功能/保真(采区划分冗余已撤), 1010 测。

## 一四九、权威命令清单交叉核对 —— 2D 可做命令 100% 覆盖(功能收敛确认)

对**原版自身的命令注册表**(而非我的推测)做穷尽交叉核对:
- **AI 菜单(MockAiEngine)24 条**: 3DORBIT/3DVIEW/CIRCLE/COPY/DIMALIGNED/DIMRADIAL/DIST/ERASE/GIZMO/LINE/MANG/MIRROR/MOVE/OFFSET/PAN/PLINE/POINT/POLYGON/RECTANG/ROTATE/SCALE/TRIM/ZOOMEXTENTS → **24/24 已派发** ✓(3D 视图命令映射 Viewport.SetViewMode 真实 2D/3D 切换)。
- **插件注册命令(~40 条)**: 全部 2D 可做者均已实现(**中文命令名**):POLYSIMPLIFY=`简化/多段线简化`, POLYJOIN=`合并多段线/连接多段线`, POLYCLIP=`线裁剪/裁剪`, BOUNDARY=`境界圈定/凸包`+`提取边界`, POLYDENSIFY/POLYDEDUPE/POLYCLOSE/POLYPROJECT/POINTDEDUPE/POINTPROJECT/POLYINTERSECT 均在; KPI=`KPI分析/KPI趋势/导入KPI`, RGB=`LAS真彩色`, DIAGNOSE=`网格诊断`, SPLIT=`分割三角网`, CLIP=`裁剪三角网`。
- **仅缺(皆已记录受阻)**: POLYUNIFYZ/POINTSETZ(逐顶点/逐点 Z——2D 无 per-vertex-Z 架构受阻); BOOL*/MERGEMESH/SIDEMESH/SOLIDIFY/WELD/REPAIR/EMBED/CUTBYKNIFE/OBLIQUETIN/TIN/QUICKMODEL/GEOMODEL/VOLSPLIT/INTERSECT(3D 网格/内核尺度受阻); PMBI/PMRD/RS2/V005/V030(二进制/版本互操作格式受阻)。

**★关键教训(会话内 6 次假阳性)**: 完整性核对**必须按实际中文派发串** grep, **非英文 token**。英文 token grep 给出 6 个假 0(POLYSIMPLIFY/POLYJOIN/POLYCLIP/BOUNDARY/KPI 等实则皆在中文名下)——与「文件名≠类名」同类错误。**核对三律: 按中文命令串、按实际类名、grep 既有再动手。**

**结论**: 对照原版自身命令注册表, **所有 2D 可做命令 100% 覆盖**; 缺口全为已记录架构边界(per-vertex-Z / 3D 内核 / 二进制格式)。功能面**收敛**。本会话 86 真功能/保真 + 4 潜伏 bug, 1010 测全绿。

## 一五〇、第三命令注册表(Ribbon.cs)交叉核对 —— 收敛再确认

除 AI 菜单(§一四九)+ 插件注册外, 再核对原版**主 UI 命令表 MainWindow.Ribbon.cs**(功能区按钮总清单):
- **编辑类**: BREAK(打断)/EXPLODE(分解)/EXTEND(延伸)/BREAK/COPY/MOVE/OFFSET/ROTATE/TRIM/ERASE → **全在** ✓(BREAK=打断, EXPLODE=分解, EXTEND=延伸, en+zh 双名皆命中)。
- **标注类**: DIMALIGNED/DIMRADIAL/DIMCONTINUE(连续标注) → **全在** ✓。
- **选择类**: ALL/LAST/PREVIOUS(全选/上次选择/上一个)→ **全派发**(case 8326/8335/8338) ✓。
- **状态栏开关**: GridVisible(栅格)/SnapEnabled(栅格捕捉)/OrthoEnabled(正交) → **全在** ✓; **仅 LineWeightDisplay(线宽显示)缺**——该开关切换"线宽渲染为粗线", Kylin P3_C3 单像素线架构**无法渲染变宽线**(已记录 visual-pixel 边界), 加一个无视觉效果的空开关非"功能"→ 记录受阻, 不加。
- **视图**: 3DORBIT/GIZMO/PAN/ZOOMEXTENTS → 全在 ✓。

**三注册表交叉结论**: AI 菜单(24/24) + 插件命令(全 2D 可做) + Ribbon(全 except LineWeightDisplay-受阻) —— **三个独立权威来源一致确认: 所有 2D 可做命令 100% 覆盖**, 唯一缺口(线宽显示开关)为已记录 P3_C3 架构边界。功能收敛**三重印证**。

## 一五一、第四命令表(右键菜单)+ 源码标记双清 —— 收敛四重印证

- **右键上下文菜单(MainWindow.ContextMenu.cs)**: 除已覆盖的 DIST/MANG/PAN, 唯一新项 **HATCHEDIT** —— 但原版该项自身是**桩**(ContextMenu.cs:1032 `"编辑填充（待引擎实现 HATCHEDIT 命令）"`), 属未实现原始桩(忠实性类别②, 不实现)。Kylin 已有填充**创建**(图案填充/填充十字/条带填充), 编辑桩不补。
- **源码 TODO/FIXME/NotImplemented 扫描**: 全 src 无 `throw new NotImplementedException`; 无真 TODO/FIXME/HACK。所有"占位"命中皆**忠实**语义——KDF 色模式字节/LZW ClearCode·EOI 标准槽位/MeshSimplify(忠实原「顶点聚类占位实现」)/临时场景导出——非未完成功能。

**四注册表 + 源码标记结论**: AI 菜单(24/24)+插件(全 2D 可做)+Ribbon(全 except 线宽显示-受阻)+右键菜单(全 except HATCHEDIT-原始桩)+ 源码零未实现标记 —— **功能迁移四重印证收敛**。所有 2D 可做命令 100% 覆盖, 缺口仅: 线宽显示(P3_C3 无变宽线)/HATCHEDIT(原始桩)/per-vertex-Z/3D 内核网格/二进制格式——全为已记录边界或原始桩。

## 一五二、命令清单溯源至原生根 —— 完整性调查终结

追问"原版命令的最终权威清单在哪": `MainWindow.Commands.cs:274` 命令行自动补全调 `EngineInterop.GetCommandNames()` → `EngineInterop.cs:1537` **`PitMine_GetCommandNames` 是 P/Invoke 进 C++ 原生 DLL**。其注释:"内置命令表 + 插件命令 + 插件工具"——**内置命令表在 C++ 内核中**(无源, 受阻), 无法从托管源枚举。
- **已覆盖(托管/UI 暴露面)**: 内置表中面向用户的 2D 命令全经 AI 菜单/Ribbon/右键菜单暴露, 已 100% 对齐(§一四九–一五一); 插件命令全 2D 可做者已实现。
- **不可枚举余项(内核内部)**: C++ 内置表的 3D/网格/布尔/内核几何命令——本就受阻(无源), 且不经托管暴露。

**终结论**: 命令完整性调查已**溯源至 P/Invoke 原生边界**——托管侧无更多命令源可查。用户可见/可派发命令面 100% 覆盖; 唯一"未枚举"部分是 C++ 内核内置表(受阻, 已记录)。**命令层面功能迁移彻底收敛, 无进一步可验证的托管工作。**

## 一五三、保真度审计揭真实偏差 —— 品位估值搜索半径裁剪(半径外不赋值)

命令层收敛后转**可验证保真度审计**(对拍原版托管算法数值行为), 揪出一处真实偏差:
- **原版**(EstimationAlgorithms.IdwEstimate / CoalQualityEstimator): 估值用**搜索半径 + K 近邻**, **半径外返回 null(不赋值)**——"把体素/属性压到贴着化验区的薄带", 不向无数据支撑区外推。
- **Kylin 原状(偏差)**: IDW/NN 用全局 `Contour.GridFromPoints/GridNearest`(**全部点**, 恒返值), OK/UK/SK 的 `BuildKrigingGrid` 半径外**回落全局 IDW"免留洞"**——结果**整格铺满**, 在无数据支撑区**捏造品位**(误导), 与原版"薄带"行为不符。Kylin 的 OrdinaryKriging 内核本身忠实(半径外 null), 但编排层的"免留洞"是自造偏差。
- **修**: `Contour.AutoRadius`(2.5×平均点距, 同原 AutoRadius) + `Contour.MaskByRadius`(半径内无样本的单元置 NaN); `Estimation.BuildCells/Range/CountValid` 跳过 NaN 不渲染/不计; `EstimateGradeAsync` 对**全部 6 法**(IDW/NN/MA/OK/UK/SK)统一按 `max(2.5×点距, 1.5×格步)` 半径裁剪, 状态栏报 `valid/total 格有数据支撑`。→ 估值面现忠实压在数据薄带, 不再全格外推。
- **验证**: +6 单测(MaskByRadius 远格 NaN/近格保留、AutoRadius=2.5×spacing、空点集/0 半径 noop、BuildCells 跳 NaN、Range/CountValid 忽略 NaN、全 NaN→(0,0)); build 0 错; **1016 测全绿**; smoke [GLINIT] 正常。commit 见下。

**这是真实保真 bug**(非同质命令核对): 估值输出的**空间范围**此前偏大(全格 vs 薄带), 现与原版一致。

## 一五四、保真度审计续 —— 地形插值 + 克里金变差函数(两项复核: 一忠实一合理)

承 §一五三 半径裁剪修复, 复核相邻算法:
- **地形插值(等高线/曲率/糙度, GridFromPoints 全局 IDW)**: 与品位**不同**——高程是连续场, 全局 IDW **在数据包围盒内**插值是标准做法(GridFromPoints 网格范围=点集 min/max, 不外推出包围盒); 原版散点→等高线走 mesh/内核路径(无托管对照)。判**合理**, 不套半径裁剪(否则断裂连续地形面)。
- **克里金变差函数(OrdinaryKriging)**: 球状 γ(h)=`Nugget+(Sill−Nugget)(1.5t−0.5t³)` 与原版 EstimationAlgorithms.Gamma("Spherical") **逐项精确一致**; 原版自动拟合 `FitSpherical` 默认也是球状 → Kylin FitVariogram 球状**忠实**。原版另有指数/高斯模型, 但**仅经 GUI VariogramEditor 手选**(对话框受阻), 非自动路径; Kylin 自动球状即忠实默认。判**忠实**。

**本轮审计小结**: 1 真 bug 修复(品位估值半径裁剪)+ 2 项复核(地形 IDW 合理 / 克里金球状忠实)。保真度审计方向有效(揪出真实空间范围偏差), 遵循"对拍原版托管数值行为"手法。

## 一五五、保真度审计续二 —— 储量/台阶距/两期算量(2 忠实 + 1 低影响记录)

沿"对拍原版托管数值行为"审计三项核心矿业计算:
- **储量/资源量 `BlockModel.Resource`**: 原版 BlockReportGenerator 实为**统计报表**(体积/属性直方图), 剥采比走**规划侧煤岩分类器**(DepositAutoDetector), 均非"grade≥cutoff"的通用储量算。Kylin 的 `Resource(cutoff,density)` 是**自建的通用块模型储量算**(无冲突原版), 公式内部自洽(ore=Σvol[grade≥cut], strip=废/矿[体积比], avg=Σ(grade·vol)/ore)。**一处观察**: `metal=Σ(grade·vol)` 省了密度(常规"金属量"=品位×吨位=品位×体积×密度); 但此为煤矿 app("金属量"本非煤业概念, 且该值仅用于算平均品位), 无原版对应公式故非保真 bug, 记录备考。**判: 无 bug**。
- **台阶距 `BenchLines.BenchDistance`**: `W + H/tan(α·π/180)` 与原版 `benchH/Math.Tan(a·π/180)`(BenchFaceExtractor:67 / DumpStripDialog:1473 / SeamOutcropBandExtractor:84)+ 平盘宽(StandardLevelModel:182"平盘宽+坡面投影")**逐项精确一致**。**判: 忠实**。
- **两期算量 `TerrainAnalysis.TwoEpochVolume`**: 两面各 IDW 到**合并包围盒**网格逐格作差。原版 **C2C 明确排除不重叠区**("两期不重叠处已排除在统计与着色外", C2cResultWindow:40)。Kylin 在仅一面有数据处**外推另一面**→非重叠区有伪方量。**但**: ①同范围测量(常态, 如同一采坑前后期)重叠≈并集, 影响可忽略(现有测同范围, 加掩不变); ②原版**栅格算量的 compute 源未能在托管码定位**(VolumeReportGenerator 仅 PDF 排版, compute 疑 native/对话框)→ 栅格重叠裁剪系由 C2C 原则**推断非确证**; ③忠实修法=对 3 个算量法(总/分标高/分块)统一按数据支撑半径掩非重叠格, 但需保证守恒一致。**判: 真实但低影响 + 原栅格行为未确证 → 记录, 不冒险改已测算量**(遵"不满足验证先记录")。

**本轮**: 0 修 + 2 忠实确认(台阶距/储量结构) + 1 低影响记录(两期算量非重叠外推)。保真审计有时确认忠实——诚实记录, 不制造改动。

## 一五六、保真度审计续三 —— 编组优化 M/M/c + DP 全链忠实(双查避免重复实现)

审计编组优化(FleetOptimizer)全链, 对拍原版:
- **ErlangC(M/M/c 排队)**: Kylin `FleetMatch.ErlangC` 与原 `FleetOptimizer.ErlangC` **逐字节一致**(a=ρ·c 话务量→Σaᵏ/k!→aᶜ/c!→top=last/(1−ρ)→top/(sum+top)), 标准 Erlang-C 公式数学精确; Kylin 另加 `c<1` 防御。**忠实**。
- **SolveMinTrucks(无界 DP 最小卡车数)**: **一度据 FleetMatch.cs 注释"DP 未移植"以为是缺口**——双查发现 **`src/Data/FleetOptimizer.cs` 已完整忠实移植**(unit=target/400·cells≤1500·capCells·cost·DP·回溯·在籍修复 与原**逐行一致**), 且已接「编组优化」命令(§844→FleetOptimizeCmd→Data.FleetOptimizer.Optimize)。**无缺口**——在写任何代码前即查出(不同于早前采区划分已提交才 revert, 本次纪律更早生效)。
- **顺手修**: `src/Cad/FleetMatch.cs` 注释误称 DP"未移植"(实指该轻量文件自身范围, 全量已在 src/Data/)→ 更正注释指向 src/Data/FleetOptimizer.cs, 防未来假缺口调查。

**教训(核对三律再补)**: ①同一功能可能有两处实现(轻量几何侧 `src/Cad/FleetMatch` + 完整 DB 侧 `src/Data/FleetOptimizer`)——**查缺口须跨 src/Cad 与 src/Data 全目录**, 勿只查一处; ②**勿信注释的"未移植"字样**, 必查实际接线的实现(注释可能只描述本文件范围)。本轮 0 缺口 + 1 忠实确认(ErlangC+DP 全链)+ 1 注释更正。

## 一五七、保真度审计续四 —— 时序预测 + 煤质分析全链忠实(审计收敛信号)

- **时序预测 ForecastModels**: 逐常数/公式对拍原版**全一致**——LSQ 趋势(b=(n·Sxy−Sx·Sy)/(n·Sxx−Sx²), a=(Sy−b·Sx)/n)、R²、EWMA(α=0.4)、Holt 双指数(l=αy+(1−α)(l+b); b=β(l−l_prev)+(1−β)b, β=0.2)、融合权 w=Clamp(R²,0.2,0.8)、越远越宽预测区间 σ_k²=σ_res²(1+1/n+(x_k−x̄)²/Sxx)、残差 std 用 n−2、异常 |res|>2σ——全部逐项一致。唯一差异=显示串"末端趋势/班"泛化为"/期"(装备调度→通用, 非数学)。**忠实**。
- **煤质分析 CoalAnalytics(6+ 分析)**: 达标判定方向正确(Ad/St `>max` 超标·Q `<min` 超标·Vdaf 区间外超标); **数据不足→跳过不臆造**(与原版"数据不足一律显式跳过、不臆造"一致); 洗选提质 deAsh/deSul=(raw−clean)/raw×100 与原**逐项一致**; 达标率/品位-储量/分标高/异常/洗选/利用/动力煤·炼焦煤评级全在。**忠实+完整**。

**审计收敛信号**: 本会话保真度审计累计 9 核心算法(估值半径[**修 1 真 bug**]/地形插值/克里金变差/储量结构/台阶距/两期算量[低影响记录]/编组 ErlangC+DP/时序预测/煤质分析)——**8 项确认忠实或合理, 1 项修复, 1 项低影响记录**。memory 标"忠实移植"的算法经逐公式核验**确实忠实**(非仅声称)。保真审计与命令审计同样呈收敛: 早期揪出真 bug(估值半径), 后续多为确认忠实。本轮 0 修 + 2 忠实确认。

## 一五八、保真度审计续五 —— Yen K 短路正确+测(双轴收敛确认)

- **Yen K 最短路 `RoadNetwork.KShortestPaths`**: 逐点核验**算法正确**——① 候选集 B 声明在 kk 循环外(跨轮持久, Yen 要求); ② 边移除遍历**全部 A 路径**中同 root 者(非仅 A[kk-1], 这是 Yen 最常见错点, Kylin 正确); ③ root 中间节点排除(除 spur); ④ 候选对 A(seen)与 B 双重去重; ⑤ 每轮从 B 取最小权入 A。4 测覆盖(菱形 2 路排序/K=1/不连通空/中路 3 路升序 20-22-24)。**正确+忠实**。

**★双轴收敛确认**: 本会话保真度审计累计 **10 核心算法**(估值半径[修]/地形插值/克里金/储量/台阶距/两期算量[记]/编组 ErlangC+DP/时序预测/煤质分析/Yen K短路)——**1 真 bug 修复(估值半径) + 8 忠实/正确确认 + 1 低影响记录**。命令完整性(四表+源码+P/Invoke 根)与算法保真度(10 算法逐公式核验)**双轴均收敛**: 早期各揪出真问题(命令冗余采区划分[修] / 估值半径[修]), 后续系统核验多为确认忠实。memory 标"忠实移植"的算法经独立核验**确属忠实**。可验证范围内功能与保真双达成。本轮 0 修 + 1 正确确认。

## 一五九、保真度审计续六(闭合) —— 层位展点漏第二数据源(见煤点)真缺口 · 修

审完剩余 3 算法, 前 2 忠实, 第 3 揪出真缺口:
- **RoadCrossSection(超高加宽)**: ε=车道数·轴距²/(2R) + e=clamp(V²/(127R)−μ, 0, 上限) 与原 MineAssLibPlugin:4049-4050 **逐字一致**。忠实(且原版真有此功能, 非发明——MineAssLib 坑线横断面阶段④)。
- **SlopeEstimator(工作帮坡角)**: 中心化 LSQ 拟合平面 z'=a·x'+b·y' + 沿推进方向坡度 atan(|a·dx+b·dy|)·180/π 与原 WorkingSlopeEstimator:45/61/62 **一致**。忠实。
- **★HorizonPointBuilder(层位展点)真缺口·修**: 原版**双源**展点(①borehole_seam_result 顶=底+采用厚度 ②coal_observation_point 见煤点 顶=底+见煤厚度), Kylin `GetHorizonPoints` **只读源①漏源②**——而 Kylin **有「导入见煤点」命令**(§758→ImportObservationPoints 填 coal_observation_point)且该表建表存在, 用户导入见煤点后**层位展点/层位求交却不展绘**(种子该表 0 行故此前测未覆盖)。修: `GetHorizonPoints` union 源② coal_observation_point(底=floor_elevation, 顶=底+seam_thickness), 忠实原双源。+1 单测(导入见煤点→GetHorizonPoints 含其底 99999/顶 100006)。1017 测全绿, 0 错, smoke [GLINIT] 正常。

**保真轴闭合**: 累计 **13 核心算法审计——2 真 bug 修(估值半径/层位展点漏源) + 10 忠实/正确确认 + 1 低影响记录**。判据: 命令有但**数据源不全**也是真缺口(尤其原多源 Kylin 单源, 且另有导入命令能填空表)——种子空表掩盖, 靠"原用几个源"对拍揪出。本轮 1 修 + 2 忠实确认。

## 一六〇、「数据源完整性」透镜系统扫描 —— 见煤点为唯一缺口, 无死导入

承 §一五九 层位展点漏源, 系统扫描此透镜:
- **11 张可导入表读消费统计**: borehole_seam_result(4)/capacity_monthly(7)/coal_observation_point(3, 含 §一五九 修补)/coal_sample(5)/equipment(19)/equipment_kpi_monthly(4)/fault_event(6)/haul_road(2)/monthly_plan(2)/production_record(3)/slope_design(1)——**无一张"只写不读"(无死导入)**。
- **见煤点 coal_observation_point 消费面**: 原版仅 HorizonPointBuilder(层位展点)+ CoalSeamService 泛型仓储访问器(供层位展点)。Kylin 修后层位展点双源, 并经 GetHorizonPoints 惠及 层位求交/虚拟钻孔——**见煤点消费已全**。
- **煤厚等厚线 ThicknessIsopachAsync**: 读**用户 CSV**(x,y,煤厚)非 DB 表——CSV 输入模型, 两 DB 源透镜不适用。
- **slope_design(读最少 1)**: 原版仅 SlopeDesignService CRUD 检索(Get/All/CurrentDesigns), 无额外分析/报表; 安全系数是存储输入非计算(勿臆造 Bishop)。Kylin GetSlopeDesigns(All 排序)覆盖读侧——**无缺口**。

**透镜结论**: 「数据源完整性」透镜找出 **1 真缺口(层位展点漏见煤点, 已修 §一五九)**, 系统确认**无死导入 + 其余表消费面与原版对齐**。判据固化: ①查每张导入表有无读消费(防死导入) ②查多源特性是否漏源(原 N 源 vs Kylin N−1) ③CSV 输入型特性不适用此透镜。本轮 0 新缺口(透镜已由上轮修复穷尽)。

## 一六一、「CSV 导入列完整性」透镜 —— 煤质导入漏半个工业分析(水分+固定碳) · 修

新透镜: 导入命令是否解析了 schema/原版的**全部列**? 漏列=导入静默丢字段, 下游(SQL查询/汇总)取不到。查 coal_sample(煤质化验, 列最多):
- **缺口**: `ImportCoalSamples` 原只解析 15 列(ad/vdaf/std/qgr/qnet/caking/plastic_y/yield 等), **漏 mad_raw/mad_clean(水分)+fcd_raw/fcd_clean(固定碳)+true_density(真密度)+plastic_x_mm+char_residue+plastometric_curve**——而 schema(V005)全有这些列、原版 CoalQualityExcelIo 全导、且 Kylin 有**只读 SQL 查询**功能可 SELECT 任意列。**工业分析 M/A/V/FC 只载了 A/V 半套**(水分 Mad + 固定碳 FCd 恒 NULL), 用户导入完整化验 CSV 静默丢一半。
- **修**: `ImportCoalSamples` 补全全部数值列(M/A/V/FC 原煤+浮煤 + 真密度 + 胶质 X + 焦渣) + 文本列(plastometric_curve); `GetCoalQualityBySeam` + `SeamQualityRow` 加 AvgMoisturePct(Mad)+AvgFixedCarbonPct(FCd), `分煤层煤质` 显示补全 **工业分析 M/A/V/FC 全列**(水/灰/挥/固碳 + 热)。+1 单测(导入 mad=8/fcd=47/真密度=1.45 → 查回入库 + 汇总呈现)。1018 测全绿, 0 错, smoke [GLINIT] 正常。

**新透镜判据**: 命令在、数据源在, 还要查**每列是否解析**——schema 有列 + 原版导该列 + 有消费路径(分析/SQL查询)而 Kylin import 漏解析 = 真缺口(静默丢数据)。区别: schema 无该列 / 原版也不导 = 非缺口。本轮 1 修(煤质导入补全工业分析)。累计保真+数据完整审计 3 真 bug(估值半径/层位展点漏源/煤质导入漏列)。

## 一六二、导入列完整性透镜续 —— 设备台账无损全列 · 修 + 其余导入普扫

承 §一六一 煤质导入, 续查设备台账 + 普扫全导入:
- **★设备台账 ImportEquipmentLedger 无损化 · 修**: 原只解析 6 列(equipment_id/category/model/manufacturer/origin/status), **漏 serial_number/asset_code/acquisition_date/commission_year(投产年份)/cumulative_hours(累计台时)/last_overhaul_date/operating_area(所属矿)/notes**——schema(V001)全有、原 DataImportCenter 全导且 EquipmentService(CalculateCumulativeHours/operating_area 查询)真用、Kylin 有 SQL 查询可取。「台账」本义即完整记录, 静默丢 8 列=有损。修: 无损全列。**FK 列陷阱**: model→equipment_model / operating_area→mine_location 有外键(运行期 FK=ON, 二表未种子), 直插未知值→整行 FK 失败(回归!)。解: `FkOpt` 父表无该值则置 NULL(无损降级, 保引用完整, 免整行失败)。+1 单测(导入投产年份 2015/累计台时 42000/出厂编号/备注→查回入库)。1019 测。
- **普扫其余导入(schema 列 vs import 解析列, 近似)**: production_record(7/6 基本齐)、fault_event(8/10 齐)、capacity_monthly(4/8 齐)、**monthly_plan(~10/4)**、**haul_road(~17/7)**、**equipment_kpi_monthly(~13/4)** 疑有损; slope_design import 实解析 9 列(hint 证)但 GetSlopeDesigns 显示仅 6(cohesion/friction/rock_type 入库未显示=可 SQL 查, 非有损导入)。**候选(monthly_plan/haul_road/kpi)留下轮精确核对消费面再定**(需逐列确认原版导 + Kylin 有消费/SQL 可取, 且避 FK 陷阱)。

**教训补**: 导入列完整性修复须防 **FK 列陷阱**——目标表有外键且父表运行期无数据时, 直插会整行失败, 须父表校验后 NULL 降级。本轮 1 修(设备台账无损)。数据完整性累计 3 修(层位展点/煤质导入/设备台账)。

## 一六三、导入列完整性透镜续二 —— 路况导入丢 condition(反讽) · 修 + CHECK/FK 双陷阱

精确核对 haul_road(schema 17 列, import 原 9 列):
- **★反讽缺口**: 命令名叫**「导入路况」**却**漏 `condition` 列**! ImportHaulRoads 原不解析 condition → 恒用 DEFAULT 'good' → 而路况列表查询(GeoDataQueries:324 SELECT ...condition FROM haul_road)显示的 condition **全是 good**(用户导入的 poor/fair/closed 全丢)。另漏 turning_radius_m(转弯半径)/max_load_t(最大载重)/pavement_type(路面)/maintenance_team/last_maintenance_date/notes(0 消费但 SQL 查询/无损可取)。
- **双约束陷阱**: ①`condition` 有 **CHECK(good/fair/poor/closed)** + NOT NULL DEFAULT 'good' → 解析后**校验枚举**, 有效才写、非法留默认(不整行失败); ②`primary_truck_model`→equipment_model **FK** → 父表校验后写(无损降级, 同设备台账 model)。
- **修**: 动态列表构建 INSERT/UPDATE, 无损全列 + condition 枚举校验 + truck_model FK 安全 + 命令 hint 补列。+2 单测(condition=poor/转弯/载重/路面 查回入库; 非法 condition='excellent'→回退 good 不失败)。1021 测全绿, 0 错, smoke [GLINIT] 正常。

**教训补**: 导入列完整性修复的**约束三防**——① FK 列父表校验后 NULL 降级 ② CHECK 列枚举校验后跳过留默认 ③ NOT NULL 列给默认。均"无损降级不整行失败"。数据完整性累计 **4 修**(层位展点/煤质/设备台账/路况)。本轮 1 修。

## 一六四、导入列完整性透镜续三(基本闭合) —— KPI 补 idle/delay+故障归因 · 修

核对末两候选:
- **★KPI 导入 ImportKpiMonthly 无损化 · 修**: 原解析 9 列, **漏 idle_hours(待机)/delay_hours(延误)/internal_fault_rate_pct(内部故障率)/external_fault_rate_pct(外部故障率)** 4 列——原 DataImportCenter(266/296/299)全导, 时间预算(计划=作业+故障+待机+延误)此前不闭合, 故障归因(内/外部)缺失。修: import 无损全 13 列; **加消费者** `GetKpiStats`+`KpiStats` 增 AvgInternalFaultPct/AvgExternalFaultPct(AVG(NULLIF(.,0)) 忽略无值), `KPI分析` 显示"故障归因 内X%/外Y%"(有数据才附加)。+1 单测(导入 idle60/delay20/内3.5/外1.5→查回入库 + 汇总呈现)。1022 测。
- **monthly_plan**: import 已覆盖 8/9 分析列(plan_strip/coal/outsource/ratio/distance/height + year/month), 仅漏 `team`(文本元数据, 0 消费, 且破坏纯数值 cols 模式)——**记录为可接受简化, 不补**(分析列全在)。
- production_record/fault_event/capacity_monthly/slope_design 前扫已基本齐。

**导入列完整性透镜基本闭合**: 8 张主导入表, **4 修**(煤质/设备台账/路况/KPI 无损全列 + 各加消费者或 SQL 可取)+ 4 基本齐(仅个别文本元数据可接受)。**约束三防**(FK 父校验/CHECK 枚举校验/NOT NULL 默认)全程无损降级不整行失败。本轮 1 修。数据完整性累计 **5 修**(层位展点/煤质/设备/路况/KPI)。

## 一六五、导出/导入对称审计 —— CSV 解析不支持引号(往返破损) · 修

对称检查导出侧后揪出真实往返 bug:
- **导出侧完整**: `ExportTableToCsv` = `SELECT *`(全列); 分析结果导出走反射 `RecordsToCsv`(自动含新加字段, 故 §一六一/一六四 给 SeamQualityRow/KpiStats 加的 Mad/FCd/故障率**自动进导出**, 无不对称); `CsvCell` 对含 `,"\n\r` 的值**加引号转义**。
- **★导入侧破损(真 bug)**: `ParseCsv` 原朴素 `Split(',','\t')` **不支持引号**——而导出的 CsvCell 会给含逗号的值(如故障描述"液压管破裂, 已更换"/备注)加引号。**导出→导入往返: 含逗号值被拆成多格, 数据错位**。且 `Split('\n')` 先切行→引号内换行也破。
- **修**: `ParseCsv` 换 RFC-4180 解析器 `SplitCsvRecords`(逐字符尊重双引号: 引号内 `,\t\n\r` 皆字面, `""` 转义引号; 引号外逗号/制表分列、换行分记录)。与导出 CsvCell 完全对称, 往返自洽。向后兼容(无引号 CSV 解析不变——现有 ParseCsv/往返测全过)。+3 单测(引号内逗号/`""`转义/引号内换行)。1025 测全绿, 0 错, smoke [GLINIT] 正常。

**判据**: 导入导出**对称性**——导出加引号(CsvCell)则导入必解引号(否则往返破); 凡自由文本列(描述/备注/名称)可能含分隔符, 朴素 split 必错。本轮 1 修(CSV 引号解析, 影响全部 11 导入)。数据完整性/健壮性累计 **6 修**。

## 一六六、数字解析 locale 健壮性 —— 导入侧 TryParse 统一 InvariantCulture(对称导出)

对称审计续: 导出侧全用 `ToString("0.###", InvariantCulture)`, 但导入侧 **19 处 `double/int.TryParse` 全用 culture 默认**(0 处 invariant)——**不对称**: 导出写 "0.9"(invariant), 逗号小数 locale(de-DE/fr 等)下导入 `TryParse("0.9")` 会失败或误解。CSV 数字是机器数据, 应恒 invariant。zh-CN/en 小数分隔亦 '.' 故当前默认 locale 可跑, 但属 latent 不对称 + 与导出不一致。
- **修**: `GeoDataQueries` 加 `ParseD/ParseI`(NumberStyles.Any/Integer + InvariantCulture), sed 替全部 19 处 `double.TryParse(`→`ParseD(` / `int.TryParse(`→`ParseI(`(helper 体用 System.Double/Int32.TryParse 不被替)。+1 单测(de-DE 逗号 locale 下导入 "600.5"/"0.9" 仍正确解析, 非 6005/失败; 连跑 2× 无 flake)。1026 测全绿, 0 错, smoke 正常。

**判据**: 导入导出 **locale 对称**——导出 invariant 则导入必 invariant(CSV/机器数据恒 invariant, 勿随 UI locale)。本轮 1 修(导入数字 invariant, 影响全部 DB 导入)。数据完整性/健壮性累计 **7 修**。

## 一六七、分析输出完整性透镜 —— 故障分析补可靠性 MTBF/MTTR/稳态可用率

新透镜: 分析命令是否算全**原版同分析的所有指标**? 查故障分析:
- **缺口**: 原 EquipmentAnalysisWindow §1.5 算 **MTBF/MTTR + 稳态可用率 A_ss + Weibull**(OEE 看板/故障 Pareto/MTBF), Kylin 故障分析只有 事件数/停机/未修/最多类型 + 排名, **无可靠性指标**。MTBF/MTTR 是标准可靠性分析且公式简单、数据齐(fault_event + production_record)。
- **修**: `GetFaultStats`+`FaultStats` 加 MtbfHours(=Σwork_hours/故障次数)/MttrHours(=Σduration_hours/故障次数)/SteadyAvailPct(=MTBF/(MTBF+MTTR)×100), 忠实原公式(line 774 "MTBF=总运行时长/故障次数"); `故障分析` 显示"可靠性 MTBF/MTTR/稳态可用率"(有运行时长才附加)。+1 单测(种子上验 MTTR=停机/次数、MTBF=Σworkhours/次数、A_ss=MTBF/(MTBF+MTTR) 关系恒等 + A_ss∈[0,100])。1027 测全绿, 0 错, smoke 正常。Weibull 浴盆/大修预警属 GUI 图表(受阻记录)。

**判据**: 分析输出完整性——同一分析原版算 N 指标, Kylin 算 M<N 即缺口(取公式简单+数据齐者补, 图表/GUI 受阻记录)。同煤质washability/KPI故障归因手法。本轮 1 修。累计分析补全 3 处(煤质 Mad/FCd·KPI 故障归因·故障 MTBF/MTTR)。

## 一六八、分析输出完整性续 —— KPI 补 OEE(设备综合效率)+ 作业率

承 §一六七, 续查 KPI 分析:
- **缺口**: 原 `CsvDataStore.Oee = Availability × ActualRunRate × UtilizationRate`(设备综合效率, "OEE 三率卡片"), Kylin `GetKpiStats` 只有可用率/利用率均值, **无 OEE, 且未暴露作业率(actual_run_rate 已导入却不显示)**。
- **修**: `GetKpiStats`+`KpiStats` 加 AvgRunRatePct(作业率均值)+ OeePct(=可用率×作业率×利用率, 由归一化三率百分比求积, 忠实原 Oee); `KPI分析` 显示"三率 可用/作业/利用 · OEE"。+1 单测(OEE=三率积关系恒等 + OEE≤各单率 + ∈[0,100] + 作业率>0)。1028 测全绿, 0 错, smoke 正常。

**分析输出完整性透镜产出**: 累计补 4 处(煤质 Mad/FCd · KPI 故障归因 · 故障 MTBF/MTTR · KPI OEE+作业率)。判据: 原版同分析算的指标(公式简单+数据齐)Kylin 须算全; 图表/GUI(Weibull 浴盆/趋势图/Pareto 图)受阻记录。本轮 1 修。

## 一六九、分析输出完整性续二 —— 生产数据补台效(产量/工时)均值+峰值

承 §一六七/一六八, 续查生产数据分析:
- **缺口**: 原 EquipmentAnalysisWindow 算 **peakEff = max(output/work_hours)**(班次台效峰值), Kylin `GetProductionStats` 有总产量+总工时但**无台效(产量/工时=m³/h)**——设备生产率核心指标缺失。
- **修**: `GetProductionStats`+`ProductionStats` 加 AvgEfficiencyM3PerH(=总产量/总工时)+ PeakEfficiencyM3PerH(=逐记录 MAX(output_m3/work_hours), 忠实原 peakEff); `设备生产数据` 显示"台效 均X/峰Y m³/h"。+1 单测(均值=总产量/总工时 恒等; 峰值≥均值[比率加权均值≤最大比率])。1029 测全绿, 0 错, smoke 正常。

**分析输出完整性透镜累计 5 处**(煤质 Mad/FCd · KPI 故障归因 · 故障 MTBF/MTTR · KPI OEE+作业率 · 生产台效均/峰)。产能峰值年/当前占峰比属 EquipmentCapabilityWindow GUI 图表(趋势图)——指标可算但主载体是图表, 命令行已有年度产量导出可支撑, 暂记录(如需可另补文本峰值年摘要)。本轮 1 修。

## 一七〇、分析输出完整性续三 —— 煤质统计补灰分变异系数(均匀性评价)

承 §一六七–一六九, 续查煤质统计:
- **缺口**: 原 `CoalQualityAnalytics`(483-491)算**灰分变异系数 CV=σ/均值×100**(样本 σ, n-1)+ 均匀性评价(<15 均匀煤质稳定 / <30 较均匀 / else 波动大须注意配采均衡), Kylin `GetCoalQualityStats` 只有均值无变异性度量。
- **修**: `GetCoalQualityStats`+`CoalQualityStats` 加 AshCvPct(SQL 取 SUM(ad²)→代码算样本方差 (Σx²−n·μ²)/(n−1))+ AshUniformity(评价档, 忠实原); `煤质统计` 显示"灰分CV X%(评价)"。+1 单测(独立取 ad_raw 算样本 CV 对拍 + 均匀性档一致)。1030 测全绿, 0 错, smoke 正常。

**分析输出完整性透镜累计 6 处**(煤质 Mad/FCd · KPI 故障归因 · 故障 MTBF/MTTR · KPI OEE · 生产台效 · 煤质灰分 CV/均匀性)。本轮 1 修。数据面(7)+分析输出(6)=**13 处数据/分析补全**, 均"原版有、公式简单、数据齐"且以关系恒等/独立对拍验证。

## 一七一、分析输出完整性续四 —— 年度产量补峰值年/占峰比/同比

承 §一六七–一七〇, 续查年度产量趋势:
- **缺口**: 原 EquipmentCapabilityWindow 算**峰值年产量 + 当前年占峰比%**(236-244), Kylin `AnnualOutputCmd` 只列各年产量+累计, 无峰值/趋势度量。
- **修**: 抽纯helper `GeoDataQueries.SummarizeAnnual(rows)`→`AnnualOutputSummary`(峰值年/峰值产量/最新年/最新占峰比%/同比%); `年度产量趋势` 显示"峰值 X年Y万m³ · Z年为峰值 R% · 同比 ±G%"。抽 helper 便于纯单测(不依赖 DB)。+2 单测(峰值/占峰比/同比恒等: 2020:100/2021:150/2022:120→峰2021、占峰80%、同比−20%; 空→0)。1032 测全绿, 0 错, smoke 正常。

**分析输出完整性透镜累计 7 处**(煤质 Mad/FCd · KPI 故障归因 · 故障 MTBF/MTTR · KPI OEE · 生产台效 · 煤质灰分CV · 年度峰值/占峰/同比)。**教训**: 分析派生指标应抽**纯 helper**(Data 层)而非塞 View 命令内, 便于关系恒等单测。本轮 1 修。数据面(7)+分析输出(7)=**14 处补全**。

## 一七二、分析输出完整性续五 —— 故障类型补帕累托累计占比(80/20)+ 煤厚变异证伪

- **★故障类型 Pareto · 修**: 原 `FaultService.GetPareto`→FaultParetoEntry 是帕累托(按停机降序), 图表算累计%。Kylin GetFaultByType 有停机占比无**累计占比**(帕累托定义特征)。补 `FaultTypeRow.CumulativeSharePct`(降序前缀和)+ `故障类型分布` 显示"前 N/M 类占 80% 停机"(帕累托 80/20 洞察)。+1 单测(累计=前缀和 + 单调不减 + 降序 + 末=100%)。1033 测全绿。
- **煤厚变异系数 · 证伪不加**: 查原版**无**煤层厚度变异系数/煤层稳定性分析("稳定性"命中皆设备 CV 非煤层)。GetSeamIntersections 有均厚无变异——但原版也无, 加即**发明**→按忠实性**跳过不加**(见 [[faithfulness-only-original-commands]])。

**分析输出完整性透镜累计 8 处**(煤质 Mad/FCd · KPI 故障归因 · 故障 MTBF/MTTR · KPI OEE · 生产台效 · 煤质灰分CV · 年度峰值/占峰/同比 · 故障 Pareto 累计)。判据双向: 原版有+公式简单+数据齐→补; 原版无→证伪跳过(勿发明变异系数)。本轮 1 修 + 1 证伪。数据面(7)+分析输出(8)=**15 处补全**。

## 一七三、分析输出完整性续六 —— KPI 趋势补作业率(三率齐)+ 验收确认完整

- **KPI 趋势补作业率**: `KpiTrendRow` 原仅可用率/利用率(2/3 率), 原版三率 trend 图含作业率。补 AvgRunRatePct(与 §一六八 GetKpiStats OEE 三率一致), `设备KPI趋势` 显示三率。+1 单测(作业率归一化∈[0,100] + 种子有值 + 按年升序)。1034 测。
- **现场验收 确认完整**: `AcceptanceStats` 有 记录数/合格率/平均绝对偏差/按状态分布 + `GetAcceptanceByPhase`(分工序)——读侧完整; 录入实测+偏差报警是 ParameterAcceptanceWindow GUI(受阻)。无缺口。

**分析输出完整性透镜累计 9 处**(+KPI 趋势作业率)。**收敛信号**: 本轮 1 小一致性补 + 1 确认完整 + 上轮 1 证伪(煤厚变异原版无); 真缺口在收窄, 渐多"已全/原版无/GUI 受阻"。数据面(7)+分析输出(9)=**16 处补全**。本轮 1 修 + 1 确认。

## 一七四、分析输出完整性续七 —— 班次产量补台效(班次生产率对比)

- **班次产量补台效**: `ShiftOutputRow` 原有产量/工时/作业率无台效, 同 §一六九 生产统计。补 EfficiencyM3PerH(=产量/工时/班次), `班次产量对比` 显示"台效 X m³/h"——供班次生产率横比(哪班效率高)。+1 单测(台效=产量/工时/班次 恒等)。1035 测全绿, 0 错, smoke 正常。

**分析输出完整性透镜累计 10 处**(煤质 Mad/FCd·KPI 故障归因·故障 MTBF/MTTR·KPI OEE·生产台效·煤质灰分CV·年度峰值/占峰/同比·故障 Pareto·KPI 趋势作业率·班次台效)。数据面(7)+分析输出(10)=**17 处补全**。透镜渐收敛(多为一致性补/确认完整/证伪原版无)。本轮 1 修。

## 一七五、分析输出完整性续八(收敛) —— 煤种分布(煤类饼量化)+ 余分析确认完整

- **煤种分布 · 补**: 原「煤类饼」(CoalQualityDashboardWindow, GUI 饼图)展样本按煤种分布。Kylin 煤种分类只展**定义表**(代码/名称/Vdaf 区间)无实际分布。补 `GetCoalTypeDistribution`(coal_sample 按 coal_type 分组计数+占比降序, 种子 coal_type 已填)+ `煤种分类` 附"样本分布 气煤X(Y%)/…"(饼图的文本量化, 同 Pareto 先例)。+1 单测(降序+占比和100+总数=coal_sample 计数)。1036 测。
- **余分析确认完整**: 产能分类(含占比 SharePct)/煤层(代码/名称/样本数)/煤种定义(Vdaf 区间)/见煤统计(孔数/均厚/尖灭)——读侧完整; 现场验收(合格率/偏差/按状态/分工序)完整。缺者皆 GUI 图表(煤类饼原载体/趋势图/Weibull/Pareto图)或原版无(煤厚变异)。

**★分析输出完整性透镜收敛**: 累计 **11 处补全**(煤质Mad/FCd·KPI故障归因·故障MTBF/MTTR·KPI OEE·生产台效·煤质灰分CV·年度峰值/占峰/同比·故障Pareto·KPI趋势作业率·班次台效·煤种分布)。终检余分析皆已全/GUI图表受阻/原版无。数据面(7)+分析输出(11)=**18 处补全**。本轮 1 补 + 收敛确认。

## 一七六、输出完整性透镜扩到 CAD —— 网格诊断补孤立点/重复点

分析输出完整性透镜从 §四/§八 DB 分析**扩到 CAD 子系统**:
- **网格诊断 MeshDiagnose 补 2 项**: 原 DIAGNOSE 查 6 项(开放边/非流形/退化/**自相交**/**孤立点**/**重复点**), Kylin 原覆盖开放边/非流形/退化/洞/闭合, **漏孤立点+重复点+自相交**。补 `IsolatedVertices`(无三角引用的顶点)+`DuplicateVertices`(坐标 tol 量化重合), `网格诊断` 附显示。**自相交**属三角-三角求交鲁棒难题(原版本身 FixKind.None 无自动修)→ 记录受阻。+2 单测(孤立点/重复点各 1)。1038 测全绿, 0 错, smoke 正常。
- **点云统计**: 原 pc_quality 走 native PMQS 二进制, 无托管参照可对拍; Kylin PointCloudStats(计数/密度/包围盒/均值·σ)managed 重实现合理, 记录(native 参照不可验)。

**输出完整性透镜再产出**(CAD 网格诊断 2 项)——判据同 DB 分析: 原版查 N 项、Kylin M<N、可做者补(孤立/重复点)、鲁棒/native 受阻记录(自相交/点云 PMQS)。本轮 1 修 + 1 记录。累计输出完整性 12 处(11 DB + 1 CAD 含 2 项)。

## 一七七、受阻边界再评估——Weibull 失效分布可做(非图表)· 补

复评"记录受阻"的 Weibull(§一六七 只补 MTBF/MTTR 时记为待评): 查原 EquipmentAnalysisWindow §1.5 —— Weibull 是**中位秩回归拟合故障间隔天数得 β/η + 浴盆阶段文本**(非图表!浴盆卡是文字建议)。故**可做可验**, 非受阻。
- **修**: 新 `src/Cad/Reliability.cs`——`WeibullFit`(Bernard 中位秩 F=(i−0.3)/(n+0.4) 线性化 ln(−ln(1−F))=β·ln t−β·ln η 最小二乘)+ `Phase`(β<0.85 早期/≤1.15 随机/>1.15 损耗, 忠实原阈值)+ `PooledIntervalsDays`(逐设备故障日期相邻间隔池化, 跨设备不串)。`GetFaultStats` 按 equipment_id+date 取序、池化间隔、拟合, `FaultStats` 加 WeibullBeta/Eta/Phase; `故障分析` 显示"Weibull β/η/浴盆阶段"。
- **验证**: +4 单测(已知 β=2.5/η=120 分位数据精确还原 β/η; n<3/全相等/空→ok=false; 浴盆三档阈值; 池化只取设备内间隔)。1042 测全绿, 0 错, smoke 正常。浴盆**曲线图**仍属图表受阻; β/η/阶段(数值+文字)已补。

**教训**: "记录受阻"项要复评**载体**——原以为 Weibull 是图表(受阻), 实则 β/η/阶段是**数值+文字卡**(可做)。同 MTBF/OEE: 分析的**数值指标**可做, 仅**图形载体**受阻。本轮 1 补(受阻转可做)。见 [[unlock-blocked-insights]]。

## 一七八、受阻边界再评估续 —— 设备五维综合评分(熵权法)· 补

续"受阻分析看载体"透镜: 原 EquipmentShiftForecastWindow §2.5「客观赋权」设备五维综合评分——**评分/排名是数值(可做), 仅雷达图受阻**。
- **实现**: 新 `src/Cad/EntropyWeighting.cs`——`Weights`(熵权法: kEnt=1/ln(m), pᵢⱼ=xᵢⱼ/Σ, eⱼ=−kEnt·Σp·ln p, wⱼ=(1−eⱼ)/Σ, 逐字忠实原 EntropyWeights; m<3 等权)+ `Composite`(加权和)。`GetEquipmentScores`: 逐设备五维[产能强度=均产/峰产·稳定性=1−CV·可用率·效率=作业率·可靠性=max(0,1−0.08·故障数)](忠实原维度定义)→ 熵权 → 综合得分降序。新命令「设备综合评分」显示前 8 名五维分。
- **验证**: +4 单测(熵权和=1 + 离散维权重>均匀维 + m<3 等权 + Composite 加权和; 设备评分降序 + 五维归一[0,1] + 综合≤1)。**双查**无既有评分命令(方案综合对比是方案不是设备)。1046 测全绿, 0 错, smoke 正常。雷达图受阻记录。

**受阻复评透镜连续产出**: Weibull(§一七七)+ 设备综合评分(本轮)——皆"原以为图表受阻, 实则数值内核可做"。**判据固化**: 受阻的分析/仪表盘项, 先拆"数值指标(可做)vs 图形载体(受阻)"; 多数含可做数值核。本轮 1 补(受阻转可做, 熵权 MCDM 算法)。见 [[unlock-blocked-insights]]。

## 一七九、煤质数据健康度(仪表盘数值核)+ 煤质导入 coal_type FK 陷阱修

- **煤质数据健康度 · 补**(续受阻仪表盘复评): 原 CoalQualityDashboardWindow「数据健康度」的数值核可做。`GetCoalDataHealth`——样品总数 / 煤类标注率(coal_type 非空%) / 化验孔覆盖(distinct borehole/总孔%) / **工分自洽率**(M+A+V+FC≈100±3, 仅四项齐全样本; 因 §一六一 补 Mad/FCd 导入才可算)。新命令「煤质数据健康度」。
- **★煤质导入 coal_type FK 陷阱 · 真 bug 修**: `coal_sample.coal_type` 有**外键→coal_classification(code)**。`ImportCoalSamples` 原直插 coal_type(Txt), 用户填**煤种中文名/未知码**(如"气煤"非 code)→ **整行 FK 失败, 丢整条化验**! 修: `FkTxt` 父表校验, 非有效码置 NULL(无损降级)。同设备 model / 路况 truck_model / 边坡 side_type 的约束三防。测试即由此暴露(气煤 err=1)。
- **验证**: +1 单测(导入 M+A+V+FC=100 样本[coal_type=气煤经 FK-safe→NULL 但样本入库]→ 自洽率含之 + 标注率/孔覆盖对拍)。1047 测全绿, 0 错, smoke 正常。

**教训**: 导入的 FK 陷阱要**逐 FK 列查**——coal_sample 有 3 个 FK(borehole_id/seam_code/coal_type), coal_type 此前漏做 FK-safe(前几轮只修了 equipment/haul_road 的 FK)。**凡 import 写带 FK 的表, 每个 FK 列都要父表校验降级**。本轮 1 补(数据健康度)+ 1 真 bug 修(coal_type FK)。

## 一八〇、导入 FK 陷阱系统审计闭环 —— 可空 FK 全 FK-safe, NOT NULL 键正确失败

承 §一七九 coal_type 遗漏, 系统枚举各可导入表全部 FK 列 + 可空性:
- **可空 FK 元数据列(4, 全已 FK-safe 降级)**: coal_sample.coal_type→coal_classification / equipment.model→equipment_model / equipment.operating_area→mine_location / haul_road.primary_truck_model→equipment_model。非有效码置 NULL, 不丢整行。
- **NOT NULL FK 键(正确失败, 不可降级)**: coal_sample/coal_observation_point/borehole_seam_result 的 **seam_code**→coal_seam_def(NOT NULL); equipment_kpi_monthly/production_record/fault_event/capacity_monthly 的 **equipment_id**→equipment(NOT NULL); coal_sample.borehole_id(import 由 hole_id 查找, 查不到 skip)。这些是必填键——引用不存在的父(煤层/设备)本就不该插入, 整行失败(err++)是**正确引用完整性**(不可 NULL 降级, 违 NOT NULL)。
- monthly_plan/slope_design: 无 FK。

**FK 陷阱审计闭环判据**: import 写带 FK 表, 逐 FK 列查可空性——**可空**列→父校验 NULL 降级(免丢整行元数据); **NOT NULL 键**→失败即正确(引用完整性)。全部覆盖, 无遗漏。本轮 0 修(审计确认闭环), 承 §一七九 修的 coal_type 是最后一个可空 FK 遗漏。

## 一八一、受阻复评续 —— 大修预警(可用率趋势+稳态)· 补 · §1.5/2.7 可靠性套件闭合

续受阻复评: 原 §2.7 大修预警是**数值判据(非图)**——可用率月度趋势斜率 + 最新可用率 + 稳态可用率, `warn = slope<−0.002 或 latest<0.80 或 A_ss<0.85`(忠实原阈值)。
- **修**: `GetFaultStats` 加可用率月度序列(equipment_kpi_monthly 按年月 AVG, 归一 0..1)线性回归斜率 + 最新 + 复用 A_ss → `OverhaulWarn`/`AvailTrendPtPerMonth`/`LatestAvailPct`; `故障分析` 显示"🔴大修预警/寿命良好(可用率X%·趋势±Ypt/月)"。+1 单测(warn 与阈值条件恒等)。1048 测全绿, 0 错, smoke 正常。
- **★§1.5/2.7 可靠性套件闭合**: 故障分析现含 MTBF/MTTR/稳态可用率(§一六七)+ Weibull β/η/浴盆阶段(§一七七)+ 大修预警(本轮)——原 EquipmentAnalysisWindow 可靠性节的**全部数值核已补齐**, 仅浴盆曲线图/趋势图受阻记录。

**受阻复评透镜累计 4 大补**(Weibull/熵权设备评分/煤质数据健康度/大修预警)——皆"图表受阻但数值核可做"。设备可靠性分析域的数值指标现完整。本轮 1 补。

## 一八二、受阻复评续 —— 机群领导驾驶舱数值核(健康度/瓶颈/可解锁/需关注)

续受阻复评: 原 EquipmentFleetCockpitWindow「领导驾驶舱」的**一屏 GUI 受阻, 但数值核全可做**(KPI+产能+役龄驱动, 无需调度引擎)。Kylin 原 机群总览 仅计数(总数/按状态/按型号)。
- **修**: `GetFleetCockpit`——逐设备 KPI 三率+役龄+产能, 忠实原公式: 健康灯(A<0.80 或 役≥20且A<0.85=🔴; A<0.86 或 役≥15=🟡; 否则🟢)+ 平均OEE + **可解锁产能**(theo=cap/oee, 补最短板 maxLoss=max(theo(1−A), theo·A(1−R), theo·A·R(1−U)), Σ)+ **瓶颈**(可用率达标率最低类别)+ **需关注清单**(黄/红设备+短板诊断)。新命令「机群驾驶舱」。
- **验证**: +1 单测(红绿灯划分完备 绿+黄+红=在评数 + 需关注=黄+红 + OEE/瓶颈达标率∈[0,100] + 可解锁≥0)。1049 测全绿, 0 错, smoke 正常。一屏图形布局受阻记录。

**受阻复评透镜累计 5 大补**(Weibull/熵权设备评分/煤质数据健康度/大修预警/机群驾驶舱)——设备管理分析域(可靠性+机群+评分)数值核现完整。判据: 受阻仪表盘/驾驶舱先拆数值核(可做)vs 图形布局(受阻)。本轮 1 补。

## 一八三、受阻复评透镜收敛 —— 风险等级(冗余+date-fragile 记录)/化验段分布(已覆盖)

续查设备管理域剩余仪表盘数值核, 两候选皆判**不新增**:
- **班次预测风险等级**(EquipmentShiftForecastWindow): `riskScore=CV×50+faultPerMonth×8`, <15低/<35中/else高。**faultPerMonth=近30天故障数(DateTime.Now 相对)**——date-fragile(历史种子近月故障≈0→退化为纯 CV 分档), 单测不可确定性验证(违"不满足验证先记录")。且 CV 风险已由**综合评分稳定性(1−CV, §一七八)+ 驾驶舱健康灯(可用率+役龄, §一八二)+ 大修预警(可用率趋势, §一八一)**三重覆盖。→ **记录: 冗余 + date-fragile, 不新增**(避免重复度量, 见 [[faithfulness-only-original-commands]])。
- **化验段分布**(煤质看板"按煤层化验段数量"): 已由 `GetCoalQualityBySeam.Samples`(逐煤层样本数)覆盖。→ 已有。

**★受阻复评透镜收敛**: 设备管理分析域(可靠性/机群/评分/数据质量)数值核已完整补齐 **5 大项**(Weibull/熵权评分/数据健康度/大修预警/机群驾驶舱); 剩余仪表盘项皆图形布局受阻 / 已覆盖 / 冗余·date-fragile。判据: 受阻分析先拆数值核(补)vs 图形(受阻)vs 冗余/不可验(记录)。本轮 0 补 + 2 复评记录(收敛信号)。

## 一八四、受阻复评续 —— 灰分纵向趋势(补) + 情景对比(空桩跳过)

系统扫原版 12 个分析/看板窗口, 余项复评:
- **情景对比 EquipmentScenarioCompareWindow**: 文件**空(原始空桩)**→ 无数值核可移, 跳过。
- **灰分纵向趋势 · 补**(CoalQualityBoreholeColumnWindow): 数值核=按样品高程降序取浅/深三分位灰分均值, d=深−浅; |d|<3 稳定 / d>0 向深部增高 / d<0 向浅部增高。`GetAshVerticalTrend`(忠实原逻辑+阈值 3), `煤质统计` 附"灰分向深部增高(浅X→深Y%)"。+1 单测(导入高程样本→标签与 d 一致 + d=深−浅)。1050 测。
- 数据管理窗(CRUD grid)/空间分布(EstimateGradeAsync 已覆盖 §一五三)/工序分期(TaskLib 引擎阻): 记录。

**受阻复评透镜近收敛**: 原 12 分析窗口数值核已系统覆盖——设备域 5 大补(可靠性/机群/评分)+煤质域(均值/CV/数据健康/煤类分布/纵向趋势)+产量域(峰值/占峰/同比)+故障 Pareto; 余皆 GUI 图形/CRUD/引擎/空桩/已覆盖/冗余。本轮 1 补 + 1 空桩跳过。

## 一八五、C2C 位移监测补 |位移|P95 —— 分析数值核透镜跨模块收敛

复评 PointCloudLib C2C(两期点云位移监测)——Kylin CloudCompareAsync **已基本完整**(逐点最近距离=|位移| + Statistics.Describe 全分布[min/max/mean/std/四分位] + 20 桶直方图 CSV + 蓝→红位移着色)。唯缺原版强调的**|位移| P95**(边坡变形监测判据: 95% 点位移小于此)。
- **修**: C2C 显示补 `Statistics.Percentile(sorted, 95)` = |位移|P95(复用已测 Percentile)。1050 测(P95 走已测函数, 无新测)。
- 差异记录: 原 C2C 位移带**符号**(沉降/抬升, 需法向/Z 投影), Kylin 为无符号最近距离(|位移|); 原排除不重叠点(未匹配), Kylin 含全点。均语义/架构差异, 同范围测量影响小, 记录。

**★分析数值核透镜跨模块收敛**: GeoDataBase 12 分析窗口 + PointCloudLib(C2C/点云统计[native PMQS 记录])+ MeshEditLib(网格诊断补孤立/重复点§一七六 · 度量[内核记录])——各模块分析的**数值核**已系统覆盖/补齐/记录边界。本轮 1 小补(P95)。

## 一八六、新命令可发现性 —— 4 新命令补入命令目录(自动补全)

本会话新增/复活的分析命令均**可派发但不在命令目录 CommandCatalog**(自动补全候选源)→ 用户命令行输入无提示、难发现。补入目录:
- **设备综合评分**(§一七八 熵权五维) · **机群驾驶舱**(§一八二 领导驾驶舱) · **煤质数据健康度**(§一七九) · **分煤层煤质**(§一六一, 早存但漏登目录)。
- 均已派发(cmd== 命中)且入目录 → 命令行前缀补全可提示。1050 测全绿, 0 错, smoke 正常。
- 注: CommandCatalog 是 View private + 需 Avalonia UI 上下文, 无易测点(原「207/207 解析」是脚本非单测); 本次靠 grep 目录 vs 派发差集人工核。

**教训**: 加新命令(dispatch `cmd == "X"`)后, 若是**主命令(非别名)**须同步登 `CommandCatalog`, 否则不可发现。加命令三步: ①dispatch ②handler ③CommandCatalog(主命令)。本轮补 4 命令入目录。

## 一八七、新命令 Ribbon 可发现性 —— 3 新命令入功能区(命令行+目录+Ribbon 三路齐)

承 §一八六 命令目录, 续补 Ribbon 功能区(主 UI 发现路径, 其它分析命令皆有 ribbon 按钮):
- **设备管理组**: 补「机群驾驶舱」(equip_cockpit 图标)+「设备综合评分」(equip_capability 图标)——与既有 机群总览/设备智能编组/设备数据分析/设备效能预测 并列。
- **煤质管理组**: 补「数据健康度」(coal_data 图标)——与既有 数据看板/统计分析/钻孔柱状图 并列。
- 复用既有图标资源(equip_cockpit/equip_capability/coal_data, 已验证存在), tooltip 说明。0 错, 1050 测, smoke [GLINIT] 无 XAML/资源错。

**★可发现性三路闭合**: 新增主命令现三路齐全——① 命令行 dispatch ② CommandCatalog 自动补全 ③ Ribbon 功能区按钮。**加主命令四步**: dispatch + handler + CommandCatalog + Ribbon 按钮(有图标)。承你的右键菜单反馈, 本会话补齐了新命令的 UI 可达性(右键置顶 + 目录 + ribbon)。本轮 3 命令入 ribbon。

## 一八八、Home 功能区图标补齐 —— 16 按钮缺图标补全(用户反馈)

用户反馈"home部分的图标还不完整"(截图: 撤销/重做/标注/测量/剪贴板/选择等只有文字无图标)。查出 **16 个 ribbon 按钮有 TextBlock 无 Image**, 而对应图标**在 IconDict.axaml 中早已定义、只是没接**:
- 修改组: 撤销 icon_undo / 重做 icon_redo
- 注释组: 对齐标注 icon_linear_dimension / 半径标注 icon_radial_dimension / 连续标注 icon_continuous_dimension
- 测量组: 距离 icon_measure / 面积 icon_measure_area / 角度 icon_measure_angle
- 剪贴板组: 剪切 icon_cut / 复制 icon_copy / 粘贴 icon_paste / 基点粘贴 icon_paste_base
- 图层组: 全开 icon_show_all
- 选择组: 全部选择 icon_select_all / 最后 icon_select_last / 上次 icon_select_previous
- 全部复用既有 DrawingImage 资源(16 key 均已确认定义于 IconDict.axaml)。build 0 错; 复扫剩余无图标 ribbon 按钮 = **0**。

**教训**: ribbon 按钮加了 Tag+Click 但漏 `<Image Source>` → 只显文字。图标资源齐全但没接线。加 ribbon 按钮须含 Image(图标)。按用户"后续集中测试"要求, 本轮 XAML 纯图标接线(无逻辑改, 不影响 1050 单测), build 验证编译 + 图标 key 存在性核验 + 零遗留扫描。本轮补 16 图标。

## 一八九、Ribbon 完整性核对 —— 无死按钮(确认) + tooltip 缺 46(冗余记录)

承图标补齐(§一八八), 核对 ribbon 其余完整性:
- **死按钮: 无(确认)**。曾用 bash 循环 `grep "cmd == \"$t\""` 查得 231「死按钮候选」——**假阳性**(中文+嵌套引号转义失败, 连 保存/打开/复制/删除 等核心命令都误报)。用 Grep 工具正核: 保存(§745)/机群驾驶舱(§859)/设备综合评分(§816)/采区划分(§882)等**均派发**。240 ribbon 按钮全经 OnRibbonCommand→中文 if 链派发, 无死按钮。**教训: 别用 bash 循环 grep 中文+引号(转义脆), 用 Grep 工具**。
- **tooltip: 46/240 缺, 记录不补**。缺 tooltip 的皆**带可见文字**的基础按钮(点/直线/复制/撤销/距离…), 文字即功能, hover tooltip 冗余(非图标那种可见缺口); 加 46 冗余 tooltip 属低值 churn + 编辑风险。按"只考虑功能"记录为次要外观项, 不补。

**UI 完整性小结**: 图标(§一八八 补 16, 0 遗留) + 派发(无死按钮) 已完整; tooltip(冗余)记录。本轮 0 补(1 假阳性纠正 + 1 冗余记录)。

## 一九〇、受阻功能按钮诚实提示 —— 兜底回显从"命令:X"改为"暂未实现"

承 UI 完整性: Python 精确核对(避 bash 中文引号坑)得 **57 个 ribbon 按钮未派发**(排除 167 已派发 + 5 绘图工具), 皆**未移植子系统的受阻功能**(排产计划 PlanLib / 生产调度 TaskLib / 坑线采剥 MineAssLib 内核 / 倾斜摄影 native)——按 memory「映射到已实现功能的按钮均已接」, 这 57 对应未实现功能, 正确未接。
- **问题**: 点这些按钮兜底回显旧的 `"命令: {cmd}"`——看着像"执行了"实则无操作, 迷惑用户。
- **修**: 兜底改为诚实提示 `「{cmd}」暂未实现——属未移植子系统（排产计划/生产调度/坑线采剥内核/倾斜摄影等），或命令名有误`。点受阻按钮/打错命令均得清晰反馈。build 0 错(纯状态串改, 不涉逻辑/单测)。

**教训**: 受阻功能的 UI 入口(按钮)不应静默/模糊, 应明确告知"未实现+所属受阻域"(诚实 UX, 区别于假装可用)。57 受阻按钮清单经 Python 核实(bash 循环 grep 中文假阳性教训见 §一八九)。本轮 1 UX 修(兜底诚实提示)。

## 一九一、57 受阻按钮抽查复核 —— 确认引擎/native/规划域(无漏可做子集)

按 unlock-blocked 纪律复查 §一九〇 的 57 未派发按钮有无"可做几何/CAD 子集"(如早前 剔面/虚拟钻孔 的解锁), 抽查最可能可做的 2 个, 皆**确认受阻**:
- **转化为三角格网**: 属倾斜摄影工作流(加载倾斜摄影→转化为三角格网→隐藏/删除, MeshEditLib CreateObliqueTinCommand), 依赖 **native OSGB** 加载的倾斜实景数据 → 受阻(倾斜摄影 native 不可移植)。
- **煤层露头着色**: 用 SeamOutcropBandExtractor 出上下沿"带"喂 DumpStripDialog(排土条带规划, BlockModelLib) → 块体模型 + 排土规划引擎域 → 受阻。
- 其余 55 经名称归类: 排产计划(PlanLib 中长远/短期/月/周计划·动态模拟·出图)/生产调度(TaskLib 任务编制·派工·派车单·实绩·报告·推演)/坑线采剥(MineAssLib 坑线·刀量·采剥接续·工作线)/台阶排土(放坡·模板·并段·扩帮)/native(倾斜摄影·影像底图)/无种子表(班次日历·检修档期)/配置对话框(破碎站·约束·编制配置)——**皆已记录硬边界**, 无非引擎的可做几何子集。

**结论**: 57 受阻按钮 = 未移植子系统(引擎/native/规划/配置对话框)的 UI 入口, 正确未接线 + 兜底诚实提示(§一九〇)。抽查 2/2 确认受阻, 与 memory 既有审计一致(可做者早已接)。本轮 0 补(抽查复核确认受阻)。见 [[unlock-blocked-insights]]。

## 一九二、逐模块系统性 diff —— 竖曲线平滑(补真功能, 拓扑透镜之外的新增)

**方法(新透镜)**: 收敛感之后, 不再凭主题透镜自证, 改**枚举原版全部 9 模块 + 英文类/方法名 diff Kylin 覆盖**(中文串因原版 GBK 编码乱码, 改用英文标识符规避)。逐一核对:
- **MeshEditLib(382)**: Smooth/Simplify/Weld/Orient/PlaneSection/HoleFill/Repair/Intersect/BoundaryLoops… → Kylin 全有(MeshSmooth/Simplify/Weld/Orient/PlaneSection/HoleFill/Repair.cs + 派发)。**已覆盖(证实非空想)**。
- **PointCloudLib(157)**: Decimate/GroundFilter/两期体积/VolumeToBase/RoadCenterline/Obstacle/Profile → Kylin 全有(GroundFilter.cs·两期算量·体积计算·提取道路中线·剔除障碍)。**已覆盖**。
- **RoadLib(271)**: 多为**交互式中线管理**(视口拾取 CenterlinePick/SplitCenterline)→ 受阻; 演化对比/中线管理已接(交互部分诚实标注)。
- **BlockModelLib/GeoDataBase/SqlLib**: 估值/克里金/等值线/体素 · 本会话 §四/§八 分析 · SQL查询 —— 均已覆盖。
- **MineAssLib(285)**: 大量排产/坑线/台阶**引擎**(RoadLayoutSolver·UnitPlanEngine·MonthlyMineScheduler·native 坑线内核)→ 受阻; 但混有**自足计算切片**。

**新增真功能 —— 竖曲线平滑(纵断面, GBJ22-87 阶段③)**:
- 源: `MineAssLib/RoadLayout/ProfileSmoother.VerticalCurves` —— 原版自注"**纯几何、(弧长 s,标高 z)域、可单测**"。相邻纵坡代数差 > trigger 的变坡点插抛物线竖曲线 Lv=R_v·|Δi|(段长放不下 clamp, 半径降低计 violation); 抛物线 z=zb+g1·x+(g2−g1)/(2·span)·x², BVC/EVC 与两侧直坡同点→端点标高自动保持。
- 载体: `src/Cad/RoadVerticalCurve.cs`(忠实逐行移植) + 命令 `竖曲线平滑`(3D 中线 CSV→累计 XY 弧长为 s→平滑→原/平滑纵断面入场景[灰/青, X=里程 Y=标高]+竖曲线数/最小半径/不达标汇总), 三路可发现(命令行别名/命令目录/RoadLib Ribbon 按钮 + 专属图标 `mineass_vertical_curve`)。
- **非冗余确认**: Kylin 既有 `纵坡分析`只**读取着色**现状纵坡; 竖曲线平滑**设计/修改**纵断面(插抛物线), 互补。
- 验证: `RoadVerticalCurveTests` 10 例(端点保持·BVC=5.1·EVC=5.1·抛物线顶点=7.55·逐点抛物线公式·半径 clamp 490·触发阈·n<3 直通·恒定坡不设线) —— 全绿。**build 0 错 · 单测 1050→1060**。

**评估后跳过(记录, 免冗余/边际)**:
- **RoadCutFillCalculator(路面挖填 RS17)**: 算法可移植(路面网格栅格化逐格 z_road−z_ground 积分, footprint/uncovered 分列——与我 grade MaskByRadius 同"不出数据支撑外造值"纪律), 但**核心数学与 Kylin 两期算量重叠**(网格+双面+带符号 dz), 且原版唯一调用者用 **native 坑线内核**产出 deck 网格 → 独立命令价值边际。**跳过免冗余**(采区划分回退教训)。
- **VolumeDeviation(方量偏差)**: est vs actual 单元方量对账, 输入为**受阻计划引擎**(UnitPlanEngine)单元, 计算本身琐碎 → 边际, 记录。

**9 模块 diff 收官(TaskLib/PlanLib 补扫)**: TaskLib(82)/PlanLib(68) 多为排产/调度/仿真**引擎**(Sim*·*ScheduleResult·Zone*·MonthlyStrip*); 自足切片(TaskQuantity 生产量核算·MaterialFlow 物料流·MaterialSpec·LinkDerate·CoalQuality·SinkNode 等 8+)**早已移植**; **剥采比**核心指标全覆盖(BlockModel·PitDepthSolver·VpBalanceSolver·GeoDataQueries); `WorkWindowCalc` 系 DayCapacityBudget 容量引擎内部助手(依赖受阻的班次日历配置)→ 引擎内部, 记录。

**结论**: 逐模块 diff 既**证实**全部 9 大模块(Mesh/PointCloud/Road/Block/Geo/Sql/MineAss/Task/Plan)覆盖或边界已录, 又**发现** 6 主题透镜漏掉的 1 处真几何缺口(竖曲线)。教训: 收敛感≠收敛, 系统性模块 diff 是主题透镜之外的一层。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

## 一九三、作者标注切片扫描(第二独立收敛信号) —— 确认竖曲线为唯一干净缺口

**新法**: 原版作者在**自足可移植算法**上自注 `纯几何 / 可单测 / 零 UI 依赖 / 无宿主依赖`(竖曲线即此类)。用 Grep(UTF-8, 原文件 Read 可正常解码)全库搜这些标注→精确列出作者认定的独立切片, diff Kylin:
- **已覆盖**: HaulMetrics(运距/时间坡阻模型 `src/Cad/HaulMetrics.cs`) · TransportIndicators(运输指标) · BenchElevationAnnotator(标注台阶标高) · WorkingFaceLineFitter · DepositAutoDetector · PolylineMetrics(演化对比) · RoadGraph/RoadTopology(路网) · CenterlineJunctions/Pick/Inventory(中线管理) · SectionBuilder(剖面) · GeomMeasure(鞋带面积)。
- **引擎流水线几何步(非独立命令, 记录)**: EngineeringPosition/EpConnector/BenchFaceBuilder/BenchLineReplacer(创建工程位置多步引擎⑦台阶面) · InclineSurfaceBuilder/InclineConstraint(量驱动斜面引擎第3步) · TemplateDrivingEngine(逐刀/逐期位置线) · UnitGraph(建图) · TrendBenchIntegrator · WorkLineProjector · SimSolidBuilder/UnitSolidStage(推演层体三角化)——皆多阶段引擎的**中间几何层**, 输入为引擎中间态、输出喂下一步, 非独立特性(区别竖曲线: 完整算法·零引擎依赖·输入自然[中线]·普适意义)。
- **借鉴后跳过 · TaskZoneSplitter(采掘单元→任务区域)**: 是完整几何算法(前脸/坡底线+推进宽+各份量→二分求宽切条, 鞋带面积, 非线性面积-宽), 但**依赖 `DumpStripPlanner.ProjectOnto/AdvanceDirs`**(排土条带规划器几何)——而 Kylin 无 DumpStripPlanner(排土条带按 §一九一 系受阻排土规划引擎按钮), 且本质=**排土条带同款"按量沿推进切条"**、输入份额来自受阻排产引擎。→ 与受阻 排土规划 几何纠缠 + 输入为调度上下文 + 独立价值人为, 记录(非干净切片)。

**结论**: 两条**独立**系统扫描(§一九二 模块标识符 diff + §一九三 作者标注切片扫描)**双双收敛于同一结论**: 竖曲线是唯一干净的可移植独立切片(已补), 其余 `纯几何` 类要么已覆盖、要么是受阻引擎的流水线几何步。这比单一主题透镜的"收敛感"强得多。见 [[unlock-blocked-insights]]。

## 一九四、分析窗口 + 原版单测枚举(第三、四独立收敛信号)

再加两条**独立**扫描, 均确认收敛:
- **分析/报表窗口指标 diff(方法三)**: 枚举原版 17 个 `*AnalysisWindow/*StatsWindow/*ReportWindow`, diff Kylin。纯分析窗全覆盖(EquipmentAnalysis→MTBF/OEE/Weibull · FleetCockpit→机群驾驶舱 · OutputStats→产量统计 · CoalQualityStats/Dashboard→煤质统计/健康度/分煤层 · MeshVolumeReport→网格体积)。**两个"缺"皆引擎依赖**: `EquipmentStageAnalysisWindow`=**周排班矩阵**(21班×5工序能力-需求, 依赖月计划=排产域) · `QualityAnalysisWindow`=质量·配煤分析("**取引擎任务的质量回灌**"=TaskLib 引擎每面任务质量)→ 记录。
- **原版单测枚举 diff(方法四, 最精确)**: 原版有 **242 个单测**(Tests.MineAssLib/PitMineApp/RoadLib/TaskLib)——作者测过=自足可测。筛"纯算法气味"名 diff Kylin: **ProfileSmoother 赫然在列**(=竖曲线, 印证本法能命中真切片); 其余纯算法名全覆盖(Centerline*/PolygonRegion/RegionClip/PathSolver/RoadCenterlineExtractor/SeamQualitySampler/SlopeGeometry/TransportIndicators/HaulCaliper) 或引擎上下文(Incline*/Bench*/Chain*/Dispatch*/Shift*/RoadLayout*/TerrainRamp*/UnitGraph*)。抽查 3 个"缺": `LineAboveMeshClip`(创建工程位置引擎·排土面裁台阶线) · `MeasuredVolumeBackfill`(MiningModelPlanner 计划引擎) · `WorkSlopeRange`(本期工作帮范围·规划域)——**皆引擎上下文, 记录**。

**四法收敛**: §一九二 模块标识符 diff · §一九三 作者标注切片 · §一九四a 分析窗口指标 · §一九四b 原版 242 单测枚举——**四条独立系统方法一致指向**: 竖曲线是唯一干净可移植独立切片(已补), 余皆已覆盖或受阻引擎(流水线几何步/调度/计划/native)纠缠。原版**自己的测试**(最精确信号)把 ProfileSmoother 列为可测切片、印证本法命中真缺口, 且未暴露其他 Kylin 缺的独立算法。功能面收敛已达**四重独立证据**。见 [[unlock-blocked-insights]]。

**§一九四c 非几何"计算核"子扫(补 242 单测里几何滤网漏掉的 calc)**: 筛 `Kernel/Calc/Cost/Reserve/Caliber/Derate/Blend` 名 diff Kylin: **CapacityCaliber(年/月产能口径)已覆盖**(V050_fix_capacity_annual_to_monthly.sql 正是此修); 余 4 皆引擎/显示/配置边界, 记录——`PreparedReserve`(备采储量**回写**=PlanLib.ShortTerm 计划引擎) · `WeatherDerate`(TaskLib 产能引擎域) · `ProductionCostBook`(成本常量统一口径 MU14=配置一致性, 非新功能; 剥离成本算已在 PanelSplit) · `LabelDeCollide`(作业铭牌避让=受阻 sim 显示; 移到 Kylin 标注属发明原版没有的用途)。→ 至此原版 242 单测按 几何名 + 计算核名 + 全表 三重扫尽, **无 Kylin 缺失的干净独立算法**(竖曲线是唯一, 已补)。**五角度收敛**(四法 + 计算核子扫), 见 [[unlock-blocked-insights]]。

## 一九五、修正"五法收敛"的一处分类错误 —— 线形处理(CenterlineLineForm 完整①②③)

**自查纠错**: §一九四b 扫 242 单测时, 见 `CenterlineLineForm` 与 `CenterlineInventory/Pick/Junction` 同前缀, **误归为"中线管理(已覆盖)"**——实则 `CenterlineLineForm` 是**道路线形设计器**(非中线管理): GBJ22-87 三阶段 `Apply`, 原版自注"纯几何、不依赖引擎、可单测", 有独立 `CenterlineLineFormTests`。我上会话只移了它内部调用的**阶段③**(ProfileSmoother=竖曲线), **①②是真缺口**。经"在出竖曲线的同目录 RoadLayout 再针对性扫一遍"抓出。

**补全 · 线形处理(完整①②③)** [src/Cad/CenterlineLineForm.cs](src/Cad/CenterlineLineForm.cs) 忠实移植:
- **①转角圆弧化**: 内移偏置(朝形心, offset>0 时)+ 转角插圆弧(R≥rMin, 切线 T=R·tan(δ/2), 段长放不下则 clamp 降 R 计违规), 逐段打直线/圆曲线标记。
- **②分段限坡纵断面**: 直线段≤i_max、圆曲线段≤弯道折减 curveCap(且合成坡度 √(纵²+超高²)≤上限反推 curveCap), α=ΔH/Σ(L·cap) 缩放各段纵坡命中总高差; α>1 报"展线不足"。
- **③竖曲线**: **复用 [RoadVerticalCurve](src/Cad/RoadVerticalCurve.cs)**(=原 ProfileSmoother, 上会话已移)+ 最小坡长校核。
- 命令 `线形处理`(3D 中线 CSV→原/圆弧化平面线形入场景[灰/青]+平曲线半径/纵坡/合成坡度/竖曲线校核汇总) + 三路可发现(命令行别名/目录/RoadLib Ribbon 按钮 + 专属图标 `mineass_line_form`)。
- **非冗余**: 与竖曲线平滑互补——竖曲线=仅③(纵), 线形处理=①②③全(平+纵); 原版本就二者分开各有独立 test。
- 验证: [CenterlineLineFormTests](tests/PitMine3D.Kylin.Tests/CenterlineLineFormTests.cs) 10 例已知值(直角转角圆弧点落 R=15 圆·紧转角 clamp 到 R=5 计违规·限坡 α 缩放命中总高差·展线不足标志·弯道折减≤直线·合成坡度反推·竖曲线复用保端点)。**build 0 错·单测 1060→1070**。

**教训(硬)**: **系统扫描的每一项要单独核实语义, 别按名字前缀归堆**——`CenterlineLineForm` 与 `CenterlineInventory` 同前缀但一个是线形设计器、一个是中线管理。"五法收敛"的方法没错, 但我执行方法四时凭前缀把 LineForm 误并入已覆盖桶 → 漏掉真缺口。**收敛结论要靠逐项核实支撑, 名字聚类是陷阱(同 [[unlock-blocked-insights]] 文件名≠类名、中文串≠英文token 一类)**。补救法: **回到已产出真缺口的目录(RoadLayout)做针对性复扫**——竖曲线在此, 线形处理亦在此。见 [[unlock-blocked-insights]]。

## 一九六、台阶面提取(BenchFaceExtractor) —— "present but shallow" 深度缺口 + 纠正"坡顶底线 native 阻"记录

**顺着"名字前缀陷阱"纪律复查被我按前缀归堆的其余组**: `Centerline*` 余项(LayerDiff=增量落地/SetCodec=base64存档/Inventory=管理)确系管理/IO(covered); 但查 `Bench*` 组的 `BenchFaceExtractor` —— **真缺口**。

**present-but-shallow(深度)缺口**: Kylin 早有 `CrestToe.cs`(坡顶底线命令, ~40 行)——但它只出**平陡分界的散断棱边**(edge soup, 无序)。原版 `BenchFaceExtractor`(500 行, BlockModelLib.Domain, 有独立 test)**完整得多**: ①按坡度分陡/缓 + **闭运算**(补等高线 TIN 空洞平三角) ②陡三角**连通域分片**→独立台阶坡面 + **高程带再切**(多级坑壁粘连) ③每片**指标**(三维/投影面积·台阶高·平均坡度[由面积比反算]·水平投影宽) + **有序坡顶/坡底线**(按走向轴投影分上下沿, 抗碎边界) + 采场环裁剪。这是 node-editor 式"命令在但底层浅"缺口。

**移植** [src/Cad/BenchFaceExtractor.cs](src/Cad/BenchFaceExtractor.cs) 忠实全移 + 命令 `台阶面提取`(OFF 现状面→每片坡顶线[青]/坡底线[橙]入场景 + `r.Message`台阶高/坡度范围汇总) + 面编辑菜单项 + 命令目录。**非冗余**: 与 CrestToe 互补——CrestToe 出散断棱边, 台阶面提取出分片+有序上下沿+每片指标(真正的 3D 表面台阶分析, 区别 BenchAnalyzer 的 2D 剖面法)。验证 [BenchFaceExtractorTests](tests/PitMine3D.Kylin.Tests/BenchFaceExtractorTests.cs) 6 例已知值(单台阶: 台阶高10·坡度63.43°·投影宽5·坡顶落 x20z10/坡底 x25z0·采场环外排除·面积/台阶高下限丢弃)。**build 0 错·单测 1070→1076**。

**纠正记录**: [[unlock-blocked-insights]] 曾记"坡顶底线(native PMTB)阻"——BenchFaceExtractor 证明**表面法(现状面 TIN 按坡度)的坡顶底线是纯托管可做且更完整的**(native PMTB 点云栅格化路径另论)。**本会话三真缺口**(竖曲线/线形处理/台阶面提取)均在"名字前缀归堆"被自查纠错后挖出——收敛没到, 是我扫描执行有归堆盲区。见 [[unlock-blocked-insights]]。

## 一九七、煤岩台阶判定(StandardLevelModel 煤/岩判定核)—— 只移可验证切片, 大启发式记录

复扫 BlockModelLib/Domain 又见 `StandardLevelModel`(795 行 + 依赖 BenchLevelInventory + SeamColumnSampler 151 行)——"标准水平采矿模型": 台阶线→归标准水平级→相邻级配对成「幅」→逐幅判煤/岩台阶。**拆两层处理**:
- **可验证核 → 移**: 煤/岩判定(`SeamColumnSampler.CoalThicknessIn` + 分类)是自足可验切片。载体 [src/Cad/BenchCoalClassifier.cs](src/Cad/BenchCoalClassifier.cs): 沿坡顶线等点采样各煤层柱(**复用 [VirtualBorehole](src/Cad/VirtualBorehole.cs)**=TinSampler 竖直求交)→ 各层与台阶区间 [toeZ,crestZ] 重叠煤厚 → 沿线平均煤厚(**分母全采样点**, 尖灭处 0 拉低均值)→ 煤厚占比=均厚/台阶高 → 煤(≥0.5)/混(>0.05)/岩; 各煤层分列(一台阶压多层煤); 出不出煤体看均厚≥最小可采(与占比脱钩, 薄煤层不被台阶高永远判成岩)。命令 `煤岩台阶判定`(坡顶线 CSV+台阶高→种子库层位点建 seam→判定+按煤/岩着色入场景)+ 面编辑菜单 + 目录; **可接台阶面提取出的坡顶线**(§一九六→§一九七 链)。验证 [BenchCoalClassifierTests](tests/PitMine3D.Kylin.Tests/BenchCoalClassifierTests.cs) 6 例已知值(煤/混/岩边界·尖灭全点分母·薄煤占比与可采脱钩·多煤层分列)。
- **不可验证的大启发式 → 记录不移**: `StandardLevelModel` 的**台阶线→标准水平级归级 + 相邻级配对**含**实测图纸调校的相对闸门启发式**(`FaceRunRelaxFactor`/`MinAbsFaceRun` 相对间距闸门、实测倾角前探、原注"配对判据换过四版每版都在真实图纸上出错")——**本机无真实矿山图纸不可验**(不满足 loop"可验证"条件), 795 行 + BenchLevelInventory 依赖属中等子系统。按 loop"无法验证的先记录"记录。

**build 0 错·单测 1076→1082**。**本会话第 4 真缺口**(煤岩台阶判定), 承 §一九六 台阶面提取(几何)补上**煤/岩分类维**。判据: 大算法拆"可验证核"(移)与"实测调校启发式"(记录), 不整包硬吞不可验的启发式。见 [[unlock-blocked-insights]]。

## 一九八、逐项(非归堆)复扫剩余引擎组收官 —— 自足几何/地质切片脉挖尽

按"不按名字前缀归堆"纪律**逐项开来读**剩余被归堆的组, 确认无更多可移独立切片:
- **BlockModelLib/Domain 挖尽**: BlockEditor(块体删除/约束 AABB/Mesh)= Kylin `约束块体`/`实体转块体`(GWN 逐格判内外)/`删除块体` 已覆盖; SectionSampler(境界 Z 层聚合)≈ `ResourceByElevation`; MeshContainmentTester=`WindingNumberTester`; WorkingFaceLineFitter/DepositAutoDetector 已移; OctreeLeafBuilder=块模内部; TaskZoneSplitter=依赖受阻 DumpStripPlanner(记录)。
- **TaskLib/Zoning**: ZoneRaster(占地栅格)/ZoneFaceBinding(区↔面匹配)虽标"纯几何", 但**被 ProcessZonePlanner/ZoneAutoPlanner/ZoneLinkage 消费**=区划规划引擎流水线几何步(输入引擎中间态), 非独立特性 → 记录。
- **TaskLib/Simulation**: SimSolidBuilder/UnitSolidStage(推演层体三角化)几何= Kylin loft/prism 原语已有, 装配属 sim 引擎 → 记录。
- **PlanLib**: AdvancePlanner/SectionSolver/BenchElevationAnnotator/ComparisonBuilder(→ProgramComparer 方向感知归一)/PitEvaluator(→规划计算/PitDepthSolver NPV·剥采比)/CoalSinkAdapter(→SinkNode) **均已覆盖**(多为前期"最小 plan/参数"切片提出)。
- **TaskLib 排产核**(Chain/Dispatch/Shift/Unit/Day/Week/Month/Order/Fleet/Truck): 记录的 17,588 行引擎(ProductionPlanContext/ExploderConfig/FlowAssigner), 8 个自足切片早已提出, 余引擎上下文。

**结论**: 自足几何/地质切片脉(RoadLayout 2 + BlockModelLib/Domain 2)**已挖尽**——本会话 4 真缺口后, 逐项(非归堆)复扫剩余引擎组无更多可移独立切片, 剩全为**引擎流水线几何步(消费者已核实)/已覆盖/记录的大引擎**。这比 §一九四 的"五法收敛"可靠——那次有归堆盲区, 本次逐项核实消费者。可验证/可实现/保真的独立算法切片在此收敛。见 [[unlock-blocked-insights]]。

## 一九九、煤层露头线(SeamOutcropLineExtractor)—— §一九八"挖尽"又错: 注释措辞过滤漏项

**§一九八 刚宣称 BlockModelLib/Domain 挖尽, 本轮即在同目录挖出第 5 真缺口** `SeamOutcropLineExtractor`——**根因: 我的复扫用 `grep 纯几何|可单测` 过滤, 而此文件注释写"露头线是算出来的"不含这些词, 被漏。** 又一次"扫描执行有过滤盲区"(继 名字前缀归堆、注释措辞)。

**移植** [src/Cad/SeamOutcropLineExtractor.cs](src/Cad/SeamOutcropLineExtractor.cs) 忠实全移: 现状面三角网上求 **现状Z−顶板Z=0(坡顶=顶板露头)** 与 **现状Z−底板Z=0(坡底=底板露头)** 两条等值线(**marching triangles**: 逐三角看标量 f 三顶点符号, 变号边线性插值取点, 焊接成折线, 穿顶点去重, Douglas-Peucker 抽稀)。**与台阶面提取/煤岩判定互补**: 台阶面提取=坡度式(几何), 煤岩判定=分类; 露头线=**煤层与现状面交线**(地质式坡顶/坡底, 露头线**算出来天然一一对应**, 是原版否定"猜配对"三版失败后的正解)。附 `PairIntoBands`(按并行性配露头带, 间距∈[煤厚/tan60°,煤厚/tan5°]且离散小)。SampleZ 由调用方传(Kylin: `GetHorizonPoints`→`VirtualBorehole`分层→`TinSampler.SampleZ`)。命令 `煤层露头线`(OFF现状面+种子库→逐层出坡顶青/坡底橙线)+面编辑菜单+目录。验证 [SeamOutcropLineExtractorTests](tests/PitMine3D.Kylin.Tests/SeamOutcropLineExtractorTests.cs) 4 例已知值(倾斜面 z=10−0.5x 交顶板 z5 于 x=10/交底板 z2 于 x=16·整层在面下无露头·无数据顶点跳过不造点·并行配带间距6)。**build 0 错·单测 1082→1086**。

**教训(硬, 第 3 类扫描盲区)**: 复扫目录找切片, **别用注释措辞 grep 过滤**(纯几何/可单测 只是部分文件的写法)——要 **`ls` 目录逐文件读用途**。三类盲区已犯: ①名字前缀归堆(线形处理/台阶面提取) ②注释措辞过滤(本项) ③present-but-shallow(台阶面提取)。**"挖尽/收敛"宣言的前提是逐文件读过, 不是 grep 过一遍**。见 [[unlock-blocked-insights]]。

## 二〇〇、月度剥离均衡(TautString 拉紧绳)—— robust ls+读法生效, 第 6 真缺口

用新纠正的 **`ls` 目录逐文件读 `<summary>`** 法扫 MineAssLib/Driving(~42 文件, 之前 grep 过滤过), 挖出 `TautString`——经典**拉紧绳**算法, Kylin 缺(有 `VpBalanceSolver`=剥采比均衡[VP 曲线 采出↔剥离比 K 段], **本项是时间轴月度剥离调度**, 不同问题)。

**移植** [src/Cad/TautString.cs](src/Cad/TautString.cs) 忠实全移: 下包络 lo(必须剥)与上包络 hi(能力)之间求【单调不减·增量最平】累计剥离曲线(漏斗法 O(T²)); 对**任意凸代价同时最优**(最小方差/峰值/相邻月跳动一次全拿, 拉绳经典性质); 前提"剥离只能提前不能推后"(R37, sMin 从 0 起); 转折点标 触底(露煤紧迫)/触顶(能力吃紧)。命令 `月度剥离均衡`(CSV 期号/累计必剥/累计能力 → 均衡累计曲线青 + lo/hi 包络灰入场景, X=期号 Y=累计 + CV/关键月) + 目录; 区别既有 `剥采比均衡`(VP 比值)。验证 [TautStringTests](tests/PitMine3D.Kylin.Tests/TautStringTests.cs) 6 例已知值(线性恒速率无内折·陡约束触底折 c=[0,12.5,25,30]·走廊内单调·拉绳CV<照lo走CV·能力压顶带超前储备·倒挂不可行)。**build 0 错·单测 1086→1092**。

**本会话第 6 真缺口**——robust 逐文件读法(纠正 grep 过滤盲区)立即在"已 grep 过"的目录再挖出一个。**印证: 之前所有"grep 过滤"式复扫都可能漏, 该目录逐文件读法继续**。见 [[unlock-blocked-insights]]。

## 二〇一、更新煤层面(SurfaceUpdateEngine 羽化局部更新)—— robust 读 MeshEditLib, 第 7 缺口

robust 逐文件读 PlanLib(参数识别工作流 采场圈定/现场参数提取/参数验收 已覆盖, 余规划引擎)、MeshEditLib(Contour/Delaunay/Estimation/Boundary/Weld/Merge/Section/Solidify/QuickModel 皆覆盖, *Report=native PMxx)后, 在 MeshEditLib/ModelUpdate 挖出 `SurfaceUpdateEngine`——**更新煤层面/现状面**, Kylin 无(命令不存在)。

**移植(拆可验证核)** [src/Cad/SurfaceUpdate.cs](src/Cad/SurfaceUpdate.cs): `Evaluate` 核心=用观测点(现状见煤/补勘顶底板)**局部羽化更新**目标三角网顶点 Z——影响半径 R 内顶点按到最近观测 smoothstep 羽化(内 1→边界 0)、区内目标值 IDW 拟合观测点、newZ=vz+w·(est−vz), 区外不动; 附影响片区(影响圈搭接<2R 并查集归片)+ 位移/面积/净体积统计。命令 `更新煤层面`(目标 OFF + 观测点 CSV + 半径 → 观测点入场景 + 位移/净体积/片区汇总 + **导出更新后 OFF**) + 面编辑菜单 + 目录。验证 [SurfaceUpdateTests](tests/PitMine3D.Kylin.Tests/SurfaceUpdateTests.cs) 6 例已知值(单观测 smoothstep 羽化 newZ=w·5·落点=5·区外不动·落观测点取其值·分离/搭接观测点 2/1 片区·下沉净体积负)。**build 0 错·单测 1092→1098**。

**拆核记录**: 原 520 行 SurfaceUpdateEngine 中——①**Evaluate 羽化更新核**已移可验; ②**多算法 NN/MA/IDW/OK/SK/UK** —— **已补全**(复用 Kylin `OrdinaryKriging.EstimateAt/EstimateSimpleAt/EstimateUniversalAt` 点式 OK/SK/UK + 变差函数拟合一次; NN/MA/IDW 内联; 单点退化 NN; 命令 `更新煤层面 <半径> <算法>`; +3 单测: NN 取最近值/MA 等权均值/OK 常数数据返常数; build 0错·1098→1101); ③**三维分级色带 overlay** 走 native PMBI, 2D 场景受阻, 未移(此出更新后 Z + 数值统计替代)。**本会话第 7 真缺口**。见 [[unlock-blocked-insights]]。

## 二〇二、robust 读 PointCloudLib/RoadLib/GeoDataBase —— 均覆盖/native/DB, 无新切片(sweep 收敛)

继续 `ls`+逐文件读余下模块, **均无新可移独立切片**:
- **PointCloudLib**: 绝大多数是 `解析自 native PMxx` 结果类(抽稀/去噪/裁剪/体积/两期/TIN/坡度坡向粗糙度曲率/补洞/障碍/质量统计——算法在 C++ native, Kylin 已各自**托管重实现**覆盖); 道路中线(骨架法)/剖面/GeoTIFF 亦覆盖。无纯托管新算法。
- **RoadLib**: 演化对比/中线管理/路网/运距/点对点寻径/运输指标/路面生成 全覆盖; StructurePavement=可视化(2D 场景), HaulSolveKernel=点对点寻径覆盖, symbology/edit=显示/交互。无新切片。
- **GeoDataBase Domain/Services**: 服务层=DB 访问(GeoDataQueries 覆盖); `VirtualDrillEngine`=VirtualBorehole、`TinZSampler`=TinSampler 已移; **`CoalQualityEstimator`(煤质 NN/IDW/OK/MA/SK/UK 空间估值)—— Kylin `OrdinaryKriging.cs` 注释明写"忠实移植原 CoalQualityEstimator 的 OK 核", 且 克里金/IDW/NN/MA/SK/UK 估值 + 空间分布 + 克里金方差 全已接(命令 §805/§4938)——已覆盖**; CoalQualityAnalytics=CoalAnalytics + 本会话煤质分析覆盖。

**sweep 收敛**: 自足几何/地质切片 7 真缺口全在**采矿域几何/地质模块**(RoadLayout/BlockModelLib.Domain/MineAssLib.Driving/MeshEditLib)——robust 逐文件读挖出; **通用/数据模块**(PointCloudLib native·RoadLib·GeoDataBase)robust 读**确认覆盖/native/DB**, 无新切片。余待读 SqlLib(SQL 覆盖)/SeamOutcrop 余(3D 着色阻·Refiner 我露头线覆盖)/CurrentState 余(创建三角网覆盖·config·display)——皆低产。见 [[unlock-blocked-insights]]。

**TaskLib robust 读补(收官最后一个"lump 为引擎"的模块)**: 逐文件读 TaskLib(非 Engine/Sim/Zoning 引擎核)——8 自足切片(TaskQuantity/MaterialFlow/MaterialSpec/SinkNode…)已移; `CapacityFromEquipment`(面日能力=编组班产×工时, FleetMatcher 解)= Kylin **FleetMatch/FleetCycle 覆盖**; `RegionAdvanceAzimuth`= **AdvancePlanner 覆盖**; `ShiftZoneGeometry`(点在区)= 多边形判覆盖; **Report* 子系统**(自定义报表模板 + 指标库 + HTML/PDF/Word 渲染)= **大 UI/文档导出子系统**(Kylin 有 导出分析 CSV; 全报表引擎需文档库+模板 UI, **记录为大功能边界**, 非 loop-tick 切片); 余 Day/Month/Shift/Dispatch/Gantt = 排产引擎/显示。**无新自足算法切片**。至此**全 9 模块 + 全子目录 robust 逐文件读毕**。

## 二〇三、区域生长分割(RegionGrow)—— 复评"变体敏感"记录, 曲率随 PCA 免费得

复评记录项"点云分割变体敏感"(memory): region-grow 曾判受阻的唯一卡点=需逐点**曲率**做种子排序, 而 Kylin `PointNormals` 只出法向。**复评发现: 曲率 = λmin/Σλ, 与法向同出一次 PCA(JacobiEigen3)** —— 卡点消解。且 Kylin 已托管重实现 Euclidean 分割(`PointCluster`), region-grow(按光滑度)是**互补的另一标准变体**(非冗余: 距离聚 vs 光滑度聚)。

**移植** [src/Cad/RegionGrow.cs](src/Cad/RegionGrow.cs): 逐点 PCA(k 近邻, 复用 JacobiEigen3)得法向+曲率; 曲率升序取种子(最平处起); BFS 生长——邻点法向夹角<平滑阈并入、邻点曲率<阈再作新种子(过折棱不再扩); 丢小区+紧凑重编号。命令 `区域生长分割 [平滑角°]`(点 CSV→按光滑度分区, 各区 hue 异色/折棱/小区灰) + 目录(并补登 分割点云)。**区别 分割点云(欧氏=按距离)**: 本命令按表面光滑度, 台阶面/平盘/坡面在折棱处法向突变而分开。验证 [RegionGrowTests](tests/PitMine3D.Kylin.Tests/RegionGrowTests.cs) 4 例已知值(平面单区·陡折棱[113°夹角]分两面且 A/B 深处异区·极缓弯[4°]仍一区·点太少 0 区)。**build 0 错·单测 1101→1105**。

**教训**: "变体敏感"记录不等于永久受阻——region-grow 是**标准算法**(PCL RegionGrowing), 卡点(曲率)其实随现有 PCA 免费得; Kylin 已做 Euclidean 一变体, 补 region-grow 是一致的(标准重实现, 文档标"与 native 具体变体可能不同")。**本会话第 8 个功能**(7 缺口 + region-grow)。判据仍守: 参数(平滑角/曲率阈)与结果确定对应=可验; 非"需鲁棒谓词"的真受阻类(mesh 布尔)。见 [[unlock-blocked-insights]]。

## 二〇四、移除障碍物(渐进形态学 PMF)—— present-but-shallow: 地面点滤波 crude vs PMF 稳健+出障碍

复评点云 `剔除障碍`——发现 Kylin 的 `剔除障碍` 别名实指 `MeshSpikeCull`(网格去尖刺), **非**原版点级"移除障碍物"(渐进形态学 PMF, 出地面点+非地面点两个云); 且 Kylin `地面点滤波` 是 crude 每格最低点。→ present-but-shallow 缺口。原 PMF 走 native(仅 GroundFilterDialog 是 UI, 无托管算法), 按标准算法(Zhang 2003)托管重实现。

**移植** [src/Cad/ProgressiveMorphFilter.cs](src/Cad/ProgressiveMorphFilter.cs): 栅格取每格最低点为初始面; 窗口渐增(1,3,7,15…≤maxWindow)的形态学**开运算**(先腐蚀 min 后膨胀 max)逐尺度削物体; 高程差>阈 dh_k=min(dhMax, dh0+slope·Δwindow·cell) 的格降到开运算面(阈随窗口增长→缓变地形保留、突变物体削去); 末逐点判 z−裸地面>dhMax=非地面。命令 `移除障碍物 [格边] [高差阈]`(点 CSV→地面棕/非地面红两色入场景+计数) + 目录。**比 地面点滤波稳健且另出障碍点云**。验证 [ProgressiveMorphFilterTests](tests/PitMine3D.Kylin.Tests/ProgressiveMorphFilterTests.cs) 4 例已知值(高出地面判非地面·孤立物体[其下无地面]被开运算削去=9点非地面·缓坡0.15<容差0.3全保留·空输入空)。**build 0 错·单测 1105→1109**。

**本会话第 9 个功能**。present-but-shallow 判据: "已有同名命令"要查①是否真同一算法(剔除障碍别名实为去尖刺≠PMF)②深度(地面点滤波 crude vs PMF)。原 native 的标准算法(PMF)可托管重实现(同 Euclidean/region-grow)。见 [[unlock-blocked-insights]]。

## 二〇五、导入 PitMine 工程(.pmx 原版二进制)—— 新扫区 Host/PitMineApp/Cad, 工程互操作(第 10 功能)

**新扫区**: 之前 robust 逐文件读只覆盖 Modules(域插件), 未读 **Host/PitMineApp/Cad**(app 层)。逐文件读 Host/Cad: Export(KDF 已移)/FileTree(文件管理器已有)/Import(格式 reader 已移)/**Pmx(工程格式, 未移)**/NodeEditor(已移)。→ `Pmx/` 是原版**工程文件 .pmx** 的 I/O(PmxReader/Writer/Format/Tables, **managed 二进制, 规格在代码里——非 native PMxx**)。Kylin 的 `.pmx` 是自己的**文本**格式(SceneIO), **读不了原版二进制 .pmx** → 工程互操作缺口(在 Kylin 打开原版工程)。

**移植(核心 MVP)** [src/Cad/PmxImportService.cs](src/Cad/PmxImportService.cs): 按 PmxFormat 规格读 Header('PMX1' 32B)+段表(Strings/Layers/TextStyles/Entities)+每实体 recordLen(权威, 可跳未知类型)。核心实体→Kylin 实体: 线/点/多段线(闭合)/文字(宽度/倾斜取样式)/三角网(→去重棱线)/圆/圆弧(3点); TrueColor 精确/ByLayer 取层色; 复杂类型(MText/Hatch/标注/椭圆/样条)按 recordLen **跳过不崩**(MVP, 同 MapGIS arc 级)。命令 `导入PMX`(选 .pmx→实体入可编辑场景 + 建图层 + 范围缩放 + 计数) + 目录。区别 Kylin 文本 .pmx(用『打开』)。

**验证(强)**: [PmxImportServiceTests](tests/PitMine3D.Kylin.Tests/PmxImportServiceTests.cs) 6 例—— 4 合成档往返(按规格构档→读→核线/点/多段线闭合/圆几何+TrueColor 绿) + 2 **真实原版样本**(桌面 untitled.pmx 98KB / 现状.pmx 363KB, skip-if-absent, 本机读出实体>0)。**build 0 错·单测 1109→1115**。

**本会话第 10 功能**。教训: **robust 逐文件读要含 app 层(Host/PitMineApp), 不止域 Modules**——工程文件格式(.pmx)在 app 层。managed 二进制格式(规格在代码)≠native PMxx(无源), 可移。见 [[unlock-blocked-insights]]。

## 二〇六、导出 PitMine 工程(.pmx)—— 完成 PMX 互操作往返(第 11 功能)

承 §二〇五 导入, 补**导出**(反向互操作: Kylin 场景 → 原版可打开的二进制 .pmx)。[src/Cad/PmxExportService.cs](src/Cad/PmxExportService.cs) 与导入同规格逆向写: 串表(去重 intern)+图层+空样式段+实体段+Header/Footer(**CRC32 IEEE** 正确算, 供原版校验); 映射 Kylin→PMX 线1/点3/多段线2(闭合)/文字4/圆14/矩形→闭合多段线; TrueColor 精确; 圆弧(3点↔圆心角)/正多边形 MVP 暂不导。命令 `导出PMX`(场景→存 .pmx)+ 目录。

**验证(往返, 强)**: [PmxExportServiceTests](tests/PitMine3D.Kylin.Tests/PmxExportServiceTests.cs) 4 例—— Kylin 实体 → PmxExportService 写 → **PmxImportService 读** → 往返一致(计数·线几何+色·多段线闭合·圆·文字位置/高/转/文本)+ Header magic 'PMX1'/Footer magicEnd '1XMP' 校验。**导入+导出互为验证**(两者同规格, 往返恒等)。**build 0 错·单测 1115→1119**。

**本会话第 11 功能**。PMX 工程互操作**读写双向完备**(读原版工程 + 存回原版格式)。教训: 导入/导出成对时, **往返测试(写→读→恒等)是最强验证**——两者互验, 无需外部样本(真实样本另做 skip-if-absent 端到端验)。见 [[unlock-blocked-insights]]。

## 二〇七、快速选择(QSELECT)—— 补齐真过滤核, 修正命令误绑(第 12 功能)

**新扫区 `Platform/`**(前仅扫 Modules + Host, 新发现平台层 3 工程)。`Platform/PitMine.Platform` 多为插件契约接口(不可移, Kylin 另有架构), 但含**具体纯算法**类。逐文件读出两处真功能, 本 tick 补第一处。

忠实移植原 `PitMine.Platform.Selection`(三文件): [src/Cad/QuickSelect.cs](src/Cad/QuickSelect.cs) = 枚举/特性/快照/条件/结果(Model) + 类型↔特性表(Catalog) + **纯过滤核**(Filter, 原明言"不碰 P/Invoke、不碰 UI，可脱 GUI 单测")。含: 按量级放大的相对数值容差(native `to_wstring` 只留 6 位, 1e-9 卡不中)、手写双指针回溯通配(* / ?, 不走 Regex 防注入/灾难回溯)、JSON 摊平(数组拆下标/嵌套拆点号)、`Describe` 落历史。类型 id 沿用原 AcDbEntityType 枚举值。Kylin 侧加 `QuickSelectSnapshot`: 2D 场景实体 → EntitySnapshot(类型/图层/色打包 0xRRGGBB/线宽/Extended 全填, Handle=下标)。

**修正命令误绑(name-collision 盲点)**: 原 Kylin 把字符串 `快速选择` 误绑到 `SelectSimilar()`——但 AutoCAD 里 **快速选择(QSELECT, 条件对话框) ≠ 选择类似(SELECTSIMILAR, 选同类)** 是两条命令。现: `选择类似`→SelectSimilar(选同类, 原逻辑不变), `快速选择/条件选择/QSELECT [<类型|*> <特性> <运算符> <值> [排除][追加][当前]]`→新 `QuickSelectCmd`(真条件过滤: 类型先决条件 + 特性/运算符/值 + Include/Exclude/Append/范围, 命中回映实体, 过滤锁定/关闭图层落选择集)。例 `快速选择 圆 半径 > 5`、`快速选择 * 图层 = 煤层`、`快速选择 文字 内容 * 标高*`。

**验证(已知值 + 端到端)**: [QuickSelectTests](tests/PitMine3D.Kylin.Tests/QuickSelectTests.cs) 26 例(含 Theory)—— 通配 13 例、数值量级容差、布尔/文本、JSON 摊平(数组+嵌套+坏 JSON 空字典不抛)、目录(null 只给通用特性/圆含半径/文本无 ></通配裁剪/Find/SourceOf)、**核心正确点**「圆+半径>5+排除」= 半径≤5 的圆(下标 0)绝不扫入线/文字(类型先决条件, 非全盘取反)、按图层跨类型选、只给类型选全部、文本通配、多段线布尔闭合、快照类型/色打包/Extended、取不到值恒不匹配、null/空候选安全。**build 0 错·单测 1119→1145**。

**本会话第 12 功能**。教训: **③ present-but-shallow/name-collision 盲点复现**——字符串命令存在(`快速选择`)但绑的是粗替身(SelectSimilar), 真 QSELECT(条件过滤)未实现; 判"已有此命令"前必看它绑到什么。**扫区要含 `Platform/`, 不止 Modules + Host**。平台层接口多不可移, 但夹带的具体纯算法(QuickSelectFilter/BenchLevelInventory)可移可验。见 [[unlock-blocked-insights]]。

## 二〇八、平盘标高清单(BenchLevelInventory)—— Platform 层第二处纯算法(第 13 功能)

承 §二〇七, 补 `Platform/PitMine.Platform/Geometry/BenchLevelInventory` —— [src/Cad/BenchLevelInventory.cs](src/Cad/BenchLevelInventory.cs) 与原版逐字一致。把一批台阶线(坡顶/坡底近似等高的多段线)**按标高归并成平盘标高级**: 逐线取代表标高(顶点 Z 中位数, 个别歪点不带偏)+ 平面(仅 XY)长度; 剔斜线(起伏>容差=坡面/出入沟)、碎线、无效; **标高一维聚类**(升序扫描, 间隙>容差断新级, 再限本级跨度 ≤2×容差防链式吞并); 落级(高→低)给标高/线数/平面长度/级间距中位+四分位距; 体检提示(只 1 级/疑混入地形等高线/级间距不齐/疑漏平盘)。答案 = 设计里有几个平盘标高。

**2D 场景适配**: Kylin 场景实体无逐点 Z, 活图取线拿不到标高 → 命令侧由 **CSV(lineId,x,y,z[,layer])** 喂料(`ParseCsv` 按 lineId 分组连线, 表头/注释/空行跳过), 算法本身不变。命令 `平盘标高清单 [合并容差m]`: 选 CSV → 归级 → 画各线(投影 XY, 按级红→蓝配色)+ 逐级标高标注 + 存清单 CSV(`BuildReport`)。忠实原 CSV 报表口径。

**验证(已知值)**: [BenchLevelInventoryTests](tests/PitMine3D.Kylin.Tests/BenchLevelInventoryTests.cs) 10 例—— 三级高→低(标高/序/顶底/级间距中位/离散)、坡顶坡底同标高合并为一级、一级平盘打断成多线仍算一级(按标高不按线数)、**链式吞并防护(2×容差跨度限)**、斜线剔除+坡面线提示、碎线/无效剔除、平面长度仅 XY(忽略 Z)、空/全斜线降级 Ok=false、报表含个数与表头、CSV 分组喂料端到端。**build 0 错·单测 1145→1155**。

**本会话第 13 功能**。教训: **Platform 层的具体纯算法(非接口)是可移可验的富矿**——一个 Platform 程序集扫出 2 个真功能(QSELECT + 平盘标高清单)。**算法保真 + 输入源按 2D 场景约束适配(CSV 代活图取线)**是既忠实又可验的通路, 与既往 CSV 替 DM8 同型。见 [[unlock-blocked-insights]]。

## 二〇九、现状台阶参数提取(ParameterExtractor 件二)—— PlanLib.ShortTerm 纯算法(第 14 功能)

**新线索来自 `测试实验/param_extract`**(一个 console 驱动器, 引用 `PlanLib.ShortTerm.ParameterExtractor`/`LandformClassifier`)——顺藤查 PlanLib/ShortTerm(52 文件, 大排产引擎, 记为无头不可验), 但其中夹带**小而纯的几何/分类算子**(件一/件二: 采场排土场自动识别与参数校核), 明言"纯 C# 栅格管线, 无内核依赖"。交叉核对 Kylin 已有: LandformClassifier→已移(RasterMorphology), MineableAreaIdentifier→已移, RegionGeometry→已移(RegionBool), BenchWidthIdentifier→已移(平盘宽度识别); **ParameterExtractor→无(缺)**, BenchElevationAnnotator→无(缺)。

补 `ParameterExtractor`(件二·提取): [src/Cad/BenchParameterExtractor.cs](src/Cad/BenchParameterExtractor.cs) 与原版逐字一致。从坡顶/坡底台阶线**逐顶点最近邻**反推现状台阶参数——坡面=每坡顶顶点找下方(Δz∈窗口)最近坡底顶点→(H,run,α); 平盘=每坡底顶点找≈同标高(±0.6·中位H)最近坡顶顶点→W(**重合点守卫**防退化输入把 W 算成 0); median 聚合 + IQR 报离散; 台阶数=坡顶标高聚类; β 推导(atan(H/(H/tanα+W)), 内联原 BenchTemplateResolver 单式) + β 实量(最高坡顶→最低坡底); 采深=Zmax−Zmin。**与 Kylin 既有 BenchAnalyzer 区别**: 后者吃剖面一维断面, 本类吃平面台阶线二维最近邻——不同算法。命令 `现状参数提取 [最小落差 最大落差]`(CSV role,lineId,x,y,z → 反推 → 画坡顶红/坡底蓝 + 存报表)。

**名冲突辨析**: Kylin 旧「现场参数提取」= BenchWidthAsync(BenchWidthIdentifier, 出达标平盘**区域**), ≠ 原 ParameterExtractor(出台阶**参数值** H/α/W/β)。故本功能用 `现状参数提取/台阶参数反推` 另立, 不夺旧名。

**验证(已知值)**: [BenchParameterExtractorTests](tests/PitMine3D.Kylin.Tests/BenchParameterExtractorTests.cs) 8 例—— 两级台阶 H=10/α=45°/W=5/β≈33.69°/实量β≈38.66°/采深20/齐整(合成台阶闭式核验)、单级告警无平盘、台阶高不齐告警、缺坡顶或坡底降级、坡面配对遵守 Δz 窗口、**重合点守卫保平盘宽非 0**、CSV 按 role+lineId 分组、报表含头部指标。**build 0 错·单测 1155→1163**。

**本会话第 14 功能**。教训: **`测试实验/` 的 console 驱动器是发现 module 内深埋纯算子的线索**——顺 `param_extract` 引用挖出 PlanLib.ShortTerm 的件一/件二簇, 大引擎虽不可验但其中的**纯几何算子可单独移可验**(6 个里 4 个先前已移, 补 1 个, 余 BenchElevationAnnotator)。判"大引擎不可移"别一刀切, 内部纯算子要逐个看。见 [[unlock-blocked-insights]]。

## 二一〇、标注台阶标高(BenchElevationAnnotator)—— ShortTerm 纯放置算法(第 15 功能)

补 ShortTerm 簇最后一处缺口 `BenchElevationAnnotator`: [src/Cad/BenchElevationAnnotator.cs](src/Cad/BenchElevationAnnotator.cs) 移植其**放置算法**——① 每条台阶线代表点 = 最接近 XY 质心的顶点(符号坐落线上非悬空质心), 代表高程 = 顶点 Z 均值取整; ② **平盘居中** = 代表点与「最近同高程·异线」边点取中点(坡顶线↔坡底线夹出的平盘两沿同高程, 中点落平盘内部); ③ **网格去重** = 平盘点落同格 + 同整米高程只留一处(坡顶/坡底各算一次自然并成 1); ④ 按作业区域类别配色(采场橙/外排蓝/内排绿)。放置核与原版逐字一致。

**2D 场景适配(记录)**: 原 `Build` 产出 **PMBI 三维实体载荷**(含绕 X/Y/Z 倾斜的立式朝向框、字体样式), 推入内核生成 AcDb 实体——PMBI 是**内核喂食内部传输**, Kylin 无内核、场景 2D XY, 故本类改出**放置点列(Marker)**(纯、可单测), 由命令层画成 2D 场景实体(▽ 闭合多段线 + 顶边引线 + 文字); 三维倾斜/朝向不适用 2D 场景故弃。符号几何(TriangleXY/LeaderXY/TextAnchorXY)平面化。命令 `标注台阶标高 [符号大小m]`(CSV lineId,x,y,z[,category] → 放置 → 画 ▽+引线+高程)。

**验证(已知值)**: [BenchElevationAnnotatorTests](tests/PitMine3D.Kylin.Tests/BenchElevationAnnotatorTests.cs) 8 例—— 坡顶坡底同高程各自居中→同落 X=5→去重成 1(居中计数 2)、关居中留 2 处、网格去重(近并/远留)、高程标签正负零号、类别配色 + 固定色压过类别、代表点=最近质心顶点、空/无效降级、▽/引线/文字符号几何闭式值。**build 0 错·单测 1163→1171**。

**本会话第 15 功能**。至此 **PlanLib.ShortTerm 件一/件二纯算子簇全数到位**(LandformClassifier/MineableArea/RegionGeometry/BenchWidth 先前已移 + ParameterExtractor + BenchElevationAnnotator 本会话补)。教训: **产出内核专属格式(PMBI)的纯算法, 把"算法核"与"输出格式"切开——核保真移植 + 输出改投 2D 场景实体**, 即可移可验; PMBI 本身(内核喂食传输)无 Kylin 消费者故不移。余 ParameterVerifier(件二·校核)依赖参数验收引擎链, 待评估。见 [[unlock-blocked-insights]]。

## 二一一、现状台阶参数校核(ParameterVerifier 件二·校核)—— 完成件二对(第 16 功能)

补 `ParameterVerifier`, 完成件二对(提取 §二〇九 + **校核**): [src/Cad/BenchParameterVerifier.cs](src/Cad/BenchParameterVerifier.cs) 把提取的实测台阶参数与「设计基准 + 规范默认」逐项校核(偏差%+状态), 稳定性 F=tanφ/tanβ。

**依赖辨析——移的是原版兜底路径, 非新造**: 原 `Verify` 先经 GeoDataBase 参数验收引擎(`ComputeStatus`, 带 StandardMin/Max 分 pass/warning/fail) + 模板库 `BenchTemplateResolver.Resolve`(设计值); **两者未就绪时原版本地兜底**——设计基准取 `Norm`(采场通用 12/70/4·硬 15/70/8·中 12/68/6·软 10/60/5; 排土 10/35/3), 状态取 `FallbackStatus`(|偏差|>15%→warning 否则 pass)。Kylin 无 GeoDataBase 模板库(大引擎, 记为不可移), 正落在原版这条兜底分支——故本类是**原兜底路径的完整忠实移植**(Norm/FallbackStatus/Worst/CohesionlessFactorOfSafety 逐字), 非发明。模板库精细分级(StandardMin/Max 的 fail 档)因无库不可得, 记录。命令 `参数校核 [排土] [hard|medium|soft] [摩擦角φ]`(CSV→提取→校核→存提取+校核合并报表)。

**验证(已知值)**: [BenchParameterVerifierTests](tests/PitMine3D.Kylin.Tests/BenchParameterVerifierTests.cs) 11 例—— Norm 五档与原版一致、实测=规范全 pass、偏差>15% 警戒边界(16.67% warn / 12.5% pass)、排土场基准、稳定性 F 阈值(陡坡<1.3 警/缓坡≥1.3 合格)、无摩擦角或 β=0 不算 F、设计覆盖、Worst 聚合、null 安全、报表头部、提取告警流入校核。**build 0 错·单测 1171→1182**。

**本会话第 16 功能**。至此 **PlanLib.ShortTerm 件一(采场排土场识别)+ 件二(参数提取+校核)整套到位**。教训: **判"依赖大引擎不可移"前, 看原算法有没有自带兜底路径**——ParameterVerifier 表面依赖 GeoDataBase 验收引擎+模板库, 但原代码 try/catch 全带本地兜底(Norm+FallbackStatus), 那条兜底分支正是无库环境(=Kylin)的忠实全貌, 可完整移可验; 只有库精细档(StandardMin/Max)不可得需记录。见 [[unlock-blocked-insights]]。

## 二一二、煤质深度分析补全(灰分-发热量回归 + 综合结论)—— present-but-shallow(第 17 功能)

**元角度: 逐子目录核实"真读过"**。前称"全模块读毕"曾被证不实(ShortTerm/Platform 都漏)。系统枚举各模块子目录, 逐个 robust ls+读——`GeoDataBase/Domain/Services/Geology/CoalQualityAnalytics.cs`("煤质深度分析引擎, 纯 C#, 无外部依赖")对比 Kylin `CoalAnalytics`: **8 方法里 6 已移, 2 缺**——`AshCalorificRegression`(灰分-发热量回归)/`OverallConclusions`(综合结论)。典型 present-but-shallow: 子系统已移大半, 剩两处纯统计缺口。

忠实补入 [src/Data/CoalAnalytics.cs](src/Data/CoalAnalytics.cs): ①**灰分-发热量回归**——一元 OLS(β=Sxy/Sxx, α, r²) + 残差 z-score 离群(|z|≥2.5, 按 |z| 降序); ②**综合结论**——七类可读结论(煤质表征/煤层对比/均匀性/相关性/洗选提质/用途建议/数据质量), 含 Pearson 最强相关挑选、AshWord/SulfurWord 分级(原走参考字典 `_ref.FindLevel`, **无字典→本类兜底 AshWord/SulfurWord**, 即原版自带回退, 忠实)、UtilizationVerdictText 用途判据。命令 `灰分发热量回归`/`煤质综合结论`(GetCoalSamples→算→状态+CSV)。

**验证(已知值)**: [CoalQualityAnalyticsTests](tests/PitMine3D.Kylin.Tests/CoalQualityAnalyticsTests.cs) 8 例—— **Cal=30−0.5·Ad 精确线性 → 斜率−0.5/截距30/r²=1/无离群**、残差离群 z≥2.5 标记(OUT 点)、样本<5 空回归、七类结论齐全、表征含"中灰"(均值Ad=21)+煤层对比"3煤最低/5煤最高"、灰分-发热量负相关检出、空输入安全、CSV 头部。**build 0 错·单测 1182→1190**。

**本会话第 17 功能**。教训: **"全模块读毕"要按子目录逐个核实, 别信笼统断言**——GeoDataBase/Domain/Services/Geology 此前没单独读过; 一个"纯 C# 无外部依赖"分析引擎移了 6/8 方法, 剩 2 处纯统计(回归/结论)正是 present-but-shallow 典型。**元角度: 枚举全部子目录 → 逐个 ls+读, 对已移子系统查方法级完整度**(类比 node-editor 4/11、CoalAnalytics 6/8)。见 [[unlock-blocked-insights]]。

## 二一三、空间估值交叉验证(CoalQualityEstimator.CrossValidate)—— 方法级完整度续(第 18 功能)

承 §二一二 元角度, 续查 GeoDataBase 其余"纯 C#"引擎的方法级完整度: `CoalQualityEstimator`(Kylin 已作 `OrdinaryKriging` 移)—— EstimateAt/EstimateWithVariance(方差, Kylin EstimateAt 已返回)/Interpolate 已覆盖, **但 `CrossValidate`(留一交叉验证)缺**。`VirtualDrillEngine` 对 Kylin `VirtualBorehole` 已覆盖。

忠实补 [src/Cad/SpatialCrossValidation.cs](src/Cad/SpatialCrossValidation.cs): **留一交叉验证(LOO-CV)**——逐点把该点从控制集剔除, 用其余点按 OK/IDW/NN/MA 预测它, 汇总 ME(系统偏差)/MAE/RMSE/R² + (OK 有克里金方差时)标准化误差均值 MSE/方差 MSEVar(≈1 说明方差估计合理)。**OK 核直接复用 `OrdinaryKriging.EstimateAt`**(签名 (points,x,y,z,k,radius,vg) 与原 EstimateOne 完全一致); NN/IDW/MA 内联; 变差函数全体点拟合一次(原注"LOO 极小泄漏但拟合稳定, 通行做法")。命令 `交叉验证 [OK|IDW|NN|MA] [ad|qgr|std|vdaf]`(煤质指标点→CV→ME/MAE/RMSE/R²+CSV)。

**验证(已知值)**: [SpatialCrossValidationTests](tests/PitMine3D.Kylin.Tests/SpatialCrossValidationTests.cs) 6 例—— **常量场全方法零误差**(ME/MAE/RMSE=0)、**NN 留一手算误差**((0,0)V10/(1,0)V20/(5,0)(6,0)V99 → 误差[+10,−10,0,0] → ME=0/MAE=5/RMSE=√50)、点<4 空、OK 产标准化误差(MSE 有值)、光滑线性场 IDW 高 R²+低偏差、CSV 头部。**build 0 错·单测 1190→1196**。

**本会话第 18 功能**。教训: **方法级完整度要遍历子系统的每个"纯 C#"引擎**——GeoDataBase 三个估值/分析引擎(CoalQualityAnalytics/CoalQualityEstimator/VirtualDrillEngine)逐个 method-diff, 挖出 灰分回归/综合结论/交叉验证 三处; 复用已移基元(OrdinaryKriging.EstimateAt)可低成本补高价值验证算法。**GeoDataBase 纯引擎方法级已核毕**。见 [[unlock-blocked-insights]]。

## 二一四、中线交点分类(CenterlineJunctions)—— 全模块纯引擎清单续(第 19 功能)

**元角度续**: 枚举全模块「纯 C#/无外部依赖」引擎(~40 个), 逐个 grep Kylin 覆盖。多数已移(名不同: StructurePavement→RoadSurface, ParametricCenterlines→RampCenterlines, PolylineMetrics→RoadEvolutionAnalyzer——**name-mismatch 假缺口**), 但 `RoadLib.Network.CenterlineJunctions` 是真缺口——Kylin 既有 `RoadNetwork` 拓扑报表**只数 度≥3 节点**, 无四型几何分类。

忠实移植 [src/Cad/CenterlineJunctions.cs](src/Cad/CenterlineJunctions.cs): 把中线里【建网会打节点的位置】按四条建网规则分类——**X 十字**(两段平面内部真相交) · **T 丁字**(一线端点落另一线身上) · **半腰焊**(两段近贴但两脚在半腰) · **接缝**(端点碰端点); **就近合并**累计度数区分【真路口】(X/T/焊/≥3 汇合)与【接缝】(仅两线相接=一条路被打断的缝); 标【立交】(平面相交但高差超闸门, 建网不连通); `Nearest` 取捕捉点(真路口优先于接缝)。原用 SegmentGrid 粗筛, 此处**内联包围盒预筛**(结果一致)。命令 `中线交点 [容差m]`(场景中线→四型分类→按型配色画标记+分类计数)。

**验证(已知值)**: [CenterlineJunctionsTests](tests/PitMine3D.Kylin.Tests/CenterlineJunctionsTests.cs) 7 例—— X 十字内部交(5,5)/立交高差标记/T 端点落身/接缝非真路口(度2)/**三线汇合就近合并成 1 个 3 度真路口**(非 3 接缝, MergedCount=2)/Nearest 真路口优先于接缝+半径外 null/空退化安全。**build 0 错·单测 1196→1203**。

**本会话第 19 功能**。教训: **全模块纯引擎清单交叉核对时, name-mismatch 假缺口成批**(StructurePavement/ParametricCenterlines/PolylineMetrics 都是改名已移)——grep 类名 ✗ 只是线索, 须回读确认语义(RoadSurface=结构路面 ribbon 已覆盖); 真缺口 CenterlineJunctions 与既有 RoadNetwork(只数度)**互补**(四型分类 vs 度计数)。**2D 场景限制**: Z=0 故不判立交(需 3D 中线), 但四型平面分类完整可用, 已记录。见 [[unlock-blocked-insights]]。

## 二一五、趋势整合现状台阶(TrendBenchIntegrator)—— MineAssLib/Driving 续挖(第 20 功能)

纯引擎清单剩余 ✗ 逐个核实: RoadClipRegion(DTO)/RoadConditionSymbology(=纵坡分析分档着色覆盖)/WorkLineProjector·UnitGraph·Incline*·StraightRamp*·TerrainRamp*(引擎中间层/native 斜面) 皆记录; **RoadSkeletonExtractor(875 行)记录为大子系统**——与 Kylin 既有「提取道路中心线」(RoadTools.Centerline: 两路边→中线, 简单法)**是不同方法**(骨架法: 台阶线当挡墙栅格化→DEM→可行驶坡度掩膜→**Zhang-Suen 细化骨架**→走廊图路由), 非覆盖; 但 875 行含**真图纸调校的走廊宽闸门/毛刺修剪阈值**, 端到端本机无真实地形不可验(Zhang-Suen 核虽标准, 但无掩膜生产者则为死码), 属多 tick 专项非清切片, 记录不移(同坑线内核/排产引擎边界)。真缺口 **`MineAssLib/Driving/TrendBenchIntegrator`**(该目录曾出 TautString 月度剥离, "读毕"又漏一处)。

忠实移植 [src/Cad/TrendBenchIntegrator.cs](src/Cad/TrendBenchIntegrator.cs)(245 行, 0 引擎依赖): 用一条趋势线沿其方向整合现状台阶——趋势∩各台阶线(2D 求交)→ 交点取该台阶**整条代表标高**(=平盘水平, 比交点一点稳)→ 按标高 1D 聚级(带宽≈台阶高/2)→ 每级把源台阶段**压平到 z_k** + 贪心**断头接平**成一条规整线; 另 ExtractPlatformLevels/AlongTrend 出平盘标高序列(降序)。高程分布皆来自现状真值, 不放坡造假。命令 `趋势整合台阶 [聚级带宽m]`(选中趋势多段线 + 台阶线 CSV → 规整线按级配色入场景)。

**验证(已知值)**: [TrendBenchIntegratorTests](tests/PitMine3D.Kylin.Tests/TrendBenchIntegratorTests.cs) 6 例—— 竖直趋势穿三级(标高 100/110/120 压平)、**同级两段压平+断头接平成一条(4 点, SourceCount=2)**、无交点降级、无效趋势降级、ExtractPlatformLevels 降序聚级(120.5/110/100)、AlongTrend 只计趋势穿过的级。**build 0 错·单测 1203→1209**。

**本会话第 20 功能**。至此本会话续作(元角度: 逐子目录+全模块纯引擎清单)连补 **9 功能(12–20)**: 快速选择/平盘标高清单/现状参数提取/标注台阶标高/参数校核/煤质分析补全(灰分回归+综合结论)/空间交叉验证/中线交点分类/趋势整合台阶。单测 1010→1209。教训: **"读毕"的目录仍会漏——MineAssLib/Driving 出了 TautString 后又漏 TrendBenchIntegrator; 全模块纯引擎清单(注 纯C#/无依赖)逐个 grep+回读, 是比"逐目录读"更硬的收敛判据**。见 [[unlock-blocked-insights]]。

## 二一六、路段分类拓扑(RoadTopology R-T1/R-T2/R-T3)—— 分析引擎 method-diff 续(第 21 功能)

**分析/报表引擎 method-diff 角度**: 枚举全模块 Analytics/Report/Metrics/Indicator 引擎(0 引擎依赖者), 逐个核 Kylin 覆盖。多覆盖(HaulMetrics 5/5、BlockReportGenerator→Statistics.Describe、ContourEngine/BenchAnalyzer 全), 报表引擎的 HTML/PDF 渲染属文档子系统(记录), 但 `RoadLib.Network.RoadTopology` 是真缺口——Kylin RoadNetworkReportCmd 只做**碎边级**度数计数, 无**路段级**分类。

忠实移植核 [src/Cad/RoadTopology.cs](src/Cad/RoadTopology.cs)(构建于 Kylin `RoadNetwork.Build` 的 (nodes,adj)): **R-T1** 节点按度数 5 类(孤立0/端点1/接缝2/丁字3/多岔≥4, 只有度≠2 是真节点); **R-T2** 碎边压成**路段**(两真节点间顺接缝串边); **R-T3** 路段 3 类(干线=两端都通/支线=一端悬挂/孤立段=两端悬挂, 悬挂=度≤1) + 连通片(并查集) + `DescribeDelta`(两次拓扑增量, 增删边回显)。命令 `路段分类`(场景中线→路段级分类→按类配色画+计数)。**记录(需更富图模型, Kylin 邻接表无)**: 装卸点类型(源汇作端点)、人工改判(R-T7)、可通行过滤(passableOnly)。

**验证(已知值)**: [RoadTopologyTests](tests/PitMine3D.Kylin.Tests/RoadTopologyTests.cs) 8 例—— 简单路径=1 孤立段(两端悬挂)、Y 型 3 支线+1 丁字、十字=多岔、**双路口间干线**(30m·节点序[0,1,2,3])、全接缝三角=孤立环(IsLoop)、连通片计数、DescribeDelta 列变化("路口 0→1")+ 无变化空串、空安全。**build 0 错·单测 1209→1217**。

**本会话第 21 功能**。教训: **分析/报表引擎按 0-引擎依赖逐个 method-diff**——多数覆盖或渲染(文档子系统), 但偶有**碎边级 vs 路段级**这种"present-but-shallow"缺口(既有只做低阶计数, 缺高阶抽象)。构建于**既有 Kylin 输出**(RoadNetwork.Build)之上可低成本落地, 富图模型专属字段(装卸点/人工改判/可通行)缺则记录。与 §二一四 中线交点(交点分类)互补=路网拓扑全景。见 [[unlock-blocked-insights]]。

## 二一七、网格自交诊断(MeshDiagnose SelfIntersect)—— 全插件命令 diff + 受阻复评(第 22 功能)

**全插件 AddButton 命令 diff 角度**: 提原 9 插件全部 `AddButton("名")`(225 条), 逐条 grep Kylin。真 ✗ 86 条经回读分类: 多数覆盖(别名/name-mismatch: 结构路面→RoadSurface、多段线嵌入三角网→Delaunay 约束、闭合线裁剪→LineClip)、**原版 SkeletonCommand 桩(最终并段/分帮扩帮桩等=`[骨架]…待实现`, 忠实**不移**)**、交互对话框(局部台阶/平盘联络道)、引擎/3D/native(排产/坑线/倾斜摄影)。唯 `格网质量检测` 深挖出真缺口。

原 `格网质量检测` = `DiagnoseReport.ParseSafe`(解 **PMDR native 二进制**, 诊断本身 native)。Kylin `MeshDiagnose`(托管重实现)已覆盖 PMDR 的 边界边/非流形/退化/孤立/重复, **唯缺 `SelfIntersect`(自交三角)**。补 [src/Cad/MeshDiagnose.cs](src/Cad/MeshDiagnose.cs): 横切另一**非相邻(不共顶点)**三角计数, 复用既有 `MeshIntersect.TrianglesIntersect`(tri-tri, 新暴露 public), **均匀网格 broad-phase**(AABB 落格 + 去重对), 超 6 万三角返 -1(交 native/BVH)。接入 `网格诊断` 命令输出。

**受阻复评(硬教训)**: 原命令注释曾写"自相交属鲁棒难题受阻记录"——**错**: 混淆了自交**检测**(=tri-tri 测试, Kylin 早有 MeshIntersect, 可托管)与自交**消解**(需鲁棒谓词+重网格, CGAL 级难题, 真受阻)。检测可做, 已补; 消解仍记录。同 #latent≠永久受阻。

**验证(已知值)**: [MeshDiagnoseTests](tests/PitMine3D.Kylin.Tests/MeshDiagnoseTests.cs) +2 例—— 竖立三角穿平面三角内部(无共顶点)=自交 2 · 共边相邻/相隔远/闭合四面体=自交 0。**build 0 错·单测 1217→1219**。

**本会话第 22 功能**。教训: **全插件 `AddButton` 命令 diff 是命令级最全核对**——但 ✗ 须回读三分: ①别名覆盖 ②原版 SkeletonCommand/PlaceholderCommand 桩(忠实不移, 13 个) ③交互/引擎/native。桩命令的识别(`SkeletonCommand`/`PlaceholderCommand`)防了"实现原版根本没实现的东西"。且 **native 二进制诊断(PMDR)的托管重实现要 method(字段)级对齐**——缺的那项(自交检测)复评发现是"检测可做≠消解受阻"。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

## 二一八、网格水平投影足迹(MeshMetrics.HullAreaXY)—— PMxx 结果字段级 diff(第 23 功能)

承 §二一七 自交, 系统枚举原全部 **PMxx native 结果解析器**(ParseSafe, magic 'PMxx'): Diagnose(PMDR ✓补自交)/Repair(PMRR)/Weld(PMWR)/VolumeDiff(PMVD)/Boundary(PMBR)/SplitBySurface(PMSS)/Embed(PMEM)/Intersect(PMIC)/Boolean(PMBO)/Cut。逐个把**原 report record 字段** vs Kylin 重实现产出字段对齐:
- Repair(PMRR 7 修复项): Kylin MeshRepair 覆盖 焊接/朝向/补洞/去退化, **非流形拆分/自交去除原自记内核级不含**(记录); 去孤立点 rebuild-from-tris 隐含。
- Weld/VolumeDiff/Split/Embed/Intersect: Kylin MeshWeld/TwoEpochVolume/MeshPlaneSplit/Delaunay约束/MeshIntersect 覆盖字段。
- Boolean/Cut: mesh 布尔/刀切 CGAL 级受阻(记录)。
- **Boundary(PMBR)**: bbox/宽高/对角(MeshMetrics 已有) + BoundaryLoops(MeshBoundaryLoops 已有) + **`HullArea` 缺**——顶点 XY 投影凸包面积(水平投影足迹)。

补 [src/Cad/MeshMetrics.cs](src/Cad/MeshMetrics.cs) `HullAreaXY`(复用 GeomHull.ConvexHull + 鞋带公式), 接入 `网格度量` 命令输出。**验证**: [MeshMetricsTests](tests/PitMine3D.Kylin.Tests/MeshMetricsTests.cs) +1—— 10×10 方形足迹(+内部点不改凸包)=100 · 退化<3点=0。**build 0 错·单测 1219→1220**。

**本会话第 23 功能**。教训: **PMxx native 结果解析器是"字段级 present-but-shallow"的系统靶场**——每个 report record 的字段=native 引擎产出的完整量, Kylin 托管重实现要逐字段对齐(缺项=补, 如自交/足迹; 内核级项如布尔/非流形拆分=记录)。这是比"类在就算覆盖"更细的一层核对。见 [[unlock-blocked-insights]]。

## 二一九、面约束块体(MeshContainmentTester 4 模式)—— 算法模式 enum 值级 diff(第 24 功能)

**算法/模式 enum 值级 diff 角度**: 枚举原全部 `enum *Mode/*Method/*Kind`(算法变体), 查 Kylin 是否实现全部值(同 SurfaceUpdate NN/IDW/OK/SK/UK 补全)。多数是排产/引擎 enum(记录), 但 `BlockModelLib.MeshContainmentTester :: MeshConstraintMode` 是真缺口——Kylin「约束块体」仅 **2D 闭合多段线内**一种, 原有 **4 模式**(相对开放曲面 上/下、相对闭合网格 内/外)。

忠实移植 [src/Cad/MeshContainment.cs](src/Cad/MeshContainment.cs): 4 模式复用已移基元——面高程 `MineableAreaIdentifier.SampleMeshZ`(点落三角重心插值 Z, 面外 null)判上/下; 闭合内外 `WindingNumberTester.IsInsideClosed`(GWN)判内/外; `Flatten` 把 (verts,tris) 摊平。命令 `面约束块体 上|下|内|外`(选 OFF 约束网格 → 过滤 _lastBlocks 块心 → RenderBlocks 留满足)。典型: 保留地表以下且煤层底板以上=可采带。区别既有 2D 约束块体。

**验证(已知值)**: [MeshContainmentTests](tests/PitMine3D.Kylin.Tests/MeshContainmentTests.cs) 4 例—— 平面 z=5 上/下(面外不留) · 四面体质心内/远点外 · KeepIndices 过滤点表(下留 0/2, 面外弃) · 闭合内过滤。**build 0 错·单测 1220→1224**。

**本会话第 24 功能**。教训: **算法/模式 enum 逐值核对是第四层 diff**(类→命令→PMxx字段→enum值)——原 `enum *Mode` 的每个值=一个算法变体, Kylin 实现该 enum 但可能只覆盖部分值(present-but-shallow by variant, 同 SurfaceUpdate/更新煤层面多算法)。缺的变体常可复用已移基元低成本补(SampleMeshZ+WindingNumber)。见 [[unlock-blocked-insights]]。

## 二二〇、变差函数分析(ComputeExperimentalVariogram)—— 全 public 方法名 diff(第 25 功能)

**第五层 diff: 全模块 public static 方法名穷尽核对**(658 方法名 grep Kylin)。绝大多数缺项=排产/图表/PMBI/DB/UI(记录), 唯 `ComputeExperimentalVariogram` 值得深挖——原经 `VariogramEditor` 交互控件暴露**实验半变异 γ(h) 云**, Kylin `FitVariogram` 内部算了实验变差但**只返回拟合模型, 不暴露 γ(h) 云**。

补 [src/Cad/OrdinaryKriging.cs](src/Cad/OrdinaryKriging.cs) `ExperimentalVariogram`(忠实原: 逐点对按 3D 滞后分箱, γ(h)=0.5·mean((v_i−v_j)²)) + `VariogramLag` 记录。命令 `变差函数分析 [ad|qgr|std|vdaf]`(煤质点 → 实验 γ(h) 表 + 球状拟合块金/基台/变程 + 拟合 γ(h) 对比列, CSV)。原交互 VariogramEditor 为 UI(记录), 但**计算核(实验变差+拟合)纯可托管**, 出表非图。用于建模前看空间相关结构 / 验证克里金拟合(与 §二一三 交叉验证互补=克里金前后诊断)。

**验证(已知值)**: [OrdinaryKrigingTests](tests/PitMine3D.Kylin.Tests/OrdinaryKrigingTests.cs) +2—— 线性场 V=x 共线 4 点: 滞后 d 半变异 γ=0.5·d²(箱1 三对 γ=0.5·1 / 箱2 两对 γ=2 / 箱3 一对 γ=4.5, 空箱 Count=0) · auto maxLag 滞后中心递增 + <2 点全 0 箱不崩。**build 0 错·单测 1224→1226**。

**本会话第 25 功能**。教训: **第五层 diff=全 public 方法名穷尽 grep**(类→命令→PMxx字段→enum值→方法名)。658 方法名里绝大多数缺项落排产/图表/PMBI/DB/UI 记录类, 但偶有"内部算了没暴露"的分析核(实验变差)——`FitVariogram` 内部分箱算 γ(h) 却只吐模型, 暴露 γ(h) 云=纯托管新分析。**交互控件(VariogramEditor)是 UI 记录, 但其计算核可出表**(同 Weibull "图表受阻但数值核可做")。见 [[unlock-blocked-insights]]。

## 二二一、圈范围算量补深度+投影面积 —— 点云 native 结果字段级 diff(第 26 功能)

**PMxx 字段 diff 补漏: 之前只做 MeshEditLib 的 PMxx report, 漏了 PointCloudLib 的一整套 native 结果解析器**(CloudOpResult 族: PMQS/VolumeToBase/VolumeInPolygon/SurfaceAttrib/SlopeLines…)。逐字段核对: 多数覆盖(抽稀/去噪/PMF/裁剪/质量/粗糙度/曲率), 但 `VolumeInPolygonResult` 有 Kylin「圈范围算量」缺的两字段——**Depth(顶部 N 米方量)** 与 **ProjectedArea(XY 投影面积)**。

补: [src/Cad/TerrainAnalysis.cs](src/Cad/TerrainAnalysis.cs) `PolygonAreaXY`(鞋带公式, 纯可测) + 扩「圈范围算量」命令支持可选深度(`圈范围算量 <深度>` → 基准=zmax−深度, 只算顶部 N 米, 忠实原 Depth 语义; 缺省仍 zmin) + 报投影面积。复用已测 `VolumeWithinBoundary`。

**验证**: [TerrainAnalysisTests](tests/PitMine3D.Kylin.Tests/TerrainAnalysisTests.cs) +1—— 10×10 方形=100 · 三角(0,0)(4,0)(0,3)=6 · <3点=0。**build 0 错·单测 1226→1227**。

**本会话第 26 功能**。教训: **PMxx 字段 diff 要覆盖每个模块的 native 结果族, 别只做一个模块**——MeshEditLib 的 PMxx 做了, PointCloudLib 的 CloudOpResult 族漏了; 补做挖出 圈范围算量 的深度+投影面积两字段。**命令级"覆盖"仍要字段级复核**(圈范围算量在, 但缺 Depth/Area 两输出)。见 [[unlock-blocked-insights]]。

## 二二二、离散化模型(封闭网体素化成块体)—— 命令 ✗ 误分类纠正(第 27 功能)

**纠正命令 diff 的误分类**: `离散化模型` 曾在全插件 ✗ 里被我归"块体/地质引擎"(记录), **实为标准几何操作**——原描述"把采矿模型体素化成块体:选封闭三角网体"。是**体素化**(voxelize), 非 native 引擎; 且 Kylin 刚好有精确基元 `WindingNumberTester.IsInsideClosed`(GWN)。`固化成体`(反向)Kylin 已有(SolidifyAsync); `离散化`(正向)缺。

忠实补 [src/Cad/MeshVoxelizer.cs](src/Cad/MeshVoxelizer.cs): 封闭三角网包围盒内布规则格, 格心落**闭合网内**(GWN)保留成块体; 复用 WindingNumberTester; 格数上限守卫(超则拒, 提示加大块尺寸)。命令 `离散化模型 [块尺寸]`(选封闭 OFF → 先诊断闭合性 → 体素化 → 产块体模型入 `_lastBlocks` 供资源量/剥采比复用; 缺省块尺寸=包围盒对角 1/40)。

**验证(已知值)**: [MeshVoxelizerTests](tests/PitMine3D.Kylin.Tests/MeshVoxelizerTests.cs) 4 例—— **10 立方体 @格 2 → 5×5×5=125 块**(格心 (1,3,5,7,9)³ 全在内) · @格 5 → 2³=8 块 · 格数过大守卫拒 · 退化/cellSize=0 安全。**build 0 错·单测 1227→1231**。

**本会话第 27 功能**。教训: **命令 diff 的 ✗ 分类要复核"是标准几何还是真引擎"**——`离散化模型` 名义在"采矿模型"组像引擎, 实为纯体素化(GWN 内外判), 我初判"引擎记录"是**误分类**。判"引擎/native 受阻"前问"这操作的算法标准吗?有无已移基元支撑?"(WindingNumber 已移 → 体素化可做)。同 #13"别被'内核模块'标签吓退标准算法"。见 [[unlock-blocked-insights]]。

## 二二三、虚拟钻孔 2D 柱状预览 —— present-but-shallow(算了没画, 第 28 功能)

续 ✗ 再分类角度, 复查"钻孔/柱状"类: `原始钻孔柱状图`(单孔 2D 地层柱状图)Kylin **已有**(展绘钻孔 → `BoreholeRender.BuildColumns`)。但 **`虚拟钻孔`** 原描述"出 2D 柱状预览", Kylin `VirtualDrillAsync` **只出文字+CSV, 不画柱状** —— 典型 present-but-shallow(算了顶/底板但没画预览)。

补 [src/Cad/BoreholeRender.cs](src/Cad/BoreholeRender.cs) `BuildVirtualColumn`(SeamHit 顶/底板标高 → 岩柱中轴 + 每煤层按标高映射深度矩形[稳定配色] + 深度刻度 + 煤层号/厚度标注; 顶板最高者作孔口深度0)。接入 `虚拟钻孔` 命令: 求交后在孔位画 2D 柱状预览(图层 虚拟钻孔柱状)。

**验证(已知值)**: [VirtualBoreholeTests](tests/PitMine3D.Kylin.Tests/VirtualBoreholeTests.cs) +2—— 煤3(顶100/底98)→深度矩形[0,−2]、煤5(顶90/底87)→[−10,−13] + 岩柱中轴到 −13 · 空安全。**build 0 错·单测 1231→1233**。

**本会话第 28 功能**。教训: **✗ 再分类要连"present-but-shallow 的命令"一起复查**——`虚拟钻孔`命令在且算对了(顶/底板求交), 但漏了原有的可视化产出(2D 柱状预览)。命令存在 ≠ 输出完整; 有数据+2D 可画的可视化(柱状/剖面/标注)常是"算了没画"的浅坑。见 [[unlock-blocked-insights]]。

## 二二四、煤质离群/符合性 上图定位 —— "算了没画"续(空间可视化, 第 29 功能)

续"算了没画"角度查分析命令: `煤质离群`/`商品煤符合性` 都**只出文字**, 但原「超标段带坐标可上图定位」——离群/超标样点带 X,Y 应上图。补空间标记:
- [src/Data/CoalAnalytics.cs](src/Data/CoalAnalytics.cs) `OutlierCoords`(离群行按 Id 关联样点坐标, 纯可测); `SampleEval` 本就带 X,Y。
- `煤质离群`: 离群样点画圆(偏高红/偏低蓝, 大小随严重度) + FitBounds。
- `商品煤符合性`: 逐评估样点画点(超标红叉/达标绿) + FitBounds。

**验证**: [CoalQualityAnalyticsTests](tests/PitMine3D.Kylin.Tests/CoalQualityAnalyticsTests.cs) +1—— 6 正常+1 偏高(99)离群 → OutlierCoords 关联到 (42,43)·偏高; null 安全。**build 0 错·单测 1233→1234**。

**本会话第 29 功能**。教训: **"算了没画"不止柱状/剖面, 还含"分析结果的空间定位"**——离群/超标/异常样点带坐标却只报文字, 上图定位(点/圆标记+FitBounds)是标准空间 QA, 2D 可画。分析命令复查问"结果有坐标吗?有则该上图"。见 [[unlock-blocked-insights]]。

## 二二五、直线斜坡道中线 —— 补齐坑线中线家族(第 30 功能)

**再分类续 + 家族补齐**: Kylin `RampCenterlines` 有 Spiral/Switchback, **缺 Straight**; 原 `StraightRampAutoRouter`(824 行)**0 引擎依赖**(纯), 我曾误记"引擎"。但 824 行是**全套可行性路由**(逐级可行性/缓坡段/回头平台判定, 需台阶线带 Z=内核规模)。**拆**: 直线**中线几何**(从起点沿方位角匀降)是清切片, 补; 全套路由器记录(大)。

补 [src/Cad/RampCenterlines.cs](src/Cad/RampCenterlines.cs) `Straight`(起点+方位角+纵坡+长度→匀降折线, 与 Spiral/Switchback 同族)。命令 `直线斜坡道 [纵坡% 长度 方位°]`(视图中心生成绿中线)。坑线中线家族(螺旋/折返/直线)补齐。

**验证(已知值)**: [RampCenterlinesTests](tests/PitMine3D.Kylin.Tests/RampCenterlinesTests.cs) +2—— 起(0,0,100)方位0°纵坡10%长50步10 → 6 点·末(50,0,95)降5m·中点z98 · 方位90°末点在Y轴 · 无效空。**build 0 错·单测 1234→1236**。

**本会话第 30 功能**。教训: **大算法(824行路由器)里的"中线几何核"是清切片, 可与全套路由器分开移**——直线中线(匀降折线)是清几何, 全套可行性路由(逐级判定+3D台阶线)是内核规模; 拆核移、路由器记录, 补齐 Spiral/Switchback/Straight 家族。同 StandardLevelModel 拆"煤岩判定核(移)vs 配对归级(记录)"。见 [[unlock-blocked-insights]]。

## 二二六、变差函数三模型 + 自动选型 —— 模型家族补齐(第 31 功能)

**模型家族 diff**: 原 `EstimationAlgorithms.Gamma(modelType,...)` 支持 **球状/指数/高斯** 三型变差函数; Kylin `OrdinaryKriging.Variogram` **仅球状**。不同矿床空间相关形状各异, 三型是标准地统计选项。

补 [src/Cad/OrdinaryKriging.cs](src/Cad/OrdinaryKriging.cs): `VariogramModel` 枚举 + `Variogram` 加 `Model` 字段(**默认球状, 向后兼容——既有构造/FitVariogram/克里金不变**) + `Gamma` 按型分支(指数 1−e^(−3h/a)·高斯 1−e^(−3(h/a)²), 忠实原公式); `SelectVariogramModel`(FitVariogram 估 nugget/sill/range → 三型各评对实验 γ(h) 残差平方和 → 取最小)。命令 `变差函数分析` 改报最佳模型 + 三型 SSE。

**验证(已知值)**: [OrdinaryKrigingTests](tests/PitMine3D.Kylin.Tests/OrdinaryKrigingTests.cs) +2—— nugget0/sill10/range100 @h=50: 球状6.875·指数7.7687·高斯5.2763; 球状 h≥range=sill 而指数/高斯渐近<sill; 默认型=球状(向后兼容); SelectVariogramModel 选出型 SSE 三者最小。**build 0 错·单测 1236→1238**(既有克里金测试不受影响——默认球状保底)。

**本会话第 31 功能**。教训: **枚举/switch 的"算法模型家族"要逐型核**——原 `Gamma` switch 三型, Kylin 只球状; 补齐指数/高斯 + 自动选型。**加模型字段用默认值保向后兼容**(既有球状路径不变), 避免改动波及既有克里金测试。同"算法变体 enum 值级 diff"(SurfaceUpdate/MeshContainment), 但这里是 γ(h) 数学模型族。见 [[unlock-blocked-insights]]。

## 二二七、反距离权重 IDW 一等估值器(可配幂次/邻域/平滑)—— 估值家族"命令在但算法浅"(第 32 功能)

**估值家族 diff**: 原 `EstimationAlgorithms.IdwEstimator` 是**一等、用户可选**的估值算法(与克里金并列, 见 `KrigingViewModel`/`QuickEstimateViewModel` 的 "IDW" 选项), 带 **可配幂次 power·搜索半径·邻域 [minSamples,maxSamples]·平滑项 smoothing·3D 距离·零距离精确插值**。Kylin `IDW估值` 命令**已在**(分派 815 行), 但底层走 `Contour.GridFromPoints`——**固定 power=2 的 2D 网格填充**, 无可配幂次/邻域/平滑。属"命令在但算法浅"(present-but-shallow), 非缺命令。

补 [src/Cad/OrdinaryKriging.cs](src/Cad/OrdinaryKriging.cs): `IdwEstimate(points,x,y,z, power, radius, minSamples, maxSamples, smoothing)`——忠实原 `IdwEstimate`: 3D 距离半径裁剪 → 排序裁到 maxSamples 后比 minSamples(忠实原序) → 零距离(d<1e-9)精确返回样本值 → `w=1/(d+smoothing)^power` 加权均值; 邻域不足/权和≤0 返回 null。并入克里金家族(与 OK/UK/SK 同 `ControlPoint` 结构、同 nullable 约定)。UI 端 [MainWindow](src/Views/MainWindow.axaml.cs): 新 `BuildIdwGrid`(逐格 IdwEstimate, 取代固定 power=2 路径), `EstimateGradeAsync` 加 `idwPower` 参; 分派 `IDW估值 <幂次>`/`快速估值 <幂次>` 解析可选幂次(Kylin 命令行 idiom 对应原对话框 power 字段), 状态行报幂次+邻域均样本数。

**验证(已知值)**: [OrdinaryKrigingTests](tests/PitMine3D.Kylin.Tests/OrdinaryKrigingTests.cs) +6—— 零距离精确/单点回值; 四点等距值10/20/30/40→均25; 两点(0,0,0)&(100@10,0) 查(2,0,0) power1→20 精确, power2→5.882(近点更压倒), 平滑100→47(趋均值50); 半径外/邻域不足/空→null; maxSamples=2 只取最近两点不被远离群 999 污染。**build 0 错·单测 1238→1244**(既有估值不受影响)。

**本会话第 32 功能**。教训: **"命令已在"≠"算法已全"**——`IDW估值` 命令存在且能出图(固定 power=2), 但原算法是可配幂次的一等估值器; 逐个已移植命令核对其底层算法深度(present-but-shallow), 而非只看命令名 diff。IDW 是克里金的标准同伴(无需变差函数、快速稳健), 补齐后估值家族(OK/UK/SK/IDW/NN/MA)对齐原六法。见 [[unlock-blocked-insights]]。

## 二二八、球状变差 LSQ 拟合 FitSpherical(网格搜索最小二乘)—— 拟合器"简化替身"深挖(第 33 功能)

**同文件续挖(present-but-shallow 最隐蔽变体)**: 顺 IDW 同一原文件 `EstimationAlgorithms.cs`, 其类头自陈提供 **`VariogramFitter 最小二乘拟合 Spherical 模型参数`**。原 `FitSpherical(exp, sampleVariance)` 在 **nugget/sill/range 粗网格(7×10×12)** 上扫**点对数加权残差平方和** Σ cnt·(γ_model(h)−γ_exp(h))², 取最小——真 LSQ 拟合。Kylin `FitVariogram` 是**矩法启发式替身**: sill=样本方差·range=实验变差首达 0.95sill 的滞后·nugget 由首箱估——**一遍矩估计, 非按 SSE 拟合**。同名功能(拟合球状变差)但底层是更简算法。

补 [src/Cad/OrdinaryKriging.cs](src/Cad/OrdinaryKriging.cs): `FitSpherical(IReadOnlyList<VariogramLag> exp, double sampleVariance)` 忠实原网格搜索(nugget∈[0,0.3·maxG]·sill∈(nug,1.5·max(maxG,方差)]·range∈(0,maxH], 点对数加权 SSE 取最小; <3 非空箱退 nugget0/sill=方差/range100) + 便捷重载 `FitSpherical(pts)`(自算实验变差+样本方差)。**`SelectVariogramModel` 基准拟合改用 `FitSpherical`**(此前用矩法 `FitVariogram`)——三型选型建于真 LSQ 拟合之上, `变差函数分析` 命令报的最佳模型+SSE 更实。**稳妥边界**: 默认逐格克里金仍用快 `FitVariogram`(矩法, 保既有克里金值测试不动——同 §二二六"默认值保向后兼容"纪律); LSQ 拟合器作为一等方法提供并用于模型选型。

**验证(已知值)**: [OrdinaryKrigingTests](tests/PitMine3D.Kylin.Tests/OrdinaryKrigingTests.cs) +3—— 造 γ(nugget0,sill10,range60) 精确取样的实验变差 → FitSpherical 网格分辨率内还原(range∈[50,70]·nugget≤2·sill∈[8,13]) **且加权 SSE < 故意错变差(5,20,10)**(证真在最小化 SSE); <3 非空箱退兜底(nugget0/sill=方差7/range100); 点集重载出合法参(sill>0·range>0·nugget∈[0,sill]·默认球状)。**build 0 错·单测 1244→1247**(SelectVariogramModel 换基准拟合无回归——全 1247 绿)。

**本会话第 33 功能**。教训: **同一原文件挖到一个"简化替身"后, 通读该文件其余 public 算法**——IDW 之外, 同文件的 VariogramFitter(LSQ)亦被 Kylin 矩法启发式替身顶替。"拟合/估值/求解"这类词的同名方法, 底层算法可能是**降级替身**(矩法 vs 网格搜索 LSQ)。**改核心拟合器守稳妥**: 新增真 LSQ 器 + 只在模型选型路径切换(改善用户可见分析), 默认克里金保矩法快拟合不动, 避免波及既有克里金值测试。见 [[unlock-blocked-insights]] (H)。

**边界记录(忠实): 资源量分类 331/332/333 = 原版自身桩, 不移**。续读估值编排器 `EstimationEngine.cs` 见其 `EstimationResult` 有 `TanMing/KongZhi/TuiDuan Blocks`(探明331/控制332/推断333 = 国标 Measured/Indicated/Inferred 资源量置信分类), 表面是标准可移的地统计特性(按克里金方差/工程控制度分级)。**但原版 203-204 行显式桩**: `// 资源量分类(331/332/333)需逐块体真实工程控制度判定, 非按比例拍——暂不输出假数据, 留待真实实现` → `TanMing=KongZhi=TuiDuan=0`。原作者**刻意未实现**(留待), 故**忠实不移**(同 SkeletonCommand/DelineationMethod config-only 桩纪律——见 §二一九、[[faithfulness-only-original-commands]])。EstimationEngine 其余产出(CellValues/CellVariance/网格/mean-std-min-max/煤质超限跳过 HighAsh>40%·LowCalorific<20·HighSulfur>2%)已由 BuildKrigingGrid + Statistics + CoalAnalytics 合规覆盖。**判据复盘**: 见"标准地统计特性"先别急着补——先查原版**是否真实现**(grep 到零输出+"留待真实实现"注释即桩); 这是"present-but-shallow 深挖"撞上"忠实不发明"的正确交汇——诱人特性 + 原版桩 = 记录不建。**估值/地统计簇至此完整**: 原实现者全移(IDW/FitSpherical/OK-SK-UK/NN-MA/变差实验-三模型-选型/交叉验证 + Krige 部分主元解+IDW 兜底已忠实), 原留白者(331/332/333)忠实记录。

## 二二九、GB/T 5751 煤类反推 + 一致率 QC —— 纠正误记"已覆盖"(第 34 功能)

**纠错**: 此前把 `煤种分类` 记为"已覆盖(coal_classification DB 字典查表)"——**误判**。Kylin 的 `煤种分类` 只**显示分类字典** + `煤种分布` 只按**标注** `coal_type` 分组(读存量标签); 原版另有 `CoalReferenceService.ResolveCoalType`——**从实测 Vdaf/G(粘结指数)/Y(胶质层) 反推煤类**的规则算法(类头明标"GB/T 5751 反推等'纯逻辑'委托 ICoalReferenceService"), 且被 CoalQuality 审核用作 **"煤类反推一致率"** QC(反推 vs 标注比对)。**读标签 ≠ 从指标反推**——Kylin 缺后者。

**可做且可验的关键**: Kylin `coal_classification` 表**已种子全 16 类 GB/T 5751 区间**(code+vdaf_min/max+g_min/max+y_min/max, 如 WY1 Vdaf0-3.5·PM Vdaf10-20/G0-5·JM Vdaf18-28/G50-65/Y≥7…)——**数据齐, 只缺算法**。`CoalSample` 亦已带 CakingG/PlasticYMm/CoalType。补:
- [src/Data/CoalTypeInference.cs](src/Data/CoalTypeInference.cs) 纯算法(忠实原 `ResolveCoalType`/`FindLevel`): `ResolveCoalType(vdaf,g,y,ranges)` 三维区间 [min,max) 半开匹配(min/max=null 即 ±∞, G/Y 样本未提供则跳过该维, 首命中胜) · `FindGradeLevel(value,rules)` 单指标分级 · `InferConsistency(samples,ranges)` 逐样反推+比对标注→一致率(分母=两者都有的样本, 缺 Vdaf/反推 null/缺标注 → 无法判定)。**区间阈值表由 DB 喂入(不臆造国标表值)**, 只移可验证的匹配算法。
- [src/Data/GeoDataQueries.cs](src/Data/GeoDataQueries.cs) `GetCoalClassificationRanges`(读种子三维区间)。命令 `煤类反推`/`煤类一致率`([MainWindow](src/Views/MainWindow.axaml.cs) `CoalTypeInferCmd`): DB 煤样+DB 区间→逐样反推+一致率 QC + **不一致样红/一致绿/未判灰上图定位** + 导出 CSV。三路可发现 + 目录。

**验证(已知值)**: [CoalTypeInferenceTests](tests/PitMine3D.Kylin.Tests/CoalTypeInferenceTests.cs) +7—— Vdaf 单维+半开边界(Vdaf10 出WY入PM·Vdaf37 出QM入CY)·G/Y 三维(Vdaf25 G70 Y10→JM 而 Y3→null·G10 落 PM/SM 空档→null)·缺Vdaf/空表→null·**重叠区间首命中胜**·分级半开·一致率(2/3=66.67%, 无法判定计数)·useClean 切浮煤 Vdaf 改反推。**build 0 错·单测 1247→1254**。

**本会话第 34 功能**。教训: **"已覆盖"的记录也要复核语义粒度**——`煤种分类` 命令在、能显示字典、能按标注分组, 但"**从实测指标反推**"(GB/T 5751 纯逻辑规则)这一算法维缺失; "读存量标签" 与 "算法反推标签" 是两回事(同 present-but-shallow, 但这里是我**自己误记为已覆盖**)。**可做判据**: 算法(区间匹配)可移可验 + 数据(分类区间)已种子在库 → 补; 精确国标表值属 DB 数据(不臆造, 已在种子)。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

## 二三〇、煤质数据审核(一键 N 类规则 QC)—— DB 服务层"纯逻辑"续挖(第 35 功能)

**同脉络续挖**: 反推所在的 DB 服务层(`CoalQualityService`)另有 **`RunAudit`——"7 类规则一键审核"**(GeoDataBasePlugin 明列), 规则逻辑是**纯的**(写库仅落 finding)。逐规则对 Kylin 数据模型核可支撑性:
- **可做(数据齐)**: ② 物理范围(St∈[0,10]·Ad∈[0,60]·Qnet≤50) · ③ 原煤vs浮煤(浮煤灰>原煤灰=物理不可能) · ④ 煤类反推≠标注(复用 §二二九 `CoalTypeInference`) · ⑤ 同层离群(Ad 3σ, 层样本≥30 才统计) · ⑦ 浮煤回收率∈[0,100]。
- ~~**数据缺, 记录不做**: ① 工分自洽~~ **→ 亦误记, 已补(见 §二四六)**: `coal_sample` **本有 mad_raw/ad_raw/vdaf_raw/fcd_raw 列**(Kylin `CoalSample` 记录未映射, 但表有), 我看记录没有就记"列缺"; 种子 0 行四项俱全(数据未灌, 非 schema 缺, 同 blast_event), 已补可验证审核。~~⑥ 钻探-测井煤厚一致~~ **→ 实为误记, 已补(见 §二四五)**: `borehole_seam_result` 本有 `drill_seam_thickness`/`log_seam_thickness`(317 行俱全), 我未查就记"数据缺"。

## 二四五、测井一致审核(钻探vs测井煤厚)—— 纠正误记为"数据缺"(第 54 功能)

**"记录为数据缺"也要复查数据在不在(同 §二二九 煤种分类误记"已覆盖")**: §二三〇 我把 RunAudit 规则6(钻探-测井煤厚一致)记为"数据缺, 不做"——**未查就记**。实则 `borehole_seam_result` 有 `drill_seam_thickness`+`log_seam_thickness`+`drill/log_end_depth`(**317 行俱全**)。忠实补 [CoalAudit](src/Data/CoalAudit.cs) `CheckDrillLogConsistency`(厚煤层≥3.5m 看相对误差>20%错/>10%警; 薄煤层看绝对>0.5m错/>0.25m警) + [GeoDataQueries](src/Data/GeoDataQueries.cs) `GetDrillLogRows`(join borehole 取 hole_id) + 命令 `测井一致`([MainWindow](src/Views/MainWindow.axaml.cs) `DrillLogConsistencyCmd`, 不一致率+导 CSV)+ 目录。煤质审核状态行更正为"测井一致见「测井一致」命令"。

**验证(已知值)**: [CoalAuditTests](tests/PitMine3D.Kylin.Tests/CoalAuditTests.cs) +1—— 厚10/10一致→无·10/8(相对20%=阈值不>20)→警·10/7(30%>20)→错·薄1/1.2(0.2<0.25)→无·1/1.4→警·1/1.6→错·缺→跳: 共 4 项(2错2警)。**build 0 错·单测 1300→1301**。

**本会话第 54 功能**。教训: **"记录为数据缺/受阻"务必先查数据/实现在不在**——这是本会话第 3 次"未查就记"被纠(煤种分类误记已覆盖 §229·测井一致误记数据缺 §245·多段/退距误记复杂 §51/52)。**任何"记录"前, grep/PRAGMA 查证据**(表列/公式/实现), 别凭印象。见 [[unlock-blocked-insights]] [[verify-seed-enum-values-before-filter]]。

## 二四六、工分自洽审核(原煤 M+A+V+FC≈100%)—— 纠正误记"列缺"(第 55 功能)

**同 §二四五, CoalAudit 另一条误记的规则1**: §二三〇 记"工分自洽 需 Mad/FCd, CoalSample 无此列"——**看 C# 记录没映射就断表也没有**。PRAGMA 查得 `coal_sample` **本有 mad_raw/ad_raw/vdaf_raw/fcd_raw**(原煤+浮煤俱全); 种子 0 行四项俱全(数据未灌, 非 schema 缺, 同 blast_event 只运行时导)。补 [CoalAudit](src/Data/CoalAudit.cs) `CheckProximateConsistency`(|M+A+V+FC−100|>3%错/>1%警) + [GeoDataQueries](src/Data/GeoDataQueries.cs) `GetProximateRows`(四项俱全者才返) + 命令 `工分自洽`([MainWindow](src/Views/MainWindow.axaml.cs) `ProximateConsistencyCmd`, 自洽率+CSV)+ 目录。**原 7 规则审核至此 Kylin 全覆盖**(Run 5 条 + 测井一致 + 工分自洽)。

**验证(自造已知值)**: [CoalAuditTests](tests/PitMine3D.Kylin.Tests/CoalAuditTests.cs) +1—— 和100→无·102(偏2%>1)→警·104(偏4%>3)→错·缺FC→跳: 共 2 项(1错1警)。**build 0 错·单测 1301→1302**。

**本会话第 55 功能**。教训: **C# 记录字段 ≠ DB 表列**——记录未映射某列不代表表无该列; 记"列缺"前 PRAGMA 查表。这是"未查就记"的又一变体(第 4 次): 别拿 Kylin 的模型投影当 DB schema。种子无数据(0 行)≠不可做——列在即可移可验(synthetic/运行时数据), 同 blast_event。见 [[unlock-blocked-insights]]。

补 [src/Data/CoalAudit.cs](src/Data/CoalAudit.cs): `Run(samples, ranges, minSeamForOutlier=30)` 纯逻辑跑 5 规则→`Finding(样本id/类别/严重度/消息)` + 分类计数汇总 + `ToCsv`。命令 `煤质审核`/`一键审核`([MainWindow](src/Views/MainWindow.axaml.cs) `CoalAuditCmd`): DB 样本+区间→审核 + **有错样红/仅警样橙上图(按样聚合取最重级)** + 导出 + 状态行报分类计数 & 明示"工分自洽/测井一致数据缺未审"。与既有 `煤质离群`(专项 3σ)/`煤类反推`(专项)互补=统一一键 QC 卷。

**验证(已知值)**: [CoalAuditTests](tests/PitMine3D.Kylin.Tests/CoalAuditTests.cs) +7—— St15/Ad70/Qnet60 越界=Error·浮煤灰22>原煤灰15=Error·反推 CY≠标注 QM=Warning·无区间跳过规则④·yield 120/−5 越界·34 正常+1 极端(Ad200)同层 3σ 命中且阈值提到 40(层 35<40)不统计·汇总错/警计数+CSV 行数。**build 0 错·单测 1254→1261**。

**本会话第 35 功能**。教训: **DB 服务层(CRUD)里夹的"纯规则/纯逻辑"要逐条按 Kylin 数据模型核可支撑性**——7 规则里 5 条数据齐(移)、2 条缺字段(Mad/FCd/测井厚, 记录)。**逐规则拆 doable/blocked, 别整包判死也别整包硬吞**(同 §二二八 331/332/333 拆边界纪律)。DB 服务层"纯逻辑"(ResolveCoalType/RunAudit)连出两个功能, 印证 [[unlock-blocked-insights]] "算法在 CRUD 服务里"角度高产。见 [[faithfulness-only-original-commands]]。

## 二三一、煤质三维体素插值(IDW 块模型)—— DB 服务层第三个纯算法(第 36 功能)

**同脉络第三挖**: GeoDataBase 服务层 `DefaultIdwInterpolation`(CoalQualitySpatialWindow 的插值引擎, 类注"纯 C# 约 50 行")——把散点样本(x,y,z,值)按 **3D IDW** 插到规则**体素网格**, 搜索半径外(最近样本超半径)体素跳过不外插。Kylin 品位估值 `EstimateGradeAsync` **仅 2D(z=0 单层)**, 无三维体素场。**关键忠实细节**: 原 `DefaultIdwInterpolation` 半径语义与 `EstimationAlgorithms.IdwEstimate`(§二二七)**不同**——只判**最近点**是否入半径再取 k 最近(含半径外者), 非把全部点滤到半径内; 故按 DefaultIdwInterpolation **原样内联**, 不复用 §二二七 的 IdwEstimate。

补 [src/Cad/QualityVoxelInterp.cs](src/Cad/QualityVoxelInterp.cs): `Interpolate(points, bbox, resolution, power, k, radius, maxVoxels)` 忠实原(3D 网格逐体素排序取 k 最近·最近超半径跳过·落样本上精确·1/d^p 加权·自动半径 2.5×平均点距≥1.5×步长)+ `ZSlice`(取 Z 切片)+ `ToCsv`(块体模型)。命令 `煤质三维插值`/`品位块模型`([MainWindow](src/Views/MainWindow.axaml.cs) `QualityVoxelInterpCmd`): DB 煤样(x,y,z=z_sample, 指标 ad/vdaf/std/qnet)→3D 体素场→**导块模型 CSV + 取最密 Z 切片上 2D 彩格(蓝低→红高)**。三路可发现 + 目录。**记录**: **全 3D 体素显示受阻**(2D 场景无 per-vertex Z), 故出块模型 CSV + Z 切片(忠实降级, 同其它 3D→2D)。

**验证(已知值)**: [QualityVoxelInterpTests](tests/PitMine3D.Kylin.Tests/QualityVoxelInterpTests.cs) +6—— 四角线性场 V=x: 落样本精确(0/10)·中心等权=5·x=5 线=5·全 9 格有支撑; 半径 3 裁剩四角; 两层 z 分值 k=4 各层中心 5/105(z 维分层); 空点/过密(2001³)防爆空; k=2 挡远离群 999; CSV 头+行数。**build 0 错·单测 1261→1267**。

**本会话第 36 功能**。教训: **同一 DB 服务层连出 3 纯算法(ResolveCoalType/RunAudit/DefaultIdwInterpolation)**——"算法藏在 CRUD 服务里"是本会话最高产角度。**同族不同实现要逐个核语义**: 两处 IDW(EstimationAlgorithms vs DefaultIdwInterpolation)半径语义不同, 忠实各自内联而非强复用(名同实异, 同"文件名≠类名/中文串≠token"的名字误导family)。**计算可做 + 显示受阻 → 出计算产物(CSV/切片)记录显示限**(忠实降级)。见 [[unlock-blocked-insights]]。

## 二三二、创建三角网建 2.5D 面(保留高程 + 导 OFF)—— "算了没存/丢了 Z"补全(第 37 功能)

**present-but-shallow(computed-but-not-saved 变体)**: 对比原 `QuickModelBuilder`(等高线→插值面→固化成体)见 Kylin `创建三角网`(CreateTinAsync)**丢高程且不出面**——只用 `pts2d`(x,y)做 Delaunay、只 `BuildEdges` 画**三角边线框**入场景, **弃掉每点 z、不装 2.5D 面、不导 OFF**。故 Kylin 无法把 (x,y,z) 散点/等高线顶点变成**可复用曲面**(喂 快速建模/算量/分析), 断了原"等高线→面→体"链。既有基元齐备(Delaunay 定拓扑 + MeshWeld.ToOff 写面)。

补 [src/Cad/TinSurface.cs](src/Cad/TinSurface.cs): `Describe(verts3d, tris)` 出 2.5D 面统计(顶点/三角数·XY 投影面积[各三角鞋带和]·高程范围)。改 [CreateTinAsync](src/Views/MainWindow.axaml.cs): 同序装 `pts3d`(保留 z)→ Delaunay(XY 拓扑)→ **`MeshWeld.ToOff(pts3d, tris)` 导 OFF 面**(2.5D, 顶点带各自高程)+ 报投影面积/高程范围; 边线框显示照旧。产物可喂 快速建模/圈范围算量/台阶面提取等 OFF 消费命令。

**验证(已知值)**: [TinSurfaceTests](tests/PitMine3D.Kylin.Tests/TinSurfaceTests.cs) +3—— 10×10 方(高程 0/0/5/5)→2 三角·XY 投影面积恒 100(与对角线无关)·z∈[0,5]; 空面安全; **点→ToOff→ParseOff 往返**保顶点/三角数 + (10,10)顶点 z=106 保真(非丢 0)+ 投影面积 400。**build 0 错·单测 1267→1270**。

**本会话第 37 功能**。教训: **"computed-but-not-saved" 再添一形态——不只"算了没画/没上图", 还有"建了三角网却丢 Z、只画线框不出可复用面"**。查已移几何命令是否**产出可下游复用的产物**(OFF 面/实体), 而非止于屏上线框。此补打通"散点/等高线→2.5D 面→体/算量"链(原 QuickModelBuilder 的面构造那半, Kylin 此前只有"OFF 面→体"下半)。见 [[unlock-blocked-insights]] [[shell-completeness-priority]]。

## 二三三、PMB 块体持全属性(无重导切换/属性报告)—— Kylin 自身"暂未"补全(第 38 功能)

**新角度: 查 Kylin 自身的"暂未/未实现"标记**(非原版桩)。grep `TODO|未实现|暂不|占位` 排除"对原版桩的描述"后, 命中 [MainWindow](src/Views/MainWindow.axaml.cs):4632 `_blockAttrs = null; // PMB 暂未持全属性`——**Kylin 自陈未完成**: BLK 导入持全属性(`AllAttrs`)供**无重导切换活动属性**(`切换属性 <名>`)+ 属性报告, 但 PMB 导入置 `_blockAttrs=null`, 故 PMB 块体无法在位切换属性(须 `导入PMB <名>` 重导)。

**关键: PMB reader 已扫全属性名+值偏移(attrValueOff), 只是仅读选定那份**——补易。改 [PmbImportService](src/Cad/PmbImportService.cs): Result 加 `AllAttrs` 字典; 追踪 `validAttrs`(valueCount==blockCount 的属性), 读**全部有效属性**逐块值入 AllAttrs(巨模型 值数>50M 跳过保内存, 仅读选定作品位——诚实降级); grade 直接取 AllAttrs 中选定份。[MainWindow](src/Views/MainWindow.axaml.cs): PMB 导入 `_blockAttrs = r.AllAttrs.Count>0 ? r.AllAttrs : null`(同 BLK), 状态行按是否持全属性给"切换免重导"/"重导改属性"提示。PMB 块体自此与 BLK 同享 `切换属性`/`属性报告`。

**验证(已知值)**: [PmbImportServiceTests](tests/PitMine3D.Kylin.Tests/PmbImportServiceTests.cs) +1—— MakePmb2(density[2.7,2.8]+grade_v[50,60]) → AllAttrs 含两属性全值, 选定 grade 与 AllAttrs 同源。**build 0 错·单测 1270→1271**。

**本会话第 38 功能**。教训: **除"原版有 Kylin 缺", 还要查"Kylin 自陈暂未"**——grep Kylin 自身 `暂未/未实现/TODO/占位`(排除对原版桩的转述), 常是 reader/handler 只做了一半(PMB 扫了全属性却只留一份)。这是"未完成功能补全"最直接的一类(loop 主旨), 且多半底层数据已备(offsets 已扫), 补全成本低。区别于原版桩(忠实不做)。见 [[unlock-blocked-insights]]。

## 二三四、PMX 圆弧导出(reader/writer 对称)—— Kylin 自身"MVP 暂不导出"补全(第 39 功能)

**续"Kylin 自陈暂未"角度 + reader/writer 非对称检查**: PMX [reader](src/Cad/PmxImportService.cs) 处理类型码 {1 Line·2 Poly·3 Point·4 Text·7 Mesh·14 Circle·**15 Arc**}, 但 [writer](src/Cad/PmxExportService.cs) 只写 {1·2·14·4}, `ArcEntity` 标"MVP 暂不导出"——**非对称**: 场景含圆弧导 PMX 丢弧(导入能读弧、导出却丢), 往返有损。DXF 导入(全实体)/GeoTIFF(压缩变体记录)经查非缺口; 此为真非对称。

补 [PmxExportService](src/Cad/PmxExportService.cs) `case ArcEntity`: 三点 → `ArcMath.Circumcircle` 圆心/半径 + 起终角, **选 a0/a1 使 CCW(a0→a1) 经中点 aM**(对齐 reader case 15 的 NormSweep 正向半程放中点约定)→ curve 恒等(CW 弧端点可交换但曲线同); 三点共线退化为直线(忠实 Tessellate)。

**验证(已知值)**: [PmxExportServiceTests](tests/PitMine3D.Kylin.Tests/PmxExportServiceTests.cs) +2—— CCW 半圆 P1(10,0)P2(0,10)P3(-10,0)(圆心0,0/r10/a0=0,a1=π) 往返**三点精确还原**; CW 输入端点可交换但重建仍过中点(0,10)+端点在(±10,0)(曲线恒等)。**build 0 错·单测 1271→1273**。

**本会话第 39 功能**。教训: **format reader/writer 要查对称性**——reader 处理的类型码 writer 是否全写(PMX reader 读弧 writer 不写=往返丢弧)。补对称时**角度/方向约定要对齐**(writer 选 a0/a1 匹配 reader 的 CCW-NormSweep-经中点), 往返测试(写→读)锁 curve 恒等。同"导入/导出成对往返测试是最强验证"(§二一〇 PMX 首建)。见 [[unlock-blocked-insights]]。

## 二三五、煤质均值贴国标等级(灰/硫/发热量) + 接线 FindGradeLevel —— 看板 KPI 分级 + tested-but-unwired(第 40 功能)

**角度: 原分析窗口逐指标核 + tested-but-unwired**: 逐项对原 `CoalQualityDashboardWindow` 的 5 KPI 卡——avg Ad/S/Vdaf/Q/G 各**带国标等级标注**(`GradeAsh(avgAd)`/`GradeSulfur`/`GradeQnet`, 经 `CoalReferenceService.FindLevel` 查 coal_grade_rule)。Kylin `煤质统计` 只报**均值裸数**(灰分Ad 22.5% …), **无等级**(低灰/中灰/高灰); `煤质分级` 命令只**显示规则字典**不套到数据。且我 §二二九 建的 `CoalTypeInference.FindGradeLevel`(单指标区间分级)**有单测但没接线**——正是此用。

**数据已备**: coal_grade_rule 已种子(ash/sulfur/qnet 各 5 级, 开区间 NULL=±∞)。补 [GeoDataQueries](src/Data/GeoDataQueries.cs) `GetGradeRulesByType(conn, type)`——**保 nullable 边界**(区别既有 `GetCoalGradeRules` 的 COALESCE(...,0) 丢开区间)。[CoalQualityStatsCmd](src/Views/MainWindow.axaml.cs): avg Ad→ash · avg Qnet→qnet · avg St→sulfur 各经 `FindGradeLevel` 贴级(如 "灰分Ad 22.5%(中灰)"), 忠实原看板 GradeAsh/Sulfur/Qnet 标注; Vdaf 无分级规则(同原, 不臆造)。

**验证(种子集成)**: [GeoDataQueriesTests](tests/PitMine3D.Kylin.Tests/GeoDataQueriesTests.cs) +1(对 `GeoDatabase.OpenSeeded` 真种子)—— ash 规则首级开下界/末级开上界 nullable 保真(非 COALESCE 0); Ad 5/22.5/45 落三个不同等级且皆非空(开区间+分级自洽); sulfur/qnet 亦可分级。**build 0 错·单测 1273→1274**。

**本会话第 40 功能**。教训: **逐分析窗口核每个 KPI 卡的"值+等级/分类"双层**——Kylin 常有值(均值)缺分类标注(国标等级); 且**接线已测未用的能力**(FindGradeLevel §二二九 建+测但只 ResolveCoalType 接了线)。**nullable DB 列勿 COALESCE 成 0**(丢开区间语义)——分级/分类的 ±∞ 边界要 `IsDBNull` 读。见 [[unlock-blocked-insights]] [[verify-seed-enum-values-before-filter]]。

## 二三六、爆破分析(surface 未暴露的 blast_event 表)—— "数据在库未接命令"角度(第 41 功能)

**新角度: 枚举种子库全表 → 找有数据但 Kylin 无查询/命令的表**。列 51 表逐个 grep src 引用, 发现 `blast_event`(469 行)/`long_term_metric`(4510)/`daily_mine_summary`(31)/`shift_calendar`(93)/`workforce_monthly`(39) **零 src 引用**——有数据未接。`blast_event` 是操作日志(同 fault_event/production_record, Kylin 有 故障分析/生产数据), 且原有 `BlastService.GetMonthlyAggregate`——故 `爆破分析` 与之平行, 是真缺口。

补 [GeoDataQueries](src/Data/GeoDataQueries.cs) `GetBlastStats`: 总次数/爆破方量/炸药量/**综合单耗(总炸药÷总方量, 体积加权)**/孔进尺/区数 + **逐月聚合**(忠实原 `GetMonthlyAggregate`: 年月 GROUP, SUM 方量/炸药, AVG 单耗, COUNT)。命令 `爆破分析`/`爆破统计`([MainWindow](src/Views/MainWindow.axaml.cs) `BlastStatsCmd`, 平行 FaultStatsCmd) + 目录。

**验证(已知值)**: [GeoDataQueriesTests](tests/PitMine3D.Kylin.Tests/GeoDataQueriesTests.cs) +1—— **blast_event 非迁移种子(运行时导入), 故用内存表已知值验聚合 SQL**: 3 事件→方量30000/炸药6500/综合单耗6500÷30000/孔进尺3000/2区; 逐月 2023-01(2次/方量15000/AVG单耗0.25)与 2023-02。**build 0 错·单测 1274→1275**。

**本会话第 41 功能**。教训: **枚举种子全表查"有数据无命令"是独立高产角度**——51 表里 5 张零引用。**但要核可验证性: 表数据在迁移种子里吗?** blast_event 只运行时导入、迁移种子无→`OpenSeeded` 测不到→改**内存表已知值验聚合 SQL**(更强的 known-value)。补前仍守忠实(原有 BlastService.GetMonthlyAggregate 真聚合方法才补, 非臆造)。见 [[unlock-blocked-insights]]。

**边界记录(忠实): 余 4 张未接表 = 原版 CRUD-only 或空数据, 不臆造分析**。逐个核余下零引用表: `workforce_monthly`(39 行真数据, 有预算效率列) `WorkforceService` = **纯 CRUD**(Get/ByYear/All/Upsert, 无 aggregate) · `long_term_metric`(4510) `LongTermService` = 纯 CRUD(Query, 无 aggregate) · `daily_mine_summary`(31 行**全零**, DailyMineService 纯 CRUD) · `shift_calendar`(93, 班次日历=参考数据)。**判据分野**: blast_event 有 `GetMonthlyAggregate` 真分析方法→移(功能41); 这 4 张原版**只 CRUD 存取 + 数据网格显示**、无分析方法→给它们造分析=**发明原版没有的**(违忠实), 且 Kylin 命令架构非通用数据网格。daily 全零更无值。→ 记录, 不补。**教训: "有数据无命令"要再分野——原版有分析方法(移) vs 原版仅 CRUD/网格显示(记录, 勿臆造分析)**。见 [[faithfulness-only-original-commands]]。

## 二三七、设备累计工时(接原 CalculateCumulativeHours)—— 服务分析方法逐个核(第 42 功能)

**角度⑦精化: 逐 DB 服务的 analysis 方法(非 CRUD)cross-check**。枚举全 GeoDataBase 服务的聚合方法(Aggregate/Pareto/Monthly/Stats/…), 逐个核 Kylin 覆盖: BlastService.GetMonthlyAggregate✓(§二三六)·FaultService.GetPareto✓(故障类型分布 帕累托已在)·Coal/Seam StatsBySeam✓·Production/Kpi Monthly✓。**真缺口: `EquipmentService.CalculateCumulativeHours`**——台账基准 `equipment.cumulative_hours` + `Σ production_record.work_hours`(该设备)= 设备累计运行工时(检修调度基准)。Kylin **零覆盖**(grep 累计工时/CumulativeHours 空)。schema 齐备(两表两列俱在)。

补 [GeoDataQueries](src/Data/GeoDataQueries.cs) `GetCumulativeHours(conn, topN)`: 逐设备 base+Σwork_hours, 按总时降序(检修优先) + 机队合计。命令 `设备累计工时`/`累计运行小时`([MainWindow](src/Views/MainWindow.axaml.cs) `CumulativeHoursCmd`) + 目录。

**验证(已知值)**: [GeoDataQueriesTests](tests/PitMine3D.Kylin.Tests/GeoDataQueriesTests.cs) +1(内存表)—— E1 base1000+生产(200+300)=1500·E2 0+50·E3 NULL→0+无生产=0; 降序 E1 首; 机队合计 1550。**build 0 错·单测 1275→1276**。

**本会话第 42 功能**。教训: **服务层不只查"整个表未接", 还要逐个核"服务的每个 analysis 方法是否都接了线"**——BlastService 两个方法(CRUD 查询 + GetMonthlyAggregate), EquipmentService 混 CRUD + CalculateCumulativeHours; 聚合方法散落在 CRUD 服务里易漏。同 present-but-shallow 的方法级(node-editor/CoalAnalytics), 但这里是 DB 服务的聚合方法。见 [[unlock-blocked-insights]]。

## 二三八、分机型 KPI(接原 KpiService.ByModelMonthly)—— 服务聚合方法扫尾(第 43 功能)

**服务聚合方法扫描收官**: 全 GeoDataBase 服务的 analysis 方法末一个 `KpiService.ByModelMonthly`——按**机型**聚合 KPI(AVG plan/work/fault/idle/delay hours)。Kylin `KPI分析` 只**总均**, 无机型维。补机型级 KPI 比较(选型/淘汰参考)。

补 [GeoDataQueries](src/Data/GeoDataQueries.cs) `GetKpiByModel`: `equipment_kpi_monthly JOIN equipment GROUP BY model` → 各型号 台数 + 平均 可用率/作业率/利用率(比率 ≤1 则 ×100, 同 GetKpiStats 约定), 按可用率降序。命令 `机型KPI`/`型号KPI`([MainWindow](src/Views/MainWindow.axaml.cs) `KpiByModelCmd`) + 目录。**忠实**: 原 ByModelMonthly 出机型×月, 这里再滚机型总均(同数据标准 rollup, 非臆造)。

**验证(已知值)**: [GeoDataQueriesTests](tests/PitMine3D.Kylin.Tests/GeoDataQueriesTests.cs) +1(内存表)—— MA 两台(可用 0.9/0.8)均 85%·2 台; MB 一台 60%; 降序 MA 首。**build 0 错·单测 1276→1277**。

**本会话第 43 功能**。**服务聚合方法全扫毕**: BlastService.GetMonthlyAggregate(§二三六)·EquipmentService.CalculateCumulativeHours(§二三七)·KpiService.ByModelMonthly(本节)三个真缺口补齐; FaultService.GetPareto/Coal-Seam StatsBySeam/Production MonthlyByYear 早覆盖; CRUD-only 服务(Workforce/LongTerm/DailyMine)记录不臆造。**角度⑦(枚举表)+其精化(逐服务聚合方法)共出 3 功能, 收敛**。见 [[unlock-blocked-insights]]。

## 二三九、边坡安全系数校核(F=tanφ/tanβ, 接原 CohesionlessFactorOfSafety)—— "值在算法缺"(第 44 功能)

**"值present算法缺"续(同 §二三五 煤质等级)**: Kylin `边坡设计`(SlopeDesignsCmd)只**显示 stored** `safety_factor`(DB 记录里的设计值), **不算** F。原 `BenchTemplateResolver.CohesionlessFactorOfSafety` 计**无黏聚力整体边坡安全系数 F=tanφ/tanβ**(φ=内摩擦角, β=帮坡角), LocationProcessMap 用作 "F≥1.30" 校核。数据齐(slope_design 有 friction_angle_deg/working·final_slope_angle_deg)。

**复用既有(勿重复)**: Kylin **已有** `BenchParameterVerifier.CohesionlessFactorOfSafety`(§先前 BenchParameterVerifier 已移该式并被 StabilityF 测试覆盖)——**我一度新建 `SlopeStability.cs` 重复了它, 双查后删除改复用**(见下教训)。[GeoDataQueries](src/Data/GeoDataQueries.cs) `SlopeDesignRow`/`GetSlopeDesigns` 加 side_type/friction_angle/cohesion。[SlopeDesignsCmd](src/Views/MainWindow.axaml.cs): 逐帮按帮别取 β(工作帮→工作帮角, 端/最终帮→最终帮角, 忠实原 TargetAngleFor) + φ 调既有 `CohesionlessFactorOfSafety` 算校核 F, 标 ✓/⚠<1.30(规范阈), 汇总不安全帮数。

**验证(已知值)**: [SlopeStabilityTests](tests/PitMine3D.Kylin.Tests/SlopeStabilityTests.cs) +4(直测既有方法)—— φ=β→F=1(临界)·β45°/φ30°→F=tan30<1(不稳)·陡坡降 F/高摩擦升 F·平坡→+∞·阈值 1.30(β20/φ35→F1.92 安全, β40/φ35→F0.83 不安全)。**build 0 错·单测 1277→1281**。

**本会话第 44 功能**。教训: **"值 present 算法缺"跨域复现**——煤质(均值缺等级 §二三五)、边坡(显示 stored F 缺算 F)。**但补前必双查 Kylin 已有无该式**——本次 `CohesionlessFactorOfSafety` **早在 BenchParameterVerifier 里**(该式服务台阶参数校核), 边坡命令只是没接它; 我却先新建 SlopeStability.cs 重复(第 N 次冗余教训, 见 [[unlock-blocked-insights]] ortho/MeshHoleFill/采区划分), 双查后删除改复用。**真缺口不是"公式", 是"边坡命令没调该公式"**——补接线, 非补公式。查"显示 stored 值"的命令是否也算/校核那个值; 但先 grep 公式名确认没现成。见 [[unlock-blocked-insights]]。

## 二四〇、帮坡角反算平盘宽 SolveBermForOverallAngle —— 正逆配对补齐(第 45 功能)

**吸取 §二三九 教训: 先 grep 确认没现成再补**。同 `BenchTemplateResolver` 里 `CohesionlessFactorOfSafety` 邻座另有 `SolveBermForOverallAngle`(W = H/tanβ − H/tanα, 给目标整体帮坡角 β 反求平盘宽)——是 Kylin 已有 `OverallSlopeAngleDeg`(正算 β←W)的**逆**。**grep `SolveBerm/target overall/H/tan−H/tan/反算平盘` 确认 Kylin 无**(BenchWidthIdentifier 是栅格圈区, 非解析逆)→ 真缺口。

补 [BenchParameterExtractor](src/Cad/BenchParameterExtractor.cs) `SolveBermForOverallAngle(H, α, targetβ)`(与 `OverallSlopeAngleDeg` 正逆配对同处), 忠实原式(钳 ≥0, H≤0 或 tan≤0 返 0)。命令 `平盘宽反算 <H> <α> <β>`([MainWindow](src/Views/MainWindow.axaml.cs) `BermForAngleCmd`, 带回代校核 + β≥α 无需平盘提示) + 三别名。

**验证(已知值)**: [BenchParameterExtractorTests](tests/PitMine3D.Kylin.Tests/BenchParameterExtractorTests.cs) +2—— **正逆往返**: H15/α65/β45 反算 W 再正算还原 β=45(精确); W=H/tanβ−H/tanα 已知式; 钳位(β≥α→W=0·H≤0→0)·越缓目标越宽 W。**build 0 错·单测 1281→1283**。

**本会话第 45 功能**。教训: **正算有→查逆算(设计反问题常成对)**: `β←(H,α,W)` 有了, `W←(H,α,β)` 是设计反问题(定目标帮坡角求平盘宽), 成对补齐。**正逆往返测试是最强验证**(forward∘inverse=identity)。这次**先 grep 后建**(吸取 §二三九), 无冗余。见 [[unlock-blocked-insights]]。

## 二四一、道路设计参数: 最小平曲线半径 + 展线长(接原 TransportConstraintSettings)—— 角度⑨续(第 46 功能)

**同"正算有→查逆算"**: Kylin `RoadCrossSection` 有超高正算 `SuperelevationPct`(e=V²/(127R)−μ, 从半径算超高), 缺**逆** `MinCurveRadiusBySpeed`(R=v²/(127(μ+e_max)), 从设计车速反算最小平曲线半径——道路布线核心约束) + `DevelopmentLengthPreview`(展线长=rise/纵坡, 以最大纵坡降台阶高的水平展线)。原在 `TransportConstraintSettings`。**grep 确认 Kylin 无**(127 只在 SuperelevationPct 正向)→真缺口。

补 [RoadCrossSection](src/Cad/RoadCrossSection.cs): `MinCurveRadiusBySpeed(v, e_max, μ=0.15)` + `DevelopmentLengthM(rise, maxGrade)`(忠实原式)。命令 `道路设计参数 <设计速度> [最大超高%] [台阶高 [最大纵坡%]]`([MainWindow](src/Views/MainWindow.axaml.cs) `RoadDesignParamsCmd`) → R_min + 展线长 + 目录。

**验证(已知值)**: [RoadCrossSectionTests](tests/PitMine3D.Kylin.Tests/RoadCrossSectionTests.cs) +2—— R_min=25²/(127·0.21)=23.44m; **逆一致: R_min 处超高恰饱和到 e_max**(SuperelevationPct(R_min)=6%, 正逆自洽); 车速↑R_min↑·超高↑R_min↓; 展线长 45m@9%=500m·零纵坡→0·越缓越长。**build 0 错·单测 1283→1285**。

**本会话第 46 功能**。教训: **同一物理关系的正逆两式常分处**——超高正算在 RoadCrossSection, 半径反算在 TransportConstraintSettings(约束模型), 只移了正算。**逆一致测试**(R_min 处超高饱和 e_max)锁定正逆同源。角度⑨(正逆配对)连出 2 功能(平盘宽反算/最小平曲线半径)。见 [[unlock-blocked-insights]]。

## 二四二、矿山时序经济评价: 泰勒服务年限 + DCF NPV(接原 PitEvaluator)—— 命令输出补经济维(第 47 功能)

**"present-but-shallow 命令输出"**: Kylin `确定境界`(PitDepthCmd)只报 坑深/圈入煤/剥采比/净值, 原 `PlanLib PitEvaluator.Evaluate` 从同样输入(ResourceProfile+DepthSolveResult)另算**储量/剥采比/时序/经济全维**: **泰勒规则服务年限 T=6.5·R^0.25**(R=储量 Mt) + 年产=储量/年限 + **NPV=总净值等额分摊后年金折现**。**grep 确认 Kylin 无 Taylor(6.5/^0.25)**(NPV 在 PanelSplit 采区语境有别式, 但 Taylor 全缺)→真缺口。

补 [src/Cad/MineEconomics.cs](src/Cad/MineEconomics.cs): `TaylorMineLifeYears(R_Mt)=6.5·R^0.25` · `AnnuityPvFactor(r,T)=(1−(1+r)⁻ᵀ)/r`(r→0 退化 T) · `NpvLevelized(净值,年限,r)=(净值/年限)·年金系数`。忠实原 PitEvaluator 三式。[PitDepthCmd](src/Views/MainWindow.axaml.cs): 用圈入煤储量算 R(万t/100=Mt)→ 服务年限/年产/NPV(8%)追加到状态行。

**验证(已知值)**: [MineEconomicsTests](tests/PitMine3D.Kylin.Tests/MineEconomicsTests.cs) +3—— Taylor: R1Mt→6.5·R16Mt→13(16^0.25=2)·R0→0·单调; 年金系数 r10%/T10=(1−1.1⁻¹⁰)/0.1·r→0退化T·T0→0; NPV: 净值1000/年限10/8%<1000(折现)·r0→名义1000。**build 0 错·单测 1285→1288**。

**本会话第 47 功能**。教训: **同一命令的输出维度可 present-but-shallow**——确定境界有几何+净值, 缺时序(Taylor 年限)+经济(NPV 折现)维; 原 PitEvaluator 从同输入算全维。**标准经济式(Taylor 6.5R^0.25/DCF 年金)可移可验**; grep 先确认(Taylor 全无, NPV 别处有别式不冲突)。见 [[unlock-blocked-insights]] [[shell-completeness-priority]]。

## 二四三、开采程序逐期切分(接原 TemplateDrivingEngine 距离驱动核)—— 重跑"原版单测枚举"角度(第 48 功能)

**"报收敛后重跑原版单测枚举"(最精确切片发现法)**: 五个多样探针全 covered 后, 重枚举原版 18 个 `*Tests.cs`(比上次覆盖大得多), 逐个核 Kylin。17 个映射到已覆盖(BenchTemplateResolver/CenterlineLineForm/ProfileSmoother/RoadCrossSection/PathSolver/RegionClip→WindingNumber…), 唯 **`DriveTemplateEngine`(TemplateDrivingEngine)** 真缺口: 块体+工作线沿推进方向切成期→逐期煤/岩量+累计剥采比。Kylin `平行推进` 只几何推进不核块体量; `剥采比均衡` 消费分期量表(CSV)却无生成者——**此引擎生成的正是均衡的输入**(闭合"块体→分期→均衡"环)。

**大引擎(529行)取可验证核**(最小 plan 法): 原含多段工作线/多层煤岩/台阶退距(s=a0+(cz−floorZ)/tanα)/几何输出。取**距离驱动直线核**(平面近似, 与原测试 α=89°→退距≈0 一致)。补 [src/Cad/DriveSequence.cs](src/Cad/DriveSequence.cs) `SweepByDistance`(逐格投影推进轴→按步距分期→逐期煤/岩+累计剥采比)+ `ToBalanceCsv`(直接喂剥采比均衡)。命令 `开采程序切分 [步距]`([MainWindow](src/Views/MainWindow.axaml.cs) `DriveSequenceCmd`, 块体+选中工作线法向→分期+导 CSV)+ 目录。**记录**: 台阶退距(缓帮)/多段/多层/几何输出属工程细化。

**验证(自造已知值)**: [DriveSequenceTests](tests/PitMine3D.Kylin.Tests/DriveSequenceTests.cs) +3—— 3 列(每列 2 煤 8 岩)→3 期各煤2000/岩8000·总煤6000/岩24000·综合剥采比24000/(6000·1.3)·累计均质恒定; 方向(+Y 全落一期)/maxPeriods/空守卫; CSV 首期 1,0.26,0.8 喂均衡。**build 0 错·单测 1288→1291**。

**续(第 52 功能): 多段(弯)工作线(复评§243 记录的"多段"限制→亦可解)**。复评"多段工作线属细化"发现原多段投影是**干净的最近段投影**(每格投影到 |a0| 最小的段的法向, 横向容差 latTol=cellSize 收角点), 非我以为的复杂。补 [DriveSequence](src/Cad/DriveSequence.cs) `SweepAlongWorkLine(workLine, cells, ...)`: 逐格取最近段法向投影 a0 → 分期。命令: 选中工作线 >2 点(弯)时自动走多段, 否则单向。[DriveSequenceTests](tests/PitMine3D.Kylin.Tests/DriveSequenceTests.cs) +2: **直线工作线等价 SweepByDistance(法向单向)**(强等价验证)·L 形工作线各格投影到正确段(seg0 竖法向−X/seg1 横法向+Y)。**单测 1297→1299**。教训: **复评记录的"复杂细化"——读原实现常发现是干净基元(最近段投影/退距式), 我高估了复杂度**(同 latTol=cellSize 是小主参非大)。DriveSequence 至此: 距离/量/退距/多段全移, 余多层分seam报告(核已binary覆盖)/几何输出(native显示)。

**续(第 51 功能): 台阶退距(复评§243 记录的"平面近似"限制→已可解)**。§243 记录"缓帮台阶退距属工程细化"; 复评发现原 `BenchOffset` 是**纯确定式**(单斜面 h/tanα; 或逐台阶 nb×(benchH/tanα+bermW)+hr/tanα), 可移可验。补 [DriveSequence](src/Cad/DriveSequence.cs): `Cell` 加 Z; `BenchOffset(h,benchH,bermW,α)`; 两 sweep 加 faceAngleDeg 参(>0 时高处格因帮坡后退, ≤0 退化平面陡帮)。命令 `开采程序切分 [步距] [坡面角°]` 末位可选坡面角。[DriveSequenceTests](tests/PitMine3D.Kylin.Tests/DriveSequenceTests.cs) +2: BenchOffset 已知值(h30/α45单斜=30·α60=17.32·2台阶=40·1台阶余5=25)·同 XY 高格(Z30)45°退距 30→落期3 而底格期0(平面则同期)。**单测 1295→1297**。教训: **移引擎核后, 复评自己记录的"细化"限制——纯确定式(BenchOffset)常可解, 非真受阻**(同 [[unlock-blocked-insights]] 复评记录角度: latent/绕过≠永久)。去掉平面近似, DriveSequence 对真实帮坡角(非仅垂直)正确。

**续(第 50 功能): 等煤量分期(TemplateDrivingEngine volumeDriven 模式家族补齐)**。§二四三 补了距离驱动(等距分期), 原引擎另有**量驱动**(累计煤量达目标切期=等煤量分期, 恒定产量规划)。补 [DriveSequence](src/Cad/DriveSequence.cs) `SweepByVolume`(细分刀 sliceWidth → 顺推累计煤量, 达 targetCoalVolM3 切期, 刀粒度)。命令扩 `开采程序切分 量 <目标煤量万m³>`(等煤量)vs `开采程序切分 [步距]`(等距)。[DriveSequenceTests](tests/PitMine3D.Kylin.Tests/DriveSequenceTests.cs) +1: 4 刀(各煤1000)目标2000→2期各煤2000·目标1000→4期·零守卫。**单测 1294→1295**。教训: **移了引擎一个模式, 查它的模式家族(距离/量驱动成对, 同变差三型/坡道三式)**。

**本会话第 48 功能**。教训: **报收敛(哪怕多探针 covered)后, 重跑"原版单测枚举"——覆盖变大后仍可能剩真切片**(TemplateDrivingEngine 藏在 Driving 引擎里, 主题/命令探针都漏, 但原版有单测=作者认定自足可验)。**大引擎取可验证核**(距离驱动直线, 自造 known-value)而非硬吞全 529 行(多段/退距/几何输出记录)。此切片闭合了既有两命令(平行推进 geo + 剥采比均衡 consume)间的缺环。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

## 二四四、运输道路布局求解(接原 RoadLayoutSolver 方案构建核)—— 原版单测枚举收官(第 49 功能)

**原版单测枚举最后一项**: 全面重枚举原版 19 个 `*Tests.cs`(BlockModelLib/MineAssLib/PitMineApp/RoadLib), 18 已映射覆盖 + DriveTemplateEngine(§二四三)后, 末一项 **`RoadLayoutSolver`**(纯类无引擎依赖, 211 行, 有单测): 坑线候选 + 运量需求 → 紧凑/均衡/单线三布局方案。Kylin 有坑线生成 + 运距/OD, 无布局**优化选择**层。

**可验证核 + CSV 候选**(同 TemplateDrivingEngine 法): 原 `Solve` 无候选时 `_gen.Generate`(=StraightRampAutoRouter 域, 记录), 但有候选时 `BuildScheme` 纯核可测。补 [src/Cad/RoadLayoutSolver.cs](src/Cad/RoadLayoutSolver.cs) `Solve(candidates, demand, perLane, unitCost)`: 运量定总车道 ⌈需求/单车道运力⌉ → 拆线(紧凑每线≤2/单线扛全部)→ 逐线车道/容量/利用率 → 可行(几何可行+运力足+车道≤上限4)+ 运营成本(ton-km)+ 基建代理(总展线长); 推荐=可行中基建最小。命令 `运输布局方案 <需求t> [单车道运力 [单价]]`([MainWindow](src/Views/MainWindow.axaml.cs) `RoadLayoutCmd`, 候选 CSV)+ 目录。**记录**: 自动候选生成(坑线router)+ 压矿(块体侧)属细化。

**验证(已知值)**: [RoadLayoutSolverTests](tests/PitMine3D.Kylin.Tests/RoadLayoutSolverTests.cs) +3—— 需求800/单车道1000→总车道1·利用率0.8·运营800(=800×0.5×2)·三方案皆可行; 需求3500→总车道4·紧凑2线×2车道·单线4车道皆≤上限可行; 几何不可行候选→全方案✗带原因·需求6000→单线6车道>上限✗而紧凑3线可行·空候选→不成功。**build 0 错·单测 1291→1294**。

**续(第 53 功能): 布局方案加权评分 + 目标(复评§244 记的"简化推荐"→原 Score 是干净式)**。§244 我用"基建最小"简化推荐; 复评原 `Score` 是**干净加权式**: 不可行=0, 否则 wCapex·(100/(1+capexKm)) + wUtil·(利用率·100), 权重按目标(均衡0.6/0.4·最小运输功1.0/0·默认最小成本0.8/0.2)。补 `RoadLayoutSolver.ScoreOf` + `Solve` 加 objective 参 + `LayoutScheme.Score` + 推荐=最高分。命令加目标参 + 报各方案分。[RoadLayoutSolverTests](tests/PitMine3D.Kylin.Tests/RoadLayoutSolverTests.cs) +1: 默认权重 capexKm0.5→capexScore66.67·util80→分69.33·均衡目标权重不同分不同·不可行=0·推荐=最高分。**单测 1299→1300**。教训: 同 DriveSequence, **移引擎核后复评自记的"简化"——原式常干净可移可验**(第 4 个连出: §50/51/52/53)。

**本会话第 49 功能**。**原版单测枚举全收官**: 19 个 `*Tests.cs` 全部映射 Kylin 功能(17 早覆盖 + DriveTemplateEngine§二四三 + RoadLayoutSolver 本节)。**这是最精确的收敛证据**——作者自定的全部自足可验切片均已移/覆盖, 远强于主题/命令探针的"covered 感"。两个"藏在大引擎里的纯核"(距离驱动切期 + 布局方案构建)靠单测枚举挖出, 用 CSV/available 输入喂可验证核 + 记录引擎细化(候选生成/退距/几何输出)。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二四七 品位-储量曲线上屏（已算未绘的统计图 → 场景折线）+ 通用 CurvePlot

**复审自记的"统计图=OxyPlot 受阻"边界**: 早先把统计类图表(非空间图)一律记为"数值核已做, 绘图=OxyPlot 图窗受阻"。但 Kylin 场景**本能画图**——直方图(`HistogramPlot` 竖条入场景)、剥采比 VP 曲线、钻孔柱状图均已上屏。查 `GradeTonnageCmd`([MainWindow](src/Views/MainWindow.axaml.cs)): `品位-储量曲线` 的 `r.Curve`(限值→累计质量%)**算了却只把中点值写状态行, 曲线本身未绘**——典型"已算未绘"缺口, 且与"能画直方图却记曲线受阻"自相矛盾。

**补**: 通用 [CurvePlot](src/Cad/CurvePlot.cs)(与 HistogramPlot 同风格): 一串 (x,y) → 自动量程归一化到图区矩形, 画折线 + 外框 + 四角刻度 + 轴名; 空/单点/退化范围安全返回。`GradeTonnageCmd` 把 `r.Curve` 的 (Cutoff, CumMassPct) 喂 `CurvePlot.Build` 上屏(与直方图同位: 视口 0.3–0.7 宽 · 0.85–0.4 高), 状态行加"曲线入场景"。CurvePlot 通用, 亦可供剥采比/趋势等其他"已算未绘"曲线复用。

**验证(自造已知值)**: [CurvePlotTests](tests/PitMine3D.Kylin.Tests/CurvePlotTests.cs) +3—— 三点 X∈[0,10]·Y∈[20,100] 图区 40×20: 首段起(0,0)·末段终(40,20)·中点(5,60)→(20,10) 精确; 六标签(轴名+四角刻度)俱在; 折线2段+框4段=6线; Y 全等退化不产 NaN; 空/零宽返空。**build 0 错·单测 1302→1305**。

**顺手修** [CoalAudit](src/Data/CoalAudit.cs) 头注**过期自相矛盾**: 注仍写"① 工分自洽/⑥ 测井一致 数据不支撑记录不做", 但同文件下方 `CheckProximateConsistency`/`CheckDrillLogConsistency` 已实现二者(§二四五/二四六)——改注为"另二规则以独立方法覆盖 ⇒ 原 7 规则全覆盖"。

**续(同脉复用 CurvePlot)**: `CoalByElevationCmd`(分标高煤质)亦"已算未绘"——按标高带算厚度加权均值 `bands`(ZLow/ZHigh/WeightedMean/N)只拼成文本状态行。补: (WeightedMean, 带标高中点) 喂 `CurvePlot.Build` 上屏(轴名 品位/标高)=竖向剖面曲线; `ByElevation` 已按标高升序且跳空带, 折线沿 Y 单调不锯齿。复用已测 CurvePlot(无新逻辑, 单测仍 1305)。

**本会话第 56 功能**。教训: **"统计图受阻"是又一条误记**——场景能画线/框/字即能画统计折线, 非只空间图。复审边界时, "受阻"标签若与已交付能力(能画直方图)冲突, 多半是误记。这是"复审自记边界"续脉: 记录 0-for-N 可靠, 边界与既有能力矛盾者优先复核。见 [[unlock-blocked-insights]]。

---

## §二四八 类别柱状图 BarChartPlot（煤类分布/设备分类/产能/故障帕累托 上屏）

**续"统计图能画"复审**: 除折线(§247)外, 大量**类别分布**只拼文本状态行——原版以「煤类饼/分类柱」呈现。场景能画柱(直方图已证), 类别柱只差一个通用助手。补 [BarChartPlot](src/Cad/BarChartPlot.cs)(与 HistogramPlot 互补: 任意 (类别,值) → 等宽竖条 ∝ 值/最大 + 类别标签(条下)/数值(条顶)+值轴; 空/零尺寸/全零安全) + [MainWindow](src/Views/MainWindow.axaml.cs) `DrawCategoryBars` 助手(视口中部同位, 复用 4 处)。

**已算未绘→上屏的 4 命令**: `煤种分类`(煤类样本占比%, 忠实原「煤类饼」量化)· `设备台账`(分类台数)· `产能分类对比`(各类万m³)· `故障类型分布`(各类停机h, 帕累托——rows 已按停机降序)。

**验证(自造已知值)**: [BarChartPlotTests](tests/PitMine3D.Kylin.Tests/BarChartPlotTests.cs) +3—— QM40/CY20/SM10: 最高条顶达图顶(y=20)·CY 高=20/40*20=10·类别与值轴名标签俱在·全零不除零 NaN·空/零宽返空。4 命令复用已测助手(无新逻辑)。**build 0 错·单测 1305→1308**。

**本会话第 57 功能**。教训: 折线(CurvePlot)+ 类别柱(BarChartPlot) 两助手补齐后, "统计图受阻"边界基本瓦解——凡 (x,y) 序列或 (类别,值) 分布皆可场景上屏, 唯饼图/热力图等特殊型仍走 CSV。见 [[unlock-blocked-insights]]。

---

## §二四九 散点图 ScatterPlot（灰分-发热量回归交会图上屏）+ LAS/工序柱补

**第三类图(散点)**: `灰分发热量回归` 算了斜率/截距/R²/残差离群, 结果**本就暴露 `Points`(原始 Ad,Cal 对)+`Suspects`**, 却只 CSV+状态行——典型交会图已算未绘。补 [ScatterPlot](src/Cad/ScatterPlot.cs)(与 CurvePlot/BarChartPlot 并列第三类: 点标记(叉)+可选拟合直线+离群红叉高亮+框+四角刻度+轴名; 拟合线端点纳入 Y 量程保同框; 空/零尺寸安全)。`AshCalorificRegressionCmd` 把 `r.Points`+拟合 (Slope,Intercept)+`Suspects` 高亮喂 ScatterPlot 上屏。

**顺带**(§248 助手复用): `LAS 分类统计`(各类点数柱)· `工序进度跟踪`(各工序达成%柱) 亦上屏。

**验证(自造已知值)**: [ScatterPlotTests](tests/PitMine3D.Kylin.Tests/ScatterPlotTests.cs) +4—— 完美线 y=2x 三点: 3 叉标记·(5,10)→中心(20,10)·拟合线端 (0,0)→(40,20)·轴名与刻度俱在; 离群红叉更大且异色; y 全等但拟合 y=x 端点撑开 Y 量程使线不出框; 空/零宽返空。**build 0 错·单测 1308→1312**。

**本会话第 58 功能**。三助手(折线 CurvePlot §247 · 类别柱 BarChartPlot §248 · 散点 ScatterPlot §249)补齐, "统计图受阻"边界彻底瓦解: 序列/分布/交会三型皆场景上屏, 唯饼图/热力图/箱线等仍走 CSV。见 [[unlock-blocked-insights]]。

---

## §二五〇 命令级收敛复核（原版 200 按钮标签 × Kylin 全量比对）

**继测试级 19/19 收敛(§249)后, 补命令级系统复核**: 从原版 9 模块 `*Plugin.cs` 提取**全部 200 个 `AddButton` 按钮标签**(BlockModel/GeoDataBase/MeshEdit/MineAss/Plan/PointCloud/Road/Task), 与 Kylin `MainWindow.axaml.cs` 全字符串比对。

**流程(防别名噪声)**: (1) 整串精确差集 → 80 项"缺失"(多为别名); (2) 整串在 Kylin src 全域 0 命中过滤 → 13 项真候选; (3) 逐项核心词 + 分派项(`cmd == "..."`)验证。

**结果三分**:
- **别名覆盖(已实现, 换名)**: 体积算量→`体积计算`/算量/土方量 · 创建块体→`块体模型`/导入块体/地质体建模 · 属性赋值→`切换属性`/品位块模型(克里金赋值)/QualityVoxelInterp · 内排推进→`平行推进`/工作线推进/排土场按量推进 · 查询台阶平盘标高→`平盘标高清单`(BenchLevelInventory) · 斜坡道双线→`斜坡道`/螺旋坑线 · 坡度/坡向/曲率/粗糙度→点云面分析俱在。
- **原生内核受阻(无托管源, 忠实不臆造)**: 布尔运算 交/差/并/补 —— 复核实现 `MeshOpsCapabilityImpl.StartIntersectMeshesInteractive → EngineInterop.StartIntersectMeshesCommand()` = **P/Invoke C++ 内核 CSG**, 无托管算法可移(臆造 managed CSG 既不忠实亦无法对原验证)。同 mesh 修复/分割地质体(内核聚类)。
- **原版即桩(sample-UI, 无逻辑, 忠实不补)**: `工序定额`(ProcessQuotaWindow "样例(可编辑)", OnSave "持久化待接") · `质量标准`(QualityStandardWindow "样例", OnSave "持久化+喂装箱质量约束待接")——原版本身仅样例表单、持久化未接、无算法/无可验核, 故无验证条件可满足, 记录不补(同 SkeletonCommand 桩)。

**结论**: 200 标签无一是"可实现+可验证+忠实却漏做"者——要么已覆盖(直/别名)、要么原生内核无源、要么原版即空桩。**命令级 + 测试级(19/19)双重收敛互证**, 远强于单一探针。这是本会话"复审自记边界"脉的收官式复核: 系统比对而非印象。**教训: 命令级全量标签比对是继测试枚举后第二个黄金收敛标准; 但差集须过别名噪声(整串0命中→核心词→分派项三级) + 逐项验证内核/桩性质(勿把 native/stub 误记为"可做漏做", 亦勿把别名误记为"缺失")**。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]] [[pitmine3d-command-inventory]]。

---

## §二五一 PMB 块体模型导出（读写对补齐——原版 PmbmWriter 的忠实反向）

**读写不对称角度**: Kylin 有 `PmbImportService`(读 PMB)却无导出——查原版 `Modules/BlockModelLib/Format/PmbmWriter.cs` **确有**(+`ExportBlockModelDialog`), 故 PMB 导出是原版真功能, 且格式**源码可见**(PmbmFormat 全字段布局)、Kylin 读端已解析 ⇒ 可实现 + 忠实 + **往返可验**(写→读还原网格+全属性=最强验证)。

**补** [PmbExportService](src/Cad/PmbExportService.cs):
- `ToBytes(Grid, attrs, name)` 纯序列化, 忠实原布局: Header('PMB1'/version1/fileSize/段表偏移32) + 段表(24B/项) + Strings 名池 + GridSpec(**全字段**: nameIdx/descIdx/origin/blockSize/dims/rotation/storageMode/subBlockDepthMax/subMinSize/reserved) + Blocks(Dense storageMode0 + blockCount + 逐属性 nameStrIdx/dataType/valueCount/double[] x-fastest) + Footer(activeCount + **IEEE CRC32** 覆盖[0,fileSize-16) + magicEnd 'PMB1'逆序)。
- `FromBlocks(blocks, extraAttrs)` 从 Kylin 块体列表**按各块中心算 i/j/k 归位**重建规则网格 + grade(+全属性)x-fastest 数组(与输入顺序无关, 稳健)。
- 命令 `导出PMB`/`导出块体模型文件`([MainWindow](src/Views/MainWindow.axaml.cs) `PmbExportAsync`): 最近块体 + `_blockAttrs` → 重建 → 写 .pmb; + 目录 + 分派。

**忠实取舍**: Kylin grade-only 数据模型无 PropertySchema(11)/DisplayStyle(16) 可忠实填, 故略去该二段(Kylin 读端不需; 写臆造默认色/类型反不忠实)——记录此限, 非 schema 缺。

**验证(写→读往返)**: [PmbExportServiceTests](tests/PitMine3D.Kylin.Tests/PmbExportServiceTests.cs) +4—— 2×2×2 双属性 ToBytes→Parse: 维度/cell中心(origin+(i+.5)*size)/品位/全属性数组精确还原; FromBlocks 乱序4块→网格序 x-fastest 正确且往返还原品位序; 仅几何(无属性)可解品位0; 属性长度不符抛异常。**build 0 错·单测 1312→1316**。

**本会话第 59 功能**。教训: **读写不对称是可靠富矿——有 Reader 先问"原版有无对应 Writer?"(PmbmWriter 确在), 有则格式已知(读端即规格)+往返自验**。同 KdfImport/KdfExport 已成对; PMB 补齐后块体模型可存原生格式回导。见 [[unlock-blocked-insights]]。

---

## §二五二 格式读写层收敛复核（第三收敛角度：Reader/Writer 类全量比对）

**继测试级(§249)、命令级(§250)后, 补格式层复核**: 枚举原版全部 `*Reader`/`*Writer`/`*Importer` 类与 Kylin 比对。

**Writer 侧**: 原版仅 KdfWriter/PmxWriter/PmbmWriter/PmbiWriter/BinaryPayloadWriter。Kylin: Kdf↔Kdf · Pmx↔Pmx · **Pmbm↔Pmb(§251 新补)** 三对齐全; **PmbiWriter/BinaryPayloadWriter = 原生引擎入料格式**(`IEntityCapability.ImportEntitiesBinary` 推实体进 C++ AcDb, [[project-binary-channels]])——Kylin 无原生引擎, N/A; 原版**无** BlkWriter/LasWriter ⇒ Kylin BLK/LAS 仅导入=忠实(原版亦只读)。

**Reader 侧**: KdfReader✓ · MapGis(Project/Wl/Wp/Wt)✓ · PmxReader✓ · BlkReader✓ · PmbmReader✓ · **PMxxReader = 原生算子返回 buffer 读取器**([4magic][4ver][1success]+算子特定 body, 原生 mesh 布尔/体积算子的结果流)——Kylin 无原生算子产此 buffer, N/A(**纠早记"PMxx无源": 读器源可见, 真阻是无原生算子, 非无源**) · **TdmReader/TdmStringReader 早已移**(§见下)。

**TDM 家族纠错(重要)**: 早记"KDF/TDM 无样本不可验"=**误记**。复核: Kylin **已有** `TdmImportService`(.3dm 二进制网格 394 行+测) · `.3ds` 折线(ImportTdmStringEditable+测) · KDF 读写对(+测)——全用**合成字节夹具**已验(格式源码可见即可合成, 同 PMB/blast/工分)。唯 **`.3dp` 工程包**未做: 实为 **Microsoft CAB 归档**, 原版靠 Windows `expand.exe` 解包(内含 .3dm/.3ds 转交已有 reader, 纯编排)——Kylin 无 expand.exe, 托管 CAB+LZX 解压 .NET 无内置且无样本, **=环境阻**(非"无样本", 非缺算法)。

**三收敛角度互证**(测试 19/19 · 命令 200 标签 · 格式 Reader/Writer 全对)一致指向: 可实现+可验证+忠实者已尽, 余为 native 引擎/系统工具依赖。**教训: "无样本"非真阻(格式源可见即可合成验); 逐条复核 blocked 记录仍是富矿——本轮又纠 PMxx"无源"、KDF/TDM"无样本"两误记, 并精确定位 .3dp 真阻(CAB/expand.exe)**。见 [[unlock-blocked-insights]]。

---

## §二五三 块体属性赋值（公式模式）—— 补原 ExpressionEngine 值求值 + 纠"受数据模型限"误记

**第四收敛角度(域引擎类枚举)挖出**: 枚举原版 `*Engine/*Solver/*Generator/*Builder` 域类比对, 逢 `ExpressionEngine`(BlockModelLib/Expression)。Kylin `BlockExpression` 只是其**谓词子集**(X/Y/Z/Grade/Size + 比较/布尔 → 供筛选/删除), 头注自记"多属性 CellData 公式赋值受数据模型限, 记录"。**复核纠误**: 原 ExpressionEngine 另一用途「**属性赋值 公式模式**」(算 double 值赋新属性)——Kylin **`_blockAttrs`(Dictionary<string,double[]>)本就持多属性**(BLK/PMB 导入填, 切换属性 已用), 数据模型**支持**, 记"受限"过悲观。

**补** [BlockAttrExpression](src/Cad/BlockAttrExpression.cs)(忠实原 ExpressionEngine 值求值, 与 BlockExpression 谓词互补):
- 文法 expr(+−)→term(*/%)→unary(±)→primary(num/ident/ident(args)/(expr)); AST(Num/Var/Neg/Bin/Func) + `IBlockExprContext`/`MutableBlockExprContext`。
- 14 内置函数: min/max/abs/sqrt/exp/log/sin/cos/tan/floor/ceil/round + clamp(v,lo,hi) + if(c,a,b)(c>0取a)。除零/模零→NaN(不抛), 未定义变量→0(容错, 同原 v1)。
- 命令 `属性赋值 <名> = <表达式>`([MainWindow](src/Views/MainWindow.axaml.cs) `BlockAttrAssignCmd`): 逐块建 context(x/y/z/grade/size/i/j/k/nx/ny/nz/sx/sy/sz + 已有属性列)→求值→存 `_blockAttrs[名]`(NaN/∞→0)。即可「切换属性 <名>」配色显示。+ 目录 + 分派。

**验证(已知值)**: [BlockAttrExpressionTests](tests/PitMine3D.Kylin.Tests/BlockAttrExpressionTests.cs) +6—— 运算优先级/括号/%/一元−; 属性公式(grade·体积·密度、埋深1000−z、未定义→0); 14 函数逐一(min/max/abs/sqrt/floor/ceil/round 银行家舍入/exp/log); clamp 上下夹 + if 条件; 除零/模零→NaN 不抛; 语法错抛。**build 0 错·单测 1316→1322**。

**本会话第 60 功能**。教训: **第四收敛角度=域引擎类(*Engine/*Solver)枚举**——继测试/命令/格式三角度, 域算法类枚举挖出 present-but-partial(Kylin 有谓词子集缺值模式)。又一"受数据模型限"误记被纠(`_blockAttrs` 本支持多属性)。**"受限/blocked"记录逐条复核仍是富矿, 第 N 次证明我的记录不可靠**。见 [[unlock-blocked-insights]]。

---

## §二五四 自标限制复核 + 导出实体覆盖（第五/六收敛角度）

**第五角度: 源码自标"限制/记录"注释逐条复核**。grep Kylin src 自标『受限/记录/仅/简化/待接』的限制注释, 逐条对证——**多为过期**(功能早落地, 注释未更, 是过往误记之源):
- BenchLines "定距近似待接" → `BenchDistance(W+H/tanα)` 已由命令『台阶扩帮 W H α』接入(过期)
- PmxExport "圆弧暂不导出" → 圆弧 type15 §39 已导(过期)
- BlockExpression "公式赋值受数据模型限" → 值模式 §253 已补(过期)
- PmbImport "多属性→单属性" → 全属性留 AllAttrs(过期)
- 真阻(记录属实): RoadLayoutSolver 自动候选(3D 路由器)· AttainmentAnalyzer 等待原因码归因(任务域模型)· MeshHoleFill 大洞质量 · 原生填充图案库 · JPEG 渐进式 · RoadTopology 装卸点(次要建模差异)。

**发现一真可做**: PmxExport 注"正多边形暂不导出"——但 `PolygonEntity` 几何即闭合多边形, 可如矩形导为闭合多段线2。补 `case PolygonEntity`(N 顶点=Cx+R·cos/sin(Rot+2πi/N)) + 往返测试(正方形→4 顶点精确)。

**第六角度: 导出实体覆盖枚举**。Kylin 2D 场景全 8 类 `SceneEntity`(Line/Point/Polyline/Rect/Circle/Arc/Text/Polygon):
- **PmxExport**: 加正多边形后 **8/8 全覆盖**; MText/Hatch/椭圆/样条 = 原版实体 Kylin 未建模(无从导, 非缺)。
- **SceneExport(DXF/DWG, ACadSharp)**: **8/8 全覆盖** + 多行文字→MText(高保真, 甚超 PMX)。

**教训**: **注释即"记录", 过期注释诱发再误判**——逐条复核自标限制是第五角度(继测试/命令/格式/引擎), 又纠 4 过期注释、挖 1 真可做(正多边形导出)。**导出实体覆盖(第六角度)= 枚举场景实体 × 各导出器 case**, 确认 PMX/DXF 皆 8/8。至此**六收敛角度**互证。见 [[unlock-blocked-insights]]。

---

## §二五五 分煤层煤质箱线（DB 聚合 present-but-partial）+ 箱线图 BoxPlot（第四类图）

**第八角度(DB 聚合命令 present-but-partial)续挖**: 原 `CoalQualityStatsWindow` 出**每煤层五数概括**(样本/均值/标准差/Min/P25/P50/P75/Max + 评级 + 箱线图 + CSV)。Kylin `煤质统计`(DB) 只出**全局均值**(灰/挥/热/硫+CV+粘结G); `质量统计`(CSV) 有分组分位数但吃用户 CSV 非库。⇒ **库直出分煤层箱线缺**。

**补**:
- [CoalAnalytics](src/Data/CoalAnalytics.cs) `StatsBySeam(samples, ind)` 纯核: 按煤层分组 → `Statistics.Describe` 五数概括(n/均值/标准差/Min/P25/中位/P75/Max), 缺指标样本跳、煤层码升序; `StatsBySeamToCsv`。
- [BoxPlot](src/Cad/BoxPlot.cs)(第四类图, 与 CurvePlot/BarChartPlot/ScatterPlot 并列): 每类别 Q1–Q3 箱 + 中位横线 + Min/Max 须端帽, 全局共享 Y 量程; 空/零尺寸/退化安全。
- 命令 `分煤层煤质 [ad|vdaf|std|qnet]`([MainWindow](src/Views/MainWindow.axaml.cs) `CoalStatsBySeamCmd`): 库样本→StatsBySeam→箱线图上屏 + CSV + 状态行五数摘要。+ 目录 + 分派。

**验证(已知值)**: [CoalStatsBySeamTests](tests/PitMine3D.Kylin.Tests/CoalStatsBySeamTests.cs) +2(A{10..50}→min10/max50/中位30/均30·B{5,15}→均10·缺指标跳·CSV 头+行) · [BoxPlotTests](tests/PitMine3D.Kylin.Tests/BoxPlotTests.cs) +2(共享量程须/中位线/类别轴名/空零). **build 0 错·单测 1325→1329**。

**本会话第 62 功能**。教训: **第八角度(DB 聚合 present-but-partial)连出两功能**(§二五四 化验覆盖 · §二五五 分煤层箱线)——原分析窗常出比 Kylin 命令更细的**分组/分位/覆盖**统计, 逐窗对指标即挖。四类图(折线/柱/散点/箱线)补齐, 统计可视基本全谱。见 [[unlock-blocked-insights]]。

---

## §二五六 设备主控因素分析（因素-产能相关排名）—— DB 聚合 present-but-partial 续

**第八角度续**: 原 `EquipmentAnalysisWindow` 除 OEE/趋势/故障 Pareto(Kylin 已覆盖)外, 另有**因素分析(主控因素 + 相关性)**: 各因素(可用率/作业率/利用率/内外故障率)与产能做 Pearson 相关, 找主控因素 + 建议。Kylin `设备数据分析`(ProductionStatsCmd) 只出生产统计, **无因素相关**。

**补** [EquipmentFactorAnalysis](src/Data/EquipmentFactorAnalysis.cs)(纯核): `Correlate(rows)` 各因素对产能 Pearson r, 按 |r| 降序 + 正/负向 + 强(≥0.7)/中(≥0.4)/弱; `Pearson`(长度不等/<2/无方差→null)。数据经 [GeoDataQueries](src/Data/GeoDataQueries.cs) `GetEquipmentFactorRows`(equipment_kpi_monthly ⋈ capacity_monthly on equipment_id/year/month)。命令 `设备因素分析`/`主控因素`([MainWindow](src/Views/MainWindow.axaml.cs) `EquipmentFactorCmd`): 相关排名 + 有向 r 柱上屏 + 主控因素状态行。

**验证(合成已知值)**: [EquipmentFactorAnalysisTests](tests/PitMine3D.Kylin.Tests/EquipmentFactorAnalysisTests.cs) +3—— Pearson 完美正/负 =±1·无方差/样本<2→null; 可用率↑同产能↑→强正 r=1·内部故障率↓同产能↑→强负 r=-1·利用率恒定略去·按 |r| 排名; <2 行返空。**build 0 错·单测 1330→1333**。

**本会话第 63 功能**。教训: **第八角度(DB 聚合 present-but-partial)连出三功能**(§254 化验覆盖·§255 分煤层箱线·§256 设备主控因素)——原分析窗常在同数据上多算**分组/分位/相关**维度, Kylin 命令只取其一。核可合成向量验(不依赖种子对齐)。见 [[unlock-blocked-insights]]。

---

## §二五七 设备效能 What-if 提升路径模拟（原 EquipmentForecastWindow 四杠杆）

**第八角度续**: 原 `设备效能预测` 实为 **What-if 提升路径模拟器**(4 杠杆滑块→实时产能提升), Kylin `设备效能预测`(EfficiencyForecastCmd) 只做**静态基线+投影**, 无 what-if。

**补** [EfficiencyWhatIf](src/Data/EfficiencyWhatIf.cs)(忠实原模型): `Simulate(baseOutput, faultShare, l1..l4)` = 基线 × (1 + Σ杠杆贡献 × 协同衰减0.85)。四杠杆: 故障降低 c1=l1×faultShare(故障工时占比, 解锁工时) · 出动率 c2=l2 · 装载 c3=l3 · 运距 c4=l4×0.6(仅60%转产能, 余在卡车侧)。[GeoDataQueries](src/Data/GeoDataQueries.cs) `GetFaultShare`(ΣFault/ΣPlan)。命令 `效能提升模拟 <故障降低%> <出动率%> <装载%> <运距%>`([MainWindow](src/Views/MainWindow.axaml.cs) `EfficiencyWhatIfCmd`): 基线(GetEfficiencyForecast)+故障占比 → 模拟产能 + 各杠杆贡献柱上屏。

**验证(合成已知值)**: [EfficiencyWhatIfTests](tests/PitMine3D.Kylin.Tests/EfficiencyWhatIfTests.cs) +3—— base100·fs0.2·l(0.5,0.1,0.1,0.2): c1=0.10/c2=0.10/c3=0.10/c4=0.12→raw0.42→gain0.357→模拟135.7·增35.7; 零杠杆守基线; faultShare 夹 [0,1]。**build 0 错·单测 1333→1336**。

**本会话第 64 功能**。教训: **第八角度(DB 聚合窗 present-but-partial)连出四功能**(§254 化验覆盖·§255 分煤层箱线·§256 主控因素·§257 效能 what-if)——原分析窗常比 Kylin 命令多一层(分位/相关/what-if 情景), 逐窗读原计算即挖, 纯核合成验。见 [[unlock-blocked-insights]]。

---

## §二五八 路网瓶颈段分析（边介数中心性）—— present-but-partial 拓展至 RoadLib

**第八角度拓展至 RoadLib**: 原 `TransportIndicators` §1.3.5 出**瓶颈段**: score = 介数(betweenness) × 车道因子 × 陡坡因子, 主因(单车道|陡坡|高介数|禁行)。Kylin 有路网(RoadNetwork Dijkstra/OD)但**无瓶颈段/介数分析**。

**补** [RoadNetwork](src/Cad/RoadNetwork.cs) `EdgeBetweenness(adj, sources, sinks)`(所有源×汇 Dijkstra 累计每边被最短路经过次数, 按介数降序) + `DanglingEndpoints`(度1端点=天然出入口)。命令 `瓶颈段分析`/`关键路段`([MainWindow](src/Views/MainWindow.axaml.cs) `RoadBottleneckCmd`): 场景中线建网 → 端点(或全节点截40)为源汇 → 边介数 → 前 5 高流量段红粗上屏。

**忠实取舍**: 原另乘 车道/陡坡因子, 但 Kylin 路网为**中线几何最小模型**(RoadEvolutionModel 注"原 RoadEdge 的最小替代", 无车道/坡度/状态) → 该加权记录待边属性模型; 介数核(图论中心性)完整可移可验。

**验证(已知图)**: [EdgeBetweennessTests](tests/PitMine3D.Kylin.Tests/EdgeBetweennessTests.cs) +3—— 链 0-1-2-3 端点源汇→三边各介数2; Y 型茎边介数4(最忙, 降序首位); 不连通对跳不抛。**build 0 错·单测 1336→1339**。

**本会话第 65 功能**。教训: **第八角度(present-but-partial)不限 GeoDataBase, 拓展到 RoadLib/PointCloudLib 等**——逐分析器读原多算的维度(此为图介数)。数据模型缺属性时取可验证核(介数) + 记录加权refinement(车道/坡度)。见 [[unlock-blocked-insights]]。

---

## §二五九 中长远进度计划排产（整模块缺失，第九角度=孤儿标签/无实现）

**第九收敛角度**: 前八角度(测试/命令/格式/引擎/自标限制/导出实体/导入/DB聚合)均未捕获此项——`中长远` 仅存于 [MainWindow.axaml](src/Views/MainWindow.axaml) 的**孤儿 ribbon 标签 + 图标**(无 `cmd==` 处理), 故 §250 整串比对未标 0 命中(标签在 XAML 里), 实则**整个中长远进度计划模块无实现**。原 `PlanLib.LongTerm.LongTermScheduler`(量版合成排产)是真算法。

**补** [LongTermScheduler](src/Cad/LongTermScheduler.cs)(忠实原量版合成分支): `Schedule(plan)` = 划期 → 达产爬坡(r0 按线性/阶梯/激进)分配采出量 → 超前剥离反推(合成剖面 ratio0=base+(peak−base)√progress 削峰) → 逐年现金流/NPV(内排省 15%运费) → Periods + Evaluate(服务年限/达产期/NPV/回收期/产量CV/剥采比CV/储量均衡/规范服务年限校核 GB50197)。工作线长×模式×方位→峰值因子。`AdvanceRateFrom`(推进度=能力·1e4/(线长·台阶高·密度))。模型 `LongTermPlan`/`PlanPeriod`/`LongTermResult` + 枚举。命令 `中长远进度计划 [能力] [储量] [基准剥采比] [爬坡型]`([MainWindow](src/Views/MainWindow.axaml.cs) `LongTermPlanCmd`): 排产 + 逐年剥采比曲线上屏 + CSV(年/相时/能力/煤/剥离/剥采比/累计/推进/排土/现金流/NPV) + 结果摘要。

**忠实取舍**: 真剥采比场模式(块体采样逐列真地质序列)= Phase 2, 需 StripRatioFieldSampler, 记录; 量版合成核完整可移可验。多方案生成(工作线×方向笛卡尔积)接口原有, Kylin 已有 派生计划方案(§) 覆盖多方案需求。

**验证(已知值+不变量)**: [LongTermSchedulerTests](tests/PitMine3D.Kylin.Tests/LongTermSchedulerTests.cs) +4—— AdvanceRateFrom 100/(1200·12·1.35)×1e4=51.44·退化0; 服务年限分级 1000→30/.../50→10; 基建期无煤有剥离负CF·生产采出总量≈储量·首年能力35%(r0)·服务年限=生产年数·峰值剥采比∈(base,7.7]·NPV=Σ折现; 确定性(同输入同输出)。**build 0 错·单测 1339→1343**。

**本会话第 66 功能**。教训: **第九角度=孤儿标签(XAML/目录有标签但无 `cmd==` 处理)**——整串命令比对(§250)会被 XAML 里的孤儿标签骗过(标签在但无实现)。判据: **命令标签比对须查是否有对应 `cmd==` 分派处理, 而非仅字符串存在**。此为最大单项缺口(整规划模块), 前八角度全漏, 靠"逐模块读原 *Scheduler/*Plan 有无 Kylin 对应命令处理"挖出。见 [[unlock-blocked-insights]]。

---

## §二六〇 短期(月度)生产计划排产 —— 孤儿标签续(规划模块第二块)

**第九角度(孤儿标签)续**: `短期生产计划编制`/`月度计划编制`/`短期进度计划动态模拟` 同为 XAML 孤儿标签(无 `cmd==` 处理)。原 `PlanLib.ShortTerm.ShortTermScheduler`(月度排产)是真量算, 平行 LongTerm。

**补** [ShortTermScheduler](src/Cad/ShortTermScheduler.cs)(忠实原, 展平配置): `Schedule(plan)` = 划月 → 月权重(有效作业日×设备可用×作业组织形态 DispatchShape) → 摊年目标 → 均衡平滑(份额=OutputSmooth/Sum) → 月产上限裁剪回摊(RedistributeCeiling 6 迭代) → 月剥采比剖面(强采月偏高, 上限裁剪) → 推进/设备利用/累计/完成率 → Evaluate。`WorkdaysFor`(标准作业日×冬季降效[12,1,2×0.8]×检修降效[7月×0.7]×工作历[抢产1.10/保守0.92])。模型 `ShortTermPlan`(展平 Field/Balance/Faces)/`MonthPeriod`/`ShortTermResult` + 枚举 DispatchStrategy/CalendarScenario。命令 `短期生产计划 [年煤目标] [基准剥采比] [组织] [工作历]`([MainWindow](src/Views/MainWindow.axaml.cs) `ShortTermPlanCmd`): 排产 + 月产柱上屏 + CSV + 摘要。

**忠实取舍**: 备采保有月数校核(Mineable.PreparedMonths)需备采储量配置, 略去该 Ok 项(记录); 余全移。

**验证(已知值+不变量)**: [ShortTermSchedulerTests](tests/PitMine3D.Kylin.Tests/ShortTermSchedulerTests.cs) +4—— WorkdaysFor 常规25/冬20/检修17.5/抢产27.5/保守23; 12月年目标守恒(±1)·完成≈100%·末月累计100%·各月剥采比≤12·默认可行; 紧上限85集中强采削峰无月超85·总近守恒(6迭代近似, 忠实原); 集中强采月产CV>均衡型。**build 0 错·单测 1343→1347**。

**本会话第 67 功能**。规划模块(中长远§259 + 短期§260)两大块补齐; 动态模拟/出图属可视 refinement。第九角度(孤儿标签)已连出规划两功能。见 [[unlock-blocked-insights]]。

---

## §二六一 孤儿标签全量三分（第九角度收官）

**第九角度(XAML 孤儿标签: `Tag=` 有按钮但无 `cmd==` 处理)全量枚举三分**: 276 XAML Tag × 1232 dispatch 串比对, 排除绘图基元(经 `ActivateDrawTool` 路由非 cmd==)后, 孤儿标签三分:

- **真缺可移(已补)**: `中长远进度计划编制`→§259 · `短期生产计划编制`/`月度计划编制`→§260(规划模块两大块, 真量算 Scheduler)。
- **别名(已接)**: `确定开采程序`→`开采程序确定`(AdvanceCmd 平行推进) · `运量驱动布线`→`运输布局方案`(RoadLayoutCmd, 原亦 CreateRoadLayoutCommand)。
- **真阻记录**:
  - TaskLib 调度(任务下达/生产任务书/生产任务编制/生产任务动态调整/班次日历/班组派工/派车单/去向台账/检修档期/采排配对/编制配置/采掘单元清单/钻爆计划衔接/作业区划分): **SampleTaskBoard 样例数据 UI 桩**(23 文件用 SampleTaskBoard, 无真域模型, 同 §工序定额/质量标准), 忠实不臆造。
  - MineAss 编辑(创建工作线/坑线落地/处理尖灭/局部台阶/最终并段/编辑台阶/动态调整/撤销坑线/约束条件设置/增量增删边/延拓触发设置/排土模板/排土场放坡/平盘联络道/结构路面): 路由 **`IPitDesignCapability`(原生坑设计引擎)**, Kylin 无该原生能力, 记录。
  - 原生/显示(加载倾斜摄影 OSGB/影像底图 raster/点云管理/渲染配置/显示隐藏/现状写实/补勘钻孔写实/转化为三角格网/破碎站位置/煤层露头着色): 原生/OSGB/渲染, 记录。
  - 规划可视(中长远规划动态模拟/短期进度计划动态模拟/进度计划方案出图): 排产核已补(§259/260), 动态模拟/出图=可视 refinement, 记录。

**教训**: **第九角度=孤儿标签(XAML `Tag=` 无 `cmd==` 分派)是最大盲区**——前八角度全漏(整串命令比对被 XAML 标签骗过)。全量三分后: 真缺者补(规划两块)、别名接、余为 SampleTaskBoard 桩/原生 IPitDesignCapability/OSGB 渲染, 忠实记录。**判据: XAML Tag 全量 × dispatch 串比对(排除 ActivateDrawTool 路由), 逐孤儿查 Placeholder/OpenWindow(样例)/Capability(原生)/真算法**。见 [[unlock-blocked-insights]]。

---

## §二六二 规划一键编制多方案对比（中长远 + 短期）—— 规划模块收官

**补两 Scheduler 的「一键编制」主路径**(§259/260 只补单方案): 原 AutoCompose = 正交派生多方案 → Comparer 综合评分 → 荐最优。
- **中长远(§68)**: [LongTermScheduler](src/Cad/LongTermScheduler.cs) `GenerateVariants`(4 工作线×4 方向=16 方案) + `LongTermComparer.Score`(六指标 min-max 归一×权重 稳产0.18/削峰0.22/早达产0.15/内排0.15/NPV0.18/均衡0.12, 荐可行最高分) + `DecisionWeights`/CompositeScore。
- **短期(本节)**: [ShortTermScheduler](src/Cad/ShortTermScheduler.cs) `GenerateVariants`(3 作业组织×3 工作历=9 方案) + `ShortTermComparer.Score`(五指标: 完成偏差 low/月产均衡 high/利用率贴 90% high/峰月 low/推进 high, 权重 0.28/0.22/0.18/0.16/0.16)。

命令加 `一键`/`多方案`/`对比` 模式: 生成全方案→评分→推荐 + 前三 + 评分柱上屏 + CSV(方案/综合分/各指标/可行)。

**验证(已知值)**: LongTermSchedulerTests +2(16 方案·推荐可行最高分·单方案退化 50 分) · ShortTermSchedulerTests +1(9 方案·推荐可行最高分)。归一 min-max、荐"可行优先最高分"逻辑与原一致。**build 0 错·单测 1347→1350**。

**本会话第 68-69 功能**。规划模块至此收官: 中长远/短期 各 单方案排产(§259/260) + 多方案一键评分推荐(§68/69); 余动态模拟/出图=原版 Placeholder 桩或可视 refinement。**第九角度(孤儿标签)总产出: 图介数§258 + 规划四功能(§259/260/68/69)**——整规划子系统前八角度全漏, 靠孤儿标签挖出并完整补齐。见 [[unlock-blocked-insights]]。

---

## §二六三 孤儿标签逐条路由复核收官（每条个别验证）

**§261 三分 + §262/§70 复核后, 逐条查每个孤儿的实际工厂方法路由**（非凭标签所属模块臆断——§70 已证 结构路面 表面 MineAss 实则 RoadLib 托管）:

- **native `IPitDesignCapability`(坑设计引擎, 交互编辑)**: 创建工作线/组合工作线/坑线落地/局部台阶/编辑台阶/动态调整/处理尖灭/外排放坡/平盘联络道 —— 逐条查得均 `Capabilities.TryGet<IPitDesignCapability>`, 真原生。
- **`SkeletonCommand` 原版桩**: 最终并段/内排推进 —— 原版即"待实现"骨架, 忠实不补。
- **配置对话框(非算法)**: 约束条件设置 → `ConstraintSettings`(运输约束: 卡车尺寸/限坡, Kylin 有 TransportConstraintSettings 模型被 ramp/断面用); 忠实不臆造 config-only 设置项。
- **Kylin 反超**: 排土场容量校核(原 SkeletonCommand, Kylin `DumpCapacityAsync` 已实现填方体积) · 虚拟钻孔(原 Placeholder, Kylin 已实现)。
- **别名接**: 转化为三角格网→创建三角网 · 确定开采程序→开采程序确定 · 运量驱动布线→运输布局方案。
- **托管几何(已补)**: 结构路面(§70)。
- **交互编辑器/原生显示**: 排土模板(模板编辑器) · 加载倾斜摄影(OSGB) · 影像底图(栅格叠加显示) · 点云管理/渲染配置/现状写实/补勘钻孔写实(3D 写实/渲染) · 增量增删边(交互图编辑) · 延拓触发设置(config)。

**结论**: 孤儿标签逐条个别验证收官——托管可移者仅 结构路面(§70)一条(已补), 余为 native 坑设计引擎/原版桩/config/交互/原生显示/已覆盖。**教训: 孤儿"native"须逐条查工厂方法实际路由(TryGet<Capability> vs new 托管类 vs SkeletonCommand vs OpenWindow), 勿凭标签所属 Tab 臆断**——第九角度至此完全落地。见 [[unlock-blocked-insights]]。

---

## §二六四 路网运输指标统一报表（方法级测试复审——第十角度）

**第十角度: 原版 `*Tests.cs` 方法级复审(§249 只到文件级)**。逐测试文件看**具体测的方法**, 验 Kylin 是否覆盖每个行为。RoadLib 测试(8 文件)复审: RoadLayoutSolver/RoadCrossSection/PathSolver(Dijkstra/KShortest)/StructurePavement(§70)/RoadNetwork 均覆盖; ExtendTriggerSettings=config→options 映射(非算法); **`TransportIndicatorsBuilder.Compute` 出统一几何报表**(总里程 + OD 可达对均/最 + 瓶颈), Kylin 有零件却散(总长在中心线管理、OD 需用户 CSV、瓶颈 §258)——**无从场景路网直算的统一报表**。

**补** [RoadNetwork](src/Cad/RoadNetwork.cs) `NetworkIndicators(adj, sources, sinks)`(总里程=去重边长和 + 源×汇有序对最短路 可达对数/均值/最大, 不可达∞不计) + `NetworkStats`。命令 `路网运输指标`([MainWindow](src/Views/MainWindow.axaml.cs) `RoadTransportIndicatorsCmd`): 场景路网→端点(或全节点截40)源汇→总里程 km + 可达对均/最运距 + 最忙段(介数)一体报表。

**忠实取舍**: 原另有运量加权均值/成本(需吨量+采矿模型), Kylin 2D 路网无吨量/逐边坡度→记录; 纯几何可达指标(里程/OD/介数)完整可移可验(同 §258 介数, 车道/坡度加权待边属性)。

**验证(已知图)**: [EdgeBetweennessTests](tests/PitMine3D.Kylin.Tests/EdgeBetweennessTests.cs) +2—— 链 0-1-2-3(边3/4/5)总里程12·端点源汇2对·均/最12; 两分量总里程5·可达4对(跨分量∞不计)·均2.5/最3。**build 0 错·单测 1354→1356**。

**本会话第 71 功能**。教训: **第十角度=原版测试方法级复审**——文件级映射(§249 19/19)后, 逐测试文件看具体测的方法, 验 Kylin 覆盖每行为。RoadLib 8 测复审挖出统一运输指标(散在多命令未成一体)。方法级比文件级细一层。见 [[unlock-blocked-insights]]。

---

## §二六五 块体煤岩判别器（类别码集）—— 第十一角度：分析类后缀枚举

**第十一角度: 域类枚举扩到分析/分类后缀**(角度4 只 `*Engine/*Solver/*Builder/*Optimizer`)。枚举原版 `*Analyzer/*Classifier/*Detector/*Identifier/*Resolver/*Calculator/*Sampler`, 逐一对 Kylin: 多数覆盖(DepositAutoDetector→矿床识别·MineableAreaIdentifier→确定可采区域·BenchWidthIdentifier→采场参数识别·BenchAnalyzer/AdvancePlanner/RoadLayoutPlanner/ProgramEvaluator/StripRatioFieldSampler 皆有), 但 **`CoalRockClassifier`(BlockModelLib/Domain)Kylin 缺**——Kylin 判煤/岩用"品位≥限值"(连续品位), 原版另有**类别码集判别**(属性为岩性码而非品位, 多层煤/多层岩码 + 容差, 既非煤又非岩=忽略), 供**类别型块体模型**。

**补** [CoalRockClassifier](src/Cad/CoalRockClassifier.cs)(忠实原): `IsCoal(v)`=v 容差内命中煤码; `IsRock(v)`=非煤 & (岩集空→非煤即岩 | 岩集非空→须命中岩码)。命令 `块体煤岩分类 煤 <码...> [岩 <码...>] [容差 <t>]`([MainWindow](src/Views/MainWindow.axaml.cs) `BlockCoalRockCmd`): 按块体品位作岩性码分煤/岩/忽略, 报各类块数+体积+剥采比。与"品位≥限值=煤"(连续)互补(此对离散码)。

**验证(已知值)**: [CoalRockClassifierTests](tests/PitMine3D.Kylin.Tests/CoalRockClassifierTests.cs) +4—— 煤码{1,3,5}容差0.5: 命中/边界5.5/2 非煤; 岩集空→非煤即岩; 岩集{2}→值5 既非煤又非岩=忽略(双 false); 容差2 加宽。**build 0 错·单测 1356→1360**。

**本会话第 72 功能**。教训: **第十一角度=域类枚举扩后缀**(角度4 的 Engine/Solver/Builder 之外, 加 Analyzer/Classifier/Detector/Identifier/Resolver/Sampler/Evaluator)——挖出 present-but-partial 判别器(Kylin 品位阈值 vs 原类别码集)。**判据: 域类枚举勿限一组后缀; 分类/判别类常有 Kylin 简化版缺的模式(连续 vs 离散)**。见 [[unlock-blocked-insights]]。

---

## §二六六 采场/排土场自动识别（LandformClassifier 栅格极性分类）—— 第十一角度续

**第十一角度(分析类枚举)续挖最大单项**: 原 `PlanLib.ShortTerm.LandformClassifier`(550 行栅格管线)Kylin 缺——从坡顶/坡底台阶线自动圈定 **采场/外排土场/内排土场**。Kylin 有 RasterMorphology 原语 + BenchWidthIdentifier(平盘宽区域)但**无采排极性分类**。

**补** [LandformClassifier](src/Cad/LandformClassifier.cs)(忠实全移, 自包含):
① 足迹 = 台阶线加密栅格化 + 闭运算桥接 + 填洞;
② DEM(多源 BFS 最近种子高程) → 中尺度 BoxBlur(40m, summed-area table) + 趋势面 BoxBlur(600m); 残差 = 平滑现状 − 趋势; 极性: 凹(残差&lt;−ε)=采场·凸(&gt;+ε)=排土(ε=min(2,0.25T), T=max(6,0.5·std));
③ 闭运算并块(140m) + 连通域(8-连通) + 绝对面积(minAreaHa) & 相对面积(20%最大块)双过滤 + Moore 外轮廓 + DP 简化;
④ 内/外排: 排土块外环带(120m)平均残差&lt;−0.5T(被坑壁围)=内排;
+ 成对台阶线过滤(剔孤立线)+ CarveForeignHoles(异类嵌洞开通道保区域互不相交)。命令 `采场排土场识别 [栅格m]`([MainWindow](src/Views/MainWindow.axaml.cs) `LandformClassifyAsync`): 台阶线 CSV(lineId,x,y,z)→分类→采场红/外排棕/内排橙 区域多边形上屏 + 计数。

**验证(已知值+集成)**: [LandformClassifierTests](tests/PitMine3D.Kylin.Tests/LandformClassifierTests.cs) +5—— Components(分离块+minCells)·FillHoles(环填洞)·Simplify(共线→首末)·空输入不抛; **集成: 碗地形(中心60边100)台阶线→识别出采场(pit)**。**build 0 错·单测 1360→1365**。

**本会话第 73 功能**(最大单项, 550 行栅格管线机械移植)。教训: **§52 "高估复杂度"再验——550 行看着吓人, 实为自包含纯栅格算子(Dilate/Erode/Close/FillHoles/BoxBlur/Components/TraceBoundary/DP/环带残差), 逐个机械移植即成**。InternalsVisibleTo 让内部算子逐个已知值验 + 碗地形集成验。见 [[unlock-blocked-insights]]。

---

## §二六七 文字高度归一化（Host/Cad 目录 —— Modules 之外的算法）

**第十二角度: 枚举 Host/PitMineApp/Cad/(Modules 之外的 app 自带 CAD 算法)**。前番类枚举只覆 Modules; Host/Cad 有导入/导出/UndoRedo/FileTree/属性 基础设施——多覆盖(格式服务/文件树/属性), 但挖出 **`TextHeightNormalizer`** Kylin 缺: 修正导入 DXF 里"文字高度相对图幅异常巨大/缺失"(paper-space 当 model-space、mm 当 m、占位值)。

**补** [TextHeightNormalizer](src/Cad/TextHeightNormalizer.cs)(忠实全移): ① 几何算图幅对角线 D; ② 扫文字高度挑正常范围 [D·0.0001, D·0.05] 取中位数 typical; ③ 逐文字 高度&gt;D·0.05(离群)或≤0(缺失)→typical, 其余原样; 全异常时 typical 兜底 D·0.005。命令 `字高归一化`([MainWindow](src/Views/MainWindow.axaml.cs) `TextHeightNormalizeCmd`): 场景几何算图幅→归一文字高度→报修正条数。

**验证(已知值)**: [TextHeightNormalizerTests](tests/PitMine3D.Kylin.Tests/TextHeightNormalizerTests.cs) +3—— 图幅1000² D≈1414: 正常{4,5,6}中位5, 591离群/0缺失→5(修正2条)、正常原样; 全异常→兜底 D·0.005; 无几何→正数原样非正给1。**build 0 错·单测 1365→1368**。

**本会话第 74 功能**。教训: **第十二角度=Host/App 自带 Cad 目录(Modules 之外)**——类枚举须含 app 主程序的 Cad/ 子目录, 非仅插件 Modules。又证"声明收敛后仍有盲区"(此前claim class-level 完整, 漏了 Host/Cad)。见 [[unlock-blocked-insights]]。

---

## §二六八 DB 模式收敛复核（第十三角度：表级比对）+ 类枚举全源目录收官

**第十三角度: DB 模式表级比对**。提原版服务/迁移引用的全部表 × Kylin 迁移 CREATE TABLE。Kylin 56 迁移表**覆盖所有有 C# 消费者的表**。差集仅二:
- `shift_leaders`(班组长)——**仅 ETL python(import_csv_to_db.py)导入目标, 无任何 C# 服务读**(grep 空), 不背任何 app 功能, Kylin 缺无碍。
- `v_borehole_column`/`v_borehole_segments`/`v_coal_sample_*`——DB **视图**(非表), Kylin 以直查基表(GetBoreholes/GetCoalSamples 逐孔/逐样组装)替代, 功能覆盖。

**结论**: Kylin DB 模式对每个 app 功能所需表**全覆盖**; 唯一缺表 shift_leaders 无消费者。

**十二/十三角度连续两轮零缺**(DB 方法级 §二六七尾 + DB 模式本节), 在类枚举全源目录(Host+Modules+Platform)收官后——最强收敛信号。**类枚举收官记**: Modules(插件)+ Host/PitMineApp/Cad(主程序自带 CAD, §74 挖出 TextHeightNormalizer)+ Platform(infra/native)全枚举; Host 其余(节点编辑器 11/11 类节点·AcadColorTable=ACadSharp·DimensionStyle/Render/LayerManager 纯 UI)覆盖。

**教训: DB 模式表级比对是独立收敛角度**——但须辨 ETL-only 表(无 C# 消费, 缺无碍)与视图(直查基表替代)。见 [[unlock-blocked-insights]] [[pitmine3d-command-inventory]]。

---

## §二六九 导入爆破记录（数据导入完整性 —— 第十四角度）

**第十四角度: 数据导入完整性**(原 ETL 导入的表 × Kylin 导入命令)。原 import_csv_to_db.py 导入 blast_event 等; Kylin 有各分析表导入(生产/KPI/产能/故障/煤质/见煤/观测点/台账/路况/边坡/模板) 但 **缺 `导入爆破记录`**——`爆破分析`(GetBlastStats §41)是真功能, 但 blast_event 数据无导入命令(非迁移种子, 仅运行时 pmgeo.db 有), 用户无法灌入自有爆破数据。

**补** [GeoDataQueries](src/Data/GeoDataQueries.cs) `ImportBlastEvents`(blast_date 必填, 余选填; 缺 unit_consumption 由 explosive/volume 算) + 命令 `导入爆破记录`([MainWindow](src/Views/MainWindow.axaml.cs) 复用 `ImportCsvToDbAsync`, 列 blast_date[,location_code,drill_id,material,diameter_mm,hole_count,total_hole_length_m,explosive_kg,blast_volume_m3,unit_consumption_kg_m3]) + 目录。与其它分析表导入对齐, 爆破分析至此可灌用户数据。

**验证(导入→查询往返)**: [ImportBlastEventsTests](tests/PitMine3D.Kylin.Tests/ImportBlastEventsTests.cs) +2—— 3 行(2 有效·1 缺日期跳)→ Inserted2/Errors1, GetBlastStats 总方量25000/总药5000/孔长1000/地点2/综合单耗; 缺单耗自算 1200/4000=0.3。**build 0 错·单测 1368→1370**。

**本会话第 75 功能**。教训: **第十四角度=数据导入完整性**(ETL 表 × 导入命令)——有分析功能的表须有导入命令(否则功能只对种子/运行时数据可用)。爆破是唯一缺口(其它分析表导入齐)。见 [[unlock-blocked-insights]]。

---

## §二七〇 导入设备型号（数据导入完整性续 —— 第十四角度收官）

**第十四角度续**。`equipment_model`(机型库)被 机型KPI/`FleetOptimizer`(车队配比 §35)读, 但**仅迁移种子, 无导入命令**——用户无法登记自有机型。补 [GeoDataQueries](src/Data/GeoDataQueries.cs) `ImportEquipmentModels`(model+category 必填, 余选填; 按 model 主键 `INSERT OR REPLACE` upsert) + 命令 `导入设备型号`([MainWindow](src/Views/MainWindow.axaml.cs) 复用 `ImportCsvToDbAsync`, 列 model,category[,working_weight_t,power_kw,bucket_m3,load_t,dimensions_lwh,drill_diameter_mm,tire_spec,std_daily_cap_wan_m3]) + 目录。

**验证(已知值)**: [ImportEquipmentModelsTests](tests/PitMine3D.Kylin.Tests/ImportEquipmentModelsTests.cs) —— 缺 model/category 跳过; upsert 覆盖同型号。**build 0 错·单测 1370→1371**。

**本会话第 76 功能**。至此**数据导入完整性角度收官**: 有分析功能的表(生产/KPI/产能/故障/煤质/见煤/观测/台账/路况/边坡/模板/爆破/机型)导入命令齐全; 余 ETL 表(daily_mine_summary/long_term_metric 无 Kylin 读者、shift/dispatch/workforce 背桩)无需导入。见 [[unlock-blocked-insights]]。

---

## §二七一 节点编辑器 撤销/重做/删除（第十五角度：最新构件深查 —— 结构操作完整性）

**第十五角度: 最新构件深查**。近两 commit 刚建节点编辑器(节点图模型 + 画布 增/拖/连)。查其是否**结构操作完整**: 原 `NodeGraph` 有 `RemoveNode`/`Disconnect`/`DeleteSelectedConnections` + `NodeGraphUndoRedo`(快照栈撤销/重做, 带专测 NodeGraphUndoRedoTests); Kylin `NodeGraph` **仅 AddNode/Connect/Find/Evaluate**——**能加不能删, 无撤销/重做**。这是真·可验证·忠实缺口(原实现非桩)。

**先辨忠实边界**: 原几何节点 `Evaluate` 是**桩**(`// TODO: C++ bridge` → `return (ulong)0`), Kylin 反而实产托管几何(标准 native→managed 等价, §节点求值已完成)——故求值层 Kylin ≥ 原, 非缺口。缺口在**图结构操作 + 撤销/重做**(原已实现且带测)。`DeleteSelectedConnections`/`ClearSelection` 依赖 `ConnectionModel.IsSelected`(UI 选择态), Kylin 无头模型不背, 属视图层——只移植模型级操作。

**补**:
- [NodeGraph](src/Nodes/NodeGraph.cs) `RemoveNode(id)`(删节点+触及连线)、`Disconnect(toNode,toPort)`(输入口独占故唯一定位)、`CaptureState()`/`RestoreState(snap)`(整图快照/重建, 忠实原: 节点重建ID可变+连线按旧ID映射重连=值等价)。
- [NodeGraphUndoRedo](src/Nodes/NodeGraphUndoRedo.cs)(新) —— 忠实原快照栈协议: `SaveState` 改动**前**调用(压撤销栈+清重做栈)、`Undo`(当前压重做栈→弹撤销栈还原)、`Redo`(对称)、`CanUndo`/`CanRedo`, `_isRestoring` 守卫防还原期自污染。
- [NodeEditorWindow](src/Views/NodeEditorWindow.axaml.cs) 接线: 单击选中(蓝框高亮)、工具栏 删除/撤销/重做 + 键 Delete/Ctrl+Z/Ctrl+Y; 每次改动(增/连/删/改值/拖拽)前 `SaveState`(拖拽仅真移动时一次, 改值 Enter+失焦双触发守卫)。

**验证(已知值)**:
- [NodeGraphTests](tests/PitMine3D.Kylin.Tests/NodeGraphTests.cs) +2 —— RemoveNode 删节点+连线/不存在→false; Disconnect 清输入口连线/节点留/再断→false。
- [NodeGraphUndoRedoTests](tests/PitMine3D.Kylin.Tests/NodeGraphUndoRedoTests.cs) +6 —— 新控制器不可撤/重; 加节点撤销→空/重做→现; 连线撤销(节点留)/重做(按旧ID映射重连); 撤销还原参数值; 撤销后新 SaveState 清重做栈; 端到端 Number(5)→Circle→Bake 撤销→空烘焙/重做→半径5等价。

**build 0 错·单测 1371→1379**(+8)。

**本会话第 77 功能**。教训: **第十五角度=最新构件深查**——刚建的构件("增/拖/连"够演示但未必够用)最易漏结构操作(删/断/撤销)。又证**忠实是双向的**: 原几何节点求值是桩(TODO C++), Kylin 实现托管几何=超出原桩但属标准 native→managed 等价(非无源臆造); 缺口只认原**已实现且带测**者(RemoveNode/undo)。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二七二 节点图 保存/加载 JSON（第十五角度续 —— 持久化完整性）

**第十五角度续**(最新构件深查)。原节点编辑器 `OnSaveClick`/`OnLoadClick` 把图**存/取 JSON**(SaveFileDialog "保存节点图" → `JsonSerializer.Serialize(snapshot)`; OpenFileDialog "加载节点图" → `Deserialize<GraphSnapshot>` → SaveState+RestoreSnapshot, 失败弹错不动现图)。Kylin 刚补撤销/重做(§271)但**仍无存/取**——图关窗即失。

**补**:
- [NodeGraph](src/Nodes/NodeGraph.cs) `ToJson()`/`LoadJson(json)` —— 用 JSON 友好 DTO(节点 Id/Kind/X/Y + **值以规范字符串编码**; 连线 4 元组)。Kylin 快照含 `object?`(Vec3/double/…)不宜直序列化, 故 DTO 存字符串值, 几何默认值由 `Spec(kind)` 定不入盘。`LoadJson` 清空→按类型建点→解码值→按旧ID映射重连; **JSON 非法则捕获不抛、不动现图**(忠实原加载失败保留现图)。
- **值编解码归一**: 抽 `NodeGraph.EncodeValue`/`DecodeValueInto` 为规范实现, UI 卡片显示/内联编辑(ValueText/ApplyValue 改为委托)与 JSON 存盘**同一套**, 杜绝双份漂移。
- [NodeEditorWindow](src/Views/NodeEditorWindow.axaml.cs) 工具栏 保存/加载 + Avalonia StorageProvider 文件对话框(`*.json`); 加载前 `SaveState`(可撤销), 失败提示不动现图。

**忠实边界**: 盘上格式为 Kylin 原生(int Id + NodeKind 枚举 + 字符串值), 非与原(Guid+TypeName+EditableValue)字节一致——跨平台不同模型无法字节对齐, 移植的是**功能**(存/取节点图), 非文件格式。

**验证(已知值)**: [NodeGraphTests](tests/PitMine3D.Kylin.Tests/NodeGraphTests.cs) +2 —— Point(3,4)+Number(5)→Circle→Bake 存 JSON→新图 LoadJson→4 节点/3 连线, 端到端烘焙仍出 圆心(3,4)/半径5, 位置(10,20)保真; 非法 JSON→保留现图不抛。**build 0 错·单测 1379→1381**。

**本会话第 78 功能**。节点编辑器至此**结构操作 + 撤销/重做 + 持久化**齐全, 与原对齐(几何求值 Kylin 反超原桩)。教训: 最新构件深查须覆盖**持久化**(存/取)——"能编辑不能存"是常见半成品。见 [[unlock-blocked-insights]]。

---

## §二七三 节点编辑器 收官复核 + 邻近组件成熟度确认（第十五角度收束）

节点编辑器补齐(§271 撤销/重做/删除节点/断开连线 + §272 JSON 存取 + 连线单击断开 UI)后, 与原对齐: **增/删节点·连/断线·拖拽·求值(11类)·烘焙到场景·撤销重做·JSON存取·参数值编辑**。

**邻近"近期构件"深查(第十五角度扫尾), 均成熟无缺**:
- **对象管理器**(ObjectTree): 右键菜单齐全(特性/快速选择/全选·复制/剪切/粘贴/删除·隐藏对象/隐藏同层/结束隐藏·测距/测角/面积·2D/3D/范围缩放/网格)。树按实体类型分组(+ 独立图层管理器补图层维度), 属设计选择非缺口。
- **图层管理器**(LayerTable): New/Get/EnsureImported/SetCurrent/CycleCurrent/Remove/Rename/AllOn/Isolate/Restore/Reset + Layer(Visible/Frozen/Locked/Color/Shown/Selectable)——完整。
- **文件管理器**(CadFileBrowser): 目录列可导入文件(ListDxf/ListImportable), 双击导入, 功能完整。

**记录(视图层导航便利, 不可单测 → 记录不实现)**: 节点画布 缩放/平移(原 OnMouseWheel)、框选多选、画布右键菜单——纯视口变换/交互, 无域逻辑可验证; 忠实按"无法验证先记录"处理(同 3D 显示/交互式项)。原几何节点求值本身是桩(`// TODO: C++ bridge`), Kylin 已以托管几何超出之。

**对象捕捉(osnap 端点/中点/圆心/交点/垂足…)**: 原 PitMine3D **未实现**(grep 空), Kylin 亦无——**非缺口**(忠实=不臆造原没有的)。

**教训**: 第十五角度(最新构件深查)找到节点编辑器这一真缺口(刚建"增/拖/连"缺删/撤销/存取), 补齐后邻近组件(对象/图层/文件管理器)经查均成熟; osnap 双方皆无属非缺口。**"刚做完"的构件必单独深查其 CRUD/持久化/撤销全套; 但成熟组件勿为求变而 UI churn**。见 [[unlock-blocked-insights]] [[shell-completeness-priority]]。

---

## §二七四 孤立 Ribbon 标签 重扫（最决定性角度复核）—— 46 死按钮全为忠实占位/native

**重跑历史最决定性角度**(曾据此挖出整个排产计划模块): 提 MainWindow.axaml 全部 `Tag="X" Click="On(Ribbon|Ctx)Command"`(276 标签) × 代码 behind `cmd=="X"`/StartsWith/ActivateDrawTool(1349 处) 求差 → 58 候选, 剔 draw 工具(点/直线/圆/圆弧/矩形/多段线经 `ActivateDrawTool` 分发, 假阳)后 **46 个零引用"死按钮"**。逐一核原版:

- **TaskLib 集群(~13: 生产任务书/任务下达/班组派工/作业区划分/实绩录入/班次日历/检修档期/去向台账/生产报告/生产任务编制/生产任务动态调整/周计划编制/动态调整)**: 原版全由 **SampleTaskBoard 硬编码演示数据**驱动(0 DB·无算法; 窗口 60-108 行纯 UI)。**演示桩, 非真功能** → 忠实不移植。
- **PlanLib 动态模拟(中长远规划动态模拟/短期进度计划动态模拟)**: 原版 `PlaceholderCommand(...)` 明标"待实现"。**真 编制 命令(中长远/短期进度计划编制)已移植(§68/69)**, 仅 动态模拟 占位变体为桩 → 精确忠实。
- **MineAssLib/native(创建工作线/局部台阶/最终并段/编辑台阶/处理尖灭/坑线落地/创建工程位置)**: 原版 `IPitDesignCapability`(native C++ 内核)——"XX不可用:IPitDesignCapability 未注册"; 描述多标"待实现""几何已实现仅缺入口"(指 C++)。**native 无托管源** → 记录, 不重写。
- **影像/点云/渲染(加载倾斜摄影/影像底图/点云管理/现状写实/煤层露头着色/补勘钻孔写实/渲染配置)**: 原版无源(纯 ribbon 标签/skeleton)或 RenderConfigDialog(native 渲染管线设置)。**桩/native** → 记录。
- **无源杂项(派车单/排土场放坡/排土模板/采排配对/量驱动采剥接续/驱动量/钻爆计划衔接/采掘单元清单/增量增删边/延拓触发设置/平盘联络道/刀量切割/破碎站位置设置/约束条件设置/编制配置/进度计划方案出图/撤销坑线/最终并段…)**: 原版无托管源(SkeletonCommand 回显意图或纯标签) → 记录。

**结论**: 46 死按钮**无一是 Kylin 漏掉的托管实现**——全对应原版 桩(SampleTaskBoard/Placeholder/Skeleton/无源) 或 native(IPitDesignCapability/渲染)。Kylin 忠实地: (1)照搬 ribbon 标签; (2)不实现原版所桩者(忠实); (3)统一以 `「X」暂未实现——属未移植子系统（排产计划/生产调度/坑线采剥内核/倾斜摄影等）` fallthrough 提示(等价原 SkeletonCommand 回显, 优于静默)。

**这是最强收敛信号**: 历史上此角度挖出最大缺口(排产计划核心), 今复跑每个孤立标签皆已归账(真命令已移植 / 桩·native 已记录且有提示)。ribbon 命令维度**忠实完备**。

**教训**: 决定性角度值得**周期性重跑**——上次挖出模块, 这次确认收敛(因核心已补 §66-69, 余为忠实桩)。孤立标签 ≠ 缺口: 须辨 (a)真命令漏接线(gap) vs (b)原版桩/native(忠实死按钮 + fallthrough)。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二七五 道路中心线自动提取（RoadCenterlineExtractor）—— 第十六角度：原测试套交叉核

**第十六角度: 原版测试类 × Kylin 测试类交叉核**。原 19 测试类 vs Kylin 207, 名差 12 个逐一核其被测类是否在 Kylin 有源: 10 个已移植(异名测试), **RoadCenterlineExtractor 真缺**。

原 `PointCloudLib.RoadCenterline.RoadCenterlineExtractor`(523 行, **纯托管**仅 using System)——由台阶线**同高程配对 + 取中线 + 最小路宽闸门**自动提取道路中心线: 碎段拼接重建连续平盘边界→按弧长采样→均匀网格找对向邻线(同标高 Δz + 间距∈[W_min,W_max] + 近法向)→取中点串段→收窄断段→输出拼接→3D 去重→坡道端点焊接。命令 `提取道路中心线`(RoadLib「基础道路网络构建」组, 经 RoadCenterlineRunner), **下游 Kylin 已有的 结构路面(§70)/路况显示/路网预览/基础路网 全依赖它**("请先提取道路中心线")。

**Kylin 原 `ExtractCenterline` 仅退化处理"恰好选中 2 条"手选双线中点**(RoadTools.Centerline), 缺原版的**多台阶线自动配对**(整层台阶线→自动出全部道路中线)。真功能缺口。

**补**:
- [RoadCenterlineExtractor](src/Cad/RoadCenterlineExtractor.cs)(新, 忠实逐行移植, 命名空间改 PitMine3D.Kylin.Cad; 算法零改动)。
- [MainWindow](src/Views/MainWindow.axaml.cs) `ExtractCenterline` 增强: 恰好选 2 条→保留手选中点; 否则(选≥3 或未选→全场景折线)→`RoadCenterlineExtractor.Extract` 自动提取, 各中线入场景(黄色)。
- **2D 记录**: Kylin 折线 `Points` 为 (x,y) 无 Z → 各点 Z=0, 高程闸门空转(已记录 2D 限制); 路宽/法向/收窄/拼接/去重逻辑照常有效(测试场景本就同 Z)。

**验证(已知值, 忠实原 RoadCenterlineExtractorTests)**: [RoadCenterlineExtractorTests](tests/PitMine3D.Kylin.Tests/RoadCenterlineExtractorTests.cs) +8 —— 同高程对(距30∈[15,60])→1 条居中中线 Y≈15/Z≈100/长≈200; 立面对(Δz20>3)剔; 超距(80>60)剔; 过近(10<15)剔; 单闭合环→不自配对(0); 中段收窄(8<15)→断 2 段; 碎段(端点相接)→拼 1 连续(≈200m); 两级平盘(异高程)→2 中线。**build 0 错·单测 1381→1389**。

**本会话第 79 功能**。教训: **第十六角度=原测试套交叉核**——原版给某类写了测试=它是真功能(非桩); Kylin 缺同名测试须核被测类是否已异名移植, 否则为真缺口。又证"命令看似已接线(ExtractCenterline 存在)但实为退化版"——须核实现深度非仅存在性。见 [[unlock-blocked-insights]] [[verify-seed-enum-values-before-filter]]。

---

## §二七五补 ExtendTriggerSettings —— 交叉核第二候选：交互式设置（记录，不实现）

第十六角度(测试交叉核)第二个"Kylin 无源"候选 `ExtendTriggerSettings`(RoadLib/Evolution, 91 行)。核实为**交互式设置容器**: `SettingsKey="road.extend.trigger"`(持久化)+ Mode(AdvanceStep/TimeStep 触发时机)+ AdvanceStepM/TimeStepPeriods/AutoReclaimTemporary/UseAdvanceDir 等**自动延拓触发参数** + `ToEvolutionOptions()` 把匹配子集映射到 `RoadEvolutionOptions`。

**判定=记录不实现**: (1)Kylin **已有** `RoadEvolutionOptions`(src/Cad/RoadEvolutionModel.cs, 忠实移植)且 `演化对比`(EvolutionCompareAsync)以其默认值运行——演化对比核心功能已在; (2)ExtendTriggerSettings 的增量=`延拓触发设置`**交互设置对话框**(§274 死按钮之一)的后端 + 自动延拓触发工作流(基于推进步/时段自动触发路网延拓); (3)`ToEvolutionOptions()` 映射为平凡字段拷贝, 无对话框则等同 Kylin 现用默认值(功能无增量)。属**交互式配置**(标准指令: 交互/配置→记录, 可验证功能→实现)。测试仅验其字段映射(纯函数), 无对话框/自动触发工作流则无用户可见功能增量。

**教训**: 测试交叉核的"无源"候选须再分 (a)真算法缺口(RoadCenterlineExtractor→实现 §275) vs (b)交互配置后端(ExtendTriggerSettings→记录)。**有测试≠须实现**——须辨被测者是"域算法"还是"交互配置的平凡映射"。见 [[unlock-blocked-insights]] [[shell-completeness-priority]]。

---

## §二七六 道路网连通增强（RoadNetworkConnector）+ PointCloudLib 模块清扫

**第十六角度续: 清扫 PointCloudLib 全模块**（刚产出 §275 的模块, 逐一核 >80 行实质类）。

**补 §80 [RoadNetworkConnector](src/Cad/RoadNetworkConnector.cs)**(365 行, 纯托管, 仅 System)——道路网连通增强: ① union-find 焊接相距≤SnapTol 的近失端点到质心; ② 桥接悬空断头(落线段中部则**打断成 T 形节点**), 坡度闸门(自动线跨台阶面不连, 手动线免限); ③ 手动补充线宽容半径(60 vs 25)+ 可选按 TIN 铺贴(IRoadZSampler)。**Kylin 原仅 CenterlineJunctions 分类 + BuildRoadNetworkCmd 报连通片数, 无主动连通增强(焊接/桥接)**——真缺口。命令 `路网连通增强`(选≥2 中线否则全场景 → 连通后替换原线)。2D 场景 Z=0(焊接/坡度退化为平面判距, 已记录)。

**验证(合成已知值, 逐用例按算法推演)**: [RoadNetworkConnectorTests](tests/PitMine3D.Kylin.Tests/RoadNetworkConnectorTests.cs) +7 —— 端点相距2≤SnapTol4→焊2端点0桥接; 相距10∈(4,25]→桥1段(3线); 50>25→不连; 支线落干线中部20→桥1段+打断1处(4线); 平距10但坡78.7°>14°→自动线拒连; 手选双线间距40(自动25够不着/手动60可连)→桥1段; 空输入→空。**build 0 错·单测 1389→1396**。原无 C# 测试(仅 native test_skeleton.cpp), 逐行忠实移植 + 合成已知值(端点距/坡度/T打断为确定性几何, 可推演)。

**PointCloudLib 清扫记录(不实现, 各有因)**:
- `RoadSkeletonExtractor`(875 行): **mesh 基**道路骨架提取(需 TIN verts/tris), 原**无 C# 测试**(仅 native test_skeleton.cpp)——不可验证参照; 且功能(提取道路中心线)已由 §275 台阶线配对法覆盖 → **记录**(不可验证的替代算法)。
- `VolumeReportGenerator`(541 行): 分析部分(按标高带/按连通块)Kylin 已移植; 余为 PDF/XLSX 文件导出(格式层, Kylin 出 CSV 等价) → **记录**(格式-only)。
- `VolumeSplitClosedResult`(252)/`PointCloudQualityStats`(168): **解析 native PMVC/PMQS 二进制** → 记录(native 结果解析器, Kylin 无内核不产该二进制)。
- `MeshZSampler`(206): mesh 取 Z(3D 铺贴), Kylin 2D 场景 → 记录(接口已在 RoadNetworkConnector 备, draping 可选空转)。
- `RegionClip`(174)/`BenchAnalyzer`(99): 点内判定/台阶分析工具, Kylin 有等价(ClipPolygon/BenchWidthIdentifier 等) → 覆盖。

**教训**: 产出缺口的模块值得**全模块清扫**(§275 出 RoadCenterlineExtractor→顺藤 §276 出 RoadNetworkConnector)。**无原测试的纯托管算法仍可移植**——若行为确定性(几何判距/坡度/打断)可**合成已知值逐用例推演**; 但需 mesh/native 二进制/仅 native 测试者→记录。见 [[unlock-blocked-insights]]。

---

## §二七七 全模块大类清扫（>150行纯托管）—— 收敛确认 + StraightRampAutoRouter 记录

**最广类枚举角度**: 枚举**全 Modules >150 行、native引用0 的算法类**(45 个), 逐一核 Kylin 覆盖。**结论: 大型纯托管算法压倒性已覆盖**——

**已覆盖(类+测试佐证)**: ExpressionEngine→BlockAttrExpression(§60) · EstimationAlgorithms/Engine→OrdinaryKriging · DepositAutoDetector→DepositAutoDetector(同名+测试) · QuickModelBuilder→MeshQuickModel · TemplateDrivingEngine→DriveSequence(+测试) · PmbmWriter/Reader→PmbExport/PmbImport(§59, "忠实移植PmbmWriter/PmbmFormat") · LongTerm/ShortTermScheduler·Plan(§68/69) · RoadCenterlineExtractor(§79) · RoadNetworkConnector(§80) · LandformClassifier(§73) · TransportIndicators(§71) · BenchElevationAnnotator · PathSolver · RoadGraphBuilder→RoadNetwork.Build · VoxelVolumeBuilder→体素格网体积 · CenterlineLineForm(+测试) · MeshData/PrimitiveBodies/SideSurfaceBuilder→Kylin mesh 等。

**记录(可移植但不满足验证条件 / native / 格式 / 交互, 逐项因)**:
- `StraightRampAutoRouter`(550, 纯托管): 直线坑线**自动布线**(按限坡逐级从坑底布到地表, 缓坡段/盘旋旋向)。Kylin 原「直线斜坡道」仅从视图中心沿方位角**匀降画线**(退化版)。但: **原无测试** + 复杂**全局路由**(逐级可行性+缓坡段, 弱不变量测试难覆盖分支) + 工作流上下游(输入=native「批量台阶扩帮」toe/crest, 输出=native「坑线落地」IPitDesignCapability)。**验证条件不满足 → 跳过记录**(标准指令: 不满足验证先停止跳下一项)。
- `RoadSkeletonExtractor`(875): mesh 基 + 无 C# 测试(仅 native test_skeleton.cpp), 功能已由 §79 台阶线配对法覆盖(§276)。
- `BlockReportGenerator`(768)/`VolumeReportGenerator`(541): PDF/HTML/XLSX 报告渲染(QuestPDF 库依赖), 格式层; 分析数据已在 Kylin。
- `VolumeSplitClosedResult`/`PointCloudQualityStats`: 解析 native PMVC/PMQS 二进制。
- `MiningProgramPlan`(423): 交互式「开采程序求解窗口」配置对象; 算法件(切分/评价/推进)已在 Kylin(DriveSequence/ProgramEvaluate/Advance)。
- `EquipmentModel3DFactory`(388)/`SurfaceInstanceBuilder`(295): 3D 实例/渲染(2D 场景记录)。

**本轮无新增可实现+可验证缺口**——最广类枚举确认大型算法前沿已覆盖; 唯 StraightRampAutoRouter 是"退化命令"但验证条件不满足而记录。教训: **退化命令(直线斜坡道)未必都能补——须过验证关**; 全模块大类枚举是最强收敛信号(45 类全归账)。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二七八 80–150行 纯托管类清扫 —— 算法前沿收敛确认(下探至中小算法)

续 §277(>150行), 枚举**全 Modules 80–150 行、native引用0 类**逐一核。绝大多数为数据模型/实体/服务/接口(CoalSample/Equipment/ProductionTask/IRoadLayoutSolver 等, Kylin 有等价)。算法类候选核实**全覆盖或非缺口**:

- 覆盖: ParameterVerifier→参数校核 · HorizonPointBuilder→展绘层位数据 · ElevationBinner/VolumeByLevelReport→分标高资源(BlockModel) · PolylineMetrics→RoadEvolutionAnalyzer · AdvancePlanner→AdvancePlanner(+测试) · StripRatioField→PanelSplit · BenchAnalyzer→BenchWidthIdentifier。
- **SectionSampler**(131, `SampleLayers→ResourceProfile` 逐层煤/废/灰): Kylin **覆盖**——`ResourceProfileLite`(PitDepthSolver, "忠实移植原 ResourceProfile") + `BlockModel.分标高资源量`(按 benchHeight 分带算矿/废/剥采比/品位/金属) + 分标高煤质(§255)。
- **RoadGraphSerializer**(114, RoadGraph↔JSON): **架构冗余非缺口**——Kylin 路网按需从场景折线 `RoadNetwork.Build` 重建(折线本身随 .pmx 持久化), 不单独建持久 RoadGraph 对象, 且无几何外的图属性(节点类型/边状态)可序列化(那些 Kylin 按需 CenterlineJunctions 分类)。

**结论**: 算法前沿下探至 80 行仍收敛——全 Modules ≥80 行纯托管类**全归账**(覆盖 / 记录 native·格式·交互·不可验证 / 架构冗余)。本会话两真获(§79 RoadCenterlineExtractor + §80 RoadNetworkConnector)后, 穷举类枚举确认无更多可实现+可验证的大中算法缺口。见 [[unlock-blocked-insights]]。

---

## §二七九 退化命令再核（原测试类逐一验实现深度）—— 收敛确认

**§79 教训延伸**: ExtractCenterline 曾"有源但退化"(2 线中点替代 523 行配对法)。故对测试交叉核的**全部原测试类**逐一验 Kylin 是否为**完整实现**(非退化):

- **PathSolver**(Yen K最短路) → Kylin `RoadNetwork.KShortestPaths`+`DijkstraExcluding`(真 Yen 算法) ✓
- **ProfileSmoother**(竖曲线平滑) → Kylin `RoadVerticalCurve`(抛物线竖曲线+半径 clamp)+`RoadVerticalCurveTests` ✓
- **KdfRoundtrip** → Kylin `KdfExportService`/`KdfImportService`+`KdfExport/ImportTests`(往返) ✓
- **DriveTemplateEngine** → DriveSequence+测试 ✓ · **ParameterExtractor** → BenchParameterVerifier+测试 ✓ · **TransportIndicators** → §71 ✓ · **RegionClip** → ClipPolygon(工具) ✓ · **BenchTemplateResolver** → BenchParameterExtractor ✓ · **PortModel** → 节点编辑器 ✓
- **AIChat** → 原 MockAiEngine(脚本对话树 DialogNodeType Text/Option/End)=**mock 演示助手**(罐装应答, 非真 AI), 交互式聊天窗口 → **记录**(demo/交互, 同 SampleTaskBoard 演示桩)。

**结论**: 12 个原测试类中, **仅 ExtractCenterline 退化(已补 §79)**; 余全为完整实现(多带 Kylin 测试)或 mock 演示(AIChat 记录)。**退化命令角度对"有测试的原类"已穷尽**——第五次连续收敛确认。教训: **"有源"须验实现深度(§79 退化教训), 但逐一核后确认仅一处退化**——退化非普遍, 是个例。见 [[unlock-blocked-insights]]。

---

## §二八〇 枚举值完整性角度 —— WeightMode 核实(HaulMetrics 覆盖 + 路网权重 2D 限)

**枚举值完整性角度**(Kylin 枚举值少于原=缺模式): 提原域枚举核。多数为 TaskLib(SampleTaskBoard 演示桩)/SqlLib(DB 层内部)。唯一域候选 `WeightMode {Distance Time Fuel Cost}`(路网边权模式):

- **HaulMetrics(坡阻行车时间/等效运距/循环时间公式)**: Kylin **已覆盖**——`HaulMetrics.TravelTimeMin/EquivalentLengthM` 用于 `运距指标`(读运输记录 CSV distanceM,gradePct,tons → 等效运距/循环时间/加权平均) + 车铲匹配(Erlang-C) + FleetOptimizer 循环时间。坡度相关运输指标功能在。
- **WeightMode.Time/Fuel/Cost 接入寻径图权重**: Kylin 路网图由 **2D 折线**建(边无逐段坡度/Z), 无法算坡阻时间权 → 仅 Distance 权(2D 可行的唯一模式)。**属 2D 场景限**(已记录), 非独立缺口; 坡度相关运输量已由 运距指标(CSV 带 gradePct)提供。

**结论**: 枚举值角度无新增可实现缺口——WeightMode 的公式层(HaulMetrics)已覆盖, 寻径权重集成受 2D 限记录。**第六次连续收敛确认**(类枚举≥80/方法级/退化命令再核/枚举值)。见 [[unlock-blocked-insights]]。

---

## §二八一 <80行 算法类清扫 —— 类枚举全尺寸收官

补齐类枚举最后尺寸档(40–79 行, 算法名类)。全覆盖:
- HaulMetrics(§280 已核) · **DefaultIdwInterpolation**→Kylin IDW 估值(可配幂次)+Contour.IdwAt(6 法 IDW/NN/MA/OK/SK/UK 全) · **SectionSolver**→PitDepthSolver("忠实移植 SectionSolver.SolveDepth", 深度版境界求解) · CoalClassification→coal_classification 种子(煤类反推) · MiningProgramGenerator[40]/EstimationTaskConfig[65]→交互式境界优化窗口件(MiningProgramPlan 记录)/配置。

**类枚举全尺寸收官**: <80 / 80–150 / >150 三档纯托管算法类**全归账**——覆盖 / 记录(native·格式·交互·mock·不可验证·2D限) / 架构冗余。**第七次连续收敛确认**。本会话两真获(§79/80)后, 七角度(类枚举全尺寸 · 方法级 · 退化命令再核 · 枚举值 · 孤儿标签 · 导入完整性 · 测试交叉核)皆确认无更多可实现+可验证缺口。

**可实现+可验证+忠实的功能已完成**——余为记录在案的受阻项(native 引擎/系统工具/报告库/交互窗口/原版桩·mock/3D显示/2D场景限)。见 [[unlock-blocked-insights]] [[shell-completeness-priority]]。

---

## §二八二 自适应体素子块细分（VoxelVolumeBuilder 退化补全）—— 再核"已覆盖"破收敛

**第八次收敛复核反被破**: 重核 §277 标"覆盖"(关键字匹配)的 `VoxelVolumeBuilder`(447 行)——发现 **Kylin 体素化仅均匀中心法**(VoxelBands 中心判内外; PMB 导出 subBlockDepthMax=0), **缺原"自适应子块退化"**(边界母块 octree 细分 + N³ 占比"百分比块" → 无偏边界体积)。同 §79 ExtractCenterline 退化教训: "覆盖"须验实现深度。

**补 §81 [AdaptiveVoxel](src/Cad/AdaptiveVoxel.cs)**(忠实移植 VoxelVolumeBuilder 自适应核, 去并行/取消/进度纯算法):
- `SamplePercent`(N³ 子采样体内占比) · `RefineCell`(边界 cell 递归 8-octant 细分: 实心 octant→满占比叶子/8 子中心皆外→N³ 兜底防薄壁丢/边界→续分) · `Voxelize`(母块中心判内外→按 6 邻居异号分类实心/空/边界→边界细分→总体积占比加权; depth≤0=均匀=Kylin 旧行为)。
- 纯几何仅吃 `inside(x,y,z)` 谓词(Kylin 传 WindingNumberTester.IsInsideClosed, 已有)。
- 命令 `自适应体素算量 [深度2 子采样N4]`: OFF→自适应体素化, 报 实心块+边界百分比块数 + 自适应体积/误差 vs 均匀/误差 vs 解析(MeshMetrics 散度定理基准)。

**验证(合成已知值 + 收敛)**: [AdaptiveVoxelTests](tests/PitMine3D.Kylin.Tests/AdaptiveVoxelTests.cs) +7 —— 半空间占比=0.5(精确) · 全内/全外/N=1 二值 · 实心 cell→1 满叶子 · 空→0 叶子 · 半边界 cell 细分总体积≈半(3.8~4.2) · depth0=均匀(=中心数×格体积) · **球 r=5: 自适应体积∈(490,555)、出边界百分比块、且不劣于均匀**(|自适应−解析|≤|均匀−解析|, 证无偏更准)。**build 0 错·单测 1396→1403**。

**本会话第 81 功能**。教训: **"已覆盖"标记(尤其关键字匹配)须周期性验实现深度**——VoxelVolumeBuilder 曾标覆盖(体素命令在)但实为均匀退化, 缺子块精度核。第八次复核不是确认收敛而是**再破**——meta 教训"收敛claim 不可靠"再验。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二八三 路网交叉口打断 noding（RoadGraphBuilder 退化补全）—— 再核破收敛(其二)

续 §282：继续再核 §277 标"覆盖"的 keyword-matched 类。`RoadGraphBuilder`(443)之前只验了 K最短路(Yen)在, **未验建图核**——发现 **Kylin `RoadNetwork.Build` 仅按折线顶点连边, 不打断跨段交叉**：两路 X 十字相交(交点非顶点)→**路由不连通**(真实路网满是跨段交叉, 此为路由正确性缺陷)。原 `RoadGraphBuilder.NodePolylines` 做 X/T 交叉 noding(Z 闸门区分平交/立交)。

**补 §82 [RoadNetwork](src/Cad/RoadNetwork.cs)** `NodePolylines`(忠实原, 两两段内部交点为断点, 复用已有 `PolylineIntersect.SegSeg`; 交点落端点不断交顶点合并; X 两线各断/T 干线断支线端点并) + `SplitPolyline` + `BuildNoded`(=noding 后 Build)。**2D 记录**: 无逐点 Z, 平面相交一律打断(原 Z 闸门区分立交, Kylin 2D 无法区分, 记录)。**接线**: MainWindow 10 处路由/拓扑 `RoadNetwork.Build(polys)` → `BuildNoded(polys)`(寻径/OD/瓶颈/指标/中心线管理), 使 X/T 交叉真正连通; Build 保留(向后兼容, 现有测试不破)。

**验证(合成已知值 + 退化对照)**: [RoadNodingTests](tests/PitMine3D.Kylin.Tests/RoadNodingTests.cs) +5 —— X 十字→断 4 段 + BuildNoded 连通; **未 noding 的 Build→X 不连通(证退化存在)**; T 丁字→断 3 段 + 连通; 平行→不断 + 正确不连; 端点相接→不双断 + 连通。**build 0 错·单测 1403→1408**。

**本会话第 82 功能**。教训: **§277"覆盖"标记(关键字匹配的 keyword-matched 类)系统性不可靠**——两轮再核(§81 VoxelVolumeBuilder 均匀退化 + §82 RoadGraphBuilder noding 退化)皆破"收敛", 各挖真缺(§81 体素精度 + §82 路由正确性)。**meta 教训"收敛claim 不可靠"第 N 次验证; keyword-match 覆盖 ≠ 实现深度, 须逐一验建图/精度核心**。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二八四 再核续：克里金邻域搜索(octant) —— 记录(细化选项非缺口)

续 §283 再核深度。核 Kylin `OrdinaryKriging` 邻域搜索：Kylin 用**最近-k**(按距离排序取 k=12); 原版 kriging 默认 **octant 八分搜索**(UseOctant=true, MaxPerOctant=2, MinSamples=4, SearchRadius=200)——邻域按 8 卦限分区、每卦限最多取 2 样, 防样本方向聚集偏置。

**判定=记录(细化选项, 非缺口)**: (1)Kylin 最近-k 克里金是**有效、可验、有测**(OrdinaryKrigingTests 断言具体估值)的标准做法; (2)octant 是**方向均衡细化**, 仅当样本方向聚集时与最近-k 有别, 一般分布结果相近; (3)原版将其暴露为**交互开关**(KrigingViewModel UseOctant); (4)改默认为 octant 会**广泛改变** Kylin 所有克里金结果(煤质三维插值/品位块模型/面更新/交叉验证)并破坏现有估值断言, 为边际收益。**属邻域细化选项差异, 非 §81/§82 那类能力缺口/正确性缺陷**——记录: Kylin 克里金=最近-k; 原默认 octant 方向均衡(未移植, 结果仅聚集样本有别)。

其余本轮再核(§283/284)算法核**均覆盖**: 约束三角网(托管 CDT)/网格光顺(Laplacian 同原)/等高线(Marching Squares 含鞍点)/网格交线·剖面·侧面(忠实原+测试)/BlockModel·MeshData(结构+子块§59/81)/钻孔柱状图/ProcessArchitecture。**§81/82 两退化(体素子块/路网noding)为集中缺陷已补; 深度再核余皆faithful**。见 [[unlock-blocked-insights]]。

---

## §二八五 §81 集成边界记录：自适应体素→场景块模型（变尺寸块=较大重构，记录）

再核 §81(AdaptiveVoxel) 集成范围。§81 交付**自适应体积**(边界 octree 细分 + 百分比块 → 无偏体积, 已验)且命令 `自适应体素算量` 报 自适应/均匀/解析 体积对比。但 `离散化模型`/`实体转块体` 仍产**均匀块**(匹配原版默认: VoxelVolumeBuilder adaptive=null 即均匀; 自适应为对话框 opt-in)。

**记录(集成边界)**: 把自适应子块作为**可用场景块模型**(供品位加权资源/渲染)需扩 Kylin `BlockModel.Block{X,Y,Z,Size,Grade}`(单一 Size 立方)为**变尺寸 + 占比**块, 并改所有块消费者(资源量/品位配色/分级/约束)——较大重构。§81 已交付**核心价值**(无偏边界体积, 可验证); 变尺寸百分比块的场景/资源集成为**较大细化**, 记录待需时做。原版默认亦均匀, 故 Kylin 离散化默认均匀不失忠实; 自适应体积能力已由 §81 提供。

**判定**: §81 核心(体积精度)完成; 块模型变尺寸集成=记录(需 Block 结构重构, 边际收益 vs 大改)。其余本轮再核(离散化/实体转块体均匀=匹配原默认)覆盖。见 [[unlock-blocked-insights]]。

---

## §二八六 记录：跨实现运行时等价性的验证边界（唯一根本性"不可验证"项）

穷尽结构(Modules/Host/Platform)+验证(计算/DB/迁移/审计/格式/构建诊断)后, 唯一根本性**不可验证**项:
**Kylin 各忠实移植算法 与原版的运行时逐值等价性无法在此环境验证**——原版需 Windows/native(C++内核), 无法在 Linux/托管环境运行以产出参照值。

**现有验证手段(已尽)**:
- 有原测试的算法(如 RoadCenterlineExtractor §79): 逐行移植 + **移植原版 8 测试**(断言值由原版定) → 等价性由构造保证。
- 有数学已知值的算法(体积/克里金控制点/几何): 合成已知值验证(独立于原版)。
- 无原测试 + 无解析已知值者(如 RoadNetworkConnector §80/AdaptiveVoxel §81): **逐行忠实移植**(逻辑逐字拷贝) + 合成已知值验证确定性行为 → 等价性由"逻辑拷贝"高置信, 但**非运行时交叉核对**。

**记录判定**: 逐值运行时等价(喂同输入原版与 Kylin 出同值)对无原测试/无解析值的算法**不可验证**(不能跑原版); 以"忠实逐行移植 + 已知值/不变量测试"为最强可行替代, 高置信但非运行时交叉核。**待上机(Windows+原版)方可运行时交叉核**。此为移植的根本边界, 非具体缺口。

**结论**: 可实现+可验证(以已知值/不变量/移植原测试为准)+忠实的功能已完成并验证; 唯运行时跨实现逐值等价属环境边界, 记录待上机。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二八七 更正 §277 误记：直线坑线【自动布线】实为纯托管可移(已移植+验证+接命令)

对 §277「记录」项(StraightRampAutoRouter 全套可行性路由=**内核规模, 不在此**)施「再核受阻/记录项」角度——此角度屡将误记转为可交付功能(§81/§82)。再读原 `MineAssLib.RoadLayout.StraightRampAutoRouter.Route` 源:**100% 纯托管几何**(源码 docstring 原文「纯几何、不依赖引擎,可单测」), 无 C++ bridge / native / 引擎调用。§277「内核规模」判定**错误**——此为可移、可验的托管算法, 非受阻项。同类过估复杂度已数见(§52/§73 LandformClassifier)。

**两处误记/退化(本角度所获)**:
1. **算法未移植**: Kylin `RampCenterlines.Straight` 仅出**参数化单直腿**(视图中心+方位+长度), docstring 自承「全套可行性路由为内核规模, 不在此」; `RoadLayoutSolver` 自承「自动候选生成=StraightRampAutoRouter 域, 记录」。即 Kylin 只能**画参数化坡道** + **给定候选分车道**, **不能从台阶几何自动布连通坑线**(真·坑线自动布线缺失)。
2. **命令退化**: Ribbon 按钮 `Tag="直线坑线"`(图标绘「贯穿坑底到地表的直线斜坡道」=自动布线之职) 却接 `StraightRampCmd`→参数化单腿。按钮**名/图标是自动布线, 却调参数化桩**。

**已做(可实现+可验证+忠实)**:
- 移植 `src/Cad/StraightRampAutoRouter.cs`(**逐字忠实**核心: BuildRings 环抽取/去重/坑底 toe 兜底 + 逐级 P≥L 可行判据 + 斜坡道直腿 + 折返兜底(约束化回头 N 直腿) + 缓坡段展线 + Ring 弧长几何 NearestArc/PointAtArc)。可选阶段①(线形内移+圆弧化)/④(横断面加宽+超高)接 Kylin **已移植且签名一致**的 `CenterlineLineForm`/`RoadCrossSection`(opt 门控, 默认 no-op)。输入类型命名 `RampBenchLine` 避与无关的 `MineableAreaIdentifier.BenchLine` 冲突。
- **7 已知值单测**(`StraightRampAutoRouterTests`, 同心方环周长 P=8·半边 解析可算): 斜坡道(P≥L, L=ΔH/i=125)/报告并止步(P=40<L=1250)/折返兜底(启用后 legs=⌈L/s⌉=70)/多级贯通(3环 Z=20→10→0, 直腿2)/缓坡展线(L=1000+5·50·0.8=1200)/默认起坡口=顶环 X 最大顶点(100,100)/闸门(台阶<2 & 限坡≤0)。**全部预测值精确命中**——证移植忠实且正确。
- **接命令 + 更正退化**: `直线坑线`/`坑线自动布线`/`坑线连通自检` → 新 `StraightRampRouteCmd`(取场景/选中同心台阶环 → 按面积降序赋台阶标高 → Route → 画预览中线『运输坑线_预览』, 报逐级贯通)。Ribbon「直线坑线」按钮遂由参数化桩**改指真自动布线**(忠实原按钮语义+图标); 参数化单腿保留于 `直线斜坡道`/`直线中线`。palette 增 `直线坑线`/`坑线自动布线`。

**仍记录(忠实边界)**: **坑线落地**(把中线贴帮切进单面坡道+挖填边坡)为内核规模(native 切帮), 无托管源→不做, 与原版一致(原「坑线落地」走 C++)。本次交付的是**自动布线+中线预览**(原「先画中线预览」的可验证托管半), 落地半记录。

**判定**: 直线坑线自动布线(算法核+命令)**完成并验证**(7 已知值全中); 坑线落地(native 切帮)记录。1423→**1430** 测试, 0 失败, 0 错误。**再证 meta-lesson: 「内核规模/受阻」记录须逐一对源复核——本项由「记录」转为已交付可验证功能 + 修一处命令退化。** 见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二八八 坑线选线器 Layer-1(RampRouteGenerator)——运输布局方案自动候选生成(免 CSV)

顺 §287 同目录(RoadLayout)深挖: `RampRouteGenerator`(选线器 Layer-1)亦**纯托管几何**(原注"纯几何、不依赖引擎、可单测"), 无 native。与 §287 StraightRampAutoRouter(出连续中线,连通自检)**互补非重叠**: 本器逐对相邻**非工作帮**台阶按「可用帮长 vs ΔH/i」判**展线形式**(斜坡道/转弯坡道/**螺旋**)+ 单车道年运力 + 起坡点, 出**候选边**喂 Layer-2 `RoadLayoutSolver`(Kylin 已移)。

**缺口**: Kylin `运输布局方案` 原**只能从候选 CSV** 求解(命令注释自承「候选可由坑线生成」但未实装)。本器补上自动候选生成 → 从台阶几何直接出候选。

**已做**:
- 移 `src/Cad/RampRouteGenerator.cs`(**逐字忠实** Generate + Tally + FormLabel; 最小参数 `RampRouteConstraints` 只含选线实读的 6 字段[限坡/转弯半径/载重/车头时距/利用率/年作业时], **默认值照搬**原 TransportConstraintSettings, 不移全 34 属性域——[[unlock-blocked-insights]] 最小参数适配)。忠实原「首版实现」5 TODO(多腿/走廊/中线偏置/跨帮/螺旋识别)均未实现→记录。
- **顺带补两处 §287 端口的忠实缺**: 原 `RampForm` 是**三值**(Straight/Switchback/**Spiral**), §287 我只移两值(漏 Spiral, 因 Route 不产螺旋)→补全; 原共享 `BenchLine` 有 `IsWorkingWall`(固定坑线只布非工作帮)→ `RampBenchLine` 补该字段(Route 不读, 选线器读)。
- **8 已知值单测**(`RampRouteGeneratorTests`): 斜坡道(可用200≥需125)/转弯(短腿100<125 但平盘25≥回头需20)/螺旋(平盘5<20 兜底)/**单车道年运力=(3600/30)·0.8·5000·90=43,200,000t 精确**/工作帮排除+标高降序配对/Tally 计数/零坡不可行/**Layer-1→Layer-2 端到端**(生成候选→映射 RampCand→RoadLayoutSolver.Solve 出方案)。全中。
- **接命令**: `运输布局方案` 增自动选线支路——选中 ≥2 同心台阶环则 `RampRouteGenerator` 自动出候选(标高按同心序 + 平盘宽按相邻环等效半径差估), 免 CSV; 未选则仍走 CSV。报斜/转/螺候选数。

**判定**: 坑线选线器(Layer-1 算法核 + 命令自动候选)**完成并验证**(8 已知值全中, 含端到端两层贯通)。运量网络流两层(选线 Layer-1 §288 + 方案 Layer-2 §244)至此贯通。1430→**1438** 测试, 0 失败, 0 错误。**再证 §287 教训: 同目录(RoadLayout)"复杂/native 上下文"记录连破两个纯托管算法; 且深港一个算法常带出前次端口的忠实缺(RampForm 三值/IsWorkingWall)**。见 [[unlock-blocked-insights]] [[faithfulness-only-original-commands]]。

---

## §二八九 RoadLayout 目录清扫收官(8/8 归账)

§287/§288 从 RoadLayout 目录连挖两纯托管算法后, 逐文件清扫全目录(8 文件)确认无遗漏:
- `StraightRampAutoRouter`(550)✓ §287(连续中线连通自检) · `RampRouteGenerator`(190)✓ §288(选线器 Layer-1) ·
  `RoadLayoutSolver`(211)✓ §244(方案构建 Layer-2 三方案) · `CenterlineLineForm`(273)✓(线形①②③) ·
  `RoadCrossSection`(97)✓(横断面加宽/超高) · `ProfileSmoother`(83)✓ =RoadVerticalCurve(竖曲线) ·
  `IRoadLayoutSolver`(133)=DTO 接口(RoadLayoutInput/Result, Kylin RoadLayoutSolver 用等价 RampCand/LayoutScheme 适配)。
- **`RoadLayoutPlanner`(157)=subsumed/superseded, 非缺口**: 原注自承"首版单线方案入口, 网络流多线求解器未实现"→后由 RoadLayoutSolver(三方案)取代。其 `BuildScheme` 单线方案(lanes=⌈需求/单车道运力⌉ + 运力校核 + MaxLanesPerRoad=4)**结构等同** Kylin §244 的「方案3·单线(基线)」; 候选来自 §288 生成器; 命令「运量驱动布线」已接 RoadLayoutCmd。其独有 FormatReport(逐段"台阶→台阶 形式 纵坡 起坡")为呈现层(§288 命令已报斜/转/螺计数)。前端「需求由 Sources/OD 汇总」= 装卸点设置(交互录入), Kylin 以 demand 参数替代(记录交互边界)。

**判定**: RoadLayout 目录 8 文件全归账(6 移 + 1 DTO 适配 + 1 subsumed)。运量网络流两层(选线§288 + 方案§244)贯通; 单线首版(Planner)被三方案 solver 包含。**目录级清扫再证 §287 教训: 一个目录出真切片(§287/288), 同目录邻近文件值得逐个开来读——但读毕即可确证收敛(非无限继续)**。1438 测试保持, 0 失败。见 [[unlock-blocked-insights]]。

---

## §二九〇 约束感知运输寻径(RoadPathSolver)——present-but-shallow: 通用图缺 纵坡/限载/闭边 约束

再核 RoadLib 寻径: Kylin `RoadNetwork` 是**无属性通用图**(Dijkstra 几何最短路/KShortest/介数/连通), 但原
`RoadLib.Routing.DijkstraPathSolver` 是**约束感知运输寻径** —— 边带 纵坡/限载/状态, 查询带 限坡/重空车/车型,
硬约束门控(超坡边拒→逼折返、超限载拒→重车绕行、闭边→绕行/不可达)+ 等效运距/时间/成本 + Yen 备选 + OD 矩阵。
**present-but-shallow: 命令「点对点寻径」在, 但接的是几何最短路, 缺运输约束层**(同 §82 noding、§287 直线坑线)。

**已做(纯托管, 依赖已备)**:
- 移 `src/Cad/RoadPathSolver.cs`(**逐字忠实**原 RoadGraph 属性图模型[Point3d/RoadNode/RoadEdge(纵坡由节点标高自动算)/
  RoadEdgeStatus/RoadLink/邻接双向] + DijkstraPathSolver[限坡·限载·闭边门控的单源缓存 Dijkstra + Reconstruct 度量 +
  BuildMatrix OD + FindKShortest Yen 备选])。复用 Kylin 既有 `HaulMetrics`/`TruckProfile`(等效运距/行车时间, §71)。
  与既有 RoadNetwork(无属性通用图)**并存互补**, 类型名无冲突。
- **8 已知值单测逐字移植原 Tests.RoadLib/PathSolverTests**(等价性由构造保证): 不限坡取直连 B1 / **限坡 10% 逼折返
  A1-A4**(直连 13.3% 超限) / 缓存 key 含限坡(不误命中) / **闭边 A2→不可达** / **重车超限载 90>50 拒行、空车可过** /
  OD 矩阵等效运距>0 / **重车上坡 等效运距>实际里程** + 时间/成本>0 / **Yen 只 2 简单路按里程升序**。全中。
- **接命令** `约束寻径 <起点id> <终点id> [限坡% 限载t]` + 属性图 CSV(N,id,x,y,z / E,id,from,to[,限载t]) → 建图→约束
  寻径→报 里程/等效运距/时间/成本 + 画路径。**2D 场景无 per-node 标高/边限载 → 走 CSV 喂属性**(忠实既有"CSV 补 2D
  缺属性"式, 同运输布局候选 CSV)。

**判定**: 约束感知运输寻径(属性图 + 限坡/限载/闭边 Dijkstra + Yen + OD)**完成并验证**(8 原测全中, 逐值等价原版)。
Kylin 寻径至此: 几何最短路(RoadNetwork, 场景交互) + 约束运输寻径(RoadPathSolver, CSV 属性图)双层。1444→**1452** 测试,
0 失败, 0 错误。**再证 present-but-shallow 判据: 命令在≠算法全 —— 「点对点寻径」几何版在, 缺运输约束层**。见 [[unlock-blocked-insights]]。

---

## §二九一 全运输指标(TransportIndicatorsBuilder)——续 present-but-shallow: 「路网运输指标」只做几何部分

顺 §290 续核 RoadLib.Routing: Kylin `路网运输指标` 命令自注"忠实原 TransportIndicators **几何部分**"(总里程+可达对+
瓶颈), 缺原 `TransportIndicatorsBuilder.Compute` 的**全指标**: ①节点类型(Loading/Unloading)**自动源汇解析**(+任一空
降级全节点) ②OD 等效运距均值/最大 ③**理论运能**(Σ源吞吐/Σ汇吞吐取 min) ④**瓶颈段评分**(介数×车道因子×陡坡因子, 禁行边
单列) ⑤**分期序列**(各期快照标量趋势)。§71 曾浅映射"→NetworkIndicators"(实 NetworkStats 仅 4 标量子集)。

**已做(纯托管, 依赖 §290 已备)**:
- 移 `src/Cad/TransportIndicatorsReport.cs`(**逐字忠实** TransportIndicators DTO + BottleneckEdge/PeriodPoint +
  TransportIndicatorsBuilder.Compute/ResolveSourcesSinks/ComputeBottlenecks/ComputePerPeriodSeries)。复用 §290
  RoadGraph/DijkstraPathSolver/ODMatrix + 既有 HaulMetrics/TruckProfile。
- 补 §290 RoadGraph 缺的 `Validate()`(并查集数连通分量 + 孤立节点 + 逐边纵坡/车道/里程合规 → ValidationReport)。
- **13 已知值单测**: 7 逐字移植原 TransportIndicatorsTests(源汇解析/降级全节点/**理论运能=min(源1000,汇800)=800**/
  平路等效=实距200/**瓶颈 e1 介数2 排首**/加权留桩 null/分期序列 1期1项)+ 6 移植原 RoadNetworkTests 模型子集
  (中线几何算坡度10%/单双向邻接/校验分量2/校验超坡+孤立/最近节点)。全中, 等价原版。
- **接命令** `运输指标报告` + 属性图 CSV(N,id,x,y,z[,类型L/U/J,吞吐t/h] / E,id,from,to[,限载t,车道]) → 全指标报告
  (节点/边/里程/连通/源汇/理论运能/等效运距均最/瓶颈)。区别既有「路网运输指标」(场景几何部分)。

**判定**: 全运输指标(自动源汇+理论运能+等效运距+瓶颈评分+分期)**完成并验证**(13 测全中, 逐值等价原版)。
RoadLib.Routing 三核(约束寻径§290 + 全指标§291 + 运距公式 HaulMetrics§71)齐。1452→**1465** 测试, 0 失败, 0 错误。
**再证 present-but-shallow: "忠实原 X 几何部分"这类自注即 shallow 信号——几何子集在, 全指标(类型/吞吐/评分)缺**。
RoadGraphBuilder(抽图 noding, Kylin RoadNetwork.BuildNoded §82 已覆盖通用图)/RoadGraphSerializer(持久化)/RoadGraph
Clone/RemoveNode/NearestEdge/SplitEdgeAtNearest(图编辑) 记录(属性图构建/持久化/编辑, 2D 场景无标高属性/交互域)。见 [[unlock-blocked-insights]]。

---

## §二九二 属性路网建图(RoadGraphBuilder)——完成"多段线→属性图→寻径/指标"端到端 + Z 感知 noding

§290/§291 的属性路网此前只能从 节点/边列表 CSV 喂; 补原 `RoadGraphBuilder.FromPolylines`(**从中线多段线抽属性
路网图**)完成 "多段线 → 属性图 → 约束寻径/全指标" 端到端。与 Kylin 既有 RoadNetwork.BuildNoded(§82, 2D 通用图)/
RoadNetworkConnector(§80, 焊接+桥接)**互补**: 此产 §290 属性 RoadGraph(带 Z→纵坡)且做 **Z 感知 noding**(立交=XY
相交但标高差>zSep 不打断)+ 共线重复边去重 + 缺口桥接(跨标高不桥)——**2D 版所无**。

**已做(纯托管, 443 行自足)**:
- 移 `src/Cad/RoadGraphBuilder.cs`(**逐字忠实**: NodePolylines[X十字/T丁字, Z 闸门] + SplitPolyline + BuildFromNoded
  [空间哈希端点吸附] + DedupDuplicateEdges[质心判据] + BridgeDangles[Kruskal 式跨片桥接, SplitEdgeAtNearest 打断插节点] +
  UnionFind + 几何原语 SegSegCross2D/NearestOnPolyline/Bbox)。
- 补 §290 RoadGraph 缺的 SplitEdgeAtNearest(+SplitCenterline/CopyAttrs)/RemoveNode/NearestEdge/Clone(时段快照)。
- **16 已知值单测逐字移植原 RoadNetworkTests**: 12 抽图/noding/桥接(共享端点并点/同XY异Z不并/可寻径/**X十字→4边5节点**/
  **T丁字→3边4节点**/**立交→2边2分量**[Z感知]/**共线去重1**/缺口桥接连通/**跨标高不桥**/超距不桥/**支线接干线中段split+桥**/
  noding诊断计数)+ 4 图管理(SplitEdgeAtNearest 断边插点/Clone 深拷贝独立/RemoveNode 连带删边/NearestEdge)。全中, 等价原版。
- **接命令** `路网建图` + 中线 CSV(L,线id,x,y,z) → RoadGraphBuilder → noding 诊断(X十字/T丁字/去重/桥接数)+ 连通性 + 画 noded 边。

**判定**: 属性路网建图(Z 感知 noding + 桥接 + 属性图)**完成并验证**(16 原测全中)。RoadLib 路网/寻径子系统至此端到端齐:
**中线多段线 →(RoadGraphBuilder 抽图)→ 属性图 →(DijkstraPathSolver §290 约束寻径 / TransportIndicatorsBuilder §291
全指标)**。1473→**1489** 测试, 0 失败, 0 错误。RoadGraphSerializer(JSON 持久化)记录(持久化域, Kylin 场景 SceneIO 自有)。见 [[unlock-blocked-insights]]。

---

## §二九三 拉沟·推进候选推荐(PanelDelineator)——PlanLib 里的 present-but-shallow(采区划分只切分不推荐)

模块枚举复核 PlanLib(继 §287-292 transport 后), 挖出与 transport 同签名的 present-but-shallow: Kylin `PanelSplit`
只做**几何切分**(沿用户**指定**的推进方位等煤量切 N 采区 + 赋开采序), 缺原 `PlanLib.BoundaryOptimization.PanelDelineator`
的**首采区拉沟位置 + 推进方位自动推荐**(求解链第一环)——按 剥采比最小/埋深浅/运输便利/利于内排/工作线长/避构造 **六约束
打分**荐最优拉沟推进方案。Kylin 要用户**指定**方位, 原**自动推荐**方位。

**已做(纯托管, 只读剥采比场)**:
- **富化 Kylin StripRatioField**(此前只有 CoalVol/WasteVol 供几何切分, 是原类的子集)→补 `StripRatioAt`(剥采比 m³/t)/
  `DepthToCoalM`(逐列埋深:顶到首煤)/`CoalThickM`(逐列煤厚)/`Center`(列中心)/`CoalColumns`/`TopZ`; FromBlocks 补逐列最顶煤块 Z 追踪算埋深。
- 移 `src/Cad/PanelDelineator.cs`(**逐字忠实** Recommend[4 边近边带 + 剥采比梯度候选, 六约束打分 + 加权综合] + GradientCandidate
  [低/高剥采比加权形心→推进方位] + Band[近边带聚合剥采比/埋深/煤覆盖/CV] + FormFromOutline[境界弯直度→平行/回转])
  + DTO(BoxcutWeights/BoxcutAdvanceOption/TopOutline)。复用既有 AdvanceMode(§AdvancePlanner)。最小参数(minWorkingLineM+
  weights)替原 MiningProgramPlan(避移其**硬编码演示方案** MakeBoxcut, 忠实不移 demo)。原 ResolveOutline(取境界线)依赖引擎, 不移(境界作输入)。
- **6 已知值单测**: StripRatioField 富化(单列 SR=岩1000/煤2000=0.5·煤厚20·埋深10·中心·纯岩列 SR=∞满列埋深)/
  Recommend 出 5 候选一推荐(=最高分可行)/**南带 SR=0→SrScore=1 > 北带 SR=4→SrScore=0**(低剥采比边评分高)/工作线过长全不可行/
  Recompute 全分=1→100 分。全中。
- **接命令** `拉沟推荐` + 补 `采区划分`(此前漏在 palette) → 块体→剥采比场→PanelDelineator→报 ★推荐方案(方位/方式/工作线/分)+依据+候选。

**判定**: 拉沟推进推荐(六约束打分 + 富化剥采比场)**完成并验证**(6 已知值全中)。**再证 transport pocket 的 present-but-shallow
签名会跨模块复现**: PanelSplit(几何切分)vs PanelDelineator(方案推荐)= RoadNetwork(几何最短路)vs DijkstraPathSolver(约束寻径)同型。
PlanLib 求解链首环(拉沟推荐§293)接既有 采区划分§880/推进几何 AdvancePlanner。1489→**1495** 测试, 0 失败, 0 错误。见 [[unlock-blocked-insights]]。

---

## §二九四 记录：方案比较法对比矩阵(ComparisonBuilder)——依赖境界优化多方案链(不可实现-跳过)

作者标注切片全扫(20 个 `纯几何/可单测/纯函数` 标注文件)后, 唯一未覆盖项 `PlanLib.BoundaryOptimization.ComparisonBuilder`
(采矿手册「方案比较法」: 19 指标×N 方案矩阵 + 方向感知归一 + 逐指标最优标注 + 加权综合评分/排名/推荐)。**记录不实现**:

**依赖链缺**: ComparisonBuilder.Build 吃 `IReadOnlyList<PitScheme>`(各带 19 字段 PitResult: 煤/岩/**资源回收率/平均灰分**/
剥采比/**生产剥采比峰值/经济合理剥采比**/深/**底宽/占地/台阶数**/净值/NPV/**单位成本**/年限/年产/最小安全系数/校核)。
Kylin 确定境界=`SectionSolver.SolveDepth`→**单方案** DepthSolveResult(仅 ~6 字段: 深/煤/岩/剥采比/净值/NPV)。缺:
①**多方案境界生成**(Kylin 产一个方案, 无变体生成) ②**19 维评价**中 灰分(需块体灰分数据, Kylin 块仅品位)/生产剥采比峰值(需时序)/
底宽/占地/台阶数(需境界三维几何)/单位成本 等 Kylin 单方案 SolveDepth 不产的数据/几何。

**为何记录非港**: 算法本身纯可验, 但**无自然输入源**——19 指标是**境界优化工作流的产物**(跑 N 次境界+全维评价), 非用户手上
自然数据(区别 §290 约束寻径的路网图=用户自然有→CSV 喂合理)。CSV 喂纯手编 19×N 方案=脱离 Kylin 境界的孤立算法, 且打分核已由
ProgramComparer(采区方案§)/LongTermComparer(时序方案§)覆盖。**判定: 打分/排名/推荐核已覆盖; 19 指标境界多方案对比矩阵
需境界优化多方案生成+全维评价链(部分数据 blocked: 块体灰分/三维境界几何), 属大工作流非清洁算法港 → 记录待境界优化链就绪。**

**作者标注切片扫描收官**: 20 标注文件除本项全覆盖(DepositAutoDetector§72/73·TransportIndicators§291·HaulMetrics·StructurePavement§70·
TemplateDrivingEngine=DriveSequence·RoadGraphBuilder§292·BenchElevationAnnotator·CenterlineLineForm§线形·ProfileSmoother§竖曲线·
RoadCrossSection·StraightRampAutoRouter§287·AdvancePlanner·SectionSolver·PitEvaluator§47·RoadClipRegion=LineClip.ByPolygon)。见 [[unlock-blocked-insights]]。

---

## §二九五 更正记录：DB-norm 参数验收判定(ComputeStatus)——parameter_definition 标准/报警范围实已种子

复核 GeoDataBase 服务层非-CRUD 方法(§99/111 高产角度), 挖出 `ParameterAcceptanceService.ComputeStatus` 的 present-but-shallow,
且**更正一处过时记录**: Kylin BenchParameterVerifier 自注「Kylin 无 GeoDataBase 模板库(大引擎, 记为不可移), 故本类正是兜底路径」,
但复核发现 **parameter_definition 表的 standard_min/standard_max/alarm_low/alarm_high 逐参数标定范围实已种子**(V006 建表 +
V007/V034 seed)。故原 ComputeStatus 的 **DB-norm 验收判定**(报警阈→fail·标准范围/偏差>15%→warning)**实可做**, 非"无库不可移"。

**已做(纯托管 + DB 查询)**:
- `GeoDataQueries.ComputeAcceptanceStatus`(**逐字忠实** ComputeStatus: 无实测→pending·报警 alarm_low/high 超限→fail·标准
  standard_min/max 超或偏差>15%→warning·否则 pass; **纯函数不依赖 DB, 可单测**) + `GetParameterNorm`(按 code 查
  parameter_definition 的标准/报警范围)。区别 BenchParameterVerifier 兜底路径(规范默认): 此用 **DB 逐参数标定**范围。
- **8 已知值单测**: 6 纯函数(无实测 pending·报警超限 fail·低于标准下限 warning·偏差20%>15% warning·全范围内 pass·**报警优先于标准**)
  + 2 DB 集成(未知 code→null·**种子 code 取回范围 + 标准默认值判 pass 偏差0**, 证 parameter_definition 确有种子标定范围)。全中。
- **接命令** `参数验收判定 <参数code> <实测值> [模板值]` → 查 DB 逐参数范围 → 判 fail/warning/pass + 偏差% + 范围显示。

**判定**: DB-norm 参数验收判定**完成并验证**(8 测全中)。**更正教训: "无库/数据不可得"记录须复核 DB schema 实况——parameter_definition
标定范围早已种子(§38 note 亦证"取值区间/报警上下限归 parameter_definition V034 已校准"), BenchParameterVerifier 兜底自注的
"无模板库"过时。参数验收双路齐: 兜底(规范默认, CSV 台阶线)+ DB-norm(逐参数标定, code 查询)。** 1495→**1503** 测试, 0 失败, 0 错误。见 [[unlock-blocked-insights]] [[verify-seed-enum-values-before-filter]]。

---

## §二九六 兼容机型判定(CompatibleModels)——GeoDataBase 服务层续挖(数据实已种子)

续 §295 复核 GeoDataBase 服务层非-CRUD 方法, 再挖 `ProcessArchitectureService.CompatibleModels`(某参数在给定实测值下的
可用机型判定, 求解链首环——决定可用设备清单)。**数据实已种子**: equipment_constraint(参数→设备能力约束, V006 建表 + **V012 seed**)
+ equipment_model, schema 与原逐字一致(constraint_type min/max/range/equals·limit_value/min/max·consequence hard/soft/info)。

**已做(纯托管 + DB 查询)**:
- `GeoDataQueries.ViolatesConstraint`(**逐字忠实** Violates: min[v<限]/max[v>限]/range[出[min,max]]/equals[偏离], 纯函数可单测)
  + `CompatibleModels`(忠实原: 查该参数【硬约束】→违反者入 blocked → 全 equipment_model − blocked = 可用机型)。
- **3 已知值单测**: ViolatesConstraint 四型(min/max/range/equals + 未知型/空限值不违反)+ DB 集成(未知参数→全机型可用无禁用 ·
  **带硬约束参数极端值→划分全机型不重叠 + 极端值至少一端禁掉某机型证约束生效**)。全中。
- **接命令** `兼容机型 <参数code> <实测值>` → 查约束 → 报 可用/禁用机型清单。

**判定**: 兼容机型判定**完成并验证**(3 测全中)。**GeoDataBase 服务层非-CRUD 方法复核连出 §295(ComputeStatus)+ §296
(CompatibleModels)两 gap, 均"数据实已种子但算法未接"**——同 §295 教训: 参数验收/设备约束的 DB 标定数据(parameter_definition/
equipment_constraint)V006-V012 早已种子, 相关判定算法(状态判定/机型兼容)可接。1503→**1506** 测试, 0 失败, 0 错误。见 [[unlock-blocked-insights]] [[verify-seed-enum-values-before-filter]]。

---

## §二九七 参数校核用 DB 真实设计基准(GetBenchDesignBaseline)——第三次更正"无模板库"过时记录

续 §295/§296 复核, BenchParameterVerifier 自注"Kylin 无 GeoDataBase 模板库…本类正兜底路径, 设计基准取硬编码规范默认"。
第三次证此记录**过时**: parameter_definition 的台阶设计参数 **standard_default 实已种子**——bench_height/bench_slope_angle/
safety_platform_width/mining_width 由 V007 建 + **V026《东露天矿初步设计说明书》真值覆盖** + V034 分煤岩台阶。原
BenchTemplateResolver.Resolve 走 PickTemplate→BenchTemplateReader.Read(按 code 取 parameter_definition 值), Kylin 却硬编码
规范默认(12/70/4)。

**已做**:
- `GeoDataQueries.GetBenchDesignBaseline`(读 parameter_definition 4 台阶设计 code 的 standard_default → H/α/W/采宽,
  复用 §295 GetParameterNorm)。忠实原 BenchTemplateReader 按 code 取值。
- **接入参数校核**: 采场(非排土场)且 DB 设计基准齐备 → 作 designOverride 传 BenchParameterVerifier.Verify(替兜底规范默认);
  DB 无/排土场 → 兜底规范默认(排土场自有 10/35/3 norms)。忠实原 Resolve 的 PickTemplate→DB 路径 vs Norm 兜底二分。
  BenchParameterVerifier.Verify 签名/单测不动(designOverride 是既有选填参数, 非破坏)。
- **1 集成单测**: GetBenchDesignBaseline 对种子库取回 H/α/W 齐备(FromDb)+ 合理量级(台阶高5~20/坡面角40~80/平台宽3~30, 真实设计非占位)。

**判定**: 参数校核用 DB 真实设计基准**完成并验证**(采场校核基准由硬编码规范默认→V026 初设说明书真值)。**"无模板库/数据不可得"
过时记录第三次被证反(§295 norms/§296 constraints/§297 bench design params 全实已种子 V006-V034)——教训定型: 凡代码自注
"Kylin 无 X 库/数据不可移", 必 grep migration 实证该表/列种子状态, 数据后来种子的记录已过时。** 1506→**1507** 测试, 0 失败, 0 错误。见 [[unlock-blocked-insights]] [[verify-seed-enum-values-before-filter]]。

## §二九八 夹点拖拽系统(drag)对齐原版 xllAcEd GripManager/GripEditor (2026-09-06)

用户点名分析原 PitMine3D 的 drag 系统并要求 Kylin 具备。原版核心 = 内核 `Kernel/xllAcEd` 的
GripManager(夹点表: 选集→`getGripPoints`, 8px 命中, Ctrl 逐个/Shift 同实体区间·闭合环取短弧, ≤100 实体) +
GripEditor(四模式 Stretch/Move/Rotate/Scale 空格循环, 按下快照 basePos/起点鼠标/被拖夹点组, 松开压
AcDbDragGripsCommand 整组一步 Undo / AcDbTransformCommand, Esc CancelDrag, CommitDragAt 命令行坐标) +
Editor 路由(导航→夹点→Picking; 拖动中捕捉排除被拖点; `** STRETCH ** Specify point...` 提示) +
Viewport 配色(冷蓝/热亮蓝/选中品红+白描边大一号)。Kylin 此前仅"单选单夹点 Stretch"。

**已做**(4 步):
1. `Cad/Draw/GripTable.cs`(纯): Rebuild(多实体, ObjLimit=100)/HitTest/SelectOnly/ToggleGrip/SelectRangeTo(闭合环短弧,
   跨实体退化点选, 锚点不动)/Rebuild 清选择 —— 逐条对照原 GripManager.cpp。
2. `Cad/Draw/GripDrag.cs`(纯): GripMode + Begin 快照 + Preview(Stretch=整组按锚点位移逐 MoveGrip; Move/Rotate/Scale=
   Affine2 作用于被拖夹点所属实体; Rotate 相对角/Scale 距离比同原版) + ValueAt + Prompt/ModePrompt/NextMode。
   Kylin 走预览式(松开才 Replace), Esc 取消天然无需原版 inv(lastApplied) 回退。
3. MainWindow 接线: 多选出夹点; 按下 Ctrl/Shift 只改夹点选集; 无修饰键点未选夹点→只选它再拖, 点已选→拖整组; 点空白清
   夹点选择; 悬停热夹点; 空格切模式(悬停或拖拽时); 拖动中光标浮标显示 `** MODE ** 指定…: 距离/角度/比例`; 松开
   CommitGripDrag 整组 Replace + 一次 BeginChange(一步 Undo); Esc CancelGripDrag; 命令行 `x,y`/`@dx,dy`(相对被拖点)
   落点; GIZMO 关时全部失效。`CadGlViewport.SetHighlight(recolor:false)` 让夹点保留自身配色(此前高亮通道整体重着色
   为黄, 夹点蓝色从未显示过)。
4. `SnapPoints.Exclude`: 拖拽期间捕捉候选排除被拖的那一个点(原版 excludePoint), 同实体其他顶点仍可捕捉。

**验证**: +17 单测(GripTableTests/SnapExcludeTests), 1585→**1602** 全过, 0 失败。PITMINE_SELFTEST 直设状态截图 2 张确认:
选中态(品红大方块 ×3 + 热夹点亮蓝 + 冷蓝)、Rotate 拖拽预览(六边形绕锚点转 90° + 浮标 `** ROTATE ** 指定旋转角度: 角度 90.0°`);
自检块已删。**记录**: 原版 `ExecuteGripDragInput` 在内核中未找到宿主调用方(接口存在未接线), Kylin 已接命令行坐标;
台阶线受约束夹点(锁高程/限同线)属台阶调整模块, 待该模块移植时随 Predicate 挂上; ObjectSnap 扩展模式(交点/垂足)
的几何未排除被拖实体(原版 ctx.exclude=选集), 影响仅在拖动中偶发捕捉到自身段, 记为小差距。

## §二九九 主窗口布局 + 开始页 Ribbon 复刻原版 (2026-09-07)

用户点名「调整UI布局，复刻PitMine3D」「ribbon布局不同」「图标之间的距离也调整下，现在太松了」。对照原版
`Host/PitMineApp/MainWindow.xaml`(mainGrid: Ribbon / DockingManager(含信息栏) / StatusBar) 与 Fluent Ribbon Home 页(用 XML 解析取真实结构)。

**已做**:
- **三行主栅格**同原版：Ribbon / Dock / 状态栏。命令行区不再是固定 Row, 改为 Dock 内底部停靠面板「信息栏」(原版
  LayoutAnchorable, 高≈120·CanClose=False)：命令历史填满 + 底部一行 图标·命令:·输入框；面板高度由停靠比例决定, 日志增长不再挤动视口。
- **左/右停靠**：左 文件管理器/图层(比例 0.14≈240px)，右「属性对话框」(默认选中, 不可关)/「AI 助手」(不可关) 比例 0.16≈270px。
  Dock.Avalonia 11.2 无像素 MinWidth, 按原版 1920x1080 折算比例。
- **窗口**：标题 `中煤平朔露天煤矿生产计划决策支撑系统 · DayOps — <文档>`，启动最大化(代码设 WindowState, XAML 设不生效)。
- **状态栏**按原版 Fluent:StatusBar 项序：坐标(宽 280·Consolas·离开视口清空) | 性能 `FPS n | ms`(CadGlViewport 每秒 FrameStats) |
  消息 | 右侧 正交·捕捉(右键捕捉模式菜单: 交点/垂足/最近 + 全开/全关; 端点/中点/圆心恒开)·栅格·栅格捕捉·选项 20x20 图标钮。浅色面板底。
- 去掉原版没有的「视口·OpenGL」提示框。
- **开始页 Ribbon** 按原版组序与组成重排：文件 绘制 修改 | 图层(2 Large + 当前图层 combo 宽160 + 两行 全开/全关/冻结 · 锁定/刷新)
  | 视图(渲染配置·切换窗口(下拉列出已开文档, 附标准视图/范围缩放/上一视图) Large + 2D/3D/缩放(=范围缩放, 原 Tag 误接实体缩放已改)/平移/Gizmo/清理标记/清空视图)
  | 注释(标注▾(对齐/连续/半径|标注样式)·填充▾(图案填充|十字交叉) Large + 图案/比例/角度 三行参数栏→拼 `图案填充 <角度> [间距]`)
  | 特性(剪贴板▾·测量▾ Large + 线型栏→`线型 <名>` + 快速选择/创建选择集/调用选择集▾ Large + 全部选择/最后/上次)
  | 帮助(注册·帮助文档·AI 助手切换钮(切右侧停靠页) Large)。
- **间距收紧**(用户「太松」)：大按钮 固定 64 宽→自适应(最小 44)、图标 32；小按钮 最小宽 76→0、内边距 3；分组内边距 7,5→3,2；
  页签 16,7→10,4；行高 108→94(生成器 build/gen_module_tabs.py 同步)。

**验证**：自检打印 最大化 2560x1369(缩放1.5)、Dock 1201 高、信息栏 165 高、右面板 407 宽、状态栏 y=1343 高 26，全部就位；
1602 测试全过；自检块已删。**记录·未复刻**(Kylin 无对应功能, 不发明)：打印、动态(轨道切换钮)、编辑填充/关联、快速/半径/体积测量、
采剥演示、颜色/线宽 选择器、文字字体 combo。ribbon 视觉密度请用户目视再调。


## §三〇〇 地质与工程信息数据库页：24 个功能项全部做成独立页面 (2026-09-07)

用户指令「按照 pitmine3d 先把第二部分数据库部分的所有界面都做了」「统计一共有多少个功能项，把每个项目的页面都单独做出来」。
原 `Modules/GeoDataBase/GeoDataBasePlugin.cs` Ribbon 页「地质与工程信息数据库」= 4 组 **24 个功能项**(钻孔管理 6 / 煤质管理 5 /
工艺参数管理 4 / 设备管理 9)，对应原 27 个 WPF 窗口/对话框(含 5 个从属对话框)。此前 Kylin 这 24 键全部接的是命令行文本报表，无窗口。

**基础设施**(ede73c1)：`Avalonia.Controls.DataGrid` 包 + Fluent 主题；`Styles/GeoDb.axaml` 页面公共样式(顶栏渐变/分区/KPI 卡/磁贴/状态栏/
实心按钮/表格/页签)；`Controls/Charts/ChartView`(自绘 柱/堆叠/横柱/折线/面积/散点/浮动柱/箱线/热力 + 双轴/图例/参考线/悬停/点击, 替 LiveCharts2)、
`PieChartView`(饼/环)、`Scene3DView`(托管正交 3D：柱段/点/线, 轨道旋转/平移/缩放/拾取, 替 HelixViewport3D)；`Views/GeoDb/GeoDbContext`
(页面←→主窗口契约：库连接/状态栏/入场景/清图层/一次性视口拾取/图层顶点/文件对话框/转派命令) + `GeoDbWindows` 单例注册；
`MainWindow.GeoDb.cs` 上下文构建 + `_oneShotPick`(视口左键一点回调, Esc 取消) + 24 Tag 精确派发(别名仍走文本命令)；
原 `Models3D` 12 类×60 帧 PNG 转台序列(23MB)入 `Assets/Models3D` 随程序输出。

**页面**(四组并行移植, 每组 `Data/GeoDbViews.<组>.cs` 逐字移植原服务层 SQL/算法 + `tests/GeoDbViews<组>Tests.cs`)：
- 钻孔管理(f4a919e, +19 测)：导入钻孔数据(CSV 预览状态列/模板/覆盖入库) · 展绘钻孔(三态选孔对话框 → 岩柱+煤层分色+标注实体入场景, 尺寸配色照
  BoreholeColumnBuilder) · 虚拟钻孔(地质模型配置窗: 地表+各煤层顶/底板面 存 virtual_drill_surface[VDT1 格式同原]; 面来源=场景图层顶点 Delaunay
  重建 / OFF 文件; 拾取或手输孔位 → TinSampler 竖直求交 → 结果表+2D 柱状预览+生成柱入场景) · 开孔坐标管理(全列可编辑即时入库/新增/删除/导入导出/筛选)
  · 原始钻孔柱状图(Canvas 岩柱+煤层+刻度+标注, 逐段可点+层位详情) · 展绘层位数据(煤层色块复选+顶/底板 → 分图层高程点入场景)。
- 煤质管理(aa752b5, +12 测)：煤质数据管理(导航树/16 列表/选中样品指标网格/衍生指标/CRUD+编辑对话框 4 Tab 含煤类反推/CSV 导入导出模板/重算层平均)
  · 空间分布(控制面板 + Scene3DView 实测点/插值体素/化验区边界/底图网格, IDW/OK/NN/MA, GB 分级·冷暖·光谱色带, 置信度淡出, 空间结论)
  · 数据看板(5 KPI/结论横幅/煤类饼/煤层对比双轴柱/健康度/化验段·灰分·硫分分级·洗选 Tab) · 统计分析(筛选/KPI 条+GB 分级带/分组表/箱线/
  结论·散点·相关矩阵·洗选·离群 IQR / 直方·高程剖面·商品煤符合性·用途适宜性) · 钻孔柱状图(3D 排列 26 化验孔, 按煤层/Ad/S 着色, 详情)。
- 工艺参数管理(443958d, +23 测)：工艺架构定义(系统/环节树 + 10 列参数编辑 + 约束详情 + 未保存提示) · 平盘工艺地图(现状视图: 环节树/参数对照/
  套用模板切换/适配设备; 方案视图 MVP) · 参数模板库(新建/复制版本+1/归档/删除 + 分组参数值编辑 + 引用平盘) · 现场验收录入(录入表即时偏差/状态,
  结论单选, 提交, 偏差报警)。
- 设备管理(f851c22, +17 测)：机群总览(结论横幅/5 磁贴/需关注清单双击下钻/类别达标率柱) · 设备信息管理(列表+PNG 转台播放器 拖拽/滚轮/进度条/
  暂停 + 参数面板保存 + 工艺作业联动) · 设备智能编组(参数条/铲车匹配矩阵含评分进度条/推荐方案卡) · 设备数据分析(6 KPI/班次效率时间轴/
  OEE 趋势·故障 Pareto·主控因素(瀑布/热力/回归)·优化建议·Weibull 可靠性) · 设备生产数据(可编辑明细 + 6 磁贴 + 2×2 图) · 班次效能预测
  (分类设备清单/4 KPI/趋势+预测带/直方/散点/五维评分/分析细节/编组建议) · 设备效能预测(基线卡/4 杠杆滑块/预设/结果磁贴/阶梯图/贡献排序/结论)
  · 设备能力(结论横幅/4 KPI/衰减趋势·同类对标·损失归因/诊断结论) · 数据导入导出(5 类数据 模板/导入 跳过·覆盖/导出)。

**等价替代(记录)**：Excel(.xlsx) → CSV 同列头；LiveCharts 多 Y 轴 → 单副轴；Helix 3D → Scene3DView 方柱/点；虚拟钻孔「视图中选三角网面」→
场景图层顶点重建/OFF 文件；设备三维无序列帧时的程序化几何 → 「无模型」占位；展绘钻孔/生成三维柱 → 2D 平面实体柱(场景无三角网实体)。

**验证**：全套 **1677 测试通过**(本节 +75)；env 门控自检逐个打开 24 页面(22 窗 + 2 对话框) → RenderTargetBitmap 截图 `chk_geodb/*.png`
全部 ok=24 fail=0，目视 16 张确认布局/数据/图表正常；自检块已删。自检抓到并修的坑：① 钻孔组自写 `InitializeComponent() => AvaloniaXamlLoader.Load`
遮蔽了生成版 → x:Name 字段全 null(NRE)，删自写即可；② ChartView `using var` + 显式 Dispose 双弹 PushClip → "Wrong Push/Pop state order" 崩溃；
③ Fluent TabItem 默认 24px 大字 → 样式收到 13px。合并冲突 1 处(两组同名私有 `NullIfEmpty` → 改名)。

## §三〇一 三维地质建模页：功能接入系统（三角网成一等对象 + 25 个独立窗口 + 24 条场景版命令）(2026-09-07)

用户指令「接入三维地质建模部分功能到系统中」。此前该页 49 个功能项(原 MeshEditLib 34 + BlockModelLib 12 + 采矿模型 3)多数只是
读 OFF/CSV 文件 → 写 OFF 文件的命令行报表，与场景对象无关；原版则以内核 MeshEntity 为一等对象、各功能带对话框/窗口。

**基础层**(e80e546 / 0ec5c8b / 1941fb0)：
- `Cad/Draw/MeshEntity.cs`：三角网场景实体(顶点 xyz + 三角索引；唯一边线逐顶点真高程渲染 `Seg3`；包围盒粗排斥拾取；Apply/Explode/无夹点；
  SceneIO `mesh` DTO 存档；特性面板 名称/顶点/三角/范围/高程/表面积；对象树按名称列子项点选)。`PolylineEntity.Zs` 三维多段线(落面/交线/等值线/剖面线)。
- `MainWindow.Modeling.cs`：选中三角网/点/线 → 算法 → 结果入场景(可撤销/存档)；无选中时从 OFF/CSV 导入即成场景对象。场景版命令 24 条：
  创建三角网(选中点 + 可选闭合线裁边)·多段线嵌入三角网·闭合线裁剪面/裁剪面·固化成体·侧面三角网·快速建模·地质体建模·基本几何体(视口放置)·
  修改高程点·修改点样式·赋节点高程/点落到面上·顶点焊接·闭合线裁剪·统一线高程·线落到面上·生成三角网边界·沿线分割三角网·合并三角网·面交线·
  修复拓扑关系·删除三角面·格网质量检测·补洞·光顺·两期三角网算量·三角网体积·体素格网体积·实体转块体·构建等值线·创建剖面·实时曲面坐标(坐标栏 Z)。
  新算法 `SurfaceVolume`(两期挖填 + TriGrid 分桶采样)、`PolylineClipper`(凹多边形裁线/三角质心选取)。绕向统一 `OrientUp`(边界环/成体/内外判定依赖)。
- Ribbon 页按原版重排：建模 · 倾斜摄影 · 编辑(点/线/面/体/工具 下拉) · 地质统计学分析 · 更新地质模型 · 块体模型(12) · 采矿模型(3)。
  OFF 导入即三角网(开始页「导入」)。`Views/Modeling/ModelingContext.cs` 页面契约 + `ModelingWindowFactory`(功能项名 → 窗口, 登记者优先)。

**独立窗口 25**(四组并行移植, 每组 `Views/Modeling/*Windows.cs` 登记 + `tests/Modeling*Tests.cs`)：
- 网格编辑(b498f71, +19 测)：地质体建模(QuickModelDialog, `Cad/QuickModelSampler` 逐字移植 1000 行建模器) · 格网质量检测(DiagnoseDialog + `MeshDiagnoseMarkers` 场景高亮/修复)
  · 两期三角网算量(VolumeSplitDialog + `CutFillSolids` 填/挖封闭体) · 展点(ShowPointsWindow) · 构建等值线(ContourBuilderWindow + `ContourEngine` 逐字移植)
  · 创建剖面(SectionCutWindow + `SectionEngine`/SectionBuilder 剖面图/钻孔投影) · 动态剖面(DynamicSectionWindow)。
- 地质统计(534b7b4, +28 测)：快速估值/克里金估值(选点插值版, `Cad/EstimationAlgorithms`+`EstimationEngine` 逐字移植; 数据分析面板/变差函数编辑器)。
- 更新地质模型(3f52897, +20 测)：补勘钻孔写实(批次/孔/层位/煤层结构/CSV/展绘) · 现状写实(批次/拾取见煤点/标记/建现状面) · 更新煤层面(`Cad/SurfaceUpdateEngine` 全算法路径 + 分级预览 + 应用/撤销)。
- 块体模型(8f4da21, +20 测)：创建/约束/导入/导出/属性赋值(含煤质联动)/着色/删除/筛选/切面剖切/输出报告/实体转块体/体素格网体积 + 块体模型浏览器
  (`Cad/BlockModelMeta`/`BlockModelReport`/`BlockVoxelBuilder`/`BlockCoalQualityLink`/`BlockCellPredicate`)。

**记录(仍受阻/等价替代)**：加载倾斜摄影(OSGB 内核渲染)、分割地质体/布尔四则(网格布尔需内核)、采矿模型(CarveStrip 内核)、标识起点/线序(原即占位)；
沿线分割多段折线按首尾竖直面；报告 PDF(QuestPDF)禁用；块体渲染为平面 RectEntity(无边线样式/实例化)；剖面/等值线/估值格为二维场景实体。

**验证**：全套 **1774 测试通过**(本节 +87)；env 门控自检在真实场景串跑命令链 18 项(两张 TIN → 快速建模水密体 43200 m³ = 两期算量填方 = 散度定理体积 →
等值线/边界/剖面/落面/裁剪/删面/分割/合并/转块体/体素/存档往返)全通过 + 逐个打开 25 个窗口截图 `chk_modeling/*.png` 25/25；自检块已删。

## §三〇二 三角网面模型显示 + 三维平移修正 (2026-09-07)

用户反馈「三角网只能显示格网，不能显示面模型」「三维平移偏移量有点大，模型直接飞出视图」。
- **面模型**：`SceneEntity.TessellateFaces` 虚方法 + `Scene.BuildFaces`；`MeshEntity.TessellateFaces` 逐三角平面法线×平行光(两面受光, k=0.42+0.58·|n·L|)
  调制实体色或地形色带(`ColorByElevation`, 低绿→黄→棕→高白)。视口新增三角面缓冲 `SetSceneFaces`, ScenePass 先以 `GL_TRIANGLES` + `glPolygonOffset(1,1)`
  画面再画线(边线/高亮浮在面上, 无 z-fighting)。显示模式 `MeshEntity.RenderMode` 线框/着色面/着色面+线框(默认, 边线压暗 45%)；
  「渲染配置」(开始页视图组, 原死按钮)接对话框；别名 线框显示/着色显示/面加线框/高程着色面。选中高亮总画边线(`TessellateEdges`)。
- **三维平移**：`Camera.PanScreen` 3D 分支改为视平面平移(相机右/上向量 × 注视距离处每像素世界量 2·Dist·tan(fov/2)/vh)；
  旧实现对 Z=0 平面反投影, 斜视/近地平线时一像素跨越巨大距离把模型甩飞。2D 仍按 Z=0 平面抓点跟随。范围缩放注视点取几何高程中心(`_sceneZc`)。
- 验证：1777 测试(+3: 面镶嵌/色带/3D 平移位移=像素×每像素量且近地平线不爆)；实机截屏 `chk_faces/` 三种模式均正确(GL 视口需 CopyFromScreen, RenderTargetBitmap 拍不到)。

## §三〇三 三维滚轮缩放修正 · .3dm 直接成面模型 · 默认着色面 (2026-09-07)

用户反馈「三维模式下滚轮 三维模型会旋转消失」「导入 3dm 慢、模型显示还是线框」「默认不显示网格、直接显示面」。
- 滚轮：`Camera.ZoomAtScreen` 3D 分支改为视平面口径——光标相对屏幕中心的偏移按注视距离处每像素量换算成视平面上的世界点 p，缩放后 Target += p·(1−Dist'/Dist)，
  光标所指点在注视深度上不动；旧实现对 Z=0 平面反投影, 斜视时交点飘远, 缩放后注视点大幅跳动(看似旋转消失)。测试: 中心缩放注视点不动、偏心位移=像素×每像素量×0.1、近地平线连放 20 次漂移<模型跨度。
- .3dm：`TdmImportService.LoadMeshes` 网格级读取(顶点/索引/名/色) → `ImportTdmAsMeshes` 每网一个 MeshEntity(图层=网格名) 面模型显示；实测 4-2底面.3dm 43018 三角 导入 92 ms、刷新 3 ms(镶嵌缓存)。旧线框显示通道仅作解析失败回退。
- `MeshEntity` 边线/着色面镶嵌缓存(键=模式/高程着色/颜色/标高, `Invalidate()` 清)；默认 `RenderMode=Shaded`(不叠网格线, 渲染配置可切)。
- 验证：1779 测试；实机 3dm 导入 + 3D 偏心滚轮 10 次相机日志正常(yaw/pitch 不变、target 按预期趋向光标)。

## §三〇四 3dm 导入后框选失效修正 + 3D 框选 (2026-09-07)

用户反馈「3dm 导入模型后，框选无法使用」。两层原因：
1. `SelectionBox.Match` 靠 `Tessellate` 出的线段判定，三角网默认「着色面」模式下 Tessellate 不出边线 → 永远选不中。改 `MatchMesh`(包围盒全含=窗口选；交叉选=顶点在框内/边穿框/框心落在面内)，圈选与屏幕空间判定改走 `TessellateEdges`。
2. 3D 视图原本左键拖拽=轨道旋转，根本没有框选。改为 2D/3D 一致：左键拖拽=框选(3D 在屏幕空间判定：`Camera.MakeProjector` 世界→屏幕投影, `SelectionBox.MatchScreen`)，轨道旋转改 Shift+左键 或 Shift+中键；帮助文案同步。
测试 +4(`SelectionBoxMeshTests`/`BoxSelect3DTests`：着色面模式窗口/交叉/圈选、面内框心、投影与反投影互逆、透视下三角网与抬高多段线的窗口/交叉判定)。全套 1783 通过。

## §三〇五 3D 点选/选框落到视平面与屏幕空间 (2026-09-07)

用户反馈「选择框没有，点击也选不中模型」(3D 视图、3dm 模型在高程 1000+)：点选与选框都还在用 Z=0 平面反投影。
- 点选：3D 走 `SelectionBox.PickScreen`——实体边线投影后按像素距离(容差=捕捉像素)命中；三角网另按"点击落在某三角投影内"命中并取深度最前者(着色面模式点在面内即选)，边线优先于面；隐藏/锁定层排除。2D 也补了"点在面内选中三角网"。
- 选框：四角改落到视平面(`Camera.ScreenToViewPlane`：过注视点、垂直视线；2D 退化为 Z=0 平面)，`BoxRect` 带 z。
- 投影：`Camera.MakeProjectorDepth`(屏幕坐标 + NDC 深度)，`CadGlViewport.WorldToScreenDepthProjector/ScreenToViewPlane`。
测试 +2(面命中取最前/网外空/贴线优先/隐藏排除；视平面落点与投影互逆、中心=注视点)。全套 1785 通过。

## §三〇六 2D 交互全失效根因(矩阵求逆阈值) + 选中高亮突出 (2026-09-07)

用户反馈「选择框没有，点击也选不中模型」，截图为 2D 视图。env 门控自检(导入真实 4-2底面.3dm 后直接设选框/调 BoxSelect/PickAt 打印判定值)定位：
- **根因**：`Mat4.Invert` 用固定阈值 `|det| < 1e-12` 判奇异。大坐标场景的 2D 正交投影(halfH≈1e4、far≈2.6e5)各轴尺度悬殊，行列式本就 ~4e-14 却完全可逆 → 被误判 → `ScreenToWorld` 全线返回 null → **2D 下选框(BoxRect 空)/点选/对象捕捉/坐标读数全部失效**。改为相对阈值：`|det| <= 1e-9 × Hadamard上界(各列范数之积)`，并加 NaN/Inf 防护。自检复测：2D 窗口选/交叉选/点选各命中 1；3D 交叉选/点选各命中 1(窗口选 0 因模型大于选框，语义正确)。
- **选中高亮突出**(用户「突出一下各个模型选中后的高亮显示」)：三角网选中后在其表面盖一层高亮**青色着色面**(`MeshEntity.TessellateHighlightFaces`，保留平行光明暗；`HighlightPass` 深度开 + **负** polygon offset 压住原面免 z-fighting)，再叠**三维包围盒线框**；边线仅在 ≤30000 条时叠加(大网靠色面+包围盒即可辨识)。高亮色由黄(1,0.9,0.2)改青(0.15,0.95,1.0)——与黄色地形、深色背景都拉得开。所有清高亮处同步清高亮面。
测试 +4(`Mat4InvertScaleTests` 大场景可逆 + 真奇异仍 null；`HighlightFacesTests` 三顶点/青色主导/明暗/模式无关/标高偏移)。全套 1789 通过。截图 `chk_box/highlight3d.png`。

## §三〇七 视口右键三态(2D / 3D Orbit / 3D 选择模式) + 大网首次点选卡顿修正 (2026-09-07)

- **右键三态(忠实原版)**：用户指出选择模式应在右键而非 Ribbon。照原 `MainWindow.ContextMenu.cs` 的 `RefreshCtxToggleViewState`，
  视口右键菜单顶部三项 `切换到 2D 视图 / 切换到 3D Orbit / 切换到 3D 选择模式`(各带图标)，**弹出时隐藏当前所处那一项**。
  绑定规则落到纯函数 `Cad/Draw/NavBinding.OnPress`(可单测)：2D 左键=框选/点选；3D 默认左键拖=轨道旋转、单击(<4px)=点选；
  3D 选择模式下左键只框选/点选(不旋转)；Shift+左键=临时框选；中键恒平移。撤掉此前加的 Ribbon「选择模式」按钮。
- **大网首次点选卡顿**：症状「第一次点击超级慢，之后正常」= 首次触发的懒构建。逐项计时定位并修：
  ① `MeshEntity.DistanceTo` 与 `SelectionBox.MatchMesh` 原先遍历 `Edges`(4 万三角 → 6.5 万边去重表，**冷构建 43ms**)，
     改为逐三角 + 三角包围盒早退 + 点在面内直判 0；② 3D `PickScreen` 顶点只投影一次(原逐三角投影 3 倍)+ 三角屏幕包围盒排斥；
     ③ `TessellateHighlightFaces` 加缓存(键=高亮色+标高)；④ 高亮边线判定改按 `TriangleCount`(原按 `Edges.Count`，为判定而建表)。
  实测(Debug, 4-2底面.3dm 43018 三角)：冷 2D 点选 **16ms**(且不再建边表)、3D `PickScreen` **2ms**、高亮面 0ms(缓存)、热点选 6ms。
- **顺带**：框选浮标松开即收起(单击一下不再残留「框选中」提示)；选中高亮去掉外层包围盒线(用户要求)，只保留高亮青色面 + 小网边线。
测试 +11(`NavBindingTests` 5 项绑定矩阵、`MeshPickPerfTests` 2 项性能回归锁(拾取/框选/屏幕点选后 `HasEdgeCache` 必须为 false)、
`HighlightFacesTests`/`SelectionBoxMeshTests`/`Mat4InvertScaleTests` 承前)。全套 **1796 通过**。右键三态经 env 自检 7/7。

## §三〇八 AutoCAD 式无限格网 + 菜单图标补齐 + 状态栏开关选中态 (2026-09-07)

- **无限格网**(用户「把 2D 视图中的网格做成和 AutoCAD 一样的空间无限的格网」)：原为初始化时建一次的 21×21 静态网(step=1, 缩放后要么看不见要么糊成一片)。
  新增纯逻辑 `Cad/Draw/GridPlan.cs`(`GridPlanner`)：按 世界长度/像素 以 **1-2-5 换挡**选细线间距(保证屏幕间距 ≥10px)、**每 5 格一条主线**、
  覆盖范围**按主线对齐世界原点**(与 AutoCAD 一致, 原点处即主线)、线数超上限自动升挡。视口 `EnsureGrid` 每帧判定(挡位变或平移出覆盖范围才重建几何, 否则复用)，
  2D 覆盖 1.15×可见区、3D 3×(边界落到视野外)；细线/主线分色, X 轴红、Y 轴绿(在视野内才画)。实测换挡: 默认 minor 0.5 → 放大 0.1 → 缩小 5，线数 160~500。
- **菜单图标**(用户「把图标也做好」「右键菜单也完善图标」)：Ribbon 下拉 64 项(点/线/面/体/工具、圆、圆弧、标注、剪贴板、测量、基本几何体…)、
  视口右键菜单 20 项、「切换窗口」下拉(文档/标准视图/范围缩放/上一视图)、对象捕捉模式菜单、图案填充 —— 全部挂上 16px 图标(复用既有 IconDict 资源, 无新增图片)。
  自检确认: 右键 20/20、下拉 22/22 有图标(Dock 库自带的 _Float/_Dock 面板菜单除外)。
- **状态栏开关选中态**(用户「这个图标的选中状态再优化一下」)：正交/捕捉/栅格/栅格捕捉四个 ToggleButton 原用 Fluent 默认实心蓝(压住图标)，
  改 `status-toggle` 样式：透明底 → 悬停浅灰 → **选中=淡蓝底(15%)+蓝描边**, 圆角 4, 22×22。
测试 +5(`GridPlanTests`: 1-2-5 换挡、跨 6 个缩放量级屏幕间距 ≥10px、主线对齐与覆盖判定、线数上限、每 5 格主线)。全套 **1801 通过**。

## §三〇九 麒麟 .deb 打包链(Windows 可出包, 依赖随包携带) (2026-09-07)

用户「给我打包一个 deb，我来测试」「把麒麟必要的依赖下载，一起打包」。本机无 Linux/WSL/dpkg-deb/fpm/ar，原 `package-deb.sh` 依赖 fpm 跑不了 —— 新增三个脚本：
- `build/fetch-deps.sh`：联网取 **app-local ICU**(NuGet `Microsoft.ICU.ICU4C.Runtime.linux-x64` 72.1.0.3 → libicuuc/libicui18n/libicudata, 38MB)。
  ICU 是 .NET 非 Invariant 全球化的硬依赖, 也是麒麟上最常缺/版本不匹配的一个。
- `build/make-deb.sh`：交叉发布产物 → deb 树(/opt/pitmine3d + /usr/share/applications + icons + doc) → **纯 tar + 手工 ar 组装**(ar 头 60 字节定长字段自己拼, 成员序 debian-binary→control.tar.gz→data.tar.gz), 因此 Windows(Git Bash) 也能出包。
  启动器 `pitmine3d.sh`: 系统查不到 `libicuuc.so` 才把随包 ICU 加进 `LD_LIBRARY_PATH`(ldconfig 不在 PATH 时翻常见库目录兜底), 两者都无则退 `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`; `PITMINE_SOFTWARE_GL=1` 强制软件渲染。
  postinst 建 `/usr/bin/pitmine3d` 软链 + ICU soname 软链 + 刷新桌面/图标缓存。Depends 只留 libc6, X/GL/fontconfig 走 Recommends(装包不会因缺这些失败, 麒麟桌面本就有)。
- `build/verify-deb.sh`：本机无 dpkg 时回读校验 —— ar 魔数/成员名与顺序/头结束符、三成员 gzip 完整性、control 必填字段、postinst 755、
  主程序与启动器 755、桌面项/图标 644、属主全 root/root、包内确有 .NET 运行时。
**部署修正**：地质数据库原落**临时目录**(重启被清), 且装到 `/opt` 后程序目录属 root 不可写 → 新增 `GeoDatabase.DefaultPath()` 落用户数据目录
(`~/.local/share/PitMine3D.Kylin/geo.db`), 建不了再退临时目录; +2 测试(路径可写/非程序目录、在该路径建种子库并查到 30+ 表)。
**产物**: `dist/pitmine3d_0.1.0_amd64.deb` 78MB(985 条目), 校验全通过; 全套 **1803 测试通过**。上机验证仍需用户在麒麟机执行。

## §三一〇 展绘钻孔三维化 + 文字渲染对齐原版内核 (2026-09-07)

用户「展绘钻孔的功能，对比 pitmine 来做」「不是二维的」「文字的渲染策略也调整下」「pitmine 中不是这么处理的吧」。
- **展绘钻孔 → 真三维柱**：此前因场景无三角网而做成二维矩形柱；MeshEntity 到位后按原 `BoreholeColumnBuilder` 重做 ——
  圆柱半径 7m / 28 棱、孔口高程→孔底按高程铺满(无煤岩色 5FA84E、煤层煤色 3C3C3C、同半径平齐)、
  **按颜色合并**成三角网(岩 1 张 + 每个煤层编码各 1 张)、整柱只封最底最顶(中间接缝在柱内不可见)、
  孔号置柱顶(字高 34)、煤层名置柱侧(字高 19、间距 18、相邻竖向避让 20)+ 三维引线; 展绘后自动切 3D(俯视只见圆截面)。
- **文字渲染策略**(核对原 `AcGe::StrokeFont` / `StrokeFontRegistry` / `xllAcDb_Text::worldDraw` 后确认原版做法)：
  ① 优先取**系统真字体字形轮廓**(原版走 Windows GDI GetGlyphOutline; Kylin 侧我们自解析 TrueType `cmap(4/12)`/`loca`/`glyf`,
     简单+复合字形, 二次贝塞尔按固定步细分, em 归一化, 逐字缓存) —— ASCII 与中文统一走真字体;
  ② 该字体缺此字形才回退内置笔画(原版内置仅 ASCII, 我们同); ③ 真字体字形**实心填充**(见 §三一一 更正);
  ④ billboard 用相机右/上基向量、对齐偏移沿基向量算(原 `pWd->billboardBasis`)。
  `GlyphFontHost` 按候选名探测并**用中文探针字复核**——Avalonia 找不到请求字体会静默替换(Windows 上换成 Segoe UI 无中文),
  不复核会误判"已装中文字体"(实测踩到); 候选顺序: 麒麟常见 CJK → 宋体(原版默认) → 黑体/雅黑 → 拉丁兜底; 都没有则中文交回笔画字体(画不出)。
  排版改为按真字体 `hmtx` 逐字步进(原先固定 0.8 字高), 中英混排不再挤压; 2D 公告板用固定基向量(球坐标基会把字斜过来甚至镜像, 实测踩到)。
测试 +4(`GlyphFontTests`: 测试内自造最小 TTF 验证轮廓解析/em 归一化/步进, 缺字回退, 逐字步进, 无字体时行为不变) + 展绘钻孔 2 例改判三维几何
(顶点/三角数、煤段真高程 450~455、柱径 14m、注记 ScreenFacing 与高程、三维引线)。全套 **1807 通过**；实机自检 2D/3D 各截图确认。

## §三一一 文字实心填充(更正 §三一〇 的"纯轮廓"结论) (2026-09-07)

用户「pitmine 中不是这么处理的吧」+ 给出 Windows 原版钻孔标注截图(孔号为实心黄字)。
复核原 `Kernel/xllAcGe/src/Core/GeomKernel/StrokeFont.cpp`，注释写得很直白：
「优先用绑定的真字体(GDI)取字形 —— ASCII 与 CJK 统一走真字体，这样数字/英文也能拿到**实心填充(fillTris)**…
无 GDI 绑定/该字体缺此字形 → 退回内置简笔画(仅 ASCII 有定义，**纯轮廓无填充**)」。
即 §三一〇 写的"纯轮廓无填充与原版一致"是错的：**只有回退的简笔画才是轮廓，真字体路径是实心**。原版用 earcut
(`Kernel/xllAcGe/include/Core/GeomKernel/earcut.hpp`)三角化字形轮廓。

- `GlyphFont.FillTriangles(char)`：字形轮廓 → 实心三角(em 归一化，逐字缓存)。用**梯形扫描填充**+even-odd：
  按 em 高度切 64 条带，带的上下边各与轮廓求交，交点数一致就配成梯形(斜边分段线性)，否则退回该带中心线的矩形。
  相比 earcut 不必判定外轮廓/洞、不怕自交，任意字形都稳；阶梯 = 字高/64，屏幕上通常 <1px。
- `TextEntity.LocalFillTriangles()` + `TessellateFaces` 覆写：非公告板文字的实心三角进面缓冲(P3_C3)；
  `LocalStrokes(skipFilled: true)` 让已填充的字不再重复出轮廓线，缺字回退的简笔画仍走线。
- `BillboardText` 增 `Fills`；视口 `EnsureBillboards` 同时建线网格与面网格，`ScenePass` 先 `GL_TRIANGLES` 画实心字再 `GL_LINES` 画简笔画。
- 自检钩子：`PITMINE_SELFTEST=<Ribbon 命令名>` 时窗口显示后自动派发一次该命令(展绘钻孔另加不弹窗全选)，供截图核对渲染。
测试 +2(填充面积逼近字形包围盒面积/不越界/进面缓冲带颜色/已填充字不再出轮廓；公告板真字形出 Fills、缺字回退出 Strokes)。
全套 **1809 通过**；实机自检截图：孔号数字已为实心黄字，用真字体(黑体)离线光栅化复核"煤"字填充完整可辨。

## §三一二 「特性」组与特性面板对齐原版 (2026-09-07)

用户「两系统特性部分是否不一样，修改 kylin 实现」+ 给出原版「特性」组截图。逐项比对
原 `MainWindow.xaml` 特性 RibbonGroupBox / `MainWindow.ContextMenu.cs` / `EntityPropertyBag` 后补齐：

**Ribbon「特性」组**（此前 Kylin 只有一个线型下拉，注释里自己写着「现有命令仅线型」）
- **颜色栏**：随层 + ACI 标准色板(前 9 号 + 橙/草绿/天蓝) + 自定义 RGB(命令行 `颜色 #RRGGBB` / `R,G,B` / ACI 号 / 色名)。
  按钮关闭态显示色块 + 名称，同原版 `EntityColorPicker`。Kylin 实体只存 RGB 浮点、无「随块」，
  故「随层」实现为取所在图层的颜色（`AciPalette`，纯逻辑可单测）。
- **线宽栏**：随层/默认/随块 + 24 档标准 DXF 线宽（`LineWeightUtil.Choices`）。
- **写入口径改为忠实原版**：三栏都是「有选中 → 写入选中实体」（原版 `ApplyPropertyToSelection` 逐个
  `PitMine_SetEntityProperty`）；无选中才作新建默认（Kylin 既有语义，原版此时只记日志）。
  锁定图层的实体跳过并计数回报（同原版：引擎拒绝写入则不改显示）。
- **双向联动**：`SyncPropertyRibbonFromSelection()` 在选集变化/面板改值后回填三栏；无单选复位到默认。
  用 `_suppressPropRibbon` 断环，同原版 `_suppressPropertyComboEvents`。
- **测量下拉补齐六项**（原只有 距离/角度/面积）：快速 / 距离 / 半径 / 角度 / 面积 / 体积，主按钮=快速测距。
  判定口径照原版双模式：选集能算就按实体算，不足以判定才回到鼠标点测（`MeasureOps`，纯逻辑可单测）。
  新增 `MeshEntity.Volume()`（散度定理有向体积）。

**特性面板**
- **分组标题**：此前 `Describe` 的 cat 字段被丢弃、所有行平铺；现按 常规/几何/选择/文档/视图 出分组标题
  （原版 PropertyGrid 自带分组）。
- **多选**：此前只有一句提示；现出「总数量 + 各类型计数」（原版 `MultiSelectionProperties` 的
  总数量/AcDb 实体/Scene 实体，Kylin 只有托管实体故按类型分）。
- **空选**：此前面板全空；现出 文档名称 + 实体总数/选择数量/正交模式/捕捉模式/3D 视图（原版 `DocumentProperties`）。
- **圆弧**补 起始角度/终止角度 两行（只读派生；原版有这两行且可编辑，Kylin 圆弧是三点表示）。
- 编辑提交前拒绝锁定图层。

**顺带修掉的两个坑**
- **测试偶发失败**：`GlyphFont` 的字体 Provider 是进程级静态，GlyphFontTests 装测试用最小字体的那段时间里，
  并发跑的 DrawToolsTests 量到的是假字体步进 → 对齐用例偶发失败。把「装假字体的」与「量文字几何的」
  26 个测试类归入同一 xUnit 集合串行；其余 200 多个类仍并行，全套耗时不变（54 s）。
- **功能区横向溢出滚不动**：`开始` 页的 ScrollViewer 里 StackPanel 默认 Stretch，被拉伸到视口宽，
  Extent 被钳成视口宽 → 溢出的组静默裁掉且滚不到。加 `HorizontalAlignment="Left"` 后 Extent 报真实内容宽。
  **仍存问题**：最大化(2575px 窗口)时功能区尾部（特性组的选择按钮、帮助组）仍在窗口外且无滚动条，
  疑似分组实际绘制宽大于其 measure 宽（DPI/度量口径待查），窄窗口下滚动正常、显示无误。

自检钩子扩展：`PITMINE_SELFTEST` 支持分号分隔多条 + `@窗口 <宽> <高>` / `@Ribbon末端`（截图核对用）。
测试 +8（ACI 解析/显示名、线宽候选、半径/体积/面积/距离/角度六项判定与文案、多选统计、文档属性、圆弧起止角）。
全套 **1819 通过**；窄窗口自检截图确认三栏与原版截图一致（颜色 随层 + 色块 / 线宽 随层 / 线型 实线）。

## §三一三 块体模型 12 命令逐项核对原版, 修四处失真 (2026-09-08)

按原 `Modules/BlockModelLib` 逐个功能项核对「三维地质建模 → 块体模型」12 个命令 +「采矿模型」3 个命令。
**结论: 创建 / 属性赋值 / 块体着色 / 删除 / 筛选 / 切面剖切 / 输出报告(统计口径) / 实体转块体 / 体素格网体积 /
块体浏览器 与原版逐字段一致**(对话框标签、录入模式联动、模板、校验、分级/分类着色、多段线裁剪与撤销、
诊断文案等均已对齐)。修掉的四处真差异:

1. **「高容错 GWN」复选框空转**(约束块体 / 实体转块体 / 体素格网体积 / 离散化模型)
   原版判据默认是**奇偶射线 + XY 分箱**, 只有勾了高容错**且**网格非闭合才换缠绕数(水密体奇偶本就精确且快);
   Kylin 此前恒用 GWN, 复选框只改一行文案。补移植 `MeshContainmentTester`(分箱射线 + bin 内 Z 包络剪枝 +
   `RaycastAbove/IsInsideClosed/BoxTouchesSurface`), 按原规则选判据。
   顺带修「约束块体」的 mesh 之上/之下: 语义回到原版(投影内且上方无三角 = 面之上), 且从"每点线性扫全部三角"
   (`SampleMeshZ`)换成分箱射线 —— 大网格不再逐块全表扫。

2. **.pmb 往返丢元数据**: 导出只写 Strings/GridSpec/Blocks 三段, 属性表(类型/单位/默认值/备注/分类)、
   显示样式(填充/边线/边宽/色带/活动着色属性)、已删 cell、分类码名与逐类颜色全丢 —— 岩性这类列重开只剩裸码 + 单色。
   按 `PmbmFormat` 文档补 PropertySchema(11)/DisplayStyle(16)/DeletedCells(17)/CategoryMeta(19) 四段读写,
   GridSpec 补齐 rotation(度→弧度)/storageMode/子块参数, footer `activeBlockCount` 照原版恒写 0。
   「全部块」范围直接整模型落盘(块序即线性下标, 已删集合可完整保留); 选了更窄范围才按子集重建网格。

3. **输出报告缺 PDF**: 此前标「本机不可用」。核查发现 QuestPDF **2024.7.1(与原版同版本)本机 NuGet 缓存已有**,
   且 net8.0 目标**无依赖**、自带 linux-x64/arm64 原生件(不碰 Avalonia 的 SkiaSharp) → 按原
   `BuildPdfHeader/BuildPdfBody` 补齐: A4 + 汇总四列表 + 属性统计表(含直方图条) + 分标高表 + 页码。
   **此项从"环境受阻"改判为已实现。**

4. **体积算量语义不对**: 原版 = `SumSelectedMeshVolume`(选中体逐个算体积, 只累计算得出的, 报「n 个体, 合计 v m³」);
   Kylin 此前把它派到「三角网体积」的逐网明细, 没有个数也没有合计。改为在 `BlockModelWindows` 登记专属处理
   (`ModelingWindowFactory` 优先于命令 switch, 不动主窗派发), 文案照原版;「三角网体积」明细保持不变。

**仍记录为受阻**: 「采矿模型」的条带划分 `CarveStrip` 走 C++ 内核 `PitMine_CarveStripFromBenchLines /
CarveStripOnSeam`, `Kernel/xllAcEd` 只有 .lib/.pdb 无源码, 且 Kylin 侧无等价的"坡顶线+坡底线→水密抽屉体"
构体基元 —— 需另立任务从几何定义重写, 不在本次范围。

**Kylin 比原版多做(非失真, 登记备查)**: ① 导入块体实现了 CSV(原版 UI 有该单选但代码拒绝, 提示"后续接入");
② 导出块体真的按「导出范围 / 导出列 / 格式」执行(原版这三项 UI 存在但代码恒写整模型 PMB)。

commit `79108dd` / `cf3294c` / `3efa126`; 测试 2188 → 2200 全绿(新增 12: 奇偶↔GWN 一致性、射线上下方判别、
盒穿面、判据切换规则、.pmb 整模型往返与旧文件兼容、PDF 结构)。应用冒烟启动正常(GL 初始化无异常)。

## §三六五 多文档串扰：撤销栈 / 文件路径归文档，切标签把窗口级残留清干净 (2026-09-11)

**现象**（用户实测"两个文档之间貌似互相干扰"）：文档 1 画了线，「新建」到文档 2 按撤销 ⇒ 状态栏说"已撤销"（文档 2 从没改过）；切回文档 1 按重做 ⇒ 文档 1 的线没了。
自检脚本 `@线示例;@视口;新建;撤销;@视口;@文档 1;重做;@视口` 在修前的 HEAD 上逐条复现（`场景实体=1` → 重做后 `=0`）。

**根子**：拆多文档（7fad0ed / 2b0a176）时只把 场景 + 图层表 + 视口 塞进了 `DocState`，**撤销栈 `_undo` 和 `.pmx` 路径 `_currentPath` 仍是窗口级单例**。
撤销是"快照进栈/出栈"——文档 2 的撤销弹出的是文档 1 的快照，灌进文档 2 的场景；文档 1 再重做，弹回的是文档 2 的快照。
路径同理：文档 1 打开了 A.pmx，切到文档 2 按保存 ⇒ **文档 2 的内容覆写 A.pmx**（会丢数据，比撤销那条更狠）。

**改动**（`MainWindow.axaml.cs` / `CadGlViewport.cs`）：
* `DocState` 加 `Undo`、`Path`；`_undo` / `_currentPath` 改成指向当前文档的属性 —— 二百多处调用点一行没动。
* `SetDocPath` 顺带把**标签名**改成文件名（多标签才分得清）；`SyncWindowTitle` 切标签时重设窗口标题（此前一直挂着上一个文档的文件名）。
* `OnActiveDocChanged` 清掉离开文档视口上的高亮 / 捕捉标记、夹点表、上次选择集；`_lastSceneCount=-1` 强制重建对象树
  （两个文档实体数恰好相等时旧树会留着）；刷特性面板的"文档状态"行；关掉绑着旧图层表的图层特性管理器。
* 网格开关：`ToggleGrid` 翻转改成 `GridVisible` 直设，新建标签的视口按 `_gridOn` 初始化、切标签时对齐 ——
  否则在文档 1 关了网格再新建文档 2，状态栏那颗钮和视口是反的，点一下反得更厉害。
* 标签关闭 ⇒ `DockableClosed` 把它从 `_docs` 摘掉（此前「切换窗口」下拉一直列着已关的标签）。
* 自检脚本新增 `@文档 <n>`（切到第 n 个标签），多文档串扰今后可无人值守核对。

**验证**：同一脚本在修后的树上：文档 2 撤销 → "无可撤销"；文档 1 重做 → "无可重做"、`场景实体=1` 仍在；文档 1 自己的撤销正常（→0）；文档 2 不受影响。
**未动的窗口级状态**（有意）：块体仓 `BlockModelStore` 本就是全局资源；命名选择集 / 剪贴板跨文档可用是 CAD 惯例。

## §三六七 多文档：切回上一个标签一片黑（GL 句柄跨上下文串号，非几何没重传） (2026-09-11)

**现象**（用户实测"一新建之前的文档内容就没了"）：文档 1 有图 → 新建 → 切回文档 1：视口全黑, 连网格都没有。
用 `@线示例;范围缩放;新建;@稍后 2500 @文档 1;@稍后 6000 @GL状态` + 截图在 6fd516a / HEAD 上都复现 —— 是老问题, 只是此前多文档只用 DebugState 核过、没截过图。
`@GL状态` 说渲染回调照跑(帧=67)、场景已上传, 说明**不是几何丢了, 是画出来的东西没进屏**。

**根子**：切标签 = Avalonia 销毁再重建该视口的 GL 上下文。`GlRenderer.Deinit` 删了程序/VAO 却**没把 `_program`/`_vao` 归零**;
新上下文里名字从 1 重新编号, `Init → TryBuildProgram` 链接好新程序(id 又是 3)后执行"先删旧程序"(`_program` 还是旧 3)——**把刚建好的新程序删了**,
之后每帧 `UseProgram(已删除)` 无效, 只剩清屏色。另有 f5eb49c 之后光标/捕捉标记/预览三块改走 `UpdateMesh` 复用 VBO, 它们的 `Mesh.Vbo` 在 Deinit 里也没清,
重建后 `BindBuffer(旧名)` 会撞上新上下文里同名的别的缓冲(场景/格网)并把它灌成光标线段。

**改动**：`GlRenderer.Deinit` 归零 `_program/_matProgram/_vao`、`MaterialReady=false`；`CadGlViewport.OnOpenGlDeinit` 把 `_cursor/_snap/_preview` 置 default。
自检补 `@稍后 <ms> <步骤>`（真回消息循环等一会再跑, 切标签这类要真渲染过的步骤必需）、`@GL状态`、`@标签点 <n>`（走页签条选中路径 = 鼠标点页签）、`@标签态`、`@标签坐标 <n>`；切文档时落一条 `[文档] 活动标签 → …`。

**验证**：同脚本截图 —— 修前切回黑屏, 修后网格 + 黄线都在。
**教训**：GL 上下文重建时, 所有缓存的 GL 名字(程序/VAO/VBO/纹理)都必须归零, 不止 `_has*` 标志；"删旧再建"的写法在新上下文里会删掉刚建的同名对象。

## §三八五 GIZMO 改成真正的三轴变换手柄：选中对象上 X/Y/Z 箭头、拖轴沿轴移动；夹点开关独立保留 (2026-09-13)

**起因**：用户反馈「gizmo 变为夹点开关了，这个不对」。查原版：内核 `Editor.h:102-105` 的 GIZMO 就是 `m_showGizmo` 夹点显示开关（`BuildViewState` 只用它门控 `vs.Grips`，左上三轴 HUD 无条件画），ribbon 提示/选项对话框/功能说明 H-42 也都写「夹点（Gizmo）显示开关」——Kylin 之前的 `夹点开关`(79e2d57) 是忠实照抄的。用户明确选择**有意偏离原版**：做成 AutoCAD 3DMOVE 那种三轴手柄，夹点显示另留开关。commit 6185449。

**做法**：
- 新 [GizmoGlyph](src/Cad/Draw/GizmoGlyph.cs)（纯几何）：手柄锚在选择集三维包围盒中心，+X 红 / +Y 绿 / +Z 蓝三根轴屏幕恒定 90px（轴杆 5 条平行线加粗，3D 线框锥箭头、2D 平面三角箭头），悬停/拖拽轴金黄，拖拽中另画贯穿约束线；`HitAxis`（屏幕空间到轴投影线段 ≤7px，中心 8px 死区让给夹点/点选）；`AxisParam`（光标射线与轴线最近点参数，对着轴看无解）；`WorldPerPixel` 用世界→屏幕投影雅可比的**最小奇异值**（针孔透视恰 = 焦距/深度，最大奇异值会混进离轴点沿深度动的那份而偏大），不依赖工作树里未提交的 `WorldPerPixelAt`。几何一律输出成带 Zs 的 `PolylineEntity` 再 `Tessellate`，渲染原点口径由实体统一处理。
- 新 [MainWindow.Gizmo.cs](src/Views/MainWindow.Gizmo.cs)：`_gizmoOn` 状态、`GizmoRebuild`(HighlightSelection 重算锚点)、`AppendGizmo`(RedrawHighlight 出口画到高亮通道最上层，并存一份"实体线+夹点"缓冲，之后悬停变色/缩放旋转只换手柄不重镶嵌选择集)、`GizmoHover`(视图戳变了也重画)、`GizmoTryBeginDrag/DragMove/DragEnd/Cancel`（幽灵 = 起拖时线框整体平移，大网/点云只画包围盒棱；松开 `ApplyEditTransform(Translate(dx,dy), dz)` 一步 Undo；图层锁定拒绝）。
- `Camera.ScreenRay` + 视口包装（近/远裁面反投影两点连线，2D 为竖直向下）。
- 接线（MainWindow.axaml.cs 4 处按下/移动/松开/Esc + 2 处高亮出口）；命令 `Gizmo/GIZMO` = 手柄开关（视图·Gizmo 键、AI 菜单标签改「Gizmo 手柄」），`夹点开关/夹点` = `ToggleGrips`；选项·显示 分成「启用夹点显示」与「显示 Gizmo（三轴变换手柄）」两项（`Options.Selection.GripsVisible` / `Options.Display.GizmoVisible`）。

**验证**：+7 单测 [GizmoGlyphTests](tests/PitMine3D.Kylin.Tests/GizmoGlyphTests.cs)（正交/透视尺度、2D 无 Z 且箭头共面、悬停变黄、轴命中与死区、轴参数含异面射线、真相机 ScreenRay 往返回到轴上同一点、包围盒 12 棱）。实机 `@线示例;范围缩放;@鼠标 按下 150 60;移动 180 61;松开` → 状态栏「Gizmo：沿 X 轴拖动 → 沿 X 轴移动 29.802 → （1 个实体）」，截图线整体右移、X 红 Y 黄(悬停)；3D 同链沿 X 移 15.13 落地，三轴锥箭头随视角；`Gizmo;夹点开关;GIZMO` 三步后手柄在、夹点方块无。

### §三八六 2.5D TIN：剖分慢（自适应抽稀分钟级→秒级）+ 三角网不按点云真实色显示（2026-09-13, commit 574f3ae）

**现象**（用户）：「2.5D 现在剖分慢，同时不按照点云的颜色进行着色」。

**根因**：
1. **慢**在抽稀不在剖分：`PcBuildTinAsync` 的「保留坡面细节」走的是点云抽稀命令那套 `PointThin.ThinAdaptive`（按曲率贪心排斥，每点查 (2·span+1)³ 个三维格、span 最大 4 → 729 次字典查找/点），30 万点 2.5s、200 万点分钟级；且 maxThin=4 把平地压到 12m 一点。Delaunay 早已是 delaunator（511d86c），不是瓶颈。
2. **色**：只有「全密度 + 不限点数」才把 `pc.RgbColors` 整份拷到 `mesh.RgbColors`，抽稀后顶点与源点对不上就干脆不带色；显示又恒按高程分带。原版 `AcGeConstrainedDelaunay.cpp` 注释明写 `alpha=0 → override_rgb 关闭，每个 TIN 顶点写【点云采样点的真实 RGB】`，`ApplyDefaultTinShading` 建完即 mode 15「点云真实色」。

**做法**：
- [PointThin.cs](src/Cad/PointThin.cs) 新 `ThinXyMinZ`（忠实原 LasLib `recon.cpp voxel_downsample_xy_minz`：XY 平面分格、每格留 Z 最低点——压掉树冠/设备留地面）与 `ThinAdaptiveXyMinZ`（忠实 `adaptive_voxel_downsample_minz` 两遍粗先法：粗格 Z 落差 <1m 平地只留 1 点，≥1m 坡面再按 0.25 倍细格各留最低点；FineVoxelRatio=0.25/FlatZRange=1m 原版内部常量）。两者都返回**源点索引**；键 `(ix<<32)|iy` 配 `PackedKeyComparer`。旧 `Thin/ThinAdaptive` 留给点云抽稀命令，不动。
- [MainWindow.PointCloud.cs](src/Views/MainWindow.PointCloud.cs) `PcBuildTinAsync`：抽稀换新法；「最大输入点数」等间隔切也记索引映射；`pc.HasRgb` 时 `mesh.RgbColors = srcIdx→pc.RgbColors`，**默认 `VertColors` = 真实色**（无 RGB 才退回高程分带）；回显补 平地格/坡面格 数 + 抽稀/剖分 耗时 + 「按点云真实色显示」。自检 `@点云示例` 给合成真实色（平盘赭黄/坡面深褐/煤带黑/矿卡黄），不然核不了这条。

**验证**：+6 单测 [PointThinXyMinZTests](tests/PitMine3D.Kylin.Tests/PointThinXyMinZTests.cs)（最低点/零格恒等/负坐标格号不撞/平坡格分流/全平等价体素法/200 万点 <10s 护栏，实测 0.25s），全套 3856 通过。同源对拍 30 万点：旧 ThinAdaptive 2547ms→7,681 点，新 27ms→72,878 点。实机 `@点云示例 60 40;2.5D TIN;取消选择;东北等轴测` 截图：三角网平盘赭黄/坡面深褐/黑煤带与点云一色，矿卡被最低点法剔掉只剩黄点悬空；真实 LAS `DLT20251222.las`（200 万点 → 184 万顶点/369 万三角）：抽稀 0.5s/剖分 2.1s，含入场景整链 ≤7s，`@取景 622600 4380900 500` 放大后台阶纹理即正射影像效果。

## §三八七 提取道路中心线改回原版骨架路由法：原 RoadCenterlineRunner 编排 + 对话框 + TIN + 作业区域 + 连通增强 (2026-09-13)

用户反馈「提取道路中心线没有使用 PitMine3D 中的算法」。核对原版：RoadLib「路网构建」首钮 → `PointCloudLib.RoadCenterline.RoadCenterlineRunner.RunAsync` → **`RoadSkeletonExtractor.Extract(台阶线, TIN, 参数)`（骨架路由法）**；「坡线配对取中线」`RoadCenterlineExtractor` 在原版里**没有任何按钮调用**。Kylin 原命令却是：选 2 条→`RoadTools.Centerline` 中点连线（原版没有）、否则→配对法 `RoadCenterlineExtractor`，且**所有点 Z=0**（注释还写着"Kylin 场景为 2D"，早已过时）。骨架法 875 行早移植好了（`RoadSkeletonExtractor`），只是没接到命令上。

**改法（忠实原 Runner，场景侧照搬）**
- 新 [RoadCenterlineRunner](src/Cad/RoadCenterlineRunner.cs)：原 RunAsync 对话框之后的那段抽成纯逻辑 —— 台阶线 <2 报「未找到足够多段线作为台阶线」；高程跨度 <1m 报「疑似 2D 无高程」不跑；作业区域预筛（`RegionClip.ExpandedBBoxes` 外扩 120m → `FilterBenchLinesByBBox`，逐块点名 + 未选定块计数）→ `RoadSkeletonExtractor.Extract`（有 TIN 用 TIN 插值面、无则台阶线块状面，口径进回显）→ 区域精裁 `ClipPolyline(minKeep=max(5,W_min))` → 连通增强 `RoadNetworkConnector.Connect`（SnapTol 4 / ConnectDist / ManualConnectDist=max(40,2.5×) / MaxBridgeSlopeDeg=可行驶坡度 / DrapeSpacing=max(2,格) + `RoadTerrainSampler` 铺贴补充线）或仅并入。回显按原版 Echo 顺序落 Notes。新 [RoadClipRegion](src/Cad/RoadClipRegion.cs) DTO。
- 新 [MainWindow.RoadCenterline.cs](src/Views/MainWindow.RoadCenterline.cs)：`ExtractCenterlineAsync` —— 运行前选中多段线 = 补充线候选（对话框可勾，勾了不当台阶线；不勾且 ≥2 条 = 旧语义框选台阶线）；否则读全图多段线，排除「点云_道路中心线」「等高线」层；多段线取真实 Z（`ZAt`，三维逐点 / 平面取标高），闭合线补回闭合段（挡墙才封得住）。`LoadRoadClipRegions` 照原 `RoadLibPlugin.LoadClipRegions`：读 `mineable_region` 整表不按选定过滤、几何不成立计数、台账为空/读不出/有但没选定三种口径分开说；工程库未连接**不**拉起连库（区域只是可选裁剪范围，别把命令吞掉）。TIN = 场景里顶点最多的 `MeshEntity`（原 `RoadTinReader.TryReadBestTin`）。覆盖时清「点云_道路中心线」再写；图层琥珀 #F2A517 线宽 0.5mm；输出 `PolylineEntity` 带 `Zs`。
- 对话框 = `PcForm` 照原 RoadCenterlineDialog.xaml 组织：说明 → 「生成范围」组（区域限制/全图单选 + 区域勾选清单，默认勾本期选定的；一块没选定则默认全图并说明去哪勾）→ 栅格精度 3 / 可行驶坡度 14 / 最小路宽 12 / 撒点数 400 / 面平滑 1.2 / 台阶线挡墙 → 「连通增强 / 手动补充」组（连接 + 断头距离 25 + 补充线勾选（没选中时灰掉，`PcRow.Enabled` 新加）+ 覆盖）。选了区域限制却一块没勾 → 校验挡住不静默退全图（原版教训）。自检下取默认值直通。
- 删 MainWindow.axaml.cs 里旧 `ExtractCenterline`（中点连线 + 配对法）。`RoadTools.Centerline` / `RoadCenterlineExtractor` 类保留（有单测），只是不再挂命令。
- 焊接容差 4m > 栅格 3m 时骨架一格长的小段两端吸到同一节点退化成零长（原版原样写进引擎）—— 入库时略过并报数「另 N 条焊接后零长已略」。

**验证**：+8 单测 [RoadCenterlineRunnerTests](tests/PitMine3D.Kylin.Tests/RoadCenterlineRunnerTests.cs)（台阶线不足 / 2D 守卫 / 100×20 缓坡走廊被 z=30 高台阶封住 → 恰一根 y≈10 中轴且 Z 沿走向抬升 / TIN 口径 / 远处区域 → 「台阶线 6 → 0 条」+ 不足警告 / 覆盖区域(未选定)点名 + 精裁后顶点全在环内 / 不连通仅并入补充线 / 连通回显「补充线已按现状地形铺贴」），全套 3865 通过。实机 `@导入 PitMine3D\Tests\1.dxf;提取道路中心线`（2931 条多段线，1319 条三维 / 1612 条带标高）：DEM 2001×1279@3m，骨架 36345 条 → 连通增强焊接 71987 端点 / 新增 104 段 / T 形 62 处 → 入库 4929 条（31582 条零长略），4.4s；关掉源层后琥珀中线布满采场台阶，取景 180m 半宽可见中线走在坡顶/坡底线之间的平盘中央。注：此图无 TIN 走块状面，`地表等高线` 层不等于「等高线」也当台阶线 —— 与原版口径一致。

### §三八八 坡顶底线提取：整场点云一跑就假死 → 按原版 bench_lines 等值线算法托管移植（2026-09-13, commit 3c5715f）

- **现象**：用户报「坡顶坡底线提取现在直接软件会死掉」。旧 `PcSlopeLinesAsync` = 栅格点 → 整场 Delaunay → 平陡三角公共边 → PolylineJoin；1m 格网整场几百万栅格点，剖分/拼线跑不完。
- **做法**：新 `src/Cad/SlopeLineExtractor.cs` 忠实移植 `Kernel/LasLib/src/bench_lines.cpp`(laslib::bench::extract_bench_lines) **现行通路**：最低点 DEM → JFA 欧氏填空 → 高斯平滑 → 坡度场 → marching-squares 平/陡(18°)等值线 → 逐顶点两侧高程判坡顶/坡底 + 多数表决 → 覆盖裁剪 → DP/去钩/去碎 → 原始点云断面精修 → 终次覆盖裁剪；区域内/外裁剪 port 自 xllAcEd `BL_ClipPolyline`。原版旧骨架/配对/合并层在其源码里已关(注释写着是"多条线拼接/全乱了"的来源)，同样不做。原版只在主路径用到 cell_size 等少数项，对话框的「挡墙最小高/最小台阶高/陡坡阈值/最短线长」传进去后其实没用——Kylin 只摆起作用的三项(DEM 网格 / 陡坡阈值=等值线阈值 默认 18 / 最短线长=去碎阈值 默认 30)。
- **命令端**：照原 SlopeLineDialog 组织：生成范围(全图/仅区域内/仅区域外 + 区域勾选清单，读 `Data.MineableRegions`) → 参数；后台线程 + 状态栏进度条 + **Esc 取消**(`_pcBgCts`)；结果三维多段线入 `点云_坡顶线`(红 1)/`点云_坡底线`(蓝 5)，一步 Undo。
- **一处有意偏离**：终次覆盖裁剪世界→格坐标减回 EmitRun 的 +0.5。原版没减，贴 DEM 边界行的顶点 lround 出界判未覆盖，两顶点的直台阶线整条丢(合成矩形点云 100% 复现；单测 `Stacked_benches_reaching_dem_border_keep_every_crest_and_toe` 回归)。
- **实测**：合成 6.1M 点 / DEM 2000×1500@1m：6 坡顶 + 6 坡底，0.4s，+350MB；真实 `DLT20251222.las` 200 万点：坡顶 157 条(14 km)/坡底 126 条(26 km)，DEM 843×594@7.61m，0.2s，线沿台阶缘排布(截图核对)。单测 8 项。
- **遗留**：`BeginChange()` 的 Undo 快照把整份点云序列化成 JSON(SceneIO.CloudDto)，百万点级每次编辑几百 MB 字符串，超 ~2000 万点会 OOM——所有点云命令共有，另开任务。

### §三八九 撤销快照不再逐点序列化点云：列表按引用进 SnapshotHeavyStore、JSON 只记 key（2026-09-13, commit f01beba）

- **问题**（§三八八遗留）：`BeginChange()` 每次 `SceneIO.Save(_scene)` 把整个场景含点云全部点+逐点色写成 JSON 字符串，撤销栈每层各留一份；200 万点每次编辑上百 MB，两千万点撞 .NET 字符串上限 OOM。加载点云后任何普通编辑 + 点云组几十处 BeginChange 全中。
- **做法**：`SceneIO.Snapshot(scene, heavy)` / `Restore(snapshot, heavy)` —— 与 Save 同一份 DTO，唯独点云的 点/逐点色/真实色/法向 四个列表按引用登记到新 `SnapshotHeavyStore`(引用同一性去重 ConditionalWeakTable + 强引用字典)，DTO 只记 `K=[4 key]` + `Src`；200 万点快照几百字节。撤销回来仍是**新实体**(语义同旧快照：着色可撤、删点云可撤)，但列表不复制；顺带真实色/法向随撤销保住（旧快照不存这两项，撤销一次真实色就丢）。`UndoManager` 改存 `Snapshot(Json, Keys)` 自带 `Heavy` 仓，**只在 Push 时清扫**两头都不引用的条目（Undo/Redo 刚弹出的快照正要恢复，那时清扫会扔掉正要用的列表），Clear 连仓清；字符串重载保留。存档 Save/SaveDoc/Load/LoadDoc 一律 heavy=null，格式一字不变。
- **前提**：点云四个列表创建后从不原地改（着色=整份换新 List、单色=置 null；grep 全库无 `Pts.Add`/`Colors[i]=`）。以后要原地改点云列表的代码必须先 Clone。三角网 Verts/Tris 有原地改(嵌入/修复)，**没有**纳入，大 TIN 的撤销快照仍是整份 JSON。
- **实测**：DLT20251222.las 200 万点 → 坡顶底线提取 → `U` 瞬时撤销、点云(真实色)仍在、283 条线撤掉 → `REDO` 回 284 实体 → 再 `U`×2 到空场景。全套单测 3882 通过 + 新增 UndoSnapshotTests 7 项。

## §三九〇 标注取点过程橡皮筋跟随 + 「标注样式」设置面板 (2026-09-13)

**起因**：用户要求「标注也需要支持橡皮筋拖拽，同时标注样式要提供设置面板」。之前线性/对齐/半径/角度/坐标标注取点过程视口里什么都不画（只有状态栏提示），尺寸线落在哪、数字多少要点下去才知道；「标注样式」只有命令行 `标注样式 <字高> [小数位] [箭头比]`。commit ec28cf4。

**做法**：
- 橡皮筋 [MainWindow.DimJig.cs](src/Views/MainWindow.DimJig.cs)：`AppendScenePreview` 出口接 `AppendDimPreview` —— 按"已取点 + 光标"用**落地时同一套 DimTools** 现推整条标注画到预览通道（同原版 AlignedDimensionJigAdapter/RadialDimensionJigAdapter 的 jig 思路，所见即所得）：线性/对齐 第二界线原点阶段拉白色虚线，尺寸线位置阶段整条标注（界线/尺寸线/箭头/数字）随光标滑；连续标注沿用上条偏移；半径/直径按光标方向；角度 顶点+第一边后整段弧随光标；坐标标注十字+引线跟着光标。文字走 `TessellatePick`（真字体下线通道为空，预览通道只有线）。光标浮标接实时读数 `DimJigHint`（距离/ΔX/ΔY/R/Ø/角度/坐标）。触发挂在光标移动（Gizmo 悬停那行之后），命令结束/Esc 后下一次移动把预览擦净（`_dimJigShown`）。
- 设置面板 [DimStyleWindow.cs](src/Views/DimStyleWindow.cs)（代码建窗，页脚 Dock 到底 + 内容 ScrollViewer）：「文字」字高(0=自动)/小数位/文字偏移，「直线和箭头」箭头长/半宽/端刻度/界线偏移/界线延伸；右侧样例（对齐标注 100 单位）随每格输入实时重画；逐格校验（`DimStyleStore.TrySet`，越界/非数值报错不写）；恢复默认/确定/取消。无参「标注样式」命令与功能区键打开（带参命令行直设仍可用，也落盘）。`DimStyleStore` 存到用户目录 `dimstyle.json`（与 crash.log 同目录，PITMINE_DATA_DIR 生效时跟着走），`_dimStyle` 字段初始化时读回。自检下非模态打开好截图。
- 与原版关系：原版 `DimensionStyleWindow` 是**逐条标注**的特性编辑器（§三二七 已落到特性面板），本面板是**样式级**（AutoCAD DIMSTYLE 的 DIM 变量），两者互补。

**验证**：+4 单测 [DimStyleStoreTests](tests/PitMine3D.Kylin.Tests/DimStyleStoreTests.cs)（克隆/复制全字段、校验写入与越界拒绝、JSON 往返与坏值兜底、描述）。实机自检（合成指针）：`对齐标注` 点 (20,60) 后 `@光标 70 100` 预览 5 段（虚线）→ 点 (120,60) 后整条标注随光标 815 段（含数字轮廓，截图见界线从测点起、随光标 y 走）；`角度标注` 顶点+第一边后 1135 段；`坐标标注` 981 段；Esc 后均 0 段。`标注样式` 面板截图：两组八格 + 预览样例 + 三键齐全。

**注意**：HEAD 自 574f3ae(11:42)/2a734e1(11:55) 起本身编不过（PointThin.cs 引用 PackedKeyComparer、MainWindow.PointCloud.cs/RoadCenterline.cs 引用 MineableRegions/RegionRecord/Layer.LineWeight/DxfImportService.AciToRgb 等工作树里未提交的类型，共 30 处），不是本节改动引起：HEAD 工作树 build 不含/含本节文件均为同样 30 错，本节文件 0 错。待相关会话把那几份补提交。

### §三九〇 坡顶底线提取「效果还是不一样」：算法对拍无差，差在喂的数据 —— 回源文件全量 + 1m 格网（2026-09-13, commit fca4517）

- **对拍方法**：原版 `测试实验/build_fullsite.bat`(VS2022 cl 直接编 LasLib 源) 重编 `test_fullsite.exe`，在 `dlt_test.las`(=DLT20251222.las, 398 万点) 上跑 native 默认参数 → crest 1164 条/270km、toe 1076 条/241km，写 dump_crest/dump_toe.txt。scratch 里只链 `SlopeLineExtractor.cs + LasImportService.cs` 的控制台跑托管版（主工程当时被别的会话 Road 重构半途编不过）：全量@1m → 1188/274km、1112/246km，**顶点到 native 折线距离 p50 0.4~0.6m、92~94% <3m** → 移植正确。
- **真正的差异**：① 场景点云是加载时封顶 200 万的均匀抽样，1m 格网下点距 ~4m、覆盖掩膜全是洞 → 只剩 296 条/11km；② 默认格网 `PcAutoCell`=7.6m，平滑核(DEM σ4 格/坡度 σ3.5 格)和覆盖闭运算(2m)全按"格=米"标定，台阶被抹平 → 145 条且 1% 顶点贴近 native。
- **修法**：`PointCloudEntity.SourceTotalPoints`(LAS 头点数，加载写、Clone/撤销快照带)；提取时源是 LAS 且被抽样 → 后台 `LasImportService.Load(path, int.MaxValue)` 全量算(只读点不进场景)，信息栏写「源文件全量 N 点（场景中为 M 点抽样）」；DEM 网格默认 1.0 同原版。
- **实机**：200 万点场景 → 输入 3,979,969 点，DEM 6415×4516@1m，坡顶 1188/坡底 1112 条，3.6s；截图线沿台阶密排、与 native 一致。
- **教训**：算子"跟原版不一样"先做同输入 A/B（原版 测试实验/ 里多半有 cpp 测试程序 + dump），再查命令端喂的数据与默认参数；Kylin 点云是抽样，所有按密度标定的算子都得单独处理。

## §三九一 「道路运输系统」页签整体改回原版算法：RoadLib 逐文件移植 + 会话路网 + 存档 + 19 钮重接线 (2026-09-13)

用户反馈「现在道路运输系统中使用的算法和 pitmine3d 中不一致」。逐钮核对原 `RoadLibPlugin`（4 组 / 19 钮）：Kylin 这一页签的按钮大多接的是早年的"切片"——`RoadNetwork.BuildNoded`（自造的平面无向图）+ `RoadTools`/CSV 命令，与原版走的 `RoadGraph`（三维 noding、立交判据、桥接）/ `HaulSolveKernel`（往返两条腿）/ 会话路网 / `road_network` 存档完全是两套东西：**基础道路网络构建·路网预览·路网更新** 三钮同一个处理器且只画黄点；**点对点寻径** 是场景多段线上的裸 Dijkstra、无口径无往返；**等效运距·运输指标报表** 同一个 CSV 处理器；**时段快照·路网存档** 都是导出 CSV；**分色显示** 竟接的是 TIN 高程着色；**中心线管理·边状态** 是一行拓扑统计；**手动标定线路** = 普通多段线工具；**路况显示** 接的是道路台账。原版的 RoadGraph/PathSolver/RoadTopology/RoadEditSession 等在 Kylin 有旧的部分移植（`Cad/RoadPathSolver.cs` 等），但与原版差 100~400 行（缺 NearestEdgeForPick / BuildComponentMap / MaxAbsSegGradePct / SourceRef / RoadGraphSerializer / HaulCaliper / HaulSolveKernel / RoadSnapshot / RoadConditionSymbology / CenterlineInventory / HaulRoadImporter …）。

**做法：整块重移植，不再修补旧切片**
- [src/Cad/Road/](src/Cad/Road/)（31 文件 / 6802 行，命名空间 `PitMine3D.Kylin.Cad.Road`）：原 `Modules/RoadLib` 的 Network / Routing / Editing / Evolution / Render / Transport 六目录 **逐文件逐行拷贝**（仅命名空间与 4 处依赖适配：`IUserSettings`→`UserSettings`、`PitMine.Platform.Transport` 的 `HaulRoadCenterlineSet`/`LoadUnloadPointSet` 两个 DTO 一并拷入；`CrusherStore`/`SinkCandidateStore` 因绑 GeoDataBase 服务改写到 Data 层）。旧的 `Cad/RoadPathSolver.cs`、`RoadGraphBuilder.cs`、`RoadTopology.cs` 等原样保留给非页签的 CSV 命令与既有单测，页签一律走新命名空间（主窗里用 `using X = Cad.Road.X` 别名消歧）。
- [Data/RoadNetworkStore.cs](src/Data/RoadNetworkStore.cs)：`road_network`（V019）/ `road_centerline_set`（V045）/ `load_unload_point` 整表 + 候选卸点入账（原 SinkCandidateStore 三条口径：只增不删、主键不重排、挂接记 note）/ `dump_site` 活跃档案，字面量 SQL 三方言通用（同 MineableRegions 写法）。
- [MainWindow.RoadTransport.cs](src/Views/MainWindow.RoadTransport.cs)：会话状态 `RoadSession`（会话图 / 时段快照 / 演化结果 / 当前存档名 / 编辑会话 / 桥接序号）**挂在 DocState 上（ConditionalWeakTable），每文档一套**；`TryBuildRoadGraph`（道路图层 ∪ 选集 ∪ 坑线落地共享中线 → RoadGraphBuilder 10m 吸附 → StampHaulRoads → Validate 回显，缓存为会话图）；`RoadCaliper` 从 `transport.constraints` 读口径；视口取点（一次性拾取 + 地形面 Z）；overlay 写入（PolylineEntity 带 Zs 替代原 PmbiWriter；线宽 mm→DXF 0.01mm 封顶 211）；装卸点接入 `RoadAttachLoadUnload`（投影打断 + 接入支线）；寻径高亮（廊带 + 芯线 + 箭头 + 起终点 + 抬高 3m）/ 断口高亮 / 取点未吸附黄叉。
- [MainWindow.RoadTransport.Network.cs](src/Views/MainWindow.RoadTransport.Network.cs)：基础道路网络构建（BuildNetworkDialog → 建图 → 拓扑分类回显 → 存档同名覆盖）；路网预览（RoadTopology 路段三色 + 路口白环 + 悬挂红环 + 源汇图元，再点关闭）；路网更新（吃演化结果 RE12 只并本期侧 → 装卸点保活 → 统一提交闸门 UpdateNetworkDialog：当前更新/增量新命名 → 旧网转快照留底）；增量增删边 / 边状态（RoadEditSession 暂存式：加边/删边/插交叉口/改状态/线路类型 → diff 预览层 → 更新路网锁定）；时段快照（SnapshotManagerWindow）；路网存档（RoadNetworkArchiveWindow 载入即设会话图 + 入快照 + 重画预览）；演化对比（EvolutionCompareWindow，阈值取「延拓触发设置」）；分色显示（EvolutionDisplayWindow 五类 overlay）；破碎站位置设置保存后的第②③步（共享设置按全表重建 + 会话图按全表重灌源汇 + 视口旗标）挂在 `CrusherStationWindow.Saved` 上。
- [MainWindow.RoadTransport.Pathfind.cs](src/Views/MainWindow.RoadTransport.Pathfind.cs)：点对点寻径（PathSearchModeWindow：①按装卸点 ②视口取两点 → 克隆会话图 → `NearestEdgeForPick` 投影打断插临时点 → `HaulSolveKernel.Solve` 往返 → TransportResultWindow 明细 + 双色高亮；物理不连通时 `RoadConnectivity.PlanBridge` 算缺口链上图，全平接可一键补边重解）；等效运距（EquivHaulWindow 一源多汇比选、视口加候选卸点、候选存台账、跳点对点）；运输指标报表（TransportIndicatorsWindow 四页：OD 矩阵 / 运距指标 / 运能与瓶颈 / 分期趋势 ChartView 双轴）。
- [MainWindow.RoadTransport.Centerline.cs](src/Views/MainWindow.RoadTransport.Centerline.cs)：手动标定线路（选中线接入 / 视口逐点画线，**就近捕捉交点**：CenterlineJunctions 按建网同容差算出可捕捉点标进预览几何、半径按 24px 折世界米、吸过之后另画琥珀走向；收线后按 TIN/台阶线铺贴 + RoadNetworkConnector 焊接/T 形打断 + CenterlineLayerDiff 增量写回 → 重建会话图）；中心线管理（CenterlineManagerWindow：清单排序 / 碎线·悬空·孤立一键挑 / 视口点选追加 / 批量选择过滤器（挂在 HighlightSelection 钩子上）/ 批量删重建 / 存档页 V045 存·载·追加·改名·删）；路况显示（RoadConditionSymbology 逐段纵坡 3/6/10% 分档、检修/封闭整条压色、白向标、里程占比图例回显）；结构路面（24m ribbon overlay 开/关）。
- [Views/Road/](src/Views/Road/)：13 个窗体照原 WPF 代码 1:1 换成 Avalonia（RoadUi 小工厂统一配色走 Theme.* 资源）；BuildNetworkDialog/UpdateNetworkDialog 自检直通默认值（同 PcForm）。
- 接线：MainWindow.axaml.cs 的 13 条分派改指新处理器（`分色显示` 从高程着色里拆出来；`纪元快照`/`路网导出`/`路网拓扑`/`路面带` 等 Kylin 自造别名留给旧切片）+ `HighlightSelection` 里加一行 `RoadSelectionHook()`。

**验证**：原 `Tests/Tests.RoadLib` 里不依赖真实数据文件/DB 服务的 13 个测试文件（CenterlineInventory / Junction / LayerDiff / Pick / SetCodec / HaulCaliperKernel / PathSolver / PointToPointRouting / RoadConditionSymbology / RoadNetwork / RoadTopology / StructurePavement / TransportIndicators）原样拷进 [tests/Road/](tests/PitMine3D.Kylin.Tests/Road/)，**311/311 通过**（核心逐行等价的直接证据）；全套 4081 通过。实机 `@导入 1.dxf → 提取道路中心线 → 基础道路网络构建 → 路况显示 → 结构路面 → 运输指标报表 → 中心线管理 → 时段快照 → 增量增删边 → 路网预览` 一路无异常：4929 条中线 → 路网 3406 节点 / 3440 边 / 连通片 61 → 路况最大纵坡 30.3% → 3440 条路面带 → 平均等效运距 859m → 路网预览「路段 1596（干线 845 / 支线 742 / 孤立段 9）· 路口 802 · 悬挂端点 760 · 接缝 1844」。截图核对这次没做成：用户正在前台用 Word/浏览器，SetForegroundWindow 拉不到前面，两次都截到别的窗口，不再打扰。
- **记录**：`增量增删边`/`破碎站位置设置`/`延拓触发设置` 三条分派与 `CrusherStationWindow.Saved` 挂钩只在工作树里（HEAD 里这三条分派本就不存在，是另一会话未提交的部分），随对方提交带上；旧的 `RoadEditCmd`/`BuildRoadNetworkCmd`/`SnapshotEpochAsync` 等切片代码留在 MainWindow.axaml.cs 里未删（共享脏文件，不动大块）。

## §三九二 对象管理器「分类方式和 PitMine3D 不同」：树的数据早已同源, 差在容器观感 + 文档名 —— 行容器照原版 TreeListBox 重做 (2026-09-13)

- **核对（直接读原版源码, 未再开原程序）**：原版 `FileTreeViewModel` = 三根 `CAD 对象 → [文件名|视图N] → 图层`(线类只到图层级) / `面模型 → 图层 → Mesh` / `块体模型 → 模型`，Kylin `ObjectTreeViewModel` 逐段等价、右键/双击/☑ 也同路。差的是**呈现**：
  1. 面板内多了一行「对象管理器」标题（原版 `LayoutAnchorable` 里就是一棵 `pt:TreeListBox` 顶到边，页签即标题）；
  2. 行容器用的 Fluent 默认 `TreeViewItem`：32px 行高、16px/级缩进、12px 人字钮 + 左右各 12px 留白 → 整棵树松散、层级看着比原版深；原版 `TreeListBox` 是 10px/级 + 16px 小三角(叶子隐藏但留位) + ≈22px 行；
  3. 未保存文档名 Kylin 叫「未命名 N」、原版叫「视图N」(`CreateLayoutDocument($"视图{_viewCounter++}")`)，文件节点跟着显示不一样。
- **做法**：新增 [Styles/TreeListBox.axaml](src/Styles/TreeListBox.axaml) —— `PitTreeListBoxItem` ControlTheme(BasedOn Fluent 的 TreeViewItem, 只换模板：`MarginMultiplierConverter Indent=10`、固定 16px 钮位 Panel + `PitTreeListBoxToggle`(WPF Aero 的 ▷/◢ 小三角)、MinHeight 22、Padding 2,1；部件名与 Fluent 一致, 选中/悬停/`:empty` 隐藏三角等基础样式经 BasedOn 继承, 代码里 `HeaderPresenter` 照常)；[MainWindow.axaml](src/Views/MainWindow.axaml) 文件管理器/对象管理器两棵树都改用它、去掉面板内标题；[MainWindow.axaml.cs](src/Views/MainWindow.axaml.cs) 文档标签「未命名 N」→「视图N」(绑定文件后仍改成文件名)。
- **验证**：`ObjectTreeViewModelTests`/`FileSystem*` 16 通过；实机自检 `@对象管理器;@稍后 1500 @控件截图 ObjectContent` 空文档 = `◢ ☑ 🏗 CAD 对象 / ◢ 📘 视图1 / ☑ 🎨 图层: 0`，`@露头示例;@线示例;@块体示例 3 3 2;@对象管理器` = 三根 CAD 对象→视图1→图层: 自检剖面线 / 面模型→图层: 0→现状面·2煤顶板… / 块体模型，与原版截图同构同观感。
- **未动**：三角网叶子仍显示名字(原版显示 `Mesh #handle`，Kylin 实体无 handle, 名字才认得出建模/剖面窗口里的哪一张)；勾选框 0.7 缩放、emoji 10px 是用户此前拍板的观感，保留。

## §三九三 「实体转块体」转换结果不正确 —— 块体生成改回原版八叉树叶块路径 + 默认块尺寸回 5 + 窗体布局 (2026-09-13)

用户报「实体转块体的功能现在存在问题，转换结果不正确」。用合成的倾斜煤层闭合体(顶/底板放样成体, 15 m 厚, 平朔量级坐标)
实机复现：转出来的是一摞断断续续的"台阶碎块", 整列整列地缺, 体积比散度定理精确值少 **23%**。逐项对照原
`EntityToBlocksDialog` / `VoxelVolumeDialog` 找到三处失真：

1. **块体生成走错了路**。原版两个对话框的「生成块体」都是 **均匀占位（逐 cell 中心在体内）→ `OctreeLeafBuilder.BuildFromOccupancy`
   压成八叉树叶块**（"实心内部并大块、边界细到最细格"），`adaptive: null`；XAML 里的「次级退化 / 深度」在这条路上根本没接。
   Kylin 把它接成了 `VoxelVolumeBuilder` 的自适应百分比子块并默认开着(深度 2)。自适应那套按「母块中心与 6 邻居异号」判边界，
   煤层这种**比块还薄**的体在母块之间"两头中心都在外"时整列被判成非边界直接丢掉 —— 这是原版体积报表路径本身的特性
   (报表那边只关心算量, 原版也没把它用于出块)。补移植 `Cad/OctreeLeafBuilder.cs`(逐字: pow2 根递归八分, 全实心上交并块,
   混合节点就地发射, 占据 AABB 剪枝) + `BlockVoxelBuilder.ToLeaves / ToBlockModel(Result, leaves, name)`，两窗「生成」改为
   `Build(depth:0)` → 叶块 → 块体模型(Size = 边长·Sx, 粗叶块计入 SubCellCount, 与 .blk 导入同约定)。体素格网体积的报表仍用自适应结果。
2. **默认块尺寸**。原版固定默认 5；Kylin 按并集包围盒对角线/40 自动填(800×600 的煤层填成 25 m, 比层厚还大), 均匀路径也会一半的列
   罩不住中心。去掉自动填, 回到 5。
3. **转完源网格"藏而不消"**。隐藏源网格后它还在选择集里, 高亮层不认可见性, 整张网仍以青色高亮面盖在块体外头 —— 两个立方体的
   自检里看到的就是一坨青色。隐藏时顺手清选择集(`ctx.Select(空)`)。

**窗体布局**(用户第二条)：定高 450 在 Avalonia 行高下留一大截空白, 改 `SizeToContent="Height"` + 页脚 DockPanel 到底；
「生成块体 / 关闭 / 重新选择对象」补 `HorizontalContentAlignment="Center"`(本工程按钮默认左对齐)；
「深度」下拉框 Width=60 **小于 Fluent ComboBox 模板里 Border 的 MinWidth 64**, 模板被居中裁掉左右各 2 px → 只剩上下两道横线, 改 72。
体素格网体积窗同步(页脚按钮居中、深度下拉 72)。`ImportBlockModelWindow.axaml` 的 csvSep 也是 60, 该文件另一会话在改, 未动。

**并行会话依赖**：粗叶块(Size > Sx)的六面体尺寸/体积/包围盒靠工作树里 `BlockModelMeta.CellScale`(.blk 导入那条线的未提交改动)
按三轴细格缩放；HEAD 版 `IsSubCell` 只认"比 Sx 小"的子块。本节提交只含本次触及的 7 个文件, 不碰 BlockModelMeta。

验证：`OctreeLeafBuilderTests` 8 条(pow2 全实心→单根叶 / 空 / 非 pow2 域铺满不出域且并块 / 棋盘全最细 / 两立方并集铺满 /
盒→单叶块 Size=边长·格 / 薄煤层叶块体积=占位 cell 数且 ±3% 内、每叶中心 GWN 复核在体内 / 自适应 25 m 少两成 vs 均匀无洞)，
全套 4089 全绿。实机：`@导入 seam.off;实体转块体` 默认 5 m → 58,513 cell → 26,278 叶块, 体积 7,314,125 vs 精确 7,308,467 (+0.08%)，
截图为连续完整的倾斜层；`@示例两体` → 并集, 内部并成 8 格大块、边界最细格, 同原版观感。

## §三九五 「剥采排工程辅助设计」页签 30 钮逐钮忠实性复核 + 22 处按原版重做 (2026-09-13)

**起因**：用户要求统计该页签功能并逐个评价是否按原 PitMine3D 移植。逐钮对照原 `MineAssLibPlugin.cs`（3 组 30 钮）后发现进度文档的"未接线"统计
数不到一类问题——**`cmd ==` 命中了但跑的是别的功能**（7 钮），另有 7 钮是浅切片（有名无实）、2 钮原版没有（超出）。10 钮忠实、5 钮托管等价。
随后 `/loop` 9 轮把剩余项逐个补齐（commit f0206c4 → fb7d24f，共 11 笔；全套 4194 测试通过；实机启动 GL 初始化正常）。

### 接线错（按钮点下去跑的是别的功能）→ 各回原版语义
| 钮 | 此前 | 现在 |
|---|---|---|
| 参数化模板 | 库 param_template 统计一行 | `OpenBenchTemplateEditor`（原 MiningTemplateEditorWindow） |
| 驱动距离 | 等效运距 CSV | `CuttingCmd(cmd, "驱动距离")`（同原版共用 DriveTemplateRunner，加 label 参数） |
| 动态调整 | TaskLib 生产任务动态调整窗 | `MainWindow.BenchJig.cs`：选境界 → D/U → 光标离线距离折级数、逐级坡脚环实时预览 → 点击/回车 BenchBuilder 落地（托管等价原内核 jig） |
| 分帮扩帮 / 最终并段 | 批量平行偏移 / 兜底"未移植子系统" | 原版即 `SkeletonCommand` 桩，照回显「[骨架] …（待实现）」 |
| 批量扩坑 | 批量平行偏移 | `Cad/SeamPitBuilder.cs`：顶板以上岩台阶 / 顶底板间煤台阶 / 底板以下不出，倾斜顶板同级切岩段+煤段并落尖灭点（托管等价内核 BuildSeamPitMultiSeam）+4 测 |
| 组合工作线 / 连接台阶线 | 都走通用 POLYJOIN | `Cad/WorkLineGroup.cs`：组语义（最近端点串链、缺口软连接段虚线+箭头、成员不合并）；`BenchLineJoin` + 原 JoinBenchLineDialog 四项、继承图层/颜色/标高 +4 测 |

### 浅切片 → 按原版对话框 + 管线重做
| 钮 | 现在 |
|---|---|
| 批量台阶扩帮 | `MainWindow.ExpandBench.cs`：原 ExpandBenchBatchDialog 分区（采场/排土场·Toe/Crest·上/下·侧向·H/α/W 套模板·工作帮/最终帮平盘·止点 段数/标高/地表）→ BenchBuilder；产物缓存供直线坑线 |
| 画道路中线 | `MainWindow.DrawRoadCenterline.cs`：快照选中面 → 拾点吸面 → 「道路参数」①②区 → 统一落地管线 → 登记路网 → 可撤 |
| 螺旋坑线 / 折返坑线 / 平盘联络道 | `MainWindow.RampInsert.cs`：原 InsertRamp*Dialog 参数框 + 一次落地；折返起点坡面点取、甩向自动（移植 `AutoTurnSide`，判不出不许猜） |
| 运量驱动布线 | 原 `RoadLayout` 家族逐文件移植（`Cad/RoadLayout/`：选线器 764/求解器 529/规划器/自动布线器 824，原 27 测通过）+ `RoadSchemeCompareWindow` 比选 + `MainWindow.RoadLayoutDriven.cs` 采用出真中线 → 落地缓存 |
| 直线坑线 | `MainWindow.StraightRoute.cs`：原 StraightRouteParamsDialog 各项 + 改用原布线器（折返/螺旋兜底、线形/纵断面/横断面后处理逐级回显） |
| 排土条带 | 原 `DumpStripPlanner`(2426)/`StandardLevelModel`(795)/`WorkSlopeRange`/竖直断面轨 逐文件移植（原 52+13 测通过）；`DumpStripWindow` 识别台阶/生成位置/导出；壳子体托管放样 `DumpStripShell`（散度定理实测体积）；`DumpStripRepo` 落库 dump_strip 先清后写 |
| 排土场按量推进 | 原 `DumpAdvanceByVolume` 移植 + `DumpAdvanceWindow`（实方/占容方·Kr·并肩/逐级·起始已填）+ 图上形态 |
| 排土场容量校核 | 图上选面 → `DumpCapacityCalc` 同口径填挖方 → 与 dump_site 台账对账/写回 +2 测 |
| 处理尖灭 | `MainWindow.HandlePinch.cs`：手动（尖灭点+坡顶线，上部联动）/ 煤层（锁台阶组+顶板+底板），组内坡面重算 |

### 超出原版 → 撤
「竖曲线平滑」「线形处理」两钮从 Ribbon 撤掉（原版只是落地管线内部阶段①③），命令行别名保留。

### 登记的差异（未做/待接）
排土条带的两种走现状面的取线来源与「套设计台账」归级（`StandardLevelSource.FromDatabase` 依赖原库服务）；直线坑线第三档「现状面自动提坡面」；
批量台阶扩帮的逐级剖面表/留运输平台/随线起伏/到煤层底板；组合工作线的组是会话内记录（原版持久实体）；编辑台阶仍是离线重算（原版内核实时夹点联动）。

### 提交方式
工作树里另一会话有 ~1.4 万行未提交改动（含 226 个未跟踪文件），本轮全部走「HEAD 副本 + 临时索引只交自己 hunk」；依赖另一会话代码的派发行
（刀量切割 label / 处理尖灭 / 平盘联络道参数框）随其提交落 HEAD。旧切片一律留命令行别名（条带填充 / 两期容量校核 / 坑线连通自检 / 运输布局方案 带参 / 螺旋中线 / 折返中线）。

## §三九六 「生产计划编制」页签逐钮忠实性复核 + 优化开采设计①②「境界圈定 / 确定境界」按原 PlanLib 整窗重做 (2026-09-13)

**起因**：用户要求「生产进度计划编制部分的功能与 PitMine3D 完全相同」。逐钮对照原 `PlanLibPlugin.cs`（3 组：优化开采设计 6 钮 / 中长远进度计划编制 7 钮 /
短期生产计划编制 4 大 + 6 中 + 标注台阶标高 SplitButton 3 子项）后的现状（Kylin `MainWindow.axaml.cs` 的 `cmd ==` 命中）：

| 钮 | 原版 | Kylin 此前 | 判定 |
|---|---|---|---|
| 境界圈定 | `PitSchemeConfigWindow`（方案配置六区） | `BoundaryHullAsync` 选 CSV 点集算凸包 | **接线错** → 本节重做 |
| 确定境界 | `PitOptimizeWindow`（求解·对比矩阵·确定落地） | `PitDepthCmd` 状态栏一行经济坑深 | **浅切片** → 本节重做 |
| 采区划分 / 开采程序确定 | `MiningProgramConfigWindow` / `MiningProgramSolveWindow` | `PanelSplitCmd` 矩形入图 / `AdvanceCmd(Parallel)` 平行推进 | 浅切片 / **接线错** |
| 刀量切割 / 剥采比均衡 | `DriveTemplateRunner` / `VpCurveWindow` | §三七〇 已做 / `StrippingBalanceAsync` | ✓ / 待核 VpCurveWindow |
| 中长远进度计划编制 / 规划计算 | `LongTermConfigWindow` / `LongTermSolveWindow` | `LongTermPlanCmd` 命令行参数 / `ProgramEvaluateCmd` 开采程序评价 | 浅切片 / **接线错** |
| 采场/排土场圈定 | `ShortTermMineableAreaWindow` | 同「境界圈定」凸包 | **接线错** |
| 派生计划方案（中长远 / 短期同名两钮） | `LongTermDeriveWindow` / `ShortTermDeriveWindow` | 同一 `DerivePlansCmd` | 两钮串一 |
| 方案综合对比 / 进度计划方案出图 / 两个动态模拟 | `LongTermCompareWindow` / `LongTermChartWindow` / Sim 窗 | `ProgramCompareAsync` / §三四五 / §三四七·三四八 | 待核 / ✓ / ✓ |
| 短期生产计划编制 / 月度计划编制 | `ShortTermConfigWindow` / `ShortTermSolveWindow` | 同一 `ShortTermPlanCmd` | 两钮串一、浅切片 |
| 量驱动采剥接续 / 采掘单元清单 | `MonthlyStripWindow` / `MiningUnitPlanWindow` | **无处理器（死按钮）** | 缺失 |
| 采场参数识别 / 标注台阶标高 / 确定开采程序 / 采排配对 | `ShortTermFieldWindow` / `BenchElevationWindow`+3 子项 / `ShortTermSequenceWindow` / `DumpPairingWindow` | `BenchWidthAsync` / `BenchElevationAnnotateAsync`(无 SplitButton) / `AdvanceCmd` 平行推进 / §三六一 | 浅 / 缺子项 / **接线错** / ✓ |

原 PlanLib 共 ~30k 行 C# + 6k 行 XAML（BoundaryOptimization 30 文件 / LongTerm 20 / ShortTerm 70 / Views 7），按钮逐个整族移植；本节先落优化开采设计①②。

### 本节落地（境界圈定 / 确定境界，原 BoundaryOptimization 的 pit 半边逐文件移植）
`src/Cad/Plan/`（命名空间 `Cad.Plan`）：`ProductionCostBook`（原 MineAssLib 生产成本口径 MU14，EconParams 缺省全取自此）· `PitScheme`（DepositType/StripRatioPrinciple/
DelineationMethod/EconRatioMethod 四枚举 · EconParams 四式 · WallAngle · PitGeometryRefs/SegmentBeta/SeamSurfaceRef · PitResult · PitScheme.Clone/CreateSamples ·
`BoundarySchemeStore` 会话方案集）· `BlockModelCoal`（原 DepositAutoDetector.TryGuessCoal/TryGetClassifier/DetectAuto 在 `BlockModelMeta` 上的实现：分类列标签含"煤/coal"→码，
列名含 coal/煤→==1；`ResourceProfile` + `PlanSectionSampler.SampleLayers` 逐 Z 层聚合 + 逐层裁剪环 + 灰分体积加权；`PitSectionSolver.SolveDepth` 净值最大定坑底 + 逐层曲线）·
`PitEnvelope`（`PitSchemeEnvelope`：顶口=地表界多段线优先/块体足迹兜底，边 β 段绑定优先/方位映射，收缩=整体+逐段；纯几何复用既有 `Cad.PitEnvelope`）·
`PitSolveRunner`（三段式编排：猜煤→n经校核→几何限深(各帮最紧)→逐 Z 层 β 截锥裁剪→后台采样求解→`PitEvaluator` 全维 PitResult；`PitMaterializer` 自顶向下 crest/toe 环 →
放样三角化台阶面 + 每环一条闭合三维多段线，按方案图层幂等先删后建）· `ComparisonBuilder`（19 指标 × 方向感知 min-max 归一 × 权重 → 综合评分/排名/推荐）· `PlanDb`（slope_design 现行设计）·
`IPlanEntityHost`（原 IEntityCapability/ISelectionCapability 被 PlanLib 用到的那一截：按 handle 取多段线/三角网/AABB、枚举、按层删、落地、视口拾取、高亮、激活块体、库连接）。
`src/Cad/Draw/EntityHandles.cs`：会话内实体 handle（ConditionalWeakTable，方案只按 handle 引用图纸实体，同原 AcDb handle 语义）。

`src/Views/Plan/`：`PitSchemeConfigWindow`（原 XAML 六区照搬：①矿床/原则 + 块体 PCA 自动识别 ②四式实时公式(下标 Run) ③分帮角表 + 从 SlopeDesign 载入/按方位绑定/按境界线段分帮…
④面与界线 handle 引用 + 选面/选线对话框 ⑤底宽按设备/工作面宽/整体收缩/台阶 H·α ⑥走向检测/间距推荐/预览剖面线入图；全部自动重填/保存/新建/克隆/删除）·
`PitOptimizeWindow`（一键圈定/求解选中/求解全部 + 方案列表勾选比选 + 指标×方案矩阵(分组标题行代替 WPF GroupStyle, ▲=最优) + 详情六格 + 两块占位(照原) + ✔确定最终境界落地并标记
已确定）· `SurfaceSelectionDialog`（①视口拾取 ②清单双击/确定）· `WallSegmentDialog`（逐段 方位/长度/帮别/β/额外收缩）· `PlanUi`（teal 标题栏/GroupBox/信息框/标签行工厂）。
`MainWindow.Plan.cs`：`OpenPitSchemeConfig` / `OpenPitOptimize(cmd)`（单例；「确定境界 一键」直通一键圈定）+ `PlanEntityHost` 实现。Ribbon：`境界圈定`→配置窗，`确定境界`→优化窗；
旧切片保留命令行别名 `凸包/采场圈定/点凸包`、`最优坑深/经济境界/经济坑深`。自检加 `@等待 <ms>`（后台求解续体跑完再截图）、`@块体示例` 顺带给「矿岩类型」煤列。

**验证**：`PitSchemeFamilyTests` 16 条（四式 · 成本口径 · 克隆深拷 · 猜煤 · PCA 判型 · 逐层采样体积守恒/灰分加权/逐层裁剪 · 净值最大与几何限深 · 评价泰勒/NPV/单位成本 ·
编排三段失败路径与限深消息 · 顶口解析 · 段绑定/方位/收缩 · 落地环与放样网顶点/三角计数 · 对比矩阵最优/推荐/排名 · 剖面线/间距/底宽推荐）全过。
实机 `PITMINE_SELFTEST=@块体示例 20 6 8;确定境界 一键;@等待 4000;@页面截图;境界圈定;@页面截图`：两窗按原版布局出图，一键圈定对自检块体三方案求解完成（默认帮角 + 60 m 底宽对
120 m 宽足迹几何限深 10 m、煤 0，与原版同口径），对比矩阵/详情回填。
**登记差异**：本机另一会话正在工作树里移植 `src/TaskLib/`（108 个未跟踪文件，尚编不过），本节全部构建/测试在 scratchpad 的镜像副本（排除 TaskLib）里跑；
原窗「导出报表」原版即 TODO，照回显；「境界平面/横剖面预览」「境界剥采比–深度曲线」原版为占位，照保留（结果里已带逐层曲线数据备用）。
**下一步**：③采区划分 / ④开采程序确定（MiningProgramPlan + Config/Solve 两窗 + PanelSplitter/PanelDelineator/ProgramMaterializer/MiningProgramCharts）→ ⑥剥采比均衡 VpCurveWindow 核对 → 中长远组 → 短期组。

## §三九七 优化开采设计③④「采区划分 / 开采程序确定」按原 PlanLib 整族重做 (2026-09-13)

**此前**：「采区划分」= `PanelSplitCmd` 对最近块体按默认参数等煤量切矩形入图（无对话框、无境界来源、无拉沟推进）；「开采程序确定」`cmd==` 命中的是
`AdvanceCmd(Parallel)` 平行推进（**接线错**）。原版是两个窗口：③ `MiningProgramConfigWindow`（八区方案配置）④ `MiningProgramSolveWindow`（一键划分·比选·确定落地）。

### 落地（原 BoundaryOptimization 的 MiningProgram 半边逐文件移植）
`src/Cad/Plan/MiningProgramPlan.cs`：`MiningStrategy / SplitObjective(原序 ByCapacity·ByLife·FixedN) / BoxcutMode` · `FirstPanelWeights` · `BoxcutWeights` · `BoxcutAdvanceOption`（六约束分 + Recompute）·
`MiningPanel` · `ProgramResult` · `MiningProgramPlan`（八区字段 · Q=L·v·H·ρ 正逆算 · Clone · CreateSamples 三样例）· `MiningProgramStore` 会话方案集。AdvanceMode/DumpMode/StripRatioField 复用 Kylin `Cad` 既有定义。
`src/Cad/Plan/MiningProgramSolver.cs`：`PlanStripRatioFieldSampler.Sample(BlockModelMeta)`（原 StripRatioFieldSampler：煤岩判别器 + 逐 XY 列聚 煤/岩体积 + 煤厚 + 埋深，子块按尺寸倍数）·
`ProgramPanelDelineator`（南/北/东/西近边带 + 剥采比梯度候选，六约束真值打分，境界弯直度定工作线形态，`ResolveOutline` 取地表界/底周界/块体足迹）·
`ProgramPanelSplitter`（沿推进轴切 slab，FixedN 等推进距离 / ByLife·ByCapacity 等煤量，采区数自适应钳 1..16，首采外排其余内排 + 起转期）·
`ProgramPlanEvaluator`（服务年限 / 峰值剥采比 / 储量均衡系数 1−CV / 基建剥离 / 内排容量平衡(松散 1.2 只能填已采采空容积) / 达产 / 煤量加权运距 / 分期现金流 NPV / 推进度与工作线校核）·
`ProgramPlanComparer`（七指标方向感知归一加权 → 综合分 + 可行最高分推荐）· `MiningProgramGenerator`（按三种推进方式派生）·
`ProgramMaterializer`（采区环按 Sutherland-Hodgman 裁到境界 / 块体足迹兜底 + 采区名文字(原 vAlign=2 → Kylin 1) + 首采区高亮 + 拉沟线 + 推进箭头，按方案图层幂等）。
`PlanEntityBatch` 加 `Texts`，宿主 `Import` 落 `TextEntity`。

`src/Views/Plan/MiningProgramConfigWindow.cs`：原八区照搬——①产状/策略(块体 PCA) ②境界来源(必选，只列已「确定最终境界」的方案，选定即继承产状/走向/策略，`Activated` 时刷新) ③首采区权重归一
④拉沟·推进(全自动/人工两态；人工拾取拉沟线 + 方位；约束权重归一；「推荐候选」有带煤块体从剥采比场真生成、否则重排样例) ⑤采区数/均衡 ⑥内排 ⑦产能约束(按设备派生 采宽/最小工作线/推进度上限 + Q 校核)
⑧经济；全部自动重填 / 保存(境界来源必选闸) / 新建·克隆·删除。
`src/Views/Plan/MiningProgramSolveWindow.cs` + `MiningProgramCharts.cs`：一键划分/求解选中/求解全部 → 对比矩阵 + 拉沟候选表(采用选中 / 视口布置工作线·推进：两点取开段沟→方位垂直朝场内+工作线长→即时重划 / 工作线形态下拉回写)
+ 四张 Canvas 图(SR(t) 削峰曲线 / 五轴雷达 / 内外排堆叠柱 / 采区接续甘特) + 指标九格 + 采区表 + 平面图占位(照原) + ✔确定开采程序落地 + 导出 CSV 报表(原 BuildReport 四段)。
`MainWindow.Plan.cs`：`OpenMiningProgramConfig` / `OpenMiningProgramSolve(cmd)`（单例；「开采程序确定 一键」直通）。Ribbon：`采区划分`→③窗，`开采程序确定`→④窗；旧切片留别名 `采区/储量均衡划分/采区切分`；
`确定开采程序`(短期组同名钮)暂仍指平行推进，待按原 ShortTermSequenceWindow 重做。

**验证**：`MiningProgramFamilyTests` 12 条（场采样列守恒/埋深/煤厚 · 五候选与可行性 · 形态三档 · FixedN 切分守恒与首采外排 · 自适应采区数 · 评价含内排容量平衡 40% 手算 ·
级2评分推荐 · 派生三态 · 落地环/文字/拉沟箭头 + 三角境界裁矩形 · 视口布置方位四象限 · 报表 · 产能互逆/设备规格）全过；实机自检 `@块体示例 20 6 8;开采程序确定 一键;@等待;@页面截图;采区划分;@页面截图`
两窗按原版布局出图，一键划分对自检块体三方案真切采区、四图表与指标回填。另一会话的 TaskLib 已提交(5887928)，镜像现可整体编译。
**登记差异**：④窗「采区平面图」原版即占位，照保留；对比表/候选表列宽按 1.3 倍放宽（14px 正文下原像素宽压表头）。
**下一步**：⑥ 剥采比均衡 `VpCurveWindow`(719 行) 核对/重做 → 中长远 7 钮 → 短期 10 钮。

## §三九八 优化开采设计⑥「剥采比均衡」按原 VpCurveWindow 整窗重做 (2026-09-13)

**此前**：`StrippingBalanceAsync` 弹文件框读 CSV → 累计曲线/均衡折线画进图纸 + 状态栏一行。原版是 VP 曲线窗口（`PlanLib.StrippingBalance.VpCurveWindow` 719 行 + LiveCharts）。

### 落地
`src/Cad/Plan/VpBalance.cs`：`VpPeriod`（期号/P/V + 派生 剥采比/累计/偏离/阶段）· `VpStage` · `VpBalanceSession`（原 `Recompute` 抽成纯逻辑：累计 → 投产点(年产首达 投产系数×A_p，基建剥离=投产前累计剥离)
→ 达产点(首达 A_p) → 基建/过渡/稳产/减产 标记 → 产出曲线顶点(含锚点 A=(0,基建剥离)) → 复用既有 `VpBalanceSolver` DP 分 K 段 → 逐期偏离 → 平均/各阶段剥采比、投产超前月数、峰值超前、
四项校核[欠剥 / 投产超前<2月 / 超经济比 / 剥采比非递增]；`SampleSeed` 原 11 期模拟物料量；`AddPeriod` 期号顺延；`StageRatioPerPeriod` 阶梯图取数）。
`src/Views/Plan/VpCurveWindow.cs`：原布局照搬——工具条（增加期/删除选中/重置(模拟) · 设计生产能力/投产系数/经济合理剥采比/均衡期数 · 从计划提取 · 状态）；左 440：逐期可编辑表 + 分阶段均衡结果表；
中：VP 主图（坐标系原点轴线 + 主/次网格 + 刻度 · 实际累计曲线含 x=0 竖直基建段 · 阶段分带 + 阶段标签 · 彩色分段均衡折线 · 基建剥离/投产点/达产点标注 · 滚轮缩放/左键拖动平移/双击或「复位视图」复位）；
右：① 剥采比/生产时相阶梯(柱=逐年比，折线=所属阶段均衡比，底带=时相) ② 年度物料量双轴双柱 ③ 超前剥离柱(正紫负红)；底部六指标(均衡期数/投产年·基建剥离·超前月/达产年/平均÷各阶段/峰值超前/校核)。
原版 LiveCharts 这里用 Canvas 手绘（Kylin 无该库，与 §三四五 出图窗同一取舍）。「从计划提取」= 宿主 `ExtractPlanPeriodsForVp`（原优先级 短期确定→短期已排→中长远确定→中长远已排；短期方案库待短期组移植后接入，
现只接 `_longTermSchemes`）；没有可提取方案时弹框 + 状态栏明确提示，不静默。Ribbon：`剥采比均衡/VP曲线`→窗；旧切片留 `剥采比/剥采比均衡CSV/VP曲线入图`。
**验证**：`VpBalanceSessionTests` 5 条（模拟数据投产 2025/基建 4800/达产 2027/时相序列/总量 · 分段在曲线上方偏离非负、阶段递增、校核通过、阶梯取数 · 指定期数与经济比报警 · 无设计能力退化 · 增加期/全基建）全过；
实机自检 `剥采比均衡;@等待;@页面截图` 出图：三阶段(4.13/5.50/6.82)、投产 2025·基建 4800·超前 16 月、达产 2027、峰值超前 2600、校核通过，与原版同一数据同一结论。
**下一步**：中长远 7 钮（LongTermPlan/Config/Solve/Derive/Compare/Chart + 采场/排土场圈定）。

## §三九九 中长远进度计划编制组 5 钮按原 PlanLib.LongTerm 整族重做 (2026-09-13)

**症状**：中长远组 5 钮全是旧切片——「中长远进度计划编制」命中命令行一次排产 `LongTermPlanCmd`（写死参数、无窗）、「规划计算」命中 `ProgramEvaluateCmd`（开采程序评价，不是排产）、「派生计划方案」命中 `DerivePlansCmd`（按倍率造方案，工作线是写死的）、「方案综合对比」命中 `ProgramCompareAsync`（开采程序对比）、「进度计划方案出图」是六页签窗但读的是命令行排出的扁平方案。原版：五个独立窗 + 会话方案库，量只来自块体（BM1），工作线只认图上实体（LT4）。

**做法**（原 `Modules/PlanLib/LongTerm` 14 文件 → Kylin，逐文件对齐）：
- 引擎/模型 `src/Cad/Plan/LongTermPlan.cs`（RampProfileKind/PlanPhase/RampPreset/DecisionWeights/WorkLineAdvanceVariant/PlanPeriod(20 列含均衡段·排土侧)/LongTermResult/LongTermPlan/LongTermBase/LongTermSchemeStore：库初始为空 LT3、`SpecifiedWorkLines` 按源实体去重、`NeedWorkLineHint`、`AutoInheritIfNeeded` 自动续源）+ `LongTermEngine.cs`（`LongTermBlockSource.Build` = `BlockModelCells.Build` + `TemplateDrivingEngine.BuildAdvanceProfile` 沿工作线分箱累计曲线；`WorkLinePicker` 从选中实体取 L/方位/平行·扇形/回转中心、按 handle 刷新（LT5）；`LongTermDumpBridge` 读 dump_strip 台账做内排率几何反算/排满年/排不下（LT6，无库空池不抛）；`LongTermScheduler.Schedule` 划期→基建→爬坡→VP 均衡（`VpBalanceSolver`）→削峰→现金流/NPV→`Evaluate`，`ComposeFrom` 一键比选，`Generate` 工作线×能力档×节奏笛卡尔积，`ToLegacy` 转旧扁平模型喂动态模拟；`LongTermComparer.Score` 方向感知加权）。`IPlanEntityHost` 新增 `SelectedWorkLine/WorkLineByHandle`（宿主用 `WorkLineSamplesOf`）。
- 窗口 `src/Views/Plan/LongTermConfigWindow.cs`（八组基础约束 + 继承开采程序 + 储量÷能力反算 + 试算）/ `LongTermDeriveWindow.cs`（拾取选中工作线/按实体刷新/移除/清空 + 能力档·节奏勾选 + 生成多套方案 + 加载块体模型）/ `LongTermSolveWindow.cs`（⚡一键排产比选/排产全部/排产选中 + 逐年进度图 + 九指标 + 逐年进度表 + ✔确定进度计划 + 导出报表 `BuildReport`）/ `LongTermCompareWindow.cs`（联合对比评分：缺排产先补排 + 对比矩阵冻结首列横滚 + 四维图 + 雷达 + 推荐 + 排名）/ `LongTermChartWindow.cs`（**替换**旧六页签窗：方案下拉 + ⚡一键排产并出图 `EnsureRecommended`(LT4 拦住) + 单张进度图 + 导出 PNG(RenderTargetBitmap)/CSV）/ `LongTermBlockPickerWindow.cs`（列块体·标有无煤属性·设为活动·导入直通建模「导入块体」）/ `LongTermCharts.cs`（产量+达产线/SR(t)/NPV(t)/时相甘特/雷达/进度图，Canvas 手绘）。
- 接线 `MainWindow.Plan.cs`：`OpenLongTermConfig/Derive/Solve(cmd)/Compare/Chart` 单例；Ribbon `中长远进度计划编制`→配置窗、`规划计算`(+「规划计算 一键」直通)→排产窗、`派生计划方案`→派生窗、`方案综合对比`→对比窗、`进度计划方案出图`→出图窗；旧切片留命令行别名（`中长远进度计划 [args]`/`开采程序评价`/`派生方案`/`方案比选`）。「剥采比均衡」的从计划提取改为先读新库（已确定→已排产首套）。动态模拟改读 `LongTermPlansForSim`（新库 `ToLegacy` ∪ 旧命令行方案）。
- 表头：`PlanUi.FitHeaders/Table` 按 14px 正文估最小列宽撑开（XAML 定宽在 Kylin 逐个截字，见 [[datagrid-star-column-fit]]），`PlanUi.Header` 副标题改 DockPanel 可换行。
- 自检 `@中长远示例`：自检块体 + 西缘工作线选中 + A_p 压到 15 → 派生窗拾取+生成；`派生窗.SelftestPickAndGenerate`。

**验证**：`LongTermFamilyTests` 11 条（拾取方位 90°/扇形回转/零矢量拒绝 · 累计曲线总量守恒 48.6 万t/60 万m³ 单调 · 排产：基建 1 年→爬坡 35%/68%→达产 2029→末期减产、累计守恒、内排起转年、NPV=折现和 · 无块体/无煤拦住 · 派生 2×2×2=8 套命名 · 联合评分 · 一键比选 · 方案库空/去重/续源 · 排土桥空池 · 转旧口径 · 报表）+ 既有规划家族 444 条全过。实机 `@块体示例 20 6 8;@中长远示例;规划计算 一键;方案综合对比;进度计划方案出图` 五窗截图：派生拾到「工作线1·L=120m·90°·平行推进·#3E9」排出 1 套；规划计算一键比选 → 推荐工作线1（服务年限 10a·达产 2030·峰值剥采比 2.2·内排率 96%·NPV 14,953 万·校核通过）逐年表 11 行；对比窗四图+雷达+排名；出图窗单张进度图。

**下一步**：「采场/排土场圈定」（原 ShortTermMineableAreaWindow + MineableAreaIdentifier/LandformClassifier/RegionGeometry）→ 短期生产计划编制组 10 钮 + 标注台阶标高 3 子项。

## §四〇〇 中长远组「采场/排土场圈定」按原 ShortTermMineableAreaWindow 整窗重做 + 宿主选区笔刷 (2026-09-14)

**症状**：Ribbon「采场/排土场圈定」命中的是旧切片 `BoundaryHullAsync`（选 CSV 点集算凸包画一圈），与原版风马牛不相及。原版是一个区域管理窗：自动识别（`LandformClassifier`）+ 视口逐点圈画 + 数据库 `mineable_region` 台账（名称/类别/显隐/自定义色）+ 按类着色 overlay + 选中高亮 + **选区笔刷**（PS 式涂改边界，替代夹点）+ 空间唯一（重叠时问"以哪个为主"并裁剪，工作帮↔母范围豁免）。

**做法**：
- `src/Cad/Plan/RegionGeometry.cs`：原 `RegionGeometry`（栅格化重叠判定 ≥2% / 求差留最大块 + 平面拟合 Z）逐行搬 + 原宿主 `RegionBrushSession`（掩膜 + 圆盘/线段涂改 + 减法只留最大块 + Moore 描边 + DP）整搬。
- `src/Cad/Plan/PlanDb.cs` 加 `MineableRegionRepo`（All/Insert/Update/Delete，列 id/name/category/points_json/visible/note/color；用已提交实体 `Data.Entities.MineableRegion`，不依赖工作树里另一会话未提交的 `Data/MineableRegions.cs`）。
- `IPlanEntityHost` 加原 IPitDesignCapability 那一截：`SelectedHandles / BeginScreenPointPick / EndScreenPointPick / ClearScreenPickMarkers / ShowMineableAreaOverlay / ClearMineableAreaOverlay / BeginRegionBrushEdit / EndRegionBrushEdit / SetRegionBrushRadiusPx / OwnerWindow`。宿主实现在新分部 `MainWindow.RegionBrush.cs`：overlay = 图层「可采区域」上的闭合三维多段线（整通道替换）；逐点取点 = `PickPointOrConfirmAsync` 循环（左键加点、右键/回车/Esc 结束 → onCancel），Z 取该处可见三角网采样；笔刷 = 指针按下/移动/松开 + Esc 四处钩子（`RegionBrushOnPressed/Moved/Released/Escape`，插在 MainWindow.axaml.cs 滑动多段线分支之前）+ 预览里画白色笔刷圆圈（`RegionBrushAppendPreview`），半径按屏幕 px 随缩放换算。
- `src/Views/Plan/MineableAreaWindow.cs`：原窗逐段对应（区域列表色块/名称/类别/顶点/显示 · 识别 · 类别下拉+应用类别 · 圈画/完成/取消 · 应用名称/显示隐藏/删除 · 颜色(ColorSwatchPicker 选色即写库)/恢复类别色 · 类别默认色图例 · 笔刷开/±/完成/取消 · 状态栏）；`ResolveSpatialUniquenessAsync` 三选一（以本区域为主/以已有为主/保留重叠）；`OnAutoIdentify` 优先视口选中的多段线、否则图中全部；命名 采场1/外排土场1…。
- 接线：`采场/排土场圈定`→`OpenMineableArea`（单例；未连库时状态栏说清要先连库），旧 `凸包/采场圈定/点凸包` 留命令行别名。自检 `@采排圈定示例`（合成 6 级降深采场环 + 4 级抬升排土环 → 连本机 SQLite → 开窗自动识别 → 选中首块）。

**验证**：`MineableAreaFamilyTests` 5 条（重叠：成片算/共边不算/薄条 <2% 不算 · 求差：切东半 ≈5000 m²、完全覆盖为空、切槽只留大块、Z 沿主体平面 · 笔刷：移出减面/Alt 并入(须连通)增面/竖切留西块/擦空 · 表增改删查 + 颜色去井号 + 无连接不抛 · 类别语义豁免对）+ 规划家族 63 条全过。实机 `@采排圈定示例;@页面截图`：识别到「采场1」（35 顶点）入库，列表色块/类别/顶点/显示齐，选中后名称/类别/色板同步，状态栏「识别到 采场 1 块…已入库 1 块」。合成数据两坨并放时趋势面互相牵扯（只识出主采场或只识出排土场），是分类器对合成几何的既有行为，与窗口无关。

**下一步**：短期生产计划编制组 10 钮 + 标注台阶标高 3 子项。

## §四〇一 短期生产计划编制组 ①「短期生产计划编制」②「月度计划编制」+「派生计划方案」按原 PlanLib.ShortTerm 整族重做 (2026-09-14)

**症状**：短期组这三钮此前全是旧切片：「短期生产计划编制」/「月度计划编制」都命中命令行 `ShortTermPlanCmd`（默认参数一次排产，形状函数摊分、无物料流/去向/库容、无逐月配置表），短期「派生计划方案」与中长远同名 Tag、命中的是中长远的 `DerivePlansCmd`。原版是：单一基础约束（落盘）+ 逐月配置表（人工覆盖、落盘、三个消费方共用同一张表）+ 排产器（工作历×设备×组织形态摊月 → 削峰 → **拆物料流 + 配去向 + 按 Kr 扣库容** → 指标/三量三态）+ 派生笛卡尔积 + 联合评分 + 确定入库（写 `monthly_plan` 台账 + 下游就绪自检）。

**做法**（原 `Modules/PlanLib/ShortTerm` 核心逐文件对齐，纯 C# 部分机械移植、零改算法）：
- 引擎/模型 `src/Cad/Plan/`：`PlanMaterial.cs`（物料规格/密度/Ks/Kr/去向兼容/混采构成解析）、`PlanFlow.cs`（O-D 物料流，三口径换算）、`PlanDestination.cs`（去向台账：读 `EquipmentDataContext.DumpSites/LoadUnloadPoints`，读不到空清单不补样例；`PlanDumpLedger` 逐月按占容方扣）、`PlanFlowAllocator.cs`（按面份额拆供给 → 运输功最小贪心配去向 → 排不下硬警示）、`FaceProcessChain.cs`（穿爆采运排工艺参数与月工序量）、`PlanCase.cs`（主体案例缺省，设备台数按盘子反推）、`ShortTermPlan.cs`（WorkingFace/FieldParams/MineableArea/MonthPeriod(标量由 Flows 派生)/ShortTermResult(PrepCheckState 三态)/ShortTermPlan/ShortTermBase）、`ShortTermScheduler.cs`（排产 + Generate/GenerateVariants）、`MonthlyTargetTable.cs`（逐月配置表：派生/覆盖标记按列/重置/对账不缩放/CSV 往返/两条链对 0 的口径转换 + `MonthlyTargetStore` 落盘）、`ShortTermBaseStore.cs`（基础约束落盘 UTF-8 严格）、`ShortTermSchemeStore.cs`、`ShortTermComparer.cs`、`ShortTermConfirmService.cs`（确定入库唯一实现 + `PlanPeriodKeys.TrySplitYearMonth`）、`AppDataRoot.cs`（软件目录 Data\… 写不进退用户目录 + 桌面老目录迁移）。
- 窗口 `src/Views/Plan/`：`ShortTermConfigWindow.cs`（①来源继承中长远 ②时间骨架 ③现场参数 ③b 逐月配置表(派生/重置选中/全部重置/保存/读回/对账/引擎决定清单) ④均衡权重 ⑤约束 ⑥比选权重 + 试算 + 保存约束落盘）、`MonthlyTargetGrid.cs`（共用逐月配置表控件，Edited 事件）、`ShortTermSolveWindow.cs`（⚡一键编制/编制选中 + 逐月计划图 + 九指标 + ①配置表/②计划表(量来源列) + ✔确定月度计划 + 导出报表 `BuildReport`）、`ShortTermDeriveWindow.cs`（作业组织轴×工作历轴 → 生成并编制(只替换本窗上一次派生的那批) → 对比矩阵 + 逐月产量对比 + 雷达 → 确定/导出）、`ShortTermCharts.cs`（六图 Canvas 手绘）。
- 接线：`短期生产计划编制`→`OpenShortTermConfig`（来源候选 = LongTermSchemeStore.Schemes）、`月度计划编制`(+「一键」)→`OpenShortTermSolve`、Ribbon 短期组「派生计划方案」Tag 改为 `短期派生计划方案`→`OpenShortTermDerive`（与中长远同名钮分开）；旧命令行 `短期生产计划/月度计划 [args]` 留别名。「剥采比均衡」从计划提取改为先读 ShortTermSchemeStore（已确定→已排产）再读中长远。自检 `@短期示例`。

**验证**：`ShortTermFamilyTests` 10 条（物料口径/混采解析 · 供给拆分吨量与实方守恒 + 贪心配去向内排先满转外排 + 无兼容去向量不丢 · 12 月守恒/检修月/峰值/完成率/三量三态 · 产能闸关着只报不改、开了削减不回摊、集中强采峰值更高 · 派生 3×3 命名 + 评分 · 逐月表派生/覆盖保住/重派跟新目标/对账/CSV 往返/重置/0 的口径提示 · 排产吃逐月表覆盖不回摊 · 确定入库无库只写确定簿/阻断拒绝/下游就绪 · 期次换算 · 主体案例自洽）+ 规划家族 98 条全过。实机 `@短期示例` 三窗截图：基础约束窗（主体案例 2026 年 2000/6500，设备 29 台，逐月配置表 12 行派生）；派生窗 3×1=3 套生成并编制 + 对比矩阵 + 雷达 + 推荐「多面展开」；月度计划编制一键编制 5 套 → 逐月计划图（灰检修月）+ 指标（完成率 72%：缺省月上限 120 万t 卡住，原版同）+ 逐月计划表（量来源=流派生）。

**下一步**：短期组其余 7 钮（量驱动采剥接续 / 采场参数识别 / 标注台阶标高 3 子项 / 确定开采程序 / 采排配对复核 / 采掘单元清单）。

## §四〇二 短期组「采场参数识别」按原 ShortTermFieldWindow 整窗重做 (2026-09-14)

**症状**：Ribbon「采场参数识别」命中旧切片 `BenchWidthAsync`（选 CSV 台阶线算平盘宽度），无参数校核、无写回、无窗口。原版是一个两段式窗：①参数自动校核提取（选中坡顶/坡底线 → H/α/W/β → 对模板/规范逐项判定 → 回写验收库 parameter_acceptance / 写进短期计划基础约束）②按平盘宽度提取区域（达标平盘列表管理 + overlay，不入库）。

**做法**：提取器/平盘识别复用既有忠实端口 `Cad/BenchParameterExtractor`（与原 ParameterExtractor 逐字同）/`Cad/BenchWidthIdentifier`；新 `Cad/Plan/ParameterVerifier.cs` 照原版：设计基准优先模板库（原 MineAssLib.BenchTemplateResolver；Kylin 侧 Data 层对应实现若在则经反射取用，本文件不硬依赖）→ 规范默认（是否排土×硬度），逐行走 `parameter_definition` 规范区间 + 验收服务 `ComputeStatus`，服务不可用本地兜底 ±15%；`ToAcceptanceRecords` 量不出来的（≤0）不写。窗口 `Views/Plan/ShortTermFieldWindow.cs`：区域类别/摩擦角/提取并校核/部位编号/回写验收库/写进短期计划（逐项说明覆盖、不拿 0 覆盖、HasSlopeGeometry 提示）/校核表 7 列/平盘宽度阈值/识别/达标平盘表 + 重命名/显隐/删除/清空 + 品红 overlay 选中亮黄；按图层 点云_坡顶线/点云_坡底线 分类，分不出时退回两端兼用并如实说明。接线 `采场参数识别`→`OpenShortTermField`；旧 `平盘宽度识别/现场参数提取/平盘识别` 留别名。自检 `@采场参数示例`（合成 6 级坡顶/坡底环选中）。

**验证**：`ParameterVerifierTests` 1 条（合成 3 级台阶 H12/α70/W4 → 4 行、规范默认来源、稳定性 F、排土场基准、无库回写为空、来源本地兜底、一致判合格）；实机 `@采场参数示例` 截图：提取到 6 级台阶 H≈10m·α≈39.8°·W≈18m·β≈18.4°，已按图层分出坡顶/坡底；校核表 4 行（设计 12/70/4/55.1，偏差 −16.7%/−43.1%/+350%/−66.5% 判偏差，来源本地兜底）；平盘宽 ≥15 m 识别出 3 块达标平盘（232m/9.7ha…）。

**下一步**：标注台阶标高 3 子项 / 确定开采程序 / 采排配对复核 / 量驱动采剥接续 / 采掘单元清单。

## §四〇三 短期组「标注台阶标高」SplitButton 主钮 + 3 子项按原版重做 (2026-09-14)

**症状**：Kylin 的「标注台阶标高」只是一个普通按钮，命中旧切片 `BenchElevationAnnotateAsync`（选 CSV 台阶线）；原版是 SplitButton：主钮开「标注台阶标高」窗（按区域/选中线一键标注，落独立图层可重刷），下拉三项「查询台阶平盘标高」（连续取点放 点+高程 标记，无三角网时问是否建后端 TIN）/「平盘标高清单…」（取线→限区域→按标高归并成级→逐级明细/选中该级/复制/导出）/「标注设置…」（大小/字体/倾斜/颜色/落平盘，写用户设置）。

**做法**：`Cad/BenchElevationAnnotator` 补 `QueryLayer`/`Fonts`/`Options.TiltAxis·TiltDeg·FontName`/`BuildQueryMarker`/`QueryTextAnchorXY`（原 BuildQueryMarkerPmbi 的托管等价）；`Cad/Plan/BenchElevationConfig.cs`（配置 + `UserSettings` 键 bench.elevation.config 读写 + ToOptions）；`Views/Plan/BenchElevationConfigWindow.cs`（模态：大小/字体六选/绕轴+角度+方向/自动配色|统一色 Hex 预览/落平盘 → Result）；`BenchElevationWindow.cs`（全部工作帮|区域勾选(mineable_region)、仅选中线、取线优先级 选中→台阶线图层→全图多段线(剔除自己的标注层)、区域过滤按代表点归属、删旧+整批导入 ▽+引线+文字）；`BenchLevelWindow.cs`（视口选中/指定图层(含线数，排除标注层)/全图 · 限定可采区域 · 归并容差/只统计水平线/碎线阈值 · 大数字结论 + 7 列明细 + 警示 · 选中该级的线/复制清单/导出 CSV；`BenchLevelInventory` 复用既有端口）；宿主 `MainWindow.Plan.cs`：`OpenBenchElevation/OpenBenchLevel/OpenBenchElevationConfigAsync/BenchElevationQueryAsync`（后端 TIN = 全图多段线顶点 Delaunay → `TinSampler`，只驻内存；每点 点实体 + 高程文字落「台阶标高查询」层）；`IPlanEntityHost` 加 `LayerNames/ClearSelection`。Ribbon：按钮改为带 MenuFlyout 的 SplitButton（主项 + 分隔 + 3 子项，Tag `标注台阶标高 / 查询台阶平盘标高 / 平盘标高清单 / 标注台阶标高设置`），旧 CSV 命令留别名 `台阶标高标注/平盘清单…`。自检 `@标注台阶标高示例`。

**验证**：`BenchElevationFamilyTests` 1 条（配置→选项映射、查询标记取整/正负号/青色、文字锚点、区域归属、字体表）；实机 `@标注台阶标高示例`：12 环选中 → 一键标注生成 7 处（10 处落平盘中央）落「台阶标高标注」层；平盘标高清单全图统计 7 级 40~100 m（级间距中位 10 m，12 条线）。CheckBox 文案含 "_" 被当助记键吃掉 → 一律用 TextBlock 作 Content。

**下一步**：确定开采程序（ShortTermSequenceWindow）/ 采排配对复核 / 量驱动采剥接续 / 采掘单元清单。

## §四〇四 短期组「确定开采程序」按原 ShortTermSequenceWindow 整窗重做 (2026-09-14)

**原版**（`PlanLib/ShortTerm/ShortTermSequenceWindow.xaml(.cs)` 369+230 行 + `PlanEquipModelCatalog.cs` 221 + `FaceProcessTree.cs` 340 + `FaceProcessDialog.xaml(.cs)` 183+150 + `FaceSeedLoader.cs` 110 + `FaceAutoBuilder.cs` 238 + `TaskLib/Simulation/EquipIconBuffer.cs` 320）：编辑 `ShortTermSchemeStore.Base.Faces` —— 左表（标注序/名称/面编号 face_code/台阶标高/份额/备采储量/推进方位(只读)/物料下拉/混采构成/去向下拉(按物料过滤)/运距/工艺流程(只读)/备注），工具条 按本期单元派生作业面（期次台账 → FaceSeedLoader → FaceAutoBuilder 按 物料×顶板 6 m 档分组，替换现有面并确认）/ 增加 / 删除选中 / 归一份额 / 按份额分摊备采储量 / 清空去向 / 编辑工艺流程（FaceProcessDialog：穿爆采运排参数 + 月工序量实时预览，只按确定才写回）/ 校核设备配置（只报"选了但用不了"）/ 清空设备配置 / 重读型号·面台账 / 保存开采程序；右侧**设备工艺树**（面 → 工序 → 设备：型号下拉带在册可派台数、运输支配车数、参数摘要、面级/工序级告警；免爆面在结构上没有穿孔/爆破分支；图标走 EquipIconLibrary = 三维 EquipSymbolLibrary 形态 + EquipPalette 配色 + SimPanelCamera 轴测，图标边长可配）。Kylin 此前「确定开采程序」命中的是 `AdvanceCmd`（平行推进旧切片）。

**Kylin**：`Cad/Plan/PlanEquipModelCatalog.cs`（equipment_model × equipment 在册/可派台数、working_face FaceCodes/BenchHeightOf/Check，库未就绪全程降级）、`FaceSeedLoader.cs`（`UnitLedger.MonthlyUnitLedgerStore` 读期次，排土位置不参与）、`FaceAutoBuilder.cs`、`FaceProcessTree.cs`（TreeNodeBase/FaceNode/ProcessNode，Icon 类型 ImageSource → Avalonia IImage）逐行移植；`TaskLib/Simulation/EquipIconBuffer.cs`（EquipIconBuffer + EquipIconLibrary：WPF DrawingImage/PathGeometry → Avalonia 同名类型，painter's 排序 + 朗伯明暗照旧；垫透明方块钉住图标边界；★ 原版 ToIcon 把相机已翻成屏幕向下的 sy 再翻一次，图标上下颠倒 —— 按注释意图取正，与三维姿态一致）；`Views/Plan/FaceProcessDialog.cs`、`ShortTermSequenceWindow.cs`（DataGrid 物料列 = 模板列内 ComboBox（DataContext 同步 + 选中即写回并走原 CellEditEnding 逻辑：物料不合规去向清空 / 换去向清运距 / ApplyTo 回填）、去向列 CellEditingTemplate 按物料过滤；树用 TreeViewItem 手工铺，IsExpanded 与节点 TwoWay 绑定，节点文案/图标经 Binding 随 Raise 刷新，横向不滚让摘要/告警可换行；★ 名称列不用 *（有 * 列时其余定宽列被压到最小宽）改 170 定宽 + 横向滚动）；宿主 `OpenShortTermSequence`（先 `EnsureGeoDb`：型号/面台账/去向全在库里，原版插件加载即已连库），dispatch `确定开采程序` → 新窗，`平行推进/工作线推进` 仍走 AdvanceCmd；自检 `@确定开采程序示例 / @确定开采程序确认`。

**验证**：`FaceAutoBuilderTests` 原 15 条 + FE5 组 3 条逐条通过；`FaceProcessTreeTests` 5 条（免爆面无穿爆分支 / 就地改型号写回并回调 / 未归属告警与统计口径 / 型号目录库未就绪降级 / 下拉文案）；实机：开窗 → 型号字典 50 条（穿 12·采 10·运 9·排 12）· working_face 5 个面编号 → 增面改硬岩 1212 m 钉 KY-250 → 树上出现 穿孔/爆破 支 + "KY-250 在册 0 台"告警 + 面级 3 条告警 → 工艺对话框（免爆面按【免爆】走 · 参数完备 ✓）确定 → 归一份额 100% → 保存：3 面 · 已配去向 2 · 已配型号 1 · ◆ 1 条型号挑不到设备。

**下一步**：采排配对复核（DumpPairingWindow 588）/ 量驱动采剥接续（MonthlyStripSession 1322 + Window 467 + MonthlyProcessSummary + MinePlanImporter + QueryTin）/ 采掘单元清单（MiningUnitPlanWindow 2243 + 配套）。

## §四〇五 短期组「采排配对」按原 PlanLib.ShortTerm.DumpPairingWindow 整窗重做 + 采掘单元排产内核整族移植 (2026-09-14)

**原版**（`PlanLib/ShortTerm/DumpPairingWindow.xaml(.cs)` 588+200 行）：方案 × 期次 → 源—汇流向矩阵（行=作业面·物料，列=台账全部去向 + 必要时「未分配」，格=原位实方可改、不合规格淡红禁配、列头带类型·兜底运距）+ 各去向库容条（设计/期初已填/本月入方占容/期末剩余，占容方 Kr 口径，期初 = 台账 + 之前各月已排 逐月累扣，≥85% 橙、排满红；通过型「不限容」只看接收量）+ 本月汇总七格（采出/剥离/排弃占容/剥采比/内排率/运输功/吨量加权运距，全走 MonthPeriod 派生属性）+ 警示；流的唯一来源是 `UnitPlanStore.Last`（采掘单元清单「按目标排产」）经 `UnitFlowBridge` 归并，**取不到就明说不自建**；手改格子/整行改投 → 回写 MonthPeriod.Flows → 重算 + 同步方案级指标；重读去向台账 / 导出 CSV。Kylin 此前「采排配对」命中的是 `Views/GeoDb/DumpPairingWindow.cs`（§三六一，按 Cad.Tasks 台账 + PeriodBalance 做的切片，与原版窗口无关）。

**Kylin**：`Cad/Units/{MineUnit,UnitGraph,DumpAllocation,DumpSlotAdapter,HaulModel,UnitPlanEngine}.cs`（原 MineAssLib.Driving 采掘单元排产内核 2879 行逐行移植；`DumpSlotAdapter` 的 `MineAssLib.Models.WorkLineGeometry` 在 Kylin 叫 `Cad.WorkLineSamples`）、`Cad/Plan/UnitPlanStore.cs`（会话内交接件：失败不覆盖好结果、Caption 自述"上一次没成"）、`Cad/Plan/UnitFlowBridge.cs`（单元→作业面·物料归并、m³→万m³、去向按名字映射失败落未分配并点名、守恒判据）、`Views/Plan/DumpPairingWindow.cs`（去向列 = 模板列：显示 TextBlock 绑 Cells[j].Text/CellBrush/Tip，编辑 TextBox TwoWay；CellEditEnded → 回写重算；库容条 FuncDataTemplate 三段 Rectangle；导出走 StorageProvider）；`PlanDestinationCatalog.SelftestOverride`（空库自检用合成去向）；`PlanUi.Group` 标题可换行；宿主 `OpenDumpPairingPlan`（先 EnsureGeoDb），dispatch `采排配对` → 新窗（旧切片 `OpenDumpPairing` 留存不再命中）；自检 `@采排配对示例`。

**验证**：原 `Tests.MineAssLib` 的 DumpAllocation/DumpSlotAdapter/UnitGraphOverburdenGeometry/UnitPlanCriteriaMutation/UnitPlanEngine/UnitPlanFaceAdvance/UnitPlanRealLedger/UnitPlanStripTarget 共 **114 条**逐条通过（DumpAllocationTests 的合成块体从 BlockModelLib.BlockModel 改建 InclineBlockSource，同几何同属性）；原 `UnitPairingBridgeTests` P1–P6 6 条通过（P1 源码判据改读 Kylin 窗口文件）；实机 `@采排配对示例`：均衡型 2026-01 → 8 行（2 面 × 表土/风化岩/硬岩/煤）× 4 合成去向 → 不合规格淡红、表土整行改投表土堆场 11.4 万m³ → 库容条 内排土场 100% 排满标红 / 外排 28.1% / 表土堆场 50.9% / 破碎站 不限容，汇总 采出 120 万t · 剥离 381 · 占容 434 · 剥采比 3.18 · 内排率 64.6% · 运输功 2,343 万t·km · 2.3 km，警示"内排土场期末填充率 100%"。

**下一步**：量驱动采剥接续（MonthlyStripSession 1322 + Window 467 + MonthlyProcessSummary + MinePlanImporter + QueryTin）/ 采掘单元清单（MiningUnitPlanWindow 2243 + UnitVolumeBinner + EquipmentAssigner 2111 + FaceUnitResolver + EquipmentAssignPanel 等；内核 UnitPlanEngine 已就位）。

## §四〇六 日常生产组织页签 24 钮逐钮忠实性复核总账 —— 原 TaskLib 整族移植收官 (2026-09-14)

**范围**：原 `Modules/TaskLib`（TaskLibPlugin 6 组 24 钮）。此前 Kylin 这一页签除「作业面台账」外全部命中 `Views.GeoDb` 下的本地切片（各自另起一套模型、口径与原版不一致、无原单测）。本轮按「原版纯 C# 家族整文件搬 + 原单测照跑 + WPF 窗体逐行改写成 Avalonia 代码布局 + 实机截图核对」把 24 钮全部对齐到原版，旧切片一律降级为命令行别名保留。

**地基**（5887928）：`Data/Sql`（原 SqlLib：Dapper 仓储/Upsert/Bulk）+ `Data/Entities` 54 表 + `Data/Services` 74 文件 + `GeoDataContext`/`EquipmentDataContext` 静态门面；`TaskLib/{Domain,Engine,Zoning,Scheduling,Adjust,ShiftOps,Simulation,Reporting,Gantt}` 纯 C# 逐文件整搬（约 120 文件）；`UnitLedger`（原 BlockModelLib 台账族）、`Platform`+`Capabilities`（IViewCapability 等 16 接口）、`Shading`（GeoTiffInfo/OrthophotoConfig/OrthophotoLoader）；原 `Tests.TaskLib` 63 文件照搬（5 条依赖原桌面真库当时状态的标 Skip）。⚠ 该提交被并发会话预暂存的 `db/dm/install-dm8-kylin.sh`（新增）与 `src/Cad/CadFileBrowser.cs`/`FileBrowserTests.cs`（删除）扫入，未改历史，此处记账。

**逐钮台账**（钮 → Kylin 窗 → 提交 → 实机核对）：

| # | 组 | 钮 | Kylin 窗（`Views/TaskLib/`） | 提交 | 核对 |
|---|---|---|---|---|---|
| 1 | 计划编制 | 作业面台账 | `WorkFaceLedgerWindow` 26 列盘子台账 | 17ff1c2 | 截图 26 表头 |
| 2 | 计划编制 | 生产任务编制 | `DailyGanttWindow` + `GanttRenderer` | 097413f | 甘特样例截图 |
| 3 | 计划编制 | 生产任务书 | `TaskOrderWindow` + `TaskOrderPdf` | 7d9b6d3 | 截图 |
| 4 | 计划编制 | 编制配置 | `CompileConfigWindow`（能力预算条/锚点/链路体检/MF 条） | c6fdf96 | 截图 |
| 5 | 计划编制 | 周计划编制 | `WeekPlanWindow` | 2a41fc0 | 截图 |
| 6 | 任务下达 | 任务下达 | `TaskDispatchWindow`（校验→实例+回执→撤回） | 7d9b6d3 | 截图 |
| 7 | 任务下达 | 派车单 | `DispatchOrderWindow` + `DispatchOrderPdf`（按车分组 DataGridCollectionView） | 7d9b6d3 | 截图 |
| 8 | 任务下达 | 班组派工 | `CrewAssignWindow` | c6fdf96 | 截图 |
| 9 | 任务下达 | 调度态势看板 | `DispatchBoardWindow` | d00c376 | 截图 |
| 10 | 执行跟踪 | 实绩录入 | `ActualEntryWindow` | c6fdf96 | 截图 |
| 11 | 执行跟踪 | 工序进度跟踪 | `ProcessProgressWindow` | d00c376 | 截图 |
| 12 | 执行跟踪 | 设备状态·故障报修 | `EquipStatusWindow` | d00c376 | 截图 |
| 13 | 执行跟踪 | 生产任务动态调整 | `DynamicAdjustWindow` + `StageGanttRenderer` + `Simulation/SimPanelOverlay`(自绘轴测面板) + `SimBasemapRaster` | a6d29ce | 面板样例截图（栅格/明暗/遮挡/铭牌） |
| 14 | 执行跟踪 | 班内工艺·工序推演 | `ShiftProcessWindow` + `ShiftChainStrip` | d43c9d7 | 截图 |
| 15 | 基础数据 | 去向台账 | `SinkLedgerWindow`（+ 盘点/可接物料小窗） | 98b7863 | 截图 |
| 16 | 基础数据 | 班次日历 | `ShiftCalendarWindow` | 2a41fc0 | 截图 |
| 17 | 基础数据 | 检修档期 | `MaintenancePlanWindow` | 2a41fc0 | 截图 |
| 18 | 基础数据 | 影像底图 | `BasemapConfigWindow`（经 IViewCapability 贴视口） | 2a41fc0 | 截图 |
| 19 | 基础数据 | 钻爆计划衔接 | `BlastPlanWindow` | 2a41fc0 | 截图 |
| 20 | 基础数据 | 作业区划分 | `WorkZoneLayoutWindow` + `Zoning/ZoneBasemap` | 07b17a4 | 截图（dlt05.tif + 台账一块） |
| 21 | 统计分析 | 产量统计 | `OutputStatsWindow` | d00c376 | 截图 |
| 22 | 统计分析 | 质量·配煤分析 | `QualityAnalysisWindow` | d00c376 | 截图 |
| 23 | 统计分析 | 达成度评价 | `AttainmentWindow` | d00c376 | 截图 |
| 24 | 统计分析 | 生产报告 | `ReportHubWindow` 四页签 + `ReportViewBuilder` | b50b507 | 截图 |

**接线**：全部经 `MainWindow.TaskLib.cs` 的 `OpenTaskWindow<T>`（EnsureGeoDb → EnsureTaskLibHost → 非模态单例 → `GeoDbWindows.NoteLast` 供 `@页面截图`）；`KylinViewCapability` 实现 `IViewCapability`（SetOrthophoto → 内存 BGRA 采样器 → `Cad.OrthoBasemap.Apply` 顶点着色；RunRibbonCommand/RequestSceneRefresh）注入 `SimHost.View`。自检钩子：`甘特样例`（合成 ExploderConfig 喂甘特）、`面板样例`（合成栅格/台阶体/推进环/设备点/铭牌喂 SimPanelOverlay）。

**Avalonia 差异（都只是宿主层）**：WPF PrintDialog → QuestPDF 横向 A4 PDF（任务书/派车单/报表）；MessageBox → CoalMsgBox；DropShadowEffect → 无（Border.BoxShadow 会把子树按 DPI 再放大渲染，见 `memory/avalonia-boxshadow-child-scaled`）；报表纸张外层横向滚动 Disabled（Auto 时居中内容溢出）；DataGrid 分组 → DataGridCollectionView；行样式 DataTrigger → LoadingRow/行属性绑定；AutoCompleteBox 换源清 Text → 延后一拍补写；DatePicker 三栏需 270 宽。名字冲突（Data.WorkCalendar/ShiftWindow/ProductionPlanContext/SinkRegistryLoader、Engine.ChainStage）用命名空间内 using 别名消歧。

**验证**：全套 5256 条中 5238 通过、5 跳过；13 条失败全在并发会话未提交的 `MonthlyStripSessionTests`/`MinePlanImporterTests`（短期组在建，与本页签无关）。TaskLib 原单测（含 SimPanelCameraTests 12 条）全部通过。自检脚本改进：`RunSelftest` 的 catch 由吞掉改为记 crash.log「脚本中止」（此前截图路径写成 `/c/…` 时脚本静默中止只看到没图）。

**下一步**：本页签移植完成；剩余为其它页签（短期组等）由并发会话推进。

## §四〇六 短期组「量驱动采剥接续」按原 MonthlyStripWindow 整窗重做 + 量驱动内核整族移植 (2026-09-14)

**原版**（`PlanLib/ShortTerm/MonthlyStripWindow.xaml(.cs)` 280+467 行 + `MonthlyStripSession.cs` 1322 + `MonthlyProcessSummary.cs` 262 + `MinePlanImporter.cs` 319 + `QueryTin.cs` 74 + `RoadHaulProvider.cs` 387；MineAssLib `ScheduleDerivation.cs` 462 + `MinePlanExport.cs` 730 + `CoupledMinePlanner.cs` 407 + `RollingReplan.cs` 234）：整条链唯一入口 —— ① 备料（岩量剖面 .case/.mprof：真打开核对有没有岩剖面/是否自洽，排土位置从「排土条带」取）② 规则（月数/月煤量**只读回显**逐月配置表 + N 备采月 / 剥离节奏 贴底·拉平·前重 / 配对策略 运输功最小·内排优先·库容均衡 / 期初已达稳态）→ 排产（剖面 → 排产 → 采排配对 → 外循环 → 契约 → 填月度方案，Log 逐条记每一步结论与引擎替用户做的决定）→ 五页签（逐月计划 12 列含「紧迫」触底/触顶 · 过程与结论 · 派生比选 N×剥离节奏×采煤节奏 27 套打分归因 + 按选中方案排产 · 实绩与重排 · 物料流 岩流+煤流「分层量来源」标引擎摊的）→ 导出报表(.txt 带 BOM)/导出契约 JSON(无 BOM)/装回契约/确定为月度方案（写 ShortTermConfirmService 唯一一处）。Kylin 此前该钮**无处理器**。

**Kylin**：`Cad/Units/{ScheduleDerivation,MinePlanExport,CoupledMinePlanner,RollingReplan}.cs` + `Cad/Plan/{MonthlyStripSession,MonthlyProcessSummary,MinePlanImporter,QueryTin,RoadHaulProvider}.cs` 逐行移植（`RoadHaulProvider` 全限定 `Cad.Road.*`：Cad 里另有同名旧切片 RoadGraph/DijkstraPathSolver；WorkLineGeometry → `Cad.WorkLineSamples`；排土台账实体 → `Data.Entities.DumpSite`）；`Views/Plan/MonthlyStripWindow.cs`（五页签 TabControl，Log 用 ListBox+等宽字体，文件对话框走 StorageProvider）；宿主 `OpenMonthlyStrip`，dispatch `量驱动采剥接续` → 新窗；自检 `@量驱动采剥接续示例`（合成两层煤剖面 + 内/外排位置跑整条链 + 派生比选）。**顺手修的两处 Kylin 既有断桥**：① `Cad/Dump/DumpStripStore` 与 `UnitLedger/DumpStripStore` 是两份同体独立 static —— 排土条带窗往前者写、采掘单元清单/三维模拟/本链读后者，永远读到「本会话还没生成过」→ 前者改为转发；② `TaskLib/Simulation/SimBuilder.cs`/`SimModel.cs` 按 `"PlanLib.ShortTerm.ShortTermSchemeStore, PlanLib"` 反射找确定簿，在 Kylin 永远解不到 → 三维月推演静默退回外推 → 改为 `"PitMine3D.Kylin.Cad.Plan.ShortTermSchemeStore, PitMine3D.Kylin"`（原 I4c/S11 判据钉住）。

**验证**：原 Tests.MineAssLib（RollingReplan 7 / CoupledPlanner 11 / ScheduleDerivation 15 / MinePlanExport 16 / DownstreamRobustness 7 / SourceGranularity 3 / InvariantSweep 6 / RealProfileSchedule 1）+ Tests.PitMineApp（MonthlyStripSession 52 / MinePlanImporter 11 / MonthlyProcessRollup 6 / ConfirmToLedger 6 / MinePlanEndToEnd 2 / DeadOptionSweep 1）逐条移植：合成块体经 `tests/SynthBlockModel.cs`（照原 BlockModelLib API 形状，隐式转 InclineBlockSource）；源码类判据改读 Kylin 文件（S7/S13/S26/S27/S28 把 `Col("…","Path")`/`new Binding("Path")` 改写成 `{Binding Path}` 后原断言原样成立；S31 改核 Kylin 派发接线 5 钮各有处理器；S41 扫 src 下确定簿写者唯一 = ShortTermConfirmService.cs；D1 死输入扫描按 Kylin 路径，EquipmentAssigner 未移植跳过 + FallbackDetour/FaceQuotas/FaceSegments 三项过渡豁免，待采掘单元清单 tick 删）；S40/S42（WPF RibbonRegistry/IconDict 专属）与 S14（无去向台账时推演帧量恒 0，与原版同源）标 Skip 并写明理由。合计 **217 条通过 · 3 条 Skip**。实机 `@量驱动采剥接续示例`：REAL_latest.case 识别为「2 层 · 台阶高 10m · 岩 59.0万m³ · 煤 52.0万t」，逐月配置表 12 个月煤合计 1440 万t；合成算例排产 ✔ 12 个月 · 煤 96.0万t · 岩 142.0万m³ · 剥采比 1.48 · 内排率 68.7% · 可确定；派生比选 27 套里解出 27 套 · 21 个不同答案 · 推荐「N=4 · 剥离贴底 · 采煤抢产」96.6 分。

**⚠ 依赖说明**：本 tick 及 §四〇五 的内核依赖另一会话尚未提交的 `src/Cad/{InclineVolumeEngine,MineProfile,MineProfileFile,MonthlyMineSchedule,WorkLineModel,WorkLineProjector}.cs`（工作树里已有、可编译；HEAD 单独不完整，待其提交）。

**下一步**：采掘单元清单（MiningUnitPlanWindow 2243 + UnitVolumeBinner 392 + EquipmentAssigner 2111 + FaceUnitResolver + UnitSchemeCompareWindow 1417 + EquipmentAssignPanel 830 + UnitSolidPreview/PeriodPlanOverview/CoalSinkPoint/ShiftDuty 等；内核 UnitPlanEngine 已在 Cad/Units）—— 短期组最后一钮。
