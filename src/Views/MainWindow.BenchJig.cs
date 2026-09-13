using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「动态调整」（剥采排工程·剥采工程）= 原 MineAssLib <c>CreateBenchDesignJigCommand</c>：选 1 条境界线 →
/// 先定 D=向下挖坑底 / U=向上到地表（回车=D）→ 移动鼠标调开采深度/层数并实时预览坡面 → 点击/回车确认，Esc 取消。
/// 原版实时回路在 C++(xllAcEd) 内闭环；这里托管等价（光标→级数、预览环、确认落地 见 <see cref="BenchJig"/> / <see cref="BenchBuilder"/>）。
/// 原版 spike 局限照搬：只驱动"深度/层数"一个维度，H/α/W 固定（取自「参数化模板」解析）。
/// </summary>
public partial class MainWindow
{
    private PolylineEntity? _benchJigLine;
    private bool _benchJigDown = true;
    private int _benchJigLevels;
    private double _benchJigH, _benchJigAlpha, _benchJigW;
    private const int BenchJigMaxLevels = 40;

    private async Task StartBenchJigAsync(PolylineEntity line)
    {
        var db = EnsureGeoDb();
        var rp = Data.BenchTemplateResolver.Resolve(db?.Connection, isDump: false);
        _benchJigH = rp.BenchHeight; _benchJigAlpha = rp.FaceAngleDeg; _benchJigW = rp.BermWidth;

        // 方向：D=向下挖坑底(默认) / U=向上到地表 —— 原版在命令行键入，这里弹一项选择（命令行位置参数 D/U 也认）
        bool down = true;
        var args = _pendingInlineArgs;
        if (args != null && args.Count > 0 && (args[0] is "U" or "u" or "上")) down = false;
        else if (args == null || args.Count == 0)
        {
            var v = await Views.Modeling.PromptDialog.AskAsync(this, "动态调整台阶形态",
                new List<Views.Modeling.PromptDialog.Field>
                {
                    new("dir", "方向", "D 向下挖坑底", Choices: new[] { "D 向下挖坑底", "U 向上到地表" }, Numeric: false),
                },
                $"依据 {rp.Provenance} ｜ H={rp.BenchHeight:0.#} / α={rp.FaceAngleDeg:0.#} / W={rp.BermWidth:0.#}（H/α/W 固定，只调深度/层数）");
            if (v == null) { StatusMsg.Text = "动态调整台阶形态：已取消。"; return; }
            down = !v.S("dir").StartsWith("U");
        }

        _benchJigLine = line; _benchJigDown = down; _benchJigLevels = 1;
        EditEcho($"> 台阶交互设计已启动({(down ? "D 向下挖坑底" : "U 向上到地表")})：移动鼠标调深度（离境界线越远级数越多），点击/回车确认，Esc 取消");
        RefreshScenePreview();   // 先画一圈，预览通道有东西后指针移动才会持续重刷
        var (kind, _, _) = await PickPointOrConfirmAsync("动态调整：移动鼠标调开采深度/层数，左键 / 回车确认，Esc 取消", true);
        var jigLine = _benchJigLine; int levels = _benchJigLevels;
        _benchJigLine = null;
        if (kind == PickKind.Cancelled || jigLine == null)
        {
            RefreshScenePreview();
            StatusMsg.Text = "动态调整台阶形态：已取消。";
            return;
        }

        bool closed = jigLine.Closed || (jigLine.Points.Count >= 3 && Dist2(jigLine.Points[0], jigLine.Points[^1]) < 1e-12);
        var r = BenchBuilder.Build(jigLine.Points, closed, jigLine.Elevation, _benchJigH, _benchJigAlpha, _benchJigW,
                                   levels, downward: down, isDump: false);
        if (!r.Ok) { RefreshScenePreview(); StatusMsg.Text = "动态调整台阶形态：" + r.Error; EditEcho("动态调整台阶形态失败：" + r.Error, EchoLevel.Error); return; }
        BeginChange();
        var made = AddBenchEntities(r, down ? "动态调整↓" : "动态调整↑", "台阶", isDump: false);
        _selected.Clear();
        RefreshScene();
        foreach (var n in r.Notes) AppendHistoryLine("  " + n, EchoBrush(EchoLevel.Info));
        StatusMsg.Text = $"✓ 动态调整台阶形态·{(down ? "向下" : "向上")} {r.Levels.Count} 级（H={_benchJigH:0.#}/α={_benchJigAlpha:0.#}/W={_benchJigW:0.#}）"
                       + $" · 总高差 {r.TotalDropM:0.#} m · 新建 {made.Count} 实体（可 Ctrl+Z）";
    }

    /// <summary>jig 预览：按光标到境界线的距离折级数，把每级坡脚环（青）+ 当前级数标记画进预览通道。挂在 AppendScenePreview。</summary>
    private void BenchJigAppendPreview(List<float> list)
    {
        var line = _benchJigLine;
        if (line == null || _cursorWorld == null) return;
        var cur = _cursorWorld.Value;
        bool closed = line.Closed || (line.Points.Count >= 3 && Dist2(line.Points[0], line.Points[^1]) < 1e-12);
        double d = BenchJig.DistanceToPolyline(line.Points, closed, cur.x, cur.y);
        _benchJigLevels = BenchJig.LevelsForDistance(d, _benchJigH, _benchJigAlpha, _benchJigW, BenchJigMaxLevels);
        var r = BenchBuilder.Build(line.Points, closed, line.Elevation, _benchJigH, _benchJigAlpha, _benchJigW,
                                   _benchJigLevels, downward: _benchJigDown, isDump: false);
        if (!r.Ok) return;
        for (int i = 0; i < r.Levels.Count; i++)
        {
            var lv = r.Levels[i];
            float t = r.Levels.Count <= 1 ? 1f : (float)i / (r.Levels.Count - 1);
            var pv = new PolylineEntity { Closed = closed, Elevation = lv.ToeZ, Cr = 0.30f, Cg = 0.95f - 0.45f * t, Cb = 0.95f };
            pv.Points.AddRange(lv.Toe);
            pv.Tessellate(list);
        }
        if (CmdPrompt != null)
            CmdPrompt.Text = $"动态调整：{r.Levels.Count} 级 · 深度 {r.TotalDropM:0.#} m（点击/回车确认，Esc 取消）:";
    }
}
