using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

// ── 快速选择(QSELECT) —— 忠实移植原版 PitMine.Platform.Selection ─────────────────
// 原三文件: QuickSelectModel.cs(枚举/特性/快照/条件/结果) + QuickSelectCatalog.cs(类型↔特性表)
// + QuickSelectFilter.cs(纯过滤核)。原设计明言"纯逻辑、不碰 P/Invoke、不碰 UI，可脱 GUI 单测"。
// Kylin 侧新增 QuickSelectSnapshot: 把 2D 场景实体(SceneEntity)造成 EntitySnapshot 喂过滤核。
// 类型 id 沿用原 AcDbEntityType 枚举值(Line1/Circle2/Arc3/Polyline4/Rect7/Polygon8/Text9/Point11)。

/// <summary>过滤取值范围。对应 QSELECT「应用到」。</summary>
public enum QuickSelectScope { WholeDrawing = 0, CurrentSelection = 1 }

/// <summary>比较运算符。对应 QSELECT「运算符」下拉。</summary>
public enum QuickSelectOperator
{
    All = 0, Equals = 1, NotEquals = 2, Greater = 3, Less = 4,
    GreaterOrEqual = 5, LessOrEqual = 6,
    /// <summary>通配匹配(* 任意多字符、? 单字符)，仅文本特性可用。</summary>
    Wildcard = 7,
}

/// <summary>命中对象进新选集还是被排除。对应「如何应用」。</summary>
public enum QuickSelectApplyMode { Include = 0, Exclude = 1 }

/// <summary>特性值类型，决定可用运算符与比法。</summary>
public enum QuickSelectValueKind { Text = 0, Number = 1, Boolean = 2 }

/// <summary>特性值来源(Core=粗筛四件套恒有; Json=需逐条拉)。</summary>
public enum QuickSelectSource { Core = 0, Json = 1 }

/// <summary>一个可筛的特性。</summary>
public sealed record QuickSelectProperty(
    string Key, string DisplayName, QuickSelectValueKind Kind, QuickSelectSource Source);

/// <summary>过滤器看到的一个实体。类型/图层/颜色/线宽恒有; Extended 用到 Json 特性时才填。</summary>
public sealed class EntitySnapshot
{
    public ulong Handle { get; init; }
    public int TypeId { get; init; }
    public string LayerName { get; init; } = string.Empty;
    public uint ColorIndex { get; init; }
    public float Lineweight { get; init; }
    /// <summary>摊平后的 Json 特性包; null = 没拉过(任何 Json 特性条件判不匹配)。</summary>
    public IReadOnlyDictionary<string, string>? Extended { get; set; }
}

/// <summary>一次快速选择的完整条件。</summary>
public sealed class QuickSelectCriteria
{
    public QuickSelectScope Scope { get; set; } = QuickSelectScope.WholeDrawing;
    /// <summary>对象类型(AcDbEntityType 值)。null = 所有图元。</summary>
    public int? TypeId { get; set; }
    /// <summary>特性键。null/空 = 不按特性筛(等价 All)。</summary>
    public string? PropertyKey { get; set; }
    public QuickSelectOperator Operator { get; set; } = QuickSelectOperator.Equals;
    public string Value { get; set; } = string.Empty;
    public QuickSelectApplyMode ApplyMode { get; set; } = QuickSelectApplyMode.Include;
    /// <summary>true = 并入当前选集; false = 顶替。</summary>
    public bool AppendToCurrentSelection { get; set; }
    public bool NeedsExtendedProperties =>
        Operator != QuickSelectOperator.All
        && !string.IsNullOrEmpty(PropertyKey)
        && QuickSelectCatalog.SourceOf(PropertyKey!) == QuickSelectSource.Json;
}

/// <summary>一次快速选择的结果。</summary>
public sealed class QuickSelectResult
{
    public ulong[] Handles { get; init; } = Array.Empty<ulong>();
    public int Examined { get; init; }
    public bool AppendToCurrentSelection { get; init; }
}

