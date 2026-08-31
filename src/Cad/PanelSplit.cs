using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>采区划分取向。</summary>
public enum SplitObjective { FixedN, ByLife, ByCapacity }

/// <summary>排土方式。</summary>
public enum DumpMode { External, Internal }

/// <summary>一个采区（忠实移植原 <c>MiningPanel</c> 的落地相关字段）。</summary>
public sealed class MiningPanel
{
    public int Index;
    public string Name = "采区1";
    public double CoalWanT;         // 煤量 万t
    public double WasteWanM3;       // 岩量 万m³
    public double StripRatio;       // 剥采比 m³/t
    public int Order;               // 开采序(1=首采区)
    public double AdvanceAzimuthDeg;
    public double WorkingLineLengthM;
    public double AdvanceRateMpa;
    public DumpMode Dump = DumpMode.External;
    public double DumpSwitchYear;
    public double ServiceLifeYears;
    public double MinX, MinY, MaxX, MaxY;   // 平面外接矩形
}

/// <summary>采区划分最小规划参数（原 MiningProgramPlan 中 PanelSplitter 实际用到的字段, 默认值照搬）。</summary>
public sealed class MiningPlanParams
{
    public SplitObjective Split = SplitObjective.ByLife;
    public int PanelCount = 4;
    public double TargetCapacityWanTa = 400;   // 万t/a
    public double TargetServiceLifeYears = 30;
    public bool InnerDumpEnabled = true;
    public double InnerDumpStartWidthM = 300;
    public double BenchHeightM = 12;
    public double TargetAdvanceRateMpa = 0;    // 0=按产能反算
    public double AdvanceAzimuthDeg = 0;       // 推进方位
    public double WorkingLineLengthM = 0;      // 0=取场宽
}

/// <summary>平面剥采比场（忠实移植原 <c>BlockModelLib.Domain.StripRatioField</c> 数据模型 + 从稀疏块体聚合的托管采样）。</summary>
public sealed class StripRatioField
{
    public int Nx, Ny;
    public double Dx, Dy, Ox, Oy, Density;
    public double[] CoalVol = Array.Empty<double>();
    public double[] WasteVol = Array.Empty<double>();
    public int Idx(int i, int j) => i + j * Nx;

    /// <summary>
    /// 从稀疏块体聚成剥采比场（托管重算原 Sampler 的聚合：逐 XY 列累 煤/岩 体积）。
    /// 煤岩判别以品位阈值 cutoff 替代原属性分类器(本块体仅品位); 假定块体等大(体素/规则块)。
    /// </summary>
    public static StripRatioField? FromBlocks(IReadOnlyList<(double X, double Y, double Z, double Size, double Grade)> blocks,
        double cutoff, double density)
    {
        if (blocks == null || blocks.Count == 0) return null;
        double cell = blocks[0].Size > 1e-9 ? blocks[0].Size : 1.0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var b in blocks)
        {
            if (b.X < minX) minX = b.X; if (b.Y < minY) minY = b.Y;
            if (b.X > maxX) maxX = b.X; if (b.Y > maxY) maxY = b.Y;
        }
        int nx = (int)Math.Round((maxX - minX) / cell) + 1;
        int ny = (int)Math.Round((maxY - minY) / cell) + 1;
        if (nx <= 0 || ny <= 0) return null;
        int ncol = nx * ny;
        var f = new StripRatioField
        {
            Nx = nx, Ny = ny, Dx = cell, Dy = cell, Ox = minX - cell / 2, Oy = minY - cell / 2, Density = density,
            CoalVol = new double[ncol], WasteVol = new double[ncol],
        };
        double cellVol = cell * cell * cell;
        foreach (var b in blocks)
        {
            int i = Math.Clamp((int)Math.Round((b.X - minX) / cell), 0, nx - 1);
            int j = Math.Clamp((int)Math.Round((b.Y - minY) / cell), 0, ny - 1);
            int col = i + j * nx;
            if (b.Grade >= cutoff) f.CoalVol[col] += cellVol; else f.WasteVol[col] += cellVol;
        }
        return f;
    }
}

/// <summary>
/// 采区划分（忠实移植原 <c>PlanLib.BoundaryOptimization.PanelSplitter.Split</c>）——沿推进轴把剥采比场
/// 切成 N 个储量均衡采区(等煤量/等推进距离), 逐采区出 煤/岩/剥采比/工作线长/推进度/序/内外排/服务年限。
/// 纯几何、可单测。原吃完整 MiningProgramPlan, 此吃最小参数(实际用到的字段)。
/// </summary>
public static class PanelSplitter
{
    /// <summary>产能反算推进度 v = 产能(万t/a)·1e4 /(工作线长·台阶高·煤密度)。</summary>
    public static double AdvanceRateFrom(double capacityWanTa, double lengthM, double benchM, double rho)
        => (lengthM <= 0 || benchM <= 0) ? 0 : capacityWanTa * 1e4 / (lengthM * benchM * rho);

