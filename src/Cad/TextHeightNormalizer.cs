using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 文字高度归一化器 —— 忠实移植原 TextHeightNormalizer: 处理导入数据里"文字高度相对图幅异常巨大/缺失"
/// (paper-space 高度当 model-space、mm 当 m 单位错、占位值)。策略 = 逐实体离群修正(非全局缩放):
///   ① 用真实几何算图幅对角线 D; ② 扫所有文字高度, 挑正常范围 [D·minFrac, D·maxFrac] 取中位数 typical;
///   ③ 逐文字: 高度 &gt; D·maxFrac(离群)或 ≤0(缺失) → 替 typical, 其余原样。全异常时 typical 兜底 D·fallbackFrac。
///   只动异常值, 正常标注不受影响。纯逻辑、可单测。
/// </summary>
public sealed class TextHeightNormalizer
{
    private readonly double _diag, _maxAllowed, _typical;

    public int CorrectedCount { get; private set; }
    public double TypicalHeight => _typical;
    public bool IsActive => _diag > 0;
    public double Diagonal => _diag;

    public TextHeightNormalizer(double bboxWidth, double bboxHeight, IEnumerable<double> allHeights,
        double maxFraction = 0.05, double minFraction = 0.0001, double fallbackFraction = 0.005)
    {
        _diag = Math.Sqrt(bboxWidth * bboxWidth + bboxHeight * bboxHeight);
        _maxAllowed = _diag * maxFraction;
        double lo = _diag * minFraction, hi = _diag * maxFraction;
        var normal = new List<double>();
        if (allHeights != null)
            foreach (var h in allHeights) if (h >= lo && h <= hi) normal.Add(h);
        if (normal.Count > 0) { normal.Sort(); _typical = normal[normal.Count / 2]; }
        else _typical = _diag > 0 ? _diag * fallbackFraction : 1.0;
    }

    /// <summary>返回修正后的高度。非 active(无图幅)时: 正数原样, 非正数给 1.0。</summary>
    public double Correct(double height)
    {
        if (_diag <= 0) return height > 0 ? height : 1.0;
        if (height <= 0 || height > _maxAllowed) { CorrectedCount++; return _typical; }
        return height;
    }
}
