// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/CoalSinkPoint.cs（逐行对应；仅命名空间适配）
using System.Collections.Generic;
using System.Linq;
using System;
using System.Globalization;
using System.IO;

namespace PitMine3D.Kylin.UnitLedger;

// ── 为什么住在 BlockModelLib 而不是 PlanLib ───────────────────────────────
//  它管的是【台账目录下、与基表同级】的一个文件，而管台账目录的 MonthlyUnitLedgerStore
//  就在本程序集。更要紧的是：造运输 O-D 的 TaskLib.LedgerOdBuilder 也要读它，
//  而 TaskLib 不引 PlanLib（只反射软读）。
//  备选是反射（Type.GetType("PlanLib.ShortTerm.CoalSinkPoint, PlanLib")）—— 否掉了：
//  改名或改签名不会报错，只会静默变成「煤一笔都画不出来」，
//  而这和「本来就没指过卸点」在界面上长得一模一样。本仓库反复栽的就是这类失败。
//  本类除 System.IO / Globalization 外零依赖，搬过来不带任何东西。

/// <summary>
/// 【煤的卸点】破碎站 / 原煤仓的位置 —— <b>在图上拾取一次，记住</b>。
///
/// <para><b>为什么不走 <c>load_unload_point</c> 表</b>：现场口径是
/// 「采装就是采矿模型，卸载就是排土场」—— 采装点是采掘单元质心、卸载点是排土位置质心，
/// 两边模型里都已经有真坐标，不该再让人去录第三张表。
/// 唯一推不出来的只有<b>煤</b>的卸点：排土场不收煤，而库里
/// （<c>mine_location</c> 只有平盘标高没有 XY、<c>process_phase 303</c> 只有一句"破碎点卸料"的描述）
/// <b>没有任何破碎站坐标</b>。所以这一个点、且只有这一个点，需要人指一次。</para>
///
/// <para><b>存在台账目录下</b>（与基表同级），不进数据库 —— 它跟着这套台账走，
/// 换个工程就该重新指。</para>
///
/// <para><b>⚠ 没指过时不许静默用默认值冒充</b>：<see cref="IsPicked"/> 为 false 时，
/// 调用方要么明确降级并在界面上说清"用的是采场质心这个引擎默认位置"，要么就别算煤的运输功。
/// 一个编出来的运距会一路传到运输功、比选打分和三维动画上，而每个数看上去都正常。</para>
/// </summary>
public sealed class CoalSinkPoint
{
    public const string FileName = "煤卸点.txt";

    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    /// <summary>名称（人填，如"1号破碎站"）。</summary>
    public string Name { get; init; } = "破碎站";
    /// <summary>拾取时刻 —— 图改了它就可能是陈的，要显示出来让人自己判。</summary>
    public DateTime PickedAt { get; init; }
    /// <summary>怎么来的（拾取了哪个实体 / 手填）。<b>原样显示，别自己再编一句</b>。</summary>
    public string SourceNote { get; init; } = "";

    public bool IsPicked => PickedAt != default;

    /// <summary>一行摘要，界面直接用。</summary>
    public string Caption => IsPicked
        ? $"{Name}（{X:0.#}, {Y:0.#}, {Z:0.#}）· {SourceNote} · {PickedAt:MM-dd HH:mm} 拾取"
        : "◆ 还没在图上指过煤的卸点 —— 现在用的是【采场质心】这个引擎默认位置，煤的运距不是真的";

    public static string PathIn(string ledgerRoot) => Path.Combine(ledgerRoot, FileName);

    /// <summary>读。没有文件、或文件坏了，一律返回 null（<b>不返回一个坐标是 0 的点冒充成功</b>）。</summary>
    public static CoalSinkPoint? Load(string ledgerRoot, out string issue)
    {
        issue = "";
        string p = PathIn(ledgerRoot);
        if (!File.Exists(p)) return null;
        try
        {
            var ci = CultureInfo.InvariantCulture;
            double x = 0, y = 0, z = 0; string name = "破碎站", note = ""; DateTime at = default;
            foreach (var raw in File.ReadAllLines(p, System.Text.Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                switch (k)
                {
                    case "X": double.TryParse(v, NumberStyles.Float, ci, out x); break;
                    case "Y": double.TryParse(v, NumberStyles.Float, ci, out y); break;
                    case "Z": double.TryParse(v, NumberStyles.Float, ci, out z); break;
                    case "Name": name = v; break;
                    case "Note": note = v; break;
                    case "PickedAt": DateTime.TryParse(v, ci, DateTimeStyles.None, out at); break;
                }
            }
            // 坐标全 0 多半是文件写坏了 —— 那不是"原点上的破碎站"，是没有值
            if (Math.Abs(x) < 1e-9 && Math.Abs(y) < 1e-9)
            { issue = $"◆ {FileName} 里的坐标是 (0,0) —— 当作没指过，请重新拾取。"; return null; }
            if (at == default) at = File.GetLastWriteTime(p);
            return new CoalSinkPoint { X = x, Y = y, Z = z, Name = name, SourceNote = note, PickedAt = at };
        }
        catch (Exception ex) { issue = $"◆ 读 {FileName} 失败：{ex.Message}"; return null; }
    }

    /// <summary>写。原子写（先 .tmp 再替换）—— 半份文件读回来是个错坐标，比没有更糟。</summary>
    public void Save(string ledgerRoot)
    {
        Directory.CreateDirectory(ledgerRoot);
        var ci = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 煤的卸点（破碎站 / 原煤仓）—— 在图上拾取一次，记住。");
        sb.AppendLine("# 采装点＝采掘单元质心、岩的卸点＝排土位置质心，都从模型来，不在这儿。");
        sb.AppendLine("# 这个文件跟着本套台账走；换工程请重新拾取。");
        sb.AppendLine(ci, $"Name={Name}");
        sb.AppendLine(ci, $"X={X:0.###}");
        sb.AppendLine(ci, $"Y={Y:0.###}");
        sb.AppendLine(ci, $"Z={Z:0.###}");
        sb.AppendLine(ci, $"PickedAt={PickedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(ci, $"Note={SourceNote}");

        string p = PathIn(ledgerRoot), tmp = p + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(true));
        if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
    }

    /// <summary>
    /// 从一组世界坐标点算质心（拾取到的实体可能是点、多段线或网格，一律折成一个点）。
    /// 点数为 0 返回 false —— <b>不给一个 (0,0,0)</b>。
    /// </summary>
    public static bool TryCentroid(double[]? xyzFlat, out double cx, out double cy, out double cz)
    {
        cx = cy = cz = 0;
        if (xyzFlat == null || xyzFlat.Length < 3) return false;
        int n = xyzFlat.Length / 3;
        for (int i = 0; i < n; i++) { cx += xyzFlat[i * 3]; cy += xyzFlat[i * 3 + 1]; cz += xyzFlat[i * 3 + 2]; }
        cx /= n; cy /= n; cz /= n;
        return true;
    }
}
