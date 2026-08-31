using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>点云欧氏聚类(PointCluster)回归 —— 标准 Euclidean cluster extraction。合成分离簇验分组。</summary>
public class PointClusterTests
{
    [Fact]
    public void Two_separated_clusters_split_correctly()
    {
        // 簇A 在原点附近, 簇B 远在 (100,0,0)——相距 100 » radius
        var pts = new List<(double x, double y, double z)>
        {
            (0,0,0),(1,0,0),(0,1,0),          // A(近邻, 间距1)
            (100,0,0),(101,0,0),(100,1,0),    // B
        };
        var label = PointCluster.Euclidean(pts, radius: 2, minSize: 1, out int nc);
        Assert.Equal(2, nc);
        // A 三点同簇, B 三点同簇, A≠B
        Assert.Equal(label[0], label[1]); Assert.Equal(label[1], label[2]);
        Assert.Equal(label[3], label[4]); Assert.Equal(label[4], label[5]);
        Assert.NotEqual(label[0], label[3]);
    }

    [Fact]
    public void Radius_controls_connectivity()
    {
        // 两点相距 5; radius 2 → 2 簇; radius 6 → 1 簇
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (5, 0, 0) };
        PointCluster.Euclidean(pts, 2, 1, out int nc2);
        PointCluster.Euclidean(pts, 6, 1, out int nc6);
        Assert.Equal(2, nc2);
        Assert.Equal(1, nc6);
    }

    [Fact]
    public void MinSize_filters_small_clusters()
    {
        // 簇A 3点, 孤点B 1个; minSize=2 → B 归 -1, 仅 1 有效簇
        var pts = new List<(double x, double y, double z)>
        {
            (0,0,0),(1,0,0),(0,1,0),   // A
            (100,0,0),                 // B 孤点
        };
        var label = PointCluster.Euclidean(pts, 2, minSize: 2, out int nc);
        Assert.Equal(1, nc);
        Assert.Equal(-1, label[3]);          // 孤点被滤
        Assert.NotEqual(-1, label[0]);
    }

    [Fact]
    public void Largest_cluster_gets_id_zero()
    {
        // 大簇(4点) + 小簇(2点) → 大簇 id 0
        var pts = new List<(double x, double y, double z)>
        {
            (0,0,0),(1,0,0),(2,0,0),(3,0,0),   // 大簇(链式, 间距1)
            (100,0,0),(101,0,0),               // 小簇
        };
        var label = PointCluster.Euclidean(pts, 2, 1, out int nc);
        Assert.Equal(2, nc);
        Assert.Equal(0, label[0]);           // 大簇 id 0
        Assert.Equal(1, label[4]);           // 小簇 id 1
    }

    [Fact]
    public void Empty_safe()
    {
        Assert.Empty(PointCluster.Euclidean(new List<(double, double, double)>(), 1, 1, out int nc));
        Assert.Equal(0, nc);
    }
}
