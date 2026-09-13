// 忠实移植自原 PitMine3D Modules/PointCloudLib/Shading/GeoTiffInfo.cs（逐行对应；仅命名空间适配）
using System;
using System.Globalization;
using System.IO;

namespace PitMine3D.Kylin.Shading
{
    /// <summary>正射影像的地理配准信息：四至（绝对世界坐标，米）+ 地面分辨率 + 来源说明。</summary>
    public sealed class OrthophotoGeoRef
    {
        /// <summary>影像像素尺寸（来自 TIFF 标签 256/257，或宿主解码器读的图头）。</summary>
        public int PixelWidth;
        public int PixelHeight;
        /// <summary>影像四至（绝对世界坐标，米；与三角网/点云同坐标系）。</summary>
        public double MinX, MinY, MaxX, MaxY;
        /// <summary>地面分辨率（米/像素）。</summary>
        public double PixelSizeX, PixelSizeY;
        /// <summary>配准来源，用于界面提示（如「GeoTIFF 标签」/「世界文件 dlt05.tfw」）。</summary>
        public string Source = "";
        /// <summary>坐标系描述（GeoTIFF GeoAsciiParams 首段；读不到为 null）。</summary>
        public string? CrsName;
    }

    /// <summary>
    /// 正射影像地理配准读取器：优先读 GeoTIFF 标签（33922 ModelTiepoint + 33550 ModelPixelScale，
    /// 退而用 34264 ModelTransformation），读不到再找同名世界文件（.tfw/.tifw/.wld）。
    ///
    /// 只读文件头的若干 KB（IFD + 几个 double 数组），不解码像素 —— 上百 MB 的影像也是毫秒级，
    /// 所以可以在对话框里选完文件就立刻回显范围/分辨率。
    /// 不做坐标系转换：影像坐标系必须与工程一致（本项目现场数据都是 CGCS2000 高斯投影）。
    /// </summary>
    public static class GeoTiffInfo
    {
        // TIFF 标签号
        private const ushort TagImageWidth = 256;
        private const ushort TagImageLength = 257;
        private const ushort TagModelPixelScale = 33550;   // double×3: (sx, sy, sz)
        private const ushort TagModelTiepoint = 33922;     // double×6N: (i,j,k, x,y,z)
        private const ushort TagModelTransformation = 34264; // double×16: 4×4 仿射
        private const ushort TagGeoKeyDirectory = 34735;   // ushort×N
        private const ushort TagGeoAsciiParams = 34737;    // ASCII

        /// <summary>
        /// 读取配准信息。成功返回对象、error 为空；失败返回 null 并给出中文原因（供就地反馈，不弹窗）。
        /// pixelWidth/pixelHeight 传入宿主解码器读到的图像尺寸（&lt;=0 表示未知，则以 TIFF 标签为准）。
        /// </summary>
        public static OrthophotoGeoRef? Read(string path, int pixelWidth, int pixelHeight, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "影像文件不存在";
                return null;
            }

            OrthophotoGeoRef? info = null;
            bool isTiff = path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
            if (isTiff)
            {
                info = TryReadGeoTiff(path, out string tiffErr);
                if (info == null && !string.IsNullOrEmpty(tiffErr)) error = tiffErr;
            }

            // TIFF 里没有配准标签（或不是 TIFF）→ 找世界文件
            int w = info?.PixelWidth > 0 ? info!.PixelWidth : pixelWidth;
            int h = info?.PixelHeight > 0 ? info!.PixelHeight : pixelHeight;
            if (info == null || info.PixelSizeX <= 0)
            {
                var wf = TryReadWorldFile(path, w, h);
                if (wf != null)
                {
                    error = "";
                    return wf;
                }
            }

            // 只读到尺寸没读到配准（PixelSizeX==0）不算成功 —— 让调用方走"手工输入四至"那条路
            if (info != null && info.PixelSizeX > 0 && info.PixelSizeY > 0) return info;
            if (string.IsNullOrEmpty(error))
                error = "影像没有地理配准信息（缺 GeoTIFF 标签，也没有同名 .tfw 世界文件）";
            return null;
        }

