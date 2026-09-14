using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 数据库浏览面板（忠实原 <c>SqlLib.UI.SqlBrowserWindow</c>）：
/// 左边表树（按模块分组、带行数），右边四页签 —— 表结构 / 数据预览 / SQL 控制台 / 迁移历史。
///
/// 查询这一层 Kylin 早就有了（<c>GeoDataQueries.ListTables / TableColumns / IsReadOnlySql /
/// RunSelectCsv</c>，`数据字典` 命令走的就是它），**缺的一直是这扇窗** —— 要看一眼某张表里有什么，
/// 此前只能导一份 CSV 出去再拿别的软件打开。
///
/// 与原版口径一致处：
///   · <b>仅查询模式默认勾上</b>，首关键字只放 SELECT / WITH / PRAGMA / EXPLAIN；
///   · 数据预览 <b>LIMIT 1000</b> 并如实标注"上限"；
///   · 执行后报<b>行数与耗时</b>。
///
/// 登记的差异：
///   · 原版的 VACUUM 按钮针对 SQLite 单文件库；Kylin 连的是 openGauss/DM 这类服务端库，
///     VACUUM 是**服务端维护动作**（需相应权限、可能长时间锁表），不该由图形客户端一个按钮触发，
///     故**不做**；要整理表空间请由 DBA 在服务端执行。
///   · 原版迁移历史有 模块/版本/脚本/校验和 四列；Kylin 的 <c>_schema_migration</c> 只有
///     <c>version</c> + <c>applied_at</c> 两列（见 <c>GeoDbDialect.EnsureSchemaMigrationTable</c>），
///     故只显示这两列 —— 不为了凑格子编出模块名和校验和。
///   · 原版"按模块分组"是拿表名去迁移脚本名里模糊查；Kylin 的迁移记录只有版本号、查不出归属，
///     故按**表名前缀**归组（equipment_* / coal_* / borehole_* …），组名即前缀，一目了然且不会张冠李戴。
/// </summary>
internal sealed class SqlBrowserWindow : Window
{
    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;
    private readonly Func<string, string, string, System.Threading.Tasks.Task<string?>> _save;   // (标题, 建议名, 内容) → 落盘名

    private readonly TreeView _tree = new();
    private readonly DataGrid _colsGrid = NewGrid();
    private readonly DataGrid _previewGrid = NewGrid();
    private readonly DataGrid _sqlGrid = NewGrid();
    private readonly DataGrid _migGrid = NewGrid();

