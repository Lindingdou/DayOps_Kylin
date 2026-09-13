// 移植自原 PitMine3D Modules/TaskLib/Simulation/SimMaterialColorProvider.cs —— 对外契约（SimBlockComposition /
// 三个物料色常量 / Create / ClassifyPoints / SampleComposition）逐行对应；
// 块体采样那一截原版直接吃 BlockModelLib（BlockModelService.Instance.Active / CoalQualityBlockBridge /
// SectionSampler），Kylin 的块体模型在 Views.Modeling.BlockModelStore，形状不同，故此处留一个可注入的后端
// （<see cref="Backend"/>）：未注入时走原版自己的第一环降级（"块体模块未就绪 → 层体按去向类型着色"），
// 文案与原版一致，永不抛。
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>层体范围内按**块体模型**统计出的煤/岩体积构成（不是按份额估的数）。</summary>
public sealed class SimBlockComposition
{
    /// <summary>统计成立（下面的体积数可用）。false 时只有 <see cref="Reason"/> 有意义。</summary>
    public bool Available { get; set; }
    /// <summary>来源 / 统计不出来的原因（界面直接显示这一句）。</summary>
    public string Reason { get; set; } = "";

    /// <summary>层体范围内的煤体积 m³（**实方**，块体口径）。</summary>
    public double CoalM3 { get; set; }
    /// <summary>层体范围内的岩（含未判定为煤的一切激活块）体积 m³（实方）。</summary>
    public double RockM3 { get; set; }
    /// <summary>命中的 cell / 叶块数。</summary>
    public long Cells { get; set; }

    public double TotalM3 => CoalM3 + RockM3;
    /// <summary>煤占比 %。</summary>
    public double CoalPct => TotalM3 > 1e-9 ? CoalM3 / TotalM3 * 100 : 0;

    /// <summary>
    /// 块体口径总方 / 层体几何体积 —— **覆盖率**。
    /// 明显小于 1 说明层体有一大块落在块体模型之外（或那里没有块），
    /// 此时煤/岩构成只代表**被覆盖的那一部分**，不是整个层体，必须让人看见。
    /// </summary>
    public double CoveragePct { get; set; }

    public string Caption => !Available ? Reason
        : $"煤 {CoalM3 / 1e4:0.###}万m³ / 岩 {RockM3 / 1e4:0.###}万m³（煤占 {CoalPct:0.#}%）"
        + $"　块体覆盖 {CoveragePct:0.#}% · {Cells:N0} 个单元";
}

/// <summary>
/// 块体模型物料着色源。构造时做一次能力探测，之后 <see cref="Available"/> 与
/// <see cref="StatusLabel"/> 就是整条链的对外结论。永不抛。
/// </summary>
public sealed class SimMaterialColorProvider : ISimMaterialColorSource
{
    /// <summary>Kylin 侧块体采样后端（由块体模型模块注入；null = 未接入）。</summary>
    public interface IBackend
    {
        /// <summary>活动块体模型名；null = 没有活动模型。</summary>
        string? ActiveModelName { get; }
        /// <summary>能力探测：有煤岩判别列则返回 true 并给出标签文案。</summary>
        bool TryDescribe(out string label);
        /// <summary>批量分类：世界坐标扁平 [x,y,z,...] → 每点类别（0=采不到 / 1=煤 / 2=岩）。</summary>
        byte[] ClassifyPoints(double[] worldXyz);
        /// <summary>层体范围内的煤/岩体积构成。</summary>
        SimBlockComposition SampleComposition(SimSolidLayer layer);
    }

    /// <summary>块体采样后端注入点。</summary>
    public static IBackend? Backend { get; set; }

    /// <summary>煤 近黑（露天矿图上煤层的常规表达）。</summary>
    // 深底上 #2B3138（近黑）几乎看不见 —— 提亮成冷灰蓝，保住"煤是冷色调"的识别，
    // 但在 #0E1626 的画布上分得出形。三色一起调，相对关系不变。
    public const uint RgbCoal = 0x8FB3D9;
    /// <summary>岩 土黄褐。</summary>
    public const uint RgbRock = 0xF2C14E;   // 亮金土（暖色 = 岩）

