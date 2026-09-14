using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Mining;

/// <summary>
/// 露天矿采剥过程演示的控制面板（忠实原 <c>BlockModelLib.Simulation.MiningSimWindow</c>）：
/// 播放/暂停 · 重置 · 重新定位 · 循环 · 反向 · 速度滑杆 · 进度条 + 四个读数
/// （工作面推进 / 采煤·剥离 / 外排土量 / 内排土量）。非模态单实例。
///
/// 与原版的两处入口差异（已在 <see cref="MiningSimSession"/> 里说明原委）：
///   · 原版从桌面「块体」目录读三个**固定文件名**的 .blk；这里改成在**已加载的块体模型**里挑三个下拉，
///     不绑死文件名，也顺带支持 PMB/CSV 等 Kylin 其它来源。
///   · 原版打开即自动加载并播放；这里选完模型点「装配」再播 —— 场上有哪些模型是用户自己定的，
///     开窗就擅自挑一个跑起来会把人吓一跳。
///
/// 登记的差异：原版画面里还有**电铲模型 / 运距对位虚线 / 正在采的块体高亮**三样装饰件，
/// 那是 native 体素通道逐帧注册小立方体画出来的；本移植的演示驱动的是场景块体自身的可见性，
/// 这三样未做（要做的话应按 Kylin 自己的画法用场景线/着色重来，属另一件事），记录。
/// </summary>
internal sealed class MiningSimWindow : Window
{
    private readonly Func<List<BlockModelMeta>> _models;
    private readonly Action _refresh;          // 重渲块体显示
    private readonly Action _zoomExtents;      // 「重新定位」：缩放到演示模型范围
    private readonly Action<string> _echo;

    private readonly MiningSimSession _session = new();
    private readonly MiningSim.Playback _playback = new();
    private readonly DispatcherTimer _timer;

    private readonly ComboBox _cmbPit = new() { Width = 240 };
    private readonly ComboBox _cmbExt = new() { Width = 240 };
    private readonly ComboBox _cmbInt = new() { Width = 240 };
    private readonly Button _btnSetup = new() { Content = "装配", Padding = new Thickness(14, 4) };

