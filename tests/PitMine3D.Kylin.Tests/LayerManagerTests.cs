using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 图层特性管理器（§三二〇）：图层新增的四列（线宽/透明度/打印/说明）、「随层」解析、
/// 存档往返，以及"改了图层透明度必须穿透镶嵌缓存"这条 —— 最后一条是本组最要紧的：
/// 不带它，用户在管理器里改完什么都不变，看上去就是"设了不生效"。
/// </summary>
public class LayerManagerTests
{
    private static MeshEntity Quad(string layer, short transp)
    {
        // 一张两三角的方片，够走面/材质面两条通道
        var m = new MeshEntity { LayerName = layer, Transparency = transp };
        m.Verts.Add((0, 0, 0)); m.Verts.Add((10, 0, 0)); m.Verts.Add((10, 10, 0)); m.Verts.Add((0, 10, 0));
        m.Tris.Add((0, 1, 2)); m.Tris.Add((0, 2, 3));
        m.RenderModeOverride = MeshEntity.DisplayMode.Shaded;   // 不吃全局档, 免得跟别的测试互相串
        return m;
    }

    // ── 随层解析 ──────────────────────────────────────────────
    [Fact]
    public void 透明度随层_实体minus1时取图层值()
    {
        var t = new LayerTable();
        t.New("煤层");
        t.Get("煤层")!.Transparency = 40;
        Assert.Equal(40, t.EffectiveTransparency(-1, "煤层"));
    }

    [Fact]
    public void 透明度随层_实体自带值时压过图层()
    {
        var t = new LayerTable();
        t.New("煤层");
        t.Get("煤层")!.Transparency = 40;
        Assert.Equal(20, t.EffectiveTransparency(20, "煤层"));   // 实体特性优先, 同 AutoCAD
    }

    [Fact]
    public void 透明度随层_未知图层按不透明_且夹到0到90()
    {
        var t = new LayerTable();
        Assert.Equal(0, t.EffectiveTransparency(-1, "查无此层"));
        t.New("A");
        t.Get("A")!.Transparency = 300;                          // 越界值不该漏出去
        Assert.Equal(90, t.EffectiveTransparency(-1, "A"));
        Assert.Equal(90, t.EffectiveTransparency(120, "A"));
    }

    [Fact]
    public void 线宽随层_minus1取图层_随块原样_未知层默认()
    {
        var t = new LayerTable();
        t.New("边界");
        t.Get("边界")!.LineWeight = 50;                           // 0.50mm
        Assert.Equal(50, t.EffectiveLineWeight(-1, "边界"));
        Assert.Equal(30, t.EffectiveLineWeight(30, "边界"));      // 实体自带
        Assert.Equal(-2, t.EffectiveLineWeight(-2, "边界"));      // 随块: Kylin 无块表, 不臆造解析
        Assert.Equal(-3, t.EffectiveLineWeight(-1, "查无此层"));  // 未知层 → 默认
    }

    [Fact]
    public void 打印列_默认打印_未知层按打印()
    {
        var t = new LayerTable();
        Assert.True(t.IsPlottable("0"));
        t.New("辅助");
        t.Get("辅助")!.Plottable = false;
        Assert.False(t.IsPlottable("辅助"));
        Assert.True(t.IsPlottable("查无此层"));
    }

    // ── 存档往返 ──────────────────────────────────────────────
    [Fact]
    public void 存档往返_四列都保住()
    {
        var t = new LayerTable();
        t.New("煤层");
        var l = t.Get("煤层")!;
        l.LineWeight = 53; l.Transparency = 35; l.Plottable = false; l.Description = "3-1 煤";
        var json = SceneIO.SaveDoc(new Scene(), t.Layers, t.Current.Name);

        var back = new LayerTable();
        back.Restore(SceneIO.LoadDoc(json).Layers, "煤层");
        var r = back.Get("煤层")!;
        Assert.Equal(53, r.LineWeight);
        Assert.Equal(35, r.Transparency);
        Assert.False(r.Plottable);
        Assert.Equal("3-1 煤", r.Description);
    }

    [Fact]
    public void 存档往返_线宽0是合法值不能被当默认省掉()
    {
        // 0 = 0.00mm 是标准线宽档; 若序列化按"类型默认 0"省略, 读回会变成 -3(默认) —— 这条就是防它
        var t = new LayerTable();
        t.New("细线");
        t.Get("细线")!.LineWeight = 0;
        var json = SceneIO.SaveDoc(new Scene(), t.Layers, t.Current.Name);

        var back = new LayerTable();
        back.Restore(SceneIO.LoadDoc(json).Layers, "细线");
        Assert.Equal(0, back.Get("细线")!.LineWeight);
    }

