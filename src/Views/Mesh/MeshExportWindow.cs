using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Mesh;

/// <summary>
/// 格网导出（忠实原 <c>MeshEditLib.Views.MeshExportView</c> + <c>MeshExportViewModel</c>）。
///
/// 在此之前 Kylin 的网格导出有两条路，都不是原版那条：`导出三角网` 只出 OFF；
/// `导出OBJ/PLY/STL` 要**先选一个 .off 文件**当源再转格式 —— 而原版是「导出**选中**网格 / 导出**所有**网格」，
/// 直接吃场景里的对象（见 [[port-dialogs-and-entry-paths]]：无数据该报"请先选中"，不该回落去选文件）。
///
/// 原版「导出选项」三项这里都落到了实处：
///   · <b>导出法线</b>：OBJ 出 <c>vn</c> 且面写 <c>f i//i</c>；PLY 头加 <c>nx/ny/nz</c>。
///   · <b>翻转 Y/Z 轴</b>：Z-up（矿业/测绘）→ Y-up（Blender / three.js）。位置与法线同翻。
///   · <b>缩放因子</b>：只乘位置，不乘法线。
///
/// 登记的差异（按原版**实况**而非标签）：原版下拉写「STL Binary」但实现写的是 ASCII，这里标签写「STL ASCII」；
/// 原版 glTF 是带 <c>// TODO</c> 的桩（空 base64 buffer，查看器打不开），按"原版桩忠实不移"不列此档。
/// </summary>
internal sealed class MeshExportWindow : Window
{
    /// <summary>一次导出请求：格式扩展名 + 选项 + 只导选中还是全部。</summary>
    internal sealed record Request(string Ext, MeshExport.Options Options, bool SelectedOnly);

    private readonly Func<Request, string> _export;   // 执行导出, 返回要显示的结果串

    private readonly ComboBox _cmbFormat = new() { Width = 260 };
    private readonly CheckBox _chkNormals = new() { Content = "导出法线" };
    private readonly CheckBox _chkFlip = new() { Content = "翻转 Y/Z 轴" };
    private readonly TextBox _txtScale = new() { Text = "1", Width = 260 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#2B579A"), MinHeight = 34 };

    // 顺序同原版下拉(去掉那档桩 glTF)
    private static readonly (string Label, string Ext)[] Formats =
    {
        ("Wavefront OBJ (*.obj)", "obj"),
        ("Stanford PLY (*.ply)", "ply"),
        ("STL ASCII (*.stl)", "stl"),
        ("Geomview OFF (*.off)", "off"),
    };

    public MeshExportWindow(Func<Request, string> export)
    {
        _export = export;

        Title = "格网导出";
        Width = 420; Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _cmbFormat.ItemsSource = Array.ConvertAll(Formats, f => f.Label);
        _cmbFormat.SelectedIndex = 0;

        static TextBlock Head(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
        static TextBlock Hint(string t) => new() { Text = t, FontSize = 11, Foreground = Brush.Parse("#666"), TextWrapping = TextWrapping.Wrap };

        var btnSel = new Button
        {
            Content = "导出选中网格", Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4),
            FontWeight = FontWeight.Bold,
        };
        var btnAll = new Button
        {
            Content = "导出所有网格", Height = 28, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        btnSel.Click += (_, _) => Run(selectedOnly: true);
        btnAll.Click += (_, _) => Run(selectedOnly: false);

        var panel = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new TextBlock { Text = "格网导出", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#2B579A") },
                Hint("将三角网格导出为通用 3D 格式"),

                Head("导出格式"), _cmbFormat,

                Head("导出选项"), _chkNormals, _chkFlip,
                new TextBlock { Text = "缩放因子:", FontSize = 11, Foreground = Brush.Parse("#666"), Margin = new Thickness(0, 6, 0, 2) },
                _txtScale,

                btnSel, btnAll,

                new Border
                {
                    BorderThickness = new Thickness(1), BorderBrush = Brush.Parse("#DDD"),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 0),
                    Child = Hint("OBJ: 通用文本格式，支持顶点/法线/面。\n"
                               + "PLY: Stanford 格式（此处为 ASCII 变体）。\n"
                               + "STL: 3D 打印标准格式，仅含三角面；法线是格式必需字段，恒写。\n"
                               + "OFF: 最简通用网格格式（顶点表+面表），double 全精度。\n"
                               + "glTF: 原版该档是未完成的占位（空 buffer，查看器打不开），故未列出。"),
                },

                new Border
                {
                    Background = Brush.Parse("#EEF3FA"), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 4), Margin = new Thickness(0, 10, 0, 0), Child = _status,
                },
            },
        };
        Content = new ScrollViewer { Content = panel };
        _status.Text = "选好格式与选项，再点「导出选中网格」或「导出所有网格」。";
    }

    private string SelectedExt => Formats[Math.Max(0, _cmbFormat.SelectedIndex)].Ext;

    internal MeshExport.Options CurrentOptions()
    {
        double scale = 1;
        var t = (_txtScale.Text ?? "").Trim();
        // 解析不了 / 非正数 一律按 1，并在状态行说明 —— 静默按 0 缩放会导出一坨退化到原点的网格
        if (t.Length > 0 && (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0))
            scale = 1;
        return new MeshExport.Options
        {
            Normals = _chkNormals.IsChecked == true,
            FlipYZ = _chkFlip.IsChecked == true,
            Scale = scale,
        };
    }

    private void Run(bool selectedOnly)
    {
        var o = CurrentOptions();
        var raw = (_txtScale.Text ?? "").Trim();
        string warn = raw.Length > 0 && Math.Abs(o.Scale - 1) < 1e-12 && raw != "1"
            ? $"（缩放因子「{raw}」认不出或不是正数，按 1 处理）" : "";
        try { _status.Text = _export(new Request(SelectedExt, o, selectedOnly)) + warn; }
        catch (Exception ex) { _status.Text = $"导出失败：{ex.Message}"; }
    }

    // ── 自检钩子用（脚本点不了控件）──
    internal bool SelectFormat(string extOrLabel)
    {
        for (int i = 0; i < Formats.Length; i++)
            if (string.Equals(Formats[i].Ext, extOrLabel, StringComparison.OrdinalIgnoreCase) || Formats[i].Label == extOrLabel)
            { _cmbFormat.SelectedIndex = i; return true; }
        return false;
    }
    internal void SetNormals(bool on) => _chkNormals.IsChecked = on;
    internal void SetFlip(bool on) => _chkFlip.IsChecked = on;
    internal void SetScale(string v) => _txtScale.Text = v;
    internal void DoExport(bool selectedOnly) => Run(selectedOnly);
    internal string StatusText => _status.Text ?? "";
}
