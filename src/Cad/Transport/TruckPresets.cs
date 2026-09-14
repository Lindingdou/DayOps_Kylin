using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PitMine3D.Kylin.Cad.Transport;

/// <summary>预设来源 —— 下拉里必须能区分"经验档"与"矿上在册车型"，两者口径完全不同。</summary>
public enum TruckPresetSource
{
    /// <summary>吨级经验档（典型经验值，不对应任何一台具体在册设备）。</summary>
    Empirical,
    /// <summary>设备库在册车型（<c>equipment_model</c> + <c>equipment_constraint</c>）。</summary>
    Registry,
}

/// <summary>
/// 一档卡车预设，供「约束条件设置」的车型下拉带出设备参数。
///
/// 【关键约定】所有几何/能力字段都是可空的：
///   有值 = 本档确实知道这个参数，切车型时覆盖 UI；
///   null = 本档【没有】这个参数，切车型时【不覆盖】用户当前输入，
///          并由对话框列进"未随车型更新"提示请人手工确认。
/// 之所以不填满：设备库里根本没有轴距/转弯半径这些列（见 <see cref="TruckPresets"/> 注释），
/// 凭空编造经验值会让弯道加宽 ε=车道·L²/(2R) 按一个假轴距算出假结果，**比留空更危险**。
/// </summary>
public sealed record TruckPreset(
    string Name,
    TruckPresetSource Source,
    double? WidthM = null,
    double? ClimbPct = null,
    double? TurnRadiusM = null,
    double? TireDiameterM = null,
    double? MaxGradePct = null,
    double? PayloadT = null,
    double? WheelbaseM = null)
{
    /// <summary>下拉显示名：在册车型带"· 在册"后缀，免得与吨级经验档混为一谈。</summary>
    public string DisplayName
        => Source == TruckPresetSource.Registry ? $"{Name} · 在册" : Name;

    /// <summary>
    /// 本档【未提供】的字段中文名清单（顺序与对话框 ① 区一致）。
    /// 切换车型后这些字段仍是上一次的值，**必须当场提示** —— 静默沿用就是错算的来源。
    /// </summary>
    public IReadOnlyList<string> MissingFieldNames()
    {
        var list = new List<string>();
        if (!WidthM.HasValue) list.Add("车宽");
        if (!ClimbPct.HasValue) list.Add("爬坡度");
        if (!TurnRadiusM.HasValue) list.Add("最小转弯半径");
        if (!TireDiameterM.HasValue) list.Add("轮胎直径");
        if (!MaxGradePct.HasValue) list.Add("限制坡度");
        if (!PayloadT.HasValue) list.Add("额定载重");
        if (!WheelbaseM.HasValue) list.Add("车辆轴距");
        return list;
    }
}

/// <summary>
/// 卡车预设表：吨级经验档 + 设备库在册车型（移植 <c>MineAssLib.Models.TruckPresets</c>）。
///
/// 【设备库现状】—— 别指望能带全六项，实际只带得出两项：
///   <c>equipment_model</c> 列：model / category / working_weight_t / power_kw / bucket_m3 /
///                             load_t / dimensions_lwh / drill_diameter_mm / tire_spec / std_daily_cap_wan_m3
///   · 额定载重 ← <c>load_t</c>                    ✔
///   · 爬坡度   ← <c>equipment_constraint</c>      ✔ 但只有个别型号录了
///   · 车宽     ← <c>dimensions_lwh</c>            ✘ 现网多为空
///   · 轮胎直径 ← <c>tire_spec</c>                 ✘ 该列存的是规格串（"37.00R57"）不是直径，**不做经验换算**
///   · 最小转弯半径                                ✘ 表里【根本没有这一列】
///   · 车辆轴距                                    ✘ 表里【根本没有这一列】
///
/// 【后续怎么把缺口补上】（补完不用改调用方，自动生效）：
///   ① 车宽：把外形尺寸回填进 <c>equipment_model.dimensions_lwh</c>（"长 × 宽 × 高"），
///      <see cref="ParseWidthM"/> 会自动取中间那个数；
///   ② 轮胎直径 / 最小转弯半径 / 轴距：<c>equipment_model</c> 需先加列（迁移 + 读取处接上），
///      在此之前一律留 null，**绝不用经验值冒充在册车型的实测参数**。
///
/// ── Kylin 侧的实现差异（登记）──
/// 原版走 <c>EquipmentDataContext</c> 仓储门面，Kylin 没有服务层，这里直接对表发 SQL；
/// 字段取舍（哪些带、哪些一律留 null）一字不差。
/// </summary>
public static class TruckPresets
{
    /// <summary>"自定义"档名 —— 不带任何参数，选中它全靠手填，不覆盖任何字段。</summary>
    public const string CustomName = "自定义";

    /// <summary>
    /// 设备库里"最大坡度"参数的 code（卡车爬坡能力挂在它下面当设备约束）。
    /// 按 code 取参数，**不写死 param_id**。
    /// </summary>
    private const string CodeRoadMaxSlope = "road_max_slope_pct";

