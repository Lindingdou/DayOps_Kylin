using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.PointCloud;

/// <summary>
/// 「缺数据」交互提醒：点云算子必须先有点云、网格算子必须先有三角网 —— 缺了就在这里说清楚
/// **缺什么、为什么、下一步点哪儿**，并直接给出去路（加载点云 / 去建三角网），而不是只在
/// 信息栏留一行字让用户自己找入口。
///
/// 为什么要这一层：这些命令都在「当前点云 / 选中三角网」上跑，没有输入时原版只往命令行
/// Echo 一句红字；移到 Kylin 后底部那一行更容易被忽略，用户的观感就是"点了没反应"。
/// 代码构建，无 XAML。
/// </summary>
internal sealed class PcNeedDataDialog : Window
{
    /// <summary>缺哪种输入。</summary>
    public enum Need { Cloud, Mesh }

    /// <summary>用户的选择：执行出路 / 取消。</summary>
    public enum Act { Cancel, Go }

    private PcNeedDataDialog(string cmdName, Need need)
    {
        Title = cmdName;
        Width = 460; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        bool cloud = need == Need.Cloud;
        string head = cloud ? $"「{cmdName}」需要先有点云" : $"「{cmdName}」需要先有三角网";
        string body = cloud
            ? "该命令在「当前点云」上运行。场景里还没有点云 —— 先加载一份（LAS / CSV / TXT / XYZ），"
              + "之后各点云算子都以它为输入；算子跑完会自动把「当前点云」切到结果上，可一路串下去。"
            : "该命令要在三角网上运行。场景里还没有三角网 —— 先用「2.5D TIN」把点云建成面，"
              + "再回来执行；也可以用「导入三角网」读入已有的 OFF 网格。";
        string goText = cloud ? "加载点云…" : "去建三角网（2.5D TIN）";

        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = Brush.Parse("#FFF4E5"), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "!", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C87A00"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = head, FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = body, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });

        var go = new Button { Content = goText, MinWidth = 150, IsDefault = true };
        go.Classes.Add("primary");
        go.Click += (_, _) => Close(Act.Go);
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
        cancel.Click += (_, _) => Close(Act.Cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(18, 16), Spacing = 14,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { go, cancel },
                },
            },
        };
    }

    /// <summary>
    /// 弹提醒并等用户选。自检模式默认直接返回 Cancel（脚本不能卡在模态框上）；
    /// PITMINE_SHOWDIALOG=1 时照常弹，供截图核对。
    /// </summary>
    public static async Task<Act> AskAsync(Window owner, string cmdName, Need need)
    {
        if (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 }
            && Environment.GetEnvironmentVariable("PITMINE_SHOWDIALOG") is not { Length: > 0 })
            return Act.Cancel;
        try { return await new PcNeedDataDialog(cmdName, need).ShowDialog<Act>(owner); }
        catch (Exception ex)
        {
            PitMine3D.Kylin.CrashLog.Write("点云", $"{cmdName} 缺数据提醒弹窗失败: {ex.Message}");
            return Act.Cancel;
        }
    }
}
