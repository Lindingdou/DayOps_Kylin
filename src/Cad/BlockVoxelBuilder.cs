using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 体素格网体积构建（忠实原 VoxelVolumeBuilder + ElevationBinner + VolumeByLevelReport）：
/// 若干封闭三角网在公共紧贴 AABB 上体素化，逐 cell 中心「在任一 mesh 内」(OR) 判定 → 保留位图 + 整体体积；
/// 开启次级退化时边界母块用 <see cref="AdaptiveVoxel.RefineCell"/> 递归细分为百分比子块（只进体积报表）；再按标高边界分桶报量。
/// 生成块体模型走原版"八叉树原生"路径：均匀占位位图 → <see cref="OctreeLeafBuilder"/> 叶块 → <see cref="ToBlockModel(Result, IReadOnlyList{OctreeLeaf}, string)"/>
/// （百分比子块那版 <see cref="ToBlockModel(Result, string)"/> 留给单测/报表对拍：薄体在母块间"两头中心都在外"时整列被丢，体积偏少两成）。
/// 内外判定默认奇偶射线 <see cref="MeshContainmentTester"/>，「高容错」且非闭合才换 <see cref="WindingNumberTester"/>。纯逻辑、可单测。
/// </summary>
public static class BlockVoxelBuilder
{
    /// <summary>体素化 cell 总数上限（原 MaxCellsCompute）。</summary>
    public const long MaxCellsCompute = 60_000_000;
    /// <summary>入场景渲染的块数上限（Kylin 视口为逐块 RectEntity，过多则卡顿）。</summary>
    public const long MaxRenderBlocks = 400_000;

    public sealed class MeshInput
    {
        /// <summary>默认判据：奇偶射线 + XY 分箱（水密体精确且快）。</summary>
        public required MeshContainmentTester Tester { get; init; }
        /// <summary>高容错判据：广义缠绕数（破洞/自交/未焊接才用；懒建缓存，见 <see cref="TesterFor"/>）。</summary>
        public double[] Verts { get; init; } = Array.Empty<double>();
        public int[] Tris { get; init; } = Array.Empty<int>();
        private WindingNumberTester? _gwn;
        /// <summary>按「高容错」开关取判据：忠实原版——只有开了高容错**且**网格非闭合才上 GWN。</summary>
        public IInsideTester TesterFor(bool highTolerance)
        {
            if (!highTolerance || Closed || Verts.Length < 9 || Tris.Length < 3) return Tester;
            return _gwn ??= new WindingNumberTester(Verts, Tris);
        }
        public bool Closed { get; init; }
        public string Name { get; init; } = "";
        public int VertexCount { get; init; }
        public int TriangleCount { get; init; }
        /// <summary>散度定理精确体积（供对比）。</summary>
        public double ExactVolume { get; init; }
    }

    /// <summary>从场景三角网建输入（一致朝向；闭合性由 MeshDiagnose 判。默认奇偶射线判据，高容错时才建 GWN）。</summary>
    public static MeshInput FromMesh(string name, IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        var mt = MeshOrient.MakeConsistent(verts, tris);
        var fv = new double[verts.Count * 3];
        for (int i = 0; i < verts.Count; i++) { fv[i * 3] = verts[i].x; fv[i * 3 + 1] = verts[i].y; fv[i * 3 + 2] = verts[i].z; }
        var ft = new int[mt.Count * 3];
        for (int i = 0; i < mt.Count; i++) { ft[i * 3] = mt[i].a; ft[i * 3 + 1] = mt[i].b; ft[i * 3 + 2] = mt[i].c; }
        var diag = MeshDiagnose.Analyze(verts, tris);
        return new MeshInput
        {
            Tester = new MeshContainmentTester(fv, ft), Verts = fv, Tris = ft, Closed = diag.IsClosed, Name = name,
            VertexCount = verts.Count, TriangleCount = tris.Count, ExactVolume = diag.IsClosed ? MeshMetrics.RobustVolume(verts, mt) : 0,
        };
    }