    /// <summary>
    /// 吨级经验档（典型经验值，可改）。
    /// 【轴距一律为 null】—— 四个吨级的轴距没有可靠出处，不编。
    /// </summary>
    public static IReadOnlyList<TruckPreset> Empirical { get; } = new[]
    {
        new TruckPreset("35t级",  TruckPresetSource.Empirical,
                        WidthM: 3.5, ClimbPct: 12.0, TurnRadiusM:  8.5, TireDiameterM: 1.9,
                        MaxGradePct: 10.0, PayloadT:  35.0),
        new TruckPreset("100t级", TruckPresetSource.Empirical,
                        WidthM: 6.0, ClimbPct: 10.0, TurnRadiusM: 10.0, TireDiameterM: 2.7,
                        MaxGradePct:  8.0, PayloadT:  90.0),
        new TruckPreset("220t级", TruckPresetSource.Empirical,
                        WidthM: 7.7, ClimbPct:  8.0, TurnRadiusM: 15.0, TireDiameterM: 3.9,
                        MaxGradePct:  8.0, PayloadT: 220.0),
        new TruckPreset("360t级", TruckPresetSource.Empirical,
                        WidthM: 9.0, ClimbPct:  8.0, TurnRadiusM: 16.0, TireDiameterM: 4.0,
                        MaxGradePct:  8.0, PayloadT: 330.0),
    };

    /// <summary>
    /// 从设备库读在册卡车型号，**只带出设备库确实存了的字段**，其余留 null。
    /// 库不可用 / 表读不到 → 返回空表（对话框降级为只有经验档），绝不抛。
    /// </summary>
    public static IReadOnlyList<TruckPreset> FromEquipmentLibrary(DbConnection? conn)
    {
        if (conn == null) return Array.Empty<TruckPreset>();
        try
        {
            var climbByModel = ReadClimbConstraints(conn);

            var list = new List<TruckPreset>();
            using (var cmd = conn.CreateCommand())
            {
                // category 的取值在种子里是中文「卡车」；两种写法都收，免得换一批种子就整片读不出来
                cmd.CommandText = "SELECT model, load_t, dimensions_lwh FROM equipment_model "
                                + "WHERE category = '卡车' OR LOWER(category) = 'truck'";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    string model = rd.IsDBNull(0) ? "" : rd.GetValue(0)?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(model)) continue;
                    double? load = rd.IsDBNull(1) ? null : Convert.ToDouble(rd.GetValue(1), CultureInfo.InvariantCulture);
                    string dims = rd.IsDBNull(2) ? "" : rd.GetValue(2)?.ToString() ?? "";

                    list.Add(new TruckPreset(
                        model, TruckPresetSource.Registry,
                        WidthM: ParseWidthM(dims),
                        ClimbPct: climbByModel.TryGetValue(model, out double c) ? c : null,
                        TurnRadiusM: null,     // 设备库无此列
                        TireDiameterM: null,   // tire_spec 是规格串不是直径，不做经验换算
                        MaxGradePct: null,     // 限制坡度是设计取值（规范封顶），不等于设备爬坡能力，不代填
                        PayloadT: load,
                        WheelbaseM: null));    // 设备库无此列
                }
            }
            return list.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        }
        catch
        {
            return Array.Empty<TruckPreset>();   // 库未就绪 → 只剩经验档
        }
    }

    /// <summary>
    /// 读"最大坡度"参数下的设备爬坡约束：型号 → 爬坡度 %。
    /// 约束表读不到就返回空表，让爬坡度整体留空，**不牵连其余字段**。
    /// </summary>
    private static Dictionary<string, double> ReadClimbConstraints(DbConnection conn)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT c.equipment_model, c.limit_value FROM equipment_constraint c "
              + "JOIN parameter_definition p ON p.param_id = c.param_id "
              + "WHERE p.code = '" + CodeRoadMaxSlope + "' AND LOWER(c.constraint_type) = 'max'";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                if (rd.IsDBNull(0) || rd.IsDBNull(1)) continue;
                string m = rd.GetValue(0)?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(m)) continue;
                map[m] = Convert.ToDouble(rd.GetValue(1), CultureInfo.InvariantCulture);
            }
        }
        catch { /* 约束表不可用 → 爬坡度留空 */ }
        return map;
    }

    /// <summary>下拉的完整档位表：自定义 + 吨级经验档 + 在册车型。</summary>
    public static List<TruckPreset> AllFor(DbConnection? conn)
    {
        var list = new List<TruckPreset> { new(CustomName, TruckPresetSource.Empirical) };
        list.AddRange(Empirical);
        list.AddRange(FromEquipmentLibrary(conn));
        return list;
    }

    /// <summary>
    /// 从设备库的"外形尺寸长宽高"串里取车宽（长 × 【宽】 × 高，中间那个数）。
    /// **只做纯解析、不做任何经验换算**；数字不足三个（如只写了"长×宽"）一律判为拿不到。
    /// </summary>
    internal static double? ParseWidthM(string? dimensionsLwh)
    {
        if (string.IsNullOrWhiteSpace(dimensionsLwh)) return null;
        var nums = new List<double>();
        foreach (Match mm in Regex.Matches(dimensionsLwh, @"\d+(?:\.\d+)?"))
            if (double.TryParse(mm.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                nums.Add(v);
        return nums.Count >= 3 ? nums[1] : null;
    }
}
