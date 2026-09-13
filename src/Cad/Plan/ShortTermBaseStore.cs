using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 短期计划【基础约束】的落盘。
///
/// <para><b>为什么必须有这个</b>：<see cref="ShortTermSchemeStore.Base"/> 是纯内存静态类
/// （全类无 Save/Load），而同一个窗口里的「保存逐月表」是<b>落盘</b>的
/// （<see cref="MonthlyTargetTable.Save"/> → 桌面\短期计划配置\逐月配置表.csv）。
/// 两者跨会话必然对不上：下次开机 CSV 还在、盘子没了 ——
/// 逐月表会先按<b>默认样例</b>（年 1000 万t / 6500 万m³）重新派生，再贴上去年那份人工覆盖。
/// 于是派生值来自样例、覆盖值来自去年的表，而界面上两者看不出区别。</para>
///
/// <para>代码自己早就记过这笔账（<c>MonthlyTargetTable.cs:936</c> 原文：
/// 「<c>ShortTermBase</c> <b>今天根本没有落盘</b>」）。</para>
///
/// <para><b>只落"人填的"那几段</b>：年目标 / 时间骨架 / 硬约束 / 放坡三件套。
/// 作业面、三量、比选权重不在这里 —— 它们要么由别处算、要么另有归属，
/// 混进来会变成第二份来源。</para>
/// </summary>
public static class ShortTermBaseStore
{
    public const string FileName = "基础约束.csv";

    /// <summary>与逐月配置表同目录 —— 两份数据同生同灭，别一个在桌面一个在别处。</summary>
    public static string DefaultRoot => MonthlyTargetStore.DefaultRoot;
    public static string DefaultPath => Path.Combine(DefaultRoot, FileName);

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>原子写：先写 .tmp 再替换，中途失败原文件一个字节都没动。</summary>
    public static bool Save(ShortTermBase b, out string path, out string error, string? file = null)
    {
        path = string.IsNullOrWhiteSpace(file) ? DefaultPath : file!;
        error = "";
        if (b == null) { error = "没有可保存的基础约束。"; return false; }
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("# 短期计划 · 基础约束（人填的那几段；作业面/三量/比选权重不在这里）");
            sb.AppendLine("# 与「逐月配置表.csv」同目录、同生同灭。改这里之后逐月表会提示不同步。");
            sb.AppendLine("键,值");
            void P(string k, double v) => sb.AppendLine(Ci, $"{k},{v.ToString("0.####", Ci)}");
            void S(string k, string v) => sb.AppendLine($"{k},{Esc(v)}");

            S("来源方案", b.SourceLongTermName ?? "");
            P("计划年份", b.PlanYear);
            P("起始月", b.StartMonth);
            P("月数", b.MonthCount);
            P("年采出万t", b.AnnualCoalTargetWanT);
            P("年剥离万m3", b.AnnualStripTargetWanM3);
            P("月产上限万t", b.MonthlyCoalCeilingWanT);
            P("月剥离上限万m3", b.MonthlyStripCeilingWanM3);
            P("剥采比上限", b.RatioCeiling);
            P("完成率容差pct", b.CompletionTolerancePct);
            P("备采保有下限月", b.MinPreparedMonths);
            P("台阶高m", b.BenchHeightM);
            P("工作线长m", b.WorkLineLenM);
            // 放坡三件套：0 = 没测过，原样落盘（不许写成缺省值，见 ShortTermBase 上的注释）
            P("坡面角deg", b.BenchFaceAngleDeg);
            P("平盘宽m", b.BermWidthM);
            P("帮坡角deg", b.OverallSlopeAngleDeg);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception ex) { error = "保存失败：" + ex.Message; return false; }
    }

    /// <summary>
    /// 读回并<b>就地填进</b><paramref name="b"/>。
    /// <para>读不出来如实报，<b>不返回一份默认值冒充成功</b> ——
    /// 一份"看上去正常的默认盘子"会一路传到逐月表、派生方案、三维，而每个数都正常。</para>
    /// <para>文件里没有的键<b>保持原值不动</b>（新增字段时老文件仍能读）。</para>
    /// </summary>
    public static bool TryLoadInto(ShortTermBase b, out List<string> issues, string? file = null)
    {
        issues = new List<string>();
        if (b == null) { issues.Add("没有可填充的对象。"); return false; }
        string path = string.IsNullOrWhiteSpace(file) ? DefaultPath : file!;
        if (!File.Exists(path))
        { issues.Add($"还没有基础约束存档（{path}）—— 在「短期生产计划编制」里点「保存约束」即可。"); return false; }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { issues.Add("◆ 读文件失败：" + ex.Message); return false; }

        // 严格 UTF-8：非 UTF-8 的中文键名会全成乱码 ⇒ 每个键都匹配不上 ⇒
        // 读回来是一份原值不动的"成功"，而界面会说已载入。宁可拒收。
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            issues.Add("◆ 文件不是 UTF-8（Excel 中文环境另存 .csv 默认 GB18030）—— 拒读。"
                     + "请用「CSV UTF-8」另存，或在本窗口重新「保存约束」。");
            return false;
        }
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

        int hit = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("键,")) continue;
            int i = line.IndexOf(',');
            if (i <= 0) continue;
            string k = line.Substring(0, i).Trim(), v = Unesc(line.Substring(i + 1).Trim());
            double d;
            bool num = double.TryParse(v, NumberStyles.Float, Ci, out d);
            switch (k)
            {
                case "来源方案": b.SourceLongTermName = v; hit++; break;
                case "计划年份": if (num) { b.PlanYear = (int)d; hit++; } break;
                case "起始月": if (num) { b.StartMonth = (int)d; hit++; } break;
                case "月数": if (num) { b.MonthCount = (int)d; hit++; } break;
                case "年采出万t": if (num) { b.AnnualCoalTargetWanT = d; hit++; } break;
                case "年剥离万m3": if (num) { b.AnnualStripTargetWanM3 = d; hit++; } break;
                case "月产上限万t": if (num) { b.MonthlyCoalCeilingWanT = d; hit++; } break;
                case "月剥离上限万m3": if (num) { b.MonthlyStripCeilingWanM3 = d; hit++; } break;
                case "剥采比上限": if (num) { b.RatioCeiling = d; hit++; } break;
                case "完成率容差pct": if (num) { b.CompletionTolerancePct = d; hit++; } break;
                case "备采保有下限月": if (num) { b.MinPreparedMonths = d; hit++; } break;
                case "台阶高m": if (num) { b.BenchHeightM = d; hit++; } break;
                case "工作线长m": if (num) { b.WorkLineLenM = d; hit++; } break;
                case "坡面角deg": if (num) { b.BenchFaceAngleDeg = d; hit++; } break;
                case "平盘宽m": if (num) { b.BermWidthM = d; hit++; } break;
                case "帮坡角deg": if (num) { b.OverallSlopeAngleDeg = d; hit++; } break;
            }
        }

        // 一个键都没认出来 = 这多半不是这个文件（或编码坏了）—— 别当成"读成功但内容为空"
        const int MinKeys = 3;
        if (hit < MinKeys)
        {
            issues.Add($"◆ 只认出 {hit} 个键（至少要 {MinKeys} 个）—— 这多半不是基础约束存档，本次没有改动任何参数。");
            return false;
        }
        return true;
    }

    private static string Esc(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Unesc(string s)
        => s.Length >= 2 && s[0] == '"' && s[^1] == '"'
            ? s.Substring(1, s.Length - 2).Replace("\"\"", "\"") : s;
}