    private readonly Slider _sldProgress = new() { Minimum = 0, Maximum = 1, Value = 0, Width = 360, IsEnabled = false };
    private readonly TextBlock _txtPercent = new() { Text = "0%", MinWidth = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _btnPlay = new() { Content = "播放", Width = 78, IsEnabled = false };
    private readonly Button _btnReset = new() { Content = "重置", Width = 78, IsEnabled = false };
    private readonly Button _btnZoom = new() { Content = "重新定位", Width = 90, IsEnabled = false };
    private readonly CheckBox _chkLoop = new() { Content = "循环" };
    private readonly CheckBox _chkReverse = new() { Content = "反向" };
    private readonly Slider _sldSpeed = new() { Minimum = 0.25, Maximum = 4, Value = 1, Width = 160 };
    private readonly TextBlock _txtSpeed = new() { Text = "1.0×", MinWidth = 44, VerticalAlignment = VerticalAlignment.Center };

    private readonly TextBlock _txtPhase = new() { Text = "选好三个模型后点「装配」。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _txtAdvance = new() { Text = "—" };
    private readonly TextBlock _txtStrip = new() { Text = "—" };
    private readonly TextBlock _txtMined = new() { Text = "—" };
    private readonly TextBlock _txtExt = new() { Text = "—" };
    private readonly TextBlock _txtInt = new() { Text = "—" };

    private bool _suppressSlider;

    public MiningSimWindow(Func<List<BlockModelMeta>> models, Action refresh, Action zoomExtents, Action<string> echo)
    {
        _models = models; _refresh = refresh; _zoomExtents = zoomExtents; _echo = echo;

        Title = "采剥过程演示";
        Width = 560; Height = 560;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MiningSim.Playback.TickMs) };
        _timer.Tick += (_, _) => OnTick();

        BuildUi();
        ReloadModels();

        _btnSetup.Click += (_, _) => Setup();
        _btnPlay.Click += (_, _) => { _playback.Toggle(); SyncPlayButton(); };
        _btnReset.Click += (_, _) => { _playback.Reset(); SyncPlayButton(); SetProgress(0); };
        _btnZoom.Click += (_, _) => _zoomExtents();
        _chkLoop.IsCheckedChanged += (_, _) => _playback.Loop = _chkLoop.IsChecked == true;
        _chkReverse.IsCheckedChanged += (_, _) => { if (_session.Ready) SetProgress(_playback.Progress); };   // 即时按当前进度重算
        _sldSpeed.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase_ValueProperty) return;
            _playback.Speed = _sldSpeed.Value;
            _txtSpeed.Text = $"{_sldSpeed.Value:0.0}×";
        };
        _sldProgress.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase_ValueProperty || _suppressSlider || !_session.Ready) return;
            _playback.Seek(_sldProgress.Value);   // 拖动即暂停(原版口径)
            SyncPlayButton();
            SetProgress(_playback.Progress);
        };
        Closed += (_, _) => { _timer.Stop(); _session.Reset(); _refresh(); };
    }

    private static readonly AvaloniaProperty RangeBase_ValueProperty = Slider.ValueProperty;

    private void BuildUi()
    {
        static TextBlock Head(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        static TextBlock Dim(string t) => new() { Text = t, FontSize = 11, Foreground = Brush.Parse("#666"), TextWrapping = TextWrapping.Wrap };

        Control Row(string label, Control c)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2) };
            sp.Children.Add(new TextBlock { Text = label, Width = 66, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(c);
            return sp;
        }
        Control Readout(string label, TextBlock value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2) };
            sp.Children.Add(new TextBlock { Text = label, Width = 96, Foreground = Brush.Parse("#666") });
            value.FontWeight = FontWeight.SemiBold;
            sp.Children.Add(value);
            return sp;
        }

        var pick = new StackPanel();
        pick.Children.Add(Row("采场:", _cmbPit));
        pick.Children.Add(Row("外排土场:", _cmbExt));
        pick.Children.Add(Row("内排土场:", _cmbInt));
        pick.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(66, 6, 0, 0),
            Children = { _btnSetup, new Button { Content = "刷新列表", Padding = new Thickness(14, 4), Command = new SimpleCommand(ReloadModels) } },
        });

        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        transport.Children.Add(_btnPlay);
        transport.Children.Add(_btnReset);
        transport.Children.Add(_btnZoom);
        transport.Children.Add(_chkLoop);
        transport.Children.Add(_chkReverse);

        var speed = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        speed.Children.Add(new TextBlock { Text = "速度", VerticalAlignment = VerticalAlignment.Center });
        speed.Children.Add(_sldSpeed);
        speed.Children.Add(_txtSpeed);

        var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        progress.Children.Add(_sldProgress);
        progress.Children.Add(_txtPercent);

        var panel = new StackPanel
        {
            Margin = new Thickness(14),
            Children =
            {
                new TextBlock { Text = "采剥过程演示", FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#2B579A") },
                Dim("三标段带并行下推：上台阶超前、下台阶滞后（剥采平行）；内排严格跟在工作面之后回填采空区，外排渐进堆进。"),

                Head("参与演示的块体模型"), pick,
                Head("播放"), progress, transport, speed,

                Head("读数"),
                Readout("工作面推进", _txtStrip),
                Readout("采煤 / 剥离", _txtMined),
                Readout("外排土量", _txtExt),
                Readout("内排土量", _txtInt),
                Readout("推进方向", _txtAdvance),

                new Border
                {
                    Background = Brush.Parse("#EEF3FA"), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6), Margin = new Thickness(0, 10, 0, 0), Child = _txtPhase,
                },
            },
        };
        Content = new ScrollViewer { Content = panel };
    }

    /// <summary>刷新三个下拉的候选（块体模型是随时可以再导入的）。</summary>
    internal void ReloadModels()
    {
        var list = _models();
        var names = list.Select(m => m.Name).ToList();
        void Fill(ComboBox cb, bool allowNone, string? keep)
        {
            var items = new List<string>();
            if (allowNone) items.Add("（不用）");
            items.AddRange(names);
            cb.ItemsSource = items;
            int idx = keep != null ? items.IndexOf(keep) : -1;
            cb.SelectedIndex = idx >= 0 ? idx : 0;
        }
        Fill(_cmbPit, false, _cmbPit.SelectedItem as string);
        Fill(_cmbExt, true, _cmbExt.SelectedItem as string);
        Fill(_cmbInt, true, _cmbInt.SelectedItem as string);
        if (list.Count == 0) _txtPhase.Text = "场上还没有块体模型 —— 先用「导入块体」载入采场/排土场，再回来装配。";
    }

    private BlockModelMeta? Pick(ComboBox cb)
    {
        if (cb.SelectedItem is not string n || n == "（不用）") return null;
        return _models().FirstOrDefault(m => m.Name == n);
    }

    internal bool Setup()
    {
        _playback.Reset();
        SyncPlayButton();
        if (!_session.Setup(Pick(_cmbPit), Pick(_cmbExt), Pick(_cmbInt), out string err))
        {
            _txtPhase.Text = err;
            SetControlsEnabled(false);
            _echo(err);
            return false;
        }
        _txtAdvance.Text = _session.AdvanceLabel;
        SetControlsEnabled(true);
        SetProgress(0);          // 采场全在、内外排隐藏
        _timer.Start();
        _txtPhase.Text = _session.StatusLabel;
        _echo($"采剥演示已装配：{_session.StatusLabel}");
        return true;
    }

    private void SetControlsEnabled(bool on)
    {
        _sldProgress.IsEnabled = on; _btnPlay.IsEnabled = on; _btnReset.IsEnabled = on; _btnZoom.IsEnabled = on;
    }

    private void SyncPlayButton() => _btnPlay.Content = _playback.Playing ? "暂停" : "播放";

    private void OnTick()
    {
        if (!_playback.Playing || !_session.Ready) return;
        double p = _playback.Tick();
        SetProgress(p);
        SyncPlayButton();     // 不循环跑到头时时钟会自己停, 按钮要跟着回「播放」
    }

    private void SetProgress(double p)
    {
        _suppressSlider = true;
        _sldProgress.Value = p;
        _suppressSlider = false;
        if (!_session.Ready) return;
        var r = _session.Step(p, _chkReverse.IsChecked == true);
        _refresh();
        UpdateReadout(r);
    }

    private void UpdateReadout(MiningSim.Readout r)
    {
        _txtPercent.Text = $"{r.Progress * 100:0}%";
        _txtPhase.Text = r.Phase + " · " + r.PanelInfo;
        _txtStrip.Text = $"{r.MinedStrips} / {r.TotalStrips}（{MiningSim.PanelCount} 标段同步）";
        _txtMined.Text = $"采煤 {r.CoalVolM3 / 1e6:0.00} · 剥离 {r.WasteVolM3 / 1e6:0.0} Mm³";
        _txtExt.Text = _session.HasExternal ? $"{r.ExtVolM3 / 1e6:0.0} Mm³" : "—（未选）";
        _txtInt.Text = _session.HasInternal ? $"{r.IntVolM3 / 1e6:0.0} Mm³" : "—（未选）";
    }

    /// <summary>供调用方缩放取范围。</summary>
    internal IEnumerable<BlockModelMeta> SessionModels() => _session.Models();

    // ── 自检钩子用（脚本点不了控件）──
    internal bool SelectPit(string name) => SelectIn(_cmbPit, name);
    internal bool SelectExt(string name) => SelectIn(_cmbExt, name);
    internal bool SelectInt(string name) => SelectIn(_cmbInt, name);
    private static bool SelectIn(ComboBox cb, string name)
    {
        if (cb.ItemsSource is not IEnumerable<string> items) return false;
        var list = items.ToList();
        int i = list.IndexOf(name);
        if (i < 0) return false;
        cb.SelectedIndex = i;
        return true;
    }
    internal void SetReverse(bool on) => _chkReverse.IsChecked = on;
    internal void SeekTo(double p) { _playback.Seek(p); SyncPlayButton(); SetProgress(p); }
    internal string PhaseText => _txtPhase.Text ?? "";
    internal string MinedText => _txtMined.Text ?? "";
    internal string StripText => _txtStrip.Text ?? "";

    /// <summary>给「刷新列表」按钮用的最小 ICommand（这窗没有 VM，不值当为一个按钮引一套）。</summary>
    private sealed class SimpleCommand : System.Windows.Input.ICommand
    {
        private readonly Action _run;
        public SimpleCommand(Action run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _run();
    }
}
