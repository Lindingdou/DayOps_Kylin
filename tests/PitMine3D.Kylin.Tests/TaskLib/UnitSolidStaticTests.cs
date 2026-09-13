// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/UnitSolidStaticTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Platform.Capabilities;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「在图上看三维」（<see cref="UnitSolidStage.BuildStatic"/>）的判据。
///
/// <para><b>这条路存在的理由就是 B1</b>：基表的「期次」「完成度」两列本来是空的
/// （要跑过「按目标排产」才写）。分帧那条 <see cref="UnitSolidStage.Build"/> 靠这两列定第几帧，
/// 空的就一个体都建不出来 —— 现场原话是「没有给期次序列（PeriodKeys），定不了每个单元落在第几帧 → 不建体」。
/// B1 和 B8 是一对：同一批行，静态能建、分帧建不出来。B8 一旦变绿，说明 BuildStatic 是多余的。</para>
/// </summary>
public class UnitSolidStaticTests
{
    // ── 假的实体能力：只把建体真正用到的那几个方法做实，其余抛"没用到" ──
    private sealed class FakeEntities : IEntityCapability
    {

        /// <summary>批量取图层名。<b>判据不判图层</b>，一律返回空串（= 取不到），
        /// 与接口约定一致：取不到的位置为空串，而不是编一个层名出来。</summary>
        public string[] GetEntityLayers(ulong[] handles)
        {
            if (handles == null) return System.Array.Empty<string>();
            var r = new string[handles.Length];
            for (int i = 0; i < r.Length; i++) r[i] = string.Empty;
            return r;
        }
        /// <summary>
        /// 三角网的类型码。<b>必须与 <c>UnitSolidStage.TypeIdTriangleMesh</c> 一致</b>（那边是 private const 6）。
        /// 这里第一版写成 3，结果 TryResolveSource 找不到样式源，8 条判据全红成同一句
        /// 「文档中无三角网可作样式源」—— 假件对不上真值时，判据测的是假件不是被测代码。
        /// </summary>
        public const int TypeIdTriangleMesh = 6;

        /// <summary>文档里已有的实体（样式源从这里挑）。默认给一张 mesh。</summary>
        public List<ulong> Existing = new() { 100UL };
        public List<int> ExistingTypes = new() { TypeIdTriangleMesh };

        /// <summary>建出来的体：handle → 图层。</summary>
        public readonly Dictionary<ulong, string> Built = new();
        public readonly List<ulong> Deleted = new();
        private ulong _next = 1000;

        public int Count => Existing.Count + Built.Count;

        public ulong[] GetAllHandles() => Existing.ToArray();

        public int[] GetEntityTypes(ulong[] handles)
        {
            var r = new int[handles.Length];
            for (int i = 0; i < handles.Length; i++)
            {
                int k = Existing.IndexOf(handles[i]);
                r[i] = k >= 0 && k < ExistingTypes.Count ? ExistingTypes[k] : 0;
            }
            return r;
        }

        public bool TryGetEntityAABB(ulong handle, out double[] min, out double[] max)
        {
            min = new double[3]; max = new double[3];
            return Existing.Contains(handle) || Built.ContainsKey(handle);
        }

        public ulong[] GetHandlesByLayer(string? layerName)
            => Built.Where(kv => kv.Value == layerName).Select(kv => kv.Key).ToArray();

        public int DeleteEntities(ulong[] handles)
        {
            int n = 0;
            foreach (var h in handles) if (Built.Remove(h)) { Deleted.Add(h); n++; }
            return n;
        }

        public ulong BuildColoredMeshOnLayer(ulong srcHandle, double[] worldXyz, uint[] triangles,
                                             uint[] vertexRgb, double lift, string layerName)
        {
            if (worldXyz == null || worldXyz.Length < 9 || triangles == null || triangles.Length < 3) return 0;
            ulong h = ++_next;
            Built[h] = layerName;
            return h;
        }

