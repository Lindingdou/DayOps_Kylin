using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace PitMine3D.Kylin.Views.Render;

/// <summary>
/// 贴图读取（「渲染配置 → 贴图」用）：PNG/JPG → RGBA8 像素块，交给 GL 上传成三平面投影贴图。
/// 原版这步在 C++ 内核里（WIC 解码 + D3D11 纹理），托管侧用 Avalonia 的 <see cref="Bitmap"/> 解码，
/// 再把 BGRA 换成 GL 要的 RGBA。
/// </summary>
internal static class TextureLoader
{
    /// <summary>内置地学贴图的文件名与中文名 —— 与原 RenderConfigDialog 的 s_builtinTextures 一字不差。</summary>
    public static readonly (string File, string Label)[] Builtin =
    {
        ("granite.png", "花岗岩"), ("sandstone.png", "砂岩"), ("limestone.png", "石灰岩"),
        ("basalt.png", "玄武岩"), ("marble.png", "大理岩"), ("shale.png", "页岩"),
        ("grass.png", "草地"), ("sand.png", "沙地"), ("gravel.png", "碎石"),
        ("snow.png", "雪地"), ("soil.png", "土壤"), ("clay.png", "黏土"),
    };

    /// <summary>内置贴图目录（随程序输出，同 Models3D 的做法）。</summary>
    public static string BuiltinDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Geo");

    /// <summary>存在的内置贴图（缺文件的跳过，不报错 —— 与原版"扫目录"同口径）。</summary>
    public static List<(string Path, string Label)> BuiltinAvailable()
    {
        var r = new List<(string, string)>();
        foreach (var (f, label) in Builtin)
        {
            string p = Path.Combine(BuiltinDir, f);
            if (File.Exists(p)) r.Add((p, label));
        }
        return r;
    }

    /// <summary>
    /// 解码成 RGBA8。内置那 12 张是原版的 1024²，原样上传；只有用户自己挑的超大图(>2048)才缩 ——
    /// 平铺贴图再大也只是白占显存，某些国产 GPU 上 4K 纹理还会直接分配失败。
    /// 失败返回 null（调用方如实报，不静默换图）。
    /// </summary>
    public static (byte[] Rgba, int W, int H)? Load(string path, int maxSize = 2048)
    {
        try
        {
            using var bmp = new Bitmap(path);
            var size = bmp.PixelSize;
            if (size.Width <= 0 || size.Height <= 0) return null;
            Bitmap? scaled = null;
            if (size.Width > maxSize || size.Height > maxSize)
            {
                double k = Math.Min(maxSize / (double)size.Width, maxSize / (double)size.Height);
                var target = new PixelSize(Math.Max(1, (int)(size.Width * k)), Math.Max(1, (int)(size.Height * k)));
                scaled = bmp.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
            }
            var use = scaled ?? bmp;
            int w = use.PixelSize.Width, h = use.PixelSize.Height;
            var buf = new byte[w * h * 4];
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { use.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), buf.Length, w * 4); }
            finally { pin.Free(); scaled?.Dispose(); }

            // Avalonia 解出来是 BGRA(小端), GL 要 RGBA —— 就地换 R/B 两道
            for (int i = 0; i + 3 < buf.Length; i += 4) (buf[i], buf[i + 2]) = (buf[i + 2], buf[i]);
            return (buf, w, h);
        }
        catch { return null; }
    }
}
