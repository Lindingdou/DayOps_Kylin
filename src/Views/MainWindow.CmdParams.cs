using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using PitMine3D.Kylin.Views.Modeling;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 命令行参数问答（AutoCAD 式）—— 命令执行过程中在命令行里逐项问参数，而不是弹对话框。
///
/// 原先命令要参数只有两条路：<b>参数必须和命令写在同一行</b>（`图案填充 45 2`，得先记住顺序），
/// 或者<b>弹模态对话框</b>（鼠标操作，命令行用户被打断）。这里补上 AutoCAD 的第三条、也是主路：
/// 命令跑起来后在命令行上一项一项问，提示带 `[选项]` 与 `&lt;默认值&gt;`，回车取默认、Esc 取消。
///
/// 接管范围：命令由<b>命令行/智能助手</b>发起时接管（Ribbon 按钮点出来的仍走对话框，忠实原版交互）。
/// 挂钩点是 <see cref="PromptDialog.CommandLineAsker"/>，所以凡是用 PromptDialog 取参数的命令一律受益。
/// </summary>
public partial class MainWindow
{
    /// <summary>一次进行中的参数问答。</summary>
    private sealed class ParamAsk
    {
        public string Title = "";
        public IReadOnlyList<PromptDialog.Field> Fields = System.Array.Empty<PromptDialog.Field>();
        public int Index;
        public readonly Dictionary<string, string> Values = new();
        public List<string> Inline = new();       // 命令行同行给的位置参数，先消费掉再问
        public readonly TaskCompletionSource<PromptValues?> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);   // 异步续跑：避免在按键处理里回到命令体
    }

    private ParamAsk? _ask;

    /// <summary>本次命令来自命令行/智能助手（而非 Ribbon 点按）——只有它为真才把参数问答搬到命令行。</summary>
    private bool _cmdLineDriven;

    /// <summary>命令行上跟在命令词后面的位置参数（`加密多段线 15` 的 "15"）。每条命令开始时重置。</summary>
    private List<string> _pendingInlineArgs = new();

    /// <summary>“摘掉参数只用命令词再试一遍”时带过去的位置参数（见 OnRibbonCommand 兜底分支）。</summary>
    private List<string>? _forcedInlineArgs;

    /// <summary>是否正在问参数（命令行的回车/空格要先喂给它）。</summary>
    private bool AskingParams => _ask != null;

    /// <summary>装上问答挂钩。构造时调用一次。</summary>
    private void InstallParamAsker() => PromptDialog.CommandLineAsker = BeginParamAsk;

    /// <summary>
    /// PromptDialog 的挂钩实现：命令行发起的命令 → 接管并返回问答任务；否则返回 null（照旧弹对话框）。
    /// </summary>
    private Task<PromptValues?>? BeginParamAsk(
        Window owner, string title, IReadOnlyList<PromptDialog.Field> fields, string? description)
    {
        // 只接管"本窗口 + 命令行发起 + 当前没有别的问答在进行"这一种情形。
        // 限定 owner 是为了不误抢子窗口(数据库页面等)自己弹的参数框。
        if (!ReferenceEquals(owner, this) || !_cmdLineDriven || _ask != null || fields == null || fields.Count == 0)
            return null;

        var ask = new ParamAsk { Title = title, Fields = fields, Inline = _pendingInlineArgs };
        _pendingInlineArgs = new List<string>();
        _ask = ask;

        LogCommand($"{title}：逐项输入参数（回车取默认值 · Esc 取消）");
        if (!string.IsNullOrWhiteSpace(description)) LogCommand("  " + description);
        ConsumeInlineThenPrompt();
        return ask.Tcs.Task;
    }

    /// <summary>先用同行给的位置参数把能填的填掉，剩下的停在第一个待答项上等键入。</summary>
    private void ConsumeInlineThenPrompt()
    {
        var ask = _ask;
        if (ask == null) return;

        while (ask.Index < ask.Fields.Count && ask.Inline.Count > 0)
        {
            var f = ask.Fields[ask.Index];
            string arg = ask.Inline[0];
            if (!ParamPrompt.TryNormalize(f, arg, out string v, out _)) break;   // 填不上就交给用户键入
            ask.Inline.RemoveAt(0);
            ask.Values[f.Key] = v;
            LogCommand($"  {f.Label} = {v}");
            ask.Index++;
        }

        if (ask.Index >= ask.Fields.Count) { FinishParamAsk(); return; }
        SyncPrompt();
        StatusMsg.Text = ParamPrompt.PromptText(ask.Fields[ask.Index]) + "：（回车取默认值 · Esc 取消）";
    }

    /// <summary>命令行键入的一行喂给问答。返回 true 表示已被问答消费。</summary>
    private bool FeedParamAsk(string raw)
    {
        var ask = _ask;
        if (ask == null) return false;

        var f = ask.Fields[ask.Index];
        if (!ParamPrompt.TryNormalize(f, raw, out string v, out string err))
        {
            LogCommand("  " + err);
            StatusMsg.Text = err;
            _lastPrompt = "";      // 强制重刷提示行(内容没变, 否则 SyncPrompt 认为无需更新)
            SyncPrompt();
            return true;
        }
        ask.Values[f.Key] = v;
        LogCommand($"  {f.Label} = {v}");
        ask.Index++;
        ConsumeInlineThenPrompt();
        return true;
    }

    private void FinishParamAsk()
    {
        var ask = _ask;
        if (ask == null) return;
        _ask = null;                          // 先摘掉再回结果：命令续跑时可能马上又要问下一组
        SyncPrompt();
        ask.Tcs.TrySetResult(PromptValues.From(ask.Values));
    }

    /// <summary>Esc：放弃这次参数问答（命令随之取消）。返回 true 表示确实取消了一次问答。</summary>
    private bool CancelParamAsk()
    {
        var ask = _ask;
        if (ask == null) return false;
        _ask = null;
        SyncPrompt();
        LogCommand($"  {ask.Title}：已取消");
        StatusMsg.Text = $"{ask.Title}：已取消";
        ask.Tcs.TrySetResult(null);
        return true;
    }

    /// <summary>问答进行中的提示行（供 CurrentPrompt 用）。</summary>
    private string ParamAskPrompt()
        => _ask == null || _ask.Index >= _ask.Fields.Count ? "" : ParamPrompt.PromptText(_ask.Fields[_ask.Index]);

    /// <summary>
    /// 正多边形：AutoCAD 的 POLYGON 先问侧面数。命令行发起且同行没给边数时接管，
    /// 其余（Ribbon 按钮、`POL 8` 这种同行已给边数的）返回 false 走原路。
    /// </summary>
    private bool TryPolygonWithPrompt(string cmd)
    {
        string t = (cmd ?? "").Trim();
        string letters = new string(System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.TakeWhile(t, char.IsLetter))).ToUpperInvariant();
        if (!(letters is "POLYGON" or "POL" || t.StartsWith("正多边形"))) return false;
        if (!_cmdLineDriven) return false;
        if (System.Linq.Enumerable.Any(t, char.IsDigit)) return false;   // 同行已给边数 → 交给 ActivateDrawTool
        _ = AskPolygonSidesAsync();
        return true;
    }

    private async Task AskPolygonSidesAsync()
    {
        var p = await AskCmdParamsAsync("正多边形",
            new PromptDialog.Field("sides", "侧面数", "6", null, "3 ~ 120"));
        if (p == null) { StatusMsg.Text = "正多边形：已取消"; return; }
        int n = p.I("sides", 6);
        if (n < 3 || n > 120) { StatusMsg.Text = "正多边形：侧面数须在 3~120 之间"; return; }
        ActivateDrawTool($"POLYGON {n}");
        SyncPrompt();
    }

    /// <summary>
    /// 给"参数原本写在同一行、缺省就静默取默认值"的命令（图案填充 / SOR / ROR / 正多边形 …）
    /// 补上命令行问答：命令行发起就逐项问，Ribbon 点按仍按默认值静默执行（不改原交互）。
    /// </summary>
    private async Task<PromptValues?> AskCmdParamsAsync(string title, params PromptDialog.Field[] fields)
    {
        var t = BeginParamAsk(this, title, fields, null);
        if (t != null) return await t;

        // 未接管(界面按钮/子窗口)：同行给的位置参数照填、其余取默认值 —— 与加问答之前的行为逐字一致。
        var inline = _pendingInlineArgs; _pendingInlineArgs = new List<string>();
        var vals = new Dictionary<string, string>();
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            vals[f.Key] = i < inline.Count && ParamPrompt.TryNormalize(f, inline[i], out string v, out _)
                ? v : ParamPrompt.DefaultOf(f);
        }
        return PromptValues.From(vals);
    }
}
