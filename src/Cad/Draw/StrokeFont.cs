using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 单笔画矢量字体 —— 把数字/常用符号绘成线段（7 段数码管风格），供 GL 线管线渲染文字，无需 Skia。
/// 覆盖 0-9 . - + : / 空格 与大写 X Y Z M（坐标/单位常用）；其余字符跳过(记录：中文字形待做)。
/// 坐标在单位盒内：宽 0.6、高 1.0。纯逻辑、可单测。
/// </summary>
public static class StrokeFont
{
    // 7 段：a上 b右上 c右下 d下 e左下 f左上 g中
    private const int A = 1, B = 2, C = 4, D = 8, E = 16, F = 32, G = 64;
    private static readonly Dictionary<char, int> Digit = new()
    {
        ['0'] = A | B | C | D | E | F,
        ['1'] = B | C,
        ['2'] = A | B | G | E | D,
        ['3'] = A | B | G | C | D,
        ['4'] = F | G | B | C,
        ['5'] = A | F | G | C | D,
        ['6'] = A | F | G | E | D | C,
        ['7'] = A | B | C,
        ['8'] = A | B | C | D | E | F | G,
        ['9'] = A | B | C | D | F | G,
    };

    /// <summary>字符 → 单位盒内的线段（x0,y0,x1,y1）；未知字符返回空。</summary>
    public static List<(double x0, double y0, double x1, double y1)> Strokes(char c)
    {
        var s = new List<(double, double, double, double)>();
        void Seg(double x0, double y0, double x1, double y1) => s.Add((x0, y0, x1, y1));

        if (Digit.TryGetValue(c, out int mask))
        {
            if ((mask & A) != 0) Seg(0, 1, 0.6, 1);
            if ((mask & B) != 0) Seg(0.6, 1, 0.6, 0.5);
            if ((mask & C) != 0) Seg(0.6, 0.5, 0.6, 0);
            if ((mask & D) != 0) Seg(0, 0, 0.6, 0);
            if ((mask & E) != 0) Seg(0, 0.5, 0, 0);
            if ((mask & F) != 0) Seg(0, 1, 0, 0.5);
            if ((mask & G) != 0) Seg(0, 0.5, 0.6, 0.5);
            return s;
        }
        switch (c)
        {
            case '-': Seg(0, 0.5, 0.6, 0.5); break;
            case '+': Seg(0, 0.5, 0.6, 0.5); Seg(0.3, 0.8, 0.3, 0.2); break;
            case '.': Seg(0.25, 0, 0.35, 0); break;
            case ':': Seg(0.3, 0.7, 0.3, 0.6); Seg(0.3, 0.4, 0.3, 0.3); break;
            case '/': Seg(0, 0, 0.6, 1); break;
            case 'X': case 'x': Seg(0, 1, 0.6, 0); Seg(0, 0, 0.6, 1); break;
            case 'Y': case 'y': Seg(0, 1, 0.3, 0.5); Seg(0.6, 1, 0.3, 0.5); Seg(0.3, 0.5, 0.3, 0); break;
            case 'Z': case 'z': Seg(0, 1, 0.6, 1); Seg(0.6, 1, 0, 0); Seg(0, 0, 0.6, 0); break;
            case 'M': case 'm': Seg(0, 0, 0, 1); Seg(0, 1, 0.3, 0.5); Seg(0.3, 0.5, 0.6, 1); Seg(0.6, 1, 0.6, 0); break;
            case ' ': break;
        }
        return s;
    }
}
