using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 作业面工艺流程编辑（穿 → 爆 → 采 → 运 → 排；移植原 <c>FaceProcessDialog</c>）。
///
/// <para><b>改完就能看见任务编制那边会拿到什么数</b>：底部实时算出月穿孔延米 / 孔数 / 炸药量 /
/// 爆破次数。孔网从 7×8 改成 6×7，延米当场变 —— 不用等排完产再回头找哪里不对。</para>
///
/// <para><b>「算不出来」和「不用穿」分开显示</b>：台阶高取不到时月延米是 0，
/// 而免爆面的月延米也是 0 —— 两者在报表上一模一样，所以这里必须用文字把它们分开。</para>
/// </summary>
internal sealed class FaceProcessDialog : Window
{
    private readonly WorkingFace _face;
    private readonly FaceProcessChain _work;      // 编辑副本：取消要能真的取消
    private readonly double _benchFallbackM;      // 库表 working_face.bench_height_m
    private bool _loading = true;

    /// <summary>按「确定」= true（已写回作业面）；取消/关闭 = false。</summary>
    public bool DialogResult { get; private set; }

    private readonly TextBlock titleText = new() { Text = "作业面工艺流程", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeight.Bold };
    private readonly ComboBox drillModeBox, loadMethodBox, dumpMethodBox;
    private readonly TextBox spacingBox = Num(), burdenBox = Num(), diaBox = Num(), benchBox = Num(), subDrillBox = Num(),
                             powderBox = Num(), leadBox = Num(), batchBox = Num(), widthBox = Num();
    private readonly CheckBox crusherBox = new() { Content = new TextBlock { Text = "经破碎站转运（半连续）" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
    private readonly TextBlock benchSrcText = RoadUi.Hint("", 12), derivedText = RoadUi.Hint("", 12.5), statusText = RoadUi.Hint("", 12);

    private static TextBox Num() => new() { Width = 70, Height = 26, Margin = new Thickness(0, 0, 14, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    /// <param name="face">要编辑的作业面。</param>
    /// <param name="benchHeightFallbackM">库表台阶高（按 FaceCode 取到的）。0 = 没取到。</param>
    public FaceProcessDialog(WorkingFace face, double benchHeightFallbackM = 0)
    {
        _face = face ?? throw new ArgumentNullException(nameof(face));
        _work = (face.Process ?? new FaceProcessChain()).Copy();
        _benchFallbackM = Math.Max(0, benchHeightFallbackM);

        Title = "作业面工艺流程 — 穿爆采运排";
        Width = 720; Height = 700; MinWidth = 600; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        // 300 不是拍的：190 时"按物料判（硬岩/夹矸要，表土/煤不要）"整条被切在"表土/"，连下拉箭头都挤没了。
        drillModeBox = PlanUi.Combo(new[] { "按物料判（硬岩/夹矸要，表土/煤不要）", "强制穿爆", "强制免爆" }, 0, 300);
        loadMethodBox = PlanUi.Combo(new[] { "正铲", "反铲", "前装机" }, 0, 150); loadMethodBox.Margin = new Thickness(0, 0, 14, 0);
        dumpMethodBox = PlanUi.Combo(new[] { "卡车排卸 + 推土机推排", "前装机倒堆", "排土机 / 排土犁" }, 0, 190);

        // ── 标题栏（橙色渐变，与「确定开采程序」同族）
        var head = new StackPanel();
        head.Children.Add(titleText);
        head.Children.Add(new TextBlock
        {
            Text = "工艺说「这道工序干不干、按什么参数干」；设备型号说「用哪台机器」——两件事。同一台电铲在表土面不穿爆、在硬岩面要穿爆。",
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE8, 0xD6)), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        var header = new Border
        {
            Padding = new Thickness(16, 10), Child = head,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0xFB, 0x92, 0x3C), 0), new GradientStop(Color.FromRgb(0xEA, 0x58, 0x0C), 1) },
            },
        };

        // ── 主体
        var body = new StackPanel();
        var g1 = new StackPanel();
        g1.Children.Add(H(new Thickness(0, 0, 0, 8), Lbl("是否穿爆"), drillModeBox));
        g1.Children.Add(H(new Thickness(0, 0, 0, 8), Lbl("孔距 a(m)"), spacingBox, Lbl("排距 b(m)"), burdenBox, Lbl("孔径(mm)"), diaBox));
        ToolTip.SetTip(benchBox, "0 = 用 working_face 台账里这个面的台阶高（需先填面编号）");
        g1.Children.Add(H(default, Lbl("台阶高(m)"), benchBox, Lbl("超深(m)"), subDrillBox));
        // ★ 单独一行，不放进上面那个横排里：横排给子元素无限宽，TextWrapping 永远不生效 —— 台账查不到台阶高时那句长文案会被从中间切断。
        benchSrcText.Margin = new Thickness(0, 6, 0, 0);
        g1.Children.Add(benchSrcText);
        body.Children.Add(PlanUi.Group("① 穿孔", g1));

        ToolTip.SetTip(leadBox, "0 = 用全局缺省。硬岩大区爆破和煤层控制爆破本来就不是一个数");
        ToolTip.SetTip(batchBox, "0 = 不限");
        body.Children.Add(PlanUi.Group("② 爆破（不排设备，排的是量与窗口）",
            H(default, Lbl("单耗(kg/m³)"), powderBox, Lbl("超前期(工日)"), leadBox, Lbl("单次规模(万m³)"), batchBox)));

        ToolTip.SetTip(widthBox, "0 = 用 working_face 台账里的采宽");
        body.Children.Add(PlanUi.Group("③ 采装", H(default, Lbl("采装方式"), loadMethodBox, Lbl("采宽(m)"), widthBox)));

        body.Children.Add(PlanUi.Group("④ 运输 ⑤ 排土", H(default, crusherBox, Lbl("排土方式"), dumpMethodBox), new Thickness(0)));

        var scroll = new ScrollViewer { Content = body, Padding = new Thickness(14, 12), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

        // 月工序量预览：改一个参数就能看见任务编制那边会拿到什么数
        var pv = new StackPanel();
        pv.Children.Add(new TextBlock { Text = "月工序量（按本面备采储量折算 —— 任务编制直接吃这几个数）", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 6) });
        derivedText.TextWrapping = TextWrapping.Wrap;
        pv.Children.Add(derivedText);
        var preview = new Border { Padding = new Thickness(14, 10), BorderThickness = new Thickness(0, 1, 0, 1), Child = pv };
        RoadUi.Theme(preview, Border.BackgroundProperty, "Theme.Panel.Background");
        RoadUi.Theme(preview, Border.BorderBrushProperty, "Theme.Panel.Border");

        // 按钮 Dock=Right、状态文字【不 Dock】吃剩余宽 —— 反过来的话，长一点的告警会把按钮挤出去，而告警本身正是最需要读全的东西。
        var foot = new DockPanel { LastChildFill = true };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        btns.Children.Add(RoadUi.Btn("恢复缺省", OnReset, 80));
        var ok = RoadUi.Btn("确定", OnOk, 80); ok.IsDefault = true; ok.Margin = new Thickness(8, 0, 0, 0); btns.Children.Add(ok);
        var cancel = RoadUi.Btn("取消", OnCancel, 70); cancel.IsCancel = true; cancel.Margin = new Thickness(8, 0, 0, 0); btns.Children.Add(cancel);
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Right); foot.Children.Add(btns);
        statusText.VerticalAlignment = VerticalAlignment.Center; statusText.TextWrapping = TextWrapping.Wrap; foot.Children.Add(statusText);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        Grid.SetRow(header, 0); Grid.SetRow(scroll, 1); Grid.SetRow(preview, 2);
        var footB = new Border { Padding = new Thickness(14, 10), Child = foot }; Grid.SetRow(footB, 3);
        root.Children.Add(header); root.Children.Add(scroll); root.Children.Add(preview); root.Children.Add(footB);
        Content = root;

