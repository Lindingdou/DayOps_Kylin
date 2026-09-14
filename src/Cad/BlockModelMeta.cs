using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
//  块体模型元数据 —— 忠实移植原 BlockModelLib.Domain 的 BlockModelSpec / BlockModel / PropertySchema /
//  DisplayStyle / FilterSet / ColormapSampler / CategoricalPalette / DefaultPalette / PolygonRegion。
//  Kylin 的块体 = List<BlockModel.Block> + 逐块属性表 Dictionary<string,double[]>(并行索引)；
//  本文件在其上补齐 名称/原点/块尺寸/维度/属性列/显示样式/筛选/删除集，纯逻辑、可单测，不碰 UI。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>存储模式（与原 BlkLib 三种后端对应；Kylin 仅记录）。</summary>
public enum BlockStorageMode : byte { Dense = 0, Sparse = 1, Adaptive = 2 }

public static class BlockStorageModeExtensions
{
    public static string ToChineseLabel(this BlockStorageMode m) => m switch
    {
        BlockStorageMode.Dense => "致密 (Dense)",
        BlockStorageMode.Sparse => "稀疏 (Sparse)",
        BlockStorageMode.Adaptive => "自适应 (Adaptive)",
        _ => m.ToString(),
    };
    public static string GetHint(this BlockStorageMode m) => m switch
    {
        BlockStorageMode.Dense => "致密煤层、规则金属矿、网格 ≤5 千万",
        BlockStorageMode.Sparse => "大型矿区、约束体后稀疏化（默认）",
        BlockStorageMode.Adaptive => "煤层尖灭、矿体边界精细",
        _ => "",
    };
}

/// <summary>属性列的数据类型（对应 PMB PropertySchema.dataType）。</summary>
public enum BlockPropertyType : byte { Float = 0, UInt32 = 1, Int32 = 2 }

public static class BlockPropertyTypeExtensions
{
    public static string ToChineseLabel(this BlockPropertyType t) => t switch
    {
        BlockPropertyType.Float => "浮点 (Float)",
        BlockPropertyType.UInt32 => "无符号整数 (UInt32)",
        BlockPropertyType.Int32 => "整数 (Int32)",
        _ => t.ToString(),
    };
    public static int ByteSize(this BlockPropertyType t) => 4;
}

/// <summary>一个属性列的 schema 定义。</summary>
public sealed class BlockPropertyColumn
{
    public string Name { get; set; } = "";
    public BlockPropertyType DataType { get; set; } = BlockPropertyType.Float;
    public string Unit { get; set; } = "";
    public double DefaultValue { get; set; }
    public string Description { get; set; } = "";
    public bool IsCategorical { get; set; }
    /// <summary>分类属性的「码→名」标签（索引 = 类别码）。null = 无标签。</summary>
    public IReadOnlyList<string>? CategoryLabels { get; set; }

    /// <summary>列名合法性：字母/下划线开头，只含字母数字下划线。</summary>
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        for (int i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }

    public BlockPropertyColumn Clone() => new()
    {
        Name = Name, DataType = DataType, Unit = Unit, DefaultValue = DefaultValue,
        Description = Description, IsCategorical = IsCategorical, CategoryLabels = CategoryLabels,
    };
    public override string ToString() => Name;
}

/// <summary>边线显示模式。</summary>
public enum BlockEdgeMode : byte { AutoFromFill = 0, Fixed = 1, HiddenUnlessSelected = 2 }

/// <summary>预设色带（与 PMB DisplayStyle.colormapDefault 对齐）。</summary>
public enum BlockColormapPreset : byte { Viridis = 0, Magma = 1, Plasma = 2, RdYlBu = 3, Jet = 4, Gray = 5, Turbo = 6 }

public static class BlockColormapPresetExtensions
{
    public static string ToChineseLabel(this BlockColormapPreset p) => p switch
    {
        BlockColormapPreset.Viridis => "Viridis (科学可视化标准)",
        BlockColormapPreset.Magma => "Magma (黑-紫-红-黄)",
        BlockColormapPreset.Plasma => "Plasma (蓝-紫-黄)",
        BlockColormapPreset.RdYlBu => "RdYlBu (矿业传统红-黄-蓝)",
        BlockColormapPreset.Jet => "Jet (蓝-青-黄-红)",
        BlockColormapPreset.Gray => "Gray (灰阶)",
        BlockColormapPreset.Turbo => "Turbo (改进 Jet)",
        _ => p.ToString(),
    };
    /// <summary>浏览器下拉的短标签（原 BlockModelBrowserView ColormapPresetList）。</summary>
    public static string ToShortLabel(this BlockColormapPreset p) => p switch
    {
        BlockColormapPreset.Viridis => "Viridis (科学标准)",
        BlockColormapPreset.Magma => "Magma (黑紫红黄)",
        BlockColormapPreset.Plasma => "Plasma (蓝紫黄)",
        BlockColormapPreset.RdYlBu => "RdYlBu (矿业红黄蓝)",
        BlockColormapPreset.Jet => "Jet (蓝青黄红)",
        BlockColormapPreset.Gray => "Gray (灰阶)",
        BlockColormapPreset.Turbo => "Turbo (改进 Jet)",
        _ => p.ToString(),
    };
}

/// <summary>预设色带采样器（忠实原 ColormapSampler：5~11 个 anchor 分段线性插值）。</summary>
public static class BlockColormap
{
    public static (byte r, byte g, byte b) Sample(BlockColormapPreset preset, double t)
    {
        if (double.IsNaN(t)) t = 0;
        t = Math.Clamp(t, 0, 1);
        return preset switch
        {
            BlockColormapPreset.RdYlBu => Lerp(RdYlBu, t),
            BlockColormapPreset.Viridis => Lerp(Viridis, t),
            BlockColormapPreset.Magma => Lerp(Magma, t),
            BlockColormapPreset.Plasma => Lerp(Plasma, t),
            BlockColormapPreset.Jet => Lerp(Jet, t),
            BlockColormapPreset.Gray => ((byte)(t * 255), (byte)(t * 255), (byte)(t * 255)),
            BlockColormapPreset.Turbo => Lerp(Turbo, t),
            _ => ((byte)(t * 255), (byte)(t * 255), (byte)(t * 255)),
        };
    }

    private static (byte, byte, byte) Lerp((byte r, byte g, byte b)[] stops, double t)
    {
        int n = stops.Length;
        double scaled = t * (n - 1);
        int i0 = (int)Math.Floor(scaled);
        int i1 = Math.Min(i0 + 1, n - 1);
        double f = scaled - i0;
        var a = stops[i0]; var b = stops[i1];
        return ((byte)(a.r + (b.r - a.r) * f), (byte)(a.g + (b.g - a.g) * f), (byte)(a.b + (b.b - a.b) * f));
    }

