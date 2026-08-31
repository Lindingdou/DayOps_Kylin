using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 分标高体素体积 —— 把封闭体的体素占用按高程带分层累计，出「各标高带体积」(忠实原「整体+分标高体积」)。
/// 取 isInside 谓词(如 WindingNumberTester.IsInsideClosed)，故与网格解耦、可用简单几何单测。纯逻辑。
/// </summary>
public static class VoxelBands
{
    public readonly record struct Band(double ZLow, double ZHigh, double Volume, long Cells);

    /// <summary>在包围盒内按 cell 体素化, 按 bandHeight 高程带累计占用体积。返回各带(低→高)。</summary>
    public static List<Band> ByElevation(
        double minX, double maxX, double minY, double maxY, double minZ, double maxZ,
        double cell, double bandHeight, Func<double, double, double, bool> isInside)
    {
        var res = new List<Band>();
        if (cell <= 1e-9 || maxZ <= minZ || isInside == null) return res;
        if (bandHeight <= 1e-9) bandHeight = (maxZ - minZ) / 10.0;
        int nBands = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / bandHeight));
        var cells = new long[nBands];
        double cellVol = cell * cell * cell;
        for (double z = minZ + cell * 0.5; z <= maxZ; z += cell)
        {
            int band = (int)((z - minZ) / bandHeight);
            if (band < 0) band = 0; if (band >= nBands) band = nBands - 1;
            for (double y = minY + cell * 0.5; y <= maxY; y += cell)
                for (double x = minX + cell * 0.5; x <= maxX; x += cell)
                    if (isInside(x, y, z)) cells[band]++;
        }
        for (int b = 0; b < nBands; b++)
        {
            double zl = minZ + b * bandHeight, zh = Math.Min(maxZ, zl + bandHeight);
            res.Add(new Band(zl, zh, cells[b] * cellVol, cells[b]));
        }
        return res;
    }

    /// <summary>各带体积 → CSV(z_low,z_high,volume,cells)。</summary>
    public static string ToCsv(IReadOnlyList<Band> bands)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder("z_low,z_high,volume,cells\n");
        foreach (var b in bands)
            sb.Append(b.ZLow.ToString("R", inv)).Append(',').Append(b.ZHigh.ToString("R", inv)).Append(',')
              .Append(b.Volume.ToString("R", inv)).Append(',').Append(b.Cells).Append('\n');
        return sb.ToString();
    }
}