        titleText.Text = $"作业面工艺流程 — {_face.Name}（{_face.MaterialName}）";
        Load();
        _loading = false;
        Recalc();

        foreach (var tb in new[] { spacingBox, burdenBox, diaBox, benchBox, subDrillBox, powderBox, leadBox, batchBox, widthBox })
            tb.TextChanged += (_, _) => OnAnyChanged();
        foreach (var cb in new[] { drillModeBox, loadMethodBox, dumpMethodBox })
            cb.SelectionChanged += (_, _) => OnAnyChanged();
        crusherBox.IsCheckedChanged += (_, _) => OnAnyChanged();
    }

    private static StackPanel H(Thickness margin, params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    private void Load()
    {
        drillModeBox.SelectedIndex = _work.DrillEnabled switch { null => 0, true => 1, false => 2 };
        spacingBox.Text = F(_work.HoleSpacingM);
        burdenBox.Text = F(_work.HoleBurdenM);
        diaBox.Text = F(_work.HoleDiameterMm);
        benchBox.Text = F(_work.BenchHeightM);
        subDrillBox.Text = F(_work.SubDrillM);
        powderBox.Text = F(_work.PowderFactorKgPerM3);
        leadBox.Text = _work.BlastLeadDays.ToString(CultureInfo.InvariantCulture);
        batchBox.Text = F(_work.BlastBatchWanM3);
        loadMethodBox.SelectedIndex = (int)_work.LoadMethod;
        widthBox.Text = F(_work.MiningWidthM);
        crusherBox.IsChecked = _work.ViaCrusher;
        dumpMethodBox.SelectedIndex = (int)_work.DumpMethod;

        benchSrcText.Text = _benchFallbackM > 0
            ? $"（填 0 则用台账的 {_benchFallbackM:0.##}m）"
            : "（◆ 台账里没查到这个面的台阶高 —— 填 0 的话穿孔量算不出来）";
    }

    /// <summary>把界面读回编辑副本。<b>读不出来的数一律保留原值</b>，不要悄悄变成 0。</summary>
    private void Harvest()
    {
        _work.DrillEnabled = drillModeBox.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        _work.HoleSpacingM = D(spacingBox.Text, _work.HoleSpacingM);
        _work.HoleBurdenM = D(burdenBox.Text, _work.HoleBurdenM);
        _work.HoleDiameterMm = D(diaBox.Text, _work.HoleDiameterMm);
        _work.BenchHeightM = D(benchBox.Text, _work.BenchHeightM);
        _work.SubDrillM = D(subDrillBox.Text, _work.SubDrillM);
        _work.PowderFactorKgPerM3 = D(powderBox.Text, _work.PowderFactorKgPerM3);
        _work.BlastLeadDays = (int)Math.Round(D(leadBox.Text, _work.BlastLeadDays));
        _work.BlastBatchWanM3 = D(batchBox.Text, _work.BlastBatchWanM3);
        _work.LoadMethod = (LoadMethod)Math.Max(0, loadMethodBox.SelectedIndex);
        _work.MiningWidthM = D(widthBox.Text, _work.MiningWidthM);
        _work.ViaCrusher = crusherBox.IsChecked == true;
        _work.DumpMethod = (DumpMethod)Math.Max(0, dumpMethodBox.SelectedIndex);
    }

    private void OnAnyChanged()
    {
        if (_loading) return;
        Harvest();
        Recalc();
    }

    /// <summary>
    /// 实时算月工序量。基数 = 本面备采储量（万t）→ 实方（按物料密度）。
    /// <para>备采储量为 0 时<b>明说"没有基数"</b>，不拿一个假量算出一串看起来像那么回事的数。</para>
    /// </summary>
    private void Recalc()
    {
        var spec = PlanMaterialCatalog.Resolve(_face.MaterialCode);
        bool drill = _work.ResolveDrilling(_face.MaterialCode);

        // 万t → 万m³ → m³
        double monthM3 = spec.FromTonnage(_face.AvailableReserveWanT) * 1e4;

        if (!drill)
        {
            derivedText.Text = $"本面按【免爆】走（{_face.MaterialName}）—— 没有穿孔与爆破工序量。"
                             + $"\n采装：{FaceProcessChain.LoadMethodText(_work.LoadMethod)}"
                             + (_work.ViaCrusher ? " · 经破碎站" : "")
                             + $" · 排土：{FaceProcessChain.DumpMethodText(_work.DumpMethod)}";
        }
        else if (monthM3 <= 0)
        {
            derivedText.Text = "◆ 本面备采储量是 0 —— 没有基数，算不出月工序量。"
                             + "先在「确定开采程序」里「按份额分摊备采储量」，或直接填这一面的备采储量。";
        }
        else
        {
            double perHole = _work.VolumePerHoleM3(_benchFallbackM);
            if (perHole <= 0)
            {
                derivedText.Text = "◆ 单孔控制方量算不出来（台阶高或孔网缺参数）—— "
                                 + "月穿孔延米会是 0。**这个 0 的意思是「算不出」，不是「不用穿」**，"
                                 + "两者在报表上长得一模一样，别混。";
            }
            else
            {
                double holes = _work.MonthlyHoleCount(monthM3, _benchFallbackM);
                double meters = _work.MonthlyDrillMeters(monthM3, _benchFallbackM);
                double kg = _work.MonthlyPowderKg(monthM3);
                int blasts = _work.MonthlyBlastCount(monthM3);
                derivedText.Text =
                      $"基数 {monthM3 / 1e4:0.##} 万m³ 原位实方（备采 {_face.AvailableReserveWanT:0} 万t ÷ ρ{spec.InSituDensityTPerM3:0.##}）"
                    + $"\n穿孔：单孔控制 {perHole:0} m³ · 孔深 {_work.HoleDepthM(_benchFallbackM):0.#} m · "
                    + $"月 {holes:N0} 个孔 · {meters:N0} 延米"
                    + $"\n爆破：{kg / 1000:0.##} t 炸药（单耗 {_work.PowderFactorKgPerM3:0.###} kg/m³）· "
                    + $"{blasts} 次"
                    + (_work.BlastLeadDays > 0 ? $" · 超前 {_work.BlastLeadDays} 工日（面级）" : " · 超前期用全局值")
                    + $"\n采运排：{FaceProcessChain.LoadMethodText(_work.LoadMethod)}"
                    + (_work.ViaCrusher ? " → 破碎站" : "")
                    + $" → {FaceProcessChain.DumpMethodText(_work.DumpMethod)}";
            }
        }

        var bad = _work.CheckComputable(_face.Name, _face.MaterialCode, _benchFallbackM);
        statusText.Text = bad.Count == 0
            ? "参数完备，月工序量算得出来 ✓"
            : "◆ " + string.Join("\n◆ ", bad.Take(3));
    }

    private void OnReset()
    {
        var d = new FaceProcessChain();
        _work.DrillEnabled = d.DrillEnabled;
        _work.HoleSpacingM = d.HoleSpacingM; _work.HoleBurdenM = d.HoleBurdenM;
        _work.HoleDiameterMm = d.HoleDiameterMm; _work.BenchHeightM = d.BenchHeightM;
        _work.SubDrillM = d.SubDrillM; _work.PowderFactorKgPerM3 = d.PowderFactorKgPerM3;
        _work.BlastLeadDays = d.BlastLeadDays; _work.BlastBatchWanM3 = d.BlastBatchWanM3;
        _work.LoadMethod = d.LoadMethod; _work.MiningWidthM = d.MiningWidthM;
        _work.ViaCrusher = d.ViaCrusher; _work.DumpMethod = d.DumpMethod;

        _loading = true; Load(); _loading = false;
        Recalc();
        statusText.Text = "已恢复缺省（露天煤矿单斗-卡车工艺的常规值）—— 按「确定」才写回作业面。";
    }

    private void OnOk()
    {
        Harvest();
        _face.Process = _work;      // 只有按确定才写回 —— 取消要能真的取消
        DialogResult = true;
        Close(true);
    }

    private void OnCancel()
    {
        DialogResult = false;
        Close(false);
    }

    /// <summary>自检直通：按当前界面值确定（等价点「确定」）。</summary>
    internal void SelftestOk() => OnOk();
    internal string SelftestDerived => derivedText.Text ?? "";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>解析失败保留原值 —— 输入到一半（"7." / 空）时不该把参数清成 0。</summary>
    private static double D(string? s, double fallback)
        => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
           ? v : fallback;
}
