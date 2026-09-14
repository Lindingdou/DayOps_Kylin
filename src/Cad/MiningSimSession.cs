using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 把 <see cref="MiningSim"/> 的数值核接到 Kylin 的块体模型上（§三二五）。
///
/// 原版是从桌面「块体」目录读三个固定名字的 .blk（<c>2036采场块体.blk</c> / <c>2036内排土场块体.blk</c> /
/// <c>2036外排土场块体.blk</c>）。本移植改为**在已加载的块体模型里挑**：Kylin 的块体本来就是
/// <see cref="BlockModelMeta"/> 列表，采场/外排/内排三个下拉各选一个即可 —— 既不绑死文件名，
/// 也顺带支持 PMB / CSV 等 Kylin 支持的其它来源（.blk 仍可先「导入块体」再来选）。
///
/// 演示期间**只改 <see cref="BlockModelMeta.SimHidden"/>**（一份不入存档、不进撤销的临时隐藏集），
/// 绝不碰 <c>DeletedIds</c> —— 否则演示一跑就把用户的模型改了。
/// </summary>
public sealed class MiningSimSession
{
    /// <summary>一个参与演示的模型 + 它的块 → 引擎 Cell 的对应关系。</summary>
    private sealed class Bound
    {
        public BlockModelMeta Meta = null!;
        public List<MiningSim.Cell> Cells = new();
        /// <summary>Cells[i] 对应 Meta.Blocks 的下标（跳过了已删/被筛掉的块）。</summary>
        public List<int> BlockIndex = new();
    }

    private Bound? _pit, _ext, _int;
    private MiningSim.Frame _frame = MiningSim.BuildFrame(Array.Empty<MiningSim.Cell>());
    private List<MiningSim.PitStrip> _strips = new();
    private List<MiningSim.DumpLayer> _dumps = new();

    public bool Ready { get; private set; }
    public bool HasExternal => _ext != null;
    public bool HasInternal => _int != null;
    public double CoalTotal { get; private set; }
    public double WasteTotal { get; private set; }
    public double ExtTotal { get; private set; }
    public double IntTotal { get; private set; }
    /// <summary>推进方向的人话描述（同原 <c>AdvanceLabel</c>）。</summary>
    public string AdvanceLabel { get; private set; } = "";
    public string StatusLabel { get; private set; } = "";

    /// <summary>
    /// 判煤：分类属性的类别名里含「煤 / SEAM / COAL」的码即为煤（原 <c>BuildPit</c> 的口径）。
    /// 找不到分类列时全按岩处理（读数里"采煤"恒 0，比瞎猜成煤诚实）。
    /// </summary>
    public static HashSet<int> CoalCodes(BlockModelMeta m, out string? attrName)
    {
        attrName = null;
        var codes = new HashSet<int>();
        var col = m.PropertySchema.FirstOrDefault(c => c.IsCategorical && c.CategoryLabels != null && c.CategoryLabels.Count > 0);
        if (col == null) return codes;
        attrName = col.Name;
        for (int i = 0; i < col.CategoryLabels!.Count; i++)
        {
            var s = col.CategoryLabels[i];
            if (string.IsNullOrEmpty(s)) continue;
            if (s.Contains('煤') || s.ToUpperInvariant().Contains("SEAM") || s.ToUpperInvariant().Contains("COAL"))
                codes.Add(i);
        }
        return codes;
    }

    private static Bound Bind(BlockModelMeta m, bool classifyCoal)
    {
        var b = new Bound { Meta = m };
        HashSet<int>? coal = null;
        double[]? codes = null;
        if (classifyCoal)
        {
            coal = CoalCodes(m, out string? attr);
            if (attr != null && coal.Count > 0) m.Attrs.TryGetValue(attr, out codes);
        }
        for (int i = 0; i < m.Blocks.Count; i++)
        {
            if (m.DeletedIds.Contains(i)) continue;   // 已删的块不参与演示
            var blk = m.Blocks[i];
            double k = m.CellScale(blk);
            bool isCoal = codes != null && i < codes.Length && coal!.Contains((int)Math.Round(codes[i]));
            b.Cells.Add(new MiningSim.Cell(blk.X, blk.Y, blk.Z, k * m.Sx, k * m.Sy, k * m.Sz, isCoal));
            b.BlockIndex.Add(i);
        }
        return b;
    }

