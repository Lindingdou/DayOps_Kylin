using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 地质体建模(连续多层)非模态窗口(原 MeshEditLib.Dialogs.QuickModelDialog; Ribbon「地质体建模」):
///   · 视口选中层位三角网 → 「从选集添加」入列,按均高自动排序(自上而下);
///   · N 张面一次构建 N−1 个封闭地质体(相邻两面夹一层),逐层调 <see cref="QuickModelSampler.BuildFromMeshesDirect"/>,
///     落「地质体·层N」图层 + 调色板配色, 经 <see cref="ModelingContext.AddMesh"/> 入场景。
/// 构建期间禁用按钮防重入;每层结果与总耗时/成功计数回显到窗口与主状态栏。
/// </summary>
public partial class QuickModelDialog : Window
{
    private readonly ModelingContext? _ctx;
    private readonly List<LayerRow> _rows = new();

    private sealed class LayerRow
    {
        public MeshEntity Mesh = null!;
        public double MeanZ;
    }

    // 层实体调色板(大地色系,循环取用)
    private static readonly (byte r, byte g, byte b)[] Palette =
    {
        (0xB8, 0xAE, 0x9C), (0x8C, 0x7A, 0x5B), (0x4A, 0x44, 0x3C), (0xC9, 0xA6, 0x6B),
        (0x7D, 0x8C, 0x6B), (0x9C, 0x6B, 0x5B), (0x6B, 0x7D, 0x8C), (0xA6, 0x8C, 0xA6),
    };

    public QuickModelDialog() { InitializeComponent(); }

    public QuickModelDialog(ModelingContext ctx) : this()
    {
        _ctx = ctx;
        ReadFromSelection();
    }

    /// <summary>从当前选集补充层位面(按实体去重)。</summary>
    public void ReadFromSelection()
    {
        try
        {
            if (_ctx != null)
                foreach (var m in _ctx.SelectedMeshes())
                {
                    if (m.Verts.Count < 3 || m.Tris.Count < 1) continue;
                    if (_rows.Any(r => ReferenceEquals(r.Mesh, m))) continue;
                    _rows.Add(new LayerRow { Mesh = m, MeanZ = LayerSolid.MeanZ(m.Verts) });
                }
            _rows.Sort((a, b) => b.MeanZ.CompareTo(a.MeanZ));   // 自上而下
            RefreshUi();
        }
        catch (Exception ex)
        {
            txtStatus.IsVisible = true;
            txtStatus.Text = $"读取选集失败:{ex.Message}";
        }
    }

    private void RefreshUi()
    {
        panelLayers.Children.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ColumnDefinitions = new ColumnDefinitions("22,*,Auto") };
            var swatch = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
            };
            if (i < _rows.Count - 1)   // 色块 = 本面与下一面之间那层实体的颜色(最后一面无层,空心)
            {
                var c = Palette[i % Palette.Length];
                swatch.Background = new SolidColorBrush(Color.FromRgb(c.r, c.g, c.b));
            }
            Grid.SetColumn(swatch, 0); grid.Children.Add(swatch);

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0),
                Text = $"层位 {i + 1} · {row.Mesh.Name} · 均高 {row.MeanZ:F1} m · {row.Mesh.Tris.Count:N0} 三角",
            };
            Grid.SetColumn(label, 1); grid.Children.Add(label);

            var btnRemove = new Button { Content = "移除", MinWidth = 48, Tag = row };
            btnRemove.Click += (_, _) => { _rows.Remove(row); RefreshUi(); };
            Grid.SetColumn(btnRemove, 2); grid.Children.Add(btnRemove);
            panelLayers.Children.Add(grid);
        }
        txtLayerSummary.Text = _rows.Count switch
        {
            0 => "尚未添加层位面。在视口选中三角网后点「从选集添加」。",
            1 => "已有 1 张面 —— 还差 1 张(至少 2 张面才能夹出 1 层)。",
            _ => $"共 {_rows.Count} 张面 → 将构建 {_rows.Count - 1} 层封闭地质体。",
        };
    }

    private void OnAddFromSelectionClick(object? sender, RoutedEventArgs e) => ReadFromSelection();
    private void OnClearLayersClick(object? sender, RoutedEventArgs e) { _rows.Clear(); RefreshUi(); }
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnBuildClick(object? sender, RoutedEventArgs e)
    {
        if (_rows.Count < 2) { txtStatus.IsVisible = true; txtStatus.Text = "至少需要 2 张层位面。"; return; }
        bool flipN = chkFlipN.IsChecked == true;
        bool multi = _rows.Count > 2;
        btnBuild.IsEnabled = false;
        txtStatus.IsVisible = true;
        var lines = new List<string>();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int okCount = 0;
            var made = new List<SceneEntity>();
            for (int i = 0; i < _rows.Count - 1; i++)
            {
                var top = _rows[i]; var bot = _rows[i + 1];
                string layerName = multi ? $"地质体·层{i + 1}" : "地质体";
                var color = Palette[i % Palette.Length];
                lines.Add($"层 {i + 1}/{_rows.Count - 1}:构建中…");
                txtStatus.Text = string.Join("\n", lines);

                var (tv, tt) = top.Mesh.Flatten(); var (bv, bt) = bot.Mesh.Flatten();
                var res = await Task.Run(() => QuickModelSampler.BuildFromMeshesDirect(tv, tt, bv, bt, flipN, layerName));
                string line;
                if (!res.Ok) line = $"层 {i + 1}: ✗ {res.Error}";
                else
                {
                    var me = new MeshEntity(layerName, res.Verts, res.Tris) { LayerName = layerName, Cr = color.r / 255f, Cg = color.g / 255f, Cb = color.b / 255f };
                    _ctx?.AddMesh(me, false);
                    made.Add(me);
                    okCount++;
                    line = $"层 {i + 1}: ✓ {res.TotalTris:N0} 三角 · " +
                           (res.Watertight ? "水密" : $"开放边 {res.OpenEdges}/非流形 {res.NonManifoldEdges}") +
                           $" · 侧壁 {res.WallLoops} 对" +
                           (res.FaultLoops > 0 ? $" · 断层面 {res.FaultLoops}" : "") +
                           (res.CappedLoops > 0 ? $" · 封盖 {res.CappedLoops}" : "") +
                           $" · 图层「{layerName}」";
                }
                lines[^1] = line;
                txtStatus.Text = string.Join("\n", lines);
                _ctx?.Status($"> GEOMODEL {line}");
            }
            sw.Stop();
            lines.Add($"完成:{okCount}/{_rows.Count - 1} 层成功({sw.ElapsedMilliseconds} ms)。");
            txtStatus.Text = string.Join("\n", lines);
            if (made.Count > 0) _ctx?.Select(made);
            _ctx?.Status($"✓ 地质体建模完成:{okCount}/{_rows.Count - 1} 层({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            lines.Add($"构建异常:{ex.Message}");
            txtStatus.Text = string.Join("\n", lines);
            _ctx?.Status($"地质体建模异常:{ex.Message}");
        }
        finally { btnBuild.IsEnabled = true; }
    }
}
