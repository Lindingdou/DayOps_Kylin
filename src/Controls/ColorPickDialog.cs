using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// 「选择颜色」对话框 —— 取色器下拉里「更多颜色…（自定义 RGB）」那一项。
/// 原版这里调的是 WinForms 的系统取色对话框(EntityColorPicker / 图层管理器 / 选项 三处同款);
/// 麒麟上没有 WinForms, 用 Avalonia 自带的 ColorView(色谱 + RGB/HSV/十六进制 三种输入)等价替代。
/// </summary>
public sealed class ColorPickDialog : Window
{
    private readonly ColorView _view;

    /// <summary>确定时选定的颜色；取消则不用看。</summary>
    public Color Picked => _view.Color;

    public ColorPickDialog(Color initial)
    {
        Title = "选择颜色";
        Width = 520; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _view = new ColorView
        {
            Color = initial,
            IsAlphaEnabled = false,          // 实体色不带透明度(透明度另有「特性」一栏)
            IsAlphaVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var ok = new Button { Content = "确定", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            Children =
            {
                _view,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };
    }

    /// <summary>弹窗取色；取消返回 null。自检(PITMINE_SELFTEST)下不弹窗、直接返回初值(同 PromptDialog)。</summary>
    public static async Task<Color?> PickAsync(Window owner, Color initial)
    {
        var d = new ColorPickDialog(initial);
        if (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 }) return initial;
        return await d.ShowDialog<bool>(owner) ? d.Picked : null;
    }
}