        // ── 以下建体用不到；被调到说明实现漂了，直接炸而不是悄悄返回空 ──
        private static T Nope<T>() => throw new NotSupportedException("BuildStatic 不该调这个方法");
        public ulong[] GetSelectedHandles() => Nope<ulong[]>();
        public int SetEntitiesVisible(ulong[] handles, bool visible) => Nope<int>();
        public bool TryGetMeshGeometry(ulong h, out double[] v, out int[] t)
        { v = Array.Empty<double>(); t = Array.Empty<int>(); return Nope<bool>(); }
        public bool TryGetPolylineWorldVertices(ulong h, out double[] x, out bool c)
        { x = Array.Empty<double>(); c = false; return Nope<bool>(); }
        public byte[] BuildConstrainedDelaunayFromSelectionBinary() => Nope<byte[]>();
        public ulong CdtCollectSelection() => Nope<ulong>();
        public int CdtJobPointCount(ulong token) => Nope<int>();
        public void CdtComputeToken(ulong token) => Nope<int>();
        public byte[] CdtTakeResultBinary(ulong token) => Nope<byte[]>();
        public void CdtDiscard(ulong token) => Nope<int>();
        public int AddPointsBatch(double[] xyzFlat) => Nope<int>();
        public int AssignZByFormulaToSelection(double a, double b, double c) => Nope<int>();
        public int SetPointStyle(ulong[] h, int style, double size) => Nope<int>();
        public bool ImportEntitiesBinary(byte[] pmbi) => Nope<bool>();
        public ulong BuildRecoloredMeshOnLayer(ulong s, uint[] vi, uint[] rgb, double lift, string lay) => Nope<ulong>();
    }

    /// <summary>假的视图能力：只记下收到过哪些命令，别的一律不支持。</summary>
    private sealed class FakeView : IViewCapability
    {
        public readonly List<string> Commands = new();
        public int Renders;

        public bool ExecuteCommand(string commandLine) { Commands.Add(commandLine); return true; }
        public void RequestRender() => Renders++;

        // 建体只用 ExecuteCommand / RequestRender；其余被调到说明实现漂了，直接炸而不是悄悄成功
        private static T Nope<T>() => throw new NotSupportedException("建体不该调这个方法");
        public bool StartTool(string toolName) => Nope<bool>();
        public string[] GetAvailableToolNames() => Nope<string[]>();
        public void CancelActiveCommand() => Nope<int>();
        public bool IsOrthoEnabled => Nope<bool>();
        public void SetOrthoEnabled(bool e) => Nope<int>();
        public bool IsSnapEnabled => Nope<bool>();
        public void SetSnapEnabled(bool e) => Nope<int>();
        public bool Is3DViewEnabled => Nope<bool>();
        public void Set3DViewEnabled(bool e) => Nope<int>();
        public bool IsFillModeEnabled => Nope<bool>();
        public void SetFillModeEnabled(bool e) => Nope<int>();
        public int GetShadingMode() => Nope<int>();
        public void SetShadingMode(int mode) => Nope<int>();
        public void SetContourSpacing(float spacing) => Nope<int>();
        public void SetValueRange(float lo, float hi) => Nope<int>();
        public void SetColormap(byte[] rgba) => Nope<int>();
        public bool SetOrthophoto(byte[] b, int w, int h, double a, double c, double d, double e) => Nope<bool>();
        public void ClearOrthophoto() => Nope<int>();
        public bool HasOrthophoto() => Nope<bool>();
        public int GetMaxOrthophotoDimension() => Nope<int>();
        public bool IsSurfaceCoordinateReadoutEnabled => Nope<bool>();
        public void SetSurfaceCoordinateReadoutEnabled(bool e) => Nope<int>();
    }

    /// <summary>造一行"基表长相"的台账行：几何齐、<b>期次和完成度都是空的</b>。</summary>
    private static MiningUnitLedger.Row BaseRow(string id, LedgerKind kind = LedgerKind.Coal,
                                                double len = 98, double wid = 40, double thick = 12)
        => new()
        {
            UnitId = id, Kind = kind, Region = "采场1", Seam = "4",
            Cx = 621908, Cy = 4381658, Cz = 1255,
            ZLo = 1255 - thick / 2, ZHi = 1255 + thick / 2,
            LengthM = len, WidthM = wid, ThickM = thick,
            Period = "", Done = 0,          // ★ 基表就是这样：两列都空
        };

    private static (UnitSolidStage stage, FakeEntities ent) NewStage()
    {
        var ent = new FakeEntities();
        return (new UnitSolidStage(ent), ent);
    }

    // ═══ B1：期次/完成度全空也要建得出来 —— 这条路存在的理由 ═══
    [Fact]
    public void B1_期次和完成度全空也能建出体()
    {
        var (stage, ent) = NewStage();
        var rows = new List<MiningUnitLedger.Row> { BaseRow("4-B3-P27"), BaseRow("4-B3-P28") };

        var res = stage.BuildStatic(rows);

        Assert.True(res.Ok, "静态视图必须无视期次/完成度建出体：" + res.Summary);
        Assert.Equal(2, res.BuiltUnitCount);
        Assert.Equal(2, res.BuiltBodyCount);
        Assert.Equal(2, ent.Built.Count);
    }

