// 移植自原 PitMine3D Modules/PointCloudLib/Shading/OrthophotoLoader.cs —— 契约（OrthophotoPixels / TryReadPixelSize /
// Load(path, maxDimension, out error) / FallbackMaxDimension）逐行对应；解码实现原版走 WPF BitmapDecoder，
// Kylin 无 WPF，改用本仓库已有的 GeoTiffSampler（TIFF 条带解码：无压缩/LZW/Deflate/PackBits/JPEG）逐像素读出，
// 只按纹理边长硬上限降采样（等比、最近邻），输出 BGRA8 与原版一致。
using System;
using System.IO;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Shading
{
    /// <summary>解码后的正射影像像素（BGRA8，行主序，无行间填充）。</summary>
    public sealed class OrthophotoPixels
    {
        public byte[] Bgra = Array.Empty<byte>();
        /// <summary>实际上传的尺寸（可能已降采样）。</summary>
        public int Width;
        public int Height;
        /// <summary>原始影像尺寸。</summary>
        public int SourceWidth;
        public int SourceHeight;

        public bool Downsampled => Width != SourceWidth || Height != SourceHeight;

        public long Bytes => (long)Width * Height * 4;
    }

    /// <summary>
    /// 正射影像解码：把 GeoTIFF/TIFF 解成 BGRA8。只有超过引擎回报的纹理边长上限才降采样，
    /// 否则一律按原分辨率上传（分辨率就是底图的价值，能上传就不缩）。
    /// </summary>
    public static class OrthophotoLoader
    {
        /// <summary>引擎不可用时的兜底边长上限（D3D 10_x 上限，任何能跑本程序的设备都支持）。</summary>
        public const int FallbackMaxDimension = 8192;

        /// <summary>
        /// 只读图头拿像素尺寸（不解码像素）。失败返回 false。
        /// 给对话框回显「影像 6609 × 4656」用；也给没有 GeoTIFF 标签、要靠 .tfw 换算四至的场景用。
        /// </summary>
        public static bool TryReadPixelSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                using var g = GeoTiffSampler.Load(path);
                if (!g.Success) return false;
                width = g.Width; height = g.Height;
                return width > 0 && height > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解码影像为 BGRA8。失败返回 null 并给出中文原因（就地反馈，不弹窗）。
        /// 大图会阻塞调用线程若干秒，调用方自行套等待光标。
        /// maxDimension = 引擎回报的本机纹理边长硬上限（&lt;=0 用 <see cref="FallbackMaxDimension"/>）；
        /// 只有超过它才降采样，否则一律按原分辨率上传。
        /// </summary>
        public static OrthophotoPixels? Load(string path, int maxDimension, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "影像文件不存在";
                return null;
            }
            try
            {
                using var g = GeoTiffSampler.Load(path);
                if (!g.Success || g.Width <= 0 || g.Height <= 0)
                {
                    error = "读不出影像尺寸（格式不支持或文件损坏；BigTIFF 需先转经典 TIFF）"
                          + (g.Error.Length > 0 ? "：" + g.Error : "");
                    return null;
                }
                int sw = g.Width, sh = g.Height;

                // 只按 GPU 硬上限缩：像素数本身不设闸限，能原样上传就原样上传
                int cap = maxDimension > 0 ? maxDimension : FallbackMaxDimension;
                double scale = 1.0;
                int longSide = Math.Max(sw, sh);
                if (longSide > cap) scale = (double)cap / longSide;

                // 只设一边，另一边等比 —— 两边都设会在非整比时轻微拉伸，破坏与四至的对应关系
                int w, h;
                if (scale >= 1.0) { w = sw; h = sh; }
                else if (sw >= sh) { w = Math.Max(1, (int)Math.Round(sw * scale)); h = Math.Max(1, (int)Math.Round(sh * (double)w / sw)); }
                else { h = Math.Max(1, (int)Math.Round(sh * scale)); w = Math.Max(1, (int)Math.Round(sw * (double)h / sh)); }

                int stride = w * 4;
                var bytes = new byte[(long)stride * h];
                for (int y = 0; y < h; y++)
                {
                    int srcRow = w == sw && h == sh ? y : Math.Min(sh - 1, (int)((y + 0.5) * sh / h));
                    long o = (long)y * stride;
                    for (int x = 0; x < w; x++, o += 4)
                    {
                        int srcCol = w == sw ? x : Math.Min(sw - 1, (int)((x + 0.5) * sw / w));
                        var px = g.ReadPixel(srcCol, srcRow);
                        if (px is { } p) { bytes[o] = p.b; bytes[o + 1] = p.g; bytes[o + 2] = p.r; bytes[o + 3] = 255; }
                        else { bytes[o + 3] = 0; }
                    }
                }

                return new OrthophotoPixels
                {
                    Bgra = bytes,
                    Width = w,
                    Height = h,
                    SourceWidth = sw,
                    SourceHeight = sh,
                };
            }
            catch (OutOfMemoryException)
            {
                error = "内存不足：影像过大，请先裁剪到工作区范围或降低分辨率";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }
}
