using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 由某现状写实批次的高程点三角化建「现状三维面」(原 MeshEditLib.ModelUpdate.CurrentStateSurfaceBuilder:
/// 纯 C# Delaunay → PMBI 三角网 → 入图; 此处 → <see cref="MeshEntity"/> 由窗口层入场景)。现状面色 #8C9AA8 灰蓝。
/// </summary>
public static class CurrentStateSurfaceBuilder
{
    public static (bool ok, string message, MeshEntity? mesh) Build(IReadOnlyList<(double x, double y, double z)> pts, string layer, string name = "现状面")
    {
        if (pts.Count < 3) return (false, $"现状点不足（{pts.Count}），需 ≥3 个点", null);

        var xy = new List<(double x, double y)>(pts.Count);
        foreach (var p in pts) xy.Add((p.x, p.y));
        var tris = Delaunay.Triangulate(xy);
        if (tris.Count == 0) return (false, "点共线 / 无法构网", null);

        var mesh = new MeshEntity(name, pts, tris) { LayerName = layer, Cr = 0x8C / 255f, Cg = 0x9A / 255f, Cb = 0xA8 / 255f };
        return (true, $"现状面已建（{pts.Count} 点 / {tris.Count} 三角），图层「{layer}」", mesh);
    }
}
