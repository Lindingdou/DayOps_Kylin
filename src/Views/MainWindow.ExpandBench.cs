using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「批量台阶扩帮」= 原 MineAssLib <c>CreateExpandBenchBatchCommand</c>（PMEP）：选 1 条境界线 → <c>ExpandBenchBatchDialog</c>
/// （边坡类型 采场/排土场 · 角色 Toe/Crest · 方向 上/下 · 开口线侧向 · H/α/W 自动套「参数化模板」· 工作帮最小工作平盘 / 最终帮安全平台 ·
/// 止点 到标高/到地表/按段数）→ 内核逐层 offset 出 toe/crest + 三角网。内核无源，几何走 <see cref="BenchBuilder"/>（与局部台阶/排土场放坡同一套）。
/// 【登记的差异】原版对话框里的「逐级剖面表(组合/混合台阶)」「留运输平台(每 N 级)」「随线起伏」「到煤层底板」四项此处未做：
/// 逐级表/运输平台需按级换参（BenchBuilder 一套 H/α/W 到底）；Kylin 境界线是平面线(单一 Elevation)只有"拍平"；到煤层底板请走「批量扩坑」。
/// 产物同时写进 <see cref="_lastBenchLevels"/>，供「直线坑线」复用 toe/crest 自动布线（原版 _lastBenchLines）。
/// </summary>
public partial class MainWindow
{
    /// <summary>最近一次批量台阶扩帮的各级台阶（原版 _lastBenchLines：供直线坑线/运量驱动布线取 toe/crest）。</summary>
    private List<BenchLevel>? _lastBenchLevels;
    private double _lastBenchBermW;
    private bool _lastBenchIsDump;

    private async Task ExpandBenchBatchCmd(string cmd, bool presetDump = false)
    {
        string entry = presetDump ? "排土场放坡" : "批量台阶扩帮";
        var sel = _selected.FindAll(e => e is PolylineEntity pl && pl.Points.Count >= 2);
        if (sel.Count != 1)
        { StatusMsg.Text = $"{entry}：请先在场景里选 1 条 polyline 作为境界线（当前选集 ≠ 恰好 1 条 polyline）"; EditEcho($"{entry}:请先在场景里选 1 条 polyline 作为境界线", EchoLevel.Warn); return; }
        var line = (PolylineEntity)sel[0];
        bool closed = line.Closed || (line.Points.Count >= 3 && Dist2(line.Points[0], line.Points[^1]) < 1e-12);
        double startZ = line.Elevation;
        EditEcho($"> 检测到境界线: {(closed ? "闭合" : "非闭合")} / {line.Points.Count} 点 / Z={startZ:F2}m");

        var ground = SamplerFromSelectedMeshes(out int meshCount);
        var db = EnsureGeoDb();
        var inv = CultureInfo.InvariantCulture;

        // ── 参数：命令行位置参数 "批量台阶扩帮 H α W [级数]" 直给；否则对话框（照原 ExpandBenchBatchDialog 分区）──
        bool isDump = presetDump, down = true, roleToe = false; int side = 1, levels = 10;
        double H, alpha, W; bool workBerm = true;
        string stopKind = ground != null ? "到地表(所选三角网)" : "按段数"; double? stopZ = null;
        var rp0 = Data.BenchTemplateResolver.Resolve(db?.Connection, isDump: isDump);
        H = rp0.BenchHeight; alpha = rp0.FaceAngleDeg; W = rp0.MinWorkingBermWidth > 0 ? rp0.MinWorkingBermWidth : rp0.BermWidth;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 4 && double.TryParse(tk[1], NumberStyles.Float, inv, out double h1) && double.TryParse(tk[2], NumberStyles.Float, inv, out double a1) && double.TryParse(tk[3], NumberStyles.Float, inv, out double w1))
        {
            H = h1; alpha = a1; W = w1;
            if (tk.Length >= 5 && int.TryParse(tk[4], out int n1) && n1 > 0) { levels = n1; stopKind = "按段数"; }
            if (tk.Contains("排土场")) isDump = true;
            if (tk.Contains("向上")) down = false;
            if (tk.Contains("右")) side = -1;
        }
        else
        {
            var fields = new List<Views.Modeling.PromptDialog.Field>
            {
                new("type", "边坡类型", isDump ? "排土场边坡(向外堆)" : "采场边坡(向内挖)", Choices: new[] { "采场边坡(向内挖)", "排土场边坡(向外堆)" }, Numeric: false),
                new("role", "境界线角色", "开口/堆顶线 (Crest)", Choices: new[] { "开口/堆顶线 (Crest)", "坑底/堆底线 (Toe)" }, Numeric: false),
                new("dir", "方向", "向下推", Choices: new[] { "向下推", "向上扩" }, Numeric: false, Hint: "采场向下=往内收；排土场向下=往外放"),
                new("side", "侧向(仅开口线)", "左侧", Choices: new[] { "左侧", "右侧" }, Numeric: false, Hint: "沿线行进方向的哪一侧放坡；闭合线忽略"),
                new("H", "台阶高 H", H.ToString("0.##", inv), "m", Hint: $"依据 {rp0.Provenance}"),
                new("alpha", "坡面角 α", alpha.ToString("0.##", inv), "°"),
                new("berm", "平盘类型", "工作帮(最小工作平盘)", Choices: new[] { "工作帮(最小工作平盘)", "最终帮(安全平台)" }, Numeric: false,
                    Hint: $"工作帮 W_work={rp0.MinWorkingBermWidth:0.#} / 最终帮 W={rp0.BermWidth:0.#}（改下面的 W 以手填为准）"),
                new("W", "平盘宽 W", W.ToString("0.##", inv), "m"),
                new("stop", "止点", stopKind, Choices: new[] { "按段数", "到指定标高", "到地表(所选三角网)" }, Numeric: false,
                    Hint: ground != null ? $"已选中 {meshCount} 张三角网作地表" : "到地表需先同时选中一张三角网"),
                new("levels", "段数", levels.ToString(inv), "级", Hint: "止点=按段数时生效；其它止点作上限"),
                new("targetZ", "指定标高", (startZ - H * 3).ToString("0.##", inv), "m", Hint: "止点=到指定标高时生效"),
            };
            var v = await Views.Modeling.PromptDialog.AskAsync(this, entry, fields,
                $"境界线 {(closed ? "闭合" : "开口")} {line.Points.Count} 点 · Z={startZ:0.##}（Kylin 境界线为平面线，台阶一律拍平）");
            if (v == null) { StatusMsg.Text = $"{entry}：用户取消"; EditEcho($"{entry}:用户取消"); return; }
            isDump = v.S("type").StartsWith("排土场");
            roleToe = v.S("role").Contains("Toe");
            down = v.S("dir") == "向下推";
            side = v.S("side") == "右侧" ? -1 : 1;
            H = v.D("H", H); alpha = v.D("alpha", alpha); W = v.D("W", W);
            workBerm = v.S("berm").StartsWith("工作帮");
            stopKind = v.S("stop");
            levels = Math.Max(1, (int)v.D("levels", levels));
            if (stopKind == "到指定标高") stopZ = v.D("targetZ", startZ - H * 3);
        }
        if (H <= 0 || alpha <= 0 || alpha >= 90 || W < 0) { StatusMsg.Text = $"{entry}：H>0、0<α<90、W≥0 才能放坡"; return; }
        if (stopKind.StartsWith("到地表") && ground == null) { StatusMsg.Text = $"{entry}：止点=到地表需同时选中一张三角网（当前没有）"; EditEcho($"{entry}:止点=到地表需同时选中一张三角网", EchoLevel.Warn); return; }
        if (stopKind == "到指定标高" && stopZ.HasValue && ((down && stopZ >= startZ) || (!down && stopZ <= startZ)))
        { StatusMsg.Text = $"{entry}：指定标高 {stopZ:0.##} 与方向不符（向下须低于 Z={startZ:0.##}，向上须高于）"; return; }