        // ── GeoTIFF 标签 ────────────────────────────────────────────────────
        private static OrthophotoGeoRef? TryReadGeoTiff(string path, out string error)
        {
            error = "";
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
                using var br = new BinaryReader(fs);

                byte b0 = br.ReadByte(), b1 = br.ReadByte();
                bool little;
                if (b0 == 0x49 && b1 == 0x49) little = true;        // "II"
                else if (b0 == 0x4D && b1 == 0x4D) little = false;  // "MM"
                else { error = "不是有效的 TIFF 文件"; return null; }

                ushort magic = ReadU16(br, little);
                if (magic == 43)
                {
                    error = "这是 BigTIFF（>4GB 的 TIFF 变体），当前解码器不支持；请转存为经典 TIFF";
                    return null;
                }
                if (magic != 42) { error = "TIFF 版本号异常（既不是 42 也不是 43）"; return null; }

                uint ifdOffset = ReadU32(br, little);
                if (ifdOffset == 0 || ifdOffset >= fs.Length) { error = "TIFF 目录偏移越界"; return null; }
                fs.Position = ifdOffset;
                int entryCount = ReadU16(br, little);
                if (entryCount <= 0 || entryCount > 4096) { error = "TIFF 目录项数异常"; return null; }

                int imgW = 0, imgH = 0;
                double[]? pixelScale = null, tiepoint = null, transform = null;
                ushort[]? geoKeys = null;
                string? geoAscii = null;

                for (int i = 0; i < entryCount; ++i)
                {
                    long entryPos = fs.Position;
                    ushort tag = ReadU16(br, little);
                    ushort type = ReadU16(br, little);
                    uint count = ReadU32(br, little);
                    long valuePos = fs.Position;   // 4 字节：值本身（放得下）或指向值的偏移

                    switch (tag)
                    {
                        case TagImageWidth: imgW = (int)ReadScalar(br, fs, little, type); break;
                        case TagImageLength: imgH = (int)ReadScalar(br, fs, little, type); break;
                        case TagModelPixelScale:
                            if (type == 12) pixelScale = ReadDoubles(br, fs, little, count, valuePos);
                            break;
                        case TagModelTiepoint:
                            if (type == 12) tiepoint = ReadDoubles(br, fs, little, Math.Min(count, 6u), valuePos);
                            break;
                        case TagModelTransformation:
                            if (type == 12) transform = ReadDoubles(br, fs, little, Math.Min(count, 16u), valuePos);
                            break;
                        case TagGeoKeyDirectory:
                            if (type == 3) geoKeys = ReadU16Array(br, fs, little, Math.Min(count, 512u), valuePos);
                            break;
                        case TagGeoAsciiParams:
                            if (type == 2) geoAscii = ReadAscii(br, fs, little, Math.Min(count, 1024u), valuePos);
                            break;
                    }
                    fs.Position = entryPos + 12;   // 每个 IFD 项固定 12 字节
                }

                if (imgW <= 0 || imgH <= 0) { error = "TIFF 缺少影像尺寸标签"; return null; }

                var info = new OrthophotoGeoRef { PixelWidth = imgW, PixelHeight = imgH };

                if (pixelScale != null && pixelScale.Length >= 2 && tiepoint != null && tiepoint.Length >= 6
                    && pixelScale[0] > 0 && pixelScale[1] > 0)
                {
                    // ModelTiepoint: 栅格点 (i,j) ↔ 世界点 (x,y)。i/j 常为 0（左上角）。
                    double sx = pixelScale[0], sy = pixelScale[1];
                    double left = tiepoint[3] - tiepoint[0] * sx;
                    double top = tiepoint[4] + tiepoint[1] * sy;   // 影像 j 向下 = 世界 Y 向下
                    // RasterPixelIsPoint（GeoKey 1025 == 2）时锚点在像素中心 → 各退半像素回到边角
                    if (RasterTypeIsPoint(geoKeys)) { left -= sx * 0.5; top += sy * 0.5; }
                    info.PixelSizeX = sx;
                    info.PixelSizeY = sy;
                    info.MinX = left;
                    info.MaxY = top;
                    info.MaxX = left + imgW * sx;
                    info.MinY = top - imgH * sy;
                    info.Source = "GeoTIFF 标签";
                }
                else if (transform != null && transform.Length >= 16)
                {
                    // 仿射矩阵形式：只支持无旋转（旋转项非 0 的影像要先在 GIS 里正北化）
                    double a = transform[0], bRot = transform[1], c = transform[3];
                    double dRot = transform[4], e = transform[5], f = transform[7];
                    if (Math.Abs(bRot) > 1e-9 || Math.Abs(dRot) > 1e-9)
                    {
                        error = "影像带旋转（ModelTransformation 含旋转项），请先在 GIS 里重采样为正北影像";
                        return null;
                    }
                    if (Math.Abs(a) < 1e-12 || Math.Abs(e) < 1e-12) { error = "影像仿射参数退化"; return null; }
                    info.PixelSizeX = Math.Abs(a);
                    info.PixelSizeY = Math.Abs(e);
                    info.MinX = c;
                    info.MaxY = f;
                    info.MaxX = c + imgW * a;
                    info.MinY = f + imgH * e;      // e 通常为负
                    Normalize(info);
                    info.Source = "GeoTIFF 仿射矩阵";
                }
                else
                {
                    // 有尺寸但没配准 → 交给调用方去找 .tfw（PixelSizeX 保持 0 作为标记）
                    return info;
                }

                info.CrsName = FirstSegment(geoAscii);
                return info;
            }
            catch (Exception ex)
            {
                error = "读 TIFF 头失败：" + ex.Message;
                return null;
            }
        }

        // ── 世界文件（.tfw / .tifw / .wld）─────────────────────────────────
        // 6 行：A(X 像素大小) D(Y 旋转) B(X 旋转) E(Y 像素大小,常为负) C/F(左上角【像素中心】坐标)
        private static OrthophotoGeoRef? TryReadWorldFile(string imagePath, int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return null;
            foreach (string ext in new[] { ".tfw", ".tifw", ".wld" })
            {
                string wf = Path.ChangeExtension(imagePath, ext);
                if (!File.Exists(wf)) continue;
                try
                {
                    var lines = File.ReadAllLines(wf);
                    if (lines.Length < 6) continue;
                    var v = new double[6];
                    bool ok = true;
                    for (int i = 0; i < 6; ++i)
                        ok &= double.TryParse(lines[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
                    if (!ok) continue;
                    double a = v[0], dRot = v[1], bRot = v[2], e = v[3], c = v[4], f = v[5];
                    if (Math.Abs(bRot) > 1e-9 || Math.Abs(dRot) > 1e-9) continue;   // 带旋转不支持
                    if (Math.Abs(a) < 1e-12 || Math.Abs(e) < 1e-12) continue;
                    var info = new OrthophotoGeoRef
                    {
                        PixelWidth = pixelWidth,
                        PixelHeight = pixelHeight,
                        PixelSizeX = Math.Abs(a),
                        PixelSizeY = Math.Abs(e),
                        MinX = c - a * 0.5,                     // 世界文件锚在左上像素【中心】
                        MaxY = f - e * 0.5,                     // e 为负 → 实际是 f + |e|/2
                        Source = "世界文件 " + Path.GetFileName(wf),
                    };
                    info.MaxX = info.MinX + pixelWidth * Math.Abs(a);
                    info.MinY = info.MaxY - pixelHeight * Math.Abs(e);
                    return info;
                }
                catch { /* 单个世界文件读失败就试下一个后缀 */ }
            }
            return null;
        }

        // ── TIFF 基础读取 ───────────────────────────────────────────────────
        private static ushort ReadU16(BinaryReader br, bool little)
        {
            byte a = br.ReadByte(), b = br.ReadByte();
            return little ? (ushort)(a | (b << 8)) : (ushort)((a << 8) | b);
        }

        private static uint ReadU32(BinaryReader br, bool little)
        {
            byte a = br.ReadByte(), b = br.ReadByte(), c = br.ReadByte(), d = br.ReadByte();
            return little ? (uint)(a | (b << 8) | (c << 16) | (d << 24))
                          : (uint)((a << 24) | (b << 16) | (c << 8) | d);
        }

        private static double ReadF64(BinaryReader br, bool little)
        {
            var raw = br.ReadBytes(8);
            if (raw.Length < 8) return double.NaN;
            if (!little) Array.Reverse(raw);
            return BitConverter.ToDouble(raw, 0);
        }

        /// <summary>读 SHORT/LONG 型单值（值一定放得进 4 字节值域，不需要跳偏移）。</summary>
        private static uint ReadScalar(BinaryReader br, FileStream fs, bool little, ushort type)
        {
            long pos = fs.Position;
            uint val = type == 3 ? ReadU16(br, little) : ReadU32(br, little);
            fs.Position = pos;
            return val;
        }

        private static double[]? ReadDoubles(BinaryReader br, FileStream fs, bool little, uint count, long valuePos)
        {
            if (count == 0) return null;
            fs.Position = valuePos;
            uint offset = ReadU32(br, little);      // double 是 8 字节，永远放不进 4 字节值域
            if (offset == 0 || offset + count * 8 > fs.Length) return null;
            fs.Position = offset;
            var arr = new double[count];
            for (uint i = 0; i < count; ++i) arr[i] = ReadF64(br, little);
            return arr;
        }

        private static ushort[]? ReadU16Array(BinaryReader br, FileStream fs, bool little, uint count, long valuePos)
        {
            if (count == 0) return null;
            fs.Position = valuePos;
            if (count <= 2)
            {
                var inline = new ushort[count];
                for (uint i = 0; i < count; ++i) inline[i] = ReadU16(br, little);
                return inline;
            }
            uint offset = ReadU32(br, little);
            if (offset == 0 || offset + count * 2 > fs.Length) return null;
            fs.Position = offset;
            var arr = new ushort[count];
            for (uint i = 0; i < count; ++i) arr[i] = ReadU16(br, little);
            return arr;
        }

        private static string? ReadAscii(BinaryReader br, FileStream fs, bool little, uint count, long valuePos)
        {
            if (count == 0) return null;
            fs.Position = valuePos;
            byte[] bytes;
            if (count <= 4) bytes = br.ReadBytes((int)count);
            else
            {
                uint offset = ReadU32(br, little);
                if (offset == 0 || offset + count > fs.Length) return null;
                fs.Position = offset;
                bytes = br.ReadBytes((int)count);
            }
            return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }

        /// <summary>GeoKey 1025 (GTRasterTypeGeoKey) == 2 表示锚点在像素中心（RasterPixelIsPoint）。</summary>
        private static bool RasterTypeIsPoint(ushort[]? geoKeys)
        {
            if (geoKeys == null || geoKeys.Length < 8) return false;
            int keyCount = geoKeys[3];
            for (int k = 0; k < keyCount; ++k)
            {
                int p = 4 + k * 4;
                if (p + 3 >= geoKeys.Length) break;
                if (geoKeys[p] == 1025 && geoKeys[p + 1] == 0) return geoKeys[p + 3] == 2;
            }
            return false;
        }

        /// <summary>GeoAsciiParams 是 '|' 分段的串，首段一般就是坐标系名（"PCS Name = ..."）。</summary>
        private static string? FirstSegment(string? ascii)
        {
            if (string.IsNullOrWhiteSpace(ascii)) return null;
            string s = ascii!.Split('|')[0].Trim();
            int eq = s.IndexOf('=');
            if (eq >= 0 && eq + 1 < s.Length) s = s.Substring(eq + 1).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static void Normalize(OrthophotoGeoRef info)
        {
            if (info.MinX > info.MaxX) (info.MinX, info.MaxX) = (info.MaxX, info.MinX);
            if (info.MinY > info.MaxY) (info.MinY, info.MaxY) = (info.MaxY, info.MinY);
        }
    }
}
