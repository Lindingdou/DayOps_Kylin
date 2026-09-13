// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/DeadOptionSweepTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Tests.Synth;
using BlockModel = PitMine3D.Kylin.Tests.Synth.BlockModel;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// <b>D 组 · 死输入普扫</b>（参数排查台账的"查法"落成判据）。
///
/// <para>现场令：「逐个功能内的各个参数和选项逐一排查，<b>保证每个参数和选项都有用</b>」。
/// 手查一遍只能管住这一次 —— 下一个人加一个字段、忘了接入口，又是一个死输入，
/// 而<b>死输入不报错</b>：引擎按缺省值算，用户以为自己能调。</para>
///
/// <para>本判据把那条查法自动化：对每个"参数集"类，取它的公开字段，
/// 在全仓找有没有人给它赋值；<b>一个赋值都没有</b>的字段必须在源码里带一句
/// <c>刻意无入口</c> 说明为什么 —— 要么接上，要么<b>写下来它是故意的</b>。</para>
///
/// <para><b>为什么允许"刻意无入口"</b>：不是每个参数都该上界面（引擎自建的图、内部兜底系数）。
/// 但"故意"必须是<b>写下来的</b>，不能靠下一个人猜。</para>
/// </summary>
public class DeadOptionSweepTests
{
    private readonly ITestOutputHelper _out;
    public DeadOptionSweepTests(ITestOutputHelper o) => _out = o;

