using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 块体模型会话仓（原 BlockModelService 的托管等价）：多模型列表 + 活动模型 + 全局切面剖切态；
/// 渲染桥接 = 把可见模型按显示样式配色成 RectEntity 入场景图层「块体模型」，并把活动模型（未删块 + 属性）推给
/// <see cref="ModelingContext.SetBlockDisplay"/>，使主窗口自身的块体命令（导出 PMB/资源量/切换属性）看到同一份数据。
/// 主窗口自己导入的块体（BLK/PMB/CSV 命令）由 <see cref="Adopt"/> 收编为一个模型。
/// </summary>
public static class BlockModelStore
{
    public const string LayerName = "块体模型";

    public static ObservableCollection<BlockModelMeta> Models { get; } = new();

    private static BlockModelMeta? _active;
    public static BlockModelMeta? Active
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value)) return;
            if (_active != null) _active.IsActive = false;
            _active = value;
            if (_active != null) _active.IsActive = true;
            ActiveChanged?.Invoke(null, EventArgs.Empty);
        }
    }
    public static event EventHandler? ActiveChanged;
    /// <summary>任何模型显示相关变化（属性/删除/筛选/着色）后触发，浏览器据此刷新。</summary>
    public static event EventHandler? DisplayChanged;

    // ── 切面剖切（全局态）──
    public static bool ClipEnabled { get; private set; }
    public static (double NX, double NY, double NZ, double D) ClipPlane { get; private set; } = (0, 0, 1, 0);
    public static event EventHandler? ClipChanged;

    private static List<BlockModel.Block>? _lastPushed;

    /// <summary>主窗口已有块体（非本仓推送的）→ 收编成模型「块体模型」；无则不动。</summary>
    public static void Adopt(ModelingContext ctx)
    {
        var blocks = ctx.Blocks();
        if (blocks == null || blocks.Count == 0 || ReferenceEquals(blocks, _lastPushed)) return;
        foreach (var m in Models) if (ReferenceEquals(m.Blocks, blocks)) return;
        var attrs = ctx.BlockAttrs();
        var meta = BlockModelMeta.FromBlocks(UniqueName("块体模型"), blocks, attrs);
        meta.DisplayStyle.FillColor = BlockDefaultPalette.Next(Models.Count);
        if (meta.Attrs.Count > 0) meta.ActiveColormapAttribute = meta.Attrs.ContainsKey("grade") ? "grade" : meta.PropertySchema[0].Name;
        Models.Add(meta);
        Active = meta;
        _lastPushed = blocks;
    }

    public static string UniqueName(string baseName)
    {
        bool Exists(string n) => Models.Any(m => string.Equals(m.Name, n, StringComparison.Ordinal));
        if (!Exists(baseName)) return baseName;
        for (int i = 2; i < 100000; i++) { var n = $"{baseName}_{i}"; if (!Exists(n)) return n; }
        return $"{baseName}_{Guid.NewGuid():N}";
    }

    /// <summary>加入模型（名字去重校验由调用方做）并设为活动、渲染。返回错误文本或 null。</summary>
    public static string? Create(ModelingContext ctx, BlockModelMeta m)
    {
        var err = Admit(m);
        if (err != null) return err;
        RefreshDisplay(ctx, m, fit: true);
        return null;
    }

    /// <summary>同 <see cref="Create"/>，但建网放后台线程（导入几百万块的模型走这条，别冻住窗口）。</summary>
    public static async Task<string?> CreateAsync(ModelingContext ctx, BlockModelMeta m)
    {
        var err = Admit(m);
        if (err != null) return err;
        await RefreshDisplayAsync(ctx, m, fit: true);
        return null;
    }

    /// <summary>校验 + 入列 + 设为活动（不渲染）。返回错误文本或 null。</summary>
    private static string? Admit(BlockModelMeta m)
    {
        var err = m.ValidateBasic();
        if (err != null) return err;
        if (Models.Any(x => string.Equals(x.Name, m.Name, StringComparison.Ordinal))) return $"已存在同名模型: {m.Name}";
        m.IsVisible = true;
        Models.Add(m);
        Active = m;
        return null;
    }

    public static string? Remove(ModelingContext ctx, BlockModelMeta m)
    {
        if (!Models.Contains(m)) return "模型不存在";
        Models.Remove(m);
        if (ReferenceEquals(Active, m)) Active = Models.Count > 0 ? Models[0] : null;
        RefreshDisplay(ctx, null);
        return null;
    }

    public static string? Rename(BlockModelMeta m, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return "新名称不能为空";
        if (Models.Any(x => !ReferenceEquals(x, m) && string.Equals(x.Name, newName, StringComparison.Ordinal))) return $"名称已被占用: {newName}";
        m.Name = newName;
        DisplayChanged?.Invoke(null, EventArgs.Empty);
        return null;
    }

    public static void SetVisibility(ModelingContext ctx, BlockModelMeta m, bool visible)
    {
        if (m.IsVisible == visible) return;
        m.IsVisible = visible;
        RefreshDisplay(ctx, m);
    }

    public static void ZoomTo(ModelingContext ctx, BlockModelMeta m)
    {
        var b = m.Bounds;
        if (b.maxX > b.minX && b.maxY > b.minY) ctx.FitBounds(new[] { b.minX, b.minY, b.maxX, b.maxY });
    }

    public static void SetClipPlane(ModelingContext ctx, double nx, double ny, double nz, double d, bool enable)
    {
        ClipPlane = (nx, ny, nz, d);
        ClipEnabled = enable;
        RefreshDisplay(ctx, null);
        ClipChanged?.Invoke(null, EventArgs.Empty);
    }
    public static void DisableClip(ModelingContext ctx) => SetClipPlane(ctx, ClipPlane.NX, ClipPlane.NY, ClipPlane.NZ, ClipPlane.D, false);

    /// <summary>剖切谓词：dot((p,1),plane) &gt; 0 一侧被切掉。</summary>
    public static Func<BlockModel.Block, bool>? ClipPredicate()
    {
        if (!ClipEnabled) return null;
        var (nx, ny, nz, d) = ClipPlane;
        return b => nx * b.X + ny * b.Y + nz * b.Z + d <= 0;
    }

    /// <summary>重渲要备的料：建好的块体网 + 活动模型未删块快照 + 缩放范围。纯计算，可在后台线程算。</summary>
    private sealed record Display(
        List<SceneEntity> Cells,
        List<BlockModel.Block>? Blocks,
        Dictionary<string, double[]>? Attrs,
        double[]? Bounds);

    private static Display Prepare(bool fit)
    {
        var clip = ClipPredicate();
        var cells = new List<SceneEntity>();
        foreach (var model in Models)
        {
            if (!model.IsVisible) continue;
            cells.AddRange(model.BuildCells(clip));
        }
        var act = Active;
        if (act == null) return new Display(cells, null, null, null);
        var (blocks, attrs) = act.LiveSnapshot();
        double[]? bounds = null;
        if (fit && cells.Count > 0) { var b = act.Bounds; bounds = new[] { b.minX, b.minY, b.maxX, b.maxY }; }
        return new Display(cells, blocks, attrs.Count > 0 ? attrs : null, bounds);
    }

    private static void Apply(ModelingContext ctx, Display d)
    {
        _lastPushed = d.Blocks;
        ctx.SetBlockDisplay(d.Blocks, d.Attrs, d.Cells, LayerName, d.Bounds);
        DisplayChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 重渲：清图层 → 每个可见模型的可见块（非删除∩筛选∩剖切）配色入场景；活动模型未删块推给主窗口。
    /// m 为触发变化的模型（可 null）。fit=true 缩放到活动模型范围。
    /// </summary>
    public static void RefreshDisplay(ModelingContext ctx, BlockModelMeta? m, bool fit = false)
        => Apply(ctx, Prepare(fit));

    /// <summary>
    /// 同 <see cref="RefreshDisplay"/>，但建网/取快照放后台线程 —— 三百万块的模型建一次网就近一秒，
    /// 占着 UI 线程窗口便是「(未响应)」。只有真在 UI 线程上调才有意义（await 后回到 UI 线程改场景）。
    /// </summary>
    public static async Task RefreshDisplayAsync(ModelingContext ctx, BlockModelMeta? m, bool fit = false)
        => Apply(ctx, await Task.Run(() => Prepare(fit)));

    /// <summary>浏览器/对话框统一的活动模型兜底：无活动取第一个。</summary>
    public static BlockModelMeta? PickDefault() => Active ?? Models.FirstOrDefault();
}
