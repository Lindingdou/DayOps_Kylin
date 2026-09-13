// 忠实移植自原 PitMine3D Modules/TaskLib/Features/BasemapConfigWindow.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Shading;           // OrthophotoConfig / GeoTiffInfo / OrthophotoLoader
using PitMine3D.Kylin.TaskLib.Adjust;    // OrthophotoBasemap（贴到视口）
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 影像底图：正射影像（GeoTIFF）的工程级配置入口。
///
/// <para>
/// <b>它解决的是"同一张影像要选三遍"</b>：作业区划分、三维推演、TIN 着色此前各弹各的文件框、
/// 各记各的状态。现在路径收在 <see cref="OrthophotoConfig"/>，三处一律先问它。
/// </para>
/// <para>
/// <b>配准一律现读文件头，不存快照</b>：<c>GeoTiffInfo</c> 只读 IFD 与几个 double，上百 MB 的影像也是毫秒级。
/// </para>
/// </summary>
public sealed class BasemapConfigWindow : Window
{
    private readonly TextBox pathBox = new() { Width = 540, IsReadOnly = true };
    private readonly CheckBox autoLoadBox = new() { Content = "需要底图的窗口打开时自动装载", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox srcBox = Ro(), crsBox = Ro(), extentBox = Ro(), pixelBox = Ro();
    private readonly TextBlock coverText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hintLine = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private bool _suppressAuto;

    private static TextBox Ro() => new() { Width = 620, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 0) };

