using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 剖面的读写（忠实原 <c>MineProfileFile</c>，'PMRP'）—— 整条链上唯一需要落盘的中间产物：煤剖面（s 轴吨 + u 轴实方）、岩剖面、指纹。
/// 台架用例（<see cref="InclineCaseFile"/> 的 Profile 一节）与生产中间文件共用同一格式；生产文件加载必须校指纹。
/// </summary>
public static class MineProfileFile
{
    private const uint Magic = 0x50524D50;   // 'PMRP'
    public const int  FileVersion = 1;
    public const int  SectionV1 = 1, SectionV2 = 2;

    public static bool TrySave(string path, CoalProfile? p, out string err)
    {
        err = "";
        if (p == null) { err = "剖面为空"; return false; }
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs, Encoding.UTF8);
            w.Write(Magic); w.Write(FileVersion);
            WriteProfile(w, p, SectionV2);
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    /// <summary>读中间文件。expect 非空时逐项比指纹，不匹配返回 null 并说清是哪一项变了。</summary>
    public static CoalProfile? TryLoad(string path, out string err, ProfileProvenance? expect = null)
    {
        err = "";
        try
        {
            if (!File.Exists(path)) { err = $"文件不存在: {path}"; return null; }
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadUInt32() != Magic) { err = "不是剖面文件（magic 不对）"; return null; }
            int ver = r.ReadInt32();
            if (ver < 1 || ver > FileVersion) { err = $"版本不支持({ver})，本程序读 1..{FileVersion}"; return null; }
            var p = ReadProfile(r, SectionV2);
            if (p == null) { err = "文件里没有剖面"; return null; }
            if (expect != null)
            {
                var got = p.Rock?.Provenance;
                if (got == null) { err = "剖面缓存失效：这份文件没有指纹（旧版本落的），需重扫块体"; return null; }
                if (!expect.Matches(got, out string reason)) { err = $"剖面缓存失效：{reason} —— 需重扫块体"; return null; }
            }
            return p;
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    internal static void WriteProfile(BinaryWriter w, CoalProfile? p, int sectionVer)
    {
        if (p == null) { w.Write(false); return; }
        w.Write(true);
        w.Write(p.Success); w.Write(p.Error ?? "");
        w.Write(p.SliceWidth); w.Write(p.TotalMaxWt); w.Write(p.CoalCellCount);
        w.Write(p.SeamCount);
        w.Write(p.SeamNames.Length); foreach (string s in p.SeamNames) w.Write(s ?? "");
        w.Write(p.SeamIncrementWt.Length); foreach (double d in p.SeamIncrementWt) w.Write(d);
        WriteBins(w, p.SeamBins);
        w.Write(p.LineCount);
        WriteBins(w, p.LineBins);
        if (sectionVer < SectionV2) return;
        WriteRock(w, p.Rock);
    }

    internal static CoalProfile? ReadProfile(BinaryReader r, int sectionVer)
    {
        if (!r.ReadBoolean()) return null;
        var p = new CoalProfile
        {
            Success = r.ReadBoolean(), Error = r.ReadString(),
            SliceWidth = r.ReadDouble(), TotalMaxWt = r.ReadDouble(), CoalCellCount = r.ReadInt64(),
            SeamCount = r.ReadInt32(),
        };
        int nn = r.ReadInt32(); var names = new string[nn];
        for (int i = 0; i < nn; i++) names[i] = r.ReadString();
        p.SeamNames = names;
        int ni = r.ReadInt32(); var inc = new double[ni];
        for (int i = 0; i < ni; i++) inc[i] = r.ReadDouble();
        p.SeamIncrementWt = inc;
        p.SeamBins = ReadBins(r);
        p.LineCount = r.ReadInt32();
        p.LineBins = ReadBins(r);
        if (sectionVer < SectionV2) return p;
        p.Rock = ReadRock(r);
        return p;
    }

    private static void WriteRock(BinaryWriter w, RockProfile? k)
    {
        if (k == null) { w.Write(false); return; }
        w.Write(true);
        w.Write(k.Success); w.Write(k.Error ?? "");
        w.Write(k.SliceWidth); w.Write(k.BenchHeight); w.Write(k.SeamCount);
        w.Write(k.SeamNames.Length); foreach (string s in k.SeamNames) w.Write(s ?? "");
        w.Write(k.GapNames.Length);  foreach (string s in k.GapNames)  w.Write(s ?? "");
        w.Write(k.SeamDensity.Length); foreach (double d in k.SeamDensity) w.Write(d);
        w.Write(k.TotalRockM3); w.Write(k.RockCellCount);
        w.Write(k.LevelMin); w.Write(k.LevelMax); w.Write(k.MinBin); w.Write(k.MaxBin);
        w.Write(k.UnclassifiedCells); w.Write(k.CrossedColumns);
        w.Write(k.Bins.Count);
        foreach (var kv in k.Bins) { w.Write(kv.Key); w.Write(kv.Value); }
        WriteBins(w, k.CoalVolBins);
        WriteBins(w, k.CoalZMoment);
        WriteProv(w, k.Provenance);
    }

    private static RockProfile? ReadRock(BinaryReader r)
    {
        if (!r.ReadBoolean()) return null;
        var k = new RockProfile
        {
            Success = r.ReadBoolean(), Error = r.ReadString(),
            SliceWidth = r.ReadDouble(), BenchHeight = r.ReadDouble(), SeamCount = r.ReadInt32(),
        };
        int a = r.ReadInt32(); var sn = new string[a]; for (int i = 0; i < a; i++) sn[i] = r.ReadString(); k.SeamNames = sn;
        int b = r.ReadInt32(); var gn = new string[b]; for (int i = 0; i < b; i++) gn[i] = r.ReadString(); k.GapNames = gn;
        int c = r.ReadInt32(); var dn = new double[c]; for (int i = 0; i < c; i++) dn[i] = r.ReadDouble(); k.SeamDensity = dn;
        k.TotalRockM3 = r.ReadDouble(); k.RockCellCount = r.ReadInt64();
        k.LevelMin = r.ReadInt32(); k.LevelMax = r.ReadInt32();
        k.MinBin = r.ReadInt64(); k.MaxBin = r.ReadInt64();
        k.UnclassifiedCells = r.ReadInt64(); k.CrossedColumns = r.ReadInt64();
        int nb = r.ReadInt32();
        for (int i = 0; i < nb; i++) { long key = r.ReadInt64(); k.Bins[key] = r.ReadDouble(); }
        k.CoalVolBins = ReadBins(r);
        k.CoalZMoment = ReadBins(r);
        k.Provenance = ReadProv(r);
        return k;
    }

    private static void WriteProv(BinaryWriter w, ProfileProvenance? v)
    {
        if (v == null) { w.Write(false); return; }
        w.Write(true);
        w.Write(v.BlockModelName ?? ""); w.Write(v.BlockCellCount);
        w.Write(v.BlockOx); w.Write(v.BlockOy); w.Write(v.BlockOz);
        w.Write(v.BlockSx); w.Write(v.BlockSy); w.Write(v.BlockSz);
        w.Write(v.BlockNx); w.Write(v.BlockNy); w.Write(v.BlockNz);
        w.Write(v.WorkLineKey); w.Write(v.SurfaceKey); w.Write(v.SeamSurfaceKey);
        w.Write(v.AlphaDeg); w.Write(v.SliceWidth); w.Write(v.BenchHeight);
        w.Write(v.CoalMode); w.Write(v.CoalThreshold); w.Write(v.SeamRuleKey);
        w.Write(v.CreatedUtc ?? ""); w.Write(v.Software ?? "");
    }

    private static ProfileProvenance? ReadProv(BinaryReader r)
    {
        if (!r.ReadBoolean()) return null;
        return new ProfileProvenance
        {
            BlockModelName = r.ReadString(), BlockCellCount = r.ReadInt64(),
            BlockOx = r.ReadDouble(), BlockOy = r.ReadDouble(), BlockOz = r.ReadDouble(),
            BlockSx = r.ReadDouble(), BlockSy = r.ReadDouble(), BlockSz = r.ReadDouble(),
            BlockNx = r.ReadInt32(), BlockNy = r.ReadInt32(), BlockNz = r.ReadInt32(),
            WorkLineKey = r.ReadUInt64(), SurfaceKey = r.ReadUInt64(), SeamSurfaceKey = r.ReadUInt64(),
            AlphaDeg = r.ReadDouble(), SliceWidth = r.ReadDouble(), BenchHeight = r.ReadDouble(),
            CoalMode = r.ReadInt32(), CoalThreshold = r.ReadDouble(), SeamRuleKey = r.ReadUInt64(),
            CreatedUtc = r.ReadString(), Software = r.ReadString(),
        };
    }

    private static void WriteBins(BinaryWriter w, Dictionary<long, double>[]? bins)
    {
        bins ??= Array.Empty<Dictionary<long, double>>();
        w.Write(bins.Length);
        foreach (var d in bins)
        {
            if (d == null) { w.Write(0); continue; }
            w.Write(d.Count);
            foreach (var kv in d) { w.Write(kv.Key); w.Write(kv.Value); }
        }
    }

    private static Dictionary<long, double>[] ReadBins(BinaryReader r)
    {
        int n = r.ReadInt32();
        var arr = new Dictionary<long, double>[Math.Max(0, n)];
        for (int i = 0; i < n; i++)
        {
            int m = r.ReadInt32();
            var d = new Dictionary<long, double>(Math.Max(0, m));
            for (int j = 0; j < m; j++) { long k = r.ReadInt64(); d[k] = r.ReadDouble(); }
            arr[i] = d;
        }
        return arr;
    }
}
