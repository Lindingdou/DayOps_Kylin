// 忠实移植自原 PitMine3D Modules/RoadLib/Editing/RoadEditSession.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>草稿图相对基准图的差异（diff 预览 + 图例用）。</summary>
public sealed class RoadGraphDiff
{
    public List<RoadEdge> Added { get; } = new();          // 草稿有、基准无
    public List<RoadEdge> Removed { get; } = new();        // 基准有、草稿无（取基准几何显示）
    public List<RoadEdge> StatusChanged { get; } = new();  // 两边都有、状态不同
    public List<RoadEdge> Modified { get; } = new();       // 两边都有、中线变了（延长/缩短/改线）
    /// <summary>两边都有、人工改判的线路类型变了（R-T7）。少了这一档，「只改类型」的编辑会 diff 出空表、预览一片空白。</summary>
    public List<RoadEdge> ClassChanged { get; } = new();
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && StatusChanged.Count == 0
                           && Modified.Count == 0 && ClassChanged.Count == 0;
}

/// <summary>
/// 路网编辑会话（「暂存式编辑 + 提交闸门」核心引擎）。
///
/// 设计要点：
///   · 持有一张「活草稿图 Draft」（进入时克隆当前网），每条变更直接改草稿——
///     不走「基准 + 重放操作日志」，规避 SplitEdge 改 id 后续操作断链的坑；
///   · 变更日志 Journal 仅供「变更清单 UI」与 diff；
///   · 撤销靠每步前克隆草稿压栈恢复（节点量级 ≤ 数千，克隆开销可忽略）；
///   · 提交前当前网（外部 _sessionGraph）保持不动，寻径/运距仍用它——
///     只有「更新路网」拿 Draft 去锁定（当前同名覆盖 / 增量新命名）。
/// 手动编辑与演化对比都把 <see cref="RoadChange"/> 喂给 <see cref="Apply"/>，共用此通道。
/// </summary>
public sealed class RoadEditSession
{
    /// <summary>进入编辑时当前网的克隆（diff 基准 + 留底）。</summary>
    public RoadGraph BaseSnapshot { get; }

    /// <summary>来源存档名（决定「当前路网更新」同名覆盖谁；无则仅会话）。</summary>
    public string? BaseArchiveName { get; }

    /// <summary>活草稿图（实时反映已应用变更；提交时即结果图）。</summary>
    public RoadGraph Draft { get; private set; }

    /// <summary>
    /// 进编辑那一刻的基准拓扑（<b>结构轴</b>：所有边都算）。基准图不变，故只算一次。
    /// </summary>
    public RoadTopologyReport BaseTopology { get; }

    /// <summary>
    /// 同上，但走<b>可通行轴</b>（检修/封闭的边按不存在算）。
    /// 「边状态」改的只是这一轴 —— 封一条边在结构上什么都没变，边还在、度数照旧。
    /// </summary>
    public RoadTopologyReport BasePassableTopology { get; }

    private readonly List<RoadChange> _journal = new();
    private readonly Stack<RoadGraph> _undo = new();
    private int _seq;

    public IReadOnlyList<RoadChange> Journal => _journal;
    public bool HasChanges => _journal.Count > 0;
    public bool CanUndo => _undo.Count > 0;

    public RoadEditSession(RoadGraph current, string? baseArchiveName = null)
    {
        BaseSnapshot = current.Clone();
        BaseArchiveName = baseArchiveName;
        Draft = current.Clone();
        BaseTopology = RoadTopology.Analyze(BaseSnapshot);
        BasePassableTopology = RoadTopology.Analyze(BaseSnapshot, passableOnly: true);
    }

    /// <summary>草稿的当前拓扑。<b>每次现算</b> —— 草稿每步都在变，缓存必然过期。</summary>
    public RoadTopologyReport DraftTopology(bool passableOnly = false)
        => RoadTopology.Analyze(Draft, passableOnly);