    public static List<MiningPanel> Split(MiningPlanParams plan, StripRatioField field)
    {
        var panels = new List<MiningPanel>();
        double az = ((plan.AdvanceAzimuthDeg % 360) + 360) % 360;
        double a180 = az % 180;
        bool axisIsY = a180 < 45 || a180 >= 135;
        bool forwardPos = axisIsY ? (az < 90 || az > 270) : (az > 0 && az < 180);

        int slabCount = axisIsY ? field.Ny : field.Nx;
        if (slabCount <= 0) return panels;

        var coal = new double[slabCount];
        var waste = new double[slabCount];
        for (int s = 0; s < slabCount; s++)
        {
            double cv = 0, wv = 0;
            if (axisIsY) { for (int i = 0; i < field.Nx; i++) { int c = field.Idx(i, s); cv += field.CoalVol[c]; wv += field.WasteVol[c]; } }
            else { for (int j = 0; j < field.Ny; j++) { int c = field.Idx(s, j); cv += field.CoalVol[c]; wv += field.WasteVol[c]; } }
            coal[s] = cv; waste[s] = wv;
        }

        var seq = Enumerable.Range(0, slabCount);
        int[] order = (forwardPos ? seq : seq.Reverse()).ToArray();

        double rho = field.Density;
        double totalCoalVol = coal.Sum();
        double totalCoalWanT = totalCoalVol * rho / 1e4;
        double annualCap = plan.TargetCapacityWanTa > 0 ? plan.TargetCapacityWanTa : Math.Max(1, totalCoalWanT / 30);
        double L = plan.WorkingLineLengthM > 0 ? plan.WorkingLineLengthM : (axisIsY ? field.Nx * field.Dx : field.Ny * field.Dy);
        double H = plan.BenchHeightM > 0 ? plan.BenchHeightM : 12;
        double advRate = plan.TargetAdvanceRateMpa > 0 ? plan.TargetAdvanceRateMpa : AdvanceRateFrom(annualCap, L, H, rho);
        double tSwitch = (plan.InnerDumpEnabled && advRate > 0) ? plan.InnerDumpStartWidthM / advRate : double.PositiveInfinity;

        int n = DecidePanelCount(plan, totalCoalWanT, annualCap);
        bool byArea = plan.Split == SplitObjective.FixedN;
        double totalMetric = byArea ? slabCount : totalCoalVol;
        double target = totalMetric / n;
        double acc = 0, panelCoal = 0, panelWaste = 0, cumLife = 0;
        int placed = 0, panelNo = 1, sMin = int.MaxValue, sMax = int.MinValue;

        for (int t = 0; t < order.Length; t++)
        {
            int s = order[t];
            if (s < sMin) sMin = s;
            if (s > sMax) sMax = s;
            panelCoal += coal[s]; panelWaste += waste[s]; acc += byArea ? 1.0 : coal[s];
            bool last = t == order.Length - 1;
            bool cut = placed < n - 1 && acc >= target * (placed + 1) - 1e-9;
            if (!cut && !last) continue;

            double coalWanT = panelCoal * rho / 1e4;
            double wasteWanM3 = panelWaste / 1e4;
            double life = annualCap > 0 ? coalWanT / annualCap : 0;
            double startTime = cumLife;
            bool internalDump = plan.InnerDumpEnabled && panelNo > 1;

            double pMinX, pMinY, pMaxX, pMaxY;
            if (axisIsY)
            {
                pMinX = field.Ox; pMaxX = field.Ox + field.Nx * field.Dx;
                pMinY = field.Oy + sMin * field.Dy; pMaxY = field.Oy + (sMax + 1) * field.Dy;
            }
            else
            {
                pMinY = field.Oy; pMaxY = field.Oy + field.Ny * field.Dy;
                pMinX = field.Ox + sMin * field.Dx; pMaxX = field.Ox + (sMax + 1) * field.Dx;
            }

            panels.Add(new MiningPanel
            {
                Index = panelNo, Name = $"采区{panelNo}", Order = panelNo,
                CoalWanT = Math.Round(coalWanT, 0), WasteWanM3 = Math.Round(wasteWanM3, 0),
                StripRatio = coalWanT > 1e-9 ? Math.Round(wasteWanM3 / coalWanT, 2) : 0,
                AdvanceAzimuthDeg = plan.AdvanceAzimuthDeg,
                WorkingLineLengthM = Math.Round(L, 0), AdvanceRateMpa = Math.Round(advRate, 0),
                Dump = internalDump ? DumpMode.Internal : DumpMode.External,
                DumpSwitchYear = internalDump ? Math.Round(Math.Max(startTime, double.IsInfinity(tSwitch) ? startTime : tSwitch), 1) : 0,
                ServiceLifeYears = Math.Round(life, 1),
                MinX = pMinX, MinY = pMinY, MaxX = pMaxX, MaxY = pMaxY,
            });

            cumLife += life; panelNo++; placed++;
            panelCoal = 0; panelWaste = 0; sMin = int.MaxValue; sMax = int.MinValue;
        }
        return panels;
    }

    private static int DecidePanelCount(MiningPlanParams plan, double totalCoalWanT, double annualCap)
    {
        int suggested = Math.Max(1, plan.PanelCount);
        if (plan.Split == SplitObjective.FixedN) return suggested;
        double perPanelLife = (plan.TargetServiceLifeYears > 0 && suggested > 0) ? plan.TargetServiceLifeYears / suggested : 5;
        if (perPanelLife < 1) perPanelLife = 1;
        int n;
        if (plan.Split == SplitObjective.ByCapacity)
            n = (int)Math.Round(totalCoalWanT / Math.Max(1, annualCap * perPanelLife));
        else
            n = (int)Math.Round((annualCap > 0 ? totalCoalWanT / annualCap : 0) / perPanelLife);
        return Math.Clamp(n, 1, 16);
    }
}
