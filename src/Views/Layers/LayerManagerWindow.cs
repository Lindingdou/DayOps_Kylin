using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.Modeling;

namespace PitMine3D.Kylin.Views.Layers;

/// <summary>
/// 图层特性管理器（忠实原 <c>PitMineApp.LayerManager.Views.LayerManagerWindow</c>）。
///
/// 在此之前，Kylin 的「图层特性管理器」按钮只是把当前层**轮转**到下一层并在状态栏回一句话
/// （<c>LayerTable.CycleCurrent</c>）—— 图层多起来就得连点，而且颜色/线宽/透明度/说明这些列
/// 根本没有入口。原版是一张表：一行一层，工具栏 新建/设为当前/将对象置为当前图层/批量冻结解冻/
/// 批量锁定解锁/批量删除/刷新，双击一行=设为当前，点颜色格=改色，底部状态栏报结果。
///
/// 与原版口径一致处：
///   · <b>删除的两条禁令</b>：默认层 "0" 不删、当前层不删（原版逐条判并给原话）；批量删除时把这两类
///     先滤掉，滤空了就报「选中的图层不可删除（包含默认图层或当前图层）」。
///   · <b>「将对象置为当前图层」</b>是把**视口里选中的实体**移到**表里选中的那一层**（原版 tooltip 原话），
///     不是反过来 —— 名字容易读反，所以按钮上照抄原文案。
///   · <b>改一格即回写</b>：原版每个属性 setter 直接调 <c>PitMine_SetLayerProperty</c>，没有「确定」；
///     这里同样改完即落到 <see cref="LayerTable"/> 并重绘。
///
/// 登记的差异：原版颜色列点开的是 WinForms <c>ColorDialog</c>（取任意 RGB 再找最近 ACI 索引），
/// Kylin 的图层存的是 RGB 浮点、没有 ACI 索引列，故颜色格走既有的 <see cref="AciPalette"/> 标准色板
/// 菜单（与「特性」组颜色下拉同一套），选完直接落 RGB；透明度一列原版存 0..255 原始 alpha，
/// 这里与 Kylin 实体「透明度」特性统一成 0..90 百分比 —— 随层解析必须同单位才有意义。
/// </summary>
internal sealed class LayerManagerWindow : Window
{
    /// <summary>表格一行：图层的一份可观察投影，改完立刻回写到 <see cref="Layer"/> 本体。</summary>
    internal sealed class Row : INotifyPropertyChanged
    {
        private readonly Layer _l;
        private readonly Action _changed;
        private readonly Func<string, string, bool>? _rename;
        internal Layer Layer => _l;

        public Row(Layer l, Action changed, Func<string, string, bool>? rename = null)
        { _l = l; _changed = changed; _rename = rename; }

        public string Name
        {
            get => _l.Name;
            set
            {
                string oldName = _l.Name;
                string newName = value?.Trim() ?? "";
                if (_rename?.Invoke(oldName, newName) == true)
                {
                    OnPropertyChanged();
                    _changed();
                }
                else OnPropertyChanged();   // 编辑失败：把单元格刷回本体中的原名
            }
        }
        public string ColorText => AciPalette.DisplayName(_l.Cr, _l.Cg, _l.Cb);
        public IBrush ColorBrush => new SolidColorBrush(Color.FromRgb(
            (byte)Math.Clamp(_l.Cr * 255f, 0, 255), (byte)Math.Clamp(_l.Cg * 255f, 0, 255), (byte)Math.Clamp(_l.Cb * 255f, 0, 255)));

        public string LineWeightText
        {
            get => LineWeightUtil.Display(_l.LineWeight);
            set
            {
                // 图层本身不能"随层"/"随块"（那两档是实体指向图层用的），解析出这两档就按"默认"收；
                // 解析不出来则原值不动，Bump() 把编辑框刷回原显示，不让表里留一个假值。
                if (LineWeightUtil.TryParse(value ?? "", out var lw))
                    _l.LineWeight = lw < 0 ? (short)-3 : lw;
                Bump();
            }
        }

