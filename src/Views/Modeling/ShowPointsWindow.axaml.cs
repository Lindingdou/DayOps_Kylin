using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 展点窗口（原 MeshEditLib.Views.ShowPointsWindow）：从「文件(CSV/Excel .xlsx/文本)」或「剪贴板」加载 X,Y,Z 点 →
/// 表格显示 + 逐行坐标校验 → 校验通过的点展绘到视图(PointEntity 十字标记, 大小按 XY 范围自适应, 一步 Undo)。
/// 表格可手动编辑/补录。.xlsx 用纯 BCL(ZipArchive + XML)读取, 免第三方库。
/// </summary>
public partial class ShowPointsWindow : Window
{
    private readonly ModelingContext? _ctx;
    private readonly ObservableCollection<PointRow> _rows = new();

    public ShowPointsWindow() { InitializeComponent(); grid.ItemsSource = _rows; }
    public ShowPointsWindow(ModelingContext ctx) : this() { _ctx = ctx; }

    // ── 加载 ─────────────────────────────────────────────────────────────
    private async void OnLoadFile(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        var path = await _ctx.OpenFileAsync("选择散点文件 (CSV / Excel / 文本)", new[] { "*.csv", "*.xlsx", "*.txt" });
        if (path == null) return;
        try
        {
            IEnumerable<string> lines = System.IO.Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? ReadXlsxRows(path) : System.IO.File.ReadLines(path);
            FillFromLines(lines);
            summaryText.Text = $"已从 {System.IO.Path.GetFileName(path)} 加载。" + Summary();
        }
        catch (Exception ex) { summaryText.Text = $"加载失败：{ex.Message}"; }
    }