    private static readonly (byte, byte, byte)[] RdYlBu =
    { (0x31,0x5B,0xAE),(0x74,0xAD,0xD1),(0xAB,0xD9,0xE9),(0xE0,0xF3,0xF8),(0xFF,0xFF,0xBF),(0xFE,0xE0,0x90),(0xFD,0xAE,0x61),(0xF4,0x6D,0x43),(0xD7,0x30,0x27) };
    private static readonly (byte, byte, byte)[] Viridis =
    { (0x44,0x01,0x54),(0x46,0x29,0x86),(0x36,0x4B,0x8A),(0x27,0x6A,0x8E),(0x1F,0x88,0x8E),(0x1F,0xA1,0x88),(0x47,0xC1,0x6A),(0xA8,0xDB,0x34),(0xFD,0xE7,0x25) };
    private static readonly (byte, byte, byte)[] Magma =
    { (0x00,0x00,0x04),(0x1E,0x0F,0x4D),(0x4E,0x12,0x82),(0x82,0x1D,0x82),(0xB5,0x36,0x79),(0xE0,0x5C,0x5D),(0xF7,0x91,0x53),(0xFE,0xC7,0x88),(0xFC,0xFD,0xBF) };
    private static readonly (byte, byte, byte)[] Plasma =
    { (0x0D,0x08,0x87),(0x47,0x03,0x9F),(0x7E,0x03,0xA8),(0xA9,0x21,0x9A),(0xCC,0x47,0x7B),(0xE6,0x6C,0x5C),(0xF8,0x94,0x40),(0xFC,0xCE,0x25),(0xF0,0xF9,0x21) };
    private static readonly (byte, byte, byte)[] Jet =
    { (0x00,0x00,0x80),(0x00,0x00,0xFF),(0x00,0x80,0xFF),(0x00,0xFF,0xFF),(0x80,0xFF,0x80),(0xFF,0xFF,0x00),(0xFF,0x80,0x00),(0xFF,0x00,0x00),(0x80,0x00,0x00) };
    private static readonly (byte, byte, byte)[] Turbo =
    { (0x30,0x12,0x3B),(0x40,0x40,0xA1),(0x46,0x73,0xC9),(0x39,0xA4,0xE1),(0x1F,0xCC,0xB4),(0x47,0xE7,0x63),(0xA4,0xFC,0x3C),(0xF4,0xCB,0x2B),(0xFB,0x82,0x21),(0xE3,0x39,0x1F),(0x7A,0x04,0x03) };
}

/// <summary>16 色定性调色板（忠实原 CategoricalPalette）。</summary>
public static class BlockCategoricalPalette
{
    private static readonly (byte, byte, byte)[] Palette =
    {
        (0x4E,0x79,0xA7),(0xF2,0x8E,0x2B),(0xE1,0x57,0x59),(0x76,0xB7,0xB2),(0x59,0xA1,0x4F),(0xED,0xC9,0x48),(0xB0,0x7A,0xA1),(0xFF,0x9D,0xA7),
        (0x9C,0x75,0x5F),(0xBA,0xB0,0xAC),(0x86,0xBC,0xB6),(0xD3,0x7C,0x3F),(0x6B,0x4C,0x9A),(0x8C,0xD1,0x7D),(0xD1,0x61,0x5D),(0x40,0x9E,0xC9),
    };
    public static (byte r, byte g, byte b) ColorForCode(int code) => code < 0 ? ((byte)0x80, (byte)0x80, (byte)0x80) : Palette[code % Palette.Length];
}

/// <summary>新建模型默认填充色（忠实原 DefaultPalette：8 色按已有模型数循环）。</summary>
public static class BlockDefaultPalette
{
    public static readonly (byte r, byte g, byte b)[] Colors =
    { (0x8F,0xA5,0xC2),(0xC8,0xB0,0x8A),(0xA3,0xC4,0xA1),(0xD4,0x9F,0x9C),(0x9A,0xA8,0xB8),(0xB8,0xA8,0x9A),(0xA1,0xB4,0xC4),(0xC4,0xC1,0xA1) };
    public static (byte r, byte g, byte b) Next(int existingCount) => Colors[Math.Max(0, existingCount) % Colors.Length];
}

/// <summary>连续属性分级着色的一个区间：[Min, Max) 取固定色。</summary>
public sealed class BlockColorClass
{
    public double Min { get; set; }
    public double Max { get; set; }
    public (byte r, byte g, byte b) Color { get; set; }
    public BlockColorClass() { }
    public BlockColorClass(double min, double max, (byte r, byte g, byte b) color) { Min = min; Max = max; Color = color; }
    public BlockColorClass Clone() => new(Min, Max, Color);
}

/// <summary>块体模型显示样式（原 DisplayStyle）。</summary>
public sealed class BlockDisplayStyle
{
    public (byte r, byte g, byte b) FillColor { get; set; } = (0x8F, 0xA5, 0xC2);
    public (byte r, byte g, byte b) EdgeColor { get; set; } = (0x3A, 0x48, 0x56);
    public BlockEdgeMode EdgeMode { get; set; } = BlockEdgeMode.AutoFromFill;
    public double EdgeWidthPx { get; set; } = 0.8;
    public BlockColormapPreset DefaultColormap { get; set; } = BlockColormapPreset.RdYlBu;
    public Dictionary<string, Dictionary<int, (byte r, byte g, byte b)>> CategoryColors { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<BlockColorClass>> ClassBreaks { get; } = new(StringComparer.Ordinal);

    public Dictionary<int, (byte r, byte g, byte b)> EnsureCategoryColors(string attr)
    {
        if (!CategoryColors.TryGetValue(attr, out var map)) { map = new(); CategoryColors[attr] = map; }
        return map;
    }

    public BlockDisplayStyle Clone()
    {
        var c = new BlockDisplayStyle { FillColor = FillColor, EdgeColor = EdgeColor, EdgeMode = EdgeMode, EdgeWidthPx = EdgeWidthPx, DefaultColormap = DefaultColormap };
        foreach (var kv in CategoryColors) c.CategoryColors[kv.Key] = new Dictionary<int, (byte, byte, byte)>(kv.Value);
        foreach (var kv in ClassBreaks) { var l = new List<BlockColorClass>(); foreach (var x in kv.Value) l.Add(x.Clone()); c.ClassBreaks[kv.Key] = l; }
        return c;
    }
}

public enum BlockFilterOperator { Equal, NotEqual, GreaterThan, LessThan, GreaterEqual, LessEqual, Between }

/// <summary>单条筛选条件（attr op value）。</summary>
public sealed class BlockFilterCondition
{
    public string AttributeName { get; set; } = "";
    public BlockFilterOperator Op { get; set; } = BlockFilterOperator.GreaterThan;
    public double Value { get; set; }
    public double Value2 { get; set; }

