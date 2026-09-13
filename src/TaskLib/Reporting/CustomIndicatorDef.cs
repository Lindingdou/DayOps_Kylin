// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/CustomIndicatorDef.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>自定义指标的计算方式。</summary>
public enum IndicatorMode { Structured, Formula }

/// <summary>结构化模式可取的事实字段。</summary>
public enum FactField
{
    PlanVol, ActualVol, PlanTonnage, ActualTonnage, PlannedHours, ActualHours, Shortfall,
    Fuel, Power, HaulTKm, HaulDistance, Ash, Calorific, Sulfur, Moisture, Count,
    // ── 采排物流（本轮新增）──
    DumpVolume,   // 排弃占容方 m³（×Kr，排土库容按此扣）
    LooseVol,     // 运输松方 m³（×Ks，配车/车厢校核）
}

/// <summary>结构化模式的聚合算子。</summary>
public enum AggOp { Sum, Avg, WeightedAvgByActualVol, Min, Max, Count }

/// <summary>物料过滤（口径）。</summary>
public enum MaterialFilter { All, Coal, Waste }

/// <summary>
/// 用户自定义指标（= 可定制的「计算规则」，纯数据、JSON 持久化）。两种模式：
///   · 结构化：字段 + 聚合 + 物料过滤（覆盖大多数量/率/煤质指标，无需写公式）；
///   · 公式：对其它指标 Id 做四则运算（如 <c>waste_vol / coal_vol</c> = 剥采比），由内置安全求值器算。
/// 由 <see cref="IndicatorRegistry.CompileCustom"/> 编译成引擎可用的 <see cref="IndicatorDef"/>（按 Id 被模板引用）。
/// </summary>
public sealed class CustomIndicatorDef
{
    public string Id { get; set; } = "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Name { get; set; } = "自定义指标";
    public string Unit { get; set; } = "";
    public string Category { get; set; } = "自定义";
    public string Format { get; set; } = "0.##";

    public IndicatorMode Mode { get; set; } = IndicatorMode.Structured;

    // 结构化
    public FactField Field { get; set; } = FactField.ActualVol;
    public AggOp Agg { get; set; } = AggOp.Sum;
    public MaterialFilter Filter { get; set; } = MaterialFilter.All;

    // 公式
    public string Formula { get; set; } = "";

    // 阈值（可选，出红黄绿）
    public double? Target { get; set; }
    public bool HigherIsBetter { get; set; } = true;
    public double WarnBand { get; set; } = 10;

    public CustomIndicatorDef ShallowCopy() => (CustomIndicatorDef)MemberwiseClone();

    // ── 字段/口径工具（结构化求值用）──
    public static double FieldValue(ProductionFact f, FactField field) => field switch
    {
        FactField.PlanVol => f.PlanVolumeM3,
        FactField.ActualVol => f.ActualVolumeM3,
        FactField.PlanTonnage => f.PlanTonnage,
        FactField.ActualTonnage => f.ActualTonnage,
        FactField.PlannedHours => f.PlannedHours,
        FactField.ActualHours => f.ActualHours,
        FactField.Shortfall => f.ShortfallM3,
        FactField.Fuel => f.FuelL,
        FactField.Power => f.PowerKwh,
        FactField.HaulTKm => f.TransportWorkTKm,
        FactField.HaulDistance => f.EffectiveHaulKm,
        FactField.Ash => f.Ash,
        FactField.Calorific => f.Calorific,
        FactField.Sulfur => f.Sulfur,
        FactField.Moisture => f.Moisture,
        FactField.DumpVolume => f.DumpVolumeM3,
        FactField.LooseVol => f.ActualLooseM3,
        _ => 1,   // Count
    };

    /// <summary>物料口径过滤。煤/岩的判定来自物料本体 <see cref="ProductionFact.IsOre"/>，不再看工序或中文串。</summary>
    public static bool Pass(ProductionFact f, MaterialFilter mf) => mf switch
    {
        MaterialFilter.Coal => f.IsCoal,
        MaterialFilter.Waste => f.IsWaste,
        _ => true,
    };
}

/// <summary>
/// 极简安全表达式求值器（递归下降）：支持 + - * / 、括号、数字、以及指标 Id 标识符。
/// 标识符经 <paramref name="resolve"/> 回调换成该指标在当前事实集上的值。无反射、无代码执行，绝不越权。
/// 解析/求值出错一律返回 NaN（报表里显示"—"，不崩）。
/// </summary>
public static class ExprEval
{
    public static double Eval(string expr, Func<string, double> resolve)
    {
        try
        {
            var p = new Parser(expr ?? "", resolve);
            double v = p.ParseExpr();
            p.ExpectEnd();
            return v;
        }
        catch { return double.NaN; }
    }

    /// <summary>只校验能否解析（供编辑器提示语法错误），不求值。resolve 恒返回 1。</summary>
    public static bool IsValid(string expr, out string error)
    {
        error = "";
        try { var p = new Parser(expr ?? "", _ => 1); p.ParseExpr(); p.ExpectEnd(); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly Func<string, double> _resolve;
        private int _i;

        public Parser(string s, Func<string, double> resolve) { _s = s; _resolve = resolve; }

        public double ParseExpr()   // 加减
        {
            double v = ParseTerm();
            while (true)
            {
                SkipWs();
                if (Peek() == '+') { _i++; v += ParseTerm(); }
                else if (Peek() == '-') { _i++; v -= ParseTerm(); }
                else return v;
            }
        }

        private double ParseTerm()  // 乘除
        {
            double v = ParseFactor();
            while (true)
            {
                SkipWs();
                if (Peek() == '*') { _i++; v *= ParseFactor(); }
                else if (Peek() == '/') { _i++; double d = ParseFactor(); v = Math.Abs(d) < 1e-12 ? double.NaN : v / d; }
                else return v;
            }
        }

        private double ParseFactor()
        {
            SkipWs();
            char c = Peek();
            if (c == '-') { _i++; return -ParseFactor(); }
            if (c == '+') { _i++; return ParseFactor(); }
            if (c == '(')
            {
                _i++;
                double v = ParseExpr();
                SkipWs();
                if (Peek() != ')') throw new FormatException("缺少右括号");
                _i++;
                return v;
            }
            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (char.IsLetter(c) || c == '_') return _resolve(ParseIdent());
            throw new FormatException($"无法解析的字符 '{c}'（位置 {_i}）");
        }

        private double ParseNumber()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            return double.Parse(_s.Substring(start, _i - start), CultureInfo.InvariantCulture);
        }

        private string ParseIdent()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            return _s.Substring(start, _i - start);
        }

        public void ExpectEnd() { SkipWs(); if (_i < _s.Length) throw new FormatException($"多余字符 '{_s[_i]}'"); }
        private char Peek() => _i < _s.Length ? _s[_i] : '\0';
        private void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
    }
}
