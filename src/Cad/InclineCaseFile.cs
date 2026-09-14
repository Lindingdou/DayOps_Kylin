using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「量驱动开采模板」的离线用例（忠实原 <c>InclineCase</c>）：一次现场跑的全部输入（工作线 / 各层顶底板 TIN / 现状面 /
/// 前界 d、d_seam / α / 台阶参数 / 角部搭接 / 控制线 / 采出量剖面）装在一个对象里，存一个文件、脱 GUI 复跑。
/// 块体不进用例（太大且另有持久化），装好的 Profile 会存进来供 Stage1/2 离线复解。
/// </summary>
public sealed class InclineCase
{
    public string Note = "";
    public readonly List<WorkLineSamples> WorkLines = new();
    public readonly List<SeamSurfaces> Seams = new();
    public readonly List<double> DSeamBySeam = new();
    public TinSampler? CurrentSurface;
    public double AlphaDeg = 15;
    public double DStage1;
    public double ZTop, ZFloor;
    public BenchTemplateParams Bench = new();
    public readonly List<WorkLineCornerJoiner.CornerLink> CornerLinks = new();
    public readonly List<(double[] Xyz, int Line)> Boundaries = new();
    public CoalProfile? Profile;
    public double AnnualProductionWt;
    public double RecoveryTotalWt;
    public double TolPct = 5.0;

    /// <summary>一行复跑台阶（Build → 按控制线裁剪），与 Runner 同一条调用链。</summary>
    public BenchTemplateResult RunBenches(bool applyBoundaryClip = true)
    {
        var res = BenchTemplateBuilder.Build(WorkLines, Seams, CurrentSurface, DStage1, DSeamBySeam, AlphaDeg, ZTop, ZFloor, Bench, CornerLinks);
        if (applyBoundaryClip && res.Success && Boundaries.Count > 0) EndWallJoiner.ClipBenchesByBoundaries(res, Boundaries, WorkLines);
        return res;
    }

    /// <summary>一行复跑斜面 + 各层交线。</summary>
    public List<InclineSurface> RunSurfaces(IReadOnlyList<double>? advanceByLine = null)
    {
        var adv = new double[WorkLines.Count];
        for (int i = 0; i < adv.Length; i++) adv[i] = (advanceByLine != null && i < advanceByLine.Count) ? advanceByLine[i] : DStage1;
        var ext = WorkLineCornerJoiner.ComputeEndExtensions(WorkLines, CornerLinks, AlphaDeg, ZTop, ZFloor, adv);
        var list = new List<InclineSurface>();
        for (int i = 0; i < WorkLines.Count; i++) list.Add(InclineSurfaceBuilder.BuildForWorkLine(WorkLines[i], AlphaDeg, ZTop, ZFloor, adv[i], Seams, ext[i].Start, ext[i].End));
        return list;
    }

    public Stage1Result? RunStage1() => Profile is { Success: true } ? InclineVolumeEngine.SolveStage1(Profile, AnnualProductionWt, TolPct) : null;
    public Stage2Result? RunStage2(double? stage1Advance = null)
        => Profile is { Success: true } ? InclineVolumeEngine.SolveStage2(Profile, stage1Advance ?? RunStage1()?.Advance ?? DStage1, TolPct, RecoveryTotalWt) : null;