    public bool Match(double v) => Op switch
    {
        BlockFilterOperator.Equal => Math.Abs(v - Value) < 1e-9,
        BlockFilterOperator.NotEqual => Math.Abs(v - Value) >= 1e-9,
        BlockFilterOperator.GreaterThan => v > Value,
        BlockFilterOperator.LessThan => v < Value,
        BlockFilterOperator.GreaterEqual => v >= Value,
        BlockFilterOperator.LessEqual => v <= Value,
        BlockFilterOperator.Between => v >= Math.Min(Value, Value2) && v <= Math.Max(Value, Value2),
        _ => true,
    };

    public override string ToString()
    {
        string op = Op switch
        {
            BlockFilterOperator.Equal => "=", BlockFilterOperator.NotEqual => "≠", BlockFilterOperator.GreaterThan => ">",
            BlockFilterOperator.LessThan => "<", BlockFilterOperator.GreaterEqual => "≥", BlockFilterOperator.LessEqual => "≤",
            BlockFilterOperator.Between => "BETWEEN", _ => "?",
        };
        return Op == BlockFilterOperator.Between ? $"{AttributeName} {op} [{Value}, {Value2}]" : $"{AttributeName} {op} {Value}";
    }
}

/// <summary>多条件筛选容器（AND/OR）。空条件 = 无筛选。</summary>
public sealed class BlockFilterSet
{
    public List<BlockFilterCondition> Conditions { get; } = new();
    public bool MatchAll { get; set; } = true;

    public bool Match(Func<string, double> getValue)
    {
        if (Conditions.Count == 0) return true;
        foreach (var c in Conditions)
        {
            bool ok = c.Match(getValue(c.AttributeName));
            if (MatchAll && !ok) return false;
            if (!MatchAll && ok) return true;
        }
        return MatchAll;
    }

    public override string ToString() => Conditions.Count == 0 ? "(无筛选)" : string.Join(MatchAll ? " AND " : " OR ", Conditions);
}

/// <summary>删除记录（本次对话框新增的删除，供精确撤销；原 BlockEditor.DeleteRecord 的稠密部分）。</summary>
public sealed class BlockDeleteRecord
{
    public HashSet<int> Ids { get; } = new();
    public void Clear() => Ids.Clear();
    public int Count => Ids.Count;
}

/// <summary>
/// 多段线圈定的平面范围（原 PolygonRegion）：多个 XY 环，奇偶规则 → 互不相交 = 并集、套在里面 = 洞。
/// </summary>
public sealed class BlockPolygonRegion
{
    private readonly List<double[]> _rings;
    public int RingCount => _rings.Count;
    public int VertexCount { get; }
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }

    private BlockPolygonRegion(List<double[]> rings)
    {
        _rings = rings;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int n = 0;
        foreach (var r in rings)
            for (int i = 0; i < r.Length; i += 2)
            {
                n++;
                if (r[i] < minX) minX = r[i]; if (r[i] > maxX) maxX = r[i];
                if (r[i + 1] < minY) minY = r[i + 1]; if (r[i + 1] > maxY) maxY = r[i + 1];
            }
        VertexCount = n; MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    /// <summary>从 XY 环集建区域（每环 ≥3 点）；无有效环 → null。</summary>
    public static BlockPolygonRegion? FromRings(IEnumerable<double[]> rings)
    {
        var list = new List<double[]>();
        foreach (var r in rings) if (r != null && r.Length >= 6) list.Add(r);
        return list.Count == 0 ? null : new BlockPolygonRegion(list);
    }

    /// <summary>多段线顶点 → 平面环(x0,y0,x1,y1,…)；不足 3 点 → null。未闭合自动首尾直连（隐含）。</summary>
    public static double[]? RingFromPoints(IReadOnlyList<(double x, double y)> pts)
    {
        if (pts == null || pts.Count < 3) return null;
        int n = pts.Count;
        if (n > 3 && Math.Abs(pts[0].x - pts[n - 1].x) < 1e-9 && Math.Abs(pts[0].y - pts[n - 1].y) < 1e-9) n--;   // 去重复闭合点
        if (n < 3) return null;
        var ring = new double[n * 2];
        for (int i = 0; i < n; i++) { ring[i * 2] = pts[i].x; ring[i * 2 + 1] = pts[i].y; }
        return ring;
    }

    /// <summary>奇偶规则：点落在奇数个环内 = 在范围内。</summary>
    public bool ContainsXY(double x, double y)
    {
        if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
        int hits = 0;
        foreach (var r in _rings) if (RegionClip.PointInPolygon(x, y, r)) hits++;
        return (hits & 1) == 1;
    }

    public bool OverlapsXY(double minX, double minY, double maxX, double maxY)
        => !(MaxX < minX || MinX > maxX || MaxY < minY || MinY > maxY);
}

/// <summary>
/// 内存中的块体模型（原 BlockModel + BlockModelSpec 合一）：规格 + 块列表 + 逐块属性 + 删除集 + 筛选 + 显示样式。
/// 块顺序：规则网格为 i + j·Nx + k·Nx·Ny；导入/体素化的块可为任意顺序（IsRegular=false）。
/// </summary>
public sealed class BlockModelMeta
{
    public const string ColormapClosedSentinel = "(关闭着色)";
    public const string ZElevationSentinel = "(高程 Z)";

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "BlockModel_1";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    // ── 几何 ──
    public double Ox, Oy, Oz;
    public double Sx = 20, Sy = 20, Sz = 10;
    public int Nx = 100, Ny = 100, Nz = 30;
    public double RotationZDeg;
    /// <summary>true = 块序为 i + j·Nx + k·Nx·Ny 的完整规则网格。</summary>
    public bool IsRegular { get; set; } = true;

    // ── 存储 ──
    public BlockStorageMode StorageMode { get; set; } = BlockStorageMode.Sparse;
    public int SubBlockDepthMax { get; set; } = 2;
    public double SubMinX = 5, SubMinY = 5, SubMinZ = 2.5;

    // ── 属性 Schema / 数据 ──
    public List<BlockPropertyColumn> PropertySchema { get; } = new();
    public List<BlockModel.Block> Blocks { get; set; } = new();
    /// <summary>逐块属性（与 Blocks 并行索引）。列存在但无数组 = 全部取默认值。</summary>
    public Dictionary<string, double[]> Attrs { get; set; } = new(StringComparer.Ordinal);
    public HashSet<int> DeletedIds { get; } = new();
    /// <summary>自适应子块数（实体转块体/体素体积次级退化产物；子块也在 Blocks 里，Size 更小）。</summary>
    public int SubCellCount { get; set; }

