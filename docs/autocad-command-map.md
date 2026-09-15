# AutoCAD 命令一致性对照

本系统的功能区按钮是中文名（忠实原 PitMine3D），命令行原先只认少量英文名。
本文列出**与 AutoCAD 同功能**的那部分，以及统一后使用的 AutoCAD 标准命令名 + acad.pgp 缩写。

对照表的唯一事实来源是 [`src/Cad/AcadCommands.cs`](../src/Cad/AcadCommands.cs)，
回归见 [`tests/PitMine3D.Kylin.Tests/AcadCommandsTests.cs`](../tests/PitMine3D.Kylin.Tests/AcadCommandsTests.cs)。
运行时在命令行敲 `ALIAS`（或「命令别名」）即可把整表打到信息栏。

## 命令行行为（与 AutoCAD 对齐）

| 行为 | 说明 |
|---|---|
| 常驻聆听 | 焦点在视口/面板时直接敲字符即进命令行，不必先用鼠标点命令框 |
| 空格 = 回车 | 纯 ASCII 命令按空格即执行；中文（留给输入法）与同行带参数的命令（`图案填充 45 2`、`SOR 8 1.0`、`POLYGON 6`）不劫持空格 |
| 空行 + 空格/回车 | 重复上次命令（仅空闲态） |
| ↑ / ↓ | 回溯命令历史 |
| Tab | 补全首个候选；候选行显示 `AutoCAD名=中文功能` |
| ESC | 取消当前命令 |
| 选择对象阶段 | `L` `P` `WP` `CP` `ALL` 按 AutoCAD **选择选项**解释（上次画的 / 上次选择集 / 窗口多边形 / 交叉多边形 / 全部）；命令提示符下则按**命令名**解释（`L`=直线、`P`=平移、`CP`=复制） |

## 命令参数交互

命令行发起的命令，**参数在命令行上逐项问**，不弹对话框（功能区按钮发起的仍走原对话框，保持原版鼠标交互）。

```
命令: 加密多段线
最大间距(m) <10>: 3
✓ 2 条多段线：顶点 7 → 106 (+99)

命令: 修改点样式
样式 [Cross (十字) [2]/X (叉) [3]/Dot (圆点) [0]/Vertical (竖线) [4]/Circle + Cross (圆+十字) [34]/Circle + X (圆+叉) [35]/Circle + Dot (圆+点) [32]/Circle + Vertical (圆+竖线) [36]/Square + Cross (方框+十字) [66]/Square + X (方框+叉) [67]/Square + Dot (方框+点) [64]/Square + Vertical (方框+竖线) [68]] <Cross (十字) [2]>: 2
大小(世界单位) <3>: 5
> 修改点样式：style=2 size=5，已更新 6
```

| 规则 | 说明 |
|---|---|
| 提示格式 | `标签(单位) [选项…] <默认值>:`，同 AutoCAD |
| 直接回车 | 取尖括号里的默认值 |
| 选项 | 可键入 全名 / 序号（1 起）/ 唯一前缀 |
| 是否项 | `Y`/`N`/`是`/`否`/`1`/`0`/`true`/`false` |
| 输入非法 | 就地回显原因并重问该项（不中断命令） |
| ESC | 放弃该命令 |
| 同行给参数 | 按顺序填，填满就不问：`加密多段线 3` · `图案填充 45 2` · `POL 8` · `SOR 8 1.0` |

## 绘制中的选项关键字

提示行末尾 `[ ]` 里的就是当前可键入的关键字，同 AutoCAD：

```
命令: PL
多段线：指定起点: 0,0
多段线：指定下一点或 [放弃(U)]（已 1 点，回车/双击结束）: 100,0
多段线：指定下一点或 [闭合(C)/放弃(U)]（已 2 点，回车/双击结束）: 100,80
多段线：指定下一点或 [闭合(C)/放弃(U)]（已 3 点，回车/双击结束）: C
多段线已闭合（3 点）
```

| 命令 | 选项 | 说明 |
|---|---|---|
| 多段线 PLINE | `C` 闭合 | 首尾相接成闭合多段线并收笔（≥3 点） |
| | `U` 放弃 | 退掉刚点的那一点，继续画 |
| 圆 CIRCLE | `3P` / `2P` / `T` | 切到 三点 / 两点 / 相切、相切、半径（仅在圆心未定时给，定了再切等于丢点） |