    private readonly TextBox _whereBox = new() { Watermark = "WHERE 子句（可空），如  year = 2025", Width = 380 };
    private readonly TextBlock _previewCount = new() { Text = "", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _sqlBox = new()
    {
        AcceptsReturn = true, Height = 110, TextWrapping = TextWrapping.Wrap,
        Watermark = "SELECT * FROM equipment LIMIT 20",
    };
    private readonly CheckBox _readonly = new() { Content = "仅查询模式(禁止写入)", IsChecked = true };
    private readonly TextBlock _sqlStatus = new() { Text = "", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Text = "就绪", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _dbPath = new() { Text = "", Foreground = Brush.Parse("#666"), FontSize = 11, TextWrapping = TextWrapping.Wrap };

    private string? _selectedTable;

    private static DataGrid NewGrid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns = false,
    };

    public SqlBrowserWindow(Func<DbConnection?> conn, Action<string> echo,
                            Func<string, string, string, System.Threading.Tasks.Task<string?>> save)
    {
        _conn = conn; _echo = echo; _save = save;

        Title = "数据库浏览";
        Width = 1020; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildUi();
        RefreshAll();
    }

    private void BuildUi()
    {
        var btnRefresh = new Button { Content = "刷新", Padding = new Thickness(12, 3) };
        var btnDict = new Button { Content = "导出数据字典", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        btnRefresh.Click += (_, _) => RefreshAll();
        btnDict.Click += (_, _) => _ = ExportDictionaryAsync();

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6),
            Children = { btnRefresh, btnDict },
        };

        _tree.SelectionChanged += (_, _) => OnTableSelected();

        // ── 表结构 ──
        _colsGrid.Columns.Add(TextCol("#", nameof(ColRow.Ordinal), 44));
        _colsGrid.Columns.Add(TextCol("列名", nameof(ColRow.Name), 200));
        _colsGrid.Columns.Add(TextCol("类型", nameof(ColRow.Type), 160));
        // 72 而不是 60: 两个汉字的表头在 60px 上仍被截成「非 / 主」(表头除文字外还留了排序标记的位置)
        _colsGrid.Columns.Add(TextCol("非空", nameof(ColRow.NotNull), 72));
        _colsGrid.Columns.Add(TextCol("主键", nameof(ColRow.Pk), 72));

        // ── 数据预览 ──
        var btnPreview = new Button { Content = "查询", Padding = new Thickness(12, 3), Margin = new Thickness(8, 0, 0, 0) };
        btnPreview.Click += (_, _) => Preview(_selectedTable, _whereBox.Text);
        var previewBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6),
            Children = { _whereBox, btnPreview, new TextBlock { Text = "  ", Width = 8 }, _previewCount },
        };
        var previewPanel = new DockPanel();
        DockPanel.SetDock(previewBar, Avalonia.Controls.Dock.Top);
        previewPanel.Children.Add(previewBar);
        previewPanel.Children.Add(_previewGrid);

        // ── SQL 控制台 ──
        var btnExec = new Button { Content = "执行 (F5)", Padding = new Thickness(12, 3) };
        btnExec.Click += (_, _) => Execute();
        _sqlBox.KeyDown += (_, e) => { if (e.Key == Key.F5) { Execute(); e.Handled = true; } };
        var sqlBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 6, 0, 6),
            Children = { btnExec, _readonly, _sqlStatus },
        };
        var sqlPanel = new DockPanel();
        DockPanel.SetDock(_sqlBox, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(sqlBar, Avalonia.Controls.Dock.Top);
        sqlPanel.Children.Add(_sqlBox);
        sqlPanel.Children.Add(sqlBar);
        sqlPanel.Children.Add(_sqlGrid);

        // ── 迁移历史 ──
        _migGrid.Columns.Add(TextCol("版本", nameof(MigRow.Version), 240));
        _migGrid.Columns.Add(TextCol("应用时间", nameof(MigRow.AppliedAt), 240));

        // Fluent 的 TabItem 默认 24px 字, 四个页签能占掉半扇窗 —— 收到 13px(同 GeoDb 各页面的做法)
        var tabs = new TabControl
        {
            Styles =
            {
                new Style(x => x.OfType<TabItem>())
                {
                    Setters =
                    {
                        new Setter(TabItem.FontSizeProperty, 13.0),
                        new Setter(TabItem.MinHeightProperty, 30.0),
                        new Setter(TabItem.PaddingProperty, new Thickness(10, 4)),
                    },
                },
            },
            Items =
            {
                new TabItem { Header = "表结构", Content = _colsGrid },
                new TabItem { Header = "数据预览", Content = previewPanel },
                new TabItem { Header = "SQL 控制台", Content = sqlPanel },
                new TabItem { Header = "迁移历史", Content = _migGrid },
            },
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("300,6,*") };
        var left = new DockPanel();
        DockPanel.SetDock(_dbPath, Avalonia.Controls.Dock.Bottom);
        left.Children.Add(_dbPath);
        left.Children.Add(_tree);
        Grid.SetColumn(left, 0); split.Children.Add(left);
        Grid.SetColumn(tabs, 2); split.Children.Add(tabs);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        var statusBar = new Border
        {
            Height = 26, Margin = new Thickness(0, 6, 0, 0),
            Background = Brush.Parse("#F3F3F3"),
            Child = _status,   // 直接放这个 TextBlock, 不必再绕一层绑定
        };
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(statusBar);
        root.Children.Add(split);
        Content = root;
    }

    private static DataGridTextColumn TextCol(string header, string path, double w) => new()
    {
        Header = new TextBlock { Text = header, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap },
        Width = new DataGridLength(w),
        Binding = new Binding(path),
    };

    private sealed record ColRow(int Ordinal, string Name, string Type, string NotNull, string Pk);
    private sealed record MigRow(string Version, string AppliedAt);

    // ── 装载 ────────────────────────────────────────────────
    internal void RefreshAll()
    {
        var conn = _conn();
        if (conn == null)
        {
            _status.Text = "没有数据库连接 —— 先用「数据库连接」连上再打开本窗。";
            _tree.ItemsSource = null;
            return;
        }
        _dbPath.Text = SafeDbLabel(conn);
        try
        {
            BuildTableTree(conn);
            LoadMigrationHistory(conn);
            _status.Text = $"已加载 · {_dbPath.Text}";
        }
        catch (Exception ex) { _status.Text = "加载失败：" + ex.Message; }
    }

    /// <summary>连接的显示串：**只取库名/主机，绝不显示连接串**（里面常带口令）。</summary>
    private static string SafeDbLabel(DbConnection conn)
    {
        try
        {
            string db = conn.Database ?? "";
            string src = conn.DataSource ?? "";
            return string.IsNullOrEmpty(db) && string.IsNullOrEmpty(src) ? conn.GetType().Name : $"{src}/{db}";
        }
        catch { return conn.GetType().Name; }
    }

    /// <summary>表名前缀 → 组名（原版按迁移脚本猜归属，Kylin 迁移记录只有版本号，猜不出，故按前缀）。</summary>
    internal static string GroupOf(string table)
    {
        if (string.IsNullOrEmpty(table)) return "其它";
        if (table.StartsWith("_")) return "系统";
        int i = table.IndexOf('_');
        return i > 0 ? table.Substring(0, i) : table;
    }

    private void BuildTableTree(DbConnection conn)
    {
        var tables = GeoDataQueries.ListTables(conn);
        var nodes = new List<TreeViewItem>();
        foreach (var g in tables.GroupBy(GroupOf).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var group = new TreeViewItem { Header = $"{g.Key} ({g.Count()})", IsExpanded = true };
            var leaves = new List<TreeViewItem>();
            foreach (var t in g.OrderBy(x => x, StringComparer.Ordinal))
            {
                long n = GeoDataQueries.TableRowCount(conn, t);
                leaves.Add(new TreeViewItem { Header = n < 0 ? t : $"{t}  ({n})", Tag = t });
            }
            group.ItemsSource = leaves;
            nodes.Add(group);
        }
        _tree.ItemsSource = nodes;
        _status.Text = $"{tables.Count} 张表 · {nodes.Count} 组";
    }

    private void LoadMigrationHistory(DbConnection conn)
    {
        var r = GeoDataQueries.RunSelect(conn, "SELECT version, applied_at FROM _schema_migration ORDER BY version", 5000);
        _migGrid.Columns.Clear();
        _migGrid.Columns.Add(TextCol("版本", nameof(MigRow.Version), 240));
        _migGrid.Columns.Add(TextCol("应用时间", nameof(MigRow.AppliedAt), 240));
        _migGrid.ItemsSource = r.Ok
            ? r.Rows.Select(x => new MigRow(x.Length > 0 ? x[0] : "", x.Length > 1 ? x[1] : "")).ToList()
            : new List<MigRow>();
    }

    private void OnTableSelected()
    {
        if (_tree.SelectedItem is not TreeViewItem tv || tv.Tag is not string table) return;
        _selectedTable = table;
        var conn = _conn();
        if (conn == null) return;
        try
        {
            var cols = GeoDbDialect.For(conn).TableColumns(conn, table);
            _colsGrid.ItemsSource = cols
                .Select((c, i) => new ColRow(i + 1, c.Name, c.Type, c.NotNull ? "✓" : "", c.Pk ? "✓" : ""))
                .ToList();
            _status.Text = $"选中表：{table} · {cols.Count} 列";
        }
        catch (Exception ex) { _status.Text = "加载列失败：" + ex.Message; }
        Preview(table, null);
    }

    // ── 数据预览 / SQL ──────────────────────────────────────
    private const int PreviewLimit = 1000;

    internal void Preview(string? table, string? where)
    {
        if (string.IsNullOrEmpty(table)) { _previewCount.Text = "请先在左侧选中一张表"; return; }
        var conn = _conn();
        if (conn == null) { _previewCount.Text = "没有数据库连接"; return; }
        string sql = $"SELECT * FROM {GeoDataQueries.QuoteIdent(table)}";
        if (!string.IsNullOrWhiteSpace(where)) sql += " WHERE " + where.Trim();
        sql += $" LIMIT {PreviewLimit}";
        var r = GeoDataQueries.RunSelect(conn, sql, PreviewLimit);
        Bind(_previewGrid, r);
        _previewCount.Text = r.Ok
            ? $"显示 {r.Rows.Count} 行（上限 {PreviewLimit}）"
            : "查询失败：" + r.Error;
    }

    internal void Execute()
    {
        string sql = (_sqlBox.Text ?? "").Trim();
        if (sql.Length == 0) { _sqlStatus.Text = "SQL 不能为空"; return; }
        var conn = _conn();
        if (conn == null) { _sqlStatus.Text = "没有数据库连接"; return; }

        if (_readonly.IsChecked == true && !GeoDataQueries.IsReadOnlySql(sql))
        {
            _sqlStatus.Text = "仅查询模式：只允许 SELECT / WITH / PRAGMA / EXPLAIN";
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = GeoDataQueries.RunSelect(conn, sql, 5000);
        sw.Stop();
        Bind(_sqlGrid, r);
        _sqlStatus.Text = r.Ok
            ? $"查询成功 · {r.Rows.Count} 行{(r.Truncated ? "（已截断）" : "")} · {sw.ElapsedMilliseconds}ms"
            : "执行失败：" + r.Error;
    }

    /// <summary>结果集 → DataGrid：列是动态的，故每次重建列。</summary>
    private static void Bind(DataGrid grid, GeoDataQueries.SelectResult r)
    {
        grid.ItemsSource = null;
        grid.Columns.Clear();
        if (!r.Ok || r.Columns.Count == 0) return;
        for (int i = 0; i < r.Columns.Count; i++)
        {
            int idx = i;   // 闭包捕获: 不取本地副本的话每列都会绑到最后一个下标
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = new TextBlock { Text = r.Columns[i], TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap },
                Width = new DataGridLength(150),
                Binding = new Binding($"[{idx}]"),
            });
        }
        grid.ItemsSource = r.Rows;
    }

    private async System.Threading.Tasks.Task ExportDictionaryAsync()
    {
        var conn = _conn();
        if (conn == null) { _status.Text = "没有数据库连接"; return; }
        try
        {
            string csv = GeoDataQueries.DataDictionaryCsv(conn);
            var name = await _save("数据字典", "data_dictionary.csv", csv);
            _status.Text = name == null ? "导出已取消" : "已导出：" + name;
            if (name != null) _echo("数据字典已导出：" + name);
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    // ── 自检钩子用 ──
    internal bool SelectTable(string table)
    {
        if (_tree.ItemsSource is not IEnumerable<TreeViewItem> groups) return false;
        foreach (var g in groups)
            if (g.ItemsSource is IEnumerable<TreeViewItem> leaves)
                foreach (var leaf in leaves)
                    if (leaf.Tag as string == table) { _tree.SelectedItem = leaf; OnTableSelected(); return true; }
        return false;
    }
    internal void SetSql(string sql) => _sqlBox.Text = sql;
    internal void SetReadOnly(bool on) => _readonly.IsChecked = on;
    internal string SqlStatusText => _sqlStatus.Text ?? "";
    internal string StatusText => _status.Text ?? "";
    internal string PreviewCountText => _previewCount.Text ?? "";
    internal int ColumnRowCount => (_colsGrid.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
}