    public sealed class Result
    {
        public double Ox, Oy, Oz, Vx, Vy, Vz; public int Nx, Ny, Nz;
        public bool[] Keep = Array.Empty<bool>();
        public long KeepCount;
        public double CellVolume;
        public List<VoxelSubCell> SubCells = new();
        public HashSet<long> SubBlockedParents = new();
        public double SubCellVolume;
        public double TotalVolume => KeepCount * CellVolume + SubCellVolume;
        public long TotalCells => (long)Nx * Ny * Nz;
        public int MeshCount, OpenMeshCount;
        public double ExactVolume;
        public (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) UnionBounds;
    }

    public static (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)? UnionBounds(IReadOnlyList<MeshInput> meshes)
    {
        if (meshes == null || meshes.Count == 0) return null;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var m in meshes)
        {
            var t = m.Tester;
            minX = Math.Min(minX, t.MinX); minY = Math.Min(minY, t.MinY); minZ = Math.Min(minZ, t.MinZ);
            maxX = Math.Max(maxX, t.MaxX); maxY = Math.Max(maxY, t.MaxY); maxZ = Math.Max(maxZ, t.MaxZ);
        }
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>仅按 AABB+体素估算网格规模（原 EstimateGrid）。</summary>
    public static (int nx, int ny, int nz, long cells, (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) bounds)? EstimateGrid(
        IReadOnlyList<MeshInput> meshes, double vx, double vy, double vz)
    {
        var b = UnionBounds(meshes);
        if (b == null || vx <= 0 || vy <= 0 || vz <= 0) return null;
        var bb = b.Value;
        int nx = Math.Max(1, (int)Math.Ceiling((bb.maxX - bb.minX) / vx));
        int ny = Math.Max(1, (int)Math.Ceiling((bb.maxY - bb.minY) / vy));
        int nz = Math.Max(1, (int)Math.Ceiling((bb.maxZ - bb.minZ) / vz));
        return (nx, ny, nz, (long)nx * ny * nz, bb);
    }