        int maxLevels = stopKind == "按段数" ? levels : Math.Max(levels, 200);
        var r = BenchBuilder.Build(line.Points, closed, startZ, H, alpha, W, maxLevels, downward: down, isDump: isDump, side: side,
                                   stopZ: stopKind == "到指定标高" ? stopZ : null,
                                   stopAtGround: stopKind.StartsWith("到地表") ? ground : null);
        foreach (var n in r.Notes) AppendHistoryLine("  " + n, EchoBrush(EchoLevel.Info));
        if (!r.Ok) { StatusMsg.Text = $"{entry}失败：{r.Error}"; EditEcho($"{entry}失败: {r.Error}", EchoLevel.Error); return; }

        BeginChange();
        string layer = isDump ? "排土场" : "台阶";
        var made = AddBenchEntities(r, entry, layer, isDump);
        _selected.Clear();
        RefreshScene();

        _lastBenchLevels = r.Levels.ToList(); _lastBenchBermW = W; _lastBenchIsDump = isDump;
        string role = roleToe ? "Toe(坑底/堆底线)" : "Crest(开口/堆顶线)";
        StatusMsg.Text = $"✓ {entry}·{(isDump ? "排土场" : "采场")}·{role}·{(down ? "向下" : "向上")}：{r.Levels.Count} 级 · 总高差 {r.TotalDropM:0.#} m"
                       + $"（H={H:0.#}/α={alpha:0.#}/W={W:0.#}·{(workBerm ? "工作帮" : "最终帮")}·止点 {stopKind}）· 新建 {made.Count} 实体（坡面+平盘+各级坡脚线；可 Ctrl+Z）· 台阶线已缓存供「直线坑线」";
    }
}