    /// <summary>人读摘要（用例旁边落一份 .txt）。</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 量驱动开采模板 离线用例   {(string.IsNullOrEmpty(Note) ? "(无备注)" : Note)}");
        sb.AppendLine($"α={AlphaDeg:0.###}°  d(Stage1)={DStage1:0.###}m  z[{ZFloor:0.###}, {ZTop:0.###}]");
        sb.AppendLine($"量目标: 年产量={AnnualProductionWt:0.#}万t  回采总量={RecoveryTotalWt:0.#}万t  容差±{TolPct:0.#}%");
        sb.AppendLine($"台阶参数: 岩台阶高={Bench.RockBenchH:0.###}m 煤坡角={Bench.CoalFaceDeg:0.###}° 岩坡角={Bench.RockFaceDeg:0.###}° 总坡角={Bench.WorkAngleDeg:0.###}° 最小平盘={Bench.MinBerm:0.###}m 端帮衔接={(Bench.JoinEndWall ? "开" : "关")}");
        if (Bench.CoalBenchHBySeam != null)
        {
            sb.Append("逐层煤台阶高: ");
            for (int i = 0; i < Bench.CoalBenchHBySeam.Count; i++)
            {
                double h = Bench.CoalBenchHBySeam[i];
                sb.Append(i > 0 ? ", " : "").Append(h < 0 ? "采全高" : h > 0.5 ? h.ToString("0.###") : $"跟岩台阶({Bench.RockBenchH:0.#})");
            }
            sb.AppendLine();
        }
        sb.AppendLine($"工作线 {WorkLines.Count} 条:");
        for (int i = 0; i < WorkLines.Count; i++)
        {
            var w = WorkLines[i];
            double cx = 0, cy = 0, cz = 0;
            foreach (var b in w.Baseline) { cx += b.X; cy += b.Y; cz += b.Z; }
            int n = Math.Max(1, w.Baseline.Count);
            double dx = 0, dy = 0;
            foreach (var s in w.Samples) { dx += s.Dx; dy += s.Dy; }
            double dl = Math.Sqrt(dx * dx + dy * dy); if (dl > 1e-9) { dx /= dl; dy /= dl; }
            sb.AppendLine($"  线{i + 1}: 基线{w.Baseline.Count}点 段{w.Samples.Count} 中心({cx / n:0.#},{cy / n:0.#},{cz / n:0.#}) 推进di=({dx:0.###},{dy:0.###}) 方位{Math.Atan2(dy, dx) * 180.0 / Math.PI:0.#}° {(w.AdvanceMode == 2 ? "扇形" : "平行")}/{(w.DirMode == 1 ? "逐段" : "统一")}{(w.Closed ? " 闭合" : "")}");
        }
        sb.AppendLine($"煤层 {Seams.Count} 层:");
        for (int i = 0; i < Seams.Count; i++)
        {
            var s = Seams[i];
            double ds = i < DSeamBySeam.Count ? DSeamBySeam[i] : DStage1;
            sb.AppendLine($"  层{i + 1}「{s.Name}」d_seam={ds:0.###}m 容重={s.Density:0.###} 增量={s.IncrementWt:0.#}万t 顶板{Tin(s.Roof)} 底板{Tin(s.Floor)}{(s.Ready ? "" : "  ← 未就绪(Build 会跳过)")}");
        }
        sb.AppendLine($"现状面: {Tin(CurrentSurface)}");
        sb.AppendLine($"角部搭接 {CornerLinks.Count} 处" + (CornerLinks.Count == 0 ? "" : ":"));
        foreach (var c in CornerLinks) sb.AppendLine($"  线{c.A + 1}·{(c.AStart ? "首" : "末")}端 ↔ 线{c.B + 1}·{(c.BStart ? "首" : "末")}端");
        sb.AppendLine($"控制线/边界线 {Boundaries.Count} 条" + (Boundaries.Count == 0 ? "" : ":"));
        foreach (var b in Boundaries) sb.AppendLine($"  绑线{b.Line + 1} {b.Xyz.Length / 3}点");
        if (Profile is { Success: true })
            sb.AppendLine($"采出量剖面: Δ={Profile.SliceWidth:0.###}m {Profile.SeamCount}层 {Profile.LineCount}线 上限={Profile.TotalMaxWt:0.#}万t 煤cell={Profile.CoalCellCount:N0}");
        else sb.AppendLine("采出量剖面: 无（Stage1/2 离线复解不了，台阶那段不受影响）");
        return sb.ToString();

        static string Tin(TinSampler? t)
        {
            if (t == null) return "无";
            t.GetGeometry(out var v, out var tri);
            return $"{v.Length / 3:N0}点/{tri.Length / 3:N0}三角 x[{t.MinX:0.#},{t.MaxX:0.#}] y[{t.MinY:0.#},{t.MaxY:0.#}] z[{t.MinZ:0.#},{t.MaxZ:0.#}]";
        }
    }
}

/// <summary>离线用例的读写（'PMIC'，小端，坐标 float64；与原版同格式，原版落的 .case 可直接读）。</summary>
public static class InclineCaseFile
{
    private const uint Magic = 0x43494D50;   // 'PMIC'
    private const int  Version = 2;