    // ── 显示 ──
    public BlockDisplayStyle DisplayStyle { get; set; } = new();
    public bool IsVisible { get; set; } = true;
    public bool IsActive { get; internal set; }
    public string? ActiveColormapAttribute { get; set; }
    public (double Min, double Max)? ColormapRange { get; set; }
    public BlockFilterSet? Filter { get; set; }
    public string? CoalAttribute { get; set; }
    /// <summary>「驱动量」上次斜面约束的删除记录（原 BlockModel.LastInclineUndo）：重跑先精确回退、勾掉约束重跑=撤销。</summary>
    public BlockDeleteRecord? LastInclineUndo { get; set; }

    public bool IsZElevationColoring => string.Equals(ActiveColormapAttribute, ZElevationSentinel, StringComparison.Ordinal);

    // ── 派生 ──
    public long BlockCount => IsRegular ? (long)Nx * Ny * Nz : Blocks.Count;
    public long RealBlockCount => Blocks.Count;
    public long DeletedBlockCount => DeletedIds.Count;
    public long LiveBlockCount() => Blocks.Count - DeletedIds.Count;
    public double CellVolume => Sx * Sy * Sz;

    /// <summary>规格包围盒（规则网格 = 原点 + N×尺寸；不规则 = 块外包络）。</summary>
    public (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) Bounds
    {
        get
        {
            if (IsRegular || Blocks.Count == 0) return (Ox, Oy, Oz, Ox + Nx * Sx, Oy + Ny * Sy, Oz + Nz * Sz);
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var b in Blocks)
            {
                double k = CellScale(b);
                double hx = k * Sx * 0.5, hy = k * Sy * 0.5, hz = k * Sz * 0.5;
                if (b.X - hx < minX) minX = b.X - hx; if (b.X + hx > maxX) maxX = b.X + hx;
                if (b.Y - hy < minY) minY = b.Y - hy; if (b.Y + hy > maxY) maxY = b.Y + hy;
                if (b.Z - hz < minZ) minZ = b.Z - hz; if (b.Z + hz > maxZ) maxZ = b.Z + hz;
            }
            return (minX, minY, minZ, maxX, maxY, maxZ);
        }
    }

    public string BoundsText
    {
        get
        {
            var b = Bounds; var ci = CultureInfo.InvariantCulture;
            return $"({b.minX.ToString("0.##", ci)}, {b.minY.ToString("0.##", ci)}, {b.minZ.ToString("0.##", ci)}) – ({b.maxX.ToString("0.##", ci)}, {b.maxY.ToString("0.##", ci)}, {b.maxZ.ToString("0.##", ci)})";
        }
    }
    public string DimensionsText => $"{Nx} × {Ny} × {Nz}";
    public string BlockSizeText => $"{Sx:0.##} × {Sy:0.##} × {Sz:0.##}";

    /// <summary>估算内存（忠实原 BlockModelSpec.EstimateMemoryBytes：每 cell 1B flags + 列字节；Sparse 折半）。</summary>
    public long EstimateMemoryBytes()
    {
        int colBytes = 0;
        foreach (var c in PropertySchema) colBytes += c.DataType.ByteSize();
        long bytes = BlockCount * (1 + colBytes);
        if (StorageMode == BlockStorageMode.Sparse) bytes /= 2;
        return bytes;
    }

    public static string BytesToHuman(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// <summary>非业务字段自检（原 ValidateBasic）。返回 null = 通过。</summary>
    public string? ValidateBasic()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "模型名称不能为空";
        if (Nx <= 0 || Ny <= 0 || Nz <= 0) return "网格数必须 ≥ 1";
        if ((long)Nx * Ny * Nz > 500_000_000) return $"块数 {(long)Nx * Ny * Nz:N0} 超过上限 5 亿，请减小网格数或增大块尺寸";
        if (Sx <= 0 || Sy <= 0 || Sz <= 0) return "块尺寸必须 > 0";
        return null;
    }

