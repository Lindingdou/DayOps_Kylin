using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PitMine3D.Kylin.Views;

namespace PitMine3D.Kylin;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 启动欢迎界面：先弹出来显示进度，主窗口就绪后由 MainWindow 关闭它（同原版 App/MainWindow 的分工）。
            var splash = new Views.Startup.StartupSplash();
            splash.Show();
            splash.Report(0.05, "正在创建主窗口…");
            desktop.MainWindow = new MainWindow(splash);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