    [Fact]
    public void 旧档兼容_缺四列时按默认读回()
    {
        // 手写一份 §三二〇 之前格式的文档(图层只有 N/C/V/F/K)
        const string json = "{\"L\":[{\"N\":\"0\",\"C\":[0.86,0.9,0.6],\"V\":true,\"F\":false,\"K\":false}],\"Cur\":\"0\",\"E\":[]}";
        var t = new LayerTable();
        t.Restore(SceneIO.LoadDoc(json).Layers, "0");
        var l = t.Get("0")!;
        Assert.Equal(-3, l.LineWeight);      // 默认线宽
        Assert.Equal(0, l.Transparency);     // 不透明
        Assert.True(l.Plottable);
        Assert.Equal("", l.Description);
    }

    // ── 随层透明度真的落到渲染上 ────────────────────────────────
    [Fact]
    public void 网随层透明_图层一改就走材质通道()
    {
        var t = new LayerTable();
        t.New("半透层");
        var scene = new Scene();
        scene.LayerTranspOf = n => t.Get(n)?.Transparency ?? 0;
        var m = Quad("半透层", -1);          // 实体=随层
        scene.Add(m);

        // 图层不透明: 走普通面通道, 没有材质批
        Assert.NotEmpty(scene.BuildFaces());
        Assert.Empty(scene.BuildMaterialFaces());

        // 图层改成 50% 透明: 该走材质通道, 且普通面通道要让开(否则同一张网画两遍, 半透会被叠实)
        t.Get("半透层")!.Transparency = 50;
        var mat = scene.BuildMaterialFaces();
        var batch = Assert.Single(mat);
        Assert.Equal(0.5f, batch.Alpha, 3);
        Assert.Empty(scene.BuildFaces());
    }

    [Fact]
    public void 改图层透明度必须穿透镶嵌缓存()
    {
        // 先建一遍面把缓存填上, 再改图层透明度 —— 缓存键不带 alpha 的话这里会命中旧缓存,
        // 用户看到的就是"图层特性管理器里改了不生效"
        var t = new LayerTable();
        t.New("L1");
        t.Get("L1")!.Transparency = 30;
        var scene = new Scene { LayerTranspOf = n => t.Get(n)?.Transparency ?? 0 };
        scene.Add(Quad("L1", -1));

        var a = scene.BuildMaterialFaces();
        Assert.Equal(0.7f, Assert.Single(a).Alpha, 3);

        t.Get("L1")!.Transparency = 80;
        var b = scene.BuildMaterialFaces();
        Assert.Equal(0.2f, Assert.Single(b).Alpha, 3);
    }

    [Fact]
    public void 实体自带透明度不受图层影响()
    {
        var t = new LayerTable();
        t.New("L1");
        t.Get("L1")!.Transparency = 90;
        var scene = new Scene { LayerTranspOf = n => t.Get(n)?.Transparency ?? 0 };
        scene.Add(Quad("L1", 25));            // 实体自己写死 25%
        Assert.Equal(0.75f, Assert.Single(scene.BuildMaterialFaces()).Alpha, 3);
    }

    [Fact]
    public void 没挂解析器时一律按不透明()
    {
        // LayerTranspOf 为 null(例如脱离主窗的纯场景) 不该崩, 也不该凭空半透
        var scene = new Scene();
        scene.Add(Quad("随便", -1));
        Assert.Empty(scene.BuildMaterialFaces());
        Assert.NotEmpty(scene.BuildFaces());
    }

    // ── 窗体行模型 ────────────────────────────────────────────
    [Fact]
    public void 行模型_线宽格不收随层随块_按默认收下()
    {
        var l = new Layer("A", 1, 1, 1) { LineWeight = 25 };
        var row = new PitMine3D.Kylin.Views.Layers.LayerManagerWindow.Row(l, () => { });
        row.LineWeightText = "随层";                   // 图层本身不能"随层"
        Assert.Equal(-3, l.LineWeight);
        row.LineWeightText = "0.50 mm";
        Assert.Equal(50, l.LineWeight);
        row.LineWeightText = "看不懂";                 // 解析不了 → 原值不动
        Assert.Equal(50, l.LineWeight);
    }

    [Fact]
    public void 行模型_透明度格夹到0到90并认不透明()
    {
        var l = new Layer("A", 1, 1, 1);
        var row = new PitMine3D.Kylin.Views.Layers.LayerManagerWindow.Row(l, () => { });
        row.TransparencyText = "45%";
        Assert.Equal(45, l.Transparency);
        Assert.Equal("45%", row.TransparencyText);
        row.TransparencyText = "999";
        Assert.Equal(90, l.Transparency);
        row.TransparencyText = "不透明";
        Assert.Equal(0, l.Transparency);
        Assert.Equal("不透明", row.TransparencyText);
    }

    [Fact]
    public void 行模型_改一格就回调刷新()
    {
        int n = 0;
        var l = new Layer("A", 1, 1, 1);
        var row = new PitMine3D.Kylin.Views.Layers.LayerManagerWindow.Row(l, () => n++);
        row.Frozen = true;
        row.Description = "说明";
        Assert.True(l.Frozen);
        Assert.Equal("说明", l.Description);
        Assert.Equal(2, n);   // 改即生效, 没有"确定"
    }
}