关键字**先于命令解析**：画多段线时键入的 `C` 是「闭合」，空闲时键入的 `C` 才是 `CIRCLE`——
和选择对象阶段的 `L`/`P`/`CP` 是同一套上下文分流。中文全名（`闭合` / `放弃`）同样认。

多点绘制中：**回车 / 双击 = 结束**（原先只能双击或 Esc）；`Esc` 仍是结束并保留已画部分。

实现：挂钩 `PromptDialog.CommandLineAsker`，所以凡是用 `PromptDialog` 取参数的命令**一律受益**；
原本"参数只能写同一行、缺省就静默取默认值"的几条（图案填充 / SOR / ROR / 正多边形）另行接上，见
[`MainWindow.CmdParams.cs`](../src/Views/MainWindow.CmdParams.cs)，提示与取值的纯逻辑在
[`ParamPrompt.cs`](../src/Views/Modeling/ParamPrompt.cs)。

## 对照表

### 文件
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| NEW / OPEN / SAVE | — | 新建 / 打开 / 保存 |
| QSAVE | — | 保存 |
| SAVEAS | — | 另存为 |
| IMPORT | IMP | 导入（DXF/DWG/OFF） |
| EXPORT | EXP | 导出（DXF） |
| OPTIONS / PREFERENCES | OP | 选项 |
| UNDO / REDO / MREDO | U | 撤销 / 重做 |
| HELP | ? | 帮助文档 |

### 绘图
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| LINE | L | 直线 |
| PLINE | PL | 多段线 |
| CIRCLE | C | 圆（另有 C2P / C3P / TTR 三个子命令） |
| ARC | A | 圆弧（另有 ARCSCE / ARCCSE） |
| RECTANG / RECTANGLE | REC | 矩形 |
| POLYGON | POL | 正多边形（可带边数：`POL 6`） |
| POINT | PO | 点 |
| TEXT / DTEXT | DT | 单行文字 |
| MTEXT | MT / T | 多行文字 |
| HATCH / BHATCH | H / BH | 图案填充（可带角度、间距：`H 45 2`） |

### 修改
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| ERASE | E | 删除 |
| MOVE | M | 移动 |
| COPY | CO / CP | 复制 |
| MIRROR | MI | 镜像 |
| OFFSET | O | 偏移 |
| ROTATE | RO | 旋转 |
| SCALE | SC | 缩放 |
| TRIM | TR | 修剪 |
| EXTEND | EX | 延伸 |
| BREAK | BR | 打断 |
| EXPLODE | X | 分解 |
| JOIN | J | 连接多段线 |
| OVERKILL | — | 删除重复线 |

### 视图
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| ZOOM | Z | 范围缩放 |
| PAN | P | 平移 |
| REGEN / REGENALL / REDRAW | RE / REA / R | 刷新（重生成） |
| 3DORBIT / ORBIT | 3DO | 三维轨道 |
| PLAN / TOP | — | 俯视 |
| BOTTOM / FRONT / BACK / LEFT / RIGHT | — | 仰视 / 主视 / 后视 / 左视 / 右视 |
| SWISO / SEISO / NEISO / NWISO | — | 西南 / 东南 / 东北 / 西北 等轴测 |
| VSCURRENT / SHADEMODE | VS | 渲染配置（着色模式 平面/平滑/线框/隐藏填充/等高线/坡度/坡向/高程/属性分级 · 等高距 · 色带与区间 · 文字字体 · 材质(PBR/地学预设) · 贴图(三平面投影) · 透明度；选中对象后改档只套该对象） |

### 图层
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| LAYER | LA | 图层（循环当前层） |
| LAYFRZ / LAYTHW | — | 冻结 / 解冻当前层 |
| LAYLCK / LAYULK | — | 锁定 / 解锁当前层 |
| LAYON | — | 打开全部图层 |
| LAYISO / LAYUNISO | — | 图层隔离 / 取消隔离 |
| LAYDEL | — | 删除当前图层 |
| HIDEOBJECTS | — | 隐藏选中对象 |
| UNISOLATEOBJECTS / UNHIDE | — | 结束隐藏 |

