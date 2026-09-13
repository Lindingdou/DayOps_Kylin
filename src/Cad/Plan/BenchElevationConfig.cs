using System;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 「标注台阶标高」的样式配置（大小/颜色/角度等；原 <c>BenchElevationConfig</c>），经用户设置持久化（键 <see cref="SettingsKey"/>），
/// 由按钮下拉的「标注设置…」编辑，标注时自动套用。
/// </summary>
public sealed class BenchElevationConfig
{
    public const string SettingsKey = "bench.elevation.config";

    /// <summary>符号大小（米）；&lt;=0 表示按数据范围自动取。</summary>
    public double SizeMeters { get; set; } = 0;
    /// <summary>把文字落到平盘中央（坡顶线↔坡底线夹出的平盘）；缺对向线则标在线上。</summary>
    public bool PlaceOnBenchCenter { get; set; } = true;
    /// <summary>倾斜轴：0=不倾斜(正立) / 1=绕 X / 2=绕 Y / 3=绕 Z。</summary>
    public int TiltAxis { get; set; } = 1;
    /// <summary>倾斜角（度，带符号；正=逆时针，负=顺时针）。</summary>
    public double TiltDeg { get; set; } = 30;
    /// <summary>true=按区域类别自动配色（采场橙/排土场蓝…）；false=统一用 <see cref="FixedColorRgb"/>。</summary>
    public bool AutoColorByCategory { get; set; } = true;
    /// <summary>统一固定颜色（0xRRGGBB），仅 <see cref="AutoColorByCategory"/>=false 时生效。</summary>
    public uint FixedColorRgb { get; set; } = 0xFFE000;
    /// <summary>字体别名（SimSun/SimHei/FangSong/KaiTi/Arial）；空 = 引擎默认(宋体)。</summary>
    public string FontName { get; set; } = "";

    public BenchElevationAnnotator.Options ToOptions() => new()
    {
        SymbolSize = SizeMeters, PlaceOnBenchCenter = PlaceOnBenchCenter, TiltAxis = TiltAxis, TiltDeg = TiltDeg,
        FixedColorRgb = AutoColorByCategory ? null : FixedColorRgb, FontName = FontName ?? "",
    };

    /// <summary>读保存的配置；无记录/读坏时用默认（原 IUserSettings.Get 的兜底）。</summary>
    public static BenchElevationConfig Load()
    {
        try { return UserSettings.Current.Get<BenchElevationConfig>(SettingsKey) ?? new BenchElevationConfig(); }
        catch { return new BenchElevationConfig(); }
    }

    public static void Save(BenchElevationConfig cfg)
    {
        try { UserSettings.Current.Set(SettingsKey, cfg); UserSettings.Current.Flush(); } catch { }
    }
}
