using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// AutoCAD 命令名/缩写 → 本系统既有命令 的对照表与解析器（纯逻辑，可单测）。
///
/// 背景：本系统的功能按钮/命令链是中文名（忠实原 PitMine3D 功能区），命令行原先只认
/// 少量英文名，且个别名字与 AutoCAD 不一致（如半径标注写成 DIMRADIAL，AutoCAD 是 DIMRADIUS）。
/// 这里把「与 AutoCAD 同功能」的那部分统一到 AutoCAD 的标准命令名 + acad.pgp 缩写，
/// 使 CAD 用户的肌肉记忆（L / PL / C / REC / M / CO / TR / EX / Z / RE / LA / DLI …）直接可用。
///
/// 只做映射，不新增功能：表里每一条的 Target 都指向系统里**已经存在**的命令。
/// AutoCAD 有、本系统没有的命令（ARRAY / FILLET / CHAMFER / STRETCH / SPLINE / BLOCK …）一律不列，
/// 免得补全里出现打了没反应的名字。
/// </summary>
public static class AcadCommands
{
    /// <summary>一条对照：AutoCAD 命令名 → 本系统命令 token（Target=null 表示系统里就叫这个名字）。</summary>
    public readonly record struct Entry(string Acad, string? Target, string Zh, string Group);

