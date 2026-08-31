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