    private async void OnLoadClipboard(object? sender, RoutedEventArgs e)
    {
        try
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            string text = cb != null ? (await cb.GetTextAsync() ?? "") : "";
            if (string.IsNullOrWhiteSpace(text)) { summaryText.Text = "剪贴板无文本(请先在表格软件选中 X,Y,Z 列复制)"; return; }
            FillFromLines(text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None));
            summaryText.Text = "已从剪贴板加载。" + Summary();
        }
        catch (Exception ex) { summaryText.Text = $"加载失败：{ex.Message}"; }
    }

    private void OnClear(object? sender, RoutedEventArgs e) { _rows.Clear(); summaryText.Text = "已清空"; }

    /// <summary>把多行文本(每行按 , \t 空格 ; 分割)装进表格;取前 3 个字段为 X,Y,Z 并校验。</summary>
    public void FillFromLines(IEnumerable<string> lines)
    {
        _rows.Clear();
        foreach (var row in ParseLines(lines)) _rows.Add(row);
        UpdateSummary();
    }

    /// <summary>纯解析(可单测): 跳过空行与 # 注释行, 前 3 字段 → X/Y/Z 并校验。</summary>
    public static List<PointRow> ParseLines(IEnumerable<string> lines)
    {
        var list = new List<PointRow>();
        int idx = 0;
        foreach (var raw in lines)
        {
            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s) || s.StartsWith('#')) continue;
            var parts = s.Split(new[] { ',', '\t', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var row = new PointRow { Index = ++idx, X = parts.Length > 0 ? parts[0] : "", Y = parts.Length > 1 ? parts[1] : "", Z = parts.Length > 2 ? parts[2] : "" };
            Validate(row);
            list.Add(row);
        }
        return list;
    }

    // ── 校验 ─────────────────────────────────────────────────────────────
    private static bool TryNum(string? s, out double v)
    {
        s = s?.Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
    }

    /// <summary>校验一行的 X,Y,Z 是否都是数值坐标。</summary>
    public static void Validate(PointRow r)
    {
        bool ok = TryNum(r.X, out double x) & TryNum(r.Y, out double y) & TryNum(r.Z, out double z);
        r.Xv = x; r.Yv = y; r.Zv = z; r.Valid = ok;
        if (ok) r.Status = "✓ 坐标";
        else
        {
            var bad = new List<string>();
            if (!TryNum(r.X, out _)) bad.Add("X");
            if (!TryNum(r.Y, out _)) bad.Add("Y");
            if (!TryNum(r.Z, out _)) bad.Add("Z");
            r.Status = $"✗ {string.Join("/", bad)} 非数值";
        }
    }

    private string Summary()
    {
        int valid = _rows.Count(r => r.Valid);
        return $"共 {_rows.Count} 行，有效坐标 {valid} 行，无效 {_rows.Count - valid} 行";
    }

    private void UpdateSummary()
    {
        int i = 0;
        foreach (var r in _rows) r.Index = ++i;
        summaryText.Text = Summary();
    }

    private void OnCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (e.Row.DataContext is PointRow r) Validate(r);
            UpdateSummary();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── 入库 ─────────────────────────────────────────────────────────────
    private void OnCommit(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) { summaryText.Text = "展绘不可用：未接入主窗口"; return; }
        var xyz = new List<double>(_rows.Count * 3);
        foreach (var r in _rows)
        {
            Validate(r);
            if (r.Valid) { xyz.Add(r.Xv); xyz.Add(r.Yv); xyz.Add(r.Zv); }
        }
        if (xyz.Count == 0) { summaryText.Text = "没有有效坐标行可展绘(请检查 X,Y,Z 列是否为数值)"; return; }
        try
        {
            double size = ComputeMarkerSize(xyz);
            var ents = new List<SceneEntity>(xyz.Count / 3);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i + 2 < xyz.Count; i += 3)
            {
                ents.Add(new PointEntity { X = xyz[i], Y = xyz[i + 1], Elevation = xyz[i + 2], Size = size, Style = 2 /* 十字 */ });
                if (xyz[i] < minX) minX = xyz[i]; if (xyz[i] > maxX) maxX = xyz[i];
                if (xyz[i + 1] < minY) minY = xyz[i + 1]; if (xyz[i + 1] > maxY) maxY = xyz[i + 1];
            }
            _ctx.AddEntities(ents, null, maxX > minX && maxY > minY ? new[] { minX, minY, maxX, maxY } : null);
            _ctx.Select(ents);
            summaryText.Text = $"已在视图绘制 {ents.Count} 个点(十字标记，可见)。" + Summary();
            _ctx.Status($"展点：{ents.Count} 个点已展绘到视图(带高程, 可选中/创建三角网)");
        }
        catch (Exception ex) { summaryText.Text = $"展绘异常：{ex.Message}"; }
    }

    /// <summary>按点的 XY 范围估一个可见的标记半长(世界单位)；范围未知时退 5。</summary>
    public static double ComputeMarkerSize(List<double> xyz)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i + 2 < xyz.Count; i += 3)
        {
            double x = xyz[i], y = xyz[i + 1];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        double ext = Math.Max(maxX - minX, maxY - minY);
        if (ext <= 0 || double.IsInfinity(ext) || double.IsNaN(ext)) return 5.0;
        return Math.Clamp(ext * 0.004, 1.0, 100.0);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── 极简 .xlsx 读取(纯 BCL：ZipArchive + XML)──────────────
    public static List<string> ReadXlsxRows(string path)
    {
        var rows = new List<string>();
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        var shared = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            using var s = ssEntry.Open();
            var sdoc = System.Xml.Linq.XDocument.Load(s);
            var sns = sdoc.Root!.Name.Namespace;
            foreach (var si in sdoc.Root.Elements(sns + "si")) shared.Add(string.Concat(si.Descendants(sns + "t").Select(t => t.Value)));
        }
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
                 ?? zip.Entries.FirstOrDefault(en => en.FullName.StartsWith("xl/worksheets/") && en.FullName.EndsWith(".xml"));
        if (sheet == null) return rows;
        using var ws = sheet.Open();
        var wdoc = System.Xml.Linq.XDocument.Load(ws);
        var wns = wdoc.Root!.Name.Namespace;
        var sheetData = wdoc.Root.Element(wns + "sheetData");
        if (sheetData == null) return rows;
        foreach (var row in sheetData.Elements(wns + "row"))
        {
            var vals = new List<string>();
            foreach (var c in row.Elements(wns + "c"))
            {
                string t = (string?)c.Attribute("t") ?? "";
                var v = c.Element(wns + "v");
                string cell;
                if (t == "s") cell = (v != null && int.TryParse(v.Value, out int si) && si >= 0 && si < shared.Count) ? shared[si] : "";
                else if (t == "inlineStr") cell = string.Concat(c.Descendants(wns + "t").Select(x => x.Value));
                else cell = v?.Value ?? "";
                vals.Add(cell);
            }
            rows.Add(string.Join(",", vals));
        }
        return rows;
    }

    public sealed class PointRow : INotifyPropertyChanged
    {
        private int _index; private string _x = "", _y = "", _z = "", _status = "";
        public int Index { get => _index; set { if (_index != value) { _index = value; Raise(nameof(Index)); } } }
        public string X { get => _x; set { if (_x != value) { _x = value; Raise(nameof(X)); } } }
        public string Y { get => _y; set { if (_y != value) { _y = value; Raise(nameof(Y)); } } }
        public string Z { get => _z; set { if (_z != value) { _z = value; Raise(nameof(Z)); } } }
        public string Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); } } }
        public bool Valid;
        public double Xv, Yv, Zv;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