    /// <summary>
    /// 装配一次演示。<paramref name="pit"/> 必给；内/外排可缺（缺了读数显「—（未选）」，同原版缺文件的处理）。
    /// 返回 false 时 <paramref name="err"/> 是给用户看的原因。
    /// </summary>
    public bool Setup(BlockModelMeta? pit, BlockModelMeta? external, BlockModelMeta? internalDump, out string err)
    {
        err = "";
        Reset();
        if (pit == null) { err = "请先选一个「采场」块体模型。"; return false; }
        _pit = Bind(pit, classifyCoal: true);
        if (_pit.Cells.Count == 0) { err = $"采场模型「{pit.Name}」没有可用的块（可能全被删除或筛掉了）。"; _pit = null; return false; }

        _ext = external != null ? Bind(external, classifyCoal: false) : null;
        if (_ext is { Cells.Count: 0 }) _ext = null;
        _int = internalDump != null ? Bind(internalDump, classifyCoal: false) : null;
        if (_int is { Cells.Count: 0 }) _int = null;

        // 起推方向由内排定；没有内排就退而用外排（同原版 `inn ?? ext`）
        var dirRef = _int?.Cells ?? _ext?.Cells;
        _frame = MiningSim.BuildFrame(_pit.Cells, dirRef);
        _strips = MiningSim.BuildPit(_pit.Cells, _frame, out double coal, out double waste);
        CoalTotal = coal; WasteTotal = waste;

        _dumps = new List<MiningSim.DumpLayer>();
        ExtTotal = IntTotal = 0;
        if (_ext != null) { _dumps.AddRange(MiningSim.BuildDump(_ext.Cells, _frame, 0, out double et)); ExtTotal = et; }
        if (_int != null) { _dumps.AddRange(MiningSim.BuildDump(_int.Cells, _frame, 1, out double it)); IntTotal = it; }

        AdvanceLabel = DescribeAdvance(_frame);
        StatusLabel = $"采场 {MiningSim.PanelCount} 标段 / 采煤 {CoalTotal / 1e6:0.0}·剥离 {WasteTotal / 1e6:0.0} Mm³"
                    + (HasExternal ? $"；外排 {ExtTotal / 1e6:0.0}" : "；外排未选")
                    + (HasInternal ? $"；内排 {IntTotal / 1e6:0.0} Mm³" : "；内排未选");
        Ready = true;
        return true;
    }

    /// <summary>推进方向的人话描述：把单位向量说成方位角与东南西北（同原 <c>DescribeAdvance</c> 的用意）。</summary>
    public static string DescribeAdvance(MiningSim.Frame f)
    {
        // 方位角: 正北为 0°, 顺时针增
        double az = Math.Atan2(f.Ux, f.Uy) * 180.0 / Math.PI;
        if (!f.Forward) az += 180;
        az = (az % 360 + 360) % 360;
        string[] names = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
        string dir = names[(int)Math.Round(az / 45.0) % 8];
        return $"沿方位角 {az:0}°（向{dir}）推进 · 共 {MiningSim.PitStripTarget} 条采掘带";
    }

    /// <summary>
    /// 把进度套到各模型上：改各自的 <c>SimHidden</c>，返回读数。
    /// 调用方拿到 true 后要重渲（<c>BlockModelStore.RefreshDisplay</c>）。
    /// </summary>
    public MiningSim.Readout Step(double p, bool reverse = false)
    {
        var r = MiningSim.Step(p, _strips, _dumps, reverse);
        if (_pit == null) return r;

        // 采场：已采(不可见)的桶 → 藏起它名下的块
        _pit.Meta.SimHidden.Clear();
        foreach (var s in _strips)
        {
            if (s.Visible) continue;
            foreach (int ci in s.CellIndices) _pit.Meta.SimHidden.Add(_pit.BlockIndex[ci]);
        }

        // 排土：反过来 —— 还没堆到的层才藏
        if (_ext != null) HideDump(_ext, 0);
        if (_int != null) HideDump(_int, 1);
        return r;
    }

    private void HideDump(Bound b, int type)
    {
        b.Meta.SimHidden.Clear();
        foreach (var d in _dumps)
        {
            if (d.Type != type || d.Visible) continue;
            foreach (int ci in d.CellIndices) b.Meta.SimHidden.Add(b.BlockIndex[ci]);
        }
    }

    /// <summary>收起演示：清掉所有临时隐藏，模型回到原样。</summary>
    public void Reset()
    {
        _pit?.Meta.ClearSimHidden();
        _ext?.Meta.ClearSimHidden();
        _int?.Meta.ClearSimHidden();
        _pit = _ext = _int = null;
        _strips = new List<MiningSim.PitStrip>();
        _dumps = new List<MiningSim.DumpLayer>();
        CoalTotal = WasteTotal = ExtTotal = IntTotal = 0;
        AdvanceLabel = StatusLabel = "";
        Ready = false;
    }

    /// <summary>参与演示的模型（供调用方重渲/缩放取范围）。</summary>
    public IEnumerable<BlockModelMeta> Models()
    {
        if (_pit != null) yield return _pit.Meta;
        if (_ext != null) yield return _ext.Meta;
        if (_int != null) yield return _int.Meta;
    }
}
