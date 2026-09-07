using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 设备信息管理(忠实原 EquipmentManagementWindow): 左 在籍设备列表(类别筛选/增删/保存),
/// 中 三维展示 = PNG 转台序列帧播放器(Assets/Models3D/{型号|类别}/frame_*.png; 拖拽/滚轮/进度条/自转),
/// 右 参数面板(可编辑, 保存写 equipment 表) + 工艺作业联动(working_face)。
/// 原 HelixToolkit 程序化几何回退在 Kylin 无对应库 → 以「无模型」占位。
/// </summary>
public partial class EquipmentManagementWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<EquipmentLedgerItem> _all = new();
    private string _currentFilter = "All";
    private bool _isDirty, _forceClose, _ready;

    // ─── PNG 序列帧播放器状态 ───
    private readonly List<Bitmap> _frames = new();
    private int _currentFrame;
    private bool _isDragging;
    private double _dragStartX;
    private int _dragStartFrame;
    private bool _suppressSliderEvent;
    private readonly DispatcherTimer _autoplayTimer;
    private readonly DispatcherTimer _resumeTimer;
    private bool _autoplayRunning;
    /// <summary>自动播放帧率(ms 间隔)。50ms ≈ 20fps,60 帧 = 3 秒一圈。</summary>
    private const int AutoplayIntervalMs = 50;
    /// <summary>用户操作完成后恢复自转的延时(ms)。</summary>
    private const int ResumeDelayMs = 1500;

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentManagementWindow() { _ctx = null!; _autoplayTimer = _resumeTimer = new DispatcherTimer(); InitializeComponent(); }

    public EquipmentManagementWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _autoplayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoplayIntervalMs) };
        _autoplayTimer.Tick += (_, _) => { if (_autoplayRunning && _frames.Count > 0) SetCurrentFrame(_currentFrame + 1); };
        _resumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ResumeDelayMs) };
        _resumeTimer.Tick += (_, _) => { _resumeTimer.Stop(); StartAutoplay(); };

        LoadData();
        storageHint.Text = "数据文件: pmgeo.db (equipment 表)";
        _all.CollectionChanged += OnCollectionChanged;
        foreach (var eq in _all) eq.PropertyChanged += OnEquipmentChanged;
        _ready = true;
        categoryFilter.SelectedIndex = 0;
        if (equipmentList.ItemCount > 0) equipmentList.SelectedIndex = 0;
        else OnEquipmentSelectionChanged(null, null);
        Closed += (_, _) => { StopAutoplay(); CancelResumeTimer(); };
    }

    private void LoadData()
    {
        _all.Clear();
        foreach (var eq in EqLoadLedger(_ctx.Conn)) _all.Add(eq);
        // 状态下拉: 原 4 项 + 库中实际值(避免绑定丢值)
        var statuses = new List<string> { "在用", "封存", "大修", "退役" };
        foreach (var s in _all.Select(e => e.Status).Where(s => !string.IsNullOrEmpty(s)).Distinct()) if (!statuses.Contains(s)) statuses.Add(s);
        statusCombo.ItemsSource = statuses;
        _isDirty = false;
        RefreshList();
        UpdateStatus();
    }

    private void OnCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (EquipmentLedgerItem eq in e.NewItems) eq.PropertyChanged += OnEquipmentChanged;
        if (e.OldItems != null) foreach (EquipmentLedgerItem eq in e.OldItems) eq.PropertyChanged -= OnEquipmentChanged;
        MarkDirty();
        RefreshList();
    }

    private void MarkDirty() { _isDirty = true; UpdateStatus(); }

    private List<EquipmentLedgerItem> Visible() => _all.Where(eq => _currentFilter == "All" || string.Equals(eq.Category, _currentFilter, StringComparison.OrdinalIgnoreCase)).ToList();

    private void RefreshList()
    {
        var sel = equipmentList.SelectedItem;
        equipmentList.ItemsSource = Visible();
        if (sel != null && Visible().Contains(sel)) equipmentList.SelectedItem = sel;
    }

    private void UpdateStatus()
        => statusText.Text = $"共 {_all.Count} 台在籍 · 当前显示 {Visible().Count} 台" + (_isDirty ? " · 有未保存修改" : "");

    private void OnCategoryFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (categoryFilter.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Tag?.ToString() ?? "All";
            RefreshList();
            UpdateStatus();
        }
    }

    private void OnEquipmentSelectionChanged(object? sender, SelectionChangedEventArgs? e)
    {
        var selected = equipmentList.SelectedItem as EquipmentLedgerItem;
        parameterPanel.DataContext = selected;
        parameterPanel.IsEnabled = selected != null;
        RefreshViewport(selected);
        viewportTitle.Text = selected != null ? $"三维展示 — {selected.Id} · {selected.Model}" : "三维展示";
        titleHint.Text = selected != null ? $"当前查看:{selected.CategoryDisplay}" : "";
        UpdateProcessLinkPanel(selected);
    }

    /// <summary>联动:从 working_face 表查该设备负责的工作面,刷新右侧"工艺作业"区块。</summary>
    private void UpdateProcessLinkPanel(EquipmentLedgerItem? selected)
    {
        void Clear(string head) { faceCodeText.Text = head; benchHeightText.Text = ""; platformWidthText.Text = ""; materialText.Text = ""; advanceRateText.Text = ""; processAdapText.Text = ""; }
        if (selected == null) { Clear("未选中设备"); return; }
        try
        {
            var face = EqActiveWorkingFace(_ctx.Conn, selected.Id);
            if (face == null) { Clear("❍ 未关联工作面"); return; }
            faceCodeText.Text = $"⛰ {face.FaceCode}  ·  平盘 {face.LocationCode}";
            benchHeightText.Text = $"台阶高度  {face.BenchHeightM:F1} m";
            platformWidthText.Text = face.WorkingPlatformWidthM.HasValue ? $"平台宽度  {face.WorkingPlatformWidthM.Value:F0} m" : "平台宽度  —";
            materialText.Text = $"物料类型  {face.Material ?? "—"} ({face.RockHardness ?? "—"})";
            advanceRateText.Text = face.AdvanceRateMPerMonth.HasValue ? $"月推进度  {face.AdvanceRateMPerMonth.Value:F0} m" : "月推进度  —";
            var (text, color) = EqEvaluateCompatibility(selected.Model, selected.Category, face.BenchHeightM);
            processAdapText.Text = text;
            processAdapText.Foreground = new SolidColorBrush(Color.Parse(color));
        }
        catch (Exception ex) { faceCodeText.Text = "⚠ 联动查询失败"; processAdapText.Text = ex.Message; }
    }

    private void OnEquipmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        if (e.PropertyName == nameof(EquipmentLedgerItem.Category) && ReferenceEquals(sender, equipmentList.SelectedItem)) RefreshViewport(sender as EquipmentLedgerItem);
    }

    // ─── 三维展示: 序列帧 ↔ 占位 ───
    private void RefreshViewport(EquipmentLedgerItem? eq)
    {
        StopAutoplay();
        if (eq == null) { ShowFrameViewer(false); return; }
        if (TryLoadFrameSequence(eq)) { ShowFrameViewer(true); StartAutoplay(); return; }
        ShowFrameViewer(false);
        fallbackText.Text = "无模型";
    }

    private void ShowFrameViewer(bool show)
    {
        frameViewerHost.IsVisible = show;
        fallbackHost.IsVisible = !show;
        // 序列帧是离线预渲染的水平环绕转台, 无真实相机 → 相机按钮整排隐藏(原窗口同此)
        cameraToolbar.IsVisible = !show;
        interactionHint.Text = show ? "拖拽旋转 · 滚轮逐帧 · 底部进度条/暂停" : "拖拽左键旋转 · 滚轮缩放 · 拖拽右键平移";
    }

    /// <summary>Assets/Models3D/{key}/frame_*.png, 按「型号 → 分类」两级回退(与原 EquipmentModel3DFactory 口径一致)。</summary>
    private bool TryLoadFrameSequence(EquipmentLedgerItem eq)
    {
        foreach (var b in _frames) b.Dispose();
        _frames.Clear();
        var files = EqResolveFrames(Path.Combine(AppContext.BaseDirectory, "Assets"), eq.Model, eq.Category);
        if (files == null) return false;
        foreach (var f in files)
        {
            try { _frames.Add(new Bitmap(f)); }
            catch { /* 个别图坏掉:跳过,只要总数 >= 2 仍可旋转 */ }
        }
        if (_frames.Count < 2) { _frames.Clear(); return false; }
        _suppressSliderEvent = true;
        frameSlider.Minimum = 0; frameSlider.Maximum = _frames.Count - 1; frameSlider.Value = 0;
        _suppressSliderEvent = false;
        _currentFrame = -1;
        SetCurrentFrame(0);
        return true;
    }

    private void SetCurrentFrame(int index)
    {
        if (_frames.Count == 0) return;
        int n = _frames.Count;
        index = ((index % n) + n) % n;
        if (index == _currentFrame && ReferenceEquals(frameImage.Source, _frames[index])) return;
        _currentFrame = index;
        frameImage.Source = _frames[index];
        frameIndexText.Text = $"{index:D3} / {n:D3}";
        if (!_suppressSliderEvent) { _suppressSliderEvent = true; frameSlider.Value = index; _suppressSliderEvent = false; }
    }

    // ─── 鼠标拖拽切换帧 ───
    private void OnFramePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_frames.Count == 0 || !e.GetCurrentPoint(frameImage).Properties.IsLeftButtonPressed) return;
        CancelResumeTimer(); StopAutoplay();
        _isDragging = true;
        _dragStartX = e.GetPosition(frameImage).X;
        _dragStartFrame = _currentFrame;
        e.Pointer.Capture(frameImage);
    }

    private void OnFramePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _frames.Count == 0) return;
        var dx = e.GetPosition(frameImage).X - _dragStartX;
        SetCurrentFrame(_dragStartFrame + (int)(dx / 8.0));   // 每 8 像素切 1 帧
    }

    private void OnFramePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        ScheduleAutoplayResume();
    }

    private void OnFramePointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_frames.Count == 0) return;
        CancelResumeTimer(); StopAutoplay();
        SetCurrentFrame(_currentFrame + (e.Delta.Y > 0 ? -1 : 1));
        ScheduleAutoplayResume();
        e.Handled = true;
    }

    private void OnFrameSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderEvent || _frames.Count == 0) return;
        CancelResumeTimer(); StopAutoplay();
        SetCurrentFrame((int)e.NewValue);
        ScheduleAutoplayResume();
    }

    // ─── 自动播放 ───
    private void OnAutoplayToggle(object? sender, RoutedEventArgs e)
    {
        CancelResumeTimer();
        if (!_autoplayRunning) StartAutoplay(); else StopAutoplay();
    }

    private void StartAutoplay()
    {
        if (_frames.Count == 0) return;
        if (!_autoplayRunning) { _autoplayRunning = true; _autoplayTimer.Start(); }
        autoplayButton.Content = "⏸ 暂停";
        autoplayButton.Background = new SolidColorBrush(Color.Parse("#D32F2F"));
    }

    private void StopAutoplay()
    {
        if (_autoplayRunning) { _autoplayRunning = false; _autoplayTimer.Stop(); }
        autoplayButton.Content = "▶ 自转";
        autoplayButton.Background = new SolidColorBrush(Color.Parse("#1976D2"));
    }

    private void ScheduleAutoplayResume() { _resumeTimer.Stop(); _resumeTimer.Start(); }
    private void CancelResumeTimer() => _resumeTimer.Stop();

    /// <summary>相机视角按钮(原仅对 HelixToolkit 三维视图有效; Kylin 未移植三维几何 → 状态栏说明)。</summary>
    private void OnCameraClick(object? sender, RoutedEventArgs e)
        => _ctx.Status("三维几何视图(HelixToolkit 程序化模型)未在 Kylin 移植, 视角按钮仅对该视图有效; 请为该型号/类别提供转台序列帧");

    // ─── 工具栏命令 ───
    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new EquipmentAddDialog(_ctx);
        if (await dialog.ShowDialog<bool>(this) && dialog.Result != null)
        {
            _all.Add(dialog.Result);
            if (_currentFilter != "All" && dialog.Result.Category != _currentFilter) categoryFilter.SelectedIndex = 0;
            equipmentList.SelectedItem = dialog.Result;
            equipmentList.ScrollIntoView(dialog.Result);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (equipmentList.SelectedItem is not EquipmentLedgerItem eq)
        {
            await EquipmentMessageBox.Info(this, "请先选择一台设备。");
            return;
        }
        var msg = $"确定删除 \"{eq.Id} · {eq.Model}\" 吗?\n\n该操作可通过点击\"保存\"前撤销(关闭窗口不保存即可恢复)。";
        if (await EquipmentMessageBox.Confirm(this, msg, "确认删除")) _all.Remove(eq);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            EqSaveLedger(_ctx.Conn, _all);
            _isDirty = false;
            UpdateStatus();
            await EquipmentMessageBox.Info(this, "已保存。");
        }
        catch (Exception ex) { await EquipmentMessageBox.Info(this, $"保存失败:{ex.Message}", "错误"); }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose && _isDirty)
        {
            e.Cancel = true;
            _ = AskSaveThenClose();
            return;
        }
        base.OnClosing(e);
    }

    private async Task AskSaveThenClose()
    {
        var r = await EquipmentMessageBox.YesNoCancel(this, "有未保存的修改,是否保存?");
        if (r == null) return;
        if (r == true)
        {
            try { EqSaveLedger(_ctx.Conn, _all); }
            catch (Exception ex) { await EquipmentMessageBox.Info(this, $"保存失败:{ex.Message}", "错误"); return; }
        }
        _forceClose = true;
        Close();
    }
}
