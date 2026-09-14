using System.IO;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>Block_Model_2.0 (.blk) 八叉树块体导入回归 —— 按原 BlkReader 文档格式构造 .blk 自验 + 真实平朔样本。</summary>
public class BlkImportServiceTests
{
    // .blk 的串是 GBK（同原 BlkReader）；中文属性名/类别名按 ASCII 写会变成 "?"，纯 ASCII 串两者等价
    private static readonly Encoding Gbk = MakeGbk();
    private static Encoding MakeGbk()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        try { return Encoding.GetEncoding("GBK"); } catch { return Encoding.GetEncoding(936); }
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var bytes = Gbk.GetBytes(s);
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

    // 各向异性 .blk: 根盒 2048×2048×64 → 细格 2×2×0.0625(maxSub=10); 两属性(矿岩类型 int32 分类, 品位 float)
    // 两块: 一个 sub=10 的最细块 + 一个 sub=8 的粗块(边长 4 细格); 尾部带类别名表 + 类别色表。
    private static byte[] MakeBlkAniso()
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        WriteStr(w, "Block_Model_2.0");
        w.Write(10); w.Write(11);
        w.Write(1000.0); w.Write(2000.0); w.Write(100.0);   // origin
        w.Write(2048.0); w.Write(2048.0); w.Write(64.0);    // 根盒: Z 只有 1/32
        w.Write(2048.0); w.Write(2048.0); w.Write(64.0);    // extent
        w.Write(0.0); w.Write((byte)0);
        w.Write(2);
        WriteStr(w, "矿岩类型"); w.Write(4);                 // 分类(int32)
        WriteStr(w, "品位"); w.Write(2);                     // 数值(float)
        w.Write(2);                                         // blockCount
        w.Write(10UL | (2UL << 5) | (3UL << 24) | (5UL << 43)); w.Write(1); w.Write(42.5f);   // sub=10 → s=1
        w.Write(8UL | (0UL << 5) | (0UL << 24) | (0UL << 43)); w.Write(0); w.Write(1.5f);     // sub=8  → s=4
        // 尾部: 类别名表 + 色表
        w.Write(2); WriteStr(w, "废石"); WriteStr(w, "9 SEAM");
        w.Write(1); WriteStr(w, "矿岩类型"); w.Write(2);
        WriteStr(w, "废石"); w.Write(0.5f); w.Write(0.5f); w.Write(0.5f);
        WriteStr(w, "9 SEAM"); w.Write(1f); w.Write(0f); w.Write(0f);
        return ms.ToArray();
    }

    /// <summary>
    /// 细格三轴尺寸各自由【根盒/2^maxSub】定，不是一个各向同性的边长。
    /// 曾只带回 Size=s·bx，于是 25×25×0.5 m 的平朔块被画成 25 的立方体、竖向胖 50 倍（模型糊成平板）。
    /// </summary>
    [Fact]
    public void Anisotropic_cell_size_comes_from_root_box_per_axis()
    {
        var r = BlkImportService.Parse(MakeBlkAniso());
        Assert.True(r.Success, r.Error);
        Assert.Equal(10, r.MaxSub);
        Assert.Equal(2.0, r.Bx, 9); Assert.Equal(2.0, r.By, 9); Assert.Equal(0.0625, r.Bz, 9);   // 2048/1024, 64/1024
        Assert.Equal(1000, r.Ox, 9); Assert.Equal(2000, r.Oy, 9); Assert.Equal(100, r.Oz, 9);
        // 细格维度 = 各轴最大细格下标+1: 最细块到 X6/Y4/Z3, 粗块到 4 → (6,4,4)
        Assert.Equal(6, r.Nx); Assert.Equal(4, r.Ny); Assert.Equal(4, r.Nz);
        Assert.Equal(1, r.VarCellCount);                    // 那个 sub=8 的粗块
        // 粗块: s=4 → X 边长 4·2=8, 中心在 origin + (0+2)·细格
        var coarse = r.Blocks[1];
        Assert.Equal(8.0, coarse.Size, 9);
        Assert.Equal(1000 + 2 * 2.0, coarse.X, 9);
        Assert.Equal(100 + 2 * 0.0625, coarse.Z, 9);        // Z 用【Z 的细格】, 不是 X 的
    }

    /// <summary>尾部类别名表 + 类别色表(按名给色→翻成码), 推荐着色属性取「带色表的分类属性」。</summary>
    [Fact]
    public void Tail_category_names_and_colors_are_read()
    {
        var r = BlkImportService.Parse(MakeBlkAniso());
        Assert.Equal(new[] { "废石", "9 SEAM" }, r.CategoryNames.ToArray());
        Assert.Equal("矿岩类型", r.ColorTableAttr);
        Assert.Equal(((byte)128, (byte)128, (byte)128), r.CategoryColors[0]);
        Assert.Equal(((byte)255, (byte)0, (byte)0), r.CategoryColors[1]);
        Assert.Equal(new[] { true, false }, r.AttrCategorical.ToArray());
        Assert.Equal("矿岩类型", r.SuggestedAttr);   // 分类且带色表 → 优先它，不是首个数值列
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
    public void All_attrs_held_per_block_for_switching()
    {
        // 无论选哪个作品位, AllAttrs 持全属性逐块值 —— 供「切换属性」免重导重取 grade
        var r = BlkImportService.Parse(MakeBlk2(2.7f, 55.0f), selectAttr: "density");
        Assert.True(r.Success, r.Error);
        Assert.Equal(new[] { "density", "grade_v" }, r.AllAttrs.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(2.7, r.AllAttrs["density"][0], 4);
        Assert.Equal(55.0, r.AllAttrs["grade_v"][0], 4);       // 未选中的属性一样被持有
        Assert.Equal(r.Blocks.Count, r.AllAttrs["grade_v"].Length);   // 长度==块数(切换按索引回写)
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
