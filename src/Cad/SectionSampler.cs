using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 块体侧采样器(忠实移植原 BlockModelLib.Domain.SectionSampler.SampleLayers 的裁剪语义)——
/// 按 Z 层聚合煤/岩体积, 支持<b>逐层裁剪多边形</b>(layerClip(k)=第 k 层境界轮廓, 出圈入资源);
/// 圈入量(CoalVol/WasteVol)受裁, 全模型总煤(TotalCoalVol)<b>不受裁</b>(算回收率)。纯逻辑、可单测。
/// 与 <see cref="ResourceProfileLite"/>(不裁全收, 供 SolveDepth)互补: 本类出真实圈入资源(配合 <see cref="PitEnvelope"/> 逐层境界)。
/// </summary>
public static class SectionSampler
{
    /// <summary>裁剪后的资源纵剖面。CoalVol/WasteVol=逐层圈入量; TotalCoalVol=全模型煤(回收率分母)。</summary>
    public sealed class ClippedProfile
    {
        public int Nz;
        public double Dz;
        public double Density;
        public double[] CoalVol = Array.Empty<double>();
        public double[] WasteVol = Array.Empty<double>();
        public double TotalCoalVol;   // 全模型煤体积(不受裁, m³)
        public double EnclosedCoalVol => CoalVol.Sum();
        public double CoalT => EnclosedCoalVol * Density;      // 圈入煤量 t
        public double WasteM3 => WasteVol.Sum();               // 圈入岩量 m³
        public double StripRatioM3PerT => CoalT > 1e-9 ? WasteM3 / CoalT : 0;   // 圈内剥采比 m³/t
        public double RecoveryPct => TotalCoalVol > 1e-9 ? EnclosedCoalVol / TotalCoalVol * 100 : 0;  // 回收率 %
    }

    /// <summary>
    /// 按 Z 层聚合, 每层用 layerClip(k) 的多边形裁: null=该层不裁(全收); 顶点&lt;3(空)=该层全出圈(如坑底以下);
    /// ≥3=逐块 PointInPolygon 判内外。忠实原 SampleLayers: 圈入受裁, TotalCoalVol 不受裁。层序同 ResourceProfileLite
    /// (k=0 底 minZ, k=Nz-1 顶)。块空返回 null。
    /// </summary>
    public static ClippedProfile? SampleClipped(
        IReadOnlyList<(double X, double Y, double Z, double Size, double Grade)> blocks,
        double cutoff, double density,
        Func<int, IReadOnlyList<(double x, double y)>?> layerClip)
    {
        if (blocks == null || blocks.Count == 0) return null;
        double cell = blocks[0].Size > 1e-9 ? blocks[0].Size : 1.0;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var b in blocks) { if (b.Z < minZ) minZ = b.Z; if (b.Z > maxZ) maxZ = b.Z; }
        int nz = (int)Math.Round((maxZ - minZ) / cell) + 1;
        if (nz <= 0) return null;
        var p = new ClippedProfile { Nz = nz, Dz = cell, Density = density, CoalVol = new double[nz], WasteVol = new double[nz] };
        double cellVol = cell * cell * cell;
        foreach (var b in blocks)
        {
            int k = Math.Clamp((int)Math.Round((b.Z - minZ) / cell), 0, nz - 1);
            bool isCoal = b.Grade >= cutoff;
            if (isCoal) p.TotalCoalVol += cellVol;              // 全模型煤(不受裁)
            var clip = layerClip(k);
            if (clip != null)
            {
                if (clip.Count < 3) continue;                   // 空多边形 → 该层全出圈
                if (!LineMath.PointInPolygon(b.X, b.Y, clip)) continue;  // 出圈 → 不计圈入
            }
            if (isCoal) p.CoalVol[k] += cellVol; else p.WasteVol[k] += cellVol;
        }
        return p;
    }
}
