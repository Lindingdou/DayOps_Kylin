// 忠实移植自原 PitMine3D Modules/RoadLib/Network/RoadGraphSerializer.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 路网图 ⇄ JSON（「保存路网」持久化用）。节点 / 边 / 中线 / 属性全量序列化成紧凑 DTO，
/// 存进 GeoDataBase 的 road_network.graph_json（GeoDataBase 不解析，只当字符串存）。
/// 短字段名压体积；反序列化容错（坏边引用缺失节点则跳过，不抛）。
/// </summary>
public static class RoadGraphSerializer
{
    public static string ToJson(RoadGraph g)
    {
        var dto = new GraphDto();
        foreach (var n in g.Nodes)
            dto.N.Add(new NodeDto
            {
                Id = n.Id, T = (int)n.Type,
                X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z,
                Tph = n.ThroughputTph, Ref = n.RefId,
            });
        foreach (var e in g.Edges)
        {
            var c = new double[e.Centerline.Count * 3];
            for (int i = 0; i < e.Centerline.Count; i++)
            {
                var p = e.Centerline[i];
                c[3 * i] = p.X; c[3 * i + 1] = p.Y; c[3 * i + 2] = p.Z;
            }
            dto.E.Add(new EdgeDto
            {
                Id = e.Id, F = e.FromId, To = e.ToId, C = c,
                Lane = e.LaneCount, One = e.OneWay, Max = e.MaxLoadT, Spd = e.SpeedLimitKph,
                Pav = e.Pavement, St = (int)e.Status, Tmp = e.IsTemporary,
                Len = e.LengthM, Grade = e.GradePct,
                W = e.WidthM, Src = e.SourceRef,
                Cls = e.RoadClass is null ? -1 : (int)e.RoadClass.Value,
            });
        }
        return JsonSerializer.Serialize(dto);
    }

    public static RoadGraph FromJson(string? json)
    {
        var g = new RoadGraph();
        if (string.IsNullOrWhiteSpace(json)) return g;
        GraphDto? dto;
        try { dto = JsonSerializer.Deserialize<GraphDto>(json); }
        catch { return g; }
        if (dto is null) return g;

        foreach (var n in dto.N)
            g.AddNode(new RoadNode(n.Id, (RoadNodeType)n.T, new Point3d(n.X, n.Y, n.Z))
            { ThroughputTph = n.Tph, RefId = n.Ref });

        foreach (var e in dto.E)
        {
            if (g.GetNode(e.F) is null || g.GetNode(e.To) is null) continue;   // 容错：跳过坏边
            int m = (e.C?.Length ?? 0) / 3;
            var cl = new Point3d[m];
            for (int i = 0; i < m; i++) cl[i] = new Point3d(e.C![3 * i], e.C[3 * i + 1], e.C[3 * i + 2]);
            g.AddEdge(new RoadEdge(e.Id, e.F, e.To, cl)
            {
                LaneCount = e.Lane, OneWay = e.One, MaxLoadT = e.Max, SpeedLimitKph = e.Spd,
                Pavement = e.Pav, Status = (RoadEdgeStatus)e.St, IsTemporary = e.Tmp,
                LengthM = e.Len, GradePct = e.Grade,
                WidthM = e.W, SourceRef = e.Src,
                RoadClass = e.Cls < 0 ? null : (RoadSegmentClass)e.Cls,
            });
        }
        return g;
    }

    /// <summary>总里程 km（保存时冗余写库，列表免反序列化显示）。</summary>
    public static double TotalKm(RoadGraph g)
    {
        double m = 0;
        foreach (var e in g.Edges) m += e.LengthM;
        return m / 1000.0;
    }

    // ── DTO（公共属性 + 短名压体积，System.Text.Json 默认序列化属性） ──
    private sealed class GraphDto
    {
        public List<NodeDto> N { get; set; } = new();
        public List<EdgeDto> E { get; set; } = new();
    }

    private sealed class NodeDto
    {
        public string Id { get; set; } = "";
        public int T { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double Tph { get; set; }
        public string? Ref { get; set; }
    }

    private sealed class EdgeDto
    {
        public string Id { get; set; } = "";
        public string F { get; set; } = "";
        public string To { get; set; } = "";
        public double[] C { get; set; } = Array.Empty<double>();
        public int Lane { get; set; } = 1;
        public bool One { get; set; }
        public double Max { get; set; }
        public double Spd { get; set; }
        public string? Pav { get; set; }
        public int St { get; set; }
        public bool Tmp { get; set; }
        public double Len { get; set; }
        public double Grade { get; set; }
        /// <summary>路面宽 m（旧存档无此字段 → 0=未知，反序列化容错）。</summary>
        public double W { get; set; }
        /// <summary>来源标识（旧存档无此字段 → null）。</summary>
        public string? Src { get; set; }
        /// <summary>人工改判的线路类型；<b>-1 = 未改判（走自动判据）</b>。
        /// 旧存档无此字段 → 保持这里的初值 -1，正是「未改判」，无需另写迁移。</summary>
        public int Cls { get; set; } = -1;
    }
}
