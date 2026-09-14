using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Controls;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.Modeling;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「三维地质建模 → 编辑」组（点编辑 / 线编辑 / 面编辑 / 体编辑 / 工具）的忠实移植。
///
/// 对照原版 <c>Modules/MeshEditLib/MeshEditLibPlugin.cs</c>，每个命令连**交互方式**一起搬：
///   ① 参数对话框 → 作用于当前选择集   (POINTSETZ / POLYUNIFYZ / POLYDEDUPE / WELD / REPAIR …)
///   ② 参数对话框 → 进入视口拾取状态机 (POLYCLIP / CLIP / CUTBYKNIFE)
///   ③ 直接进入视口拾取状态机           (DIAGNOSE / BOUNDARY / DELFACES / SPLITALONG / INTERSECT)
///   ④ 独立窗口                         (VOLSPLIT / 体素格网体积 / 等值线 / 剖面)
/// 提示语与结果回显也按原版命令行的措辞逐条给到信息栏（<see cref="EditEcho"/>）。
/// </summary>
public partial class MainWindow
{
    // ═══════════════════ 信息栏回显（原版 ICommandLineCapability.Echo 的等价）═══════════════════
    private enum EchoLevel { Info, Success, Warn, Error }

    /// <summary>
    /// 信息栏一行的**统一格式**（忠实原版 <c>MainWindow.Commands.cs / AppendToHistoryCore</c>）：
    ///
    ///   <c>[HH:mm:ss] &lt;符号&gt;&lt;正文&gt;</c>
    ///
    /// 时间戳单独一种淡色, 正文按类别上色; 符号即原版那套: Info 无 / Success ✓ / Warn ⚠ / Error ✗。
    /// 所有进信息栏的东西(命令回显 / 交互提示 / 结果)都只经这一个口子, 免得又各写各的格式。
    /// 调用方一律传**裸文本**, 时间与符号由这里补 —— 同原版接口约定。
    /// </summary>
    private void AppendHistoryLine(string body, string fgKey)
    {
        if (CmdLog == null || string.IsNullOrWhiteSpace(body)) return;
        // 行高写死一点: 中英混排时中文走字体回退, 回退字体的行盒比 Consolas 高,
        // 不给 LineHeight 就按各行自己的度量走 —— 行距忽宽忽窄, 中文那几行上下还会被切掉一点。
        var tb = new TextBlock
        {
            FontSize = 12,
            LineHeight = 18,
            FontFamily = new FontFamily("Consolas,monospace"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 1),
        };
        var time = new Avalonia.Controls.Documents.Run($"[{DateTime.Now:HH:mm:ss}] ");
        var text = new Avalonia.Controls.Documents.Run(body);
        tb.Inlines!.Add(time); tb.Inlines.Add(text);
        ThemeBind(time, Avalonia.Controls.Documents.TextElement.ForegroundProperty, CmdLogTimeBrush);
        ThemeBind(text, Avalonia.Controls.Documents.TextElement.ForegroundProperty, fgKey);
        CmdLog.Children.Add(tb);
        while (CmdLog.Children.Count > CmdLogMaxLines) CmdLog.Children.RemoveAt(0);
        CmdLogScroll?.ScrollToEnd();
    }

    private const int CmdLogMaxLines = 100;                                    // 历史上限(超出丢最早的)
    // 信息栏配色一律是主题资源键(Styles/Theme.axaml 浅/深各一套), 每个 Run 绑资源 —— 切主题后历史行一起换色
    private const string CmdLogTimeBrush = "Theme.Log.Time";       // 时间戳: 比正文淡, 不抢读
    internal const string CmdLogCmdBrush = "Theme.Log.Cmd";        // 命令回显「> 命令」
    private const string CmdLogPromptBrush = "Theme.Log.Prompt";   // 交互提示

    /// <summary>severity → 前缀符号(同原版: Info 不加符号)。</summary>
    private static string EchoSymbol(EchoLevel level) => level switch
    {
        EchoLevel.Success => "✓ ",
        EchoLevel.Warn => "⚠ ",
        EchoLevel.Error => "✗ ",
        _ => "",
    };

    private static string EchoBrush(EchoLevel level) => level switch
    {
        EchoLevel.Success => "Theme.Log.Success",
        EchoLevel.Warn => "Theme.Log.Warn",
        EchoLevel.Error => "Theme.Log.Error",
        _ => "Theme.Log.Body",
    };

    /// <summary>把一行结果写进信息栏（同原版命令行的 Info/Success/Warn/Error 分色回显）+ 状态栏。</summary>
    private void EditEcho(string text, EchoLevel level = EchoLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _statusEchoed = text;       // 这句已由本方法带 severity 写进信息栏, 状态栏钩子别再写一遍
        StatusMsg.Text = text;
        AppendHistoryLine(EchoSymbol(level) + text, EchoBrush(level));
    }

    private string? _statusEchoed;   // 最近一条已进信息栏的文案(去重: 同一句连着设两遍只留一行)

