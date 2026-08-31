using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// LAS 点云导入 —— 忠实公开 ASPRS LAS 1.2/1.4 规范(非依赖原 native LasLib): 读头(scale/offset/点数/记录长)
/// + 逐点 X/Y/Z int32→世界坐标(×scale+offset)。大文件按步长直接 seek 抽稀(O(maxPoints) 次寻址)。
/// 曾误记"native 无托管源", 实则格式公开+有样本→可逆向可验(同 KDF/TDM)。纯逻辑、可单测(合成/真实样本)。
/// </summary>
public static class LasImportService
{
    public sealed class LasResult
    {
        public bool Success;
        public string Error = "";
        public byte VersionMajor, VersionMinor, PointFormat;
        public long PointCount;                                   // 头声明的总点数
        public List<(double x, double y, double z)> Points = new();  // 抽稀后实际读入
        public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;         // 头里的包围盒
    }

    public static LasResult Load(string path, int maxPoints = 500000)
    {
        try { using var fs = File.OpenRead(path); return Read(fs, maxPoints); }
        catch (System.Exception ex) { return new LasResult { Error = ex.Message }; }
    }

    public static LasResult Read(Stream s, int maxPoints = 500000)
    {
        var r = new LasResult();
        var br = new BinaryReader(s);
        if (s.Length < 227) { r.Error = "文件过小(非 LAS)"; return r; }

        var magic = br.ReadBytes(4);
        if (magic.Length < 4 || magic[0] != (byte)'L' || magic[1] != (byte)'A' || magic[2] != (byte)'S' || magic[3] != (byte)'F')
        { r.Error = "非 LAS(缺 LASF 魔数)"; return r; }

        s.Seek(24, SeekOrigin.Begin); r.VersionMajor = br.ReadByte(); r.VersionMinor = br.ReadByte();
        s.Seek(96, SeekOrigin.Begin); uint offsetToPoints = br.ReadUInt32();
        s.Seek(104, SeekOrigin.Begin); r.PointFormat = br.ReadByte(); ushort recLen = br.ReadUInt16();
        uint legacyCount = br.ReadUInt32();                       // offset 107
        s.Seek(131, SeekOrigin.Begin);
        double sx = br.ReadDouble(), sy = br.ReadDouble(), sz = br.ReadDouble();
        double ox = br.ReadDouble(), oy = br.ReadDouble(), oz = br.ReadDouble();
        r.MaxX = br.ReadDouble(); r.MinX = br.ReadDouble();
        r.MaxY = br.ReadDouble(); r.MinY = br.ReadDouble();
        r.MaxZ = br.ReadDouble(); r.MinZ = br.ReadDouble();

        long pointCount = legacyCount;
        if (r.VersionMinor >= 4 && legacyCount == 0 && s.Length >= 255)   // LAS 1.4 用 offset 247 的 uint64
        { s.Seek(247, SeekOrigin.Begin); pointCount = (long)br.ReadUInt64(); }
        r.PointCount = pointCount;

        if (recLen < 12 || offsetToPoints < 227 || pointCount <= 0) { r.Success = true; return r; }   // 头有效但无点

        long step = (maxPoints > 0 && pointCount > maxPoints) ? pointCount / maxPoints : 1;
        if (step < 1) step = 1;
        for (long i = 0; i < pointCount; i += step)
        {
            long off = offsetToPoints + i * (long)recLen;
            if (off + 12 > s.Length) break;
            s.Seek(off, SeekOrigin.Begin);
            int xi = br.ReadInt32(), yi = br.ReadInt32(), zi = br.ReadInt32();
            r.Points.Add((xi * sx + ox, yi * sy + oy, zi * sz + oz));
            if (maxPoints > 0 && r.Points.Count >= maxPoints) break;
        }
        r.Success = true;
        return r;
    }
}