    // ── 对照表 ────────────────────────────────────────────────────────────────
    // Target = null: 命令行 switch 里已经就是这个 AutoCAD 名字, 无需改写。
    // Target != null: 改写成系统既有 token（英文 switch 名 或 中文命令链名）。
    private static readonly Entry[] Entries =
    {
        // ── 文件 ──
        new("NEW",        null,         "新建",           "文件"),
        new("OPEN",       null,         "打开",           "文件"),
        new("SAVE",       null,         "保存",           "文件"),
        new("QSAVE",      "SAVE",       "保存",           "文件"),
        new("SAVEAS",     "另存为",      "另存为",         "文件"),
        new("IMPORT",     "导入",        "导入(DXF)",      "文件"),
        new("IMP",        "导入",        "导入(DXF)",      "文件"),
        new("EXPORT",     "导出",        "导出(DXF)",      "文件"),
        new("EXP",        "导出",        "导出(DXF)",      "文件"),
        new("OPTIONS",    null,         "选项",           "文件"),
        new("OP",         null,         "选项",           "文件"),
        new("PREFERENCES","OPTIONS",    "选项",           "文件"),
        new("UNDO",       null,         "撤销",           "文件"),
        new("U",          "UNDO",       "撤销",           "文件"),
        new("REDO",       null,         "重做",           "文件"),
        new("MREDO",      "REDO",       "重做",           "文件"),
        new("HELP",       "帮助文档",    "帮助",           "文件"),
        new("?",          "帮助文档",    "帮助",           "文件"),

        // ── 绘图 ──
        new("LINE",       null,         "直线",           "绘图"),
        new("L",          "LINE",       "直线",           "绘图"),
        new("PLINE",      null,         "多段线",         "绘图"),
        new("PL",         "PLINE",      "多段线",         "绘图"),
        new("CIRCLE",     null,         "圆",             "绘图"),
        new("C",          "CIRCLE",     "圆",             "绘图"),
        new("ARC",        null,         "圆弧",           "绘图"),
        new("A",          "ARC",        "圆弧",           "绘图"),
        new("RECTANG",    null,         "矩形",           "绘图"),
        new("REC",        "RECTANG",    "矩形",           "绘图"),
        new("RECTANGLE",  "RECTANG",    "矩形",           "绘图"),
        new("POLYGON",    null,         "正多边形",       "绘图"),
        new("POL",        "POLYGON",    "正多边形",       "绘图"),
        new("POINT",      null,         "点",             "绘图"),
        new("PO",         "POINT",      "点",             "绘图"),
        new("TEXT",       null,         "单行文字",       "绘图"),
        new("DTEXT",      "TEXT",       "单行文字",       "绘图"),
        new("DT",         "TEXT",       "单行文字",       "绘图"),
        new("MTEXT",      "多行文字",    "多行文字",       "绘图"),
        new("MT",         "多行文字",    "多行文字",       "绘图"),
        new("T",          "多行文字",    "多行文字",       "绘图"),
        new("HATCH",      "图案填充",    "图案填充",       "绘图"),
        new("H",          "图案填充",    "图案填充",       "绘图"),
        new("BHATCH",     "图案填充",    "图案填充",       "绘图"),
        new("BH",         "图案填充",    "图案填充",       "绘图"),

        // ── 修改 ──
        new("ERASE",      null,         "删除",           "修改"),
        new("E",          "ERASE",      "删除",           "修改"),
        new("MOVE",       null,         "移动",           "修改"),
        new("M",          "MOVE",       "移动",           "修改"),
        new("COPY",       null,         "复制",           "修改"),
        new("CO",         "COPY",       "复制",           "修改"),
        new("CP",         "COPY",       "复制（选择态=交叉多边形圈选）", "修改"),
        new("MIRROR",     null,         "镜像",           "修改"),
        new("MI",         "MIRROR",     "镜像",           "修改"),
        new("OFFSET",     null,         "偏移",           "修改"),
        new("O",          "OFFSET",     "偏移",           "修改"),
        new("ROTATE",     null,         "旋转",           "修改"),
        new("RO",         "ROTATE",     "旋转",           "修改"),
        new("SCALE",      null,         "缩放",           "修改"),
        new("SC",         "SCALE",      "缩放",           "修改"),
        new("TRIM",       null,         "修剪",           "修改"),
        new("TR",         "TRIM",       "修剪",           "修改"),
        new("EXTEND",     null,         "延伸",           "修改"),
        new("EX",         "EXTEND",     "延伸",           "修改"),
        new("BREAK",      null,         "打断",           "修改"),
        new("BR",         "BREAK",      "打断",           "修改"),
        new("EXPLODE",    null,         "分解",           "修改"),
        new("X",          "EXPLODE",    "分解",           "修改"),
        new("JOIN",       "连接多段线",  "连接多段线",     "修改"),
        new("TEXTEDIT",   "编辑文字",    "编辑文字",       "修改"),
        new("DDEDIT",     "编辑文字",    "编辑文字",       "修改"),
        new("ED",         "编辑文字",    "编辑文字",       "修改"),
        new("J",          "连接多段线",  "连接多段线",     "修改"),
        new("OVERKILL",   "删除重复线",  "删除重复线",     "修改"),

        // ── 视图 ──
        new("ZOOM",       null,         "范围缩放",       "视图"),
        new("Z",          "ZOOM",       "范围缩放",       "视图"),
        new("PAN",        "平移",        "平移",           "视图"),
        new("P",          "平移",        "平移（选择态=上次选择集）", "视图"),
        new("REGEN",      null,         "重生成/刷新",    "视图"),
        new("RE",         "REGEN",      "重生成/刷新",    "视图"),
        new("REGENALL",   "REGEN",      "重生成/刷新",    "视图"),
        new("REA",        "REGEN",      "重生成/刷新",    "视图"),
        new("REDRAW",     "REGEN",      "重画",           "视图"),
        new("R",          "REGEN",      "重画",           "视图"),
        new("3DORBIT",    null,         "三维轨道",       "视图"),
        new("3DO",        "3DORBIT",    "三维轨道",       "视图"),
        new("ORBIT",      "3DORBIT",    "三维轨道",       "视图"),
        new("PLAN",       "俯视",        "俯视（平面视图）", "视图"),
        new("TOP",        "俯视",        "俯视",           "视图"),
        new("BOTTOM",     "仰视",        "仰视",           "视图"),
        new("FRONT",      "主视",        "主视",           "视图"),
        new("BACK",       "后视",        "后视",           "视图"),
        new("LEFT",       "左视",        "左视",           "视图"),
        new("RIGHT",      "右视",        "右视",           "视图"),
        new("SWISO",      "西南等轴测",  "西南等轴测",     "视图"),
        new("SEISO",      "东南等轴测",  "东南等轴测",     "视图"),
        new("NEISO",      "东北等轴测",  "东北等轴测",     "视图"),
        new("NWISO",      "西北等轴测",  "西北等轴测",     "视图"),
        new("VSCURRENT",  "渲染配置",    "视觉样式/渲染配置", "视图"),
        new("VS",         "渲染配置",    "视觉样式/渲染配置", "视图"),
        new("SHADEMODE",  "渲染配置",    "视觉样式/渲染配置", "视图"),

        // ── 图层 ──
        new("LAYER",      null,         "图层（循环当前层）", "图层"),
        new("LA",         "LAYER",      "图层（循环当前层）", "图层"),
        new("LAYFRZ",     null,         "冻结当前层",     "图层"),
        new("LAYTHW",     null,         "解冻当前层",     "图层"),
        new("LAYLCK",     null,         "锁定当前层",     "图层"),
        new("LAYULK",     null,         "解锁当前层",     "图层"),
        new("LAYON",      null,         "打开全部图层",   "图层"),
        new("LAYISO",     "图层隔离",    "图层隔离",       "图层"),
        new("LAYUNISO",   "取消隔离",    "取消图层隔离",   "图层"),
        new("LAYDEL",     "删除图层",    "删除当前图层",   "图层"),
        new("HIDEOBJECTS","隐藏对象",    "隐藏选中对象",   "图层"),
        new("UNISOLATEOBJECTS", "结束隐藏", "结束隐藏",    "图层"),
        new("UNHIDE",     "结束隐藏",    "结束隐藏",       "图层"),

        // ── 标注 ──
        new("DIMLINEAR",  null,         "线性标注",       "标注"),
        new("DIMLIN",     "DIMLINEAR",  "线性标注",       "标注"),
        new("DLI",        "DIMLINEAR",  "线性标注",       "标注"),
        new("DIMALIGNED", null,         "对齐标注",       "标注"),
        new("DIMALI",     "DIMALIGNED", "对齐标注",       "标注"),
        new("DAL",        "DIMALIGNED", "对齐标注",       "标注"),
        new("DIMRADIUS",  null,         "半径标注",       "标注"),
        new("DRA",        "DIMRADIUS",  "半径标注",       "标注"),
        new("DIMDIAMETER","直径标注",    "直径标注",       "标注"),
        new("DIMDIA",     "直径标注",    "直径标注",       "标注"),
        new("DDI",        "直径标注",    "直径标注",       "标注"),
        new("DIMANGULAR", "角度标注",    "角度标注",       "标注"),
        new("DIMANG",     "角度标注",    "角度标注",       "标注"),
        new("DAN",        "角度标注",    "角度标注",       "标注"),
        new("DIMCONTINUE",null,         "连续标注",       "标注"),
        new("DCO",        "DIMCONTINUE","连续标注",       "标注"),
        new("DIMORDINATE","坐标标注",    "坐标标注",       "标注"),
        new("DOR",        "坐标标注",    "坐标标注",       "标注"),
        new("DIMSTYLE",   "标注样式",    "标注样式",       "标注"),
        new("DST",        "标注样式",    "标注样式",       "标注"),
        new("DDIM",       "标注样式",    "标注样式",       "标注"),
        new("D",          "标注样式",    "标注样式",       "标注"),

        // ── 特性 / 查询 ──
        new("PROPERTIES", null,         "特性",           "特性"),
        new("PR",         "PROPERTIES", "特性",           "特性"),
        new("CH",         "PROPERTIES", "特性",           "特性"),
        new("MO",         "PROPERTIES", "特性",           "特性"),
        new("DDMODIFY",   "PROPERTIES", "特性",           "特性"),
        new("LIST",       "PROPERTIES", "列表（特性）",   "特性"),
        new("LI",         "PROPERTIES", "列表（特性）",   "特性"),
        new("LS",         "PROPERTIES", "列表（特性）",   "特性"),
        new("DIST",       null,         "距离",           "特性"),
        new("DI",         "DIST",       "距离",           "特性"),
        new("AREA",       null,         "面积",           "特性"),
        new("AA",         "AREA",       "面积",           "特性"),
        new("MEASUREGEOM","快速测量",    "快速测量",       "特性"),
        new("MEA",        "快速测量",    "快速测量",       "特性"),
        new("COLOR",      "颜色",        "颜色",           "特性"),
        new("COLOUR",     "颜色",        "颜色",           "特性"),
        new("COL",        "颜色",        "颜色",           "特性"),
        new("DDCOLOR",    "颜色",        "颜色",           "特性"),
        new("LINETYPE",   "线型",        "线型",           "特性"),
        new("LTYPE",      "线型",        "线型",           "特性"),
        new("LT",         "线型",        "线型",           "特性"),
        new("DDLTYPE",    "线型",        "线型",           "特性"),

        // ── 选择 / 剪贴板 ──
        new("QSELECT",    null,         "快速选择",       "选择"),
        new("FILTER",     "快速选择",    "快速选择（过滤）", "选择"),
        new("FI",         "快速选择",    "快速选择（过滤）", "选择"),
        new("SELECTSIMILAR", null,      "选择类似",       "选择"),
        new("GROUP",      null,         "创建选择集",     "选择"),
        new("G",          "GROUP",      "创建选择集",     "选择"),
        new("COPYCLIP",   null,         "复制到剪贴板",   "选择"),
        new("CUTCLIP",    null,         "剪切",           "选择"),
        new("PASTECLIP",  null,         "粘贴（指定插入点）", "选择"),
        new("PASTEORIG",  null,         "原坐标粘贴",     "选择"),

        // ── 草图设置 ──
        new("OSNAP",      "对象捕捉",    "对象捕捉开关",   "草图"),
        new("OS",         "对象捕捉",    "对象捕捉开关",   "草图"),
        new("DDOSNAP",    "对象捕捉",    "对象捕捉开关",   "草图"),
        new("ORTHO",      null,         "正交开关",       "草图"),
        new("GRID",       null,         "栅格开关",       "草图"),
        new("SNAP",       null,         "栅格捕捉开关",   "草图"),
        new("SN",         "SNAP",       "栅格捕捉开关",   "草图"),

        // ── 三维实体 ──
        new("BOX",        null,         "立方体",         "三维"),
        new("SPHERE",     null,         "球体",           "三维"),
        new("CYLINDER",   "圆柱",        "圆柱",           "三维"),
        new("CYL",        "圆柱",        "圆柱",           "三维"),
        new("UNION",      "布尔-并集",   "布尔并集",       "三维"),
        new("UNI",        "布尔-并集",   "布尔并集",       "三维"),
        new("SUBTRACT",   "布尔-差集",   "布尔差集",       "三维"),
        new("SU",         "布尔-差集",   "布尔差集",       "三维"),
        new("INTERSECT",  "布尔-交集",   "布尔交集",       "三维"),
        new("IN",         "布尔-交集",   "布尔交集",       "三维"),
        new("LOFT",       null,         "侧面三角网（放样）", "三维"),
        new("SECTION",    "创建剖面",    "创建剖面",       "三维"),
        new("SEC",        "创建剖面",    "创建剖面",       "三维"),
    };

