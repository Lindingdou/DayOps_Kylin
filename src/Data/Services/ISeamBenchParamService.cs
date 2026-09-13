// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ISeamBenchParamService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 逐煤层台阶参数服务（煤的采矿模型·倾斜分层）。
///
/// 这是【覆盖层】：区间 / 报警上下限 / 全矿默认值仍归 <c>parameter_definition</c> 的 V034 三项
/// （coal_bench_height 2007 / coal_bench_slope_angle 2008 / coal_platform_width 2009）。
/// <see cref="Effective"/> 负责把「本表覆盖 → 全矿默认 → 代码兜底」这条回落链走完，
/// 调用方拿到的就是可以直接用于建模的实数，不必各处重复这套回落（那是漂移的温床）。
/// </summary>
public interface ISeamBenchParamService
{
    /// <summary>全部逐煤层参数行（按 coal_seam_def.sort_order）。</summary>
    IReadOnlyList<SeamBenchParam> All(bool activeOnly = true);

    /// <summary>某层煤的参数行（默认方案）。没有返回 null。</summary>
    SeamBenchParam? Get(string seamCode, string? designVersion = null);

    /// <summary>按 (seam_code, design_version) upsert。返回行 id。</summary>
    long Upsert(SeamBenchParam entity);

    void Delete(long id);

    /// <summary>
    /// 给 <c>coal_seam_def</c> 里有、但本表还没建行的煤层补空行（全部回落默认）。
    /// 返回新建行数。供参数分组窗口打开时保证「煤层列得全」——
    /// 地质模型里有顶底板面、字典里也有的煤层，不该因为没建参数行就从分组里消失。
    /// </summary>
    int EnsureRowsForAllSeams();

    /// <summary>
    /// 取某层煤【生效】的台阶参数：本表覆盖 → parameter_definition 默认 → 代码兜底。
    /// 任何一层缺失都不抛，最终必定返回一组可用的数。
    /// </summary>
    EffectiveSeamBench Effective(string seamCode, string? designVersion = null);
}

/// <summary>
/// 某层煤最终生效的台阶参数（回落链已走完，全是实数，可直接喂建模）。
/// <see cref="Sources"/> 记每项从哪一级取到的，供界面标注"这项是覆盖值 / 全矿默认"。
/// </summary>
public sealed class EffectiveSeamBench
{
    public string SeamCode = "";

    public double BenchHeightM;
    public double BenchSlopeAngleDeg;
    public double BermWidthM;
    public double StripWidthM;
    public double MinMineableThickM;

    /// <summary>true = 倾斜分层（跟煤层倾向）；false = 顶底水平。</summary>
    public bool Inclined = true;

    /// <summary>true = 从底板起算往上切分层。</summary>
    public bool FloorDatum = true;

    /// <summary>字段名 → 来源（"覆盖" / "全矿默认" / "兜底"）。</summary>
    public readonly Dictionary<string, string> Sources = new();

    /// <summary>坡面水平投影 H/tanα（m）—— 台阶几何的基本量。</summary>
    public double FaceRunM
    {
        get
        {
            double a = BenchSlopeAngleDeg;
            if (a <= 0 || a >= 90) return 0;
            return BenchHeightM / System.Math.Tan(a * System.Math.PI / 180.0);
        }
    }
}
