using System;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// MapGIS 6.x .WL/.WT 导入回归（忠实移植 MapGisWlReader/MapGisWtReader）。
/// 用真实小样本(剖面方向.WL/.WT, A1煤层.WL)作解析夹具——二进制逆向格式无法内联生成。
/// </summary>
public class MapGisImportTests
{
    private static string Fx(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "mapgis", name);

    [Fact]
    public void Wl_parses_lines_with_vertices_inside_bbox()
    {
        var er = MapGisImportService.LoadWl(Fx("section.wl"));
        Assert.True(er.Success, $"WL 应解析成功：{er.Error}");
        Assert.NotEmpty(er.Entities);
        var plines = er.Entities.OfType<PolylineEntity>().ToList();
        Assert.Equal(er.Entities.Count, plines.Count);           // 全是折线
        Assert.All(plines, p => Assert.True(p.Points.Count >= 2, "每条线≥2 点"));
        // bounds 有效且顶点落在 bbox 内(几何合理性——防错位读出天文数字坐标)
        double xmin = er.Bounds[0], ymin = er.Bounds[1], xmax = er.Bounds[2], ymax = er.Bounds[3];
        Assert.True(xmax > xmin && ymax > ymin, "bbox 应非退化");
        double pad = Math.Max(1.0, (xmax - xmin) * 0.01);
        foreach (var p in plines)
            foreach (var (x, y) in p.Points)
            {
                Assert.InRange(x, xmin - pad, xmax + pad);
                Assert.InRange(y, ymin - pad, ymax + pad);
            }
        Assert.Equal(plines.Count, er.TypeCounts["多段线"]);
    }

    [Fact]
    public void Wl_coal_seam_file_parses()
    {
        var er = MapGisImportService.LoadWl(Fx("seam.wl"));
        Assert.True(er.Success, $"煤层 WL 应解析：{er.Error}");
        Assert.NotEmpty(er.Entities);
        Assert.All(er.Entities.OfType<PolylineEntity>(), p => Assert.NotEmpty(p.Points));
    }

    [Fact]
    public void Wt_parses_texts_with_nonempty_gbk_strings()
    {
        var er = MapGisImportService.LoadWt(Fx("legend.wt"));
        Assert.True(er.Success, $"WT 应解析成功：{er.Error}");
        var texts = er.Entities.OfType<TextEntity>().ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.False(string.IsNullOrEmpty(t.Text), "注记文字非空"));
        Assert.All(texts, t => Assert.True(t.Height > 0, "字高>0(兜底后)"));
        // 至少一条含中文(GBK 解码成功的证据——防乱码/空)
        Assert.Contains(texts, t => t.Text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF));
        // 位置落在 bbox 内
        double xmin = er.Bounds[0], ymin = er.Bounds[1], xmax = er.Bounds[2], ymax = er.Bounds[3];
        double pad = Math.Max(1.0, (xmax - xmin) * 0.02);
        Assert.All(texts, t => { Assert.InRange(t.X, xmin - pad, xmax + pad); Assert.InRange(t.Y, ymin - pad, ymax + pad); });
    }

    [Fact]
    public void Wp_parses_region_boundary_arcs_inside_bbox()
    {
        var er = MapGisImportService.LoadWp(Fx("legend.wp"));
        Assert.True(er.Success, $"WP 应解析成功：{er.Error}");
        var plines = er.Entities.OfType<PolylineEntity>().ToList();
        Assert.NotEmpty(plines);                                  // 至少若干边界 arc
        Assert.All(plines, p => Assert.True(p.Points.Count >= 2, "每条 arc≥2 点"));
        double xmin = er.Bounds[0], ymin = er.Bounds[1], xmax = er.Bounds[2], ymax = er.Bounds[3];
        Assert.True(xmax > xmin && ymax > ymin, "bbox 非退化");
        double pad = Math.Max(100.0, (xmax - xmin) * 0.01);       // WP 解析器本身按 bbox±100 过滤垃圾顶点
        foreach (var p in plines)
            foreach (var (x, y) in p.Points)
            {
                Assert.InRange(x, xmin - pad, xmax + pad);
                Assert.InRange(y, ymin - pad, ymax + pad);
            }
        Assert.Equal(plines.Count, er.TypeCounts["多段线"]);
    }

    [Fact]
    public void Load_dispatches_by_extension()
    {
        Assert.True(MapGisImportService.Load(Fx("section.wl")).Success);
        Assert.True(MapGisImportService.Load(Fx("legend.wt")).Success);
        Assert.True(MapGisImportService.Load(Fx("legend.wp")).Success);
    }

    [Fact]
    public void Project_reader_extracts_members_from_real_mpj()
    {
        var members = MapGisImportService.ReadProjectLayers(Fx("project.mpj"));
        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.Contains(m.Ext, new[] { ".wl", ".wt", ".wp" }));
        Assert.All(members, m => Assert.EndsWith(m.Ext, m.FileName.ToLowerInvariant()));
        // 去重：文件名唯一
        Assert.Equal(members.Count, members.Select(m => m.FileName.ToLowerInvariant()).Distinct().Count());
        // 真实工程应至少含线(WL)与区(WP)成员
        Assert.Contains(members, m => m.Ext == ".wl");
    }

    [Fact]
    public void Project_load_merges_member_entities_end_to_end()
    {
        // 合成最小 .mpj(magic + GBK 成员路径), 与真实成员文件同置临时目录 → LoadProject 加载并合并
        string dir = Path.Combine(Path.GetTempPath(), "pm_mpj_test_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Fx("section.wl"), Path.Combine(dir, "线.wl"));
            File.Copy(Fx("legend.wt"), Path.Combine(dir, "注记.wt"));
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
            var gbk = System.Text.Encoding.GetEncoding("GBK");
            byte[] head = System.Text.Encoding.ASCII.GetBytes("WMAP`D2:");
            // 头 8B + 正文, 需 ≥64B(reader 最小长度校验)——补足占位
            byte[] body = gbk.GetBytes("mapgis project layers list padding........ .\\线.wl , .\\注记.wt ........end\0");
            string mpj = Path.Combine(dir, "proj.mpj");
            using (var fs = File.Create(mpj)) { fs.Write(head); fs.Write(body); }

            var er = MapGisImportService.LoadProject(mpj);
            Assert.True(er.Success, $"合成工程应加载：{er.Error}");
            Assert.Contains(er.Entities, e => e is PolylineEntity);   // 来自 WL 成员
            Assert.Contains(er.Entities, e => e is TextEntity);       // 来自 WT 成员
            Assert.True(er.LayerOrder.Count >= 2, "两成员各一图层");
            Assert.Contains("线", er.LayerOrder);
            Assert.Contains("注记", er.LayerOrder);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Bad_magic_and_missing_file_error_without_crash()
    {
        // 用一个非 MapGIS 文件(本测试 dll 自身)当坏输入 → 应报错不崩
        string notMapgis = typeof(MapGisImportTests).Assembly.Location;
        var er = MapGisImportService.LoadWl(notMapgis);
        Assert.False(er.Success);
        Assert.NotNull(er.Error);

        var er2 = MapGisImportService.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "mapgis", "does_not_exist.wl"));
        Assert.False(er2.Success);

        var er3 = MapGisImportService.Load(Fx("section.wl") + ".xyz");   // 不支持的扩展名(且文件不存在)
        Assert.False(er3.Success);
    }
}