    public BasemapConfigWindow()
    {
        Title = "影像底图 — 日常生产组织";
        TaskUi.Place(this, 880, 560);

        var header = TaskUi.Header("影像底图", "正射影像（GeoTIFF）配一次 —— 作业区划分 / 三维推演 / TIN 着色 之后都读它，不再各选各的");

        var top = new StackPanel();
        var f1 = Fld(Lab("影像文件"));
        ToolTip.SetTip(pathBox, "工程级配置，存 %LOCALAPPDATA%/PitMine/orthophoto.json");
        f1.Children.Add(pathBox);
        var pick = TaskUi.Btn("选择…", OnPick, 72); pick.Margin = new Thickness(8, 0, 0, 0); f1.Children.Add(pick);
        var clear = TaskUi.Btn("清除配置", OnClear, 82); clear.Margin = new Thickness(8, 0, 0, 0); f1.Children.Add(clear);
        top.Children.Add(f1);
        var f2 = Fld(Lab(""));
        ToolTip.SetTip(autoLoadBox, "关掉则各窗口仍需手动点一次「装载底图」，但路径还是用这里配的");
        autoLoadBox.IsCheckedChanged += (_, _) => { if (!_suppressAuto) OnAutoLoadChanged(); };
        f2.Children.Add(autoLoadBox);
        top.Children.Add(f2);
        var f3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var apply = TaskUi.Btn("立即贴到视口", OnApplyToViewport, 110); apply.Margin = new Thickness(0); f3.Children.Add(apply);
        var unapply = TaskUi.Btn("从视口清除", OnClearViewport, 100); unapply.Margin = new Thickness(10, 0, 0, 0); f3.Children.Add(unapply);
        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        f3.Children.Add(toolStatus);
        top.Children.Add(f3);

        // 配准信息一律**现读文件头**，不存快照
        var geo = new StackPanel();
        geo.Children.Add(Fld(Lab("配准来源"), srcBox));
        geo.Children.Add(Fld(Lab("坐标系"), crsBox));
        geo.Children.Add(Fld(Lab("四至(m)"), extentBox));
        geo.Children.Add(Fld(Lab("像素/分辨率"), pixelBox));
        TaskUi.Theme(coverText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        var body = new StackPanel();
        body.Children.Add(TaskUi.GroupBox("地理配准（每次打开现读文件头，不缓存）", geo, new Thickness(0, 0, 0, 8), 10));
        body.Children.Add(TaskUi.GroupBox("覆盖核对（影像框外的东西画不出来，只会是一片空白）", coverText, null, 10));
        var scroll = new ScrollViewer { Content = body, Padding = new Thickness(14, 10), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

        TaskUi.Theme(hintLine, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        var foot = TaskUi.Bar(hintLine, top: false, padY: 9);

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var bar = TaskUi.Bar(top, top: true, padY: 8);
        Grid.SetRow(header, 0); Grid.SetRow(bar, 1); Grid.SetRow(scroll, 2); Grid.SetRow(foot, 3);
        g.Children.Add(header); g.Children.Add(bar); g.Children.Add(scroll); g.Children.Add(foot);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        Content = g;

        Opened += (_, _) => Fill();
    }

    private static TextBlock Lab(string t)
    {
        var l = new TextBlock { Text = t, Width = 96, VerticalAlignment = VerticalAlignment.Center };
        TaskUi.Theme(l, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        return l;
    }

    private static StackPanel Fld(params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3) };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    private void Fill()
    {
        var cfg = OrthophotoConfig.Current;
        pathBox.Text = string.IsNullOrWhiteSpace(cfg.Path) ? "（未配置）" : cfg.Path;
        _suppressAuto = true; autoLoadBox.IsChecked = cfg.AutoLoad; _suppressAuto = false;

        if (!OrthophotoConfig.HasPath)
        {
            ClearGeoBoxes("未配置正射影像");
            coverText.Text = "配好影像后，这里会逐个核对作业面/采掘单元是否落在影像范围内。";
            toolStatus.Text = OrthophotoConfig.LastIoLabel;
            hintLine.Text = Hint(null);
            return;
        }

        if (!OrthophotoConfig.Available)
        {
            ClearGeoBoxes("已配置，但当前读不到这个文件");
            coverText.Text = "";
            toolStatus.Text = $"路径读不到（盘/网络路径没连上？）——**配置保留不清除**，插回来即可继续用：{cfg.Path}";
            hintLine.Text = Hint(null);
            return;
        }

        // 现读文件头
        int pw = 0, ph = 0;
        try { OrthophotoLoader.TryReadPixelSize(cfg.Path, out pw, out ph); } catch { }

        OrthophotoGeoRef? geo = null;
        string err = "";
        try { geo = GeoTiffInfo.Read(cfg.Path, pw, ph, out err); }
        catch (Exception ex) { err = ex.Message; }

        if (geo == null || !(geo.PixelSizeX > 0) || !(geo.MaxX > geo.MinX) || !(geo.MaxY > geo.MinY))
        {
            ClearGeoBoxes(err.Length > 0 ? err : "四至解析失败");
            coverText.Text =
                "没有可用配准就贴不上、也算不出覆盖：本程序**不会**拿区域包围盒去凑一个四至——那样画出来的坐标是错的。\n"
              + "常见成因：① 导出时没勾「写入 GeoTIFF 标签」；② BigTIFF（>4GB）需先转经典 TIFF；"
              + "③ 影像带旋转（ModelTransformation 含旋转项），需先在 GIS 里重采样为正北。";
            toolStatus.Text = "配准不可用";
            hintLine.Text = Hint(null);
            return;
        }

        double km2 = (geo.MaxX - geo.MinX) * (geo.MaxY - geo.MinY) / 1e6;
        srcBox.Text = geo.Source;
        crsBox.Text = string.IsNullOrEmpty(geo.CrsName)
            ? "（影像未写坐标系名）—— 本程序不做坐标系转换，影像必须与工程同一套投影，否则整体平移"
            : geo.CrsName + "　—— 本程序不做坐标系转换，须与工程同一套投影";
        extentBox.Text = $"X [{geo.MinX:0.#} ~ {geo.MaxX:0.#}]　Y [{geo.MinY:0.#} ~ {geo.MaxY:0.#}]　覆盖 {km2:0.##} km²";

        long bytes = (long)geo.PixelWidth * geo.PixelHeight * 4;
        pixelBox.Text = $"{geo.PixelWidth}×{geo.PixelHeight}　{geo.PixelSizeX:0.###} × {geo.PixelSizeY:0.###} m/像素"
                      + $"　解码后约 {bytes / 1024.0 / 1024.0:0} MB（超 GPU 纹理边长上限时自动降采样）";

        coverText.Text = Coverage(geo);
        toolStatus.Text = $"配准已读出（{geo.Source}）";
        hintLine.Text = Hint(geo);
    }

    /// <summary>
    /// 逐作业面/采掘单元核一遍是否落在影像框内。
    /// <b>没有坐标的对象要单独说"判不了"</b>，不能混进"都在框内"里——那等于用沉默证明了一件没验过的事。
    /// </summary>
    private static string Coverage(OrthophotoGeoRef geo)
    {
        var lines = new List<string>();

        try
        {
            var cfg = ProductionPlanContext.Config();
            var faces = cfg.Faces.Where(f => f.Process == PitMine3D.Kylin.TaskLib.Domain.ProcessType.Load).ToList();
            int inBox = 0, outBox = 0, noPos = 0;
            var outNames = new List<string>();

            foreach (var f in faces)
            {
                if (!f.HasSourcePosition) { noPos++; continue; }
                bool inside = f.SourceX >= geo.MinX && f.SourceX <= geo.MaxX
                           && f.SourceY >= geo.MinY && f.SourceY <= geo.MaxY;
                if (inside) inBox++;
                else { outBox++; if (outNames.Count < 5) outNames.Add(f.Zone); }
            }

            lines.Add($"作业面：{inBox} 个在影像范围内"
                    + (outBox > 0 ? $"　⚠ {outBox} 个在框外（{string.Join("、", outNames)}）—— 这些面在底图上画出来会是空白" : "")
                    + (noPos > 0 ? $"　· {noPos} 个没录源坐标，判不了（在「作业面台账」补坐标）" : ""));
        }
        catch (Exception ex) { lines.Add($"作业面覆盖核对未做：{Short(ex)}"); }

        try
        {
            if (MiningUnitLink.HasBase) lines.Add(MiningUnitLink.SourceLabel + "（单元中心坐标的覆盖核对随「作业区划分」一并做）");
        }
        catch { }

        return lines.Count > 0 ? string.Join("\n", lines) : "没有可核对的对象。";
    }

    private static string Hint(OrthophotoGeoRef? geo) =>
        "配一次即可：作业区划分、生产任务动态调整（三维推演底图）、TIN 正射着色都读这一份配置；"
      + "在那些窗口里临时选过别的文件，也会写回这里。\n"
      + "配准信息每次打开现读文件头（毫秒级），不缓存 —— 影像被换成同名新版本时，缓存的旧四至会让覆盖判断悄悄错掉。"
      + (geo != null ? "" : "\n注：没有 GeoTIFF 标签的影像需要同名 .tfw / .tifw / .wld 世界文件。");

    private void ClearGeoBoxes(string why)
    {
        srcBox.Text = why;
        crsBox.Text = extentBox.Text = pixelBox.Text = "—";
    }

    // ── 操作 ────────────────────────────────────────────────────────────────

    private async void OnPick()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择正射影像（需带 GeoTIFF 配准标签或同名 .tfw 世界文件）",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("正射影像 (GeoTIFF)") { Patterns = new[] { "*.tif", "*.tiff" } }, new FilePickerFileType("所有文件") { Patterns = new[] { "*" } } },
        });
        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;

        if (!OrthophotoConfig.Remember(path))
        { toolStatus.Text = $"配置保存失败：{OrthophotoConfig.LastIoLabel}"; return; }

        Fill();
        toolStatus.Text = $"已配置：{Path.GetFileName(path)}　|　{OrthophotoConfig.LastIoLabel}";
    }