/// <summary>「对象类型 → 可筛特性」表(QSELECT 两个下拉的数据源)。忠实原 QuickSelectCatalog。</summary>
public static class QuickSelectCatalog
{
    public const int TypeUnknown = 0, TypeLine = 1, TypeCircle = 2, TypeArc = 3, TypePolyline = 4,
        TypeBlockReference = 5, TypeTriangleMesh = 6, TypeRectangle = 7, TypePolygon = 8,
        TypeText = 9, TypeMText = 10, TypePoint = 11, TypeHatch = 12, TypeDimension = 13,
        TypeAlignedDimension = 14, TypeRadialDimension = 15, TypeWorkLine = 16, TypeWorkLineGroup = 17;

    private static readonly Dictionary<int, string> s_typeNames = new()
    {
        [TypeUnknown] = "未知", [TypeLine] = "直线", [TypeCircle] = "圆", [TypeArc] = "圆弧",
        [TypePolyline] = "多段线", [TypeBlockReference] = "块参照", [TypeTriangleMesh] = "三角网",
        [TypeRectangle] = "矩形", [TypePolygon] = "多边形", [TypeText] = "单行文字", [TypeMText] = "多行文字",
        [TypePoint] = "点", [TypeHatch] = "填充", [TypeDimension] = "标注", [TypeAlignedDimension] = "对齐标注",
        [TypeRadialDimension] = "半径标注", [TypeWorkLine] = "工作线", [TypeWorkLineGroup] = "工作线组",
    };

    private static readonly QuickSelectProperty[] s_common =
    {
        new("layer", "图层", QuickSelectValueKind.Text, QuickSelectSource.Core),
        new("color", "颜色", QuickSelectValueKind.Number, QuickSelectSource.Core),
        new("lineweight", "线宽", QuickSelectValueKind.Number, QuickSelectSource.Core),
        new("transparency", "透明度", QuickSelectValueKind.Number, QuickSelectSource.Json),
        new("visible", "可见性", QuickSelectValueKind.Boolean, QuickSelectSource.Json),
    };

