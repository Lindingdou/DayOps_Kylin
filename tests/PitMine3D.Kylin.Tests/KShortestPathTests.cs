using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>备选路径 Yen K 最短路回归。</summary>
public class KShortestPathTests
{
    // 构造一个有两条平行路径的图：
    //   0 ─10─ 1 ─10─ 3   (上路, 总长 20)
    //   0 ─12─ 2 ─12─ 3   (下路, 总长 24)
    private static List<List<(int to, double w)>> DiamondGraph()
    {
        var adj = new List<List<(int, double)>>();
        for (int i = 0; i < 4; i++) adj.Add(new List<(int, double)>());
        void E(int u, int v, double w) { adj[u].Add((v, w)); adj[v].Add((u, w)); }
        E(0, 1, 10); E(1, 3, 10);   // 上
        E(0, 2, 12); E(2, 3, 12);   // 下
        return adj;
    }

    [Fact]
    public void Finds_two_ranked_paths()
    {
        var adj = DiamondGraph();
        var paths = RoadNetwork.KShortestPaths(adj, 0, 3, 3);
        Assert.Equal(2, paths.Count);                       // 只有两条简单路径
        Assert.Equal(new List<int> { 0, 1, 3 }, paths[0]);  // 最短(20)在前
        Assert.Equal(new List<int> { 0, 2, 3 }, paths[1]);  // 次短(24)
        Assert.True(RoadNetwork.PathWeight(adj, paths[0]) <= RoadNetwork.PathWeight(adj, paths[1]));
    }

    [Fact]
    public void K1_returns_only_shortest()
    {
        var adj = DiamondGraph();
        var paths = RoadNetwork.KShortestPaths(adj, 0, 3, 1);
        Assert.Single(paths);
        Assert.Equal(20.0, RoadNetwork.PathWeight(adj, paths[0]), 6);
    }

    [Fact]
    public void Disconnected_returns_empty()
    {
        var adj = new List<List<(int to, double w)>>();
        for (int i = 0; i < 3; i++) adj.Add(new List<(int, double)>());
        adj[0].Add((1, 5)); adj[1].Add((0, 5));   // 2 号孤立
        var paths = RoadNetwork.KShortestPaths(adj, 0, 2, 3);
        Assert.Empty(paths);
    }

    [Fact]
    public void DijkstraExcluding_avoids_excluded_edge()
    {
        var adj = DiamondGraph();
        // 禁掉上路首边 (0,1) → 只能走下路
        var path = RoadNetwork.DijkstraExcluding(adj, 0, 3,
            new HashSet<int>(), new HashSet<(int, int)> { (0, 1) });
        Assert.Equal(new List<int> { 0, 2, 3 }, path);
    }

    [Fact]
    public void Three_paths_when_available()
    {
        // 加第三条中路 0-4-3 长 22
        var adj = DiamondGraph();
        adj.Add(new List<(int, double)>());   // node 4
        adj[0].Add((4, 11)); adj[4].Add((0, 11));
        adj[4].Add((3, 11)); adj[3].Add((4, 11));
        var paths = RoadNetwork.KShortestPaths(adj, 0, 3, 3);
        Assert.Equal(3, paths.Count);
        // 升序：20, 22, 24
        Assert.Equal(20.0, RoadNetwork.PathWeight(adj, paths[0]), 6);
        Assert.Equal(22.0, RoadNetwork.PathWeight(adj, paths[1]), 6);
        Assert.Equal(24.0, RoadNetwork.PathWeight(adj, paths[2]), 6);
    }
}
