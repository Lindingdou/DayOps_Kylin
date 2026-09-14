using System.IO;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>原版 PMX 工程二进制读取回归 —— 合成档往返(按 PmxFormat 规格构档→读→核实体)。</summary>
public class PmxImportServiceTests
{
    static byte[] Strings(params string[] ss)
    {
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(ss.Length);
        foreach (var s in ss) { var b = Encoding.UTF8.GetBytes(s); bw.Write((ushort)b.Length); bw.Write(b); }
        return ms.ToArray();
    }
    static byte[] LayersRed()
    {
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(1);                       // 1 layer
        bw.Write(0);                       // nameStrIdx = 0
        bw.Write((byte)2);                 // colorMode TrueColor
        bw.Write((byte)255); bw.Write((byte)0); bw.Write((byte)0);   // red
        bw.Write((byte)0);                 // flags
        bw.Write((byte)0);                 // lineWeight
        bw.Write(new byte[3]);             // reserved
        return ms.ToArray();
    }
    static byte[] NoStyles() { using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms); bw.Write(0); return ms.ToArray(); }

    static void Xyz(BinaryWriter bw, double x, double y, double z) { bw.Write(x); bw.Write(y); bw.Write(z); }

    static void Ent(BinaryWriter outw, byte[] body)   // recordLen + body
    { outw.Write(body.Length); outw.Write(body); }

    static byte[] Body(byte type, byte cmode, byte r, byte g, byte b, System.Action<BinaryWriter> spec)
    {
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(type); bw.Write(0 /*layerIdx*/); bw.Write(cmode); bw.Write(r); bw.Write(g); bw.Write(b);
        spec(bw);
        return ms.ToArray();
    }

    // 自定义 图层段/实体段 的工程(索引色等场景用); 头/段表布局同 BuildPmx。
    static byte[] BuildPmx(byte[] layers, byte[] ents)
    {
        byte[] strings = Strings("测试层");
        byte[] styles = NoStyles();
        int hdrTable = 32 + 4 * 24;
        long sOff = hdrTable, lOff = sOff + strings.Length, tOff = lOff + layers.Length, eOff = tOff + styles.Length;
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(0x31584D50u); bw.Write(1u); bw.Write(0u); bw.Write(0L); bw.Write(32L); bw.Write(4);
        void Sec(int id, long off, long size) { bw.Write(id); bw.Write(0); bw.Write(off); bw.Write(size); }
        Sec(1, sOff, strings.Length); Sec(2, lOff, layers.Length); Sec(3, tOff, styles.Length); Sec(6, eOff, ents.Length);
        bw.Write(strings); bw.Write(layers); bw.Write(styles); bw.Write(ents);
        return ms.ToArray();
    }

    // 图层用 AutoCAD 索引色(ACI)存色 —— 现场工程的常态(colorMode=1)。
    static byte[] LayersAci(byte aci)
    {
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(1); bw.Write(0); bw.Write((byte)1); bw.Write(aci); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((byte)0); bw.Write((byte)0); bw.Write(new byte[3]);
        return ms.ToArray();
    }

    static byte[] BuildPmx()
    {
        byte[] strings = Strings("测试层");
        byte[] layers = LayersRed();
        byte[] styles = NoStyles();

        using var ems = new MemoryStream(); using var ew = new BinaryWriter(ems);
        ew.Write(4);   // 4 entities
        Ent(ew, Body(1, 2, 0, 255, 0, w => { Xyz(w, 100, 200, 0); Xyz(w, 300, 400, 0); }));                 // Line 绿
        Ent(ew, Body(3, 2, 0, 0, 255, w => { Xyz(w, 500, 600, 0); }));                                       // Point 蓝
        Ent(ew, Body(2, 0, 0, 0, 0, w => { w.Write((byte)1); w.Write(3); Xyz(w, 0, 0, 0); Xyz(w, 10, 0, 0); Xyz(w, 10, 10, 0); }));   // Polyline 闭合 3 点
        Ent(ew, Body(14, 0, 0, 0, 0, w => { Xyz(w, 50, 50, 0); w.Write(25.0); Xyz(w, 0, 0, 1); }));          // Circle c(50,50) r25 + normal
        byte[] ents = ems.ToArray();

        int hdrTable = 32 + 4 * 24;                 // 128
        long sOff = hdrTable, lOff = sOff + strings.Length, tOff = lOff + layers.Length, eOff = tOff + styles.Length;
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(0x31584D50u); bw.Write(1u); bw.Write(0u); bw.Write(0L); bw.Write(32L); bw.Write(4);
        void Sec(int id, long off, long size) { bw.Write(id); bw.Write(0); bw.Write(off); bw.Write(size); }
        Sec(1, sOff, strings.Length); Sec(2, lOff, layers.Length); Sec(3, tOff, styles.Length); Sec(6, eOff, ents.Length);
        bw.Write(strings); bw.Write(layers); bw.Write(styles); bw.Write(ents);
        return ms.ToArray();
    }

    static PmxImportService.Result LoadBytes(byte[] bytes)
    {
        string tmp = Path.GetTempFileName();
        try { File.WriteAllBytes(tmp, bytes); return PmxImportService.Load(tmp); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void Reads_core_entities_from_synthetic_pmx()
    {
        var r = LoadBytes(BuildPmx());
        Assert.True(r.Success, r.Error);
        Assert.Equal(1, r.Lines);
        Assert.Equal(1, r.Points);
        Assert.Equal(1, r.Polylines);
        Assert.Equal(1, r.Circles);
        Assert.Contains("测试层", r.LayerNames);
    }

    [Fact]
    public void Line_geometry_and_truecolor_are_preserved()
    {
        var r = LoadBytes(BuildPmx());
        var le = (LineEntity)r.Entities.First(e => e is LineEntity);
        Assert.Equal(100, le.X0, 6); Assert.Equal(200, le.Y0, 6);
        Assert.Equal(300, le.X1, 6); Assert.Equal(400, le.Y1, 6);
        Assert.Equal(0f, le.Cr, 3); Assert.Equal(1f, le.Cg, 3); Assert.Equal(0f, le.Cb, 3);   // 绿(TrueColor)
    }

    [Fact]
    public void Polyline_closed_and_circle_geometry_preserved()
    {
        var r = LoadBytes(BuildPmx());
        var pl = (PolylineEntity)r.Entities.First(e => e is PolylineEntity);
        Assert.True(pl.Closed);
        Assert.Equal(3, pl.Points.Count);
        var ce = (CircleEntity)r.Entities.First(e => e is CircleEntity);
        Assert.Equal(50, ce.Cx, 6); Assert.Equal(50, ce.Cy, 6); Assert.Equal(25, ce.Radius, 6);
    }

    [Fact]
    public void Non_pmx_bytes_fail_cleanly()
    {
        var r = LoadBytes(Encoding.UTF8.GetBytes("this is not a pmx file at all, just text...."));
        Assert.False(r.Success);
        Assert.NotEqual("", r.Error);
    }

    // 真实原版工程样本(桌面测试目录), 存在则端到端验; 不在(CI)则跳过。同 LAS/KDF skip-if-absent 纪律。
    [Theory]
    [InlineData(@"C:\Users\cFore\Desktop\2026年6月测试文件\untitled.pmx")]
    [InlineData(@"C:\Users\cFore\Desktop\2026年6月测试文件\现状.pmx")]
    public void Reads_real_sample_project_if_present(string path)
    {
        if (!File.Exists(path)) return;   // skip-if-absent
        var r = PmxImportService.Load(path);
        Assert.True(r.Success, $"{Path.GetFileName(path)}: {r.Error}");
        Assert.True(r.Entities.Count > 0, $"{Path.GetFileName(path)}: 未读出实体");
        Assert.True(PmxImportService.IsOriginalPmx(path), "真实原版工程应被识别为二进制 .pmx");
    }

    // 「打开」/文件管理器双击 靠这个探头分派: 原版二进制 .pmx 与 Kylin 文本 .pmx 同后缀,
    // 分派错就是用户看到的"系统无法打开 pmx"。
    [Fact]
    public void 探头认得出原版二进制pmx()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, BuildPmx());
            Assert.True(PmxImportService.IsOriginalPmx(tmp));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void 文本pmx与短文件不会被误判为二进制()
    {
        string tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "SCENE 1\nLINE 0 0 10 10\n");   // Kylin 自己存的文本 .pmx
            Assert.False(PmxImportService.IsOriginalPmx(tmp));
            File.WriteAllBytes(tmp, new byte[] { (byte)'P', (byte)'M' });   // 比 magic 还短
            Assert.False(PmxImportService.IsOriginalPmx(tmp));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // 现场的原版工程多用 AutoCAD 索引色(ACI)而非 TrueColor: 只认 TrueColor 就整张图成默认灰,
    // 用户看到的就是"颜色没保留"。1=红 5=蓝, 与 DXF 导入同一张表。
    [Fact]
    public void 实体索引色按ACI还原()
    {
        using var ems = new MemoryStream(); using var ew = new BinaryWriter(ems);
        ew.Write(2);
        Ent(ew, Body(1, 1, 1, 0, 0, w => { Xyz(w, 0, 0, 0); Xyz(w, 10, 0, 0); }));    // ACI 1 红
        Ent(ew, Body(1, 1, 5, 0, 0, w => { Xyz(w, 0, 5, 0); Xyz(w, 10, 5, 0); }));    // ACI 5 蓝
        var r = LoadBytes(BuildPmx(LayersAci(7), ems.ToArray()));

        Assert.True(r.Success, r.Error);
        var red = r.Entities[0]; var blue = r.Entities[1];
        Assert.True(red.Cr > 0.8f && red.Cg < 0.4f && red.Cb < 0.4f, $"红读成 {red.Cr},{red.Cg},{red.Cb}");
        Assert.True(blue.Cb > 0.8f && blue.Cr < 0.6f, $"蓝读成 {blue.Cr},{blue.Cg},{blue.Cb}");
        Assert.False(IsFallbackGray(red), "索引色回落成默认灰 = 颜色没保留");
        Assert.False(IsFallbackGray(blue), "索引色回落成默认灰 = 颜色没保留");
    }

    // ByLayer(colorMode=0) 的实体取图层色; 图层色本身也可能是索引色。
    [Fact]
    public void 随层实体取图层的索引色()
    {
        using var ems = new MemoryStream(); using var ew = new BinaryWriter(ems);
        ew.Write(1);
        Ent(ew, Body(1, 0, 0, 0, 0, w => { Xyz(w, 0, 0, 0); Xyz(w, 10, 0, 0); }));    // ByLayer
        var r = LoadBytes(BuildPmx(LayersAci(3), ems.ToArray()));    // 图层 ACI 3 绿

        Assert.True(r.Success, r.Error);
        var e = r.Entities[0];
        Assert.True(e.Cg > 0.7f && e.Cr < 0.6f, $"随层绿读成 {e.Cr},{e.Cg},{e.Cb}");
    }

    // 真实原版工程(现状.pmx: 图层 ACI 7, 实体 ACI 1/5)——存在则核对读出的确是多种真颜色。
    [Fact]
    public void 真实工程读出多种颜色_样本在才跑()
    {
        string path = Path.Combine(SampleDir, "现状.pmx");
        if (!File.Exists(path)) return;   // skip-if-absent
        var r = PmxImportService.Load(path);
        Assert.True(r.Success, r.Error);
        var colors = r.Entities.Select(e => (e.Cr, e.Cg, e.Cb)).Distinct().ToList();
        Assert.True(colors.Count >= 2, $"只读出 {colors.Count} 种颜色, 索引色没还原");
        Assert.DoesNotContain(r.Entities, IsFallbackGray);
    }

    const string SampleDir = @"C:\Users\cFore\Desktop\2026年6月测试文件";

    static bool IsFallbackGray(PitMine3D.Kylin.Cad.Draw.SceneEntity e)
        => System.Math.Abs(e.Cr - 0.72f) < 1e-3 && System.Math.Abs(e.Cg - 0.74f) < 1e-3 && System.Math.Abs(e.Cb - 0.8f) < 1e-3;

    // 原版工程里等高线是真三维(逐点 Z): 丢了 Z, 图在三维里就是压平的一张纸, 转起来看不出起伏。
    [Fact]
    public void 逐点变高的多段线保住三维Z()
    {
        using var ems = new MemoryStream(); using var ew = new BinaryWriter(ems);
        ew.Write(1);
        Ent(ew, Body(2, 1, 1, 0, 0, w =>
        {
            w.Write((byte)0); w.Write(3);
            Xyz(w, 0, 0, 100); Xyz(w, 10, 0, 120); Xyz(w, 20, 0, 90);
        }));
        var r = LoadBytes(BuildPmx(LayersAci(7), ems.ToArray()));

        Assert.True(r.Success, r.Error);
        var pl = (PolylineEntity)r.Entities[0];
        Assert.True(pl.Has3D, "逐点变高的多段线应存 Zs");
        Assert.Equal(100, pl.ZAt(0), 6);
        Assert.Equal(120, pl.ZAt(1), 6);
        Assert.Equal(90, pl.ZAt(2), 6);
    }

    // 等高线(整条一个高程)只存 Elevation, 不占一份 Zs 数组。
    [Fact]
    public void 等高多段线只存标高()
    {
        using var ems = new MemoryStream(); using var ew = new BinaryWriter(ems);
        ew.Write(1);
        Ent(ew, Body(2, 1, 1, 0, 0, w => { w.Write((byte)0); w.Write(2); Xyz(w, 0, 0, 1330); Xyz(w, 10, 0, 1330); }));
        var r = LoadBytes(BuildPmx(LayersAci(7), ems.ToArray()));

        var pl = (PolylineEntity)r.Entities[0];
        Assert.False(pl.Has3D);
        Assert.Equal(1330, pl.Elevation, 6);
        Assert.Equal(1330, pl.ZAt(0), 6);
    }

    // 线/点/圆/弧 的标高同样要收(每实体单一 Elevation, 线取两端均值 —— 同 DXF 导入口径)。
    [Fact]
    public void 线点圆弧的标高也收()
    {
        using var ems = new MemoryStream(); using var ew = new BinaryWriter(ems);
        ew.Write(3);
        Ent(ew, Body(1, 1, 1, 0, 0, w => { Xyz(w, 0, 0, 100); Xyz(w, 10, 0, 200); }));       // 线: 均值 150
        Ent(ew, Body(3, 1, 1, 0, 0, w => { Xyz(w, 5, 5, 77); }));                            // 点
        Ent(ew, Body(14, 1, 1, 0, 0, w => { Xyz(w, 0, 0, 42); w.Write(25.0); Xyz(w, 0, 0, 1); }));   // 圆
        var r = LoadBytes(BuildPmx(LayersAci(7), ems.ToArray()));

        Assert.Equal(150, r.Entities[0].Elevation, 6);
        Assert.Equal(77, r.Entities[1].Elevation, 6);
        Assert.Equal(42, r.Entities[2].Elevation, 6);
    }

    // 真实原版工程(现状.pmx: 2309 条逐点变高的等高线, Z 约 1122~1516 m)——存在则核对三维没被压平。
    [Fact]
    public void 真实工程保住三维_样本在才跑()
    {
        string path = Path.Combine(SampleDir, "现状.pmx");
        if (!File.Exists(path)) return;   // skip-if-absent
        var r = PmxImportService.Load(path);
        Assert.True(r.Success, r.Error);
        var pls = r.Entities.OfType<PolylineEntity>().ToList();
        Assert.True(pls.Count(p => p.Has3D) > 1000, "逐点变高的等高线应保住 Zs");
        double zmin = double.MaxValue, zmax = double.MinValue;
        foreach (var pl in pls)
            for (int i = 0; i < pl.Points.Count; i++) { double z = pl.ZAt(i); if (z < zmin) zmin = z; if (z > zmax) zmax = z; }
        Assert.True(zmax - zmin > 300, $"Z 跨度只有 {zmax - zmin:0.#}, 三维信息被压平了");
    }

    [Fact]
    public void 文件不存在时探头回false不抛()
        => Assert.False(PmxImportService.IsOriginalPmx(Path.Combine(Path.GetTempPath(), "no_such_file_xyz.pmx")));
}