    private static readonly Dictionary<int, QuickSelectProperty[]> s_perType = new()
    {
        [TypeLine] = new QuickSelectProperty[]
        {
            new("startPoint.0", "起点 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("startPoint.1", "起点 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("startPoint.2", "起点 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("endPoint.0", "端点 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("endPoint.1", "端点 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("endPoint.2", "端点 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypeCircle] = new QuickSelectProperty[]
        {
            new("center.0", "圆心 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("center.1", "圆心 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("center.2", "圆心 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("radius", "半径", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypeArc] = new QuickSelectProperty[]
        {
            new("center.0", "圆心 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("center.1", "圆心 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("center.2", "圆心 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("radius", "半径", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("startAngle", "起始角", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("endAngle", "终止角", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypePolyline] = new QuickSelectProperty[]
        {
            new("closed", "是否闭合", QuickSelectValueKind.Boolean, QuickSelectSource.Json),
            new("vertexCount", "顶点数", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypeTriangleMesh] = new QuickSelectProperty[]
        {
            new("vertexCount", "顶点数", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("triangleCount", "三角数", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("hasNormals", "含法线", QuickSelectValueKind.Boolean, QuickSelectSource.Json),
            new("basePoint.0", "基点 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("basePoint.1", "基点 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("basePoint.2", "基点 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypeRectangle] = new QuickSelectProperty[]
        {
            new("corner1.0", "角点1 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("corner1.1", "角点1 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("corner1.2", "角点1 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("corner2.0", "角点2 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("corner2.1", "角点2 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("corner2.2", "角点2 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypePoint] = new QuickSelectProperty[]
        {
            new("position.0", "位置 X", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("position.1", "位置 Y", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("position.2", "位置 Z", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypeHatch] = new QuickSelectProperty[]
        {
            new("patternName", "图案名", QuickSelectValueKind.Text, QuickSelectSource.Json),
            new("patternScale", "图案比例", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("patternAngle", "图案角度", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypeText] = new QuickSelectProperty[]
        {
            new("textString", "内容", QuickSelectValueKind.Text, QuickSelectSource.Json),
            new("height", "字高", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("rotation", "旋转角", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
        [TypeMText] = new QuickSelectProperty[]
        {
            new("textString", "内容", QuickSelectValueKind.Text, QuickSelectSource.Json),
            new("lineCount", "行数", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("height", "行高", QuickSelectValueKind.Number, QuickSelectSource.Json),
            new("rotation", "旋转角", QuickSelectValueKind.Number, QuickSelectSource.Json),
        },
    };

    private static readonly Dictionary<string, QuickSelectProperty> s_byKey =
        s_common.Concat(s_perType.Values.SelectMany(v => v))
                .GroupBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public static string TypeName(int typeId) =>
        s_typeNames.TryGetValue(typeId, out var n) ? n : $"类型 {typeId}";

    public static IReadOnlyList<int> AllTypeIds() =>
        s_typeNames.Keys.Where(k => k != TypeUnknown).OrderBy(k => k).ToArray();

    public static IReadOnlyList<QuickSelectProperty> PropertiesFor(int? typeId)
    {
        if (typeId is null) return s_common;
        return s_perType.TryGetValue(typeId.Value, out var extra)
            ? s_common.Concat(extra).ToArray()
            : s_common;
    }

    public static QuickSelectProperty? Find(string key) =>
        key is not null && s_byKey.TryGetValue(key, out var p) ? p : null;

    public static QuickSelectSource SourceOf(string key) =>
        Find(key)?.Source ?? QuickSelectSource.Json;

    public static IReadOnlyList<QuickSelectOperator> OperatorsFor(QuickSelectValueKind kind) => kind switch
    {
        QuickSelectValueKind.Number => new[]
        {
            QuickSelectOperator.Equals, QuickSelectOperator.NotEquals,
            QuickSelectOperator.Greater, QuickSelectOperator.Less,
            QuickSelectOperator.GreaterOrEqual, QuickSelectOperator.LessOrEqual, QuickSelectOperator.All,
        },
        QuickSelectValueKind.Boolean => new[]
        {
            QuickSelectOperator.Equals, QuickSelectOperator.NotEquals, QuickSelectOperator.All,
        },
        _ => new[]
        {
            QuickSelectOperator.Equals, QuickSelectOperator.NotEquals,
            QuickSelectOperator.Wildcard, QuickSelectOperator.All,
        },
    };

    public static string OperatorName(QuickSelectOperator op) => op switch
    {
        QuickSelectOperator.Equals => "=  等于",
        QuickSelectOperator.NotEquals => "<> 不等于",
        QuickSelectOperator.Greater => ">  大于",
        QuickSelectOperator.Less => "<  小于",
        QuickSelectOperator.GreaterOrEqual => ">= 大于等于",
        QuickSelectOperator.LessOrEqual => "<= 小于等于",
        QuickSelectOperator.Wildcard => "*  通配匹配",
        _ => "全部选择",
    };
}

/// <summary>快速选择的过滤核 —— 纯逻辑，可脱 GUI 单测。忠实原 QuickSelectFilter。</summary>
public static class QuickSelectFilter
{
    private const double NumberEpsilon = 1e-6;

    /// <summary>按条件筛一遍候选集。candidates 为 null/空时返回空结果，不抛。</summary>
    public static QuickSelectResult Apply(IReadOnlyList<EntitySnapshot>? candidates, QuickSelectCriteria criteria)
    {
        if (candidates is null || candidates.Count == 0 || criteria is null)
            return new QuickSelectResult
            {
                Handles = Array.Empty<ulong>(),
                Examined = candidates?.Count ?? 0,
                AppendToCurrentSelection = criteria?.AppendToCurrentSelection ?? false,
            };

        var hits = new List<ulong>();
        foreach (var e in candidates)
        {
            if (e is null) continue;
            // 对象类型是【先决条件】: 两种模式下类型不符的都直接出局(并进"命中"再取反是错的)。
            if (criteria.TypeId.HasValue && e.TypeId != criteria.TypeId.Value) continue;
            bool matched = MatchesProperty(e, criteria);
            if (matched == (criteria.ApplyMode == QuickSelectApplyMode.Include)) hits.Add(e.Handle);
        }

        return new QuickSelectResult
        {
            Handles = hits.ToArray(),
            Examined = candidates.Count,
            AppendToCurrentSelection = criteria.AppendToCurrentSelection,
        };
    }

    /// <summary>单条判定: 类型 AND 特性。不能直接取反当 Exclude 判据(见 Apply)。</summary>
    public static bool Matches(EntitySnapshot entity, QuickSelectCriteria criteria)
    {
        if (entity is null || criteria is null) return false;
        if (criteria.TypeId.HasValue && entity.TypeId != criteria.TypeId.Value) return false;
        return MatchesProperty(entity, criteria);
    }

    /// <summary>只判特性这一维(不看对象类型)。Include/Exclude 取反的就是这一维。</summary>
    public static bool MatchesProperty(EntitySnapshot entity, QuickSelectCriteria criteria)
    {
        if (entity is null || criteria is null) return false;
        if (criteria.Operator == QuickSelectOperator.All || string.IsNullOrEmpty(criteria.PropertyKey))
            return true;
        var prop = QuickSelectCatalog.Find(criteria.PropertyKey!);
        if (prop is null) return false;               // 未知特性: 判不匹配
        string? actual = ReadValue(entity, prop);
        if (actual is null) return false;             // 取不到值: 一律不匹配(含 <>)
        return Compare(actual, criteria.Value ?? string.Empty, criteria.Operator, prop.Kind);
    }

    /// <summary>从快照取某特性原始字符串值; 取不到返回 null。</summary>
    public static string? ReadValue(EntitySnapshot entity, QuickSelectProperty prop)
    {
        if (prop.Source == QuickSelectSource.Core)
            return prop.Key switch
            {
                "layer" => entity.LayerName,
                "color" => entity.ColorIndex.ToString(CultureInfo.InvariantCulture),
                "lineweight" => entity.Lineweight.ToString("R", CultureInfo.InvariantCulture),
                "type" => entity.TypeId.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
        if (entity.Extended is null) return null;
        return entity.Extended.TryGetValue(prop.Key, out var v) ? v : null;
    }

    /// <summary>按值类型比较。</summary>
    public static bool Compare(string actual, string expected, QuickSelectOperator op, QuickSelectValueKind kind)
    {
        switch (kind)
        {
            case QuickSelectValueKind.Number:
            {
                if (!TryParseNumber(actual, out double a) || !TryParseNumber(expected, out double b)) return false;
                double tol = NumberEpsilon * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
                return op switch
                {
                    QuickSelectOperator.Equals => Math.Abs(a - b) <= tol,
                    QuickSelectOperator.NotEquals => Math.Abs(a - b) > tol,
                    QuickSelectOperator.Greater => a > b + tol,
                    QuickSelectOperator.Less => a < b - tol,
                    QuickSelectOperator.GreaterOrEqual => a >= b - tol,
                    QuickSelectOperator.LessOrEqual => a <= b + tol,
                    _ => false,
                };
            }
            case QuickSelectValueKind.Boolean:
            {
                if (!TryParseBool(actual, out bool a) || !TryParseBool(expected, out bool b)) return false;
                return op switch
                {
                    QuickSelectOperator.Equals => a == b,
                    QuickSelectOperator.NotEquals => a != b,
                    _ => false,
                };
            }
            default:
                return op switch
                {
                    QuickSelectOperator.Equals => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                    QuickSelectOperator.NotEquals => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
                    QuickSelectOperator.Wildcard => WildcardMatch(actual, expected),
                    _ => false,
                };
        }
    }

    /// <summary>通配匹配: * 任意多字符(含 0), ? 恰好一字符, 其余字面量。大小写不敏感。手写双指针回溯，不走 Regex。</summary>
    public static bool WildcardMatch(string? input, string? pattern)
    {
        input ??= string.Empty;
        pattern ??= string.Empty;
        int i = 0, p = 0, starIdx = -1, matchIdx = 0;
        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEqualsIgnoreCase(pattern[p], input[i]))) { i++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { starIdx = p; matchIdx = i; p++; }
            else if (starIdx >= 0) { p = starIdx + 1; i = ++matchIdx; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool CharEqualsIgnoreCase(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>把实体属性 JSON 摊平成 键→字符串 字典(数组拆下标, 嵌套拆点号路径)。解析失败返回空字典，不抛。</summary>
    public static Dictionary<string, string> FlattenJson(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try { using var doc = JsonDocument.Parse(json); Walk(doc.RootElement, null, result); }
        catch (JsonException) { }
        return result;
    }

    private static void Walk(JsonElement el, string? prefix, Dictionary<string, string> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    Walk(p.Value, prefix is null ? p.Name : $"{prefix}.{p.Name}", sink);
                break;
            case JsonValueKind.Array:
            {
                int idx = 0;
                foreach (var item in el.EnumerateArray()) Walk(item, $"{prefix}.{idx++}", sink);
                break;
            }
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (prefix is not null) sink[prefix] = el.GetBoolean() ? "true" : "false";
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
            default:
                if (prefix is not null) sink[prefix] = el.ToString();
                break;
        }
    }

    private static bool TryParseNumber(string s, out double value) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseBool(string s, out bool value)
    {
        value = false;
        s = s?.Trim() ?? string.Empty;
        if (bool.TryParse(s, out value)) return true;
        if (s is "1" or "是" or "Yes" or "yes") { value = true; return true; }
        if (s is "0" or "否" or "No" or "no") { value = false; return true; }
        return false;
    }

    /// <summary>把条件描述成一行人话(落命令行历史)。</summary>
    public static string Describe(QuickSelectCriteria c, int hitCount, int examined)
    {
        var sb = new StringBuilder();
        sb.Append("快速选择: ");
        sb.Append(c.Scope == QuickSelectScope.WholeDrawing ? "整个图形" : "当前选择集");
        sb.Append(" · ");
        sb.Append(c.TypeId is null ? "所有图元" : QuickSelectCatalog.TypeName(c.TypeId.Value));
        if (c.Operator != QuickSelectOperator.All && !string.IsNullOrEmpty(c.PropertyKey))
        {
            var prop = QuickSelectCatalog.Find(c.PropertyKey!);
            sb.Append(" · ");
            sb.Append(prop?.DisplayName ?? c.PropertyKey);
            sb.Append(' ');
            sb.Append(QuickSelectCatalog.OperatorName(c.Operator).Split(' ')[0]);
            sb.Append(' ');
            sb.Append(c.Value);
        }
        if (c.ApplyMode == QuickSelectApplyMode.Exclude) sb.Append(" · 排除");
        if (c.AppendToCurrentSelection) sb.Append(" · 追加");
        sb.Append($" → 在 {examined} 个对象中选中 {hitCount} 个");
        return sb.ToString();
    }
}

/// <summary>
/// Kylin 侧的过滤核喂料(原版调用方=对话框做的三件 I/O 的托管等价)：
/// 把 2D 场景实体 <see cref="SceneEntity"/> 造成 <see cref="EntitySnapshot"/>。
/// 类型 id 沿用原 AcDbEntityType 枚举值; 场景 2D 故所有 Z=0; Extended 直接填齐(内存态无需 JSON)。
/// Handle = 实体在场景中的下标(调用方据此把命中回映到实体引用)。纯逻辑、可单测。
/// </summary>
public static class QuickSelectSnapshot
{
    /// <summary>Kylin 实体类 → 目录类型 id(与 QuickSelectCatalog 常量对齐)。</summary>
    public static int TypeIdOf(SceneEntity e) => e switch
    {
        LineEntity => QuickSelectCatalog.TypeLine,
        CircleEntity => QuickSelectCatalog.TypeCircle,
        ArcEntity => QuickSelectCatalog.TypeArc,
        PolylineEntity => QuickSelectCatalog.TypePolyline,
        RectEntity => QuickSelectCatalog.TypeRectangle,
        PolygonEntity => QuickSelectCatalog.TypePolygon,
        TextEntity => QuickSelectCatalog.TypeText,
        PointEntity => QuickSelectCatalog.TypePoint,
        _ => QuickSelectCatalog.TypeUnknown,
    };

    /// <summary>RGB(0..1 float) 打包成 0xRRGGBB 整数(color 特性按 Number 比)。</summary>
    public static uint PackColor(SceneEntity e)
    {
        uint r = (uint)Math.Clamp((int)Math.Round(e.Cr * 255f), 0, 255);
        uint g = (uint)Math.Clamp((int)Math.Round(e.Cg * 255f), 0, 255);
        uint b = (uint)Math.Clamp((int)Math.Round(e.Cb * 255f), 0, 255);
        return (r << 16) | (g << 8) | b;
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>造一个实体的快照(含 Extended 全特性)。</summary>
    public static EntitySnapshot From(SceneEntity e, ulong handle)
    {
        var ext = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transparency"] = N(e.Transparency),
            ["visible"] = e.Visible ? "true" : "false",
        };
        switch (e)
        {
            case LineEntity l:
                ext["startPoint.0"] = N(l.X0); ext["startPoint.1"] = N(l.Y0); ext["startPoint.2"] = "0";
                ext["endPoint.0"] = N(l.X1); ext["endPoint.1"] = N(l.Y1); ext["endPoint.2"] = "0";
                break;
            case CircleEntity c:
                ext["center.0"] = N(c.Cx); ext["center.1"] = N(c.Cy); ext["center.2"] = "0";
                ext["radius"] = N(c.Radius);
                break;
            case ArcEntity a:
            {
                var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
                if (cc != null)
                {
                    var (cx, cy, r) = cc.Value;
                    ext["center.0"] = N(cx); ext["center.1"] = N(cy); ext["center.2"] = "0";
                    ext["radius"] = N(r);
                    ext["startAngle"] = N(Math.Atan2(a.Y1 - cy, a.X1 - cx));
                    ext["endAngle"] = N(Math.Atan2(a.Y3 - cy, a.X3 - cx));
                }
                break;
            }
            case PolylineEntity pl:
                ext["closed"] = pl.Closed ? "true" : "false";
                ext["vertexCount"] = pl.Points.Count.ToString(CultureInfo.InvariantCulture);
                break;
            case RectEntity rc:
                ext["corner1.0"] = N(rc.X0); ext["corner1.1"] = N(rc.Y0); ext["corner1.2"] = "0";
                ext["corner2.0"] = N(rc.X1); ext["corner2.1"] = N(rc.Y1); ext["corner2.2"] = "0";
                break;
            case PointEntity p:
                ext["position.0"] = N(p.X); ext["position.1"] = N(p.Y); ext["position.2"] = "0";
                break;
            case TextEntity t:
                ext["textString"] = t.Text ?? "";
                ext["height"] = N(t.Height);
                ext["rotation"] = N(t.Rotation);
                break;
        }
        return new EntitySnapshot
        {
            Handle = handle,
            TypeId = TypeIdOf(e),
            LayerName = e.LayerName ?? "",
            ColorIndex = PackColor(e),
            Lineweight = e.LineWeight,
            Extended = ext,
        };
    }

    /// <summary>把一批实体造成快照(Handle = 下标)。</summary>
    public static List<EntitySnapshot> FromScene(IReadOnlyList<SceneEntity> entities)
    {
        var list = new List<EntitySnapshot>(entities.Count);
        for (int i = 0; i < entities.Count; i++) list.Add(From(entities[i], (ulong)i));
        return list;
    }

    /// <summary>
    /// 范围里**真有的**类型 id（按目录顺序，去重）。QSELECT「对象类型」下拉的数据源。
    /// 忠实原 <c>QuickSelectService.PresentTypeIds</c> 的用意：列图上根本没有的类型，
    /// 用户选中后只能得到空集 —— 那是白给一次挫败。
    /// </summary>
    public static IReadOnlyList<int> PresentTypeIds(IReadOnlyList<EntitySnapshot>? snaps)
    {
        if (snaps == null || snaps.Count == 0) return Array.Empty<int>();
        var present = new HashSet<int>();
        foreach (var s in snaps) present.Add(s.TypeId);
        var ordered = new List<int>();
        foreach (int id in QuickSelectCatalog.AllTypeIds()) if (present.Remove(id)) ordered.Add(id);
        foreach (int id in present) ordered.Add(id);   // 目录里没登记的(不该有)也别丢
        return ordered;
    }

    /// <summary>范围里出现过的图层名（去重、按名排序）。QSELECT「值」下拉在特性=图层时的候选。
    /// 让人手打层名是快速选择最容易白跑一趟的地方 —— 差一个字就是空集。</summary>
    public static IReadOnlyList<string> PresentLayerNames(IReadOnlyList<EntitySnapshot>? snaps)
    {
        if (snaps == null || snaps.Count == 0) return Array.Empty<string>();
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in snaps) if (!string.IsNullOrEmpty(s.LayerName)) set.Add(s.LayerName);
        return set.ToList();
    }
}