    // ═══ B8：同一批行走分帧那条【必须】建不出来 —— B1 的对照 ═══
    [Fact]
    public void B8_同一批行走分帧那条建不出来()
    {
        var (stage, _) = NewStage();
        var rows = new List<MiningUnitLedger.Row> { BaseRow("4-B3-P27"), BaseRow("4-B3-P28") };

        // 给了期次序列，但行的 Period 是空的 —— 匹配不上任何一帧
        var res = stage.Build(rows, new UnitSolidStageOptions { PeriodKeys = new[] { "2026-08" } });

        Assert.False(res.Ok);
        Assert.Equal(0, res.BuiltBodyCount);
        // 这条一旦变绿，说明分帧那条自己能处理空期次 ⇒ BuildStatic 就是多余的，该删
    }

    // ═══ B2：一个 UnitId 只建一个体（跨期多行不重复建）═══
    [Fact]
    public void B2_同号多行只建一个体并记账()
    {
        var (stage, ent) = NewStage();
        var a = BaseRow("4-B3-P27"); a.Period = "2026-08";
        var b = BaseRow("4-B3-P27"); b.Period = "2026-09";     // 同号，跨期第二行
        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { a, b });

        Assert.Equal(1, res.PlannedUnitCount);
        Assert.Equal(1, res.BuiltBodyCount);
        Assert.Single(ent.Built);
        Assert.Contains(res.Messages, m => m.Contains("同号"));   // 让步要说出来
    }

    // ═══ B3：几何不成立的行跳过并给出理由，不拿缺省值冒充 ═══
    [Theory]
    [InlineData(0, 40, 12)]     // 走向长为 0
    [InlineData(98, 0, 12)]     // 推进宽为 0
    public void B3_几何不成立就跳过并说明(double len, double wid, double thick)
    {
        var (stage, ent) = NewStage();
        var bad = BaseRow("坏行", LedgerKind.Coal, len, wid, thick);
        var good = BaseRow("好行");

        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { bad, good });

        Assert.Equal(2, res.PlannedUnitCount);
        Assert.Equal(1, res.BuiltBodyCount);              // 只有好行建出来
        Assert.Equal(1, res.SkippedUnitCount);
        Assert.Single(ent.Built);
        Assert.Contains(res.Messages, m => m.Contains("坏行"));
    }

    [Fact]
    public void B3b_厚度为零也建不出来不许拿缺省厚度顶上()
    {
        var (stage, _) = NewStage();
        var r = BaseRow("零厚");
        r.ZLo = r.ZHi = 1255; r.ThickM = 0;

        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { r });

        Assert.False(res.Ok);
        Assert.Contains(res.Messages, m => m.Contains("厚度") || m.Contains("冒充"));
    }

    // ═══ B4：没有样式源 → **照建 + 留条**，不是拒绝 ═══
    //
    // ⚠ 本条在 2026-08-10 反转过，原来断言的是「明确拒绝」。反转的理由（不是放宽标准）：
    //   那道闸是 C# 侧自己加的，**内核从来没要求过样式源** ——
    //   PitMine_BuildColoredMeshOnLayer（xllAcEd.cpp:8860-8868）解不出 srcHandle 时
    //   basePoint 退回本网第一个顶点、baseColor 退回白色；而单元体逐顶点上色，baseColor 用不到，
    //   basePoint 用自己的第一个顶点在精度上还更好（偏移量更小）。
    //   现场后果：空文档里点「建单元体」→ 97 行几何全齐的台账一个体都建不出来，
    //   而弹出来的原因指向"去导入一张现状面"——一个与真实障碍无关的方向。
    //   ⇒ 「拒绝」这条规则本身是错的，不是判据太严。详见 UnitSolidNoStyleSourceTests。
    [Fact]
    public void B4_没有样式源时照建并留条()
    {
        var ent = new FakeEntities { Existing = new List<ulong>(), ExistingTypes = new List<int>() };
        var stage = new UnitSolidStage(ent);

        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { BaseRow("A") });

        Assert.True(res.Ok, "空文档里建不出来了 —— 那道假闸又回来了。" + res.Summary);
        Assert.True(res.BuiltBodyCount > 0);
        // 降级不许静默：basePoint 换了口径，必须说出来
        Assert.Contains(res.Messages, m => m.Contains("没有样式源"));
    }

    // ═══ B5：形态是盒子还是真轨必须说出来 ═══
    [Fact]
    public void B5_全是盒子时必须在标签里说出近似()
    {
        var (stage, _) = NewStage();
        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { BaseRow("A"), BaseRow("B") });

        // 会话内没有真轨（测试进程从没跑过采矿模型）⇒ 全部盒子
        Assert.Equal(2, res.BoxUnits);
        Assert.Contains("盒子", res.GeometrySourceLabel);
        Assert.Contains("近似", res.GeometrySourceLabel);
    }

    // ═══ B6：采场与排土落在各自图层（清理是按层整批删的，串层就会互删）═══
    [Fact]
    public void B6_采场与排土各落各的图层()
    {
        var (stage, ent) = NewStage();
        var res = stage.BuildStatic(new List<MiningUnitLedger.Row>
        {
            BaseRow("煤A", LedgerKind.Coal),
            BaseRow("岩A", LedgerKind.Rock),
            BaseRow("排A", LedgerKind.Dump),
        });

        Assert.Equal(3, res.BuiltBodyCount);
        var layers = ent.Built.Values.ToList();
        Assert.Equal(2, layers.Count(l => l == UnitSolidStage.LayerUnitPit));    // 煤 + 岩
        Assert.Equal(1, layers.Count(l => l == UnitSolidStage.LayerUnitDump));   // 排土
    }

    // ═══ B7：再建一次要先清掉上一批，不能越堆越多 ═══
    [Fact]
    public void B7_重复建体先清旧的()
    {
        var (stage, ent) = NewStage();
        var rows = new List<MiningUnitLedger.Row> { BaseRow("A"), BaseRow("B") };

        stage.BuildStatic(rows);
        Assert.Equal(2, ent.Built.Count);

        var res2 = stage.BuildStatic(rows);
        Assert.Equal(2, ent.Built.Count);          // 还是 2，不是 4
        Assert.Equal(2, res2.Deleted);             // 清了 2 个旧的，并记了账
    }

    // ═══ V1–V4：相机与位置 —— "建了 2312 个体"和"屏幕全黑"必须不再兼容 ═══

    [Fact]
    public void V1_建出体后发一次ZOOMEXTENTS并说明框的是全图()
    {
        var (stage, _) = NewStage();
        var view = new FakeView();

        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { BaseRow("A") },
                                    new UnitSolidStage.StaticOptions { View = view });

        Assert.True(res.Ok);
        Assert.Equal(1, view.Commands.Count(c => c == "ZOOMEXTENTS"));
        // 措辞必须说清框的是全图 —— 内核没有 zoom-to-AABB，写"已缩放到位"就是骗人
        Assert.Contains(res.Messages, m => m.Contains("全图"));
    }

    [Fact]
    public void V2_关掉缩放闸就真的不发()
    {
        var (stage, _) = NewStage();
        var view = new FakeView();

        stage.BuildStatic(new List<MiningUnitLedger.Row> { BaseRow("A") },
            new UnitSolidStage.StaticOptions { View = view, ZoomExtentsAfterBuild = false });

        Assert.Empty(view.Commands);      // 关掉规则闸必须真的关掉
    }

    [Fact]
    public void V3_没建出体就不发也不报已发()
    {
        var (stage, _) = NewStage();
        var view = new FakeView();

        // 几何不成立 ⇒ 0 个体
        var bad = BaseRow("坏", LedgerKind.Coal, len: 0);
        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { bad },
                                    new UnitSolidStage.StaticOptions { View = view });

        Assert.False(res.Ok);
        Assert.Empty(view.Commands);
        Assert.DoesNotContain(res.Messages, m => m.Contains("已发"));
    }

    [Fact]
    public void V4_位置标签给出真实跨度()
    {
        var (stage, _) = NewStage();
        var a = BaseRow("A");
        var b = BaseRow("B");
        b.Cx = a.Cx + 5000;               // 往东挪 5 km

        var res = stage.BuildStatic(new List<MiningUnitLedger.Row> { a, b });

        Assert.True(res.HasBounds);
        Assert.Equal(2, res.BuiltBodyCount);
        // 两体各宽 98m（走向长），质心相距 5000 ⇒ 总跨度 ≈ 5098
        Assert.Contains("跨度", res.LocationLabel);
        Assert.Matches(@"跨度 50\d\d ×", res.LocationLabel);

        // ★ 这一条是 NaN 哨兵的理由：若 MinX 的哨兵用 0，漏填时跨度会变成 62 万 ——
        //   一个看着"正常"的大数，不会让任何判据变红。用 NaN 则漏填直接掉出 Located，
        //   HasBounds 变 false，与 BuiltBodyCount>0 矛盾，当场红。
        Assert.DoesNotContain("没建出体", res.LocationLabel);
    }

    [Fact]
    public void V5_一个体都没有时位置标签不炸()
    {
        var (stage, _) = NewStage();
        var res = stage.BuildStatic(new List<MiningUnitLedger.Row>());
        Assert.False(res.HasBounds);
        Assert.Equal("位置：没建出体。", res.LocationLabel);   // 空序列上 Min() 会抛，必须先判 HasBounds
    }

    // ═══ P1–P5：纯几何预览（三维预览对话框走这条）═══

    [Fact]
    public void P1_预览不需要任何能力也不碰内核()
    {
        // 注意：没有 FakeEntities、没有 stage 实例 —— 静态方法直接调
        var res = UnitSolidStage.BuildPreview(
            new List<MiningUnitLedger.Row> { BaseRow("A"), BaseRow("B") });

        Assert.True(res.Ok);
        Assert.Equal(2, res.Bodies.Count);
        // 几何真的在内存里，不是空壳
        Assert.All(res.Bodies, b => Assert.True(b.Xyz.Length >= 9 && b.Tris.Length >= 12));
    }

    [Fact]
    public void P2_预览和入图必须给出同一份几何()
    {
        // ★ 这是"预览不许比入图好看"的判据：同一批行，两条路的体数/体积/包围盒必须一致。
        //   两边各算一遍几何时，最容易出现的错就是预览显示"全部真轨"而入图是一堆盒子。
        var rows = new List<MiningUnitLedger.Row>
        {
            BaseRow("煤A", LedgerKind.Coal),
            BaseRow("岩A", LedgerKind.Rock),
            BaseRow("排A", LedgerKind.Dump),
            BaseRow("坏", LedgerKind.Coal, len: 0),        // 几何不成立，两边都该跳过
        };

        var pre = UnitSolidStage.BuildPreview(rows);
        var (stage, _) = NewStage();
        var built = stage.BuildStatic(rows);

        Assert.Equal(pre.Bodies.Count, built.BuiltBodyCount);
        Assert.Equal(pre.PlannedUnitCount, built.PlannedUnitCount);
        Assert.Equal(pre.SkippedUnitIds.Count, built.SkippedUnitCount);
        Assert.Equal(pre.BoxBodies, built.BoxUnits);
        Assert.Equal(pre.RealRailBodies, built.RealRailUnits);

        double vp = pre.Bodies.Sum(b => Math.Abs(b.VolumeM3));
        double vb = built.Units.SelectMany(u => u.Bodies).Sum(b => Math.Abs(b.VolumeM3));
        Assert.Equal(vp, vb, 6);        // 同一份几何 ⇒ 体积应当逐位相同，不是"接近"
    }

    [Fact]
    public void P3_预览也要跳过几何不成立的行并说明()
    {
        var res = UnitSolidStage.BuildPreview(new List<MiningUnitLedger.Row>
        {
            BaseRow("好"), BaseRow("坏", LedgerKind.Coal, wid: 0),
        });

        Assert.Single(res.Bodies);
        Assert.Single(res.SkippedUnitIds);
        Assert.Contains("坏", res.SkippedUnitIds);
        Assert.Contains(res.Messages, m => m.Contains("坏"));
    }

    [Fact]
    public void P4_预览的包围盒能算出真实跨度()
    {
        var a = BaseRow("A");
        var b = BaseRow("B"); b.Cx = a.Cx + 5000;

        var res = UnitSolidStage.BuildPreview(new List<MiningUnitLedger.Row> { a, b });

        Assert.Equal(2, res.Bodies.Count);
        Assert.All(res.Bodies, x => Assert.False(double.IsNaN(x.MinX)));
        double span = res.Bodies.Max(x => x.MaxX) - res.Bodies.Min(x => x.MinX);
        Assert.InRange(span, 5000, 5200);
    }

    [Fact]
    public void P5_预览空输入不炸并说明()
    {
        Assert.False(UnitSolidStage.BuildPreview(null).Ok);
        var res = UnitSolidStage.BuildPreview(new List<MiningUnitLedger.Row>());
        Assert.False(res.Ok);
        Assert.Contains(res.Messages, m => m.Contains("没有"));
    }

    // ═══ B9：空输入不炸，且说得出是空的 ═══
    [Fact]
    public void B9_空输入给出说明而不是异常()
    {
        var (stage, _) = NewStage();
        var res = stage.BuildStatic(new List<MiningUnitLedger.Row>());
        Assert.False(res.Ok);
        Assert.Contains("没有", res.Summary);

        var res2 = stage.BuildStatic(null);
        Assert.False(res2.Ok);
    }
}