### 标注
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| DIMLINEAR / DIMLIN | DLI | 线性标注（轴对齐，量 X/Y） |
| DIMALIGNED / DIMALI | DAL | 对齐标注（平行测线，量真距） |
| DIMRADIUS | DRA | 半径标注 |
| DIMDIAMETER / DIMDIA | DDI | 直径标注 |
| DIMANGULAR / DIMANG | DAN | 角度标注 |
| DIMCONTINUE | DCO | 连续标注 |
| DIMORDINATE | DOR | 坐标标注 |
| DIMSTYLE / DDIM | D / DST | 标注样式 |

### 特性 / 查询
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| PROPERTIES / DDMODIFY | PR / CH / MO | 特性 |
| LIST | LI / LS | 列表（退化为特性面板） |
| DIST | DI | 距离 |
| AREA | AA | 面积 |
| MEASUREGEOM | MEA | 快速测量 |
| COLOR / COLOUR / DDCOLOR | COL | 颜色 |
| LINETYPE / LTYPE / DDLTYPE | LT | 线型 |

### 选择 / 剪贴板
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| QSELECT | — | 快速选择 |
| FILTER | FI | 快速选择（过滤） |
| SELECTSIMILAR | — | 选择类似 |
| GROUP | G | 创建选择集 |
| COPYCLIP / CUTCLIP | — | 复制到剪贴板 / 剪切 |
| PASTECLIP | — | 粘贴（提示指定插入点，同 AutoCAD） |
| PASTEORIG | — | 原坐标粘贴 |

### 草图设置
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| OSNAP / DDOSNAP | OS | 对象捕捉开关 |
| ORTHO | — | 正交开关 |
| GRID | — | 栅格开关 |
| SNAP | SN | 栅格捕捉开关 |

### 三维
| AutoCAD | 缩写 | 本系统功能 |
|---|---|---|
| BOX / SPHERE | — | 立方体 / 球体 |
| CYLINDER | CYL | 圆柱 |
| UNION | UNI | 布尔-并集 |
| SUBTRACT | SU | 布尔-差集 |
| INTERSECT | IN | 布尔-交集 |
| LOFT | — | 侧面三角网（两线放样） |
| SECTION | SEC | 创建剖面 |

## 顺带修掉的三处不一致

| 处 | 原先 | 现在 | 理由 |
|---|---|---|---|
| 半径标注 | `DIMRADIAL` | `DIMRADIUS`（旧名保留兼容） | AutoCAD 没有 DIMRADIAL 这个命令 |
| 对齐标注 | `DIMALIGNED` 与 `DIMLINEAR` 都落到线性标注 | 拆开：`DIMALIGNED` → 平行测线量真距 | 二者在 AutoCAD 是两条不同命令，原先打 DIMALIGNED 得到的其实是线性标注 |
| 粘贴 | `PASTECLIP` 与 `PASTEORIG` 都按原坐标粘 | `PASTECLIP` 提示指定插入点，`PASTEORIG` 按原坐标 | AutoCAD 语义；中文按钮「粘贴 / 原坐标粘贴 / 基点粘贴」维持原样 |

## 不列入表中的两类

**一、AutoCAD 有、本系统没有的命令** —— 不做占位别名，免得补全里出现打了没反应的名字：
`ARRAY` `FILLET` `CHAMFER` `STRETCH` `SPLINE` `ELLIPSE` `XLINE` `RAY` `DONUT` `REVCLOUD`
`BLOCK` `INSERT` `WBLOCK` `TABLE` `PEDIT` `LENGTHEN` `ALIGN` `DIVIDE` `MEASURE` `MATCHPROP`
`PURGE` `AUDIT` `PLOT` `LAYOUT` `VPORTS` `UCS` `ID` `EXTRUDE` `REVOLVE` `SWEEP` `SLICE`。

**二、本系统特有、AutoCAD 无对应的命令** —— 保留原名，不硬套 AutoCAD：
滑动多段线 `PLDRAG`/`SPL`、加密多段线 `DENSIFY`、简化 `SIMPLIFY`/`DP`、坑线/台阶/路网/块体/煤质等全部矿山业务命令。

> 已知冲突一处：`SPL` 在本系统是「滑动多段线」，在 AutoCAD 是 `SPLINE`。
> 本系统没有 SPLINE 功能，故未改动；若日后要严格对齐，应把 `SPL` 空出来，只留 `PLDRAG`。
