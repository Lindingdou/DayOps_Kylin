using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 导入块体对话框（忠实原 ImportBlockModelDialog）：PMB（<see cref="PmbImportService"/>）/ BLK（<see cref="BlkImportService"/>）
/// 原生支持；CSV（x,y,z[,尺寸,品位]，<see cref="BlockModel.Parse"/>，Kylin 已有）按 CSV 选项读；JSON / FlatBuffers 原程序即未接入。
/// 重名自动加后缀。<c>ShowDialog&lt;bool&gt;</c> true = 已导入。
/// </summary>
public partial class ImportBlockModelWindow : Window
{
    private readonly ModelingContext _ctx;

    public ImportBlockModelWindow() { _ctx = null!; InitializeComponent(); }

    public ImportBlockModelWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        filePathBox.TextChanged += (_, _) => UpdatePreview();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _ctx.OpenFileAsync("选择块体文件", new[] { "*.pmb", "*.blk", "*.csv", "*.txt" });
            if (path != null) filePathBox.Text = path;
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "选择文件", ex.Message); }
    }

    private string Format(string path)
    {
        if (fmtPmb.IsChecked == true) return "pmb";
        if (fmtCsv.IsChecked == true) return "csv";
        if (fmtJson.IsChecked == true) return "json";
        if (fmtFlat.IsChecked == true) return "flat";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch { ".pmb" => "pmb", ".blk" => "blk", ".csv" or ".txt" or ".xyz" => "csv", ".json" => "json", _ => ext.TrimStart('.') };
    }

    private Encoding CsvEncoding()
    {
        try
        {
            return csvEnc.SelectedIndex switch
            {
                1 => (Encoding.GetEncoding("GBK")),
                2 => Encoding.Latin1,
                _ => new UTF8Encoding(false),
            };
        }
        catch
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding("GBK"); }
            catch { return new UTF8Encoding(false); }
        }
    }

    private void UpdatePreview()
    {
        string path = filePathBox.Text?.Trim() ?? "";
        if (!File.Exists(path)) { previewBox.IsVisible = false; statusLabel.Text = "ⓘ 选择文件后即估算行数与内存"; return; }
        try
        {
            var fi = new FileInfo(path);
            string fmt = Format(path);
            if (fmt == "csv")
            {
                var lines = File.ReadLines(path, CsvEncoding()).Take(10).ToList();
                previewBox.Text = string.Join("\n", lines);
                previewBox.IsVisible = true;
                double avgLen = lines.Count > 0 ? lines.Average(l => l.Length + 1) : 20;
                long approxRows = (long)Math.Max(0, fi.Length / Math.Max(1.0, avgLen) - (csvHeader.IsChecked == true ? 1 : 0));
                statusLabel.Text = $"ⓘ CSV 约 {(long)approxRows:N0} 行 · 文件 {BlockModelMeta.BytesToHuman(fi.Length)} · 内存 ≈ {BlockModelMeta.BytesToHuman((long)approxRows * 48)}";
            }
            else
            {
                previewBox.IsVisible = false;
                statusLabel.Text = $"ⓘ {fmt.ToUpperInvariant()} 文件 {BlockModelMeta.BytesToHuman(fi.Length)}";
            }
        }
        catch (Exception ex) { statusLabel.Text = $"预览失败：{ex.Message}"; }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        try
        {
            string path = filePathBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path)) { await BlockMsgBox.WarnAsync(this, "导入块体", "请先选择要导入的文件。"); return; }
            if (!File.Exists(path)) { await BlockMsgBox.WarnAsync(this, "导入块体", $"文件不存在：\n{path}"); return; }
            string fmt = Format(path);
            // 读文件与建元数据都放后台线程：八十多 MB 的块体文件占着 UI 线程, 窗口就会挂"(未响应)"
            importBtn.IsEnabled = false;
            statusLabel.Text = "ⓘ 正在读取…界面可继续操作";
            List<BlockModel.Block> blocks; Dictionary<string, double[]>? attrs = null; string note;
            string baseName = Path.GetFileNameWithoutExtension(path);
            PmbImportService.Result? pmb = null;
            BlkImportService.Result? blk = null;
            switch (fmt)
            {
                case "pmb":
                {
                    var r = await Task.Run(() => PmbImportService.Load(path));   // 后台读, 同 blk
                    if (!r.Success) { await BlockMsgBox.WarnAsync(this, "块体文件无效", r.Error); return; }
                    pmb = r;
                    blocks = r.Blocks; attrs = r.AllAttrs.Count > 0 ? r.AllAttrs : null;
                    if (!string.IsNullOrWhiteSpace(r.ModelName)) baseName = r.ModelName;
                    note = $"\n网格 {r.Nx}×{r.Ny}×{r.Nz}，含 {r.AttrNames.Count} 个属性列的 cell 数据。";
                    if (r.Schema.Count > 0) note += $"\n属性表 {r.Schema.Count} 列（类型/单位/默认值/分类码名）已随文件恢复。";
                    if (r.DeletedCells.Count > 0) note += $"\n含 {r.DeletedCells.Count:N0} 个已删除 cell。";
                    break;
                }
                case "blk":
                {
                    var r = await Task.Run(() => BlkImportService.Load(path));   // 后台读: 八十多 MB 的块体文件别占着 UI 线程
                    if (!r.Success) { await BlockMsgBox.WarnAsync(this, "块体文件无效", r.Error); return; }
                    blk = r;
                    blocks = r.Blocks; attrs = r.AllAttrs.Count > 0 ? r.AllAttrs : null;
                    note = $"\n八叉树原生 {r.BlockCount:N0} 个子块（变尺寸），含 {r.AttrNames.Count} 个属性列。"
                         + $"\n铺满 {r.Nx}×{r.Ny}×{r.Nz} 细格（最细格 {r.Bx:0.##}×{r.By:0.##}×{r.Bz:0.###} m）。";
                    if (r.CategoryNames.Count > 0) note += $"\n类别名表 {r.CategoryNames.Count} 类，随文件配色 {r.CategoryColors.Count} 类。";
                    break;
                }
                case "csv":
                {
                    var enc = CsvEncoding();
                    var text = await Task.Run(() => File.ReadAllText(path, enc));
                    var r = BlockModel.Parse(text);
                    if (!r.Success) { await BlockMsgBox.WarnAsync(this, "块体文件无效", r.Error ?? "解析失败"); return; }
                    blocks = r.Blocks;
                    note = $"\nCSV 解析 {r.Blocks.Count:N0} 块（跳过 {r.SkippedLines} 行），品位 {r.GradeMin:0.###}~{r.GradeMax:0.###}。";
                    break;
                }
                default:
                    await BlockMsgBox.WarnAsync(this, "导入块体", "当前仅支持 PMB（.pmb）、Block_Model（.blk）与 CSV 格式。\nJSON / FlatBuffers 等格式将在后续版本接入。");
                    return;
            }
            if (blocks.Count == 0) { await BlockMsgBox.WarnAsync(this, "导入块体", "文件中没有块体。"); return; }
            if (blocks.Count > BlockVoxelBuilder.MaxRenderBlocks)
            {
                bool go = await BlockMsgBox.ConfirmAsync(this, "块数较多", $"文件含 {blocks.Count:N0} 块，超过视口逐块渲染建议上限 {BlockVoxelBuilder.MaxRenderBlocks:N0}，导入后视口可能卡顿。\n仍要导入？");
                if (!go) return;
            }
            string uniq = BlockModelStore.UniqueName(baseName);
            var capturedBlocks = blocks; var capturedAttrs = attrs; var capturedBlk = blk;
            // .blk 的几何由文件直接给定（原点 / 三轴细格 25×25×0.5 m / 细格维度 / 层级）——
            // 交给 FromBlocks 去"猜"只会猜出一个各向同性尺寸，块被画成 25 的立方体、竖向胖 50 倍。
            var meta = await Task.Run(() => capturedBlk != null
                ? BlockModelMeta.FromLeaves(uniq, capturedBlocks, capturedAttrs,
                    capturedBlk.Ox, capturedBlk.Oy, capturedBlk.Oz,
                    capturedBlk.Bx, capturedBlk.By, capturedBlk.Bz,
                    capturedBlk.Nx, capturedBlk.Ny, capturedBlk.Nz,
                    capturedBlk.MaxSub, capturedBlk.VarCellCount)
                : BlockModelMeta.FromBlocks(uniq, capturedBlocks, capturedAttrs));
            meta.DisplayStyle.FillColor = BlockDefaultPalette.Next(BlockModelStore.Models.Count);
            if (meta.PropertySchema.Count > 0) { meta.ActiveColormapAttribute = meta.PropertySchema[0].Name; meta.ColormapRange = null; }
            if (blk != null) ApplyBlkMetadata(meta, blk);
            if (pmb != null) ApplyPmbMetadata(meta, pmb);
            // 建网格同样放后台(CreateAsync): 三百万块建一次网近一秒, 占着 UI 线程窗口就是"(未响应)"
            statusLabel.Text = $"ⓘ 已读入 {blocks.Count:N0} 块，正在建网格…";
            var err = await BlockModelStore.CreateAsync(_ctx, meta);
            if (err != null) { await BlockMsgBox.WarnAsync(this, "导入失败", err); return; }
            await BlockMsgBox.InfoAsync(this, "导入成功", $"模型 {meta.Name} 已导入（{meta.BlockCount:N0} 块）。{note}\n来源: {path}");
            _ctx.Status($"导入块体 {meta.Name}：{blocks.Count:N0} 块");
            Close(true);
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "导入块体", ex.Message); }
        finally { importBtn.IsEnabled = true; UpdatePreview(); }   // 任何一条 return 都要把按钮放回去
    }

    /// <summary>
    /// 把 .blk 里随文件存的元数据落回模型（同原 BlkReader → BlockModelSpec 装配）：
    /// 属性表（分类/数值、单位、-1 空值哨兵、类别名表）、按类别码的显示色表、推荐着色属性。
    /// 没有这一步，「矿岩类型」这种分类码会被当连续值走色带 —— 就是导入后满屏蓝红渐变、看不出岩性的原因。
    /// </summary>
    internal static void ApplyBlkMetadata(BlockModelMeta meta, BlkImportService.Result r)
    {
        meta.Description = $"导入自 Block_Model_2.0 (.blk)：{r.BlockCount:N0} 个 octree 子块（原生变尺寸），"
                         + $"铺满 {r.Nx}×{r.Ny}×{r.Nz} 细格立方体（最细格 {r.Bx:0.##}×{r.By:0.##}×{r.Bz:0.###} m）。";
        for (int a = 0; a < r.AttrNames.Count; a++)
        {
            var col = meta.PropertySchema.Find(c => c.Name == r.AttrNames[a]);
            if (col == null) continue;
            bool cat = r.AttrCategorical[a];
            col.DataType = cat ? BlockPropertyType.Int32 : BlockPropertyType.Float;
            col.IsCategorical = cat;
            col.DefaultValue = BlkImportService.NoData;      // -1 = 空 cell / nodata（与源文件一致）
            col.Unit = BlkImportService.UnitFor(r.AttrNames[a]);
            if (cat && r.CategoryNames.Count > 0) col.CategoryLabels = new List<string>(r.CategoryNames);
        }
        if (r.CategoryColors.Count > 0 && !string.IsNullOrEmpty(r.SuggestedAttr))
        {
            var map = meta.DisplayStyle.EnsureCategoryColors(r.SuggestedAttr!);
            foreach (var kv in r.CategoryColors) map[kv.Key] = kv.Value;
        }
        if (r.FillColor is { } fc) meta.DisplayStyle.FillColor = fc;
        if (!string.IsNullOrEmpty(r.SuggestedAttr) && meta.PropertySchema.Exists(c => c.Name == r.SuggestedAttr))
        {
            meta.ActiveColormapAttribute = r.SuggestedAttr;   // 优先「带色表的分类属性」，同原 SuggestedColormapAttribute
            meta.ColormapRange = null;
        }
    }

    /// <summary>
    /// 把 PMB 里随文件存的模型元数据落回模型（同原 PmbmReader → BlockModelSpec 装配）：
    /// 属性表（类型/单位/默认值/备注/分类码名）、显示样式 + 活动着色属性、已删 cell、旋转 / 存储模式 / 子块参数。
    /// </summary>
    internal static void ApplyPmbMetadata(BlockModelMeta meta, PmbImportService.Result r)
    {
        meta.Description = r.Description;
        meta.RotationZDeg = r.RotationZDeg;
        meta.StorageMode = r.StorageMode;
        if (r.SubBlockDepthMax > 0) meta.SubBlockDepthMax = r.SubBlockDepthMax;
        if (r.SubMinX > 0) { meta.SubMinX = r.SubMinX; meta.SubMinY = r.SubMinY; meta.SubMinZ = r.SubMinZ; }

        // 属性表：文件里的列信息覆盖「按数据推」的那份（同名列补齐类型/单位/默认值/备注/分类标签）
        foreach (var col in r.Schema)
        {
            var exist = meta.PropertySchema.Find(c => c.Name == col.Name);
            if (exist == null) meta.PropertySchema.Add(col);
            else
            {
                exist.DataType = col.DataType; exist.Unit = col.Unit; exist.DefaultValue = col.DefaultValue;
                exist.Description = col.Description; exist.IsCategorical = col.IsCategorical;
                if (col.CategoryLabels is { Count: > 0 }) exist.CategoryLabels = col.CategoryLabels;
            }
        }

        if (r.Style is { } st)
        {
            meta.DisplayStyle.FillColor = st.FillColor;
            meta.DisplayStyle.EdgeColor = st.EdgeColor;
            meta.DisplayStyle.EdgeMode = st.EdgeMode;
            meta.DisplayStyle.EdgeWidthPx = st.EdgeWidthPx;
            meta.DisplayStyle.DefaultColormap = st.DefaultColormap;
            foreach (var kv in st.CategoryColors)
            {
                var map = meta.DisplayStyle.EnsureCategoryColors(kv.Key);
                foreach (var c in kv.Value) map[c.Key] = c.Value;
            }
        }
        if (!string.IsNullOrEmpty(r.ActiveColormapAttribute) && meta.PropertySchema.Exists(c => c.Name == r.ActiveColormapAttribute))
        {
            meta.ActiveColormapAttribute = r.ActiveColormapAttribute;
            meta.ColormapRange = null;
        }

        // 已删 cell：PMB 的线性下标 = 本次导入的块序（reader 按 x-fastest 生成整个网格）
        foreach (var id in r.DeletedCells)
            if (id >= 0 && id < meta.Blocks.Count) meta.DeletedIds.Add((int)id);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
