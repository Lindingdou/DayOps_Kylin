using System.IO;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>Block_Model_2.0 (.blk) 八叉树块体导入回归 —— 按原 BlkReader 文档格式构造 .blk 自验 + 真实平朔样本。</summary>
public class BlkImportServiceTests
{
    private static void WriteStr(BinaryWriter w, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        int len = bytes.Length;
        while (len >= 0x80) { w.Write((byte)(len | 0x80)); len >>= 7; }
        w.Write((byte)len);
        w.Write(bytes);
    }

    // 单块 .blk: origin(1000,2000,100) 根盒 2048³, 属性1(grade,float), 块 sub=10 voxel(X5,Y3,Z2) grade 42.5
    private static byte[] MakeBlk()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        WriteStr(w, "Block_Model_2.0");
        w.Write(10); w.Write(11);                          // a,b
        w.Write(1000.0); w.Write(2000.0); w.Write(100.0); // origin
        w.Write(2048.0); w.Write(2048.0); w.Write(2048.0);// 根盒
        w.Write(2048.0); w.Write(2048.0); w.Write(2048.0);// extent
        w.Write(0.0);                                     // d9
        w.Write((byte)0);                                 // flag
        w.Write(1);                                       // attrCount
        WriteStr(w, "grade"); w.Write(2);                 // schema: name, type 2(float)
        w.Write(1);                                       // blockCount
        ulong loc = 10UL | (2UL << 5) | (3UL << 24) | (5UL << 43);   // sub=10, Z=2, Y=3, X=5
        w.Write(loc);
        w.Write(42.5f);                                   // grade
        return ms.ToArray();
    }

    [Fact]
    public void Parses_octree_leaf_position_size_grade()
    {
        var r = BlkImportService.Parse(MakeBlk());
        Assert.True(r.Success, r.Error);
        Assert.Equal(1, r.Blocks.Count);
        var b = r.Blocks[0];
        // maxSub=10, sh=0, s=1; 细格 = 2048/2^10 = 2; 中心 = origin + (voxel+0.5)*2
        Assert.Equal(1011, b.X, 6);   // 1000 + (5+0.5)*2
        Assert.Equal(2007, b.Y, 6);   // 2000 + (3+0.5)*2
        Assert.Equal(105, b.Z, 6);    // 100 + (2+0.5)*2
        Assert.Equal(2, b.Size, 6);   // s*cell = 1*2
        Assert.Equal(42.5, b.Grade, 5);
    }

    [Fact]
    public void Bad_magic_rejected()
    {
        var b = MakeBlk(); b[1] = (byte)'X';   // 破坏 magic 首字符(magic 在 7-bit 长度前缀后)
        Assert.False(BlkImportService.Parse(b).Success);
    }

    [Fact]
    public void Too_small_rejected()
    {
        Assert.False(BlkImportService.Parse(new byte[10]).Success);
    }

    // 双属性 .blk(密度,品位 均 float), 单块 loc 同上
    private static byte[] MakeBlk2(float density, float grade)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        WriteStr(w, "Block_Model_2.0");
        w.Write(10); w.Write(11);
        w.Write(1000.0); w.Write(2000.0); w.Write(100.0);
        w.Write(2048.0); w.Write(2048.0); w.Write(2048.0);
        w.Write(2048.0); w.Write(2048.0); w.Write(2048.0);
        w.Write(0.0); w.Write((byte)0);
        w.Write(2);                                       // attrCount=2
        WriteStr(w, "density"); w.Write(2);
        WriteStr(w, "grade_v"); w.Write(2);
        w.Write(1);
        ulong loc = 10UL | (2UL << 5) | (3UL << 24) | (5UL << 43);
        w.Write(loc);
        w.Write(density); w.Write(grade);                 // attr0=密度, attr1=品位
        return ms.ToArray();
    }

    [Fact]
    public void Attribute_names_listed_and_first_used_by_default()
    {
        var r = BlkImportService.Parse(MakeBlk2(2.7f, 55.0f));
        Assert.True(r.Success, r.Error);
        Assert.Equal(new[] { "density", "grade_v" }, r.AttrNames);
        Assert.Equal("density", r.UsedAttr);                  // 默认首数值属性
        Assert.Equal(2.7, r.Blocks[0].Grade, 4);
    }

    [Fact]
    public void Select_attribute_by_name()
    {
        var r = BlkImportService.Parse(MakeBlk2(2.7f, 55.0f), selectAttr: "grade_v");
        Assert.True(r.Success, r.Error);
        Assert.Equal("grade_v", r.UsedAttr);
        Assert.Equal(55.0, r.Blocks[0].Grade, 4);          // 选中品位
    }

    [Fact]
    public void Unknown_select_falls_back_to_first()
    {
        var r = BlkImportService.Parse(MakeBlk2(2.7f, 55.0f), selectAttr: "不存在");
        Assert.Equal("density", r.UsedAttr);                  // 找不到 → 回落首个
        Assert.Equal(2.7, r.Blocks[0].Grade, 4);
    }

    [Fact]
    public void Real_blk_sample_parses()
    {
        string[] cands =
        {
            @"C:\Users\cFore\Desktop\2026年6月测试文件\平朔数据\东露天块体模型（2026.06）.blk",
            @"C:\Users\cFore\Desktop\块体\2036采场块体.blk",
        };
        string? path = cands.FirstOrDefault(File.Exists);
        if (path == null) return;   // 无样本机器跳过
        var r = BlkImportService.Load(path);
        Assert.True(r.Success, r.Error);
        Assert.True(r.BlockCount > 0);
        Assert.NotEmpty(r.Blocks);
        // 平朔坐标系 X~620100+, Y~4379400+ 量级 —— 防 loc 解码错成天文/负坐标
        double minX = r.Blocks.Min(b => b.X), maxX = r.Blocks.Max(b => b.X);
        double minY = r.Blocks.Min(b => b.Y), maxY = r.Blocks.Max(b => b.Y);
        Assert.InRange(maxX - minX, 1, 1e6);   // 合理跨度(非退化、非天文)
        Assert.InRange(maxY - minY, 1, 1e6);
        Assert.All(r.Blocks, b => Assert.True(b.Size > 0 && b.Size < 1e5));
    }
}
