using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「三维地质建模 → 块体模型」组纯逻辑回归：BlockModelMeta（规则网格/属性/公式/筛选/删除/着色/快照）、BlockCellPredicate、
/// BlockPolygonRegion、BlockModelReport、BlockVoxelBuilder（体素体积/分标高/转块体）、BlockCoalQualityLink（真库煤样估值）。
/// </summary>
public class ModelingBlockModelTests
{
    private static BlockModelMeta Small()
    {
        // 原点 (100,200,300), 块 10×10×5, 网格 3×2×2 = 12 块
        var m = BlockModelMeta.CreateRegular("T", 100, 200, 300, 10, 10, 5, 3, 2, 2);
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "grade", DefaultValue = 0 });
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "rock", IsCategorical = true, DefaultValue = 0 });
        return m;
    }

    // ── BlockModelMeta ──

    [Fact]
    public void CreateRegular_orders_blocks_i_j_k_and_centers()
    {
        var m = Small();
        Assert.Equal(12, m.Blocks.Count);
        Assert.Equal(12, m.BlockCount);
        Assert.Equal((105, 205, 302.5), (m.Blocks[0].X, m.Blocks[0].Y, m.Blocks[0].Z));
        Assert.Equal((125, 215, 307.5), (m.Blocks[11].X, m.Blocks[11].Y, m.Blocks[11].Z));
        Assert.Equal((2, 1, 1), m.IJK(11));
        Assert.Equal((0, 1, 0), m.IJK(3));
        var b = m.Bounds;
        Assert.Equal((100, 200, 300, 130, 220, 310), b);
        Assert.Equal(500, m.CellVolume, 6);
        Assert.Null(m.ValidateBasic());
        Assert.Contains("块尺寸", new BlockModelMeta { Sx = 0 }.ValidateBasic());
    }

    [Fact]
    public void EstimateMemory_sparse_halves_dense()
    {
        var m = Small();
        m.StorageMode = BlockStorageMode.Dense;
        long dense = m.EstimateMemoryBytes();     // 12 × (1 + 4 + 4)
        Assert.Equal(12 * 9, dense);
        m.StorageMode = BlockStorageMode.Sparse;
        Assert.Equal(dense / 2, m.EstimateMemoryBytes());
        Assert.Equal("1.5 KB", BlockModelMeta.BytesToHuman(1536));
    }

    [Fact]
    public void Formula_uses_ijk_xyz_and_attributes()
    {
        var m = Small();
        m.SetConstant("grade", 2);
        var eng = BlockAttrExpression.Compile("i + 10*j + 100*k + grade");
        int written = m.ApplyFormula("f", eng);
        Assert.Equal(12, written);
        Assert.Equal(2, m.GetValue("f", 0), 9);
        Assert.Equal(112 + 2, m.GetValue("f", 11), 9);   // i=2,j=1,k=1
        var z = BlockAttrExpression.Compile("z + sz + nx");
        m.ApplyFormula("g", z);
        Assert.Equal(302.5 + 5 + 3, m.GetValue("g", 0), 9);
        Assert.NotNull(m.FindColumn("f"));   // 公式列自动登记
    }

    [Fact]
    public void Constant_with_scope_writes_only_visible()
    {
        var m = Small();
        m.SetConstant("grade", 1);
        m.Filter = new BlockFilterSet();
        m.Filter.Conditions.Add(new BlockFilterCondition { AttributeName = "grade", Op = BlockFilterOperator.GreaterThan, Value = 0 });
        m.DeletedIds.Add(0);
        Assert.Equal(11, m.CountVisibleCells());
        long n = m.SetConstant("grade", 5, m.IsCellVisible);
        Assert.Equal(11, n);
        Assert.Equal(1, m.GetValue("grade", 0), 9);   // 已删块未写
        Assert.Equal(5, m.GetValue("grade", 1), 9);
    }

    [Fact]
    public void Filter_between_and_or()
    {
        var m = Small();
        var g = m.EnsureAttr("grade");
        for (int i = 0; i < g.Length; i++) g[i] = i;
        var fs = new BlockFilterSet { MatchAll = true };
        fs.Conditions.Add(new BlockFilterCondition { AttributeName = "grade", Op = BlockFilterOperator.Between, Value = 8, Value2 = 3 });
        m.Filter = fs;
        Assert.Equal(6, m.CountVisibleCells());   // 3..8
        fs.MatchAll = false;
        fs.Conditions.Add(new BlockFilterCondition { AttributeName = "grade", Op = BlockFilterOperator.Equal, Value = 11 });
        Assert.Equal(7, m.CountVisibleCells());
        Assert.Contains("BETWEEN", fs.ToString());
        Assert.Contains(" OR ", fs.ToString());
    }

    [Fact]
    public void ApplyKeep_records_and_restores_only_own_deletes()
    {
        var m = Small();
        m.DeletedIds.Add(5);   // 其它来源
        var rec = new BlockDeleteRecord();
        int n = m.ApplyKeep((i, b) => b.Z < 305, rec);   // 删上层 6 块（下层 5 号是其它来源）
        Assert.Equal(6, n);
        Assert.Equal(7, m.DeletedBlockCount);
        Assert.Equal(5, m.LiveBlockCount());
        Assert.Equal(6, m.Restore(rec));
        Assert.Single(m.DeletedIds);
        Assert.Equal(1, m.ClearDeleted());
        Assert.Empty(m.DeletedIds);
    }

    [Fact]
    public void ColorOf_fill_z_ramp_classes_and_categories()
    {
        var m = Small();
        m.DisplayStyle.FillColor = (1, 2, 3);
        Assert.Equal(((byte)1, (byte)2, (byte)3), m.ColorOf(0));
        // 高程 Z：底层→色带起点，顶层→终点方向（RdYlBu 蓝→红）
        m.ActiveColormapAttribute = BlockModelMeta.ZElevationSentinel;
        var lo = m.ColorOf(0); var hi = m.ColorOf(11);
        Assert.True(lo.b > lo.r); Assert.True(hi.r > hi.b);
        // 连续属性自动范围
        var g = m.EnsureAttr("grade"); for (int i = 0; i < g.Length; i++) g[i] = i;
        m.ActiveColormapAttribute = "grade"; m.ColormapRange = null;
        Assert.Equal(BlockColormap.Sample(BlockColormapPreset.RdYlBu, 0), m.ColorOf(0));
        Assert.Equal(BlockColormap.Sample(BlockColormapPreset.RdYlBu, 1), m.ColorOf(11));
        // 分级区间 [0,6) 红 / [6,11] 绿
        m.DisplayStyle.ClassBreaks["grade"] = new List<BlockColorClass> { new(0, 6, (255, 0, 0)), new(6, 11, (0, 255, 0)) };
        Assert.Equal(((byte)255, (byte)0, (byte)0), m.ColorOf(5));
        Assert.Equal(((byte)0, (byte)255, (byte)0), m.ColorOf(6));
        Assert.Equal(((byte)0, (byte)255, (byte)0), m.ColorOf(11));   // 末区间含上界
        // 分类
        var r = m.EnsureAttr("rock"); r[0] = 3; r[1] = 3; r[2] = 7;
        m.ActiveColormapAttribute = "rock";
        Assert.Equal(BlockCategoricalPalette.ColorForCode(3), m.ColorOf(0));
        m.DisplayStyle.EnsureCategoryColors("rock")[7] = (9, 9, 9);
        Assert.Equal(((byte)9, (byte)9, (byte)9), m.ColorOf(2));
        var cats = m.EnumerateCategories("rock");
        Assert.Equal(new[] { 0, 3, 7 }, cats.Select(c => c.Code).ToArray());
    }

    /// <summary>
    /// 取色预案(ColorPlan)必须与逐块现算的 ColorOf 给出同一颜色 —— 五种着色分支逐一对拍。
    /// 建网格走的是预案那条(见下面的 BuildCellMesh_is_linear_in_block_count)，两条不一致就是配色错。
    /// </summary>
    [Fact]
    public void ColorOf_with_plan_matches_per_block_recompute()
    {
        var m = Small();
        var g = m.EnsureAttr("grade"); for (int i = 0; i < g.Length; i++) g[i] = i;
        var rk = m.EnsureAttr("rock"); rk[0] = 3; rk[1] = 3; rk[2] = 7;
        m.FindColumn("rock")!.IsCategorical = true;
        m.DisplayStyle.EnsureCategoryColors("rock")[7] = (9, 9, 9);

        void Same(string what)
        {
            var plan = m.MakeColorPlan();
            for (int i = 0; i < m.Blocks.Count; i++)
                Assert.Equal(m.ColorOf(i), m.ColorOf(i, plan));   // what: 出错时看调用行
            Assert.NotNull(what);
        }

        m.ActiveColormapAttribute = null; Same("关闭着色");
        m.ActiveColormapAttribute = BlockModelMeta.ZElevationSentinel; Same("高程 Z");
        m.ActiveColormapAttribute = "grade"; m.ColormapRange = null; Same("连续属性(自动值域)");
        m.ColormapRange = (2, 8); Same("连续属性(固定值域)");
        m.ColormapRange = null;
        m.DisplayStyle.ClassBreaks["grade"] = new List<BlockColorClass> { new(0, 6, (255, 0, 0)), new(6, 11, (0, 255, 0)) };
        Same("分级区间");
        m.ActiveColormapAttribute = "rock"; Same("分类离散");
        m.ActiveColormapAttribute = "不存在的列"; Same("缺列回落默认值");
    }

    /// <summary>
    /// 建网格必须是 O(块数)，不是 O(块数²)。
    ///
    /// 曾经是后者：着色值域未固定时（导入块体正是这么设的）逐块取色都要扫一遍整列属性，
    /// 实测 2 万块 0.6 秒、16 万块 26 秒，311 万块的东露天块体模型外推近 3 小时 —— 导入块体
    /// 「(未响应)」卡死就是这个。这里给 12 万块一个宽到不会误报的上限：真退回 O(n²) 得几十秒。
    /// </summary>
    [Fact]
    public void BuildCellMesh_is_linear_in_block_count()
    {
        const int n = 120_000;
        var blocks = new List<BlockModel.Block>(n);
        var vals = new double[n];
        for (int i = 0; i < n; i++)
        {
            blocks.Add(new BlockModel.Block { X = i % 100 * 10, Y = i / 100 % 100 * 10, Z = i / 10000 * 5, Size = 10, Grade = i % 97 });
            vals[i] = i % 97;
        }
        var m = BlockModelMeta.FromBlocks("大", blocks, new Dictionary<string, double[]> { ["品位"] = vals });
        m.ActiveColormapAttribute = "品位";
        m.ColormapRange = null;   // 值域自动 —— 正是会触发逐块重扫的那档
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mesh = m.BuildCellMesh(null, out var stat);
        sw.Stop();
        Assert.NotNull(mesh);
        Assert.True(stat.DrawnCells > 0);
        Assert.True(sw.Elapsed.TotalSeconds < 8,
            $"{n:N0} 块建网格用了 {sw.Elapsed.TotalSeconds:0.0} 秒 —— 逐块取色八成又在扫全表(O(块数²))");
    }

    /// <summary>
    /// 八叉树叶块建模：几何由文件给定（原点/三轴细格/维度），逐块边长倍数按 Size/Sx 推，
    /// 于是粗块画成 k·(Sx,Sy,Sz) 的长方体 —— 而不是边长 Size 的立方体。
    /// 平朔块体细格 25×25×0.5 m，画成立方体就竖向胖 50 倍，整个模型糊成一块平板。
    /// </summary>
    [Fact]
    public void FromLeaves_keeps_anisotropic_cell_geometry()
    {
        // 细格 25×25×0.5：一个最细块 + 一个 4 倍的粗块（彼此不相邻，都要画）
        var blocks = new List<BlockModel.Block>
        {
            new() { X = 12.5, Y = 12.5, Z = 0.25, Size = 25 },
            new() { X = 1000, Y = 1000, Z = 100, Size = 100 },
        };
        var m = BlockModelMeta.FromLeaves("八叉树", blocks, null, 0, 0, 0, 25, 25, 0.5, 240, 244, 1088, 11, 1);
        Assert.Equal(25, m.Sx, 9); Assert.Equal(25, m.Sy, 9); Assert.Equal(0.5, m.Sz, 9);
        Assert.Equal(1.0, m.CellScale(blocks[0]), 9);
        Assert.Equal(4.0, m.CellScale(blocks[1]), 9);   // 100/25

        var mesh = m.BuildCellMesh(null, out var stat);
        Assert.Equal(2, stat.DrawnCells);
        // 粗块的 Z 半高 = 4 × 0.5/2 = 1（若按立方体就是 50）
        double zTop = mesh!.Verts.Where(v => v.x > 500).Max(v => v.z);
        double zBot = mesh.Verts.Where(v => v.x > 500).Min(v => v.z);
        Assert.Equal(101, zTop, 6);
        Assert.Equal(99, zBot, 6);
        // 体积按 k³·细格体积（25·25·0.5），不是 Size³
        Assert.Equal(25 * 25 * 0.5 * (1 + 64), m.LiveVolume(), 6);
    }

    [Fact]
    public void BuildCells_respects_deleted_filter_and_clip()
    {
        var m = Small();
        m.DeletedIds.Add(0);
        // 出的是一张六面体网格；块数看 stat.DrawnCells(此前是「实体个数=块数」)
        var mesh = m.BuildCellMesh(null, out var stat);
        Assert.NotNull(mesh);
        Assert.Equal(11, stat.DrawnCells);
        // 3×2×2 的盒子外表面 = 2·(3·2 + 3·2 + 2·2) = 32 张面；抠掉一个角块，它自己的 3 张外面没了、
        // 邻居原先被它挡住的 3 张露出来，面数不变。被挡住的面不发，见 BlockMeshBuilder 类注释。
        Assert.Equal(32, stat.FaceCount);
        Assert.Equal(stat.FaceCount * 4, mesh!.Verts.Count);
        // 孤立的一块要有真实厚度(不是平板)：单块模型六面全发, X 边 = Sx, Z 边 = Sz
        var one = BlockModelMeta.CreateRegular("单块", 100, 200, 300, 10, 10, 5, 1, 1, 1);
        var oneMesh = one.BuildCellMesh(null, out var oneStat)!;
        Assert.Equal(6, oneStat.FaceCount);
        double x0 = oneMesh.Verts.Min(v => v.x), x1 = oneMesh.Verts.Max(v => v.x);
        double z0 = oneMesh.Verts.Min(v => v.z), z1 = oneMesh.Verts.Max(v => v.z);
        Assert.Equal(10, x1 - x0, 9);
        Assert.Equal(5, z1 - z0, 9);
        m.BuildCellMesh(b => b.Z < 305, out var clipped);
        Assert.Equal(5, clipped.DrawnCells);   // 剖切：上层 6 块被切，下层 0 号已删 → 5
        m.Filter = new BlockFilterSet();
        m.Filter.Conditions.Add(new BlockFilterCondition { AttributeName = "grade", Op = BlockFilterOperator.GreaterThan, Value = 0 });
        m.BuildCellMesh(null, out var unfiltered);
        Assert.Equal(11, unfiltered.DrawnCells);   // 无属性数据 → 不收窄
        m.SetConstant("grade", 0);
        Assert.Empty(m.BuildCells());
    }

    [Fact]
    public void LiveSnapshot_excludes_deleted_and_syncs_grade()
    {
        var m = Small();
        var g = m.EnsureAttr("grade"); for (int i = 0; i < g.Length; i++) g[i] = i * 0.5;
        m.ActiveColormapAttribute = "grade";
        m.DeletedIds.Add(0);
        var (blocks, attrs) = m.LiveSnapshot();
        Assert.Equal(11, blocks.Count);
        Assert.Equal(0.5, blocks[0].Grade, 9);
        Assert.Equal(11, attrs["grade"].Length);
        Assert.Equal(5.5, attrs["grade"][10], 9);
    }

    [Fact]
    public void FromBlocks_infers_grid_and_grade_column()
    {
        var blocks = new List<BlockModel.Block>();
        for (int k = 0; k < 2; k++) for (int i = 0; i < 3; i++) blocks.Add(new BlockModel.Block { X = 5 + i * 10, Y = 5, Z = 5 + k * 10, Size = 10, Grade = i });
        var m = BlockModelMeta.FromBlocks("imp", blocks, null);
        Assert.False(m.IsRegular);
        Assert.Equal(3, m.Nx); Assert.Equal(1, m.Ny); Assert.Equal(2, m.Nz);
        Assert.Equal(0, m.Ox, 9); Assert.Equal(0, m.Oy, 9);
        Assert.True(m.HasData("grade"));
        Assert.Equal(2, m.GetValue("grade", 2), 9);
        Assert.Equal(6, m.BlockCount);
    }

    [Fact]
    public void GenerateClasses_equal_and_quantile()
    {
        var m = Small();
        var g = m.EnsureAttr("grade"); for (int i = 0; i < g.Length; i++) g[i] = i + 1;   // 1..12（0 为默认值哨兵）
        var eq = m.GenerateClasses("grade", 4, false, BlockColormapPreset.Jet);
        Assert.Equal(4, eq.Count);
        Assert.Equal(1, eq[0].Min, 9); Assert.Equal(12, eq[3].Max, 9);
        Assert.Equal(eq[0].Max, eq[1].Min, 9);
        Assert.Equal(3.75, eq[1].Min, 9);
        var q = m.GenerateClasses("grade", 2, true, BlockColormapPreset.Jet);
        Assert.Equal(1, q[0].Min, 9); Assert.Equal(12, q[1].Max, 9);
        Assert.InRange(q[0].Max, 6, 7);
        Assert.NotEqual(eq[0].Color, eq[3].Color);
    }

    [Fact]
    public void Colormap_sample_endpoints()
    {
        Assert.Equal(((byte)0x31, (byte)0x5B, (byte)0xAE), BlockColormap.Sample(BlockColormapPreset.RdYlBu, 0));
        Assert.Equal(((byte)0xD7, (byte)0x30, (byte)0x27), BlockColormap.Sample(BlockColormapPreset.RdYlBu, 1));
        Assert.Equal(((byte)0, (byte)0, (byte)0), BlockColormap.Sample(BlockColormapPreset.Gray, -1));
        Assert.Equal(((byte)255, (byte)255, (byte)255), BlockColormap.Sample(BlockColormapPreset.Gray, 2));
        Assert.Equal(((byte)0x7A, (byte)0x04, (byte)0x03), BlockColormap.Sample(BlockColormapPreset.Turbo, 1));
        Assert.Equal(BlockDefaultPalette.Colors[1], BlockDefaultPalette.Next(9));
        Assert.Equal(BlockCategoricalPalette.ColorForCode(0), BlockCategoricalPalette.ColorForCode(16));
        Assert.True(BlockPropertyColumn.IsValidName("_ok1"));
        Assert.False(BlockPropertyColumn.IsValidName("1bad"));
        Assert.False(BlockPropertyColumn.IsValidName("a b"));
    }

    // ── 多段线范围 ──

    [Fact]
    public void PolygonRegion_union_hole_and_open_ring()
    {
        var outer = BlockPolygonRegion.RingFromPoints(new List<(double, double)> { (0, 0), (10, 0), (10, 10), (0, 10), (0, 0) })!;   // 重复闭合点被去掉
        Assert.Equal(8, outer.Length);
        var hole = BlockPolygonRegion.RingFromPoints(new List<(double, double)> { (4, 4), (6, 4), (6, 6), (4, 6) })!;
        var r = BlockPolygonRegion.FromRings(new[] { outer, hole })!;
        Assert.Equal(2, r.RingCount);
        Assert.True(r.ContainsXY(1, 1));
        Assert.False(r.ContainsXY(5, 5));     // 洞
        Assert.False(r.ContainsXY(11, 5));
        Assert.True(r.OverlapsXY(9, 9, 20, 20));
        Assert.False(r.OverlapsXY(11, 11, 20, 20));
        Assert.Null(BlockPolygonRegion.RingFromPoints(new List<(double, double)> { (0, 0), (1, 1) }));
        Assert.Null(BlockPolygonRegion.FromRings(Array.Empty<double[]>()));
    }

    // ── 谓词表达式 ──

    [Fact]
    public void CellPredicate_compare_logic_and_arithmetic()
    {
        var ctx = new MutableBlockExprContext();
        ctx.Set("z", 250); ctx.Set("grade", 0.05); ctx.Set("x", 1020);
        Assert.True(BlockCellPredicate.Compile("z > 200").Evaluate(ctx));
        Assert.False(BlockCellPredicate.Compile("z > 300").Evaluate(ctx));
        Assert.True(BlockCellPredicate.Compile("grade < 0.1 AND z > 5").Evaluate(ctx));
        Assert.True(BlockCellPredicate.Compile("abs(x - 1000) < 50").Evaluate(ctx));
        Assert.False(BlockCellPredicate.Compile("NOT (z > 200)").Evaluate(ctx));
        Assert.True(BlockCellPredicate.Compile("(z - 1) * 2 > 3 || grade > 1").Evaluate(ctx));
        Assert.True(BlockCellPredicate.Compile("(z > 1000) or (grade <= 0.05)").Evaluate(ctx));
        Assert.True(BlockCellPredicate.Compile("z - 100").Evaluate(ctx));      // 无比较：>0 为真
        Assert.False(BlockCellPredicate.Compile("grade - 1").Evaluate(ctx));
        Assert.Contains("z", BlockCellPredicate.Compile("z > 200 AND grade < 1").ReferencedVariables);
        Assert.Throws<BlockExprException>(() => BlockCellPredicate.Compile("z >"));
        Assert.Throws<BlockExprException>(() => BlockCellPredicate.Compile("(z > 1"));
        Assert.Throws<BlockExprException>(() => BlockCellPredicate.Compile(""));
    }

    // ── 报告 ──

    [Fact]
    public void Report_summary_attribute_stats_and_levels()
    {
        var m = Small();
        var g = m.EnsureAttr("grade"); for (int i = 0; i < g.Length; i++) g[i] = i;   // 0..11
        m.DeletedIds.Add(11);
        var r = BlockModelReport.Compute(m);
        Assert.Equal(12, r.TotalCells); Assert.Equal(1, r.DeletedCells); Assert.Equal(11, r.VisibleCells);
        Assert.Equal(11 * 500, r.VisibleVolume, 6);
        var a = r.Attributes.First(x => x.Name == "grade");
        Assert.Equal(0, a.Min, 9); Assert.Equal(10, a.Max, 9); Assert.Equal(5, a.Mean, 9);
        Assert.Equal(Math.Sqrt(10.0), a.Std, 6);
        Assert.Equal(10, a.NonDefaultCount);
        Assert.NotNull(a.Histogram); Assert.Equal(11, a.Histogram!.Sum());
        var rock = r.Attributes.First(x => x.Name == "rock");
        Assert.True(double.IsNaN(rock.Min));   // 无数据列
        Assert.Equal(2, r.Levels.Count);
        Assert.Equal(6, r.Levels[0].Cells); Assert.Equal(5, r.Levels[1].Cells);
        Assert.Equal(r.VisibleVolume, r.Levels.Sum(l => l.Volume), 6);
        // 范围限定
        var fs = new BlockFilterSet(); fs.Conditions.Add(new BlockFilterCondition { AttributeName = "grade", Op = BlockFilterOperator.GreaterEqual, Value = 8 });
        var rs = BlockModelReport.Compute(m, fs);
        Assert.True(rs.IsScoped); Assert.Equal(3, rs.ScopedCells);
        Assert.Equal(8, rs.Attributes[0].Min, 9); Assert.Equal(3 * 500, rs.ScopedVolume, 6);
        string txt = BlockModelReport.RenderPlainText(rs);
        Assert.Contains("当前筛选", txt); Assert.Contains("分标高统计", txt);
        string csv = BlockModelReport.RenderCsv(r);
        Assert.Contains("summary,visible_cells,11", csv);
        Assert.Contains("grade,,Float,10,0,10,5,", csv);
        Assert.Contains("z_low,z_high,cells,volume_m3", csv);
        string html = BlockModelReport.RenderHtml(r);
        Assert.Contains("<h1>块体模型报告: T</h1>", html);
        Assert.Contains("class=\"hist\"", html);
    }

    // ── 体素体积 ──

    private static BlockVoxelBuilder.MeshInput Box(double cx, double cy, double cz, double sx, double sy, double sz)
    {
        var (v, t) = PrimitiveBodies.Box(cx, cy, cz, sx, sy, sz);
        return BlockVoxelBuilder.FromMesh("box", v, t);
    }

    [Fact]
    public void VoxelBuilder_box_uniform_matches_exact_and_bins_by_level()
    {
        var box = Box(5, 5, 5, 10, 10, 10);
        Assert.True(box.Closed);
        Assert.Equal(1000, box.ExactVolume, 6);
        var est = BlockVoxelBuilder.EstimateGrid(new[] { box }, 2, 2, 2);
        Assert.Equal((5, 5, 5), (est!.Value.nx, est.Value.ny, est.Value.nz));
        var r = BlockVoxelBuilder.Build(new[] { box }, 2, 2, 2, out var err);
        Assert.NotNull(r); Assert.Equal("", err);
        Assert.Equal(125, r!.KeepCount);
        Assert.Equal(1000, r.TotalVolume, 6);
        Assert.Empty(r.SubCells);
        var lv = BlockVoxelBuilder.BinByElevation(r, BlockVoxelBuilder.NormalizeLevels(BlockVoxelBuilder.ParseLevels("0, 4\n10,4")));
        Assert.Equal(2, lv.Levels.Count);
        Assert.Equal(400, lv.Levels[0].Volume, 6);     // z∈[0,4): 2 层
        Assert.Equal(600, lv.Levels[1].Volume, 6);     // z∈[4,10): 3 层
        Assert.Equal(0, lv.OutOfRangeCells);
        Assert.Equal(lv.TotalVolume, lv.Levels.Sum(l => l.Volume) + lv.OutOfRangeVolume, 6);
        var meta = BlockVoxelBuilder.ToBlockModel(r, "vox");
        Assert.Equal(125, meta.Blocks.Count); Assert.False(meta.IsRegular); Assert.Equal(0, meta.SubCellCount);
        Assert.Equal(2, meta.Sx, 9);
        string csv = BlockVoxelBuilder.RenderCsv(r, lv, "vox");
        Assert.Contains("summary,total_volume_m3,1000", csv);
        Assert.Contains("0,4,50,400", csv);
        Assert.Contains("<h1>体素格网体积报表: vox</h1>", BlockVoxelBuilder.RenderHtml(r, lv, "vox"));
    }

    [Fact]
    public void VoxelBuilder_adaptive_sphere_closer_than_uniform()
    {
        var (v, t) = PrimitiveBodies.Sphere(0, 0, 0, 5, 24, 32);
        var sph = BlockVoxelBuilder.FromMesh("s", v, t);
        double exact = sph.ExactVolume;
        Assert.InRange(exact, 480, 524);
        var uni = BlockVoxelBuilder.Build(new[] { sph }, 2, 2, 2, out _)!;
        var ada = BlockVoxelBuilder.Build(new[] { sph }, 2, 2, 2, out _, depth: 2, sampleN: 3)!;
        Assert.NotEmpty(ada.SubCells);
        Assert.NotEmpty(ada.SubBlockedParents);
        Assert.True(Math.Abs(ada.TotalVolume - exact) < Math.Abs(uni.TotalVolume - exact));
        Assert.InRange(ada.TotalVolume / exact, 0.9, 1.1);
        var meta = BlockVoxelBuilder.ToBlockModel(ada, "s");
        Assert.Equal(ada.SubCells.Count, meta.SubCellCount);
        Assert.True(meta.HasData("percent"));
        Assert.Equal(ada.KeepCount + ada.SubCells.Count, meta.Blocks.Count);
        Assert.Equal(ada.TotalVolume, meta.Blocks.Select((b, i) => (b.Size < meta.Sx - 1e-9 ? b.Size * b.Size * b.Size : meta.CellVolume) * meta.GetValue("percent", i)).Sum(), 3);
    }

    [Fact]
    public void VoxelBuilder_rejects_bad_input()
    {
        Assert.Null(BlockVoxelBuilder.Build(Array.Empty<BlockVoxelBuilder.MeshInput>(), 1, 1, 1, out var err));
        Assert.Contains("无有效", err);
        var box = Box(0, 0, 0, 1000, 1000, 1000);
        Assert.Null(BlockVoxelBuilder.Build(new[] { box }, 1, 1, 1, out err));
        Assert.Contains("超上限", err);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, BlockVoxelBuilder.NormalizeLevels(new[] { 3.0, 1.0, 2.0, 1.0000001 }).ToArray());
    }

    // ── 煤质联动（真库种子）──

    [Fact]
    public void CoalQualityLink_estimates_from_seed_samples()
    {
        using var db = TestDb.Open();
        var samples = GeoDataQueries.GetCoalSamples(db.Connection);
        Assert.NotEmpty(samples);
        var (col, get, _) = BlockCoalQualityLink.Indicator("ad", false);
        Assert.Equal("Ad", col);
        var seam = samples.Where(s => s.Z.HasValue && get(s).HasValue).GroupBy(s => s.SeamCode).OrderByDescending(g => g.Count()).First().Key;
        var ctrl = BlockCoalQualityLink.ControlPoints(samples, seam, get);
        Assert.True(ctrl.Count >= 3);
        double minX = ctrl.Min(p => p.X), maxX = ctrl.Max(p => p.X), minY = ctrl.Min(p => p.Y), maxY = ctrl.Max(p => p.Y), minZ = ctrl.Min(p => p.Z), maxZ = ctrl.Max(p => p.Z);
        var m = BlockModelMeta.CreateRegular("coal", minX, minY, minZ - 1, Math.Max(1, (maxX - minX) / 4), Math.Max(1, (maxY - minY) / 4), Math.Max(1, (maxZ - minZ + 2) / 2), 4, 4, 2);
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "seam", IsCategorical = true });
        m.SetConstant("seam", 1);
        var dv = BlockCoalQualityLink.DistinctValues(m, "seam");
        Assert.Single(dv); Assert.Equal(32, dv[0].Count);
        var mapping = new[] { new BlockCoalQualityLink.SeamMapping(1, seam) };
        Assert.Equal(32, BlockCoalQualityLink.PreviewCells(m, "seam", mapping)[0].Cells);
        var res = BlockCoalQualityLink.Estimate(m, "seam", mapping, new[] { "ad" }, false, "IDW", samples);
        Assert.True(res.Ok, res.Message);
        Assert.Equal(32, res.CoalCells);
        int written = res.Columns[0].Written;
        Assert.True(written > 0);
        double lo = ctrl.Min(p => p.V), hi = ctrl.Max(p => p.V);
        var ad = m.GetAttr("Ad")!;
        for (int i = 0; i < ad.Length; i++) if (ad[i] != 0) Assert.InRange(ad[i], lo - 1e-9, hi + 1e-9);   // IDW 不越样本值域
        Assert.Equal("煤质可信度", res.ReliabilityColumn);
        Assert.Equal(32, res.RelHigh + res.RelMid + res.RelLow);
        var rel = m.GetAttr("煤质可信度")!;
        Assert.All(rel, v => Assert.InRange(v, 0, 1));
        // 最近邻：块值恰等于某样本值
        var nn = BlockCoalQualityLink.Estimate(m, "seam", mapping, new[] { "ad" }, false, "最近邻", samples);
        Assert.True(nn.Ok);
        Assert.Contains(m.GetAttr("Ad")![0], ctrl.Select(p => p.V));
        // 反向采样 + 相关
        var b0 = m.Blocks[0];
        Assert.Equal(m.GetValue("Ad", 0), BlockCoalQualityLink.BlockValueAt(m, b0.X, b0.Y, b0.Z, "Ad"));
        Assert.Null(BlockCoalQualityLink.BlockValueAt(m, minX - 1000, minY, b0.Z, "Ad"));
        Assert.Equal(1, BlockCoalQualityLink.Pearson(new[] { 1.0, 2, 3 }, new[] { 2.0, 4, 6 }), 9);
    }

    [Fact]
    public void CoalQualityLink_kriging_writes_sigma_column()
    {
        using var db = TestDb.Open();
        var samples = GeoDataQueries.GetCoalSamples(db.Connection);
        var (_, get, _) = BlockCoalQualityLink.Indicator("ad", false);
        var seam = samples.Where(s => s.Z.HasValue && get(s).HasValue).GroupBy(s => s.SeamCode).OrderByDescending(g => g.Count()).First().Key;
        var ctrl = BlockCoalQualityLink.ControlPoints(samples, seam, get);
        double minX = ctrl.Min(p => p.X), maxX = ctrl.Max(p => p.X), minY = ctrl.Min(p => p.Y), maxY = ctrl.Max(p => p.Y), minZ = ctrl.Min(p => p.Z), maxZ = ctrl.Max(p => p.Z);
        var m = BlockModelMeta.CreateRegular("coal", minX, minY, minZ - 1, Math.Max(1, (maxX - minX) / 3), Math.Max(1, (maxY - minY) / 3), Math.Max(1, (maxZ - minZ + 2)), 3, 3, 1);
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "seam" });
        m.SetConstant("seam", 4);
        var res = BlockCoalQualityLink.Estimate(m, "seam", new[] { new BlockCoalQualityLink.SeamMapping(4, seam) }, new[] { "ad", "qnet" }, false, "克里金 (OK)", samples);
        Assert.True(res.Ok, res.Message);
        Assert.Equal(2, res.Columns.Count);
        Assert.Contains(res.VarianceColumns, c => c.Column == "Ad_σ");
        Assert.All(m.GetAttr("Ad_σ")!, v => Assert.True(v >= 0));
        Assert.Contains("已写入属性列", res.Message);
        var bad = BlockCoalQualityLink.Estimate(m, "nope", new[] { new BlockCoalQualityLink.SeamMapping(4, seam) }, new[] { "ad" }, false, "IDW", samples);
        Assert.False(bad.Ok);
    }
}