    /// <summary>默认落盘位置；环境变量 PITMINE_INCLINE_DUMP_CASE 可整个覆盖。</summary>
    public static string DefaultDumpPath
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("PITMINE_INCLINE_DUMP_CASE");
            if (!string.IsNullOrWhiteSpace(env)) return env!;
            return Path.Combine(Path.GetTempPath(), "pitmine_benchdump", "REAL_latest.case");
        }
    }

    public static bool TrySave(string path, InclineCase c, out string err)
    {
        err = "";
        if (c == null) { err = "用例为空"; return false; }
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(Magic); w.Write(Version); w.Write(c.Note ?? "");
                w.Write(c.AlphaDeg); w.Write(c.DStage1); w.Write(c.ZTop); w.Write(c.ZFloor);
                w.Write(c.AnnualProductionWt); w.Write(c.RecoveryTotalWt); w.Write(c.TolPct);
                var p = c.Bench ?? new BenchTemplateParams();
                w.Write(p.RockBenchH); w.Write(p.CoalFaceDeg); w.Write(p.RockFaceDeg); w.Write(p.WorkAngleDeg); w.Write(p.MinBerm); w.Write(p.JoinEndWall);
                if (p.CoalBenchHBySeam == null) w.Write(-1);
                else { w.Write(p.CoalBenchHBySeam.Count); foreach (double h in p.CoalBenchHBySeam) w.Write(h); }
                w.Write(c.WorkLines.Count);
                foreach (var wl in c.WorkLines) WriteWorkLine(w, wl);
                w.Write(c.Seams.Count);
                for (int i = 0; i < c.Seams.Count; i++)
                {
                    var s = c.Seams[i];
                    w.Write(s?.Name ?? ""); w.Write(s?.Attribute ?? ""); w.Write(s?.Category ?? "");
                    w.Write(s?.Density ?? 0); w.Write(s?.IncrementWt ?? 0);
                    w.Write(i < c.DSeamBySeam.Count ? c.DSeamBySeam[i] : c.DStage1);
                    WriteTin(w, s?.Roof); WriteTin(w, s?.Floor);
                }
                WriteTin(w, c.CurrentSurface);
                w.Write(c.CornerLinks.Count);
                foreach (var k in c.CornerLinks) { w.Write(k.A); w.Write(k.AStart); w.Write(k.B); w.Write(k.BStart); }
                w.Write(c.Boundaries.Count);
                foreach (var b in c.Boundaries)
                {
                    w.Write(b.Line);
                    var xyz = b.Xyz ?? Array.Empty<double>();
                    w.Write(xyz.Length);
                    foreach (double v in xyz) w.Write(v);
                }
                MineProfileFile.WriteProfile(w, c.Profile, MineProfileFile.SectionV2);
            }
            try { File.WriteAllText(Path.ChangeExtension(path, ".txt"), c.Summary(), new UTF8Encoding(true)); } catch { }
            return true;
        }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    public static InclineCase? TryLoad(string path, out string err)
    {
        err = "";
        try
        {
            if (!File.Exists(path)) { err = "文件不存在: " + path; return null; }
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadUInt32() != Magic) { err = "魔数不符（不是 PMIC 用例）"; return null; }
            int ver = r.ReadInt32();
            if (ver < 1 || ver > Version) { err = $"版本不支持({ver})，本程序读 1..{Version}"; return null; }
            var c = new InclineCase { Note = r.ReadString() };
            c.AlphaDeg = r.ReadDouble(); c.DStage1 = r.ReadDouble(); c.ZTop = r.ReadDouble(); c.ZFloor = r.ReadDouble();
            c.AnnualProductionWt = r.ReadDouble(); c.RecoveryTotalWt = r.ReadDouble(); c.TolPct = r.ReadDouble();
            var p = new BenchTemplateParams { RockBenchH = r.ReadDouble(), CoalFaceDeg = r.ReadDouble(), RockFaceDeg = r.ReadDouble(), WorkAngleDeg = r.ReadDouble(), MinBerm = r.ReadDouble(), JoinEndWall = r.ReadBoolean() };
            int nCoalH = r.ReadInt32();
            if (nCoalH >= 0) { var hs = new double[nCoalH]; for (int i = 0; i < nCoalH; i++) hs[i] = r.ReadDouble(); p.CoalBenchHBySeam = hs; }
            c.Bench = p;
            int nWl = r.ReadInt32();
            for (int i = 0; i < nWl; i++) c.WorkLines.Add(ReadWorkLine(r));
            int nSeam = r.ReadInt32();
            for (int i = 0; i < nSeam; i++)
            {
                var s = new SeamSurfaces { Name = r.ReadString(), Attribute = r.ReadString(), Category = r.ReadString(), Density = r.ReadDouble(), IncrementWt = r.ReadDouble() };
                c.DSeamBySeam.Add(r.ReadDouble());
                s.Roof = ReadTin(r); s.Floor = ReadTin(r);
                c.Seams.Add(s);
            }
            c.CurrentSurface = ReadTin(r);
            int nLink = r.ReadInt32();
            for (int i = 0; i < nLink; i++) { int a = r.ReadInt32(); bool aS = r.ReadBoolean(); int b = r.ReadInt32(); bool bS = r.ReadBoolean(); c.CornerLinks.Add(new WorkLineCornerJoiner.CornerLink(a, aS, b, bS)); }
            int nB = r.ReadInt32();
            for (int i = 0; i < nB; i++)
            {
                int line = r.ReadInt32(); int n = r.ReadInt32();
                var xyz = new double[n];
                for (int k = 0; k < n; k++) xyz[k] = r.ReadDouble();
                c.Boundaries.Add((xyz, line));
            }
            c.Profile = MineProfileFile.ReadProfile(r, ver);
            return c;
        }
        catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    private static void WriteTin(BinaryWriter w, TinSampler? t)
    {
        if (t == null) { w.Write(0); w.Write(0); return; }
        t.GetGeometry(out var v, out var tri);
        w.Write(v.Length); foreach (double d in v) w.Write(d);
        w.Write(tri.Length); foreach (int i in tri) w.Write(i);
    }

    private static TinSampler? ReadTin(BinaryReader r)
    {
        int nv = r.ReadInt32();
        var v = new double[nv];
        for (int i = 0; i < nv; i++) v[i] = r.ReadDouble();
        int nt = r.ReadInt32();
        var tri = new int[nt];
        for (int i = 0; i < nt; i++) tri[i] = r.ReadInt32();
        return nv == 0 ? null : TinSampler.TryBuild(v, tri);
    }

    private static void WriteWorkLine(BinaryWriter w, WorkLineSamples? g)
    {
        g ??= new WorkLineSamples();
        w.Write(g.AdvanceMode); w.Write(g.DirMode); w.Write(g.Closed); w.Write(0f);   // ArrowLength：原版工作线几何的箭头长，Kylin 不存（占位保格式）
        w.Write(g.Baseline.Count);
        foreach (var b in g.Baseline) { w.Write(b.X); w.Write(b.Y); w.Write(b.Z); }
        w.Write(g.Samples.Count);
        foreach (var s in g.Samples) { w.Write(s.Ax); w.Write(s.Ay); w.Write(s.Az); w.Write(s.Dx); w.Write(s.Dy); }
        w.Write(g.HasFanParams); w.Write(g.RotDir);
        w.Write(g.PivotX); w.Write(g.PivotY); w.Write(g.PivotZ);
    }

    private static WorkLineSamples ReadWorkLine(BinaryReader r)
    {
        var g = new WorkLineSamples { AdvanceMode = r.ReadByte(), DirMode = r.ReadByte(), Closed = r.ReadBoolean() };
        r.ReadSingle();   // ArrowLength
        int nb = r.ReadInt32();
        for (int i = 0; i < nb; i++) g.Baseline.Add((r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
        int ns = r.ReadInt32();
        for (int i = 0; i < ns; i++) g.Samples.Add((r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
        g.HasFanParams = r.ReadBoolean(); g.RotDir = r.ReadByte();
        g.PivotX = r.ReadDouble(); g.PivotY = r.ReadDouble(); g.PivotZ = r.ReadDouble();
        g.Success = g.Baseline.Count >= 2;
        if (!g.Success) g.Error = "基线点不足 2";
        return g;
    }
}