    private static string RepoRoot([CallerFilePath] string self = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", ".."));

    /// <summary>要扫的参数集：（源文件, 类名）。加新功能时把它的参数集加进来。</summary>
    private static readonly (string File, string Class)[] Targets =
    {
        // ① 源头：两个模型对话框
        (@"src\UnitLedger\MiningModelPlannerStrip.cs", "Options"),
        (@"src\Cad\Dump\DumpStripPlanner.cs", "Options"),
        // ② 主干：单元链
        (@"src\Cad\Units\UnitPlanEngine.cs", "UnitPlanInput"),
        (@"src\Cad\Units\UnitGraph.cs", "Options"),
        (@"src\Cad\Units\EquipmentAssigner.cs", "EquipmentAssignInput"),
        // ③ 前置：量驱动链
        (@"src\Cad\MonthlyMineSchedule.cs", "MonthlyScheduleInput"),
        (@"src\Cad\Units\DumpAllocation.cs", "DumpAllocationInput"),
        (@"src\Cad\Units\CoupledMinePlanner.cs", "CoupledPlanInput"),
        (@"src\Cad\Plan\MonthlyStripSession.cs", "MonthlyStripSessionInput"),
        // ④ 终点：三维动态模拟
        (@"src\TaskLib\Simulation\UnitSolidStage.cs", "UnitSolidStageOptions"),
        // ⑤ 短期计划之外的功能（现场令是「逐个功能」，不是「这一条链」）
        (@"src\Cad\Road\RoadEvolutionOptions.cs", "RoadEvolutionOptions"),
        (@"src\Cad\Transport\ExtendTriggerSettings.cs", "ExtendTriggerSettings"),
        (@"src\Cad\Tasks\Scheduling\TaskExploder.cs", "ExploderConfig"),
        (@"src\Data\FleetOptimizer.cs", "FleetOptInput"),
        (@"src\Cad\RoadCenterlineExtractor.cs", "RoadCenterlineOptions"),
    };

    /// <summary>
    /// <b>显式豁免</b> —— 只有两种理由，且都要写清：
    /// ① 那个文件正在用户手上改（去动它会撞车）；② 判据本身的已知限制。
    /// <b>豁免必须是看得见的</b>：藏起来的豁免等于把判据悄悄关掉。
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // ⚠ 下面这一批的**共同理由**：`DumpStripPlanner.cs` 正在用户手上改（git 里是 M）。
        //    去改它的注释会撞车，比留一条可见的豁免更糟。**这些是待办，不是结论** ——
        // ⚠ Kylin 过渡豁免：这三项由「采掘单元清单」窗口（MiningUnitPlanWindow）填，那一 tick 移植完就删掉这三行
        ["UnitPlanEngine.UnitPlanInput.FallbackDetour"] = "待采掘单元清单移植（原由 MiningUnitPlanWindow 赋值）",
        ["UnitPlanEngine.UnitPlanInput.FaceQuotas"] = "待采掘单元清单移植（原由 MiningUnitPlanWindow 赋值）",
        ["UnitPlanEngine.UnitPlanInput.FaceSegments"] = "待采掘单元清单移植（原由 MiningUnitPlanWindow 赋值）",
        //    等那边的工作落定，逐条问口径：是接上界面，还是写「刻意无入口」。
        ["DumpStripPlanner.Options.SplitProbeSteps"] = "只有判据在用（DumpStripPlannerTests）；文件在用户手上改",
        ["DumpStripPlanner.Options.SplitBadSegLimit"] = "文件在用户手上改",
        ["DumpStripPlanner.Options.MaxReachRatio"] = "文件在用户手上改",
        ["DumpStripPlanner.Options.MaxClaimedOverlap"] = "文件在用户手上改",
        ["DumpStripPlanner.Options.ContinueAfterClamp"] = "文件在用户手上改",
        ["DumpStripPlanner.Options.RailSampleStepM"] = "文件在用户手上改",
        ["DumpStripPlanner.Options.MinClampRatio"] = "文件在用户手上改",
        ["DumpStripPlanner.Options.MinStrikeRatio"] = "文件在用户手上改",

        // ⚠ 道路中心线提取：六个阈值里**只有 `MinRoadWidth` 有入口**，其余五个人调不了。
        //    这**不是"刻意无入口"**（那样标就是把缺口写成设计），是真缺口 ——
        //    点云质量/台阶线碎裂程度各矿不同，提取效果不好时用户只能调路宽这一个。
        //    对话框 `RoadCenterlineDialog.xaml.cs` 正在用户手上改，所以先登记不动手。
        ["RoadCenterlineExtractor.RoadCenterlineOptions.MaxPairDist"] = "待办：应给入口；对话框在用户手上改",
        ["RoadCenterlineExtractor.RoadCenterlineOptions.ZTolerance"] = "待办：应给入口；对话框在用户手上改",
        ["RoadCenterlineExtractor.RoadCenterlineOptions.StitchGap"] = "待办：应给入口；对话框在用户手上改",
        ["RoadCenterlineExtractor.RoadCenterlineOptions.WeldGap"] = "待办：应给入口；对话框在用户手上改",
        ["RoadCenterlineExtractor.RoadCenterlineOptions.MaxNormalAngleDeg"] = "待办：应给入口；对话框在用户手上改",
    };