    /// <summary>整表（供「命令别名」列表与帮助窗口）。</summary>
    public static IReadOnlyList<Entry> Table => Entries;

    private static readonly Dictionary<string, string> Rewrite =
        Entries.Where(e => e.Target != null)
               .GroupBy(e => e.Acad, StringComparer.OrdinalIgnoreCase)   // 同名只取首条(表内顺序即优先级)
               .ToDictionary(g => g.Key, g => g.First().Target!, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Known =
        new(Entries.Select(e => e.Acad), StringComparer.OrdinalIgnoreCase);

    /// <summary>所有支持的 AutoCAD 命令名（补全候选用），按表内顺序去重。</summary>
    public static IReadOnlyList<string> Names { get; } =
        Entries.Select(e => e.Acad).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// 「选择对象」阶段下按 AutoCAD 选择选项解释的关键字：
    /// L=上次画的(Last)、P=上次选择集(Previous)、WP=窗口多边形、CP=交叉多边形、ALL=全部。
    /// 这批字母在 AutoCAD 命令提示符下是别的意思（L=LINE、P=PAN、CP=COPY），故按上下文分流。
    /// </summary>
    private static readonly Dictionary<string, string> SelectionKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["L"] = "LAST", ["P"] = "PREVIOUS", ["WP"] = "WP", ["CP"] = "CP", ["ALL"] = "ALL",
        };

