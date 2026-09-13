using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「日常生产组织」页签的开窗通路 —— 对应原 <c>TaskLibPlugin.OpenWindow(key, factory)</c>：
/// 统一 modeless 单例开窗，同一个窗已开则前置，否则用 factory 造新窗；出错只写状态栏不抛。
///
/// <para>窗口本身取数一律走 <see cref="Data.EquipmentDataContext"/> 静态门面（GeoDatabase 打开时注入），
/// 所以这里先 <c>EnsureGeoDb()</c> 把库拉起来再开窗 —— 否则窗里所有走库的路径都静默退到兜底。</para>
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<Type, Window> _taskWindows = new();

    private void OpenTaskWindow<T>(Func<T> factory) where T : Window
    {
        try
        {
            // ★ EnsureGeoDb 首次返回 null（连接还没建好），命令会被重新派发一次；窗只在库就绪后开。
            if (EnsureGeoDb() == null) return;
            if (_taskWindows.TryGetValue(typeof(T), out var existing) && existing.IsVisible)
            {
                existing.Activate();
                StatusMsg.Text = $"{existing.Title} 已在前台";
                return;
            }
            var win = factory();
            _taskWindows[typeof(T)] = win;
            win.Closed += (_, _) => _taskWindows.Remove(typeof(T));
            GeoDb.GeoDbWindows.NoteLast(win);   // 让 @页面截图 自检拍得到
            win.Show(this);
        }
        catch (Exception ex)
        {
            StatusMsg.Text = $"[日常生产组织] 打开 {typeof(T).Name} 失败: {ex.Message}";
        }
    }
}
