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
    }
}
