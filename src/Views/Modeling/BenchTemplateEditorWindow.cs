using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Collections;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 参数化开采模板编辑器（移植原 <c>MineAssLib.Views.MiningTemplateEditorWindow</c>）。
///   · 正向编辑 H/α/W → **实时**算最终帮坡角 + 越界校核 + 2D 剖面预览；
///   · 规范预设一键套推荐值；帮角反算 W；
///   · 存为命名模板 → 放坡入口（参数校核等）经 <see cref="BenchTemplateResolver"/> 自动套用。
///
/// 采场 / 排土两种类型同一个窗口，靠上方单选切换 —— 与原版一致
/// （原版「参数化模板」与「排土模板」两个按钮开的就是同一个窗，只是初始类型不同）。
/// </summary>
internal sealed class BenchTemplateEditorWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>内置规范预设（镜像 <see cref="BenchTemplateResolver.Norm"/>；UI 便捷用）。</summary>
    private static readonly Dictionary<string, (double H, double A, double W)> Presets = new()
    {
        ["hard"] = (15, 70, 8),
        ["medium"] = (12, 68, 6),
        ["soft"] = (10, 60, 5),
        ["dump"] = (10, 35, 3),
    };

    private readonly Func<System.Data.Common.DbConnection?> _conn;
    private readonly Action<string> _echo;
    private bool _ready;

    private readonly ComboBox _cmbExisting = new() { MinWidth = 200 };
    private readonly TextBox _tbName = new() { Width = 220, Padding = new Thickness(6, 3) };
    private readonly RadioButton _rbPit = new() { Content = "采场", GroupName = "SlopeType", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
    private readonly RadioButton _rbDump = new() { Content = "排土场", GroupName = "SlopeType" };
    private readonly ComboBox _cmbHardness = new() { MinWidth = 120 };

    private readonly TextBox _tbH = Num(), _tbA = Num(), _tbW = Num(), _tbWw = Num(), _tbMw = Num();
    private readonly TextBox _tbCoalH = Num(), _tbCoalA = Num(), _tbCoalW = Num();
    private readonly TextBox _tbTargetBeta = Num();

    private readonly TextBlock _lblBeta = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _lblWarn = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#D97706") };
    private readonly TextBlock _lblStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#555") };
    private readonly Canvas _preview = new() { Height = 150, Background = Brush.Parse("#FAFAFA") };
    private readonly Border _coalBox;

    private static TextBox Num() => new()
    { Width = 84, HorizontalContentAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 3) };

    internal BenchTemplateEditorWindow(Func<System.Data.Common.DbConnection?> conn, Action<string> echo, bool dump)
    {
        _conn = conn; _echo = echo;
        Title = "参数化开采模板";
        // 高度按**这块屏放得下**定，不是按"内容想要多高"定：本机 150% 缩放下
        // 600 逻辑 = 900 物理，相对宿主居中之后底边连同页脚一起出屏。
        // 内容本来就在 ScrollViewer 里，矮一点只是多滚两下，页脚却一定点得到。
        Width = 760; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PitMine3D.Kylin.Views.WindowFit.ClampToScreen(this);

        _cmbHardness.ItemsSource = new[] { "（不限）", "硬岩", "中硬岩", "软岩" };
        _cmbHardness.SelectedIndex = 0;
        _coalBox = Group("煤台阶（煤岩分层放坡 · 仅采场）", new WrapPanel
        {
            Children =
            {
                Cell("台阶高 m", _tbCoalH), Cell("坡面角 °", _tbCoalA), Cell("平盘宽 m", _tbCoalW),
            },
        });

        Content = BuildLayout();

        _rbPit.IsCheckedChanged += (_, _) => OnTypeChanged();
        _rbDump.IsCheckedChanged += (_, _) => OnTypeChanged();
        foreach (var t in new[] { _tbH, _tbA, _tbW, _tbWw, _tbMw, _tbCoalH, _tbCoalA, _tbCoalW })
            t.TextChanged += (_, _) => { if (_ready) Recompute(); };
        _cmbExisting.SelectionChanged += (_, _) => { if (_ready) LoadSelected(); };
        _preview.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty && _ready) Recompute(); };

        // 初值：库里的规范默认；库不通就用硬兜底
        var conn0 = _conn();
        _tbWw.Text = F(BenchTemplateResolver.StandardDefault(conn0, "working_platform_width")
                       ?? BenchTemplateResolver.DefaultMinWorkingBerm);
        _tbCoalH.Text = F(BenchTemplateResolver.StandardDefault(conn0, "coal_bench_height") ?? BenchTemplateResolver.DefaultCoalH);
        _tbCoalA.Text = F(BenchTemplateResolver.StandardDefault(conn0, "coal_bench_slope_angle") ?? BenchTemplateResolver.DefaultCoalA);
        _tbCoalW.Text = F(BenchTemplateResolver.StandardDefault(conn0, "coal_platform_width") ?? BenchTemplateResolver.DefaultCoalW);
        _tbMw.Text = "";
        _tbTargetBeta.Text = "30";

        if (dump) { _rbDump.IsChecked = true; _tbName.Text = "排土场标准 v1.0"; }
        else { _tbName.Text = "采场标准 v1.0"; }

        _ready = true;
        ApplyPreset(dump ? "dump" : "hard");
        ReloadList();
    }

    private static string F(double v) => v.ToString("0.##", Inv);

    private static Control Cell(string label, Control box) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 18, 6),
        // 标签定宽 82 装不下「安全平台 W m」「采宽 m（可空）」(截图核对出来的: 末尾被切),
        // 给到 112; 另关掉裁剪, 宁可挤也别悄悄少一个字。
        Children =
        {
            new TextBlock
            {
                Text = label, Width = 112, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
            },
            box,
        },
    };

    private static Border Group(string header, Control body) => new()
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
                new Border { Padding = new Thickness(10, 8), Child = body },
            },
        },
    };

    private Control BuildLayout()
    {
        Button B(string t, Action a, bool bold = false)
        {
            var b = new Button { Content = t, Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 8, 0) };
            if (bold) b.FontWeight = FontWeight.SemiBold;
            b.Click += (_, _) => a();
            return b;
        }

        var body = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 8),
            Children =
            {
                Group("模板", new StackPanel
                {
                    Children =
                    {
                        new WrapPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "已有", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6) },
                                _cmbExisting,
                                new TextBlock { Text = "名称", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 6) },
                                _tbName,
                            },
                        },
                        new WrapPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "边坡类型", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                                _rbPit, _rbDump,
                                new TextBlock { Text = "岩性", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) },
                                _cmbHardness,
                                B("套规范预设", ApplyPresetFromHardness),
                            },
                        },
                    },
                }),

                Group("岩台阶", new StackPanel
                {
                    Children =
                    {
                        new WrapPanel
                        {
                            Children =
                            {
                                Cell("台阶高 H m", _tbH), Cell("坡面角 α °", _tbA),
                                Cell("安全平台 W m", _tbW),
                            },
                        },
                        new WrapPanel
                        {
                            Children = { Cell("最小工作平盘 m", _tbWw), Cell("采宽 m（可空）", _tbMw) },
                        },
                        new WrapPanel
                        {
                            Children =
                            {
                                new TextBlock { Text = "帮角反算：目标帮坡角 °", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                                _tbTargetBeta,
                                B("反算安全平台 W", SolveBerm),
                            },
                        },
                    },
                }),

                _coalBox,

                Group("推导与校核（实时）", new StackPanel
                {
                    Children = { _lblBeta, _lblWarn, new Border { Margin = new Thickness(0, 6, 0, 0), Child = _preview } },
                }),
            },
        };

        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 6, 12, 10),
            Children = { B("删除", DeleteTemplate), B("保存模板", SaveTemplate, bold: true), B("关闭", Close) },
        };

        // 页脚与状态行**各自单独 Dock 到底**，不要套一层 StackPanel 再整个 Dock ——
        // 那种写法在本窗实测渲染不出页脚(截图核对: 底部一片空白)，而分开 Dock 的写法
        // 在「延拓触发设置」上是验证过的。给页脚一条浅底色，它在不在一眼看得出来。
        var footBar = new Border
        {
            Background = Brush.Parse("#F3F3F3"), Padding = new Thickness(12, 6, 12, 10),
            BorderBrush = Brush.Parse("#E0E0E0"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = foot,
        };
        var statusBar = new Border { Padding = new Thickness(12, 2), Child = _lblStatus };

        var root = new DockPanel();
        DockPanel.SetDock(footBar, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(statusBar, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(footBar);
        root.Children.Add(statusBar);
        root.Children.Add(new ScrollViewer { Content = body });
        return root;
    }

    // ── 交互 ────────────────────────────────────────────────────────────────

    private bool IsDump => _rbDump.IsChecked == true;

    private string? HardnessCode => _cmbHardness.SelectedIndex switch
    { 1 => "hard", 2 => "medium", 3 => "soft", _ => null };

    private void OnTypeChanged()
    {
        if (!_ready) return;
        // 排土场没有煤岩分层这回事 —— 整块藏起来，而不是留着让人填了却存不进去
        _coalBox.IsVisible = !IsDump;
        Recompute();
    }

    private void ApplyPresetFromHardness() => ApplyPreset(IsDump ? "dump" : (HardnessCode ?? "medium"));

    private void ApplyPreset(string key)
    {
        if (!Presets.TryGetValue(key, out var p)) return;
        bool old = _ready; _ready = false;
        _tbH.Text = F(p.H); _tbA.Text = F(p.A); _tbW.Text = F(p.W);
        _coalBox.IsVisible = !IsDump;
        _ready = old;
        Recompute();
    }

    private static double D(TextBox t, double dflt)
        => double.TryParse(t.Text, NumberStyles.Float, Inv, out double v) ? v : dflt;

    private void SolveBerm()
    {
        double h = D(_tbH, 0), a = D(_tbA, 0), beta = D(_tbTargetBeta, 0);
        double w = BenchTemplateResolver.SolveBermForOverallAngle(h, a, beta);
        if (w <= 0 && beta > 0)
        {
            // 台阶本身就没那么陡，再削平盘也到不了 —— 说清楚，不要给个 0 让人以为算对了
            SetStatus($"目标帮坡角 {beta:0.#}° 比坡面角 {a:0.#}° 还陡：光靠平盘宽达不到，需先调坡面角。", "#DC2626");
            return;
        }
        _tbW.Text = F(w);
        Recompute();
        SetStatus($"已按目标帮坡角 {beta:0.#}° 反算安全平台 W={w:0.##} m。", null);
    }

    private void Recompute()
    {
        double h = D(_tbH, 0), a = D(_tbA, 0), w = D(_tbW, 0), ww = D(_tbWw, 0);
        double beta = BenchTemplateResolver.OverallSlopeAngleDeg(h, a, w);
        double betaWork = BenchTemplateResolver.OverallSlopeAngleDeg(h, a, ww);
        _lblBeta.Text = $"最终帮坡角 β ≈ {beta:0.##}°（H/α + 安全平台 {w:0.##}m）"
                      + $"　|　工作帮坡角 ≈ {betaWork:0.##}°（+ 最小工作平盘 {ww:0.##}m）";

        // 越界校核走与解析器同一份规范范围
        var conn = _conn();
        var warns = new List<string>();
        void Chk(string code, double v) { AppendRangeWarning(conn, code, v, warns); }
        Chk("bench_height", h);
        Chk("bench_slope_angle", a);
        Chk("safety_platform_width", w);
        Chk("working_platform_width", ww);
        if (!IsDump)
        {
            Chk("coal_bench_height", D(_tbCoalH, 0));
            Chk("coal_bench_slope_angle", D(_tbCoalA, 0));
            Chk("coal_platform_width", D(_tbCoalW, 0));
        }
        _lblWarn.Text = warns.Count > 0 ? "⚠ " + string.Join("；", warns) : "";
        _lblWarn.IsVisible = warns.Count > 0;

        DrawPreview(h, a, w);
    }

    private static void AppendRangeWarning(System.Data.Common.DbConnection? conn, string code, double v, List<string> into)
    {
        if (conn == null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT name, unit, standard_min, standard_max FROM parameter_definition WHERE code = '{code}'";
            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return;
            string name = rd.IsDBNull(0) ? code : rd.GetValue(0)?.ToString() ?? code;
            string unit = rd.IsDBNull(1) ? "" : rd.GetValue(1)?.ToString() ?? "";
            if (!rd.IsDBNull(2))
            {
                double lo = Convert.ToDouble(rd.GetValue(2), Inv);
                if (v < lo) into.Add($"{name} {v:0.##}{unit} 低于规范下限 {lo:0.##}");
            }
            if (!rd.IsDBNull(3))
            {
                double hi = Convert.ToDouble(rd.GetValue(3), Inv);
                if (v > hi) into.Add($"{name} {v:0.##}{unit} 高于规范上限 {hi:0.##}");
            }
        }
        catch { }
    }

    /// <summary>2D 剖面预览（比例真实，坡面 + 平盘 N 级）。</summary>
    private void DrawPreview(double h, double a, double w)
    {
        _preview.Children.Clear();
        double cw = _preview.Bounds.Width, ch = _preview.Bounds.Height;
        if (cw < 30 || ch < 30 || h <= 0 || a <= 0 || a >= 90) return;

        const int levels = 4;
        double run = h / Math.Tan(a * Math.PI / 180.0) + Math.Max(0, w);
        double totalW = run * levels, totalH = h * levels;
        if (totalW <= 1e-6 || totalH <= 1e-6) return;

        double s = Math.Min((cw - 20) / totalW, (ch - 20) / totalH);   // 等比例，不拉伸
        double x = 10, y = ch - 10;
        var pts = new List<Avalonia.Point> { new(x, y) };
        for (int i = 0; i < levels; i++)
        {
            double face = h / Math.Tan(a * Math.PI / 180.0);
            x += face * s; y -= h * s;
            pts.Add(new Avalonia.Point(x, y));      // 坡面
            x += Math.Max(0, w) * s;
            pts.Add(new Avalonia.Point(x, y));      // 平盘
        }
        var line = new Polyline
        {
            Points = pts, Stroke = Brush.Parse("#1971C2"), StrokeThickness = 1.6,
        };
        _preview.Children.Add(line);

        // 整体帮坡角那条虚线：一眼看出台阶摞起来到底有多陡
        var start = pts[0];
        var end = pts[^1];
        _preview.Children.Add(new Line
        {
            StartPoint = start, EndPoint = end,
            Stroke = Brush.Parse("#DC2626"), StrokeThickness = 1,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
        });
    }

    // ── 模板列表 / 存 / 删 ──────────────────────────────────────────────────

    private void ReloadList()
    {
        var names = BenchTemplateStore.List(_conn()).Select(t => t.Name).ToList();
        names.Insert(0, "（新建）");
        bool old = _ready; _ready = false;
        _cmbExisting.ItemsSource = names;
        _cmbExisting.SelectedIndex = 0;
        _ready = old;
    }

    private void LoadSelected()
    {
        if (_cmbExisting.SelectedItem is not string name || name == "（新建）") return;
        var spec = BenchTemplateStore.Load(_conn(), name);
        if (spec == null) { SetStatus($"读不到模板「{name}」。", "#DC2626"); return; }

        bool old = _ready; _ready = false;
        _tbName.Text = spec.Name;
        _rbDump.IsChecked = spec.IsDump;
        _rbPit.IsChecked = !spec.IsDump;
        _cmbHardness.SelectedIndex = spec.Hardness switch { "hard" => 1, "medium" => 2, "soft" => 3, _ => 0 };
        _tbH.Text = F(spec.BenchHeight); _tbA.Text = F(spec.FaceAngleDeg); _tbW.Text = F(spec.BermWidth);
        _tbWw.Text = F(spec.MinWorkingBerm);
        _tbMw.Text = spec.MiningWidth is > 0 ? F(spec.MiningWidth.Value) : "";
        _tbCoalH.Text = F(spec.CoalBenchHeight); _tbCoalA.Text = F(spec.CoalFaceAngleDeg); _tbCoalW.Text = F(spec.CoalBermWidth);
        _coalBox.IsVisible = !spec.IsDump;
        _ready = old;

        Recompute();
        SetStatus($"已载入模板「{name}」（{(spec.IsDump ? "排土场" : "采场")}）。", null);
    }

    private BenchTemplateSpec ReadUi() => new()
    {
        Name = _tbName.Text ?? "",
        IsDump = IsDump,
        Hardness = HardnessCode,
        BenchHeight = D(_tbH, 0),
        FaceAngleDeg = D(_tbA, 0),
        BermWidth = D(_tbW, 0),
        MinWorkingBerm = D(_tbWw, 0),
        MiningWidth = double.TryParse(_tbMw.Text, NumberStyles.Float, Inv, out double mw) && mw > 0 ? mw : null,
        CoalBenchHeight = D(_tbCoalH, BenchTemplateResolver.DefaultCoalH),
        CoalFaceAngleDeg = D(_tbCoalA, BenchTemplateResolver.DefaultCoalA),
        CoalBermWidth = D(_tbCoalW, BenchTemplateResolver.DefaultCoalW),
    };

    private void SaveTemplate()
    {
        var spec = ReadUi();
        var r = BenchTemplateStore.Save(_conn(), spec);
        if (!r.Ok) { SetStatus("保存失败：" + r.Error, "#DC2626"); return; }

        string msg = (r.Inserted ? "✅ 已新建模板「" : "✅ 已更新模板「") + spec.Name + "」"
                   + $"（{(spec.IsDump ? "排土场" : "采场")}）：H={spec.BenchHeight:0.#} α={spec.FaceAngleDeg:0.#}° "
                   + $"安全平台={spec.BermWidth:0.#} 工作平盘={spec.MinWorkingBerm:0.#}"
                   + $" → 放坡/参数校核入口自动套用（写入 {r.ParamsWritten} 项）";
        // 库里没有的参数码要说出来 —— 否则用户以为存好了，实际那几项永远读不到
        if (r.MissingCodes.Count > 0)
            msg += $"　⚠ 库里没有参数码 {string.Join("、", r.MissingCodes)}，这几项没能存下";
        SetStatus(msg, r.MissingCodes.Count > 0 ? "#D97706" : "#16A34A");
        _echo(msg);
        ReloadList();
    }

    private void DeleteTemplate()
    {
        string name = (_tbName.Text ?? "").Trim();
        if (name.Length == 0) { SetStatus("请先选中或填入要删除的模板名。", "#D97706"); return; }
        string err = BenchTemplateStore.Delete(_conn(), name);
        if (err.Length > 0) { SetStatus("删除失败：" + err, "#DC2626"); return; }
        SetStatus($"已删除模板「{name}」。", null);
        _echo($"已删除开采模板「{name}」");
        ReloadList();
    }

    private void SetStatus(string text, string? color)
    {
        _lblStatus.Text = text;
        _lblStatus.Foreground = Brush.Parse(color ?? "#555");
    }

    // ── 自检钩子用 ──
    internal string BetaText => _lblBeta.Text ?? "";
    internal string StatusText => _lblStatus.Text ?? "";
    internal string WarnText => _lblWarn.Text ?? "";
    internal bool CoalVisible => _coalBox.IsVisible;
}
