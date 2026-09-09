using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PitMine3D.Kylin.Views.Startup;

/// <summary>
/// 企业标识（中煤标志）—— 矢量路径与由它渲染出的窗口图标，全应用共一份。
///
/// 路径取自原版 SplashWindow 的 DrawingImage 几何（1024×1024 视口，EvenOdd 填充：
/// 外圈"中"字轮廓 + 内部镂空各一条子路径）。启动画面、主窗口图标、各子窗口图标都用它，
/// 免得哪天改标识时漏掉一处。
/// </summary>
public static class BrandLogo
{
    /// <summary>中煤标准蓝。</summary>
    public const string Blue = "#0086D1";

    /// <summary>中煤标志矢量路径（1024×1024 视口）。只含直线段，无曲线。</summary>
    public const string Path =
        "F0 M152.7,378.3 L133.6,403.7 L118.6,432.0 L108.8,460.3 L103.0,493.2 L102.4,526.2 L106.4,556.2 L115.7,586.8 " +
        "L130.1,616.9 L149.8,644.6 L178.1,672.3 L206.4,691.4 L235.9,704.7 L254.9,710.4 L280.9,715.1 L397.6,716.2 " +
        "L399.9,721.4 L396.5,801.1 L399.9,809.2 L408.6,816.7 L617.1,816.7 L625.2,809.8 L628.7,802.9 L625.8,719.7 " +
        "L628.1,716.2 L744.8,715.1 L760.4,712.8 L784.7,706.4 L814.7,693.7 L829.7,685.0 L848.8,671.2 L868.5,652.7 " +
        "L879.4,640.0 L892.1,622.1 L903.1,602.4 L912.4,579.9 L918.1,559.7 L921.6,541.2 L919.9,536.0 L717.1,536.0 " +
        "L714.2,538.9 L710.2,551.6 L702.6,566.0 L691.7,580.5 L680.1,591.4 L665.7,601.3 L650.7,608.2 L633.3,612.8 " +
        "L624.1,613.4 L621.8,610.5 L618.9,537.7 L617.1,536.0 L558.8,536.0 L557.1,538.3 L557.1,609.9 L554.8,613.4 " +
        "L472.7,614.0 L469.2,611.7 L468.7,415.8 L469.2,413.5 L472.7,411.2 L554.8,411.8 L557.1,415.8 L557.1,486.3 " +
        "L559.4,488.6 L615.4,488.6 L617.1,486.9 L614.8,417.0 L615.4,412.9 L617.1,411.2 L633.9,412.3 L650.7,417.0 " +
        "L667.4,425.1 L680.7,434.3 L691.1,444.1 L700.3,455.7 L710.2,473.6 L714.2,485.7 L716.5,488.0 L919.3,488.0 " +
        "L921.0,486.3 L919.3,471.3 L911.2,443.0 L903.7,425.1 L892.7,404.8 L875.4,381.1 L856.3,361.5 L838.4,347.1 " +
        "L823.4,337.2 L805.5,328.0 L781.8,318.8 L747.1,311.2 L732.1,310.1 L613.7,310.1 L610.8,308.9 L608.5,298.5 " +
        "L606.2,220.0 L602.1,211.9 L594.0,207.3 L431.7,207.3 L428.2,208.4 L421.3,214.8 L419.0,222.9 L416.7,304.3 " +
        "L414.9,308.9 L412.1,310.1 L293.0,310.1 L274.6,311.8 L253.8,315.9 L223.7,326.3 L196.6,340.7 L175.8,355.7 Z " +
        "M406.9,410.6 L409.2,412.9 L401.1,600.1 L399.9,611.1 L395.3,613.4 L379.1,609.3 L361.2,600.7 L347.4,590.3 " +
        "L332.3,574.1 L321.9,557.9 L313.8,538.9 L309.8,521.5 L309.2,500.7 L312.1,484.6 L317.9,469.0 L326.6,453.9 " +
        "L339.3,438.9 L352.0,428.5 L369.3,418.7 L388.4,412.3 Z";

    private static WindowIcon? _icon;

    /// <summary>
    /// 窗口图标（任务栏/标题栏）。把矢量渲染成 256×256 位图再包成图标 —— 只做一次，之后复用。
    /// 渲染失败（无图形后端等）返回 null，调用方不设图标即可，不该因为一个图标把窗口拖崩。
    /// </summary>
    public static WindowIcon? Icon()
    {
        if (_icon != null) return _icon;
        try
        {
            const int px = 256;
            var rtb = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
            var geom = Geometry.Parse(Path);
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushTransform(Matrix.CreateScale(px / 1024.0, px / 1024.0)))
                ctx.DrawGeometry(Brush.Parse(Blue), null, geom);

            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            return _icon = new WindowIcon(ms);
        }
        catch (Exception ex)
        {
            PitMine3D.Kylin.CrashLog.Write("图标", "中煤标志渲染失败，窗口用默认图标：" + ex.Message);
            return null;
        }
    }

    /// <summary>给窗口装上企业图标（渲染不出来就保持默认，不抛）。</summary>
    public static void Apply(Window w)
    {
        var ic = Icon();
        if (ic != null) w.Icon = ic;
    }
}
