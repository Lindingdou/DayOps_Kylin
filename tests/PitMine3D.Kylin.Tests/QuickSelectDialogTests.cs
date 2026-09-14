using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 快速选择对话框（§三二二）的数据源：类型/图层候选只列**范围里真有的**。
/// 列图上根本没有的类型或层名，用户选中后只能得到空集 —— 那是白给一次挫败（原版注释的用意）。
/// </summary>
public class QuickSelectDialogTests
{
    private static Scene SampleScene()
    {
        var s = new Scene();
        s.Add(new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0, LayerName = "台阶线" });
        s.Add(new LineEntity { X0 = 0, Y0 = 5, X1 = 10, Y1 = 5, LayerName = "台阶线" });
        s.Add(new CircleEntity { Cx = 3, Cy = 3, Radius = 8, LayerName = "钻孔" });
        s.Add(new CircleEntity { Cx = 9, Cy = 9, Radius = 2, LayerName = "钻孔" });
        s.Add(new TextEntity { X = 1, Y = 1, Height = 2, Text = "标高 1200", LayerName = "0" });
        return s;
    }

    [Fact]
    public void 类型候选只列范围里真有的()
    {
        var snaps = QuickSelectSnapshot.FromScene(SampleScene().Entities.ToList());
        var ids = QuickSelectSnapshot.PresentTypeIds(snaps).ToList();

        Assert.Contains(QuickSelectCatalog.TypeLine, ids);
        Assert.Contains(QuickSelectCatalog.TypeCircle, ids);
        Assert.Contains(QuickSelectCatalog.TypeText, ids);
        // 图上没有的类型不该出现在下拉里(选中只能得空集)
        Assert.DoesNotContain(QuickSelectCatalog.TypeArc, ids);
        Assert.DoesNotContain(QuickSelectCatalog.TypePolyline, ids);
        Assert.DoesNotContain(QuickSelectCatalog.TypePoint, ids);
    }

    [Fact]
    public void 类型候选去重且按目录顺序()
    {
        var snaps = QuickSelectSnapshot.FromScene(SampleScene().Entities.ToList());
        var ids = QuickSelectSnapshot.PresentTypeIds(snaps).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());   // 两条直线只出一项
        Assert.Equal(3, ids.Count);
        var order = QuickSelectCatalog.AllTypeIds().ToList();
        var ranks = ids.Select(i => order.IndexOf(i)).ToList();
        Assert.Equal(ranks.OrderBy(x => x).ToList(), ranks);   // 顺序跟着目录, 不随实体入场顺序抖
    }

    [Fact]
    public void 图层候选去重排序且跳过空名()
    {
        var scene = SampleScene();
        scene.Add(new PointEntity { X = 0, Y = 0, LayerName = "" });   // 空层名不进候选
        var snaps = QuickSelectSnapshot.FromScene(scene.Entities.ToList());
        var names = QuickSelectSnapshot.PresentLayerNames(snaps).ToList();

        Assert.Equal(new[] { "0", "台阶线", "钻孔" }, names.OrderBy(x => x, System.StringComparer.Ordinal).ToArray());
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.DoesNotContain("", names);
    }

    [Fact]
    public void 空场景两个候选都返回空而不是崩()
    {
        Assert.Empty(QuickSelectSnapshot.PresentTypeIds(null));
        Assert.Empty(QuickSelectSnapshot.PresentLayerNames(null));
        Assert.Empty(QuickSelectSnapshot.PresentTypeIds(new EntitySnapshot[0]));
        Assert.Empty(QuickSelectSnapshot.PresentLayerNames(new EntitySnapshot[0]));
    }

    // ── 对话框攒出来的条件, 交给同一个过滤核跑出来要对 ──────────────────
    [Fact]
    public void 对话框条件_按半径筛圆()
    {
        var pool = SampleScene().Entities.ToList();
        var crit = new QuickSelectCriteria
        {
            TypeId = QuickSelectCatalog.TypeCircle,
            PropertyKey = "radius", Operator = QuickSelectOperator.Greater, Value = "5",
        };
        var res = QuickSelectFilter.Apply(QuickSelectSnapshot.FromScene(pool), crit);
        Assert.Single(res.Handles);
        Assert.IsType<CircleEntity>(pool[(int)res.Handles[0]]);
        Assert.Equal(8, ((CircleEntity)pool[(int)res.Handles[0]]).Radius);
    }

    [Fact]
    public void 对话框条件_排除模式取补集()
    {
        var pool = SampleScene().Entities.ToList();
        var inc = new QuickSelectCriteria { PropertyKey = "layer", Operator = QuickSelectOperator.Equals, Value = "钻孔" };
        var exc = new QuickSelectCriteria { PropertyKey = "layer", Operator = QuickSelectOperator.Equals, Value = "钻孔",
                                            ApplyMode = QuickSelectApplyMode.Exclude };
        int nInc = QuickSelectFilter.Apply(QuickSelectSnapshot.FromScene(pool), inc).Handles.Length;
        int nExc = QuickSelectFilter.Apply(QuickSelectSnapshot.FromScene(pool), exc).Handles.Length;
        Assert.Equal(2, nInc);
        Assert.Equal(pool.Count - nInc, nExc);   // 包括 + 排除 = 全体, 没有漏也没有重
    }

    [Fact]
    public void 对话框条件_运算符全部选择时值不参与()
    {
        // 「值」栏在这一档是禁用的; 就算里面残留了字, 结果也该是该类型全选
        var pool = SampleScene().Entities.ToList();
        var crit = new QuickSelectCriteria
        {
            TypeId = QuickSelectCatalog.TypeCircle,
            PropertyKey = "radius", Operator = QuickSelectOperator.All, Value = "残留的旧值",
        };
        var res = QuickSelectFilter.Apply(QuickSelectSnapshot.FromScene(pool), crit);
        Assert.Equal(2, res.Handles.Length);
    }

    [Fact]
    public void 图层候选喂回过滤核能选中()
    {
        // 下拉里挑出来的层名, 原样丢给过滤核必须命中 —— 两边对层名的口径要一致
        var pool = SampleScene().Entities.ToList();
        var snaps = QuickSelectSnapshot.FromScene(pool);
        foreach (var name in QuickSelectSnapshot.PresentLayerNames(snaps))
        {
            var crit = new QuickSelectCriteria { PropertyKey = "layer", Operator = QuickSelectOperator.Equals, Value = name };
            Assert.NotEmpty(QuickSelectFilter.Apply(snaps, crit).Handles);
        }
    }
}