    private async void OnClear()
    {
        if (!OrthophotoConfig.HasPath) { toolStatus.Text = "本来就没配。"; return; }
        if (!await TaskUi.Confirm(this, "影像底图", "清除正射影像配置？\n\n各功能会退回「每次自己选文件」。影像文件本身不受影响。"))
            return;

        OrthophotoConfig.Clear();
        Fill();
        toolStatus.Text = "配置已清除（影像文件本身没动）";
    }

    private void OnAutoLoadChanged()
    {
        var s = OrthophotoConfig.Current;
        s.AutoLoad = autoLoadBox.IsChecked == true;
        toolStatus.Text = OrthophotoConfig.Save(s)
            ? (s.AutoLoad ? "已开：需要底图的窗口打开时自动装载" : "已关：各窗口仍需手动点一次装载")
            : $"保存失败：{OrthophotoConfig.LastIoLabel}";
    }

    private void OnApplyToViewport()
    {
        string path = OrthophotoConfig.ResolvePath(out string why);
        if (path.Length == 0) { toolStatus.Text = why; return; }

        var old = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        BasemapResult res;
        try { res = OrthophotoBasemap.Load(path); }
        finally { Cursor = old; }

        toolStatus.Text = res.Message + (res.Notes.Count > 0 ? "　|　" + string.Join("　", res.Notes) : "");
    }

    private void OnClearViewport()
    {
        // 只从视口卸下贴图，**不动配置** —— 两件事分开：看腻了想关掉，不等于不要这张影像了
        try { toolStatus.Text = OrthophotoBasemap.Clear() + "（配置未改动）"; }
        catch (Exception ex) { toolStatus.Text = $"清除失败：{Short(ex)}"; }
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
