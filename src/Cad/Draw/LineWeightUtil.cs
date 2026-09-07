using System;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// DXF 线宽值(short: -1=随层, -2=随块, -3=默认, 0..211=0.01mm)的显示/解析/规整。
/// 对应原版特性面板"线宽"(常规类, 可编辑)。纯逻辑、可单测。
/// </summary>
public static class LineWeightUtil
{
    // 标准 DXF 线宽档(单位 0.01mm)
    private static readonly short[] Std =
        { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

    /// <summary>值 → 显示串(随层/默认/随块 或 "0.25 mm")。</summary>
    public static string Display(short lw)
    {
        if (lw == -1) return "随层";
        if (lw == -3) return "默认";
        if (lw == -2) return "随块";
        if (lw < 0) return "随层";
        return (lw / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " mm";
    }

    /// <summary>解析编辑输入(随层/默认/随块 或 mm 数, 容 "mm" 后缀)→ 规整到最近标准档。无法解析返 false。</summary>
    public static bool TryParse(string text, out short lw)
    {
        lw = -1;
        string t = text.Trim();
        if (t is "随层" or "ByLayer" or "bylayer" or "BYLAYER") { lw = -1; return true; }
        if (t is "默认" or "Default" or "default" or "DEFAULT") { lw = -3; return true; }
        if (t is "随块" or "ByBlock" or "byblock" or "BYBLOCK") { lw = -2; return true; }
        t = t.Replace("mm", "").Replace("MM", "").Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double mm) || mm < 0) return false;
        int v = (int)Math.Round(mm * 100);   // mm → 0.01mm
        lw = Snap((short)Math.Clamp(v, 0, 211));
        return true;
    }

    /// <summary>把任意 0.01mm 值规整到最近的标准线宽档。</summary>
    public static short Snap(short v)
    {
        short best = Std[0]; int bd = int.MaxValue;
        foreach (var s in Std) { int d = Math.Abs(s - v); if (d < bd) { bd = d; best = s; } }
        return best;
    }

    /// <summary>Ribbon「特性」组线宽下拉的候选值(随层/默认/随块 + 标准档)。顺序即下拉顺序。</summary>
    public static short[] Choices
    {
        get
        {
            var a = new short[3 + Std.Length];
            a[0] = -1; a[1] = -3; a[2] = -2;
            Array.Copy(Std, 0, a, 3, Std.Length);
            return a;
        }
    }
}
