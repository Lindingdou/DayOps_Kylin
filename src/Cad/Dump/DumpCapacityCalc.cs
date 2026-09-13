using System;

namespace PitMine3D.Kylin.Cad.Dump;

/// <summary>
/// 「排土场容量校核」的算量核（原版走内核 VolumeSplitFromHandles 两期填挖方；这里按同口径托管重算）：
/// 在【排土场坡面】的平面包围盒上按格网采样，dz = 坡面 − 现状面；dz &gt; minDz 计填、dz &lt; −minDz 计挖，
/// 只算两张面都采到的格。填方体积 = 这个设计形态的总容积（几何上限）；挖方在排土场语境下是异常信号（坡面穿地）。
/// </summary>
public static class DumpCapacityCalc
{
    public sealed class Result
    {
        public double FillM3, CutM3, AreaFillM2, AreaCutM2;
        public int Cells, Sampled;
        public bool Ok => Sampled > 0;
    }

    public static Result Compute(IRoadZSampler terrain, IRoadZSampler dumpFace,
                                 double minX, double minY, double maxX, double maxY, double cellM, double minDz)
    {
        var r = new Result();
        if (terrain == null || dumpFace == null) return r;
        if (cellM <= 1e-6) cellM = 2;
        if (minDz < 0) minDz = 0;
        int nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellM));
        int ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellM));
        double a = cellM * cellM;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                r.Cells++;
                double x = minX + (i + 0.5) * cellM, y = minY + (j + 0.5) * cellM;
                if (!dumpFace.TrySample(x, y, out double zf) || !terrain.TrySample(x, y, out double zt)) continue;
                r.Sampled++;
                double dz = zf - zt;
                if (dz > minDz) { r.FillM3 += dz * a; r.AreaFillM2 += a; }
                else if (dz < -minDz) { r.CutM3 += -dz * a; r.AreaCutM2 += a; }
            }
        return r;
    }
}