    /// <summary>
    /// 排弃土（已堆到排土场的料）。
    ///
    /// <para><b>为什么单独一色，而不是沿用源物料色</b>：料一旦排下去就是**排弃物**，
    /// 它是煤是岩在排土场上已经不构成区别 —— 库容按占容方扣、边坡按排弃土的稳定角算，
    /// 都与"原来是哪一层"无关。图上若还按源物料分色，会让人以为排土场里分着煤和岩两种堆，
    /// 那是不存在的分别。</para>
    /// </summary>
    public const uint RgbDumped = 0xE0764A;   // 亮砖红（与岩同暖但更红，一眼分得开）

    private readonly IBackend? _backend;

    public bool Available { get; }
    public string StatusLabel { get; }
    public uint CoalRgb => RgbCoal;
    public uint RockRgb => RgbRock;
    public uint DumpedRgb => RgbDumped;

    /// <summary>活动块体模型名（统计用；不可用时空）。</summary>
    public string ModelName { get; }

    private SimMaterialColorProvider(IBackend? backend, bool ok, string label, string modelName)
    {
        _backend = backend; Available = ok; StatusLabel = label; ModelName = modelName;
    }

    /// <summary>探测当前活动块体模型并建着色源。每一环断了都给人读原因，不静默。</summary>
    public static SimMaterialColorProvider Create()
    {
        var b = Backend;
        if (b == null)
            return new SimMaterialColorProvider(null, false,
                "物料分色不可用：块体模块未就绪（块体采样后端未接入）→ 层体按去向类型着色。", "");

        string? name;
        try { name = b.ActiveModelName; }
        catch (Exception ex)
        {
            return new SimMaterialColorProvider(null, false,
                $"物料分色不可用：块体模块未就绪（{ex.GetType().Name}）→ 层体按去向类型着色。", "");
        }

        if (name == null)
            return new SimMaterialColorProvider(null, false,
                "物料分色不可用：**当前没有活动块体模型**（在「块体模型」里打开/新建一个并设为活动）→ 层体按去向类型着色。", "");

        string label;
        bool ok;
        try { ok = b.TryDescribe(out label); }
        catch (Exception ex) { ok = false; label = ex.Message; }
        if (!ok)
            return new SimMaterialColorProvider(b, false,
                $"物料分色不可用：块体模型「{name}」上找不到煤岩类型属性列"
              + "（在「关联煤岩属性」里指定哪一列是煤岩类型、哪些码是煤/岩）→ 层体按去向类型着色。", name);

        return new SimMaterialColorProvider(b, true, label, name);
    }

    // ═════════════════════════ ① 逐顶点分色 ═════════════════════════

    /// <inheritdoc/>
    public byte[] ClassifyPoints(double[] worldXyz)
    {
        int np = worldXyz == null ? 0 : worldXyz.Length / 3;
        var res = new byte[Math.Max(0, np)];
        if (!Available || _backend == null || np == 0) return res;
        try { return _backend.ClassifyPoints(worldXyz!) ?? res; }
        catch { return res; }
    }

    // ═════════════════════════ ② 煤/岩体积构成 ═════════════════════════

    /// <summary>层体实体范围内按块体统计煤/岩体积。统计不出来就如实说，绝不拿按份额估的数冒充块体统计。</summary>
    public SimBlockComposition SampleComposition(SimSolidLayer layer)
    {
        var res = new SimBlockComposition();
        if (!Available || _backend == null)
        {
            res.Reason = "煤/岩构成：" + StatusLabel;
            return res;
        }
        if (!layer.HasGeometry || layer.SampleCount < 3)
        {
            res.Reason = "煤/岩构成：本期未建出层体，无空间可统计。";
            return res;
        }
        try { return _backend.SampleComposition(layer) ?? res; }
        catch (Exception ex)
        {
            res.Reason = $"煤/岩构成：统计失败（{ex.GetType().Name}: {ex.Message}）";
            return res;
        }
    }
}