        public string TransparencyText
        {
            get => _l.Transparency <= 0 ? "不透明" : _l.Transparency + "%";
            set
            {
                var t = (value ?? "").Trim().TrimEnd('%', ' ');
                if (t is "不透明" or "") { _l.Transparency = 0; Bump(); return; }
                if (short.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    _l.Transparency = Math.Clamp(v, (short)0, (short)90);
                Bump();
            }
        }

        public bool Visible { get => _l.Visible; set { _l.Visible = value; Bump(); } }
        public bool Frozen { get => _l.Frozen; set { _l.Frozen = value; Bump(); } }
        public bool Locked { get => _l.Locked; set { _l.Locked = value; Bump(); } }
        public bool Plottable { get => _l.Plottable; set { _l.Plottable = value; Bump(); } }
        public string Description { get => _l.Description; set { _l.Description = value ?? ""; Bump(); } }

        private bool _isCurrent;
        public bool IsCurrent { get => _isCurrent; set { _isCurrent = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentMark)); } }
        /// <summary>当前层标记（原版是整行加粗 + 底色，这里另出一列 ✓ —— DataGrid 行样式触发器在 Avalonia 里要整套模板）。</summary>
        public string CurrentMark => _isCurrent ? "✓" : "";

        private void Bump()
        {
            foreach (var n in new[] { nameof(ColorText), nameof(ColorBrush), nameof(LineWeightText), nameof(TransparencyText),
                                      nameof(Visible), nameof(Frozen), nameof(Locked), nameof(Plottable), nameof(Description) })
                OnPropertyChanged(n);
            _changed();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string n = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly LayerTable _layers;
    private readonly Action _refresh;                 // 图层状态变了 → 重绘视口 + 刷主窗图层面板
    private readonly Func<int> _selectedEntityCount;  // 视口当前选中实体数
    private readonly Func<string, int> _assignSelected;   // 把选中实体移到某层, 返回成功数
    private readonly Func<string, string, bool> _renameLayer;  // 图层改名 + 其上实体随迁
    private readonly Func<string, bool> _removeLayer;     // 删层(含把该层实体归并到 "0"), 返回成功

    private readonly ObservableCollection<Row> _rows = new();
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = false,
        SelectionMode = DataGridSelectionMode.Extended,
        CanUserSortColumns = false,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        HeadersVisibility = DataGridHeadersVisibility.Column,
    };
    private readonly TextBlock _status = new() { Text = "就绪", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

    public LayerManagerWindow(LayerTable layers, Action refresh, Func<int> selectedEntityCount,
                              Func<string, int> assignSelected, Func<string, string, bool> renameLayer,
                              Func<string, bool> removeLayer)
    {
        _layers = layers; _refresh = refresh;
        _selectedEntityCount = selectedEntityCount; _assignSelected = assignSelected;
        _renameLayer = renameLayer; _removeLayer = removeLayer;

        Title = "图层特性管理器";
        Width = 940; Height = 500;   // 够工具栏九个按钮排一行 + 九列不挤(中文表头两字要 64px)
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildColumns();
        _grid.ItemsSource = _rows;
        _grid.DoubleTapped += (_, _) => SetCurrent();

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        void Btn(string text, Action act, string? tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 4) };
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { try { act(); } catch (Exception ex) { _status.Text = $"操作失败：{ex.Message}"; } };
            bar.Children.Add(b);
        }
        Btn("新建图层", AddLayer);
        Btn("重命名图层", RenameSelectedLayer);
        Btn("设为当前", SetCurrent);
        Btn("将对象置为当前图层", AssignObjects, "将当前选中的实体移动到表中选中的图层");
        Btn("批量冻结", () => Batch("frozen", true));
        Btn("批量解冻", () => Batch("frozen", false));
        Btn("批量锁定", () => Batch("locked", true));
        Btn("批量解锁", () => Batch("locked", false));
        Btn("批量删除", BatchDelete);
        Btn("刷新", () => { Reload(); _status.Text = "已刷新"; });

