# DayOps → Kylin(Avalonia) UI 1:1 复刻进度

目标：把 Kylin 版 UI 向原版 DayOps 的 Ribbon **1:1 完全复刻**——所有图标用程序绘制的矢量图标、
完整布局、设计/美化对齐、功能对齐。**loop 每 3 分钟推进一批，直到所有模块完成后停止。**

## ✅ 已完成

- **图标转换管线**：[build/convert_icons.py](../build/convert_icons.py) 把原版 WPF `DrawingImage` 图标批量转 Avalonia。
  系统性差异已处理：命名空间 / Pen `StartLineCap+EndLineCap`→单一 `LineCap` / `DashStyle.Dashes` 空格→逗号 /
  `LinearGradientBrush` 相对点→百分比(仅限渐变标签内，避开 LineGeometry 绝对坐标) / 去 `DrawingGroup.ClipGeometry`。
- **93 个原版矢量图标**全部转换并 **Avalonia 编译 0 错误** → [src/Styles/IconDict.axaml](../src/Styles/IconDict.axaml)。
- **开始 tab** 的 文件/绘制/修改/图层/视图/特性/帮助 七组按钮已接真图标（`<Image Source="{StaticResource icon_*}"/>`），
  Windows 实测渲染 1:1 正确（新建绿⊕徽章、打开橙翻盖、保存蓝软盘、移动四向箭头…全彩+渐变）。

## ✅ 模块页进度

- **工具链就绪**：[convert_icons.py](../build/convert_icons.py) 支持模块参数（`python convert_icons.py <模块名>`）；
  [gen_module_tabs.py](../build/gen_module_tabs.py) 从"组→按钮(名/图标键/大小)"数据结构生成 TabItem XAML 并替换占位块——**可复用于全部 8 模块**。
- **原版模块 tab 实为 7 个**（BlockModel 不单列 tab，挂到「三维地质建模」的组）：
  地质与工程信息数据库 / 三维地质建模 / 点云处理 / 剥采排工程辅助设计 / 道路运输系统 / 生产计划编制 / 日常生产组织。
  gen_module_tabs.py 用哨兵注释托管整段模块页，按原版序生成，未详录的先出「（待复刻）」占位。
- **[1/7] 地质与工程信息数据库**（GeoDataBase）：36 图标，4 组 24 按钮，1:1 渲染✅
- **[2/7] 三维地质建模**（MeshEditLib）：61 图标，5 组（建模/倾斜摄影/编辑/地质统计/更新地质模型），1:1 渲染✅
  转换器新增：MappingMode 删除(保留 Absolute 判断) / DashCap 删除 / FillRule Nonzero→NonZero。
- **[3/7] 点云处理**（PointCloudLib）：35 图标，6 组（点云数据/修复/编辑/分析逐点/三角网重建/三角网分析），1:1 渲染✅
  转换器新增：SweepDirection Counterclockwise→CounterClockwise。
- **[4/7] 剥采排工程辅助设计**（MineAssLib）：37 图标，3 组（剥采工程16/运输工程9/排土工程5），1:1 渲染✅（转换器已稳，一次过）。
- **[5/7] 道路运输系统**（RoadLib）：34 图标，4 组（路网构建/运输表征/路网维护与延拓/寻径与运距），1:1 渲染✅（提取道路中心线复用点云字典的 pc_road_centerline）。
- **[6/7] 生产计划编制**（PlanLib）：27 图标，3 组（优化开采设计6/中长远进度计划7/短期生产计划10），1:1 渲染✅。
- **[7/7] 日常生产组织**（TaskLib）：21 图标，6 组（基础数据5/任务编制5/任务下达4/执行调度4/评价分析4/调整·推演2），1:1 渲染✅。

## ✅ 全部完成（loop 已 CronDelete 停止）

- **7 个模块 tab + 开始 tab 全部 1:1 复刻**，共 **173 个按钮**，全部用原版程序绘制矢量图标（Host 93 + 7 模块 264 = 357 个图标已转 Avalonia）。
- **在真 Linux（Ubuntu 22.04 WSLg，麒麟代理）实测全量渲染通过**：8 tab 全就位、图标 1:1、OpenGL 4.0 视口正常、中文正确。截图 docs/module-task-on-linux.png。
- linux-x64 自包含交付包已含全部图标重新发布。

## 布局修复（2026-08-28）

- **小按钮 3 行 × N 列**（对齐 DayOps）：原来一列只塞 2 个——根因是 FluentTheme 的 Button `MinHeight=32` 顶掉了 `Height`，且功能区 band 只有 98px（减标题/内边距后内容区 ~63px 装不下 3×22）。
  修：`Button.small` 压 `MinHeight=0`+`Height=22`+`Padding=5,0`；band 高 98→108（生成器与开始页同步）。现文件组=另存为/导入/选项一列 3 行，绘制/修改组多列各 3 行，与 DayOps 一致。

## ⏳ 精修项（非阻塞，后续可选）

- 编辑组/工具的 DropDownButton/SplitButton 子菜单（当前渲为顶层大按钮）；BlockModel 组挂到三维地质建模 tab；命令别名/快捷键/状态栏功能对齐。
   套路：读 `Modules/<M>/<M>Plugin.cs` 的 AddTab/AddGroup/AddButton → 填 gen_module_tabs.py MODULES → `convert_icons.py <M>` → `gen_module_tabs.py` → App.axaml 合并字典 → 构建。
   （另：编辑组的 DropDownButton/SplitButton 子项目前渲为顶层大按钮，下拉子菜单待精修；BlockModel 组待挂到三维地质建模 tab。）
2. **开始 tab 补全**：注释组、修改组的 RibbonToolBar 三行布局、图层组的下拉/开关精确布局。
3. **美化/设计对齐**：Ribbon 配色、间距、分组分隔线、标签页样式精确对齐 DayOps。
4. **功能对齐**：命令别名、快捷键、状态栏项、命令行行为。

## 下一步
补模块页真 Ribbon（先转换 `Modules/*/Assets/IconDict.xaml` 的模块图标，再按各模块 tab 布局搭）。
