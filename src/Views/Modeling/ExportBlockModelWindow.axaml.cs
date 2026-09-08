using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 导出块体对话框（忠实原 ExportBlockModelDialog）：来源块体 / 导出范围（全部·当前选择集·属性表达式）/ 导出列（几何 + 属性勾选）/
/// 输出（PMB 走 <see cref="PmbExportService"/>；CSV 逐块 cx,cy,cz,sx,sy,sz,属性…；JSON/FlatBuffers 原程序即禁用）；「预览 5 行」等宽文本弹窗。
/// 「当前选择集」= 当前筛选可见块（原选择集 v2 未接入，Kylin 用筛选可见等价）。
/// </summary>
public partial class ExportBlockModelWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly List<CheckBox> _attrBoxes = new();

    public ExportBlockModelWindow() { _ctx = null!; InitializeComponent(); }

    public ExportBlockModelWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        sourceCombo.ItemsSource = BlockModelStore.Models;
        var def = BlockModelStore.PickDefault();
        if (def != null) sourceCombo.SelectedItem = def;
        formatCombo.SelectionChanged += (_, _) => FixExtension();
        foreach (var rb in new[] { scopeAll, scopeSelection, scopeExpr }) rb.IsCheckedChanged += (_, _) => UpdateEstimate();
    }

    private BlockModelMeta? Source => sourceCombo.SelectedItem as BlockModelMeta;
    private string Ext => formatCombo.SelectedIndex == 1 ? ".csv" : ".pmb";

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        attrChecks.Children.Clear(); _attrBoxes.Clear();
        if (Source is { } m)
        {
            foreach (var c in m.PropertySchema)
            {
                var cb = new CheckBox { Content = string.IsNullOrEmpty(c.Unit) ? c.Name : $"{c.Name}  ({c.Unit})", IsChecked = true, Tag = c.Name };
                _attrBoxes.Add(cb); attrChecks.Children.Add(cb);
            }
            attrHint.Text = m.PropertySchema.Count == 0 ? "模型未定义任何属性列，导出只含几何 + spec。" : $"{m.PropertySchema.Count} 个属性列";
            if (string.IsNullOrWhiteSpace(pathBox.Text)) pathBox.Text = m.Name + Ext;
        }
        UpdateEstimate();
    }

    private void FixExtension()
    {
        string p = pathBox.Text?.Trim() ?? "";
        if (p.Length == 0) return;
        if (p.EndsWith(".pmb", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 4);
        pathBox.Text = p + Ext;
        UpdateEstimate();
    }

    /// <summary>导出范围内的块索引。</summary>
    private List<int> ScopeIndices(BlockModelMeta m, out string? error)
    {
        error = null;
        var idx = new List<int>();
        BlockCellPredicate? pred = null;
        MutableBlockExprContext? ctx = null; List<string>? refAttrs = null;
        if (scopeExpr.IsChecked == true)
        {
            try { pred = BlockCellPredicate.Compile(exprBox.Text ?? ""); }
            catch (BlockExprException ex) { error = "属性表达式语法错误：" + ex.Message; return idx; }
            ctx = new MutableBlockExprContext(); m.FillConstants(ctx);
            refAttrs = pred.ReferencedVariables.Where(v => !BlockModelMeta.IsBuiltinVar(v.ToLowerInvariant())).ToList();
        }
        for (int i = 0; i < m.Blocks.Count; i++)
        {
            if (m.DeletedIds.Contains(i)) continue;
            if (scopeSelection.IsChecked == true && !m.IsCellVisible(i)) continue;
            if (pred != null) { m.FillContext(ctx!, i, refAttrs!); if (!pred.Evaluate(ctx!)) continue; }
            idx.Add(i);
        }
        return idx;
    }

    private void UpdateEstimate()
    {
        if (Source is not { } m) { statusLabel.Text = "ⓘ 预计行数与文件大小将在选定范围后估算"; return; }
        var idx = ScopeIndices(m, out var err);
        if (err != null) { statusLabel.Text = "ⓘ " + err; return; }
        int cols = 6 + _attrBoxes.Count(b => b.IsChecked == true);
        long bytes = Ext == ".csv" ? idx.Count * (long)cols * 10 : (long)m.BlockCount * 4 * Math.Max(1, cols - 5) + 1024;
        statusLabel.Text = $"ⓘ 预计 {idx.Count:N0} 行 · 约 {BlockModelMeta.BytesToHuman(bytes)}";
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            string name = (Source?.Name ?? "BlockModel") + Ext;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Ext == ".csv" ? "导出块体到 CSV 文件" : "导出块体到 PMB 文件", SuggestedFileName = name, DefaultExtension = Ext.TrimStart('.'),
                FileTypeChoices = new[] { new FilePickerFileType(Ext == ".csv" ? "CSV" : "PMB") { Patterns = new[] { "*" + Ext } } },
            });
            if (file != null) { pathBox.Text = file.Path.LocalPath; UpdateEstimate(); }
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "选择输出路径", ex.Message); }
    }

    private IEnumerable<string> SelectedAttrs() => _attrBoxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!);

    private string BuildPreviewText(BlockModelMeta m, int maxRows)
    {
        var ci = CultureInfo.InvariantCulture;
        var attrs = SelectedAttrs().ToList();
        var sb = new StringBuilder(1024);
        sb.AppendLine($"模型: {m.Name}    总块数: {m.BlockCount:N0}    已删: {m.DeletedBlockCount:N0}");
        sb.AppendLine($"网格: {m.Nx}×{m.Ny}×{m.Nz}    块尺寸: {m.Sx}×{m.Sy}×{m.Sz}    存储: {m.StorageMode}");
        sb.AppendLine(new string('─', 72));
        sb.Append("ID         ").Append("(i, j, k)        ").Append("cx        cy        cz       ");
        foreach (var a in attrs) sb.Append(a.PadRight(Math.Max(12, a.Length + 2)));
        sb.AppendLine(); sb.AppendLine(new string('─', 72));
        var idx = ScopeIndices(m, out _);
        int rows = 0;
        foreach (var i in idx)
        {
            if (rows >= maxRows) break;
            var b = m.Blocks[i];
            var (ii, jj, kk) = m.IsRegular ? m.IJK(i) : ((int)Math.Floor((b.X - m.Ox) / m.Sx), (int)Math.Floor((b.Y - m.Oy) / m.Sy), (int)Math.Floor((b.Z - m.Oz) / m.Sz));
            sb.Append(i.ToString(ci).PadRight(11)).Append($"({ii,3}, {jj,3}, {kk,3})  ");
            sb.Append(b.X.ToString("0.###", ci).PadRight(10)).Append(b.Y.ToString("0.###", ci).PadRight(10)).Append(b.Z.ToString("0.###", ci).PadRight(9));
            foreach (var a in attrs) sb.Append(m.GetValue(a, i).ToString("0.####", ci).PadRight(Math.Max(12, a.Length + 2)));
            sb.AppendLine(); rows++;
        }
        if (rows == 0) sb.AppendLine("(无可见 cell 可预览 — 所有 cell 已被标记删除或范围为空)");
        if (attrs.Count == 0) sb.AppendLine("\nⓘ 未勾选任何属性列，导出只含几何 + spec。");
        if (m.Attrs.Count == 0) sb.AppendLine("ⓘ CellData 为空（未做过属性赋值），所有属性显示其 DefaultValue。");
        return sb.ToString();
    }

    private async void OnPreview(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Source is not { } m) { await BlockMsgBox.WarnAsync(this, "导出预览", "请先选择要预览的块体模型。"); return; }
            var win = new Window
            {
                Title = $"导出预览 - {m.Name} (前 5 cell)", Width = 780, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.Parse("#F4F5F7")),
            };
            var tb = new TextBox { Text = BuildPreviewText(m, 5), IsReadOnly = true, FontFamily = new FontFamily("Consolas,monospace"), FontSize = 12, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap };
            var btn = new Button { Content = "关闭", Width = 80, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, IsCancel = true, IsDefault = true };
            btn.Click += (_, _) => win.Close();
            var g = new Grid { Margin = new Thickness(12), RowDefinitions = new RowDefinitions("*,Auto") };
            Grid.SetRow(btn, 1); g.Children.Add(tb); g.Children.Add(btn);
            win.Content = g;
            await win.ShowDialog(this);
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "导出预览", ex.Message); }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Source is not { } m) { await BlockMsgBox.WarnAsync(this, "导出块体", "请先选择要导出的块体模型。"); return; }
            if (string.IsNullOrWhiteSpace(pathBox.Text)) { await BlockMsgBox.WarnAsync(this, "导出块体", "请先选择输出路径。"); return; }
            string path = pathBox.Text.Trim();
            if (!path.EndsWith(Ext, StringComparison.OrdinalIgnoreCase)) path += Ext;
            var idx = ScopeIndices(m, out var err);
            if (err != null) { await BlockMsgBox.WarnAsync(this, "导出块体", err); return; }
            if (idx.Count == 0) { await BlockMsgBox.WarnAsync(this, "导出块体", "导出范围内没有块。"); return; }
            var attrs = SelectedAttrs().ToList();
            if (Ext == ".csv")
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.Append("cx,cy,cz,sx,sy,sz");
                foreach (var a in attrs) sb.Append(',').Append(BlockModelReport.EscCsv(a));
                sb.AppendLine();
                foreach (var i in idx)
                {
                    var b = m.Blocks[i];
                    bool sub = m.SubCellCount > 0 && b.Size < m.Sx - 1e-9;
                    sb.Append(b.X.ToString("R", ci)).Append(',').Append(b.Y.ToString("R", ci)).Append(',').Append(b.Z.ToString("R", ci)).Append(',')
                      .Append((sub ? b.Size : m.Sx).ToString("R", ci)).Append(',').Append((sub ? b.Size : m.Sy).ToString("R", ci)).Append(',').Append((sub ? b.Size : m.Sz).ToString("R", ci));
                    foreach (var a in attrs) sb.Append(',').Append(m.GetValue(a, i).ToString("R", ci));
                    sb.AppendLine();
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            else
            {
                // 「全部块」= 整模型原样落盘（网格 + 属性表 + 样式 + 已删集合 + 分类元数据，同原版 PmbmWriter.Write）；
                // 选了更窄的范围才按子集重建网格。
                bool whole = scopeAll.IsChecked == true;
                File.WriteAllBytes(path, PmbExportService.FromModel(m, whole ? null : idx, attrs));
            }
            await BlockMsgBox.InfoAsync(this, "导出成功", $"模型 {m.Name} 已导出到：\n{path}\n\n大小: {new FileInfo(path).Length / 1024.0:F1} KB");
            _ctx.Status($"导出块体 {m.Name}：{idx.Count:N0} 块 → {Path.GetFileName(path)}");
            Close(true);
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "导出块体", ex.Message); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
