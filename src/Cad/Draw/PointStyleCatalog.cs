using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>点样式(PDMODE)的常用界面选项。基础符号可叠加外接圆或外接方框。</summary>
public readonly record struct PointStyleOption(int Code, string Label);

public static class PointStyleCatalog
{
    private static readonly PointStyleOption[] _options =
    {
        new(2,  "Cross (十字) [2]"),
        new(3,  "X (叉) [3]"),
        new(0,  "Dot (圆点) [0]"),
        new(4,  "Vertical (竖线) [4]"),
        new(34, "Circle + Cross (圆+十字) [34]"),
        new(35, "Circle + X (圆+叉) [35]"),
        new(32, "Circle + Dot (圆+点) [32]"),
        new(36, "Circle + Vertical (圆+竖线) [36]"),
        new(66, "Square + Cross (方框+十字) [66]"),
        new(67, "Square + X (方框+叉) [67]"),
        new(64, "Square + Dot (方框+点) [64]"),
        new(68, "Square + Vertical (方框+竖线) [68]"),
    };

    public static IReadOnlyList<PointStyleOption> Options => _options;

    public static string LabelFor(int code)
        => _options.FirstOrDefault(x => x.Code == code).Label is { Length: > 0 } label
            ? label
            : $"PDMODE {Math.Clamp(code, 0, 127)}";

    public static int CodeFor(string label)
    {
        var option = _options.FirstOrDefault(x => x.Label == label);
        if (option.Label is { Length: > 0 }) return option.Code;

        if (int.TryParse(label.Trim(), out int numeric)) return Math.Clamp(numeric, 0, 127);
        if (label.StartsWith("PDMODE ", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(label[7..].Trim(), out int pdmode)) return Math.Clamp(pdmode, 0, 127);

        int open = label.LastIndexOf('['), close = label.LastIndexOf(']');
        return open >= 0 && close > open && int.TryParse(label[(open + 1)..close], out int code)
            ? Math.Clamp(code, 0, 127)
            : 2;
    }
}