    public BlockPropertyColumn? FindColumn(string attr)
    {
        foreach (var c in PropertySchema) if (string.Equals(c.Name, attr, StringComparison.Ordinal)) return c;
        foreach (var c in PropertySchema) if (string.Equals(c.Name, attr, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public double ColumnDefault(string attr) => FindColumn(attr)?.DefaultValue ?? 0.0;

    public bool HasAttribute(string attr) => Attrs.ContainsKey(attr) || FindColumn(attr) != null;
    public bool HasData(string attr) => Attrs.ContainsKey(attr);

    /// <summary>取属性数组（无数据 → null）。</summary>
    public double[]? GetAttr(string attr) => Attrs.TryGetValue(attr, out var a) ? a : null;

    /// <summary>确保属性数组存在（新分配填默认值）并登记列。</summary>
    public double[] EnsureAttr(string attr, double? defaultValue = null)
    {
        if (!Attrs.TryGetValue(attr, out var arr) || arr.Length != Blocks.Count)
        {
            double def = defaultValue ?? ColumnDefault(attr);
            var na = new double[Blocks.Count];
            if (def != 0) Array.Fill(na, def);
            if (arr != null) Array.Copy(arr, na, Math.Min(arr.Length, na.Length));
            arr = na; Attrs[attr] = arr;
        }
        if (FindColumn(attr) == null) PropertySchema.Add(new BlockPropertyColumn { Name = attr, DefaultValue = defaultValue ?? 0 });
        return arr;
    }

    /// <summary>取块 idx 的属性值（无数据取列默认）。</summary>
    public double GetValue(string attr, int idx)
    {
        if (Attrs.TryGetValue(attr, out var a) && idx >= 0 && idx < a.Length) return a[idx];
        return ColumnDefault(attr);
    }

    /// <summary>属性数据值域 [min,max]（排除已删块与非有限值）；无数据 → null。</summary>
    public (double Min, double Max)? GetAttributeRange(string attr)
    {
        if (!Attrs.TryGetValue(attr, out var a) || a.Length == 0) return null;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int i = 0; i < a.Length; i++)
        {
            if (DeletedIds.Count > 0 && DeletedIds.Contains(i)) continue;
            double v = a[i];
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            if (v < lo) lo = v; if (v > hi) hi = v;
        }
        return lo > hi ? null : (lo, hi);
    }

    /// <summary>着色属性可选列表：(关闭) + (高程 Z) + 全部属性列。</summary>
    public List<string> ColormapChoices
    {
        get
        {
            var l = new List<string> { ColormapClosedSentinel, ZElevationSentinel };
            foreach (var c in PropertySchema) l.Add(c.Name);
            return l;
        }
    }
    public string ColormapChoiceText
    {
        get => ActiveColormapAttribute ?? ColormapClosedSentinel;
        set => ActiveColormapAttribute = (value == ColormapClosedSentinel || string.IsNullOrEmpty(value)) ? null : value;
    }

    /// <summary>规则网格线性索引 → (i,j,k)。</summary>
    public (int i, int j, int k) IJK(int idx)
    {
        long layer = (long)Nx * Ny;
        int k = (int)(idx / layer); long rem = idx - k * layer; int j = (int)(rem / Nx); int i = (int)(rem - (long)j * Nx);
        return (i, j, k);
    }

    /// <summary>
    /// 采剥演示（§三二五）临时藏起来的 cell。**不是「删除块体」**：不入存档、不进撤销、关掉演示即清空 ——
    /// 演示要一帧一帧把已采的块藏掉，借 <see cref="DeletedIds"/> 会把演示状态写进用户的模型里。
    /// </summary>
    public HashSet<int> SimHidden { get; } = new();

    /// <summary>收起演示的临时隐藏（关窗/重置时调）。</summary>
    public void ClearSimHidden() => SimHidden.Clear();

    /// <summary>cell 当前是否可见：非删除 ∩ 非演示隐藏 ∩ 通过 Filter（同原 IsCellVisible）。</summary>
    public bool IsCellVisible(int idx)
    {
        if (idx < 0 || idx >= Blocks.Count) return false;
        if (DeletedIds.Contains(idx)) return false;
        if (SimHidden.Count > 0 && SimHidden.Contains(idx)) return false;
        var f = Filter;
        if (f != null && f.Conditions.Count > 0)
        {
            bool anyData = false;
            foreach (var c in f.Conditions) if (Attrs.ContainsKey(c.AttributeName)) { anyData = true; break; }
            if (!anyData) return true;   // 无属性数据无法判定 → 不收窄
            if (!f.Match(a => GetValue(a, idx))) return false;
        }
        return true;
    }

    public long CountVisibleCells()
    {
        long n = 0;
        for (int i = 0; i < Blocks.Count; i++) if (IsCellVisible(i)) n++;
        return n;
    }

    /// <summary>可见块总体积（母块×块体积 + 子块真实体积；子块按其 Size³ 计）。</summary>
    public double LiveVolume()
    {
        double v = 0;
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (DeletedIds.Contains(i)) continue;
            var b = Blocks[i];
            double k = CellScale(b); v += k * k * k * CellVolume;
        }
        return v;
    }

    /// <summary>
    /// 变尺寸单元（子块 / 八叉树粗叶块）：块的 X 边长 <see cref="BlockModel.Block.Size"/> ≠ 模型细格 <see cref="Sx"/>。
    /// 原来只认"比 Sx 小"(子块)；.blk 的八叉树叶块可以比最细格【大】(层级 sub &lt; maxSub)，也得认。
    /// </summary>
    public bool IsVarCell(in BlockModel.Block b) => SubCellCount > 0 && Math.Abs(b.Size - Sx) > 1e-9;

    /// <summary>
    /// 单元相对细格的边长倍数 k：单元尺寸 = k·(Sx, Sy, Sz)。
    ///
    /// 关键在于**按三轴各自的细格尺寸缩放**，而不是画成边长 b.Size 的立方体。.blk 的最细格是
    /// 25×25×0.5 m（水平规则 + 垂向自适应细分），画成 25×25×25 的立方体就竖向胖了 50 倍，
    /// 整个模型糊成一块平板 —— 原版 SurfaceInstanceBuilder 发的是 (s·sx, s·sy, s·sz) 的长方体。
    /// 各向同性的模型（实体转块体/体素退化，Sx=Sy=Sz）下 k·(Sx,Sy,Sz) 恰好还原成原来的立方体。
    /// </summary>
    public double CellScale(in BlockModel.Block b) => IsVarCell(b) && Sx > 1e-12 ? b.Size / Sx : 1.0;

    // ── 建模/导入 ──

    /// <summary>按规格生成完整规则网格（块中心 = 原点 + (i+0.5)·尺寸），属性列只登记不分配。</summary>
    public static BlockModelMeta CreateRegular(string name, double ox, double oy, double oz, double sx, double sy, double sz, int nx, int ny, int nz)
    {
        var m = new BlockModelMeta { Name = name, Ox = ox, Oy = oy, Oz = oz, Sx = sx, Sy = sy, Sz = sz, Nx = nx, Ny = ny, Nz = nz, IsRegular = true };
        long n = (long)nx * ny * nz;
        if (n > int.MaxValue) throw new ArgumentException("块数超过 int 上限");
        var blocks = new List<BlockModel.Block>((int)n);
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                    blocks.Add(new BlockModel.Block { X = ox + (i + 0.5) * sx, Y = oy + (j + 0.5) * sy, Z = oz + (k + 0.5) * sz, Size = sx, Grade = 0 });
        m.Blocks = blocks;
        return m;
    }

    /// <summary>
    /// 用八叉树叶块建模（.blk 导入）：几何由文件直接给定 —— 原点 / 三轴细格尺寸 / 细格维度 / 最深层级，
    /// 不走 <see cref="FromBlocks"/> 那套"从块中心猜尺寸"（猜出来只有一个各向同性尺寸，25×25×0.5 的
    /// 细格会被猜成 25 的立方体，模型竖向胖 50 倍）。变尺寸叶块数记入 <see cref="SubCellCount"/>，
    /// 逐块边长倍数由 <see cref="CellScale"/> 从 Size/Sx 推。
    /// </summary>
    public static BlockModelMeta FromLeaves(
        string name, List<BlockModel.Block> blocks, Dictionary<string, double[]>? attrs,
        double ox, double oy, double oz, double sx, double sy, double sz,
        int nx, int ny, int nz, int subDepthMax, int varCellCount)
    {
        var m = new BlockModelMeta
        {
            Name = name, IsRegular = false, Blocks = blocks,
            Ox = ox, Oy = oy, Oz = oz,
            Sx = sx > 0 ? sx : 1, Sy = sy > 0 ? sy : 1, Sz = sz > 0 ? sz : 1,
            Nx = Math.Max(1, nx), Ny = Math.Max(1, ny), Nz = Math.Max(1, nz),
            StorageMode = BlockStorageMode.Sparse,
            SubCellCount = varCellCount,
        };
        if (subDepthMax > 0) m.SubBlockDepthMax = subDepthMax;
        m.SubMinX = m.Sx; m.SubMinY = m.Sy; m.SubMinZ = m.Sz;   // 次级退化最小尺寸 = 最细格(同原 BlkReader)
        RegisterAttrs(m, blocks, attrs);
        return m;
    }

    /// <summary>用任意块集合建模（导入 CSV/BLK/PMB 或主窗口已有块）：从块中心推断原点/尺寸/维度（同 PmbExportService.FromBlocks）。</summary>
    public static BlockModelMeta FromBlocks(string name, List<BlockModel.Block> blocks, Dictionary<string, double[]>? attrs)
    {
        var m = new BlockModelMeta { Name = name, IsRegular = false, Blocks = blocks };
        if (blocks.Count > 0)
        {
            double size = blocks[0].Size > 0 ? blocks[0].Size : 1;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var b in blocks)
            {
                if (b.X < minX) minX = b.X; if (b.X > maxX) maxX = b.X;
                if (b.Y < minY) minY = b.Y; if (b.Y > maxY) maxY = b.Y;
                if (b.Z < minZ) minZ = b.Z; if (b.Z > maxZ) maxZ = b.Z;
            }
            m.Sx = m.Sy = m.Sz = size;
            m.Ox = minX - size / 2; m.Oy = minY - size / 2; m.Oz = minZ - size / 2;
            m.Nx = Math.Max(1, (int)Math.Round((maxX - minX) / size, MidpointRounding.AwayFromZero) + 1);
            m.Ny = Math.Max(1, (int)Math.Round((maxY - minY) / size, MidpointRounding.AwayFromZero) + 1);
            m.Nz = Math.Max(1, (int)Math.Round((maxZ - minZ) / size, MidpointRounding.AwayFromZero) + 1);
        }
        RegisterAttrs(m, blocks, attrs);
        return m;
    }