    /// <summary>
    /// 状态栏 → 信息栏留痕。
    ///
    /// 为什么要这条钩子: 命令结果散在一千多处 <c>StatusMsg.Text = …</c> 里, 只有走
    /// <see cref="EditEcho"/> 的那部分进得了信息栏 —— 于是信息栏里常见「&gt; 距离」后面什么都没有,
    /// 翻不出这一步到底做成没有。逐处去改既改不全、以后也一定会漏, 所以在**唯一的出口**上挂钩子:
    /// 状态栏文案一变就按统一格式(带时间戳)补一行, 去重防止同一句刷屏。
    ///
    /// 严重度: 这类调用点没带 severity, 一律按 Info 记; 要标 ✓/⚠/✗ 就改用 <see cref="EditEcho"/>。
    /// </summary>
    private void InstallStatusEcho()
    {
        if (StatusMsg == null) return;
        StatusMsg.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty) return;
            string? t = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(t) || t == _statusEchoed) return;
            _statusEchoed = t;
            AppendHistoryLine(t!, EchoBrush(EchoLevel.Info));
        };
    }

    // ═══════════════════ 视口拾取状态机（原版 kernel PickBox 状态机的托管等价）═══════════════════
    private enum PickKind { Picked, Confirmed, Cancelled }

    /// <summary>
    /// 一次视口取点。<paramref name="confirmable"/> = true 时右键 / 回车 = 确认结束（原版 DELFACES 的手势）。
    /// 提示同时进 命令行提示标签 + 光标旁浮标(随指针移动显示, 同绘制工具的步骤提示) + 信息栏；
    /// <paramref name="quiet"/> = true 只更新标签与浮标、不再往信息栏写一行 —— 逐面点选那种"每点一下就重新等下一点"的循环,
    /// 每圈都回显一遍提示会把信息栏刷满。
    /// </summary>
    private Task<(PickKind kind, double x, double y)> PickPointOrConfirmAsync(string prompt, bool confirmable,
        Controls.CadGlViewport.CursorMode? cursor = null, bool quiet = false)
    {
        var tcs = new TaskCompletionSource<(PickKind, double, double)>();
        _pickConfirmable = confirmable;
        _pickCursor = cursor ?? Controls.CadGlViewport.CursorMode.CrosshairOnly;   // 默认取点 = 十字; 选线/选面由调用处给形态
        _oneShotPick = (x, y) =>
        {
            _pickConfirmable = false;
            if (double.IsNaN(x)) tcs.TrySetResult((PickKind.Cancelled, 0, 0));
            else if (double.IsPositiveInfinity(x)) tcs.TrySetResult((PickKind.Confirmed, 0, 0));
            else tcs.TrySetResult((PickKind.Picked, x, y));
        };
        SetPickPrompt(prompt, quiet);
        Activate();
        return tcs.Task;
    }

    /// <summary>
    /// 拾取步骤提示三处同步：命令行提示标签(直接写, 并记为 _lastPrompt 免得 SyncPrompt 再往信息栏灰字重复一行)、
    /// 光标旁浮标(下次指针移动时按 CurrentPrompt 取到)、信息栏(quiet 时省略)。
    /// </summary>
    private void SetPickPrompt(string prompt, bool quiet = false)
    {
        _pickPrompt = prompt;
        if (!quiet) EditEcho(prompt);
        _lastPrompt = prompt;
        if (CmdPrompt != null) CmdPrompt.Text = prompt.Length == 0 ? "" : prompt + ":";
        SyncCursorMode();
        if (_active.DragTip is { Opacity: > 0 }) RefreshPickTip(_lastPointer);   // 浮标已经在光标旁: 指针不动也把文案换掉
    }

    /// <summary>
    /// 拾取期间光标旁浮标：有悬停实时说明就只显示它(面号/面积/点击效果/已选数, 整句步骤提示已在命令行标签里),
    /// 否则显示步骤提示。指针移动路径(_onHostMoved)每次都算一遍; 指针不动时提示/悬停信息变了(点了一下、命令换了提示)
    /// 也要调这一下, 否则浮标上还是旧文案。
    /// </summary>
    private void RefreshPickTip(Avalonia.Point p)
    {
        if (_oneShotPick == null && _pickHover == null) return;
        string text = _pickHover != null && !string.IsNullOrEmpty(_pickHoverInfo) ? _pickHoverInfo! : _pickPrompt;
        if (text.Length == 0) { HideDragTip(); return; }
        if (double.IsNaN(p.X) || double.IsNaN(p.Y)) return;   // 没有屏幕点(自检直喂世界坐标)就不摆浮标
        ShowTipAt(p, text);
    }

    private (double sx, double sy) _lastPickScreen = (double.NaN, double.NaN);   // 最近一次一次性拾取的屏幕点(ConsumeOneShotPick 记)

    /// <summary>
    /// 在视口中点选一个指定类型的实体（原版"方框光标点选…"）。点空了就照原版那样提示重选，Esc 取消返回 null。
    /// 点/线与面两套判定分开（忠实原 Picking::PickSortedAcDb）：
    /// <list type="bullet">
    /// <item><b>点/线</b>（白方框光标）：落在拾取框内才算 —— 容差就是屏幕上画的那个框(<see cref="Controls.CadGlViewport.CursorBoxPx"/>)，
    ///   所见即所选；早前用捕捉容差×2(≈24 px)，框外一大圈的线也会被选上。</item>
    /// <item><b>面</b>（黄框 + 十字光标）：射线落在某个三角内才算命中，没有边线容差 —— 早前 2D 按"到边线距离 ≤ 容差"判，
    ///   点在面外一圈也能选中面。多张网叠在一起时取最前(3D 深度最近 / 2D 该处高程最高)的那张。</item>
    /// </list>
    /// 3D 视图一律按屏幕点做深度拾取(同 PickAt)：Z=0 反投影的世界点不代表实体位置(模型在千米高程)，早前拿它算 XY 距离，
    /// 上下相叠的两张网选完第一张就再也点不中第二张（「面交线」实测）。extra 排除掉的实体不参与命中，所以第二张即使
    /// 整张被第一张盖着，点它的投影范围也能选到。
    /// </summary>
    private async Task<T?> PickEntityInViewportAsync<T>(string prompt, Func<T, bool>? extra = null,
                                                       string? missHint = null) where T : SceneEntity
    {
        // 光标形态照原版分步给: 拾取三角网 = 黄框 + 十字(MeshPickBox), 拾取线/其它实体 = 白方框(PickBox)。
        bool faces = typeof(MeshEntity).IsAssignableFrom(typeof(T));
        var pickCursor = faces ? Controls.CadGlViewport.CursorMode.MeshPickBox : Controls.CadGlViewport.CursorMode.PickBox;
        while (true)
        {
            var (kind, x, y) = await PickPointOrConfirmAsync(prompt, false, pickCursor);
            if (kind != PickKind.Picked) return null;
            var cands = _scene.Entities.OfType<T>()
                .Where(e => e.Visible && _layers.IsSelectable(e.LayerName) && (extra == null || extra(e))).ToList();
            T? best = null;
            var sp = _lastPickScreen;
            if (faces)
            {
                best = PickFaceAt(cands.OfType<MeshEntity>(), sp.sx, sp.sy, x, y) as T;
            }
            else if (!Viewport.Is2DView && !double.IsNaN(sp.sx))
            {
                best = SelectionBox.PickScreen(cands, sp.sx, sp.sy, Controls.CadGlViewport.CursorBoxPx, Viewport.WorldToScreenDepthProjector()) as T;
            }
            else
            {
                double bd = PixelsToWorld(_lastPointer, Controls.CadGlViewport.CursorBoxPx);
                foreach (var e in cands)
                {
                    double d = e.DistanceTo(x, y);
                    if (d <= bd) { bd = d; best = e; }
                }
            }
            if (best != null)
            {
                SelectEntities(new SceneEntity[] { best });
                return best;
            }
            EditEcho(missHint ?? "未拾取到目标对象，请重选（Esc 取消）", EchoLevel.Warn);
        }
    }

    /// <summary>
    /// 面拾取：射线落在某个三角内才算命中，没有边线容差。3D 按屏幕点取深度最前的网(容差 0 → 线框模式下的网格边线也不参与)；
    /// 2D 取该 XY 处高程最高的网(俯视看得见的)。屏幕点为 NaN(自检直喂世界坐标)时退回 2D 判法。
    /// </summary>
    private MeshEntity? PickFaceAt(IEnumerable<MeshEntity> cands, double sx, double sy, double wx, double wy)
    {
        if (!Viewport.Is2DView && !double.IsNaN(sx))
            return SelectionBox.PickScreen(cands, sx, sy, 0, Viewport.WorldToScreenDepthProjector()) as MeshEntity;
        MeshEntity? best = null; double bestZ = double.MinValue;
        foreach (var me in cands)
        {
            double? z = me.TopZAt(wx, wy);
            if (z == null || z.Value <= bestZ) continue;
            bestZ = z.Value; best = me;
        }
        return best;
    }

    // ═══════════════════ 「选择对象」阶段（AutoCAD 动词-名词流程）═══════════════════
    // 编辑类命令一律：先激活命令 → 提示"选择对象" → 单击/框选加减选 → 右键 / 回车确定 → 再弹参数、再执行。
    // 不再出现"请先选中 X"然后什么都不做的死胡同。已有预选会作为初始选择集带进来，右键即确定。

    private TaskCompletionSource<bool>? _selectObjectsTcs;   // 「选择对象」阶段的等待者
    // 本轮「选择对象」要的是三角网面：光标换黄框+十字、点选按"射线落在面内"判(见 PickAt)，
    // 白方框 + 边线容差那套留给点/线 —— 修复拓扑/合并三角网等选面的命令别再顶着线拾取的方框。
    private bool _editSelectFaces;

    /// <summary>进入「选择对象」阶段并等待用户右键/回车确定（Esc 取消）。返回 false = 取消。faces = 选的是三角网面。</summary>
    private Task<bool> AwaitSelectObjectsAsync(string cmdName, string what, bool faces = false)
    {
        _selectObjectsTcs?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        _selectObjectsTcs = tcs;
        _editName = cmdName;
        _editAwaitSelect = true;
        _editSelectFaces = faces;
        _tool = null; _measure = null; _angle = null;
        EditEcho($"{cmdName}：选择{what} — 单击选取 · 按住拖动框选 · 再点取消选 · 右键/回车确定 · Esc 取消"
               + (_selected.Count > 0 ? $"（已选 {_selected.Count}）" : ""));
        RefreshScene();
        Activate();
        return tcs.Task;
    }

    /// <summary>「选择对象」阶段收尾（由右键确定 / Esc 取消 / 回车调用）。</summary>
    private bool FinishSelectObjects(bool confirmed)
    {
        var tcs = _selectObjectsTcs;
        if (tcs == null) return false;
        _selectObjectsTcs = null;
        _editAwaitSelect = false; _editSelectFaces = false;
        tcs.TrySetResult(confirmed);
        return true;
    }

    /// <summary>
    /// 「选择对象」：命令激活后提示选择，右键确定；选出的对象里筛出需要的类型。
    /// 数量不够就照 AutoCAD 那样说明原因并重新提示，Esc 才退出（返回空表）。
    /// </summary>
    private async Task<List<T>> SelectObjectsAsync<T>(
        string cmdName, string what, int min = 1, Func<T, bool>? filter = null) where T : SceneEntity
    {
        bool faces = typeof(MeshEntity).IsAssignableFrom(typeof(T));
        while (true)
        {
            if (!await AwaitSelectObjectsAsync(cmdName, what, faces))
            { EditEcho($"{cmdName}：已取消"); return new List<T>(); }
            var got = _selected.OfType<T>().Where(e => filter == null || filter(e)).ToList();
            if (got.Count >= min)
            {
                if (got.Count < _selected.Count)
                    EditEcho($"{cmdName}：选中 {_selected.Count} 个，其中 {got.Count} 个{what}参与运算（其余类型已忽略）", EchoLevel.Warn);
                return got;
            }
            EditEcho(_selected.Count == 0
                ? $"{cmdName}：未选中对象，请选择{what}（需 ≥{min} 个；Esc 取消）"
                : $"{cmdName}：所选 {_selected.Count} 个里没有足够的{what}（需 ≥{min} 个），请重选（Esc 取消）", EchoLevel.Warn);
        }
    }

    // ═══════════════════ 派发：编辑组 40 项 ═══════════════════
    /// <summary>
    /// 「编辑」组命令派发。在 <see cref="TryModelingCommandCoreAsync"/> 的 switch 之前调用，
    /// 这里认领的命令即以本文件的忠实实现为准（旧的近似实现不再被这些名字命中）。
    /// </summary>
    private async Task<bool> TryEditGroupCommandAsync(string cmd)
    {
        switch (cmd)
        {
            // ── 点编辑 ──
            case "修改高程点": await EdPointSetZAsync(); return true;
            case "删除重复点": case "去重复点": case "点去重": await EdPointDedupeAsync(); return true;
            case "修改点样式": case "点样式": await EdPointStyleAsync(); return true;
            case "赋节点高程": await EdAssignZAsync(); return true;
            case "点落到面上": case "点落面": case "点投影到面": await EdPointProjectAsync(); return true;
            case "顶点焊接": case "网格焊接": case "合并顶点": await EdWeldAsync(); return true;

            // ── 线编辑 ──
            case "删除重复线": case "去重复线": case "线去重": await EdPolylineDedupeAsync(); return true;
            case "闭合线裁剪": await EdPolylineClipAsync(); return true;
            case "统一线高程": await EdPolylineUnifyZAsync(); return true;
            case "闭合多段线": await EdPolylineCloseAsync(); return true;
            case "加密多段线": await EdPolylineDensifyAsync(); return true;
            case "抽稀等值线": await EdPolylineSimplifyAsync(); return true;
            case "标识起点": case "标识线序":
                EditEcho($"{cmd}：原程序此项为功能预留（占位按钮），未实现", EchoLevel.Warn); return true;
            case "连接多段线": await EdPolylineJoinAsync(); return true;
            case "两线交点": case "求交点": case "线交点": await EdPolylineIntersectAsync(); return true;
            case "线落到面上": await EdPolylineProjectAsync(); return true;

            // ── 面编辑 ──
            case "生成三角网边界": case "网格边界": await EdMeshBoundaryAsync(); return true;
            case "闭合线裁剪面": case "裁剪面": await EdMeshClipByLoopAsync(); return true;   // 「裁剪面」原版与「闭合线裁剪面」调同一命令, 功能区已去掉那个重复项, 只留命令别名
            case "沿线分割三角网": await EdMeshSplitAlongAsync(); return true;
            case "面交线": case "两网交线": case "网格交线": await EdMeshIntersectAsync(); return true;
            case "合并三角网": await EdMergeMeshesAsync(); return true;
            case "多段线嵌入三角网": await EdEmbedPolylineAsync(); return true;
            case "修复拓扑关系": case "网格修复": await EdMeshRepairAsync(); return true;
            case "删除三角面": await EdDeleteMeshFacesAsync(); return true;

            // ── 体编辑 ──
            case "布尔-并集": await EdBooleanAsync(MeshBoolean.Op.Union); return true;
            case "布尔-交集": await EdBooleanAsync(MeshBoolean.Op.Intersection); return true;
            case "布尔-差集": await EdBooleanAsync(MeshBoolean.Op.Difference); return true;
            case "布尔-补集": await EdBooleanAsync(MeshBoolean.Op.Complement); return true;
            case "分割地质体": await EdCutByKnifeAsync(); return true;
        }
        return false;
    }

    /// <summary>
    /// 合并三角网 (MERGEMESH)：选 ≥2 张网 → 焊接接缝重合顶点合成**一张面**、去重叠区重复三角，
    /// 默认沿用第一张源网的图层 / 颜色（「沿线分割三角网」的逆操作）。
    /// </summary>
    private async Task EdMergeMeshesAsync()
    {
        var meshes = await SelectObjectsAsync<MeshEntity>("合并三角网", "三角网", 2);
        if (meshes.Count < 2) return;
        EditEcho($"MERGEMESH (合并三角网)：{meshes.Count} 张网");
        var (verts, tris) = MeshWeld.Concat(meshes
            .Select(m => ((IReadOnlyList<(double x, double y, double z)>)m.Verts, (IReadOnlyList<(int a, int b, int c)>)m.Tris)).ToList());
        var mm = MeshMetrics.Compute(verts, tris);
        double diag = Math.Sqrt((mm.MaxX - mm.MinX) * (mm.MaxX - mm.MinX)
                              + (mm.MaxY - mm.MinY) * (mm.MaxY - mm.MinY)
                              + (mm.MaxZ - mm.MinZ) * (mm.MaxZ - mm.MinZ));
        var w = MeshWeld.Weld(verts, tris, diag > 0 ? diag * 1e-4 : 1e-6, dropDuplicateTris: true);
        BeginChange();
        foreach (var m in meshes) { _scene.Remove(m); _selected.Remove(m); _surfGrids.Remove(m); }
        var me = new MeshEntity(NewMeshName("合并网"), w.Verts, w.Tris);
        me.CopyStyleFrom(meshes[0]);                       // 默认沿用第一张源网的图层 / 颜色
        _scene.Add(me); RefreshScene(); SelectEntities(new SceneEntity[] { me });
        var d = MeshDiagnose.Analyze(w.Verts, w.Tris);
        EditEcho($"合并完成「{me.Name}」：{meshes.Count} 张网 → {w.OutputTris} 三角"
               + $"（接缝焊接 {w.InputVerts - w.OutputVerts} 顶点、去重叠重复三角 {w.DuplicateTris}）· "
               + $"开放边 {d.BoundaryEdges} · 非流形 {d.NonManifoldEdges}", EchoLevel.Success);
    }

    /// <summary>
    /// 多段线嵌入三角网 (EMBED)：容差对话框 → 多段线落面(节点重算：与每条三角边的交点都成节点, 高程取自面)
    /// + 三角网原地保形细分(线段成为网边, 面形不变) → 同时替换三角网与多段线实体。严格 / 部分嵌入按原版逐条回显。
    /// 此前整张重做约束 Delaunay：地形网原有结构被推倒, 线在两顶点之间穿山悬空 —— 用户截图"没能正确嵌入"。
    /// </summary>
    private async Task EdEmbedPolylineAsync()
    {
        var picked = await SelectObjectsAsync<SceneEntity>("多段线嵌入三角网", "约束多段线 + 目标三角网", 1,
            e => (e is PolylineEntity pe && pe.Points.Count >= 2) || e is MeshEntity);
        if (picked.Count == 0) return;
        var lines = picked.OfType<PolylineEntity>().Where(l => l.Points.Count >= 2).ToList();
        if (lines.Count == 0) { EditEcho("多段线嵌入三角网：所选里没有可作约束的多段线", EchoLevel.Warn); return; }
        var mesh = picked.OfType<MeshEntity>().FirstOrDefault()
                   ?? await PickEntityInViewportAsync<MeshEntity>("多段线嵌入三角网：请点选目标三角网…（Esc 取消）");
        if (mesh == null) { EditEcho("多段线嵌入三角网：未指定目标三角网", EchoLevel.Warn); return; }
        double? tol = await AskToleranceAsync("嵌入多段线参数", 1e-6);
        if (tol == null) return;
        EditEcho($"EMBED (多段线嵌入三角网) tolerance={tol.Value:G}");
        var res = EmbedPolylinesIntoMesh(mesh, lines, tol.Value);
        if (res == null) { EditEcho("嵌入失败：三角网为空或退化", EchoLevel.Error); return; }
        int nodes = 0; foreach (var pl in res.Polylines) nodes += pl.Count;
        int srcNodes = 0; foreach (var l in lines) srcNodes += l.Points.Count;
        EditEcho($"细分: 面 {res.OriginalFaces} → {res.NewFaces} (被分 {res.SplitFaces}, 新增点 {res.InsertedPoints}) · "
               + $"多段线节点重算: {srcNodes} → {nodes} 节点(补 {Math.Max(0, nodes - srcNodes)} 个与三角边的交点), 逐点贴面");
        if (res.Strict) EditEcho("严格嵌入：所有 segment 已成为 mesh 边", EchoLevel.Success);
        else EditEcho($"部分嵌入：{res.Unembedded} 个 segment 未能落到 mesh 边上（{res.OutsideNodes} 个节点在三角网范围外, 保留原高程）", EchoLevel.Warn);
    }

    /// <summary>
    /// 多段线落面 + 三角网保形细分, 并把新网、新线换进场景(一步可撤销)。返回 null 表示网退化。
    /// 新线一律带逐点 Z(Zs 按源标高回基), 网外节点保留源高程。
    /// </summary>
    private MeshEmbed.Result? EmbedPolylinesIntoMesh(MeshEntity mesh, IReadOnlyList<PolylineEntity> lines, double tol)
    {
        var input = new List<MeshEmbed.Line>(lines.Count);
        foreach (var l in lines)
            input.Add(new MeshEmbed.Line(l.Points, Enumerable.Range(0, l.Points.Count).Select(l.ZAt).ToList(), l.Closed));
        var res = MeshEmbed.Embed(mesh.Verts, mesh.Tris, input, tol);
        if (res == null || res.Tris.Count == 0) return null;
        BeginChange();
        var nu = new MeshEntity(mesh.Name, res.Verts, OrientUp(res.Verts, res.Tris));
        SwapEntity(mesh, nu);
        _surfGrids.Remove(mesh);
        for (int i = 0; i < lines.Count && i < res.Polylines.Count; i++)
        {
            var src = lines[i]; var pts = res.Polylines[i];
            if (pts.Count == 0) continue;
            var pl = new PolylineEntity { Closed = src.Closed && pts.Count > 2 };
            pl.CopyStyleFrom(src);
            pl.Zs = new List<double>(pts.Count);
            foreach (var p in pts) { pl.Points.Add((p.x, p.y)); pl.Zs.Add(p.z - src.Elevation); }
            SwapEntity(src, pl);
        }
        RefreshScene(); HighlightSelection();
        return res;
    }

    /// <summary>原位替换场景实体(保留次序与选中态)；不记撤销, 调用方自己 BeginChange。</summary>
    private void SwapEntity(SceneEntity old, SceneEntity nu)
    {
        int idx = _scene.Entities.IndexOf(old);
        if (idx >= 0) _scene.Entities[idx] = nu; else _scene.Add(nu);
        int si = _selected.IndexOf(old); if (si >= 0) _selected[si] = nu;
    }

    // ══════════════════════════════ 体编辑 ══════════════════════════════

    /// <summary>
    /// 布尔运算 (BOOLUNION / BOOLINTER / BOOLDIFF / BOOLCOMP)：对当前选中的 2 个闭合三角网直接运算，
    /// 结果替换原两体（选择顺序 = A、B；差集 = A−B，补集 = B−A）。
    /// </summary>
    private async Task EdBooleanAsync(MeshBoolean.Op op)
    {
        string tag = op switch
        {
            MeshBoolean.Op.Union => "BOOLUNION",
            MeshBoolean.Op.Intersection => "BOOLINTER",
            MeshBoolean.Op.Difference => "BOOLDIFF",
            _ => "BOOLCOMP",
        };
        var meshes = await SelectObjectsAsync<MeshEntity>(
            $"布尔-{MeshBoolean.OpName(op)}", "两个闭合三角网体（先选的是 A，后选的是 B）", 2);
        if (meshes.Count < 2) return;
        if (meshes.Count > 2)
            EditEcho($"布尔-{MeshBoolean.OpName(op)}：选中 {meshes.Count} 个，只取前两个作 A、B", EchoLevel.Warn);
        var mA = meshes[0]; var mB = meshes[1];
        EditEcho($"{tag} (布尔-{MeshBoolean.OpName(op)})：A =「{mA.Name}」, B =「{mB.Name}」");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = MeshBoolean.Compute(mA.Verts, mA.Tris, mB.Verts, mB.Tris, op);
        if (!r.Success) { EditEcho($"布尔运算失败：{r.Error}", EchoLevel.Error); return; }
        EditEcho($"原 A: {r.FacesFromA} 面 + 原 B: {r.FacesFromB} 面 (分裂 {r.SplitFaces})");
        BeginChange();
        foreach (var m in new[] { mA, mB }) { _scene.Remove(m); _selected.Remove(m); _surfGrids.Remove(m); }
        var res = new MeshEntity(NewMeshName($"{MeshBoolean.OpName(op)}体"), r.Verts, r.Tris);
        res.CopyStyleFrom(mA);
        _scene.Add(res); RefreshScene(); SelectEntities(new SceneEntity[] { res });
        var d = MeshDiagnose.Analyze(r.Verts, r.Tris);
        EditEcho($"{MeshBoolean.OpName(op)} 完成：{r.Verts.Count} 顶点 / {r.Tris.Count} 面 · "
               + $"{(d.IsClosed ? $"闭合体, 体积 {MeshMetrics.RobustVolume(r.Verts, r.Tris):0.##}" : $"开放(开放边 {d.BoundaryEdges})")} · "
               + $"{sw.ElapsedMilliseconds} ms →「{res.Name}」", EchoLevel.Success);
    }

    /// <summary>
    /// 分割地质体 (CUTBYKNIFE)：对话框选保留侧 → ① 点选刀(开放面) ② 点选闭合实体 →
    /// 保留所选一侧并用刀在体内的部分封盖，新闭合体替换原实体。
    /// </summary>
    private async Task EdCutByKnifeAsync()
    {
        var dlg = await PromptDialog.AskAsync(this, "分割地质体", new[]
        { new PromptDialog.Field("side", "保留侧", "刀下方", null, null, false, new[] { "刀下方", "刀上方" }) });
        if (dlg == null) { EditEcho("分割地质体：用户取消"); return; }
        bool keepBelow = dlg.S("side") == "刀下方";
        EditEcho($"刀切闭合实体（保留{(keepBelow ? "刀下方" : "刀上方")}）：先点选刀(开放面)，再点选要被切的闭合实体…");
        var knife = await PickEntityInViewportAsync<MeshEntity>(
            "① 点选刀（开放面）…（Esc 取消）", null, "该处没有三角网，请重选刀面（Esc 取消）");
        if (knife == null) { EditEcho("分割地质体：已取消"); return; }
        var solid = await PickEntityInViewportAsync<MeshEntity>(
            "② 点选要被切的闭合实体…（Esc 取消）", e => !ReferenceEquals(e, knife), "该处没有另一个三角网，请重选（Esc 取消）");
        if (solid == null) { EditEcho("分割地质体：已取消"); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = MeshBoolean.CutByKnife(solid.Verts, solid.Tris, knife.Verts, knife.Tris, keepBelow);
        if (!r.Success) { EditEcho($"分割地质体失败：{r.Error}", EchoLevel.Error); return; }
        EditEcho($"保留侧 {r.FacesFromA} 面 + 刀面封盖 {r.FacesFromB} 面 (分裂 {r.SplitFaces})");
        var nu = new MeshEntity(NewMeshName(solid.Name + (keepBelow ? "-下" : "-上")), r.Verts, r.Tris);
        ReplaceMesh(solid, nu);
        var d = MeshDiagnose.Analyze(r.Verts, r.Tris);
        EditEcho($"刀切完成：「{nu.Name}」{r.Verts.Count} 顶点 / {r.Tris.Count} 面 · "
               + $"{(d.IsClosed ? $"水密闭合体, 体积 {MeshMetrics.RobustVolume(r.Verts, r.Tris):0.##}" : $"开放边 {d.BoundaryEdges}（接缝未完全闭合，可用「修复拓扑关系」补）")} · "
               + $"{sw.ElapsedMilliseconds} ms", EchoLevel.Success);
    }

    // ══════════════════════════════ 点编辑 ══════════════════════════════

    /// <summary>修改高程点 (POINTSETZ)：参数对话框取目标 Z → 选中点的 Z 统一置为该值。</summary>
    private async Task EdPointSetZAsync()
    {
        var pts = await SelectObjectsAsync<PointEntity>("修改高程点", "点");
        if (pts.Count == 0) return;
        var dlg = await PromptDialog.AskAsync(this, "修改高程点",
            new[] { new PromptDialog.Field("z", "目标 Z", pts[0].Elevation.ToString("0.###", Inv), "m") },
            $"把选中的 {pts.Count} 个点的高程统一设为指定值");
        if (dlg == null) { EditEcho("修改高程点：用户取消"); return; }
        double z = dlg.D("z");
        EditEcho($"POINTSETZ z={z:F3}");
        BeginChange();
        foreach (var p in pts) p.Elevation = z;
        RefreshScene(); HighlightSelection();   // 就地改 Z 的图元仍选中: 高亮线缓存(_hlEntGeom)得重镶嵌, 否则 3D 里青色高亮留在旧高程、夹点却在新高程
        EditEcho($"{pts.Count} 个点的 Z 已置为 {z:F3}", EchoLevel.Success);
    }

    /// <summary>删除重复点 (POINTDEDUPE)：容差对话框 → 选中点按容差去重。</summary>
    private async Task EdPointDedupeAsync()
    {
        var pts = await SelectObjectsAsync<PointEntity>("删除重复点", "点", 2);
        if (pts.Count < 2) return;
        double? tol = await AskToleranceAsync("删除重复点", 1e-3);
        if (tol == null) return;
        EditEcho($"POINTDEDUPE tolerance={tol.Value:G}");
        var coords = pts.Select(p => (p.X, p.Y)).ToList();
        var keep = new HashSet<int>(GeomDedup.KeepAfterDedup(coords, tol.Value));
        int removed = 0;
        BeginChange();
        var newSel = new List<SceneEntity>();
        for (int i = 0; i < pts.Count; i++)
        {
            if (keep.Contains(i)) newSel.Add(pts[i]);
            else { _scene.Remove(pts[i]); removed++; }
        }
        SelectEntities(newSel); RefreshScene();
        EditEcho(removed == 0
            ? $"✓ 未发现重复点（共 {pts.Count} 个）"
            : $"✓ 删除重复点：{pts.Count} → {pts.Count - removed} (-{removed})", EchoLevel.Success);
    }

    /// <summary>修改点样式：样式(十字/叉/圆点) + 大小，批量改选中 Point。</summary>
    private async Task EdPointStyleAsync()
    {
        var pts = await SelectObjectsAsync<PointEntity>("修改点样式", "点");
        if (pts.Count == 0) return;
        // 原版 3 选项 = AcDbPoint 的 Cross / X / Dot；这里映射到 Kylin 的 PDMODE：2=十字, 3=叉, 0=点
        string cur = (pts[0].Style & 31) switch { 3 => "X (叉)", 0 => "Dot (圆点)", _ => "Cross (十字)" };
        var dlg = await PromptDialog.AskAsync(this, "修改点样式", new[]
        {
            new PromptDialog.Field("style", "样式", cur, null, null, false, new[] { "Cross (十字)", "X (叉)", "Dot (圆点)" }),
            new PromptDialog.Field("size", "大小", pts[0].Size > 0 ? pts[0].Size.ToString("0.##", Inv) : "3", "世界单位", "标记半长（世界单位）"),
        }, $"批量修改 {pts.Count} 个点的显示样式与大小");
        if (dlg == null) { EditEcho("修改点样式：用户取消"); return; }
        string s = dlg.S("style");
        int style = s.StartsWith("X") ? 3 : s.StartsWith("Dot") ? 0 : 2;
        double size = dlg.D("size");
        BeginChange();
        foreach (var p in pts) { p.Style = style; if (size > 0) p.Size = size; }
        RefreshScene(); HighlightSelection();   // 样式/大小变了, 选中高亮要按新符号重镶嵌
        EditEcho($"修改点样式：style={s.Split(' ')[0]} size={size:0.##}，已更新 {pts.Count}", EchoLevel.Success);
    }

    /// <summary>赋节点高程：按公式 Z = a·X + b·Y + c 给选中的 Point / Polyline 逐节点赋 Z。</summary>
    private async Task EdAssignZAsync()
    {
        var ents = await SelectObjectsAsync<SceneEntity>("赋节点高程", "点 / 多段线", 1,
            e => e is PointEntity or PolylineEntity);
        if (ents.Count == 0) return;
        var pts = ents.OfType<PointEntity>().ToList();
        var lines = ents.OfType<PolylineEntity>().ToList();
        var dlg = await PromptDialog.AskAsync(this, "赋节点高程 (Z = a·X + b·Y + c)", new[]
        {
            new PromptDialog.Field("a", "X 系数 a", "0", null, "Z 对 X 的斜率"),
            new PromptDialog.Field("b", "Y 系数 b", "0", null, "Z 对 Y 的斜率"),
            new PromptDialog.Field("c", "常数 c", "0", null, "基准平面截距"),
        }, "按平面方程给选中对象的每个节点重算高程");
        if (dlg == null) { EditEcho("赋节点高程：用户取消"); return; }
        double a = dlg.D("a"), b = dlg.D("b"), c = dlg.D("c");
        BeginChange();
        int n = 0, nodes = 0;
        foreach (var p in pts) { p.Elevation = a * p.X + b * p.Y + c; n++; nodes++; }
        foreach (var l in lines)
        {
            var zs = new List<double>(l.Points.Count);
            foreach (var (x, y) in l.Points) zs.Add(a * x + b * y + c);
            l.Zs = zs; l.Elevation = 0; n++; nodes += zs.Count;
        }
        RefreshScene(); HighlightSelection();   // 同「修改高程点」: 就地改 Z, 高亮/夹点/特性面板都要跟上
        EditEcho($"赋节点高程：Z = {a}·X + {b}·Y + {c}，已更新 {n} 个实体 / {nodes} 个节点",
                 n > 0 ? EchoLevel.Success : EchoLevel.Warn);
    }

    /// <summary>点落到面上 (POINTPROJECT)：选 N 点 + 1 三角网 → 点的 Z 投影到面。</summary>
    private async Task EdPointProjectAsync()
    {
        var picked = await SelectObjectsAsync<SceneEntity>("点落到面上", "点（可连同目标三角网一起选）", 1,
            e => e is PointEntity or MeshEntity);
        if (picked.Count == 0) return;
        var pts = picked.OfType<PointEntity>().ToList();
        if (pts.Count == 0) { EditEcho("点落到面上：所选里没有点实体", EchoLevel.Warn); return; }
        var mesh = picked.OfType<MeshEntity>().FirstOrDefault()
                   ?? await PickEntityInViewportAsync<MeshEntity>("点落到面上：请在视口中点选目标三角网…（Esc 取消）");
        if (mesh == null) { EditEcho("点落到面上：未指定目标三角网", EchoLevel.Warn); return; }
        double? tol = await AskToleranceAsync("点落到面上", 1e-6);
        if (tol == null) return;
        EditEcho($"POINTPROJECT tolerance={tol.Value:G}");
        BeginChange();
        int hit = 0;
        foreach (var p in pts) { var z = MeshZ(mesh, p.X, p.Y); if (z.HasValue) { p.Elevation = z.Value; hit++; } }
        RefreshScene(); HighlightSelection();   // 同「修改高程点」
        int missed = pts.Count - hit;
        if (missed > 0) EditEcho($"{missed} 个点未能投影到 mesh 范围内", EchoLevel.Warn);
        EditEcho($"已投影 {hit} 个点的 Z 到「{mesh.Name}」表面", EchoLevel.Success);
    }

    /// <summary>顶点焊接 (WELD)：容差对话框 → 合并选中三角网中距离相近的重复顶点。</summary>
    private async Task EdWeldAsync()
    {
        var meshes = await SelectObjectsAsync<MeshEntity>("顶点焊接", "三角网");
        if (meshes.Count == 0) return;
        double? tol = await AskToleranceAsync("顶点焊接参数", 1e-6);
        if (tol == null) return;
        EditEcho($"WELD (顶点焊接) tolerance={tol.Value:G}");
        int vIn = 0, vOut = 0, fIn = 0, fOut = 0;
        foreach (var m in meshes.ToList())
        {
            var w = MeshWeld.Weld(m.Verts, m.Tris, tol.Value, dropDuplicateTris: true);
            vIn += w.InputVerts; vOut += w.OutputVerts; fIn += w.InputTris; fOut += w.OutputTris;
            ReplaceMesh(m, new MeshEntity(m.Name, w.Verts, w.Tris));
        }
        if (vIn == vOut) { EditEcho("无重复顶点，未做任何合并", EchoLevel.Success); return; }
        EditEcho($"焊接完成：顶点 {vIn} → {vOut} (合并 {vIn - vOut} 个), 面 {fIn} → {fOut}", EchoLevel.Success);
    }

    // ══════════════════════════════ 线编辑 ══════════════════════════════

    /// <summary>删除重复线 (POLYDEDUPE)：容差对话框 → 选中多段线按几何重合去重。</summary>
    private async Task EdPolylineDedupeAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("删除重复线", "多段线", 2);
        if (polys.Count < 2) return;
        double? tol = await AskToleranceAsync("删除重复线", 1e-3);
        if (tol == null) return;
        EditEcho($"POLYDEDUPE tolerance={tol.Value:G}");
        var kept = new List<PolylineEntity>();
        var dups = new List<PolylineEntity>();
        foreach (var p in polys)
        {
            bool isDup = kept.Any(q => GeomDedup.SamePolyline(p.Points, p.Closed, q.Points, q.Closed, tol.Value));
            if (isDup) dups.Add(p); else kept.Add(p);
        }
        if (dups.Count == 0) { EditEcho($"未发现重复多段线（共 {polys.Count} 条）", EchoLevel.Success); return; }
        BeginChange();
        foreach (var d in dups) _scene.Remove(d);
        SelectEntities(kept); RefreshScene();
        EditEcho($"删除重复多段线：{polys.Count} → {kept.Count} (-{dups.Count})", EchoLevel.Success);
    }

    /// <summary>
    /// 闭合线裁剪 (POLYCLIP)：参数对话框（容差 + 保留侧）→ 视口点选闭合形状作裁刀，
    /// 被裁对象 = 模型空间里其余全部多段线（原版语义，不需要事先选中它们）。
    /// </summary>
    private async Task EdPolylineClipAsync()
    {
        var dlg = await PromptDialog.AskAsync(this, "闭合线裁剪线", new[]
        {
            new PromptDialog.Field("tolerance", "几何容差", "0.001", null, "判定接近/重合的距离阈值"),
            new PromptDialog.Field("side", "保留侧", "圈内", null, null, false, new[] { "圈内", "圈外" }),
        });
        if (dlg == null) { EditEcho("闭合线裁剪：用户取消"); return; }
        bool keepInside = dlg.S("side") == "圈内";
        EditEcho($"POLYCLIP tolerance={dlg.D("tolerance"):G} side={(keepInside ? "圈内" : "圈外")}");
        var knife = await PickEntityInViewportAsync<PolylineEntity>(
            "请用小方框光标点击作为裁刀的闭合形状（多段线/矩形/多边形）…（Esc 取消）",
            p => p.Closed && p.Points.Count >= 3,
            "该处没有闭合多段线，请重选裁刀（Esc 取消）");
        if (knife == null) { EditEcho("闭合线裁剪：已取消"); return; }

        var targets = _scene.Entities.OfType<PolylineEntity>()
            .Where(l => !ReferenceEquals(l, knife) && l.Visible && _layers.IsSelectable(l.LayerName) && l.Points.Count >= 2)
            .ToList();
        if (targets.Count == 0) { EditEcho("闭合线裁剪：模型空间里没有可被裁剪的多段线", EchoLevel.Warn); return; }
        BeginChange();
        int made = 0, dropped = 0, intact = 0;
        var added = new List<SceneEntity>();
        foreach (var l in targets)
        {
            var pieces = PolylineClipper.ClipPolyline(knife.Points, Line3(l), l.Closed, keepInside);
            if (pieces.Count == 0) { _scene.Remove(l); dropped++; continue; }
            // 整条都在保留侧 → 原对象原封不动留着。重建一条"看起来一样"的新线会白白丢掉
            // 闭合标志、以及选择集/夹点对原对象的引用；没被切的线本来就不该动。
            if (IsIntact(l, pieces)) { intact++; continue; }
            _scene.Remove(l);
            foreach (var pc in pieces)
            {
                var pl = DerivePolyline(l, pc, closed: false);   // 碎片必然是开口的
                _scene.Add(pl); added.Add(pl); made++;
            }
        }
        RefreshScene();
        if (added.Count > 0) SelectEntities(added);
        EditEcho($"闭合线裁剪：{targets.Count} 条线保留{(keepInside ? "圈内" : "圈外")} → {made} 段"
               + (intact > 0 ? $"（{intact} 条整条在保留侧, 原样未动）" : "")
               + (dropped > 0 ? $"（{dropped} 条整条落在保留侧之外已删除）" : ""), EchoLevel.Success);
    }

    /// <summary>裁剪结果是否"整条都在保留侧"（点数与原线一致，闭合线的碎片首尾重合）——是则原对象不该动。</summary>
    private static bool IsIntact(PolylineEntity src, List<List<(double x, double y, double z)>> pieces)
    {
        if (pieces.Count != 1) return false;
        var pc = pieces[0];
        int expect = src.Closed ? src.Points.Count + 1 : src.Points.Count;
        if (pc.Count != expect) return false;
        for (int i = 0; i < src.Points.Count; i++)
            if (Math.Abs(pc[i].x - src.Points[i].x) > 1e-9 || Math.Abs(pc[i].y - src.Points[i].y) > 1e-9) return false;
        return true;
    }

    /// <summary>统一线高程 (POLYUNIFYZ)：把选中多段线所有节点 Z 置为指定值。</summary>
    private async Task EdPolylineUnifyZAsync()
    {
        var lines = await SelectObjectsAsync<PolylineEntity>("统一线高程", "多段线");
        if (lines.Count == 0) return;
        var dlg = await PromptDialog.AskAsync(this, "统一线高程",
            new[] { new PromptDialog.Field("z", "目标 Z", lines[0].Elevation.ToString("0.###", Inv), "m") });
        if (dlg == null) { EditEcho("统一线高程：用户取消"); return; }
        double z = dlg.D("z");
        EditEcho($"POLYUNIFYZ z={z:F3}");
        BeginChange();
        int verts = 0;
        foreach (var l in lines) { l.Elevation = z; l.Zs = null; verts += l.Points.Count; }
        RefreshScene(); HighlightSelection();   // 同「修改高程点」: 就地改 Z, 高亮/夹点要跟上
        EditEcho($"{lines.Count} 条多段线 / {verts} 个顶点 Z 已置为 {z:F3}", EchoLevel.Success);
    }

    /// <summary>闭合多段线 (POLYCLOSE)：把选中多段线全部置为闭合（无参数）。</summary>
    private async Task EdPolylineCloseAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("闭合多段线", "多段线");
        if (polys.Count == 0) return;
        EditEcho("POLYCLOSE (闭合多段线)");
        var open = polys.Where(p => !p.Closed && p.Points.Count >= 3).ToList();
        if (open.Count == 0) { EditEcho("所有选中多段线已经是闭合状态", EchoLevel.Success); return; }
        BeginChange();
        var newSel = new List<SceneEntity>();
        foreach (var p in polys)
        {
            if (p.Closed || p.Points.Count < 3) { newSel.Add(p); continue; }
            var np = new PolylineEntity { Closed = true }; np.CopyStyleFrom(p);
            np.Points.AddRange(p.Points);
            if (p.Zs != null) np.Zs = new List<double>(p.Zs);
            _scene.Replace(p, np); newSel.Add(np);
        }
        SelectEntities(newSel); RefreshScene();
        EditEcho($"闭合完成：{open.Count} 条多段线", EchoLevel.Success);
    }

    /// <summary>加密多段线 (POLYDENSIFY)：最大间距对话框 → 每段超长即等距插点。</summary>
    private async Task EdPolylineDensifyAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("加密多段线", "多段线");
        if (polys.Count == 0) return;
        var dlg = await PromptDialog.AskAsync(this, "加密多段线",
            new[] { new PromptDialog.Field("maxStep", "最大间距", "10", "m", "每段超过此长度则插入中间点") });
        if (dlg == null) { EditEcho("加密多段线：用户取消"); return; }
        double step = Math.Max(dlg.D("maxStep"), 1e-6);
        EditEcho($"POLYDENSIFY maxStep={step:F3}");
        BeginChange();
        int ov = 0, fv = 0;
        var newSel = new List<SceneEntity>();
        foreach (var p in polys)
        {
            var densified = PolylineEdit.Densify(p.Points, p.Closed, step);
            var np = DerivePolyline(p, densified, p.Closed);
            ov += p.Points.Count; fv += densified.Count;
            _scene.Replace(p, np); newSel.Add(np);
        }
        SelectEntities(newSel); RefreshScene();
        EditEcho($"{polys.Count} 条多段线：顶点 {ov} → {fv} (+{fv - ov})", EchoLevel.Success);
    }

    /// <summary>抽稀等值线 (POLYSIMPLIFY)：Douglas-Peucker，容差 + 最少节点数（节点少的台阶线整条不动）。</summary>
    private async Task EdPolylineSimplifyAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("抽稀等值线", "多段线 / 等值线");
        if (polys.Count == 0) return;
        var dlg = await PromptDialog.AskAsync(this, "抽稀等值线", new[]
        {
            new PromptDialog.Field("tolerance", "抽稀容差", "1", "m",
                "Douglas-Peucker 容差：偏离原线不超过此值的点被删，越大越稀。建三角网前用它给过密等值线减点。"),
            new PromptDialog.Field("minNodes", "最少节点数", "50", null,
                "只抽稀节点数超过此值的线（密集等值线）；节点少的台阶线/简单线整条不动。"),
        });
        if (dlg == null) { EditEcho("抽稀等值线：用户取消"); return; }
        double tol = Math.Max(dlg.D("tolerance"), 1e-6);
        int minNodes = Math.Max(dlg.I("minNodes"), 3);
        EditEcho($"POLYSIMPLIFY tolerance={tol:F3} minNodes={minNodes}（只动节点>{minNodes} 的线，台阶线不动）");
        BeginChange();
        int processed = 0, ov = 0, fv = 0;
        var newSel = new List<SceneEntity>();
        foreach (var p in polys)
        {
            if (p.Points.Count <= minNodes) { newSel.Add(p); continue; }
            var simp = PolylineSimplify.DouglasPeucker(p.Points, tol);
            if (simp.Count < 2 || simp.Count == p.Points.Count) { newSel.Add(p); continue; }
            var np = DerivePolyline(p, simp, p.Closed);
            ov += p.Points.Count; fv += simp.Count; processed++;
            _scene.Replace(p, np); newSel.Add(np);
        }
        SelectEntities(newSel); RefreshScene();
        if (processed == 0) { EditEcho($"选中的 {polys.Count} 条线节点均不超过 {minNodes}，未做抽稀", EchoLevel.Success); return; }
        EditEcho($"抽稀 {processed} 条等值线(节点>{minNodes})：顶点 {ov} → {fv} (减 {ov - fv})；台阶线/短线未动", EchoLevel.Success);
    }

    /// <summary>连接多段线 (POLYJOIN)：端点容差匹配，把 N 条合并成更少条。</summary>
    private async Task EdPolylineJoinAsync()
    {
        var polys = await SelectObjectsAsync<PolylineEntity>("连接多段线", "多段线", 2, p => p.Points.Count >= 2);
        if (polys.Count < 2) return;
        double? tol = await AskToleranceAsync("连接多段线", 1e-3);
        if (tol == null) return;
        EditEcho($"POLYJOIN tolerance={tol.Value:G}");
        var chains = PolylineJoin.Join(polys.Select(p => (IReadOnlyList<(double x, double y)>)p.Points).ToList(), tol.Value);
        if (chains.Count >= polys.Count)
        { EditEcho($"无可连接的端点对（共 {polys.Count} 条多段线）", EchoLevel.Warn); return; }
        BeginChange();
        foreach (var p in polys) _scene.Remove(p);
        var added = new List<SceneEntity>();
        foreach (var ch in chains)
        {
            if (ch.Count < 2) continue;
            bool closed = ch.Count > 2 && Math.Abs(ch[0].x - ch[^1].x) < tol.Value && Math.Abs(ch[0].y - ch[^1].y) < tol.Value;
            // 样式随第一条源线（同原版 POLYJOIN），逐点 Z 取"离哪条源线近就用哪条的"
            var np = DerivePolyline(polys[0], ch, closed);
            var absZ = DrapeZAlongAny(polys, ch);
            np.Zs = absZ == null ? null : PolylineEdit.RebaseZ(absZ, np.Elevation);
            _scene.Add(np); added.Add(np);
        }
        SelectEntities(added); RefreshScene();
        EditEcho($"连接完成：{polys.Count} → {added.Count} (合并了 {polys.Count - added.Count} 条)", EchoLevel.Success);
    }

    /// <summary>
    /// 合并后的链按"离哪条源线最近就取哪条的 Z"重建**绝对**高程（源线全是平面线则返回 null）。
    /// 写回实体前须经 <see cref="PolylineEdit.RebaseZ"/> 回基。
    /// </summary>
    private static List<double>? DrapeZAlongAny(IReadOnlyList<PolylineEntity> src, IReadOnlyList<(double x, double y)> pts)
    {
        if (!src.Any(s => s.Has3D)) return null;
        var best = new List<double>(pts.Count);
        var bestD = new List<double>(pts.Count);
        for (int i = 0; i < pts.Count; i++) { best.Add(0); bestD.Add(double.MaxValue); }
        foreach (var s in src)
        {
            var absZ = Enumerable.Range(0, s.Points.Count).Select(s.ZAt).ToList();
            var z = PolylineEdit.SampleZAlong(s.Points, absZ, pts);
            for (int i = 0; i < pts.Count; i++)
            {
                double d = NearestDist2(s.Points, pts[i]);
                if (d < bestD[i]) { bestD[i] = d; best[i] = z[i]; }
            }
        }
        return best;
    }

    /// <summary>点到折线的最近距离平方（挑"离哪条源线最近"用）。</summary>
    private static double NearestDist2(IReadOnlyList<(double x, double y)> line, (double x, double y) p)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var (ax, ay) = line[i]; var (bx, by) = line[i + 1];
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            double t = len2 > 1e-18 ? Math.Clamp(((p.x - ax) * dx + (p.y - ay) * dy) / len2, 0, 1) : 0;
            double qx = ax + dx * t, qy = ay + dy * t;
            double d2 = (p.x - qx) * (p.x - qx) + (p.y - qy) * (p.y - qy);
            if (d2 < best) best = d2;
        }
        return best;
    }

    /// <summary>两线交点 (POLYINTERSECT)：选 2 条多段线 2D 求交，每个交点创建一个 Point。</summary>
    private async Task EdPolylineIntersectAsync()
    {
        var ents = await SelectObjectsAsync<SceneEntity>("两线交点", "多段线 / 直线 / 矩形", 2,
            e => AsSequence(e, out _, out _));
        if (ents.Count < 2) return;
        var seqs = new List<(List<(double x, double y)> pts, bool closed)>();
        foreach (var e in ents)
            if (AsSequence(e, out var pts, out var closed)) seqs.Add((pts, closed));
        double? tol = await AskToleranceAsync("两线交点", 1e-6);
        if (tol == null) return;
        EditEcho($"POLYINTERSECT tolerance={tol.Value:G}");
        double t = Math.Max(tol.Value, 1e-12);
        var all = new List<(double x, double y)>();
        for (int i = 0; i < seqs.Count; i++)
            for (int j = i + 1; j < seqs.Count; j++)
                foreach (var h in PolylineIntersect.Between(seqs[i].pts, seqs[i].closed, seqs[j].pts, seqs[j].closed, t))
                    if (!all.Any(q => (q.x - h.x) * (q.x - h.x) + (q.y - h.y) * (q.y - h.y) <= t * t)) all.Add(h);
        if (all.Count == 0) { EditEcho("两条多段线无 2D 交点", EchoLevel.Success); return; }
        BeginChange();
        var added = new List<SceneEntity>();
        foreach (var (x, y) in all)
        {
            var pe = new PointEntity { X = x, Y = y, Cr = 0.95f, Cg = 0.3f, Cb = 0.3f };
            AssignLayer(pe); _scene.Add(pe); added.Add(pe);
        }
        SelectEntities(added); RefreshScene();
        EditEcho($"找到 {all.Count} 个交点，已创建 POINT 实体", EchoLevel.Success);
    }

    /// <summary>
    /// 线落到面上 (POLYPROJECT)：选 N 多段线 + 1 三角网 → 节点重算落面：原顶点 Z 投到面上, 且每段与三角边的交点
    /// 都补成节点(高程沿边插值), 整条线逐段贴面。此前只投顶点(同原版内核), 两顶点之间的直段穿山悬空
    /// —— 用户截图"只是节点在面上, 中间没有插值在面上"。网外顶点保留原高程。
    /// </summary>
    private async Task EdPolylineProjectAsync()
    {
        var picked = await SelectObjectsAsync<SceneEntity>("线落到面上", "多段线（可连同目标三角网一起选）", 1,
            e => e is PolylineEntity or MeshEntity);
        if (picked.Count == 0) return;
        var lines = picked.OfType<PolylineEntity>().Where(l => l.Points.Count > 0).ToList();
        if (lines.Count == 0) { EditEcho("线落到面上：所选里没有多段线", EchoLevel.Warn); return; }
        var mesh = picked.OfType<MeshEntity>().FirstOrDefault()
                   ?? await PickEntityInViewportAsync<MeshEntity>("线落到面上：请在视口中点选目标三角网…（Esc 取消）");
        if (mesh == null) { EditEcho("线落到面上：未指定目标三角网", EchoLevel.Warn); return; }
        double? tol = await AskToleranceAsync("线落到面上", 1e-6);
        if (tol == null) return;
        EditEcho($"POLYPROJECT tolerance={tol.Value:G}");
        var input = new List<MeshEmbed.Line>(lines.Count);
        foreach (var l in lines)
            input.Add(new MeshEmbed.Line(l.Points, Enumerable.Range(0, l.Points.Count).Select(l.ZAt).ToList(), l.Closed));
        var res = MeshEmbed.Drape(mesh.Verts, mesh.Tris, input, tol.Value);
        if (res == null) { EditEcho("线落到面上：三角网为空或退化", EchoLevel.Error); return; }
        BeginChange();
        for (int i = 0; i < lines.Count && i < res.Polylines.Count; i++)
        {
            var l = lines[i]; var pts = res.Polylines[i];
            if (pts.Count == 0) continue;
            // 原地改点串 + 逐点 Z(按源标高回基), 实体身份/图层/颜色/选中态都不动
            var xy = new List<(double x, double y)>(pts.Count); var zs = new List<double>(pts.Count);
            foreach (var p in pts) { xy.Add((p.x, p.y)); zs.Add(p.z - l.Elevation); }
            l.Points = xy; l.Zs = zs;
            if (l.Closed && xy.Count < 3) l.Closed = false;
        }
        RefreshScene(); HighlightSelection();   // 同「修改高程点」
        if (res.OutsideNodes > 0) EditEcho($"{res.OutsideNodes} 个顶点在 mesh 范围外, 保留原高程", EchoLevel.Warn);
        EditEcho($"{lines.Count} 条多段线已投影到「{mesh.Name}」：节点重算 {res.InputNodes} → {res.OutputNodes}"
               + $"(补 {Math.Max(0, res.OutputNodes - res.InputNodes)} 个与三角边的交点), 逐段贴面", EchoLevel.Success);
    }

    // ══════════════════════════════ 面编辑 ══════════════════════════════

    /// <summary>
    /// 生成三角网边界 (BOUNDARY)：视口点选三角网 → 提取最大边界环
    /// （开放面取真实边界最大环；闭合体取凸包外轮廓）→ 闭合多段线落「边界」图层。
    /// </summary>
    private async Task EdMeshBoundaryAsync()
    {
        EditEcho("BOUNDARY (生成三角网边界)：请在视口中点选三角网…");
        var m = await PickEntityInViewportAsync<MeshEntity>(
            "生成三角网边界：请点选三角网…（Esc 取消）", null, "该处没有三角网，请重选（Esc 取消）");
        if (m == null) { EditEcho("生成三角网边界：已取消"); return; }
        EditEcho($"生成三角网边界：「{m.Name}」{m.TriangleCount} 三角，提取中…");
        // 百万三角的面模型朝向一致化 + 边界环要 1~2 s(见 MeshBoundaryBench), 别占 UI 线程; 网格顶点/三角在此期间只读。
        var loops = await Task.Run(() => MeshBoundaryLoops.Extract(m.Verts, OrientUp(m.Verts, m.Tris)));
        List<(double x, double y, double z)> ring;
        string how;
        if (loops.Count > 0)
        {
            ring = loops.OrderByDescending(RingLengthXY).First();
            how = $"开放边界最大环（共 {loops.Count} 环）";
        }
        else
        {
            var hull = GeomHull.ConvexHull(m.Verts.Select(v => (v.x, v.y)).ToList());
            if (hull.Count < 3) { EditEcho("生成三角网边界失败：无法提取边界", EchoLevel.Error); return; }
            ring = hull.Select(p => (p.x, p.y, MeshZ(m, p.x, p.y) ?? m.Bounds.minZ)).ToList();
            how = "闭合体 → 凸包外轮廓";
        }
        BeginChange();
        var pl = Poly3(ring, true, 0.95f, 0.5f, 0.2f);
        var layer = _layers.Get("边界") ?? _layers.EnsureImported("边界", 0.95f, 0.5f, 0.2f);
        pl.LayerName = layer.Name;
        _scene.Add(pl);
        PopulateDrawingLayers(); RefreshScene(); SelectEntities(new SceneEntity[] { pl });
        var b = m.Bounds;
        EditEcho($"边界提取：「{m.Name}」{how} · {ring.Count} 节点 · "
               + $"AABB {b.maxX - b.minX:0.#}×{b.maxY - b.minY:0.#} · 已落「边界」图层", EchoLevel.Success);
    }

    private static double RingLengthXY(List<(double x, double y, double z)> ring)
    {
        double s = 0;
        for (int i = 0; i + 1 < ring.Count; i++)
            s += Math.Sqrt((ring[i + 1].x - ring[i].x) * (ring[i + 1].x - ring[i].x)
                         + (ring[i + 1].y - ring[i].y) * (ring[i + 1].y - ring[i].y));
        return s;
    }

    /// <summary>
    /// 闭合线裁剪面 / 裁剪面 (CLIP)：对话框选保留侧 →
    /// ① 方框光标点选闭合多段线（裁刀）→ ② 黄框光标点选三角网。
    /// </summary>
    private async Task EdMeshClipByLoopAsync()
    {
        var dlg = await PromptDialog.AskAsync(this, "闭合线裁剪面", new[]
        { new PromptDialog.Field("side", "保留侧", "圈内", null, null, false, new[] { "圈内", "圈外" }) });
        if (dlg == null) { EditEcho("闭合线裁剪：用户取消"); return; }
        bool keepInside = dlg.S("side") == "圈内";
        EditEcho($"CLIP {(keepInside ? "内裁剪 (保留圈内)" : "外裁剪 (保留圈外)")}");
        EditEcho("① 方框光标点选闭合多段线（裁刀）→ ② 黄框光标点选三角网（Esc 取消）");
        var loop = await PickEntityInViewportAsync<PolylineEntity>(
            "① 点选闭合多段线（裁刀）…（Esc 取消）", p => p.Closed && p.Points.Count >= 3,
            "该处没有闭合多段线，请重选裁刀（Esc 取消）");
        if (loop == null) { EditEcho("闭合线裁剪：已取消"); return; }
        var m = await PickEntityInViewportAsync<MeshEntity>(
            "② 点选要裁剪的三角网…（Esc 取消）", null, "该处没有三角网，请重选（Esc 取消）");
        if (m == null) { EditEcho("闭合线裁剪：已取消"); return; }

        // 精确裁剪(原内核 clip_tin_by_polygon)：跨界三角沿裁刀边切开, 不再按质心整块取舍(那样边界锯齿、尖刺伸出裁刀)。
        if (!MeshPolygonClip.IsSingleValuedSurface(m.Verts, m.Tris))
        { EditEcho("检测到封闭实体或悬挑面（三角网法向有上有下），闭合线裁剪仅支持单值高程面，已中止", EchoLevel.Error); return; }
        var res = MeshPolygonClip.Clip(m.Verts, m.Tris, loop.Points, keepInside);
        if (res == null) { EditEcho("闭合线裁剪：裁刀多边形退化（顶点 <3 或自交/共线），无法剖分", EchoLevel.Error); return; }
        if (res.Value.Tris.Count == 0)
        { EditEcho(keepInside ? "裁剪结果为空（多段线未覆盖任何面）" : "裁剪结果为空（多段线覆盖了整个三角网）", EchoLevel.Warn); return; }
        int before = m.Tris.Count;
        ReplaceMesh(m, new MeshEntity(m.Name, res.Value.Verts, res.Value.Tris));
        EditEcho($"裁剪完成：「{m.Name}」三角 {before} → {res.Value.Tris.Count}（沿裁刀边精确切开，保留{(keepInside ? "圈内" : "圈外")}）", EchoLevel.Success);
    }

    /// <summary>
    /// 沿线分割三角网 (SPLITALONG)：① 点选多段线（切线）→ ② 点选三角网 → 切成左右两片
    /// （左片沿用原色，右片偏青），原网删除，可 Ctrl+Z 还原。
    /// </summary>
    private async Task EdMeshSplitAlongAsync()
    {
        // 场景里压根没有可选的线/网时先说清楚 —— 否则进了"点选切线"模式却什么都点不中,
        // 用起来就是"点了没反应"(点云选项卡的其余命令都在入口处报缺什么, 这条也照做)。
        bool anyLine = _scene.Entities.OfType<PolylineEntity>().Any(e => e.Visible && e.Points.Count >= 2 && _layers.IsSelectable(e.LayerName));
        bool anyMesh = _scene.Entities.OfType<MeshEntity>().Any(e => e.Visible && _layers.IsSelectable(e.LayerName));
        if (!anyMesh) { EditEcho("分割三角网：场景里没有三角网，请先用「2.5D TIN」把点云建成面。", EchoLevel.Error); return; }
        if (!anyLine) { EditEcho("分割三角网：场景里没有可作切线的多段线，请先画一条穿过三角网的线。", EchoLevel.Error); return; }
        EditEcho("SPLITALONG (沿线分割三角网)：请按提示 ① 选切线（多段线） ② 选三角网…");
        var line = await PickEntityInViewportAsync<PolylineEntity>(
            "① 点选切线（多段线 / 直线段）…（Esc 取消）", p => p.Points.Count >= 2,
            "该处没有多段线，请重选切线（Esc 取消）");
        if (line == null) { EditEcho("沿线分割三角网：已取消"); return; }
        var m = await PickEntityInViewportAsync<MeshEntity>(
            "② 点选要分割的三角网…（Esc 取消）", null, "该处没有三角网，请重选（Esc 取消）");
        if (m == null) { EditEcho("沿线分割三角网：已取消"); return; }

        // 严格沿多段线 XY 投影逐段切（原内核 splitByPolylineVertical）：跨线三角沿折线切开、子三角按最近段定左右。
        // 早前只按首末两点的竖直面切，折线一拐就不再贴着线走。闭合多段线补上闭合段，作边界用时左/右即圈内/圈外。
        var cut = new List<(double x, double y)>(line.Points);
        if (line.Closed && cut.Count >= 3 && (cut[0].x != cut[^1].x || cut[0].y != cut[^1].y)) cut.Add(cut[0]);
        EditEcho($"沿线分割三角网：「{m.Name}」{m.TriangleCount} 三角 · 切线 {cut.Count - 1} 段，切分中…");
        var res = await Task.Run(() => MeshPolylineSplit.Split(m.Verts, m.Tris, cut, 1e-6));
        if (!res.Success) { EditEcho($"沿线分割失败：{res.Error}", EchoLevel.Error); return; }
        if (!res.HasLeft || !res.HasRight)
        { EditEcho("分割失败：分割线未穿过该三角网（整张网在切线一侧），请重选", EchoLevel.Error); return; }
        BeginChange();
        _scene.Remove(m); _selected.Remove(m); _surfGrids.Remove(m);
        var a = new MeshEntity(NewMeshName(m.Name + "-左"), res.LeftVerts, res.LeftTris); a.CopyStyleFrom(m);
        var b = new MeshEntity(NewMeshName(m.Name + "-右"), res.RightVerts, res.RightTris); b.CopyStyleFrom(m);
        b.Cg = Math.Min(1, b.Cg + 0.15f); b.Cb = Math.Min(1, b.Cb + 0.15f);
        _scene.Add(a); _scene.Add(b); RefreshScene(); SelectEntities(new SceneEntity[] { a, b });
        string cap = res.CapLoops > 0 ? $" · 闭合体切口封盖 {res.CapLoops} 环" : "";
        EditEcho($"分割完成：「{m.Name}」→ 左 {res.LeftTris.Count} 三角 /「{a.Name}」、右 {res.RightTris.Count} 三角 /「{b.Name}」"
               + $"（沿线切开 {res.CutFaces} 三角{cap}，可 Ctrl+Z 还原）", EchoLevel.Success);
    }

    /// <summary>面交线 (INTERSECT)：依次点选两个三角网 → 逐三角求交 + 连通拼接 → 交线落「交线」图层。</summary>
    private async Task EdMeshIntersectAsync()
    {
        EditEcho("INTERSECT (面交线)：请在视口中依次点选两个三角网…");
        var m1 = await PickEntityInViewportAsync<MeshEntity>(
            "① 点选第一个三角网…（Esc 取消）", null, "该处没有三角网，请重选（Esc 取消）");
        if (m1 == null) { EditEcho("面交线：已取消"); return; }
        var m2 = await PickEntityInViewportAsync<MeshEntity>(
            "② 点选第二个三角网…（Esc 取消）", e => !ReferenceEquals(e, m1),
            "该处没有另一个三角网，请重选（Esc 取消）");
        if (m2 == null) { EditEcho("面交线：已取消"); return; }

        var segs = MeshIntersect.IntersectionSegments(m1.Verts, m1.Tris, m2.Verts, m2.Tris);
        if (segs.Count == 0) { EditEcho($"「{m1.Name}」与「{m2.Name}」不相交", EchoLevel.Warn); return; }
        var b = m1.Bounds;
        double tol = Math.Max(Math.Max(b.maxX - b.minX, b.maxY - b.minY) * 1e-6, 1e-9);
        var zLookup = new Dictionary<(long, long), double>();
        foreach (var s in segs)
        {
            zLookup[((long)Math.Round(s.A.x / tol), (long)Math.Round(s.A.y / tol))] = s.A.z;
            zLookup[((long)Math.Round(s.B.x / tol), (long)Math.Round(s.B.y / tol))] = s.B.z;
        }
        var chains = Contour.LinkSegments(segs.Select(s => (s.A.x, s.A.y, s.B.x, s.B.y)).ToList(), tol);
        BeginChange();
        var layer = _layers.Get("交线") ?? _layers.EnsureImported("交线", 0.95f, 0.3f, 0.3f);
        var added = new List<SceneEntity>();
        foreach (var ch in chains)
        {
            if (ch.Count < 2) continue;
            var pts = ch.Select(p => (p.x, p.y,
                zLookup.TryGetValue(((long)Math.Round(p.x / tol), (long)Math.Round(p.y / tol)), out var z)
                    ? z : (MeshZ(m1, p.x, p.y) ?? 0))).ToList();
            var pl = Poly3(pts, false, 0.95f, 0.3f, 0.3f);
            pl.LayerName = layer.Name;
            _scene.Add(pl); added.Add(pl);
        }
        PopulateDrawingLayers(); RefreshScene(); SelectEntities(added);
        EditEcho($"面交线「{m1.Name}」∩「{m2.Name}」：{segs.Count} 段 → {added.Count} 条交线（已落「交线」图层）", EchoLevel.Success);
    }

    /// <summary>修复拓扑关系 (REPAIR)：参数面板取 6 个修复开关 + 容差 → 原位替换选中三角网。</summary>
    private async Task EdMeshRepairAsync()
    {
        var meshes = await SelectObjectsAsync<MeshEntity>("修复拓扑关系", "三角网");
        if (meshes.Count == 0) return;
        var dlg = await PromptDialog.AskAsync(this, "修复拓扑参数", new[]
        {
            new PromptDialog.Field("tolerance", "几何容差", "0.000001", null, "判定顶点重合/小特征的距离阈值（0 = 按包围盒自动）"),
            new PromptDialog.Field("removeDegenerate", "删除退化面", "是", null, null, false, null, true),
            new PromptDialog.Field("weldVertices", "焊接重复顶点", "是", null, null, false, null, true),
            new PromptDialog.Field("fillHoles", "填充小孔洞", "是", null, null, false, null, true),
            new PromptDialog.Field("removeIsolated", "删除孤立顶点", "是", null, null, false, null, true),
            new PromptDialog.Field("splitNonManifold", "拆分非流形边", "是", null, null, false, null, true),
            new PromptDialog.Field("flipInverted", "翻转方向不一致面", "否", null, "默认关闭，避免误判", false, null, true),
        });
        if (dlg == null) { EditEcho("修复拓扑：用户取消"); return; }
        var opt = new MeshRepair.Options
        {
            Tolerance = dlg.D("tolerance"),
            RemoveDegenerate = dlg.B("removeDegenerate"),
            WeldVertices = dlg.B("weldVertices"),
            FillHoles = dlg.B("fillHoles"),
            RemoveIsolated = dlg.B("removeIsolated"),
            SplitNonManifold = dlg.B("splitNonManifold"),
            FlipInverted = dlg.B("flipInverted"),
        };
        EditEcho($"REPAIR (修复拓扑) tolerance={opt.Tolerance:G}");
        foreach (var m in meshes.ToList())
        {
            EditEcho($"「{m.Name}」{m.TriangleCount} 三角，修复中…");
            var r = await Task.Run(() => MeshRepair.Repair(m.Verts, m.Tris, opt));   // 大网别占 UI 线程; 期间网格只读
            if (r.TotalChanges == 0) { EditEcho($"「{m.Name}」已经干净，无需修复", EchoLevel.Success); continue; }
            if (r.RemovedDegenerate > 0) EditEcho($"去退化面: {r.RemovedDegenerate}");
            if (r.WeldedVertices > 0) EditEcho($"焊接顶点: {r.WeldedVertices}");
            if (r.FilledHoles > 0) EditEcho($"填充孔洞: {r.FilledHoles} 个 ({r.FilledFaces} 面)");
            if (r.RemovedIsolated > 0) EditEcho($"删除孤立顶点: {r.RemovedIsolated}");
            if (r.FlippedFaces > 0) EditEcho($"翻转面: {r.FlippedFaces}");
            if (r.SplitNonManifoldEdges > 0) EditEcho($"拆分非流形边: {r.SplitNonManifoldEdges}");
            ReplaceMesh(m, new MeshEntity(m.Name, r.Verts, r.Tris));
            EditEcho($"「{m.Name}」修复完成：最终 {r.Verts.Count} 顶点 / {r.Tris.Count} 面 · 开放边 {r.BoundaryBefore} → {r.BoundaryAfter}", EchoLevel.Success);
        }
    }

    /// <summary>
    /// 删除三角面 (DELFACES)：① 点选三角网 → ② 点选 / 拖框选要删的三角面（再点同一面 = 取消选中；左→右窗口 / 右→左交叉框选一次
    /// 框进多个面, Shift+拖框 = 从已选中移除）→ ③ 右键 / 回车一起删除（Esc 放弃，全程可 Ctrl+Z 还原）。忠实原版 DeleteMeshFacesCommandState
    /// （原版只有逐个点选；拖框多选是用户要求加的, 与 Kylin 空闲态框选同一手势与颜色）：
    /// <list type="bullet">
    /// <item>已选面黄色描边(原版 RefreshDeleteFacesOverlay) + 红色盖面(Kylin 惯例, 线框/着色档都看得清)；</item>
    /// <item>光标压到哪个三角面就亮哪个面(黄色盖面 + 描边; 压在已选面上转橙色, 提示"点击=取消")，浮标里跟着报
    ///   面号 / 面积 / 点击效果；命令行提示标签实时带「已选 N 面」(原版 BuildViewState 的 JigPrompt)；</item>
    /// <item>3D 视图按屏幕点做深度拾取(原版走射线求交)，2D 俯视按 XY 取最高的那层 —— 早前只有 XY 投影一条路,
    ///   3D 里点到的世界点是 Z=0 反投影, 与模型不在一个高程, 逐面点选在 3D 视图里根本点不中。</item>
    /// </list>
    /// </summary>
    private async Task EdDeleteMeshFacesAsync()
    {
        EditEcho("DELFACES (删除三角面)：请按提示 ① 选三角网 ② 点选要删除的面 ③ 右键 / 回车确认");
        var m = await PickEntityInViewportAsync<MeshEntity>(
            "① 点选要编辑的三角网…（Esc 取消）", null, "删除三角面：该处没有三角网，请重选（Esc 取消）");
        if (m == null) { EditEcho("删除三角面：已取消"); return; }
        if (m.Tris.Count == 0) { EditEcho($"删除三角面：「{m.Name}」三角网几何为空", EchoLevel.Warn); return; }

        // 选中态的青色盖面撤掉, 逐面点选要看得见网面本身与逐个三角(青色边线留着, 便于认三角)
        Viewport.SetHighlightFaces(null);
        var picker = new MeshFacePick(m);
        var marked = new HashSet<int>();
        int hover = -2;   // -1 = 不在网上; -2 = 未算过(强制下一次悬停重画)
        bool cancelled = false;
        string Prompt() => $"② 点选 / 拖框选要删除的三角面（再点同一面=取消；Shift+拖框=移除；右键/回车=确认；Esc=取消）— 已选 {marked.Count} 面";
        _pickHover = (sp, w) =>
        {
            int ti = FaceAtPointer(picker, sp, w);
            if (ti == hover) return;
            hover = ti;
            RefreshDelFacesOverlay(picker, marked, hover);
            _pickHoverInfo = DelFacesHoverInfo(picker, marked, hover);
            RefreshPickTip(sp);   // 说明随悬停即时换(指针不动、只是点了一下也要换)
        };
        // 拖框多选：框内的面一次全加进来(Shift = 全移出去); 拾取仍挂着, 松开后接着点/框/确认
        _pickBox = (a, b, crossing, remove) =>
        {
            var hits = FacesInBox(picker, a, b, crossing);
            string mode = crossing ? "交叉" : "窗口";
            if (hits.Count == 0) EditEcho($"框选（{mode}）：框内没有三角面 — 共 {marked.Count} 面待删除", EchoLevel.Warn);
            else
            {
                int changed = 0;
                if (remove) { foreach (var t in hits) if (marked.Remove(t)) changed++; }
                else { foreach (var t in hits) if (marked.Add(t)) changed++; }
                double area = 0; foreach (var t in hits) area += picker.Area(t);
                EditEcho(remove
                    ? $"框选（{mode}）移除 {changed} 面（框内 {hits.Count} 面）— 共 {marked.Count} 面待删除"
                    : $"框选（{mode}）加入 {changed} 面（框内 {hits.Count} 面，{area:0.##} m²）— 共 {marked.Count} 面待删除");
            }
            hover = -2;
            _pickHover!(b, Viewport.ScreenToWorld(b.X, b.Y));   // 按新选集重画叠层 + 悬停说明(光标停在松开处)
            SetPickPrompt(Prompt(), quiet: true);                // 命令行标签上的「已选 N 面」跟着变
        };
        try
        {
            EditEcho($"三角网「{m.Name}」已确认（{m.Tris.Count} 面）。点选或拖框选要删除的三角面：再点同一面 = 取消选中；左→右窗口 / 右→左交叉框选；Shift+拖框 = 移除；右键 / 回车 = 一起删除；Esc = 取消");
            bool first = true;
            while (true)
            {
                // 每圈先按光标当前位置算一次悬停(点选状态变了, 同一面的颜色与说明也要换): 刚点完鼠标还压在那个面上,
                // 不等它动就把"已选中，点击取消"亮出来; 选完三角网那一下也一样, 光标就在网上
                hover = -2;
                _pickHover(_lastPointer, Viewport.ScreenToWorld(_lastPointer.X, _lastPointer.Y));
                var (kind, x, y) = await PickPointOrConfirmAsync(Prompt(), true,
                    Controls.CadGlViewport.CursorMode.MeshPickBox, quiet: !first);   // 逐面点选 = 黄框 + 十字; 提示只回显一次, 之后只换标签/浮标
                first = false;
                if (kind == PickKind.Cancelled) { cancelled = true; break; }
                if (kind == PickKind.Confirmed) break;
                var sp = double.IsNaN(_lastPickScreen.sx) ? (Avalonia.Point?)null : new Avalonia.Point(_lastPickScreen.sx, _lastPickScreen.sy);
                int ti = FaceAtPointer(picker, sp, (x, y));
                if (ti < 0) { EditEcho("删除三角面：未命中三角面，请在三角网上点击（右键 / 回车确认，Esc 取消）", EchoLevel.Warn); continue; }
                double area = picker.Area(ti);
                if (marked.Add(ti)) EditEcho($"已选中三角面 #{ti}（面积 {area:0.##} m²）— 共 {marked.Count} 面待删除");
                else { marked.Remove(ti); EditEcho($"取消选中三角面 #{ti} — 共 {marked.Count} 面待删除"); }
            }
        }
        finally
        {
            _pickHover = null; _pickHoverInfo = null; _pickBox = null; _pickDragging = false;
            Viewport.SetFacePickOverlay(null, null);
            HideDragTip();
        }
        if (cancelled) { HighlightSelection(); EditEcho("删除三角面：已放弃（未改动三角网）", EchoLevel.Warn); return; }
        if (marked.Count == 0) { HighlightSelection(); EditEcho("未选中任何三角面，取消删除", EchoLevel.Warn); return; }
        int before = m.Tris.Count, beforeV = m.Verts.Count;
        var (nv, nt) = PolylineClipper.RemoveTriangles(m.Verts, m.Tris, marked.ToList());
        ReplaceMesh(m, new MeshEntity(m.Name, nv, nt));
        EditEcho($"删除三角面完成「{m.Name}」：删除 {marked.Count} 面，{beforeV} 顶点 / {before} 面 → {nv.Count} 顶点 / {nt.Count} 面"
               + $"{(beforeV - nv.Count > 0 ? $"（清掉 {beforeV - nv.Count} 个孤立顶点）" : "")}。Ctrl+Z 可还原。", EchoLevel.Success);
    }

    /// <summary>
    /// 指针(屏幕点 + Z=0 反投影世界点) → 三角面索引。3D 视图按屏幕点做深度拾取(同 PickEntityInViewportAsync / PickAt 的口径),
    /// 2D 俯视按 XY 取该处最高的那层；没命中 -1。
    /// </summary>
    private int FaceAtPointer(MeshFacePick picker, Avalonia.Point? screen, (double x, double y)? world)
    {
        if (!Viewport.Is2DView)
            return screen == null ? -1 : picker.FindAtScreen(screen.Value.X, screen.Value.Y, Viewport.ViewStamp, Viewport.WorldToScreenDepthProjector());
        return world == null ? -1 : picker.FindAtXY(world.Value.x, world.Value.y);
    }

    /// <summary>
    /// 拖框 → 三角面索引集(与 FaceAtPointer 同口径)：3D 按屏幕矩形对投影判(被挡住的背面也算, 同 CAD 窗选)，2D 按世界 XY 矩形判。
    /// 左→右窗口(全含才选) / 右→左交叉(碰到即选)由调用处按拖动方向给。
    /// </summary>
    private List<int> FacesInBox(MeshFacePick picker, Avalonia.Point a, Avalonia.Point b, bool crossing)
    {
        if (!Viewport.Is2DView)
            return picker.FacesInScreenRect(a.X, a.Y, b.X, b.Y, crossing, Viewport.ViewStamp, Viewport.WorldToScreenDepthProjector());
        var wa = Viewport.ScreenToWorld(a.X, a.Y); var wb = Viewport.ScreenToWorld(b.X, b.Y);
        if (wa == null || wb == null) return new List<int>();
        return picker.FacesInRect(wa.Value.x, wa.Value.y, wb.Value.x, wb.Value.y, crossing);
    }

    /// <summary>
    /// 悬停面的实时说明(光标旁浮标)：面号 / 面积 / 点击效果 ＋ 已选数与下一步手势(原版 JigPrompt 里的「已选 N 面」)；
    /// 不在网上则提示到网上点。
    /// </summary>
    private static string DelFacesHoverInfo(MeshFacePick picker, HashSet<int> marked, int hover)
    {
        string tail = marked.Count == 0 ? "尚未选面 · 拖框可多选 · Esc 放弃" : $"已选 {marked.Count} 面 · 右键/回车一起删除";
        if (hover < 0) return $"光标不在三角网上 ｜ {tail}";
        bool sel = marked.Contains(hover);
        return $"▲ 面 #{hover} · {picker.Area(hover):0.##} m² · {(sel ? "已选中，点击取消" : "点击选中")} ｜ {tail}";
    }

    /// <summary>
    /// 删除三角面的可视反馈(原版 RefreshDeleteFacesOverlay 的等价, 多一层盖面)：
    /// 已选面 = 红色盖面 + 黄色描边；悬停面 = 黄色盖面 + 黄描边(已选面上悬停 → 橙色, 提示点击会取消)。
    /// 几何按 SetHighlight 约定先减渲染原点再转 float(大坐标矿区不减就飞出视野)，Z 加 Elevation 贴在网面上。
    /// </summary>
    private void RefreshDelFacesOverlay(MeshFacePick picker, IReadOnlyCollection<int> marked, int hover)
    {
        var m = picker.Mesh;
        if (marked.Count == 0 && hover < 0) { Viewport.SetFacePickOverlay(null, null); return; }
        double ox = RenderOrigin.X, oy = RenderOrigin.Y, elev = m.Elevation;
        var fills = new List<float>((marked.Count + 1) * 18);
        var lines = new List<float>((marked.Count + 1) * 36);
        const float yr = 1.0f, yg = 0.92f, yb = 0.0f;   // 原版 DELFACES 描边黄
        void Tri(int ti, float fr, float fg, float fb)
        {
            if (ti < 0 || ti >= m.Tris.Count) return;
            var (a, b, c) = m.Tris[ti];
            if (a >= m.Verts.Count || b >= m.Verts.Count || c >= m.Verts.Count) return;
            var p = m.Verts[a]; var q = m.Verts[b]; var r = m.Verts[c];
            void F((double x, double y, double z) v)
            { fills.Add((float)(v.x - ox)); fills.Add((float)(v.y - oy)); fills.Add((float)(v.z + elev)); fills.Add(fr); fills.Add(fg); fills.Add(fb); }
            void L((double x, double y, double z) v)
            { lines.Add((float)(v.x - ox)); lines.Add((float)(v.y - oy)); lines.Add((float)(v.z + elev)); lines.Add(yr); lines.Add(yg); lines.Add(yb); }
            F(p); F(q); F(r);
            L(p); L(q); L(q); L(r); L(r); L(p);
        }
        foreach (var ti in marked) if (ti != hover) Tri(ti, 0.95f, 0.25f, 0.25f);   // 待删面：红
        if (hover >= 0)
        {
            if (marked.Contains(hover)) Tri(hover, 1.0f, 0.55f, 0.15f);   // 压在已选面上：橙(点击将取消)
            else Tri(hover, 1.0f, 0.85f, 0.25f);                          // 未选面：黄(点击将选中)
        }
        Viewport.SetFacePickOverlay(fills.ToArray(), lines.ToArray());
    }

    // ══════════════════════════════ 公共小件 ══════════════════════════════

    /// <summary>只需一个容差参数的算子前置对话框（原版 PromptTolerance 的等价）。</summary>
    private async Task<double?> AskToleranceAsync(string title, double @default)
    {
        var dlg = await PromptDialog.AskAsync(this, title, new[]
        { new PromptDialog.Field("tolerance", "几何容差", @default.ToString("G", Inv), null, "判定接近/重合的距离阈值") });
        if (dlg == null) { EditEcho($"{title}：用户取消"); return null; }
        return Math.Max(dlg.D("tolerance"), 0);
    }
}