    /// <summary>
    /// 本次编辑<b>累计</b>的拓扑变化（相对进编辑时的基准），没动返回空串。
    ///
    /// 对基准算累计、而不是对上一步算增量：撤销 / 清空之后这个数会自己走回去，
    /// 不需要另存一份「上一步的拓扑」，也就没有那份缓存跟草稿对不上的故障面。
    /// 这是「增量增删边」「边状态」当场自证的唯一途径 —— 加一条边到底把两片接上了没有
    /// （连通片 −1）、封一条边有没有把干线打成孤立段（可通行轴上孤立段 +N），
    /// 光看 diff 预览的绿线红叉是看不出来的。
    /// </summary>
    public string TopologyDelta(bool passableOnly = false)
        => RoadTopology.DescribeDelta(
            passableOnly ? BasePassableTopology : BaseTopology,
            DraftTopology(passableOnly));

    /// <summary>分配一个在草稿图中唯一的边 id（杜绝与基准 id 撞）。</summary>
    public string NewEdgeId()
    {
        string id;
        do { id = $"E{++_seq}"; } while (Draft.GetEdge(id) != null);
        return id;
    }

    /// <summary>分配一个在草稿图中唯一的节点 id。</summary>
    public string NewNodeId()
    {
        string id;
        do { id = $"N{++_seq}"; } while (Draft.GetNode(id) != null);
        return id;
    }

    /// <summary>应用一条变更（统一通道：压撤销栈 → 改草稿 → 记日志）。</summary>
    public void Apply(RoadChange c)
    {
        _undo.Push(Draft.Clone());
        c.Apply(Draft);
        _journal.Add(c);
    }

    /// <summary>撤销上一步（恢复草稿 + 丢最后一条日志）。</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Draft = _undo.Pop();
        if (_journal.Count > 0) _journal.RemoveAt(_journal.Count - 1);
        return true;
    }

    /// <summary>清空所有变更，草稿回到基准。</summary>
    public void Clear()
    {
        Draft = BaseSnapshot.Clone();
        _journal.Clear();
        _undo.Clear();
    }

    /// <summary>草稿 vs 基准的差异（按边 id 比对：新增/删除/改状态/改线）。</summary>
    public RoadGraphDiff ComputeDiff()
    {
        var diff = new RoadGraphDiff();
        var baseById = new Dictionary<string, RoadEdge>();
        foreach (var e in BaseSnapshot.Edges) baseById[e.Id] = e;
        var draftIds = new HashSet<string>();

        foreach (var e in Draft.Edges)
        {
            draftIds.Add(e.Id);
            if (!baseById.TryGetValue(e.Id, out var b)) { diff.Added.Add(e); continue; }
            // 三档各自独立判（RE11）。原来是 else-if 串：同一条边"既改了状态又改了线"时
            // 只报改状态，改线那一档被吃掉——diff 预览里那条线不会变橙，人以为线没动。
            if (e.Status != b.Status) diff.StatusChanged.Add(e);
            if (CenterlineChanged(b, e)) diff.Modified.Add(e);
            if (e.RoadClass != b.RoadClass) diff.ClassChanged.Add(e);
        }
        foreach (var b in BaseSnapshot.Edges)
            if (!draftIds.Contains(b.Id)) diff.Removed.Add(b);

        return diff;
    }

    /// <summary>
    /// 中线是否实质改变（RE11）：<b>逐坐标比</b>。
    ///
    /// 原来比的是"点数 + 总里程"，于是<b>整体横移判成没变</b>——把一条边平移 10 m，
    /// 点数不变、总长一个字节都不差，diff 出空表、预览一片空白（台架
    /// <c>RoadEvolutionDiagnosticTests.UC_编辑提交侧的变化识别</c> 钉住）。
    /// 改线本来就是"点挪了"，判据就该看点。与
    /// <see cref="RoadLib.Network.CenterlineLayerDiff"/> 落地那一侧同口径，两处不再各说各话。
    /// </summary>
    private static bool CenterlineChanged(RoadEdge a, RoadEdge b)
    {
        if (a.Centerline.Count != b.Centerline.Count) return true;
        for (int i = 0; i < a.Centerline.Count; i++)
        {
            var p = a.Centerline[i];
            var q = b.Centerline[i];
            if (!p.X.Equals(q.X) || !p.Y.Equals(q.Y) || !p.Z.Equals(q.Z)) return true;
        }
        return false;
    }
}
