// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/MiningModelPlanner.cs 的 Strip 内嵌类 + DefaultCoalDensity
//（逐行对应；仅命名空间适配）。Kylin 侧采矿模型规划器本体在别处，这里只留台账（MiningUnitLedger.FromStrips /
// UnitRailFile.AddStrips）消费的那份带契约，免得台账层为了一个 DTO 拖进整个规划器。
using System;

namespace PitMine3D.Kylin.UnitLedger;

public static class MiningModelPlanner
{
    /// <summary>煤视密度默认值（t/m³）。</summary>
    public const double DefaultCoalDensity = 1.35;

    /// <summary>一条采掘带 = 一个待建的采矿模型体。</summary>
    public sealed class Strip
    {
        public string SeamCode = "";
        /// <summary>本带在该层煤里的序号（1 起，仅用于命名/定位）。</summary>
        public int Index;
        /// <summary>本带在该层煤里的带号（同一条连续露头带的各幅共用）。</summary>
        public int BandId;

        /// <summary>同一条露头带切出的第几幅 / 共几幅。</summary>
        public int PanelIndex = 1, PanelCount = 1;

        /// <summary>前脸上沿（顶板），扁平 xyz。喂内核的 crest。</summary>
        public double[] CrestXyz = Array.Empty<double>();
        /// <summary>前脸下沿（底板），扁平 xyz。喂内核的 toe。</summary>
        public double[] ToeXyz = Array.Empty<double>();

        /// <summary>走向长度（m，水平）。</summary>
        public double StrikeLenM;
        /// <summary>本带平均煤厚（m）= 前脸高度均值。</summary>
        public double ThickM;
        /// <summary>现状面在该处的坡度（°）。</summary>
        public double SurfaceSlopeDeg;

        /// <summary>估算体积（m³）= 走向长 × W × 煤厚。</summary>
        public double EstVolumeM3;

        /// <summary>本带实际推进宽度（m）。默认 = W；前方 40m 内有【别的带】时收到两带中间。</summary>
        public double AdvanceWidthM;

        /// <summary>本带内煤占的体积（m³）—— 岩台阶专用，已从 <see cref="EstVolumeM3"/> 扣除。</summary>
        public double CoalVolumeM3;

        /// <summary>扣煤【之前】的毛体积（m³）。毛 − 煤 = 净，三个数都留着，账才对得上。</summary>
        public double GrossVolumeM3;

        public int PointCount => CrestXyz.Length / 3;
    }
}

/// <summary>原 MiningPlanExporter 里被台账引用的那一个常量。</summary>
public static class MiningPlanExporter
{
    public const double DefaultCoalDensity = MiningModelPlanner.DefaultCoalDensity;
}
