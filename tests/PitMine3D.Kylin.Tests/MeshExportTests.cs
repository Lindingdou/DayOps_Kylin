using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网导出 OBJ/PLY/STL(MeshExport)回归 —— 忠实原网格导出的公开文本格式。</summary>
public class MeshExportTests
{
    // 单位方 z=0(4 顶点 2 三角, CCW → 法向 +Z)
    private static readonly List<(double x, double y, double z)> V = new()
    { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0) };
    private static readonly List<(int a, int b, int c)> T = new() { (0, 1, 2), (0, 2, 3) };

    [Fact]
    public void Obj_has_vertices_and_1based_faces()
    {
        var lines = MeshExport.ToObj(V, T).TrimEnd('\n').Split('\n');
        Assert.Equal(4, lines.Count(l => l.StartsWith("v ")));
        Assert.Equal(2, lines.Count(l => l.StartsWith("f ")));
        Assert.Contains("f 1 2 3", lines);   // OBJ 1 基(0,1,2 → 1,2,3)
        Assert.Contains("f 1 3 4", lines);
        Assert.Contains("v 0 0 0", lines);
    }

    [Fact]
    public void Ply_header_counts_and_0based_faces()
    {
        var text = MeshExport.ToPly(V, T);
        Assert.StartsWith("ply\n", text);
        Assert.Contains("format ascii 1.0", text);
        Assert.Contains("element vertex 4", text);
        Assert.Contains("element face 2", text);
        Assert.Contains("end_header", text);
        var lines = text.TrimEnd('\n').Split('\n');
        Assert.Contains("3 0 1 2", lines);   // PLY 0 基, 面前缀顶点数 3
        Assert.Contains("3 0 2 3", lines);
    }

    [Fact]
    public void Stl_facets_and_normals()
    {
        var text = MeshExport.ToStlAscii(V, T);
        Assert.StartsWith("solid ", text);
        Assert.EndsWith("endsolid PitMine3D_Kylin\n", text);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "facet normal").Count);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(text, "vertex ").Count);   // 2 三角×3 顶点
        Assert.Contains("facet normal 0 0 1", text);   // z=0 面 CCW → +Z 法向
    }

    [Fact]
    public void ByExtension_dispatches()
    {
        Assert.StartsWith("ply", MeshExport.ByExtension("ply", V, T));
        Assert.StartsWith("solid", MeshExport.ByExtension(".stl", V, T));
        Assert.StartsWith("# PitMine3D", MeshExport.ByExtension("obj", V, T));
        Assert.StartsWith("# PitMine3D", MeshExport.ByExtension("unknown", V, T));   // 未知→OBJ
    }

    [Fact]
    public void Empty_mesh_valid_containers()
    {
        Assert.Contains("element vertex 0", MeshExport.ToPly(new List<(double, double, double)>(), new List<(int, int, int)>()));
        Assert.Contains("endsolid", MeshExport.ToStlAscii(new List<(double, double, double)>(), new List<(int, int, int)>()));
    }

    [Fact]
    public void Degenerate_triangle_skipped_in_stl_normal()
    {
        // 退化三角(共线)法向零 → 不崩, 仍输出 facet(法向 0 0 0)
        var v = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (2, 0, 0) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2) };
        Assert.Contains("facet normal 0 0 0", MeshExport.ToStlAscii(v, t));
    }
}
