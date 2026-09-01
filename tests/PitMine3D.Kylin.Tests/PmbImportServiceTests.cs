using System.IO;
using System.Text;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>PMB 块体导入(PmbImportService)回归 —— 按原 PmbmFormat/PmbmReader 文档字节布局构造 PMB 自验(规格符合性)。</summary>
public class PmbImportServiceTests
{
    // 含 Strings 段的 PMB: 2×1×1 网格(2 cell) + 2 命名属性(density, grade_v). 验属性名+选择
    private static byte[] MakePmb2()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        void PmbStr(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }   // PMB 串: u16 长 + UTF8
        const int stringsOff = 104, gridOff = 126, blocksOff = 218;
        // Header(32)
        w.Write((uint)0x31424D50); w.Write((uint)1); w.Write((uint)0);
        w.Write((long)306); w.Write((long)32); w.Write((uint)3);   // fileSize, stOff, sectionCount=3
        // SectionTable(3×24)
        w.Write((uint)1); w.Write((uint)0); w.Write((long)stringsOff); w.Write((long)22);   // Strings
        w.Write((uint)10); w.Write((uint)0); w.Write((long)gridOff); w.Write((long)92);      // GridSpec
        w.Write((uint)12); w.Write((uint)0); w.Write((long)blocksOff); w.Write((long)72);    // Blocks
        // Strings @104: count + "density" + "grade_v"
        w.Write((uint)2); PmbStr("density"); PmbStr("grade_v");
        // GridSpec @126 (92): 2×1×1
        w.Write(-1); w.Write(-1);
        w.Write(10.0); w.Write(20.0); w.Write(0.0);
        w.Write(2.0); w.Write(2.0); w.Write(2.0);
        w.Write(2); w.Write(1); w.Write(1);
        w.Write(0.0); w.Write((byte)0); w.Write((byte)0);
        w.Write(0f); w.Write(0f); w.Write(0f); w.Write((ushort)0);
        // Blocks @218 (72): storageMode+reserved+blockCount+attrCount, 2 attr × {nameIdx,dataType,reserved,valueCount, 2 doubles}
        w.Write((byte)0); w.Write(new byte[3]); w.Write((long)2); w.Write((uint)2);
        w.Write(0); w.Write((byte)0); w.Write(new byte[3]); w.Write((uint)2); w.Write(2.7); w.Write(2.8);   // density(nameIdx 0)
        w.Write(1); w.Write((byte)0); w.Write(new byte[3]); w.Write((uint)2); w.Write(50.0); w.Write(60.0); // grade_v(nameIdx 1)
        // Footer(16)
        w.Write((long)2); w.Write((uint)0); w.Write((uint)0x504D4231);
        return ms.ToArray();
    }

    [Fact]
    public void Strings_attribute_names_and_selection()
    {
        var def = PmbImportService.Parse(MakePmb2());
        Assert.True(def.Success, def.Error);
        Assert.Equal(new[] { "density", "grade_v" }, def.AttrNames);
        Assert.Equal("density", def.UsedAttr);            // 缺省首属性
        Assert.Equal(2.7, def.Blocks[0].Grade, 4);
        // 选 grade_v
        var sel = PmbImportService.Parse(MakePmb2(), selectAttr: "grade_v");
        Assert.Equal("grade_v", sel.UsedAttr);
        Assert.Equal(50.0, sel.Blocks[0].Grade, 4);
        Assert.Equal(60.0, sel.Blocks[1].Grade, 4);
    }

    [Fact]
    public void AllAttrs_holds_every_attribute_for_in_place_switch()
    {
        // PMB 持全属性(同 BLK): AllAttrs 应含 density[2.7,2.8] 与 grade_v[50,60], 供无重导切换/属性报告。
        var r = PmbImportService.Parse(MakePmb2());
        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.AllAttrs.Count);
        Assert.Equal(new[] { 2.7, 2.8 }, r.AllAttrs["density"]);
        Assert.Equal(new[] { 50.0, 60.0 }, r.AllAttrs["grade_v"]);
        // 选定属性的 grade 与 AllAttrs 一致(同一份数据)。
        Assert.Equal(r.AllAttrs["density"][0], r.Blocks[0].Grade, 6);   // 缺省首属性 density
    }

    // 构造最小 PMB v1: 2×2×1 网格(4 cell) + 1 属性(品位). origin(10,20,0) blockSize(2,2,2) grades[5,10,15,20]
    private static byte[] MakePmb(double[] grades)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        const int gridOff = 80, blocksOff = 172;
        int blocks = 4;
        // Header(32)
        w.Write((uint)0x31424D50);                 // magic 'PMB1'
        w.Write((uint)1);                          // version
        w.Write((uint)0);                          // flags
        w.Write((long)248);                        // fileSize
        w.Write((long)32);                         // sectionTableOff
        w.Write((uint)2);                          // sectionCount
        // SectionTable(2×24)
        w.Write((uint)10); w.Write((uint)0); w.Write((long)gridOff); w.Write((long)92);      // GridSpec
        w.Write((uint)12); w.Write((uint)0); w.Write((long)blocksOff); w.Write((long)60);    // Blocks
        // GridSpec(92) @80
        w.Write(-1); w.Write(-1);                                  // nameIdx, descIdx
        w.Write(10.0); w.Write(20.0); w.Write(0.0);               // origin
        w.Write(2.0); w.Write(2.0); w.Write(2.0);                 // blockSize
        w.Write(2); w.Write(2); w.Write(1);                       // dims
        w.Write(0.0);                                             // rot
        w.Write((byte)0); w.Write((byte)0);                      // storage, subDepth
        w.Write(0f); w.Write(0f); w.Write(0f);                   // subMin(3 float)
        w.Write((ushort)0);                                      // reserved
        // Blocks(60) @172
        w.Write((byte)0); w.Write(new byte[3]);                  // storageMode Dense, reserved
        w.Write((long)blocks); w.Write((uint)1);                // blockCount, attrCount
        w.Write(-1); w.Write((byte)0); w.Write(new byte[3]);    // 首属性 nameIdx, dataType, reserved
        w.Write((uint)blocks);                                  // valueCount
        foreach (var g in grades) w.Write(g);                   // 4 doubles
        // Footer(16) @232
        w.Write((long)blocks); w.Write((uint)0); w.Write((uint)0x504D4231);   // activeBlockCount, crc(0), magicEnd '1BMP'
        return ms.ToArray();
    }

    [Fact]
    public void Parses_grid_geometry_and_grades()
    {
        var r = PmbImportService.Parse(MakePmb(new[] { 5.0, 10.0, 15.0, 20.0 }));
        Assert.True(r.Success, r.Error);
        Assert.Equal(4, r.Blocks.Count);
        Assert.Equal(2, r.Nx); Assert.Equal(2, r.Ny); Assert.Equal(1, r.Nz);
        // idx0 = (i0,j0,k0) → 中心 origin+(0.5,0.5,0.5)*blockSize = (11,21,1), 品位 5
        Assert.Equal(11, r.Blocks[0].X, 6); Assert.Equal(21, r.Blocks[0].Y, 6); Assert.Equal(1, r.Blocks[0].Z, 6);
        Assert.Equal(5, r.Blocks[0].Grade, 6);
        Assert.Equal(2, r.Blocks[0].Size, 6);
        // idx1 = i1 → x=13(x-fastest)
        Assert.Equal(13, r.Blocks[1].X, 6); Assert.Equal(21, r.Blocks[1].Y, 6); Assert.Equal(10, r.Blocks[1].Grade, 6);
        // idx2 = j1 → y=23
        Assert.Equal(11, r.Blocks[2].X, 6); Assert.Equal(23, r.Blocks[2].Y, 6); Assert.Equal(15, r.Blocks[2].Grade, 6);
        // idx3 = (i1,j1) → (13,23), 品位 20
        Assert.Equal(13, r.Blocks[3].X, 6); Assert.Equal(23, r.Blocks[3].Y, 6); Assert.Equal(20, r.Blocks[3].Grade, 6);
    }

    [Fact]
    public void Bad_magic_rejected()
    {
        var b = MakePmb(new[] { 1.0, 2, 3, 4 }); b[0] = (byte)'X';
        Assert.False(PmbImportService.Parse(b).Success);
    }

    [Fact]
    public void Too_small_rejected()
    {
        Assert.False(PmbImportService.Parse(new byte[10]).Success);
    }

    [Fact]
    public void Grid_without_blocks_gives_zero_grade_cells()
    {
        // 只有 GridSpec 段(无 Blocks): 仍出几何 cell, 品位 0
        var full = MakePmb(new[] { 1.0, 2, 3, 4 });
        // 改 sectionCount=1(只留 GridSpec), 抹掉第二段表项的 Blocks id → 让 blocksOff 找不到
        // 直接构造: 用 Parse 但只认 GridSpec —— 简化: 把第二段 id 改成未知(如 99)
        full[32 + 24] = 99;   // 第二段表项(offset 32+24)的 id 首字节 → 非 12
        var r = PmbImportService.Parse(full);
        Assert.True(r.Success, r.Error);
        Assert.Equal(4, r.Blocks.Count);
        Assert.All(r.Blocks, b => Assert.Equal(0, b.Grade, 6));   // 无 Blocks → 品位 0
    }
}