        var root = new Grid { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(bar, 0); root.Children.Add(bar);
        Grid.SetRow(_grid, 1); root.Children.Add(_grid);
        var statusBar = new Border
        {
            Height = 26, Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)),
            Child = _status,
        };
        Grid.SetRow(statusBar, 2); root.Children.Add(statusBar);
        Content = root;

        Reload();
    }

    private void BuildColumns()
    {
        // 表头一律给 TextBlock 而不是裸字符串：DataGrid 对字符串表头会按内容区裁字，
        // 「可见/冻结/锁定/打印」在任何宽度下都被截掉半个字（列再宽也一样，不是宽度问题）。
        static TextBlock Head(string t) => new()
        {
            Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
        };
        DataGridTextColumn Text(string header, string path, double w, bool readOnly = false) => new()
        {
            Header = Head(header), Width = new DataGridLength(w),
            Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay },
            IsReadOnly = readOnly,
        };
        DataGridCheckBoxColumn Check(string header, string path, double w) => new()
        {
            Header = Head(header), Width = new DataGridLength(w),
            Binding = new Binding(path) { Mode = BindingMode.TwoWay },
        };

        _grid.Columns.Add(Text("当前", nameof(Row.CurrentMark), 72, readOnly: true));
        _grid.Columns.Add(Text("名称", nameof(Row.Name), 140));

        // 颜色列：色块 + 色名，点一下弹标准色板（同「特性」组颜色下拉那套）
        var colorCol = new DataGridTemplateColumn { Header = Head("颜色"), Width = new DataGridLength(120) };
        colorCol.CellTemplate = new FuncDataTemplate<Row>((row, _) =>
        {
            var sw = new Border { Width = 20, Height = 14, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            var lbl = new TextBlock { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            sw.Bind(Border.BackgroundProperty, new Binding(nameof(Row.ColorBrush)));
            lbl.Bind(TextBlock.TextProperty, new Binding(nameof(Row.ColorText)));
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(4, 0, 0, 0) };
            sp.Children.Add(sw); sp.Children.Add(lbl);
            sp.PointerPressed += (_, e) => { e.Handled = true; if (row != null) PickColor(row, sp); };
            return sp;
        }, supportsRecycling: true);
        _grid.Columns.Add(colorCol);

        _grid.Columns.Add(Text("线宽", nameof(Row.LineWeightText), 90));
        _grid.Columns.Add(Text("透明度", nameof(Row.TransparencyText), 80));
        // 72 而不是 56: 两个汉字的表头在 56px 上被截成「可 / 冻 / 锁 / 打」, 64px 上「可见」仍差半字 ——
        // DataGrid 表头除文字外还留了排序标记的位置(即便关了排序)。见 [[datagrid-star-column-fit]]
        _grid.Columns.Add(Check("可见", nameof(Row.Visible), 72));
        _grid.Columns.Add(Check("冻结", nameof(Row.Frozen), 72));
        _grid.Columns.Add(Check("锁定", nameof(Row.Locked), 72));
        _grid.Columns.Add(Check("打印", nameof(Row.Plottable), 72));
        // 说明是唯一的星列：定宽列已经把前面占满，星列吃剩下的（定宽挤星列会把它挤没）
        _grid.Columns.Add(new DataGridTextColumn { Header = Head("说明"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding(nameof(Row.Description)) { Mode = BindingMode.TwoWay } });
    }

    /// <summary>重建行（图层增删/改名后调），并把当前层标记与选中行还原。</summary>
    internal void Reload()
    {
        var keep = (_grid.SelectedItem as Row)?.Name;
        _rows.Clear();
        foreach (var l in _layers.Layers)
            _rows.Add(new Row(l, () => { _refresh(); }, TryRenameLayer) { IsCurrent = ReferenceEquals(l, _layers.Current) });
        if (keep != null) _grid.SelectedItem = _rows.FirstOrDefault(r => r.Name == keep);
        _status.Text = $"共 {_rows.Count} 个图层 · 当前「{_layers.Current.Name}」";
    }

    private List<Row> SelectedRows() => _grid.SelectedItems?.Cast<Row>().ToList() ?? new List<Row>();

    private async void AddLayer()
    {
        var values = await PromptDialog.AskAsync(this, "新建图层", new[]
        {
            new PromptDialog.Field("name", "图层名称", _layers.NextAvailableName(), Numeric: false)
        }, "输入唯一的图层名称。");
        if (values == null) { _status.Text = "新建图层：已取消"; return; }
        string name = values.S("name").Trim();
        if (!_layers.TryNew(name, out var l) || l == null)
        {
            _status.Text = name.Length == 0 ? "新建图层失败：名称不能为空"
                : $"新建图层失败：图层「{name}」已存在或名称无效";
            return;
        }
        Reload();
        _grid.SelectedItem = _rows.FirstOrDefault(r => r.Name == l.Name);
        _refresh();
        _status.Text = $"已新建图层「{l.Name}」并置为当前 · 共 {_rows.Count} 个图层";
    }

    private async void RenameSelectedLayer()
    {
        if (_grid.SelectedItem is not Row row) { _status.Text = "请先选择要重命名的图层"; return; }
        if (row.Name == "0") { _status.Text = "默认图层「0」不可改名"; return; }
        var values = await PromptDialog.AskAsync(this, "重命名图层", new[]
        {
            new PromptDialog.Field("name", "新名称", row.Name, Numeric: false)
        }, "名称列也可以直接双击编辑。");
        if (values == null) { _status.Text = "重命名图层：已取消"; return; }
        row.Name = values.S("name");
    }

    private bool TryRenameLayer(string oldName, string newName)
    {
        newName = newName.Trim();
        if (oldName == "0") { _status.Text = "默认图层「0」不可改名"; return false; }
        if (newName.Length == 0 || newName == oldName)
        { _status.Text = "重命名失败：新名为空或与原名相同"; return false; }
        if (_layers.Get(newName) != null)
        { _status.Text = $"重命名失败：图层「{newName}」已存在"; return false; }
        if (!_renameLayer(oldName, newName))
        { _status.Text = $"重命名图层「{oldName}」失败"; return false; }
        _status.Text = $"图层「{oldName}」已重命名为「{newName}」";
        return true;
    }

    private void SetCurrent()
    {
        if (_grid.SelectedItem is not Row row) { _status.Text = "请先选择一行"; return; }
        if (!_layers.SetCurrent(row.Name)) { _status.Text = "设置当前图层失败"; return; }
        foreach (var r in _rows) r.IsCurrent = r.Name == row.Name;
        _refresh();
        _status.Text = $"当前图层: {row.Name}";
    }

    private void AssignObjects()
    {
        if (_grid.SelectedItem is not Row row) { _status.Text = "请先选择一个目标图层"; return; }
        int sel = _selectedEntityCount();
        if (sel == 0) { _status.Text = "没有选中的实体（先在视口里选中要移动的对象）"; return; }
        int ok = _assignSelected(row.Name);
        _refresh();
        _status.Text = $"已将 {ok}/{sel} 个实体移动到图层「{row.Name}」";
    }

    private void Batch(string prop, bool on)
    {
        var sel = SelectedRows();
        if (sel.Count == 0) { _status.Text = "请先选中至少一个图层"; return; }
        foreach (var r in sel)
        {
            if (prop == "frozen") r.Frozen = on;
            else r.Locked = on;
        }
        _refresh();
        _status.Text = prop == "frozen"
            ? $"已{(on ? "冻结" : "解冻")} {sel.Count} 个图层"
            : $"已{(on ? "锁定" : "解锁")} {sel.Count} 个图层";
    }

    private void BatchDelete()
    {
        var sel = SelectedRows();
        if (sel.Count == 0) { _status.Text = "请先选中至少一个图层"; return; }
        // 原版同款两条禁令：默认层 "0" 与当前层滤掉
        var del = sel.Where(r => r.Name != "0" && !r.IsCurrent).ToList();
        if (del.Count == 0) { _status.Text = "选中的图层不可删除（包含默认图层或当前图层）"; return; }
        int ok = del.Count(r => _removeLayer(r.Name));
        Reload();
        _refresh();
        _status.Text = $"已删除 {ok}/{del.Count} 个图层（其上实体已移到「0」层）";
    }

    private void PickColor(Row row, Control anchor)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        foreach (var (_, name, r8, g8, b8) in AciPalette.Entries)
        {
            var swatch = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
                Background = new SolidColorBrush(Color.FromRgb(r8, g8, b8)),
            };
            var mi = new MenuItem { Header = name, Icon = swatch };
            var (cr, cg, cb) = (r8 / 255f, g8 / 255f, b8 / 255f);
            mi.Click += (_, _) =>
            {
                row.Layer.Cr = cr; row.Layer.Cg = cg; row.Layer.Cb = cb;
                Reload();
                _refresh();
                _status.Text = $"图层「{row.Name}」颜色已改为 {name}";
            };
            items.Add(mi);
        }
        menu.ItemsSource = items;
        menu.Open(anchor);
    }

    /// <summary>自检钩子用：按名字选中一行（脚本点不了 DataGrid）。</summary>
    internal bool SelectRow(string name)
    {
        var r = _rows.FirstOrDefault(x => x.Name == name);
        if (r == null) return false;
        _grid.SelectedItem = r;
        return true;
    }
}