    /// <summary>属性列登记（长度对得上的入表）+ 品位列兜底。FromBlocks / FromLeaves 共用。</summary>
    private static void RegisterAttrs(BlockModelMeta m, List<BlockModel.Block> blocks, Dictionary<string, double[]>? attrs)
    {
        if (attrs != null)
            foreach (var kv in attrs)
            {
                if (kv.Value.Length != blocks.Count) continue;
                m.Attrs[kv.Key] = kv.Value;
                m.PropertySchema.Add(new BlockPropertyColumn { Name = kv.Key });
            }
        bool anyGrade = false;
        foreach (var b in blocks) if (b.Grade != 0) { anyGrade = true; break; }
        if (anyGrade && !m.Attrs.ContainsKey("grade"))
        {
            var g = new double[blocks.Count];
            for (int i = 0; i < blocks.Count; i++) g[i] = blocks[i].Grade;
            m.Attrs["grade"] = g;
            m.PropertySchema.Insert(0, new BlockPropertyColumn { Name = "grade", Description = "品位" });
        }
    }

    // ── 删除/恢复（原 BlockEditor 稠密路径）──

    /// <summary>按谓词保留：keep(idx, block)=false 的未删块标删。返回本次新增删除数；record 记录以便撤销。</summary>
    public int ApplyKeep(Func<int, BlockModel.Block, bool> keep, BlockDeleteRecord? record = null)
    {
        int n = 0;
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (DeletedIds.Contains(i)) continue;
            if (keep(i, Blocks[i])) continue;
            DeletedIds.Add(i); record?.Ids.Add(i); n++;
        }
        return n;
    }

    /// <summary>恢复 record 中记录的删除（只回退自己加的）。</summary>
    public int Restore(BlockDeleteRecord record)
    {
        int n = 0;
        foreach (var id in record.Ids) if (DeletedIds.Remove(id)) n++;
        record.Clear();
        return n;
    }

    public int ClearDeleted() { int n = DeletedIds.Count; DeletedIds.Clear(); return n; }

    // ── 公式赋值 ──

    /// <summary>表达式内置变量名（原 ExpressionEngine 约定）。</summary>
    public static bool IsBuiltinVar(string v) => v is "i" or "j" or "k" or "x" or "y" or "z" or "nx" or "ny" or "nz" or "sx" or "sy" or "sz";

    /// <summary>把每个块的 i/j/k/x/y/z/nx…/属性 灌入上下文（供公式/删除表达式逐块求值）。</summary>
    public void FillContext(MutableBlockExprContext ctx, int idx, IReadOnlyList<string> refAttrs)
    {
        var b = Blocks[idx];
        if (IsRegular) { var (i, j, k) = IJK(idx); ctx.Set("i", i); ctx.Set("j", j); ctx.Set("k", k); }
        else
        {
            ctx.Set("i", Math.Floor((b.X - Ox) / Sx)); ctx.Set("j", Math.Floor((b.Y - Oy) / Sy)); ctx.Set("k", Math.Floor((b.Z - Oz) / Sz));
        }
        ctx.Set("x", b.X); ctx.Set("y", b.Y); ctx.Set("z", b.Z);
        foreach (var a in refAttrs) ctx.Set(a, GetValue(a, idx));
    }

    public void FillConstants(MutableBlockExprContext ctx)
    {
        ctx.Set("nx", Nx); ctx.Set("ny", Ny); ctx.Set("nz", Nz);
        ctx.Set("sx", Sx); ctx.Set("sy", Sy); ctx.Set("sz", Sz);
    }

    /// <summary>对每个 cell 求公式 → 写属性列。scope 非空只写命中的 cell。NaN/∞ → 0（同原）。返回写入数。</summary>
    public int ApplyFormula(string attr, BlockAttrExpression engine, Func<int, bool>? scope = null)
    {
        var data = EnsureAttr(attr);
        var ctx = new MutableBlockExprContext();
        FillConstants(ctx);
        var refAttrs = new List<string>();
        foreach (var v in engine.ReferencedVariables) if (!IsBuiltinVar(v.ToLowerInvariant())) refAttrs.Add(v);
        int written = 0;
        for (int idx = 0; idx < Blocks.Count; idx++)
        {
            if (scope != null && !scope(idx)) continue;
            FillContext(ctx, idx, refAttrs);
            double r = engine.Evaluate(ctx);
            if (double.IsNaN(r) || double.IsInfinity(r)) r = 0;
            data[idx] = r; written++;
        }
        return written;
    }

    /// <summary>常量赋值：scope=null 全部；否则只写命中的 cell。返回写入数。</summary>
    public long SetConstant(string attr, double value, Func<int, bool>? scope = null)
    {
        var data = EnsureAttr(attr);
        long n = 0;
        for (int i = 0; i < data.Length; i++) { if (scope != null && !scope(i)) continue; data[i] = value; n++; }
        return n;
    }

    // ── 着色 ──

    /// <summary>
    /// 逐块取色的「预案」：把 <see cref="ColorOf(int)"/> 里与块号无关的那部分（活动属性列 / 取值数组 /
    /// 值域 / 分类色表 / 分级表 / 高程范围）先算一次，逐块只查表。
    ///
    /// 非它不可：着色值域未固定（<see cref="ColormapRange"/> 为 null，导入块体正是这么设的）时，
    /// ColorOf 每块都要 <see cref="GetAttributeRange"/> 扫一遍整列属性，于是「建网格时逐块取色」是
    /// O(块数²) —— 实测 2 万块 0.6 秒、4 万块 1.7 秒、8 万块 6.4 秒、16 万块 26 秒，311 万块的东露天
    /// 块体模型外推近 3 小时。导入块体「未响应」卡死的就是这一步。
    /// </summary>
    public readonly struct ColorPlan
    {
        public readonly bool Solid;                                     // 着色关闭 → 一律填充色
        public readonly (byte r, byte g, byte b) Fill;
        public readonly BlockColormapPreset Ramp;
        public readonly bool ByElevation;                               // 按高程 Z 着色
        public readonly double Lo, Hi;                                  // 高程/属性值域(已定好, 不再逐块重算)
        public readonly string Attr;
        public readonly double[]? Data;                                 // 活动属性列数据(缺列时为 null)
        public readonly double Default;                                 // 缺列/越界时的取值(同 GetValue)
        public readonly bool Categorical;
        public readonly Dictionary<int, (byte r, byte g, byte b)>? CatColors;
        public readonly List<BlockColorClass>? Classes;

        internal ColorPlan(BlockModelMeta m)
        {
            var ds = m.DisplayStyle;
            Fill = ds.FillColor; Ramp = ds.DefaultColormap;
            Attr = m.ActiveColormapAttribute ?? "";
            Solid = string.IsNullOrEmpty(m.ActiveColormapAttribute);
            ByElevation = !Solid && m.IsZElevationColoring;
            Data = null; Default = 0; Categorical = false; CatColors = null; Classes = null; Lo = 0; Hi = 1;
            if (Solid) return;
            if (ByElevation) { var b = m.Bounds; Lo = b.minZ; Hi = b.maxZ; return; }
            m.Attrs.TryGetValue(Attr, out Data);
            Default = m.ColumnDefault(Attr);
            Categorical = m.FindColumn(Attr) is { IsCategorical: true };
            if (Categorical) { ds.CategoryColors.TryGetValue(Attr, out CatColors); return; }
            if (ds.ClassBreaks.TryGetValue(Attr, out var cls) && cls.Count > 0) { Classes = cls; return; }
            var range = m.ColormapRange ?? m.GetAttributeRange(Attr) ?? (0, 1);
            Lo = range.Min; Hi = range.Max;
        }
    }

    /// <summary>备一份取色预案（逐块取色前调一次，见 <see cref="ColorPlan"/>）。</summary>
    public ColorPlan MakeColorPlan() => new ColorPlan(this);

    /// <summary>按当前显示样式/着色属性算块 idx 的颜色（忠实原 SurfaceInstanceBuilder 的取色分支）。</summary>
    public (byte r, byte g, byte b) ColorOf(int idx) => ColorOf(idx, MakeColorPlan());

    /// <summary>同上，但取色预案由调用方备好 —— 逐块建网格走这条，别让值域每块重扫一遍。</summary>
    public (byte r, byte g, byte b) ColorOf(int idx, in ColorPlan p)
    {
        if (p.Solid) return p.Fill;
        if (p.ByElevation)
        {
            double t = p.Hi > p.Lo ? (Blocks[idx].Z - p.Lo) / (p.Hi - p.Lo) : 0.5;
            return BlockColormap.Sample(p.Ramp, t);
        }
        double v = p.Data != null && idx >= 0 && idx < p.Data.Length ? p.Data[idx] : p.Default;
        if (p.Categorical)
        {
            int code = (int)Math.Round(v);
            if (p.CatColors != null && p.CatColors.TryGetValue(code, out var cc)) return cc;
            return BlockCategoricalPalette.ColorForCode(code);
        }
        if (p.Classes is { Count: > 0 } classes)
        {
            if (v < classes[0].Min) return classes[0].Color;
            for (int c = 0; c < classes.Count; c++)
            {
                bool last = c == classes.Count - 1;
                if (v >= classes[c].Min && (v < classes[c].Max || (last && v <= classes[c].Max))) return classes[c].Color;
            }
            return classes[classes.Count - 1].Color;
        }
        double tt = p.Hi > p.Lo ? (v - p.Lo) / (p.Hi - p.Lo) : 0.5;
        return BlockColormap.Sample(p.Ramp, tt);
    }

    /// <summary>
    /// 可见块 → **六面体网格**（一个模型一张三角网，逐顶点色）。clip 非空时再按剖切谓词过滤。
    ///
    /// 此前每块出一张平面 RectEntity（只有 Sx/Sy 足印、放在 Z 上），三维里看到的是一摞平板而不是体素；
    /// 现按 Sx/Sy/Sz 出真立方体，并照原版 SurfaceInstanceBuilder 的口径**整块剔除**内部块。
    /// 面色取每块自己的配色（逐顶点色），实体基色取样式的填充色；块界靠逐块描边（见下方 EdgeMode 处理）。
    /// </summary>
    public List<SceneEntity> BuildCells(Func<BlockModel.Block, bool>? clip = null)
    {
        var r = BuildCellMesh(clip, out _);
        return r == null ? new List<SceneEntity>() : new List<SceneEntity> { r };
    }

    /// <summary>
    /// 画出的块数超过这个数就不再描边（边线数 ≈ 12·块数，太多会盖死面并拖慢视口）。
    ///
    /// 原版是**恒描边**的（一堆同色立方体只画面会糊成一整块，看不出块界），这里的上限纯粹是
    /// 托管渲染的线预算。定 6 万是当年「311 万叶块剔不掉、只能抽稀画 30 万块」时的保守值；
    /// 原版是**恒描边**的（一堆同色立方体只画面会糊成一整块，看不出块界），这里的上限纯粹是
    /// 托管渲染的线预算。定 6 万是当年「311 万叶块剔不掉、只能抽稀画 30 万块」时的保守值。
    ///
    /// 现在两件事都变了：一是按细格占用剔块，平朔那份 311 万叶块只剩 7.1 万块壳层；二是<b>逐面</b>
    /// 剔除后只发露出来的面，棱也只从这些面上出 —— 于是描的正好是"看得见的块界"，顶面是粗叶块的
    /// 大格子、侧面是薄片的层理，跟原版一个样。（早先放宽上限却<b>不</b>剔面时，六张面连同被挡住的
    /// 那几张一起描边，侧面糊成一片白噪，那是面没剔干净，不是棱太多。）
    /// </summary>
    public const int WireframeCellLimit = 200_000;

    /// <summary>可见块 → 一张六面体三角网；stat 给出画了几块/剔了几块/截断几块。无可见块返回 null。</summary>
    public MeshEntity? BuildCellMesh(Func<BlockModel.Block, bool>? clip, out BlockMeshBuilder.Result stat)
    {
        var cells = new List<BlockMeshBuilder.Cell>(Blocks.Count);
        double hz = Sz * 0.5;
        var plan = MakeColorPlan();      // 取色预案备一次: 逐块现算值域是 O(块数²), 见 ColorPlan
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (!IsCellVisible(i)) continue;
            var b = Blocks[i];
            if (clip != null && !clip(b)) continue;
            double k = CellScale(b);
            double hx = k * Sx * 0.5, hy = k * Sy * 0.5, h = k * hz;
            var (cr, cg, cb) = ColorOf(i, plan);
            cells.Add(new BlockMeshBuilder.Cell(b.X, b.Y, b.Z, hx, hy, h, cr / 255f, cg / 255f, cb / 255f));
        }
        stat = BlockMeshBuilder.Build(cells, edgeLimit: WireframeCellLimit);
        var ds = DisplayStyle;
        var mesh = BlockMeshBuilder.ToMesh(stat, $"块体-{Name}", (ds.FillColor.r / 255f, ds.FillColor.g / 255f, ds.FillColor.b / 255f));
        if (mesh == null) return null;   // 无可见块(全删/全被筛掉/全被剖切)

        // 边线：块体恒描边（同原版 —— 一堆同色立方体只画面会糊成一整块，看不出块界），
        // 故用逐实体覆盖而不跟随全局显示模式；边色按样式的 EdgeMode：
        //   AutoFromFill → 由每块自己的填充色压暗（原版 litColor·0.55）
        //   Fixed        → 用样式里的固定边色
        //   HiddenUnlessSelected → 不描边（原版此模式亦只是隐藏）
        // 块太多时不描边：边线数 = 12·块数，几十万条会盖死面且拖慢视口（原版靠按屏幕尺寸淡出边线，此处按块数设阈）。
        bool tooMany = stat.DrawnCells > WireframeCellLimit;
        if (ds.EdgeMode == BlockEdgeMode.HiddenUnlessSelected || tooMany)
            mesh.RenderModeOverride = MeshEntity.DisplayMode.Shaded;
        else
        {
            mesh.RenderModeOverride = MeshEntity.DisplayMode.ShadedWireframe;
            if (ds.EdgeMode == BlockEdgeMode.Fixed)
                mesh.EdgeColorFixed = (ds.EdgeColor.r / 255f, ds.EdgeColor.g / 255f, ds.EdgeColor.b / 255f);
        }
        return mesh;
    }

    /// <summary>未删块 + 其属性子表（供推给主窗口 ctx.SetBlocks / 导出）。Grade 同步为着色属性值（无则 grade 列）。</summary>
    public (List<BlockModel.Block> blocks, Dictionary<string, double[]> attrs) LiveSnapshot()
    {
        var keep = new List<int>(Blocks.Count);
        for (int i = 0; i < Blocks.Count; i++) if (!DeletedIds.Contains(i)) keep.Add(i);
        string? gradeAttr = ActiveColormapAttribute != null && !IsZElevationColoring && Attrs.ContainsKey(ActiveColormapAttribute) ? ActiveColormapAttribute
            : Attrs.ContainsKey("grade") ? "grade" : null;
        var ga = gradeAttr != null ? Attrs[gradeAttr] : null;
        var blocks = new List<BlockModel.Block>(keep.Count);
        foreach (var i in keep) { var b = Blocks[i]; if (ga != null) b.Grade = ga[i]; blocks.Add(b); }
        var attrs = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var kv in Attrs)
        {
            if (kv.Value.Length != Blocks.Count) continue;
            var a = new double[keep.Count];
            for (int n = 0; n < keep.Count; n++) a[n] = kv.Value[keep[n]];
            attrs[kv.Key] = a;
        }
        return (blocks, attrs);
    }

    /// <summary>分类属性的类别清单（原 AttributeCategories.Enumerate）：名表里有名的码 ∪ 数据里出现的码，升序。</summary>
    public List<(int Code, string Label)> EnumerateCategories(string attr, int maxCategories = 256)
    {
        var set = new SortedSet<int>();
        var col = FindColumn(attr);
        var labels = col?.CategoryLabels;
        if (labels is { Count: > 0 })
            for (int i = 0; i < labels.Count && set.Count < maxCategories; i++)
                if (!string.IsNullOrEmpty(labels[i])) set.Add(i);
        if (Attrs.TryGetValue(attr, out var arr))
            for (int i = 0; i < arr.Length; i++)
            {
                if (DeletedIds.Count > 0 && DeletedIds.Contains(i)) continue;
                double v = arr[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                int code = (int)Math.Round(v);
                if (code >= 0 && Math.Abs(v - code) < 1e-6) set.Add(code);
                if (set.Count >= maxCategories) break;
            }
        var list = new List<(int, string)>();
        foreach (var c in set) list.Add((c, labels != null && c < labels.Count ? labels[c] ?? "" : ""));
        return list;
    }

    /// <summary>数据 [min,max] 剔除默认值哨兵（原 RobustRange）。</summary>
    public (double Lo, double Hi) RobustRange(string attr)
    {
        double def = FindColumn(attr)?.DefaultValue ?? double.NaN;
        if (!Attrs.TryGetValue(attr, out var arr) || arr.Length == 0) return (0, 1);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var v in arr)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v == def) continue;
            if (v < lo) lo = v; if (v > hi) hi = v;
        }
        if (lo > hi) return (0, 1);
        if (lo == hi) return (lo, lo + 1);
        return (lo, hi);
    }

    /// <summary>n+1 个分级边界：等间距 / 分位数（原 ComputeEdges）。</summary>
    public double[] ComputeClassEdges(string attr, int n, bool quantile)
    {
        var (lo, hi) = RobustRange(attr);
        var edges = new double[n + 1];
        if (!quantile) { for (int i = 0; i <= n; i++) edges[i] = lo + (hi - lo) * i / n; return edges; }
        double def = FindColumn(attr)?.DefaultValue ?? double.NaN;
        var sample = new List<double>();
        if (Attrs.TryGetValue(attr, out var arr))
        {
            int stride = Math.Max(1, arr.Length / 200_000);
            for (int i = 0; i < arr.Length; i += stride) { double v = arr[i]; if (double.IsNaN(v) || double.IsInfinity(v) || v == def) continue; sample.Add(v); }
        }
        if (sample.Count < n + 1) { for (int i = 0; i <= n; i++) edges[i] = lo + (hi - lo) * i / n; return edges; }
        sample.Sort();
        edges[0] = sample[0]; edges[n] = sample[sample.Count - 1];
        for (int i = 1; i < n; i++)
        {
            double q = (double)i / n * (sample.Count - 1); int idx = (int)q; double frac = q - idx;
            edges[i] = idx + 1 < sample.Count ? sample[idx] * (1 - frac) + sample[idx + 1] * frac : sample[idx];
        }
        return edges;
    }

    /// <summary>按边界 + 色带生成 n 个区间色（原 GenerateClasses）。</summary>
    public List<BlockColorClass> GenerateClasses(string attr, int n, bool quantile, BlockColormapPreset preset)
    {
        n = Math.Clamp(n, 1, 64);
        var edges = ComputeClassEdges(attr, n, quantile);
        var list = new List<BlockColorClass>(n);
        for (int i = 0; i < n; i++)
        {
            double t = n == 1 ? 0.5 : (i + 0.5) / n;
            list.Add(new BlockColorClass(edges[i], edges[i + 1], BlockColormap.Sample(preset, t)));
        }
        return list;
    }

    public override string ToString() => $"{Name} ({BlockCount:N0} 块)";
}
