using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 煤厚分析（对应原 钻孔管理·煤厚分析）——逐钻孔累计煤层(岩性含「煤」)的分层厚度 Σ(至−自)。
/// 数据来自钻孔 CSV(孔号,X,Y,高程,自,至,岩性)非 DM8; 纯计算、可单测。
/// </summary>
public static class CoalThicknessAnalyzer
{
    /// <summary>一组分层(自,至,岩性)的累计煤厚(岩性含「煤」的层)。</summary>
    public static double CoalThickness(IEnumerable<(double from, double to, string rock)> intervals)
    {
        double t = 0;
        if (intervals == null) return 0;
        foreach (var (from, to, rock) in intervals)
            if (!string.IsNullOrEmpty(rock) && rock.Contains("煤")) t += Math.Abs(to - from);
        return t;
    }
}