    /// <summary>该 token 是否为本表认得的 AutoCAD 命令名（不含参数）。</summary>
    public static bool IsAcadCommand(string token) =>
        !string.IsNullOrWhiteSpace(token) && Known.Contains(token.Trim());

    /// <summary>
    /// 把用户键入的一行（命令 + 可选参数）里的**命令词**换成系统既有 token；参数原样保留。
    /// 不认识的原样返回（后面仍走中文命令链/绘图工具）。
    /// </summary>
    /// <param name="typed">命令行原文，如 "REC" / "H 45 2" / "距离"。</param>
    /// <param name="selectingObjects">当前是否处于编辑命令的「选择对象」阶段。</param>
    public static string Resolve(string typed, bool selectingObjects = false)
    {
        if (string.IsNullOrWhiteSpace(typed)) return typed;
        string s = typed.Trim();
        int sp = s.IndexOfAny(new[] { ' ', '\t' });
        string head = sp < 0 ? s : s.Substring(0, sp);
        string rest = sp < 0 ? "" : s.Substring(sp);

        if (selectingObjects && SelectionKeywords.TryGetValue(head, out string? sel))
            return sel + rest;

        return Rewrite.TryGetValue(head, out string? target) ? target + rest : s;
    }

    /// <summary>
    /// 命令行「空格 = 回车」（AutoCAD 习惯）的例外表：这些命令的参数只能跟在同一行，
    /// 空格得留给参数，不能当回车。首词命中即不触发空格提交。
    ///
    /// 会在命令行上逐项问参数的命令（图案填充 / SOR / ROR / 正多边形 / 立方体…）**不在**表里 ——
    /// 它们和 AutoCAD 一样：空格直接执行，参数随后一项项问。
    /// </summary>
    private static readonly HashSet<string> ArgTaking =
        new(StringComparer.OrdinalIgnoreCase) { "QSELECT", "QSEL" };

    /// <summary>
    /// 该行是否可以用空格当回车提交：纯 ASCII（中文命令留给回车，免与输入法抢空格）、
    /// 行内尚无空格、且不是要在同一行跟参数的命令。
    /// </summary>
    public static bool SpaceSubmits(string typed)
    {
        if (string.IsNullOrEmpty(typed)) return true;                 // 空行 + 空格 = 重复上次命令
        foreach (char c in typed) if (c > 127 || c == ' ' || c == '\t') return false;
        return !ArgTaking.Contains(typed.Trim());
    }
}