    /// <summary>
    /// 体素化（原 Build）：depth≤0 均匀中心判定；depth&gt;0 边界母块递归细分（SampleN³ 子采样占比）。
    /// 失败返回 null + error。
    /// </summary>
    public static Result? Build(IReadOnlyList<MeshInput> meshes, double vx, double vy, double vz, out string error,
        int depth = 0, int sampleN = 4, long maxSubCells = 4_000_000, CancellationToken ct = default, bool highTolerance = false)
    {
        error = "";
        var est = EstimateGrid(meshes, vx, vy, vz);
        if (est == null) { error = "无有效封闭网格，或体素尺寸非法（须 > 0）。"; return null; }
        var (nx, ny, nz, cells, bb) = est.Value;
        if (cells > MaxCellsCompute) { error = $"网格 cell 数 {cells:N0} 超上限 {MaxCellsCompute:N0}，请增大体素尺寸。"; return null; }
        // 内外测试器选择（忠实原 VoxelVolumeBuilder）：默认奇偶射线；「高容错」只对**非闭合**网格换 GWN——
        // 水密体的奇偶射线本就精确，无需上昂贵的缠绕数。
        var testers = meshes.Select(m => m.TesterFor(highTolerance)).ToArray();
        bool InsideAny(double x, double y, double z)
        {
            for (int mi = 0; mi < testers.Length; mi++) if (testers[mi].IsInsideClosed(x, y, z)) return true;
            return false;
        }
        var r = new Result
        {
            Ox = bb.minX, Oy = bb.minY, Oz = bb.minZ, Vx = vx, Vy = vy, Vz = vz, Nx = nx, Ny = ny, Nz = nz,
            CellVolume = vx * vy * vz, MeshCount = meshes.Count, OpenMeshCount = meshes.Count(m => !m.Closed),
            ExactVolume = meshes.Sum(m => m.ExactVolume), UnionBounds = bb,
        };
        var keep = new bool[cells];
        var centerIn = new bool[cells];
        double CX(int i) => bb.minX + (i + 0.5) * vx; double CY(int j) => bb.minY + (j + 0.5) * vy; double CZ(int k) => bb.minZ + (k + 0.5) * vz;
        long nxy = (long)nx * ny;
        // ① 母块中心内外：按 Z 层并行（同原版 Parallel.For；各层写各自 cell 无竞争，测试器只读、线程安全）
        System.Threading.Tasks.Parallel.For(0, nz, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, k =>
        {
            double cz = CZ(k);
            for (int j = 0; j < ny; j++)
            {
                double cy = CY(j);
                long jBase = j * (long)nx + k * nxy;
                for (int i = 0; i < nx; i++)
                    if (InsideAny(CX(i), cy, cz)) centerIn[jBase + i] = true;
            }
        });
        bool NIn(int i, int j, int k) => (uint)i < (uint)nx && (uint)j < (uint)ny && (uint)k < (uint)nz && centerIn[i + j * (long)nx + k * nxy];
        long keepCount = 0; double subVol = 0;
        int maxDepth = Math.Max(0, depth); sampleN = Math.Max(1, sampleN);
        for (int k = 0; k < nz; k++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    long idx = i + j * (long)nx + k * nxy;
                    bool ci = centerIn[idx];
                    if (maxDepth <= 0) { if (ci) { keep[idx] = true; keepCount++; } continue; }
                    bool boundary = ci != NIn(i - 1, j, k) || ci != NIn(i + 1, j, k) || ci != NIn(i, j - 1, k) || ci != NIn(i, j + 1, k) || ci != NIn(i, j, k - 1) || ci != NIn(i, j, k + 1);
                    if (!boundary) { if (ci) { keep[idx] = true; keepCount++; } continue; }
                    int before = r.SubCells.Count;
                    AdaptiveVoxel.RefineCell(InsideAny, r.SubCells, CX(i), CY(j), CZ(k), vx, vy, vz, maxDepth, sampleN);
                    if (r.SubCells.Count > before)
                    {
                        r.SubBlockedParents.Add(idx);
                        for (int s = before; s < r.SubCells.Count; s++) { var sc = r.SubCells[s]; subVol += sc.Sx * sc.Sy * sc.Sz * sc.Percent; }
                    }
                    if (r.SubCells.Count > maxSubCells) { error = $"子块数超上限 {maxSubCells:N0}，请减小退化深度或增大体素尺寸。"; return null; }
                }
        }
        r.Keep = keep; r.KeepCount = keepCount; r.SubCellVolume = subVol;
        return r;
    }

    // ── 分标高报量（原 ElevationBinner）──

    public sealed class LevelRow { public double ZLow, ZHigh; public long Cells; public double Volume; }
    public sealed class ElevationReport
    {
        public List<LevelRow> Levels = new();
        public long OutOfRangeCells; public double OutOfRangeVolume; public double TotalVolume; public long TotalCells;
    }

    /// <summary>标高规整：排序 + 去重（容差 1e-6）。</summary>
    public static List<double> NormalizeLevels(IEnumerable<double> raw)
    {
        var list = new List<double>();
        foreach (var z in raw.OrderBy(z => z)) if (list.Count == 0 || Math.Abs(z - list[^1]) > 1e-6) list.Add(z);
        return list;
    }

    /// <summary>解析标高文本（换行/逗号/分号/空白分隔）。</summary>
    public static List<double> ParseLevels(string? text)
    {
        var res = new List<double>();
        foreach (var tok in (text ?? "").Split(new[] { '\n', '\r', ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(tok.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) res.Add(z);
        return res;
    }

    /// <summary>按升序边界分桶：N 个边界 → N-1 个 [b_i,b_{i+1})；中心 Z 法；子块逐个分桶。</summary>
    public static ElevationReport BinByElevation(Result r, IReadOnlyList<double> boundaries)
    {
        var rep = new ElevationReport { TotalCells = r.KeepCount + r.SubCells.Count, TotalVolume = r.TotalVolume };
        bool hasBands = boundaries != null && boundaries.Count >= 2;
        double[] b = hasBands ? boundaries!.ToArray() : Array.Empty<double>();
        if (hasBands) for (int i = 0; i + 1 < b.Length; i++) rep.Levels.Add(new LevelRow { ZLow = b[i], ZHigh = b[i + 1] });
        long nxy = (long)r.Nx * r.Ny;
        for (int k = 0; k < r.Nz; k++)
        {
            long kBase = k * nxy, layerKeep = 0, end = kBase + nxy;
            for (long idx = kBase; idx < end; idx++) if (r.Keep[idx]) layerKeep++;
            if (layerKeep == 0) continue;
            double cz = r.Oz + (k + 0.5) * r.Vz;
            int band = hasBands ? FindBand(b, cz) : -1;
            if (band < 0) { rep.OutOfRangeCells += layerKeep; rep.OutOfRangeVolume += layerKeep * r.CellVolume; }
            else { rep.Levels[band].Cells += layerKeep; rep.Levels[band].Volume += layerKeep * r.CellVolume; }
        }
        foreach (var s in r.SubCells)
        {
            double vol = s.Sx * s.Sy * s.Sz * s.Percent;
            int band = hasBands ? FindBand(b, s.Z) : -1;
            if (band < 0) { rep.OutOfRangeCells++; rep.OutOfRangeVolume += vol; }
            else { rep.Levels[band].Cells++; rep.Levels[band].Volume += vol; }
        }
        return rep;
    }

    private static int FindBand(double[] b, double z)
    {
        if (z < b[0] || z >= b[^1]) return -1;
        int lo = 0, hi = b.Length - 1;
        while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (b[mid] <= z) lo = mid; else hi = mid - 1; }
        return lo;
    }

    // ── 转块体模型 ──

    /// <summary>
    /// 体素化结果 → 块体模型：实心母块（Size=Vx）+ 子块（Size=子块边长，属性 percent=占比）。
    /// 只存实际块（原八叉树"只存实际块"语义），IsRegular=false。
    /// </summary>
    public static BlockModelMeta ToBlockModel(Result r, string name)
    {
        var blocks = new List<BlockModel.Block>((int)Math.Min(int.MaxValue, r.KeepCount + r.SubCells.Count));
        var percent = new List<double>();
        long nxy = (long)r.Nx * r.Ny;
        for (int k = 0; k < r.Nz; k++)
            for (int j = 0; j < r.Ny; j++)
                for (int i = 0; i < r.Nx; i++)
                {
                    if (!r.Keep[i + j * (long)r.Nx + k * nxy]) continue;
                    blocks.Add(new BlockModel.Block { X = r.Ox + (i + 0.5) * r.Vx, Y = r.Oy + (j + 0.5) * r.Vy, Z = r.Oz + (k + 0.5) * r.Vz, Size = r.Vx, Grade = 0 });
                    percent.Add(1);
                }
        foreach (var s in r.SubCells)
        {
            blocks.Add(new BlockModel.Block { X = s.X, Y = s.Y, Z = s.Z, Size = s.Sx, Grade = 0 });
            percent.Add(s.Percent);
        }
        var m = new BlockModelMeta
        {
            Name = name, IsRegular = false, Blocks = blocks, Ox = r.Ox, Oy = r.Oy, Oz = r.Oz, Sx = r.Vx, Sy = r.Vy, Sz = r.Vz, Nx = r.Nx, Ny = r.Ny, Nz = r.Nz,
            StorageMode = r.SubCells.Count > 0 ? BlockStorageMode.Adaptive : BlockStorageMode.Sparse, SubCellCount = r.SubCells.Count,
        };
        if (r.SubCells.Count > 0)
        {
            m.Attrs["percent"] = percent.ToArray();
            m.PropertySchema.Add(new BlockPropertyColumn { Name = "percent", DefaultValue = 1, Description = "体内占比(百分比块)" });
        }
        return m;
    }

    /// <summary>
    /// 均匀占位位图 → 八叉树叶块（忠实原 EntityToBlocksDialog / VoxelVolumeDialog「生成块体」：
    /// <c>OctreeLeafBuilder.BuildFromOccupancy</c>，实心内部并大块、边界细到最细格）。
    /// 调用方须以 depth=0（均匀中心判定）的 <see cref="Build"/> 结果为料；自适应百分比子块只进体积报表，不进块体模型。
    /// </summary>
    public static List<OctreeLeaf> ToLeaves(Result r) => OctreeLeafBuilder.BuildFromOccupancy(r.Nx, r.Ny, r.Nz, r.Keep);

    /// <summary>
    /// 八叉树叶块 → 块体模型：每叶一块，中心 = 原点 + (细格原点 + 边长/2)·细格尺寸，<see cref="BlockModel.Block.Size"/> = 边长·Sx；
    /// 叶块尺寸 = k·(Sx,Sy,Sz)（k = Size/Sx，与 .blk 导入同约定），粗叶块数记入 <see cref="BlockModelMeta.SubCellCount"/>。
    /// 只存实际块（原"八叉树原生"语义），IsRegular=false、Sparse。
    /// </summary>
    public static BlockModelMeta ToBlockModel(Result r, IReadOnlyList<OctreeLeaf> leaves, string name)
    {
        var blocks = new List<BlockModel.Block>(leaves.Count);
        int coarse = 0;
        byte maxShift = 0;
        foreach (var lf in leaves)
        {
            int s = lf.Size;
            if (lf.Shift > 0) coarse++;
            if (lf.Shift > maxShift) maxShift = lf.Shift;
            blocks.Add(new BlockModel.Block
            {
                X = r.Ox + (lf.X + s * 0.5) * r.Vx, Y = r.Oy + (lf.Y + s * 0.5) * r.Vy, Z = r.Oz + (lf.Z + s * 0.5) * r.Vz,
                Size = s * r.Vx, Grade = 0,
            });
        }
        var m = new BlockModelMeta
        {
            Name = name, Description = "八叉树叶块（实心内部并大块、边界细到最细格）", IsRegular = false, Blocks = blocks,
            Ox = r.Ox, Oy = r.Oy, Oz = r.Oz, Sx = r.Vx, Sy = r.Vy, Sz = r.Vz, Nx = r.Nx, Ny = r.Ny, Nz = r.Nz,
            StorageMode = BlockStorageMode.Sparse, SubCellCount = coarse,
        };
        if (maxShift > 0) m.SubBlockDepthMax = maxShift;
        m.SubMinX = r.Vx; m.SubMinY = r.Vy; m.SubMinZ = r.Vz;   // 最细格 = 体素尺寸（同 .blk 导入）
        return m;
    }

    // ── 报表（原 VolumeByLevelReport）──

    public static string RenderHtml(Result r, ElevationReport lv, string title)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(8192);
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-cn\"><head><meta charset=\"UTF-8\">");
        sb.Append("<title>体素格网体积报表 - ").Append(BlockModelReport.Esc(title)).AppendLine("</title>");
        sb.AppendLine("<style>body{font-family:-apple-system,'Segoe UI','Microsoft YaHei',sans-serif;margin:32px;color:#1f2937;} h1{color:#1e3a5f;border-bottom:2px solid #0d6efd;padding-bottom:8px;} h2{color:#1e3a5f;margin-top:28px;} .meta{color:#6c757d;font-size:12px;margin-bottom:16px;} .summary-grid{display:grid;grid-template-columns:repeat(2,1fr);gap:8px 24px;background:#f8f9fa;padding:16px;border-radius:6px;margin:16px 0;} .summary-grid .k{color:#6c757d;} .summary-grid .v{font-weight:600;color:#1e3a5f;} table{width:100%;border-collapse:collapse;margin-top:8px;font-size:13px;} th,td{padding:8px 12px;text-align:left;border-bottom:1px solid #dee2e6;} th{background:#e9ecef;font-weight:600;color:#495057;} .num{text-align:right;font-family:'Consolas',monospace;} tr:hover{background:#f8f9fa;} tfoot td{font-weight:700;background:#f1f3f5;} .warn{color:#b45309;}</style></head><body>");
        sb.Append("<h1>体素格网体积报表: ").Append(BlockModelReport.Esc(title)).AppendLine("</h1>");
        sb.Append("<div class=\"meta\">生成时间: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine("</div>");
        if (r.OpenMeshCount > 0) sb.Append("<p class=\"warn\">⚠ 选中的 ").Append(r.MeshCount).Append(" 个网格中有 ").Append(r.OpenMeshCount).AppendLine(" 个非封闭/非流形，体积可能不可靠。</p>");
        sb.AppendLine("<h2>汇总</h2><div class=\"summary-grid\">");
        void Row(string k, string v) => sb.Append("<span class=\"k\">").Append(BlockModelReport.Esc(k)).Append("</span><span class=\"v\">").Append(BlockModelReport.Esc(v)).AppendLine("</span>");
        Row("整体体积", $"{r.TotalVolume:N1} m³");
        if (r.ExactVolume > 0) Row("散度定理精确体积", $"{r.ExactVolume:N1} m³（偏差 {(r.TotalVolume - r.ExactVolume) / r.ExactVolume * 100:+0.##;-0.##}%）");
        Row("体内 cell 数", $"{r.KeepCount:N0} / {r.TotalCells:N0}");
        Row("体素尺寸 (m)", $"{r.Vx:0.###} × {r.Vy:0.###} × {r.Vz:0.###}");
        Row("单 cell 体积", $"{r.CellVolume:N3} m³");
        Row("网格数", $"{r.Nx} × {r.Ny} × {r.Nz}");
        Row("参与网格", $"{r.MeshCount} 个");
        sb.AppendLine("</div>");
        sb.AppendLine("<h2>分标高水平报量</h2>");
        if (lv.Levels.Count == 0) sb.AppendLine("<p style=\"color:#6c757d\">未指定标高列表（或不足 2 个边界），仅整体体积。</p>");
        else
        {
            sb.AppendLine("<table><thead><tr><th>标高区间 (m)</th><th class=\"num\">cell 数</th><th class=\"num\">体积 (m³)</th><th class=\"num\">占比</th></tr></thead><tbody>");
            foreach (var row in lv.Levels)
            {
                double pct = lv.TotalVolume > 0 ? row.Volume / lv.TotalVolume * 100 : 0;
                sb.Append("<tr><td>").Append(row.ZLow.ToString("0.###", ci)).Append(" ~ ").Append(row.ZHigh.ToString("0.###", ci)).Append("</td><td class=\"num\">").Append(row.Cells.ToString("N0", ci))
                  .Append("</td><td class=\"num\">").Append(row.Volume.ToString("N1", ci)).Append("</td><td class=\"num\">").Append(pct.ToString("0.##", ci)).Append("%</td></tr>");
            }
            sb.AppendLine("</tbody>");
            if (lv.OutOfRangeCells > 0)
                sb.Append("<tr><td>区间外</td><td class=\"num\">").Append(lv.OutOfRangeCells.ToString("N0", ci)).Append("</td><td class=\"num\">").Append(lv.OutOfRangeVolume.ToString("N1", ci)).Append("</td><td class=\"num\">—</td></tr>");
            sb.Append("<tfoot><tr><td>合计</td><td class=\"num\">").Append(lv.TotalCells.ToString("N0", ci)).Append("</td><td class=\"num\">").Append(lv.TotalVolume.ToString("N1", ci)).Append("</td><td class=\"num\">100%</td></tr></tfoot></table>");
        }
        sb.AppendLine("<div style=\"margin-top:32px;color:#6c757d;font-size:11px\">PitMine3D BlockModelLib · 体素格网体积</div></body></html>");
        return sb.ToString();
    }

    public static string RenderCsv(Result r, ElevationReport lv, string title)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(2048);
        sb.AppendLine("section,key,value");
        void Csv(string sec, string k, string v) => sb.Append(BlockModelReport.EscCsv(sec)).Append(',').Append(BlockModelReport.EscCsv(k)).Append(',').AppendLine(BlockModelReport.EscCsv(v));
        Csv("summary", "title", title);
        Csv("summary", "generated_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Csv("summary", "total_volume_m3", r.TotalVolume.ToString("0.######", ci));
        Csv("summary", "exact_volume_m3", r.ExactVolume.ToString("0.######", ci));
        Csv("summary", "kept_cells", r.KeepCount.ToString(ci));
        Csv("summary", "total_cells", r.TotalCells.ToString(ci));
        Csv("summary", "cell_volume_m3", r.CellVolume.ToString("0.######", ci));
        Csv("summary", "voxel_x", r.Vx.ToString(ci)); Csv("summary", "voxel_y", r.Vy.ToString(ci)); Csv("summary", "voxel_z", r.Vz.ToString(ci));
        Csv("summary", "mesh_count", r.MeshCount.ToString(ci));
        Csv("summary", "open_mesh_count", r.OpenMeshCount.ToString(ci));
        sb.AppendLine();
        sb.AppendLine("z_low,z_high,cells,volume_m3");
        foreach (var row in lv.Levels)
            sb.Append(row.ZLow.ToString("0.######", ci)).Append(',').Append(row.ZHigh.ToString("0.######", ci)).Append(',').Append(row.Cells.ToString(ci)).Append(',').AppendLine(row.Volume.ToString("0.######", ci));
        if (lv.OutOfRangeCells > 0) sb.Append("out_of_range,,").Append(lv.OutOfRangeCells.ToString(ci)).Append(',').AppendLine(lv.OutOfRangeVolume.ToString("0.######", ci));
        return sb.ToString();
    }
}