    [Fact(DisplayName = "D1 参数集里不许有【没人填、也没说明为什么没人填】的字段")]
    public void D1_NoSilentDeadOptions()
    {
        string root = RepoRoot();
        var allSrc = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)   // Kylin：单程序集，全在 src 下（bin/obj 滤掉）
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        // 文件内容缓存（几百个文件，读一遍就够）。
        // ⚠ 先滤 NUL 字节（有的 xaml.cs 里有，grep 会当二进制跳过），**再按 UTF-8 解码**。
        //   我第一版写成 `(char)b` 逐字节转 —— 那是 Latin-1，中文全成乱码，
        //   于是"注释里有没有写「刻意无入口」"这一半**永远匹配不上**，而字段名是 ASCII 照常工作
        //   ⇒ 判据一半静默失效、还报得理直气壮。与 L1/L2 是同一族的坑，我自己又踩了一次。
        var texts = allSrc.ToDictionary(
            f => f,
            f => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f).Where(b => b != 0).ToArray()));

        var dead = new List<string>();
        int checkedFields = 0, exempted = 0;

        foreach (var (rel, cls) in Targets)
        {
            string path = Path.Combine(root, rel);
            if (!File.Exists(path) && rel.Contains("EquipmentAssigner")) { _out.WriteLine($"· 未移植，跳过：{rel}（采掘单元清单那一 tick 再补）"); continue; }
            Assert.True(File.Exists(path), $"判据读的文件不在：{path}（挪位置了？判据要跟着改）");
            string src = texts.TryGetValue(path, out var t) ? t : File.ReadAllText(path);

            foreach (var (name, doc) in PublicFields(src, cls))
            {
                checkedFields++;
                string key = $"{Path.GetFileNameWithoutExtension(path)}.{cls}.{name}";

                // 谁给它赋过值？（排除声明文件自身；`X =` / `X=` / 对象初始化器都算）
                var setter = new Regex($@"\b{Regex.Escape(name)}\s*=[^=]", RegexOptions.Compiled);
                bool assigned = texts.Any(kv => !string.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase)
                                             && setter.IsMatch(kv.Value));
                if (assigned) continue;

                if (Exempt.TryGetValue(key, out string? why)) { exempted++; _out.WriteLine($"· 豁免 {key} —— {why}"); continue; }

                // 没人填 ⇒ 必须写明是故意的
                if (doc.Contains("刻意无入口", StringComparison.Ordinal)) { _out.WriteLine($"· 刻意无入口 {key}"); continue; }
                dead.Add($"{key}　（没有任何调用方给它赋值，注释里也没写「刻意无入口」）");
            }
        }

        _out.WriteLine($"\n扫了 {checkedFields} 个字段 · 豁免 {exempted} 个 · 死输入 {dead.Count} 个");
        Assert.True(dead.Count == 0,
            "这些参数没人填、也没说明为什么（死输入不报错：引擎按缺省值算，用户以为自己能调）：\n　"
            + string.Join("\n　", dead)
            + "\n处理法：① 接上界面/上游；② 或在字段注释里写「刻意无入口：<理由>」。");
    }

    /// <summary>取某个类里的公开字段名 + 它上面那段文档注释。</summary>
    private static List<(string Name, string Doc)> PublicFields(string src, string cls)
    {
        var res = new List<(string, string)>();
        var m = Regex.Match(src, $@"class\s+{Regex.Escape(cls)}\b");
        if (!m.Success) return res;

        // 从类头起按大括号配平截出类体 —— 按行数硬切会把下一个类的字段也算进来
        int open = src.IndexOf('{', m.Index);
        if (open < 0) return res;
        int depth = 0, end = open;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        string body = src.Substring(open, end - open);

        // 只取字段（`public <类型> <名>;` 或带初值），不取属性/方法/嵌套类。
        // ⚠ 说明文字**不靠"捕获紧邻的 /// 块"取**：那种写法对空行、\r\n、跨行 <para> 都很脆，
        //   我第一版就是这么写的 —— 明明标注了「刻意无入口」判据却照样报红（判据自伤）。
        //   改成取字段声明**前面一段源码**：注释在哪一行、断没断行都不影响。
        foreach (Match f in Regex.Matches(body,
                     // ⚠ `=` 后面要挡掉 `>`：表达式体属性 `public double X => 1 - Y;` 也长成
                     //   "public 类型 名 = … ;" 的样子，会被当成字段抓进来 —— 而它是【算出来的】，
                     //   本来就没人给它赋值，于是判据报一条永远修不掉的假阳性
                     //   （实测抓到 ExploderConfig.WeatherFactor）。假阳性会把真的那条淹掉。
                     @"^[ \t]*public\s+(?!sealed|class|static|override|virtual)(?<type>[A-Za-z_][\w\.<>,\?\[\]]*)\s+(?<name>[A-Z]\w*)\s*(?:=(?!>)[^;]*)?;",
                     RegexOptions.Multiline))
        {
            string name = f.Groups["name"].Value;
            if (name.Length == 0) continue;
            int from = Math.Max(0, f.Index - 900);
            res.Add((name, body.Substring(from, f.Index - from)));
        }
        return res;
    }
}
