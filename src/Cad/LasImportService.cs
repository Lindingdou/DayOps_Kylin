using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// LAS 点云导入 —— 忠实公开 ASPRS LAS 1.2/1.4 规范(非依赖原 native LasLib): 读头(scale/offset/点数/记录长)
/// + 逐点 X/Y/Z int32→世界坐标(×scale+offset)。大文件在全文件上均匀取样封顶(测区范围完整, 密度按比例降)。
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
        public List<(float r, float g, float b)>? Colors;        // 含 RGB 的点格式(2/3/5/7/8)才非 null; 与 Points 同长
        public List<float> Intensity = new();                    // 回波强度(所有点格式偏移12均有, uint16 原值); 与 Points 同长
        public List<byte> Classification = new();                // ASPRS 分类码(2=地面/3-5=植被/6=建筑…); 与 Points 同长
        public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;         // 头里的包围盒
    }

    /// <summary>点格式的 RGB 字段字节偏移(ASPRS 规范); 无 RGB 返回 -1。格式 2=20/3=28/5=28/7=30/8=30。</summary>
    private static int RgbOffset(byte format) => format switch { 2 => 20, 3 => 28, 5 => 28, 7 => 30, 8 => 30, _ => -1 };

    public static LasResult Load(string path, int maxPoints = 500000)
    {
        // bufferSize=1: 自己按块读盘, 不让 FileStream 再套一层 4 KB 缓冲(逐条 seek 时每条都拖一页)
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1); return Read(fs, maxPoints); }
        catch (Exception ex) { return new LasResult { Error = ex.Message }; }
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

        int rgbOff = RgbOffset(r.PointFormat);   // 有 RGB 的点格式 → 逐点读真实色
        int classOff = r.PointFormat <= 5 ? 15 : 16;   // 分类码字节偏移(ASPRS: 格式0-5=15, 格式≥6=16)

        // 抽稀 = 在全文件上均匀取样: 第 k 个样本取第 k·N/take 条记录, 从文件头一直取到文件尾。
        // 曾经写成 step=N/maxPoints(取整)+读满 maxPoints 就 break: 398 万点封顶 200 万时 step=1,
        // 只读了前 200 万条就停 —— 航测 LAS 是按空间分块落盘的, 文件前一半恰是测区的西南半幅,
        // 视口里点云"只显示了一半"。均匀取样后无论封顶多少, 测区范围都完整, 只是密度按比例降。
        long take = (maxPoints > 0 && pointCount > maxPoints) ? maxPoints : pointCount;
        long strideBytes = Math.Max(1, pointCount / take) * recLen;
        // 读盘按块: 样本稀(块间距大)就一条一条 seek; 样本密就一次读几 MB 顺着挑, 免得 200 万次 seek 各拖一页盘。
        int chunk = (int)Math.Min(4 << 20, Math.Max(recLen, Math.Min(int.MaxValue, strideBytes * 64)));
        var buf = new byte[chunk];
        long bufStart = -1; int bufLen = 0;
        int rgbMax = 0;                          // 8 位色写进 16 位字段(常见于部分航测软件)时的判据
        for (long k = 0; k < take; k++)
        {
            long i = k * pointCount / take;      // k < 2^31, N < 2^40 → 积不溢出
            long off = offsetToPoints + i * (long)recLen;
            if (off + 12 > s.Length) break;
            if (bufStart < 0 || off < bufStart || off + recLen > bufStart + bufLen)
            {
                s.Seek(off, SeekOrigin.Begin);
                bufStart = off;
                bufLen = ReadFully(s, buf, (int)Math.Min(chunk, s.Length - off));
                if (bufLen < 12) break;
            }
            int p = (int)(off - bufStart);
            if (p + 12 > bufLen) break;          // 末条记录被截断
            var span = buf.AsSpan(p);
            int xi = BinaryPrimitives.ReadInt32LittleEndian(span);
            int yi = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4));
            int zi = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8));
            r.Points.Add((xi * sx + ox, yi * sy + oy, zi * sz + oz));
            r.Intensity.Add(p + 14 <= bufLen ? BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12)) : 0);   // 强度: 偏移12(紧接XYZ), 原值
            r.Classification.Add(p + classOff + 1 <= bufLen ? buf[p + classOff] : (byte)0);   // 分类码: 格式≤5 偏移15 / 格式≥6 偏移16
            if (rgbOff >= 0)                      // 真实色(RGB uint16 归一化); 保 Colors 与 Points 同长
            {
                r.Colors ??= new List<(float, float, float)>();
                if (p + rgbOff + 6 <= bufLen)
                {
                    ushort rr = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(rgbOff));
                    ushort gg = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(rgbOff + 2));
                    ushort bb = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(rgbOff + 4));
                    if (rr > rgbMax) rgbMax = rr; if (gg > rgbMax) rgbMax = gg; if (bb > rgbMax) rgbMax = bb;
                    r.Colors.Add((rr / 65535f, gg / 65535f, bb / 65535f));
                }
                else r.Colors.Add((0, 0, 0));    // 截断记录兜底
            }
        }
        // 整份色值都没超过 255 → 写入方存的是 8 位色(规范虽要求 16 位, CloudCompare/PDAL 同样按此兜底), 否则一片黑
        if (r.Colors != null && rgbMax > 0 && rgbMax <= 255)
            for (int j = 0; j < r.Colors.Count; j++)
            {
                var c = r.Colors[j];
                r.Colors[j] = (c.r * 257f, c.g * 257f, c.b * 257f);   // 65535/255 = 257
            }
        r.Success = true;
        return r;
    }

    /// <summary>读满 count 字节(流可能分多次给); 返回实际读到的字节数(到 EOF 时小于 count)。</summary>
    private static int ReadFully(Stream s, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }
}
