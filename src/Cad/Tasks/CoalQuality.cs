using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks;

/// <summary>
/// 煤质（灰分/热值/硫分/水分）+ 达标判定——忠实移植 TaskLib.Domain.CoalQuality。
/// 另加 Blend(按吨量加权混合)：灰/热/硫为质量性质, 配煤物理上按吨量加权(标准), 供配煤核算。
/// </summary>
public sealed class CoalQuality
{
    public double AshPct { get; set; }        // 灰分 %
    public double CalorificMJkg { get; set; } // 热值 MJ/kg
    public double SulfurPct { get; set; }     // 硫分 %
    public double MoisturePct { get; set; }   // 水分 %

    public string Caption => $"灰{AshPct:0.#}% · 热{CalorificMJkg:0.#}MJ/kg · 硫{SulfurPct:0.##}%";

    /// <summary>是否在目标允许带内（实测对目标，越限项返回 false）。忠实原口径。</summary>
    public bool MeetsTarget(CoalQuality target, double ashTolPct = 1.0, double cvTolMJ = 1.0, double sTolPct = 0.1)
        => AshPct <= target.AshPct + ashTolPct
        && CalorificMJkg >= target.CalorificMJkg - cvTolMJ
        && SulfurPct <= target.SulfurPct + sTolPct;

    /// <summary>综合配煤入仓标准（忠实 ExploderConfig.BlendStandard 默认值）。</summary>
    public static CoalQuality Standard => new() { AshPct = 12.8, CalorificMJkg = 21.5, SulfurPct = 0.7 };

    /// <summary>按吨量加权混合多路煤质（配煤核算：灰/热/硫/水按质量加权）。空/零吨返回全零。</summary>
    public static CoalQuality Blend(IEnumerable<(double tonnage, CoalQuality q)> sources)
    {
        var list = sources?.Where(s => s.tonnage > 0 && s.q != null).ToList() ?? new();
        double t = list.Sum(s => s.tonnage);
        if (t <= 1e-9) return new CoalQuality();
        return new CoalQuality
        {
            AshPct = list.Sum(s => s.tonnage * s.q.AshPct) / t,
            CalorificMJkg = list.Sum(s => s.tonnage * s.q.CalorificMJkg) / t,
            SulfurPct = list.Sum(s => s.tonnage * s.q.SulfurPct) / t,
            MoisturePct = list.Sum(s => s.tonnage * s.q.MoisturePct) / t,
        };
    }
}
