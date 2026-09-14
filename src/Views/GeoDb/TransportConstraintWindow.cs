using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Transport;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 「约束条件设置」（移植原 <c>MineAssLib.Views.TransportConstraintDialog</c>）——
/// 开拓运输系统设计的硬约束与参数。
///
/// 设计原则（原版的话）：<b>能从【设备库 / 几何 / 规范】推出来的全部自动算，
/// 只把"设备选型"与"价/成本"留给人填。</b>八个分区与原版一一对应，⑧ 区是实时预览、不用填。
///
/// 方案：一台机器做两个矿（或同一个矿的两套比选口径）不能互相覆盖，故配置是
/// 「多份方案 + 当前是哪一份」（<see cref="TransportConstraintProfiles"/>）。
/// <b>镜像不变量</b>：保存时把当前方案镜像回老的单份配置键，消费侧只读那一个键、不必知道方案的存在。
///
/// 校验判据在 <see cref="TransportConstraintCheck"/>（纯函数）：预览与确认共用同一份，
/// "预览说没问题、确认却拦下来"从设计上不可能。
/// </summary>
internal sealed class TransportConstraintWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Func<DbConnection?> _conn;
    private readonly Action<string> _echo;

    private TransportConstraintProfiles _profiles = new();
    private List<TruckPreset> _presets = new();
    private bool _loading;      // 载入时抑制 TextChanged 触发的预览重算

    // ── 输入控件（按分区分组，名字对齐原版）──
    private readonly Dictionary<string, TextBox> _tb = new(StringComparer.Ordinal);
    private readonly ComboBox _cmbProfile = new() { MinWidth = 180 };
    private readonly ComboBox _cmbTruck = new() { MinWidth = 180 };
    private readonly ComboBox _cmbObjective = new() { MinWidth = 180 };
    private readonly CheckBox _chkAutoLane = new() { Content = "车道数按各期产量自动确定" };

    private readonly TextBlock _lblProfileHint = Note();
    private readonly TextBlock _lblTruckSource = Note();
    private readonly TextBlock _lblTruckGap = Note("#D97706");
    private readonly StackPanel _derived = new() { Spacing = 1 };
    private readonly TextBlock _lblWarn = Note("#DC2626");
    private readonly TextBlock _lblHard = Note("#DC2626");
    private readonly TextBlock _lblSoft = Note("#D97706");
    private readonly TextBlock _status = Note();

    /// <summary>优化目标下拉项（与原版一致）。</summary>
    private static readonly string[] Objectives = { "最小总成本", "最短运距", "最小基建量" };

    private static TextBlock Note(string? color = null) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Foreground = Brush.Parse(color ?? "#555"),
    };

    internal TransportConstraintWindow(Func<DbConnection?> conn, Action<string> echo)
    {
        _conn = conn; _echo = echo;
        Title = "约束条件设置";
        // 八个分区一条竖列 + 滚动条，照原版的 900×720。
        // 试过把高度加到 980 让 ⑧ 区(实时预览)少滚两下，实测**反而更糟**：
        // 本机界面缩放 0.77，逻辑 980 落到屏上是 ~1270 物理像素，窗口底边连同「确定/取消」
        // 一起被推出屏幕外 —— 按逻辑像素定高，在缩放不是 1 的机器上就是这个下场。
        // 要改高只能按屏幕工作区反算，不能拍脑袋给个大数。
        Width = 900; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        Content = BuildLayout();
        LoadProfiles();
    }

    // ── 布局 ────────────────────────────────────────────────────────────────

    private TextBox Field(string key, double width = 90)
    {
        var t = new TextBox { Width = width, Padding = new Thickness(6, 3) };
        t.TextChanged += (_, _) => { if (!_loading) RefreshPreview(); };
        _tb[key] = t;
        return t;
    }

    /// <summary>一格 = 「标签 + 输入框」。分区里按每行两格排。</summary>
    private Control Cell(string label, string key, double width = 90) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 18, 6),
        Children =
        {
            new TextBlock { Text = label, Width = 132, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap },
            Field(key, width),
        },
    };

    private static Control Section(string header, params Control[] body)
    {
        var inner = new StackPanel { Margin = new Thickness(10, 8) };
        foreach (var c in body) inner.Children.Add(c);
        return new Border
        {
            BorderBrush = Brush.Parse("#D0D0D0"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(10, 4),
                        Child = new TextBlock { Text = header, FontWeight = FontWeight.SemiBold },
                    },
                    inner,
                },
            },
        };
    }

    private static WrapPanel Row(params Control[] cells)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in cells) w.Children.Add(c);
        return w;
    }

    private Control BuildLayout()
    {
        _cmbObjective.ItemsSource = Objectives;
        _cmbObjective.SelectionChanged += (_, _) => { if (!_loading) RefreshPreview(); };
        _cmbTruck.SelectionChanged += (_, _) => { if (!_loading) OnTruckChanged(); };
        _cmbProfile.SelectionChanged += (_, _) => { if (!_loading) OnProfileChanged(); };
        _chkAutoLane.IsCheckedChanged += (_, _) => { if (!_loading) RefreshPreview(); };

        Button B(string t, Action a)
        {
            var b = new Button { Content = t, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => a();
            return b;
        }

        var profileBar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(0, 0, 0, 4),
            Children =
            {
                new TextBlock { Text = "方案", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                _cmbProfile,
                new Border { Width = 10 },
                B("新建", NewProfile), B("复制", DuplicateProfile), B("改名…", RenameProfile), B("删除", RemoveProfile),
            },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(12, 8, 12, 8),
            Children =
            {
                Section("① 设备选型（人选 · 驱动其余）",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6),
                        Children =
                        {
                            new TextBlock { Text = "卡车型号", Width = 132, VerticalAlignment = VerticalAlignment.Center },
                            _cmbTruck,
                        },
                    },
                    Row(Cell("车宽 m", "TruckWidth"), Cell("额定爬坡度 %", "TruckClimbPct")),
                    Row(Cell("最小转弯半径 m", "TruckTurnRadius"), Cell("最大轮胎直径 m", "TireDiameter")),
                    Row(Cell("额定载重 t/车", "TruckPayload"), Cell("车辆轴距 m", "VehicleWheelbase")),
                    _lblTruckSource, _lblTruckGap),

                Section("② 硬约束默认（规范/经验 · 可改）",
                    Row(Cell("限制坡度 i_max %", "MaxGradePct"), Cell("最小工作平盘宽 m", "MinWorkingBenchWidth")),
                    Row(Cell("车道数", "LaneCount"), _chkAutoLane),
                    Row(Cell("车间安全间隙 c m", "LaneClearance"), Cell("安全带/路肩 d m", "SafetyStrip"))),

                Section("③ 通过能力参数（默认 · 可改）",
                    Row(Cell("设计车速 km/h", "DesignSpeedKmh"), Cell("车头时距 s", "HeadwaySec")),
                    Row(Cell("设备利用率 %", "UtilizationPct"), Cell("年有效作业时间 h", "WorkHoursPerYear"))),

                Section("④ 价 / 成本（人给 · 给默认）",
                    Row(Cell("矿石价值 元/t", "OreValue"), Cell("运输单价 元/(t·km)", "HaulUnitCost")),
                    Row(Cell("剥离单价 元/m³", "StripUnitCost"), Cell("油价 元/L", "FuelPrice"))),

                Section("⑤ 优化目标",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "目标", Width = 132, VerticalAlignment = VerticalAlignment.Center },
                            _cmbObjective,
                            new Border { Width = 18 },
                            new TextBlock { Text = "参考台阶高 m", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                            Field("RefBenchHeight"),
                        },
                    }),

                Section("⑥ 线形约束（GBJ22-87 · 可改）",
                    Row(Cell("最小平曲线半径 m", "MinCurveRadiusM"), Cell("弯道加宽阈值 m", "CurveWidenThresholdM")),
                    Row(Cell("弯道最大纵坡 %", "CurveMaxGradePct"), Cell("最大合成坡度 %", "MaxResultantGradePct")),
                    Row(Cell("最大超高横坡 %", "MaxSuperelevationPct"), Cell("设竖曲线变坡阈值 %", "VerticalCurveTriggerDiffPct")),
                    Row(Cell("最小竖曲线半径 m", "MinVerticalCurveRadiusM"), Cell("最小坡长 m", "MinGradeSectionLengthM"))),

                Section("⑦ 缓坡段（长大下坡制动/散热 · 可改）",
                    Row(Cell("最大连续下降高差 m", "MaxContinuousDropM"), Cell("缓坡段纵坡 %", "EaseGradePct")),
                    Row(Cell("缓坡段最小长度 m", "EaseMinLengthM"))),

                Section("⑧ 自动计算（实时预览 · 无需填写）", _derived, _lblWarn, _lblHard, _lblSoft),
            },
        };

        var ok = B("确定", Commit);
        ok.FontWeight = FontWeight.SemiBold; ok.MinWidth = 84;
        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 10),
            Children =
            {
                B("恢复默认", () => Load(new TransportConstraintSettings())),
                B("导入…", Import), B("导出…", Export),
                new Border { Width = 1, Background = Brush.Parse("#D0D0D0"), Margin = new Thickness(4, 2, 12, 2) },
                ok, B("取消", Close),
            },
        };

        var top = new StackPanel { Margin = new Thickness(12, 10, 12, 0), Children = { profileBar, _lblProfileHint } };
        var bottom = new StackPanel { Children = { new Border { Margin = new Thickness(12, 0), Child = _status }, foot } };

        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body });
        return root;
    }

    // ── 装载 ────────────────────────────────────────────────────────────────

    private void LoadProfiles()
    {
        // 老单份配置是升级迁移的来源：容器为空时用它建「默认方案」，用户原配置原样保住
        var fallback = TransportConstraintProfileStore.LoadMirrorOrDefault(null);
        _profiles = TransportConstraintProfileStore.Load(null) ?? new TransportConstraintProfiles();
        var cur = _profiles.EnsureUsable(fallback);

        _presets = TruckPresets.AllFor(_conn());
        int registryCount = _presets.Count(p => p.Source == TruckPresetSource.Registry);
        _lblTruckSource.Text = TransportConstraintCheck.TruckSourceNote(registryCount);

        _loading = true;
        _cmbTruck.ItemsSource = _presets.Select(p => p.DisplayName).ToList();
        RefreshProfileCombo();
        _loading = false;

        Load(cur);
    }

    private void RefreshProfileCombo()
    {
        _cmbProfile.ItemsSource = _profiles.SortedNames();
        _cmbProfile.SelectedItem = _profiles.CurrentName;
        _lblProfileHint.Text = $"共 {_profiles.Profiles.Count} 份方案；当前「{_profiles.CurrentName}」。"
                             + "保存时当前方案会镜像回单份配置键，布线 / 坑线 / 寻径读的就是它。";
    }

    /// <summary>把一份配置铺到界面上。</summary>
    private void Load(TransportConstraintSettings s)
    {
        _loading = true;
        void P(string key, double v) => _tb[key].Text = v.ToString("0.####", Inv);

        P("TruckWidth", s.TruckWidth); P("TruckClimbPct", s.TruckClimbPct);
        P("TruckTurnRadius", s.TruckTurnRadius); P("TireDiameter", s.TireDiameter);
        P("TruckPayload", s.TruckPayload); P("VehicleWheelbase", s.VehicleWheelbase);
        P("MaxGradePct", s.MaxGradePct); P("MinWorkingBenchWidth", s.MinWorkingBenchWidth);
        _tb["LaneCount"].Text = s.LaneCount.ToString(Inv);
        P("LaneClearance", s.LaneClearance); P("SafetyStrip", s.SafetyStrip);
        P("DesignSpeedKmh", s.DesignSpeedKmh); P("HeadwaySec", s.HeadwaySec);
        P("UtilizationPct", s.UtilizationPct); P("WorkHoursPerYear", s.WorkHoursPerYear);
        P("OreValue", s.OreValue); P("HaulUnitCost", s.HaulUnitCost);
        P("StripUnitCost", s.StripUnitCost); P("FuelPrice", s.FuelPrice);
        P("RefBenchHeight", s.RefBenchHeight);
        P("MinCurveRadiusM", s.MinCurveRadiusM); P("CurveWidenThresholdM", s.CurveWidenThresholdM);
        P("CurveMaxGradePct", s.CurveMaxGradePct); P("MaxResultantGradePct", s.MaxResultantGradePct);
        P("MaxSuperelevationPct", s.MaxSuperelevationPct); P("VerticalCurveTriggerDiffPct", s.VerticalCurveTriggerDiffPct);
        P("MinVerticalCurveRadiusM", s.MinVerticalCurveRadiusM); P("MinGradeSectionLengthM", s.MinGradeSectionLengthM);
        P("MaxContinuousDropM", s.MaxContinuousDropM); P("EaseGradePct", s.EaseGradePct);
        P("EaseMinLengthM", s.EaseMinLengthM);

        _chkAutoLane.IsChecked = s.AutoLaneByCapacity;
        _cmbObjective.SelectedItem = Objectives.Contains(s.Objective) ? s.Objective : Objectives[0];
        int ti = _presets.FindIndex(p => string.Equals(p.Name, s.TruckClass, StringComparison.Ordinal));
        _cmbTruck.SelectedIndex = ti >= 0 ? ti : 0;
        _lblTruckGap.Text = "";
        _lblTruckGap.IsVisible = false;

        _loading = false;
        RefreshPreview();
    }

    /// <summary>从界面读出一份配置（某字段解析不出就保留该字段的默认值）。</summary>
    internal TransportConstraintSettings ReadUi()
    {
        var s = new TransportConstraintSettings
        {
            TruckClass = SelectedPreset()?.Name ?? TruckPresets.CustomName,
            Objective = _cmbObjective.SelectedItem as string ?? Objectives[0],
            AutoLaneByCapacity = _chkAutoLane.IsChecked == true,
        };
        void D(string key, Action<double> set)
        {
            if (double.TryParse(_tb[key].Text, NumberStyles.Float, Inv, out double v)) set(v);
        }
        D("TruckWidth", v => s.TruckWidth = v); D("TruckClimbPct", v => s.TruckClimbPct = v);
        D("TruckTurnRadius", v => s.TruckTurnRadius = v); D("TireDiameter", v => s.TireDiameter = v);
        D("TruckPayload", v => s.TruckPayload = v); D("VehicleWheelbase", v => s.VehicleWheelbase = v);
        D("MaxGradePct", v => s.MaxGradePct = v); D("MinWorkingBenchWidth", v => s.MinWorkingBenchWidth = v);
        if (int.TryParse(_tb["LaneCount"].Text, NumberStyles.Integer, Inv, out int lanes)) s.LaneCount = lanes;
        D("LaneClearance", v => s.LaneClearance = v); D("SafetyStrip", v => s.SafetyStrip = v);
        D("DesignSpeedKmh", v => s.DesignSpeedKmh = v); D("HeadwaySec", v => s.HeadwaySec = v);
        D("UtilizationPct", v => s.UtilizationPct = v); D("WorkHoursPerYear", v => s.WorkHoursPerYear = v);
        D("OreValue", v => s.OreValue = v); D("HaulUnitCost", v => s.HaulUnitCost = v);
        D("StripUnitCost", v => s.StripUnitCost = v); D("FuelPrice", v => s.FuelPrice = v);
        D("RefBenchHeight", v => s.RefBenchHeight = v);
        D("MinCurveRadiusM", v => s.MinCurveRadiusM = v); D("CurveWidenThresholdM", v => s.CurveWidenThresholdM = v);
        D("CurveMaxGradePct", v => s.CurveMaxGradePct = v); D("MaxResultantGradePct", v => s.MaxResultantGradePct = v);
        D("MaxSuperelevationPct", v => s.MaxSuperelevationPct = v);
        D("VerticalCurveTriggerDiffPct", v => s.VerticalCurveTriggerDiffPct = v);
        D("MinVerticalCurveRadiusM", v => s.MinVerticalCurveRadiusM = v);
        D("MinGradeSectionLengthM", v => s.MinGradeSectionLengthM = v);
        D("MaxContinuousDropM", v => s.MaxContinuousDropM = v); D("EaseGradePct", v => s.EaseGradePct = v);
        D("EaseMinLengthM", v => s.EaseMinLengthM = v);
        return s;
    }

    private TruckPreset? SelectedPreset()
    {
        int i = _cmbTruck.SelectedIndex;
        return i >= 0 && i < _presets.Count ? _presets[i] : null;
    }

    // ── 交互 ────────────────────────────────────────────────────────────────

    /// <summary>切车型：**只覆盖该档确实提供的字段**，没提供的保持原值并当场列出来。</summary>
    private void OnTruckChanged()
    {
        var p = SelectedPreset();
        if (p == null) return;

        _loading = true;
        void Set(string key, double? v) { if (v.HasValue) _tb[key].Text = v.Value.ToString("0.####", Inv); }
        Set("TruckWidth", p.WidthM);
        Set("TruckClimbPct", p.ClimbPct);
        Set("TruckTurnRadius", p.TurnRadiusM);
        Set("TireDiameter", p.TireDiameterM);
        Set("MaxGradePct", p.MaxGradePct);
        Set("TruckPayload", p.PayloadT);
        Set("VehicleWheelbase", p.WheelbaseM);
        _loading = false;

        // "自定义"档不带任何参数，本来就不该报"未提供"
        string gap = string.Equals(p.Name, TruckPresets.CustomName, StringComparison.Ordinal)
            ? "" : TransportConstraintCheck.TruckGapNote(p);
        _lblTruckGap.Text = gap;
        _lblTruckGap.IsVisible = gap.Length > 0;

        RefreshPreview();
    }

    private void OnProfileChanged()
    {
        if (_cmbProfile.SelectedItem is not string name) return;
        // 切走之前先把界面上的改动落回原方案，免得改了一半切走就丢了
        _profiles.Put(_profiles.CurrentName, ReadUi());
        _profiles.CurrentName = name;
        var s = _profiles.Find(name);
        if (s != null) Load(s);
        RefreshProfileCombo();
    }

    private void RefreshPreview()
    {
        var s = ReadUi();

        _derived.Children.Clear();
        foreach (var line in TransportConstraintCheck.DerivedLines(s))
            _derived.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) });

        string warn = TransportConstraintCheck.GradeWarning(s);
        _lblWarn.Text = warn;
        _lblWarn.IsVisible = warn.Length > 0;

        // 硬错 / 软警实时回显 —— 让"哪个约束在卡着"当场看得见（确认时用同一套规则再拦一次）
        var hard = TransportConstraintCheck.CollectHardErrors(s);
        _lblHard.Text = hard.Count > 0 ? "✖ " + string.Join("\n✖ ", hard) : "";
        _lblHard.IsVisible = hard.Count > 0;

        var soft = TransportConstraintCheck.CollectSoftWarnings(s);
        _lblSoft.Text = soft.Count > 0 ? "⚠ " + string.Join("\n⚠ ", soft) : "";
        _lblSoft.IsVisible = soft.Count > 0;
    }

    private void SetStatus(string text, string? color = null)
    {
        _status.Text = text;
        _status.Foreground = Brush.Parse(color ?? "#555");
        if (text.Length > 0) _echo(text);
    }

    // ── 方案增删改 ──────────────────────────────────────────────────────────

    private void NewProfile() => AddProfile(new TransportConstraintSettings(), "新方案");
    private void DuplicateProfile() => AddProfile(ReadUi(), _profiles.CurrentName);

    private void AddProfile(TransportConstraintSettings s, string baseName)
    {
        _profiles.Put(_profiles.CurrentName, ReadUi());     // 先落回当前方案
        string name = _profiles.UniqueName(baseName);
        _profiles.Put(name, s);
        _profiles.CurrentName = name;
        _loading = true; RefreshProfileCombo(); _loading = false;
        Load(s);
        SetStatus($"已新增方案「{name}」（点「确定」才写盘）");
    }

    private async void RenameProfile()
    {
        string? name = await TextPrompt.ShowAsync(this, "方案改名", "新名称", _profiles.CurrentName);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!_profiles.Rename(_profiles.CurrentName, name!.Trim()))
        { SetStatus("改名失败：新名重名，或名字为空。", "#DC2626"); return; }
        _loading = true; RefreshProfileCombo(); _loading = false;
        SetStatus($"已改名为「{_profiles.CurrentName}」（点「确定」才写盘）");
    }

    private void RemoveProfile()
    {
        string gone = _profiles.CurrentName;
        if (!_profiles.Remove(gone))
        { SetStatus("只剩一份方案了，删不得（容器不能为空）。", "#D97706"); return; }
        _loading = true; RefreshProfileCombo(); _loading = false;
        var s = _profiles.Find(_profiles.CurrentName);
        if (s != null) Load(s);
        SetStatus($"已删除方案「{gone}」，当前切到「{_profiles.CurrentName}」（点「确定」才写盘）");
    }

    // ── 导入 / 导出 / 确定 ──────────────────────────────────────────────────

    private async void Import()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        { Title = "导入约束方案", AllowMultiple = false });
        var f = files?.FirstOrDefault();
        if (f == null) return;

        var box = TransportConstraintProfileStore.TryImport(f.Path.LocalPath, out string? err);
        if (box == null) { SetStatus("导入失败：" + (err ?? "未知原因"), "#DC2626"); return; }

        _profiles.Put(_profiles.CurrentName, ReadUi());
        // 重名自动加 "(2)"、"(3)"… 后缀，**绝不覆盖已有方案**
        var added = _profiles.MergeFrom(box);
        if (added.Count == 0) { SetStatus("导入的文件里没有方案。", "#D97706"); return; }
        _profiles.CurrentName = added[^1];
        _loading = true; RefreshProfileCombo(); _loading = false;
        var s = _profiles.Find(_profiles.CurrentName);
        if (s != null) Load(s);
        SetStatus($"已导入 {added.Count} 份方案：{string.Join("、", added)}（点「确定」才写盘）");
    }

    private async void Export()
    {
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        { Title = "导出约束方案", SuggestedFileName = "transport.constraint.profiles.json", DefaultExtension = "json" });
        if (f == null) return;
        _profiles.Put(_profiles.CurrentName, ReadUi());
        if (TransportConstraintProfileStore.TryExport(f.Path.LocalPath, _profiles, out string? err))
            SetStatus($"已导出 {_profiles.Profiles.Count} 份方案 → {f.Path.LocalPath}", "#16A34A");
        else SetStatus("导出失败：" + (err ?? "未知原因"), "#DC2626");
    }

    private void Commit()
    {
        var s = ReadUi();
        // 与预览同一套判据：预览说没问题，这里就不会拦
        var hard = TransportConstraintCheck.CollectHardErrors(s);
        if (hard.Count > 0)
        { SetStatus("以下约束互相打架，请先调整：" + string.Join("；", hard), "#DC2626"); return; }

        _profiles.Put(_profiles.CurrentName, s);   // 界面改动落回当前方案
        if (!TransportConstraintProfileStore.TrySave(null, _profiles, out string? err))
        { SetStatus("保存失败：" + (err ?? "未知原因"), "#DC2626"); return; }

        _echo($"约束条件已保存：方案「{_profiles.CurrentName}」"
            + $"（限坡 {s.MaxGradePct:0.#}% · {s.LaneCount} 车道 · 路面宽 {s.RoadWidthPreview():0.0} m）");
        Close();
    }

    // ── 自检钩子用 ──
    internal string DerivedText => string.Join("\n", _derived.Children.OfType<TextBlock>().Select(t => t.Text));
    internal string HardText => _lblHard.Text ?? "";
    internal string SoftText => _lblSoft.Text ?? "";
    internal string ProfileHint => _lblProfileHint.Text ?? "";
    internal string TruckSourceText => _lblTruckSource.Text ?? "";
}

/// <summary>一行文本输入的小对话框（方案改名用）。</summary>
internal sealed class TextPrompt : Window
{
    private readonly TextBox _box = new() { Width = 260 };
    private string? _result;

    private TextPrompt(string title, string label, string? initial)
    {
        Title = title;
        Width = 360; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box.Text = initial ?? "";

        var ok = new Button { Content = "确定", Padding = new Thickness(16, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 4), IsCancel = true };
        ok.Click += (_, _) => { _result = _box.Text; Close(); };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 8,
            Children =
            {
                new TextBlock { Text = label },
                _box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };
    }

    internal static async System.Threading.Tasks.Task<string?> ShowAsync(Window owner, string title, string label, string? initial)
    {
        var d = new TextPrompt(title, label, initial);
        await d.ShowDialog(owner);
        return d._result;
    }
}
