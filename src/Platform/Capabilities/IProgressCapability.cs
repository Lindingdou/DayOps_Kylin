// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IProgressCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System.Collections.Generic;
using System;
using System.Threading;

namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 进度能力：让任意模块的复杂计算把进度反映到宿主状态栏的全局进度条 + 取消按钮。
    /// 宿主实现并桥接到状态栏；所有上报由实现负责切回 UI 线程，业务可在后台线程随便调。
    ///
    /// 典型用法（业务侧，跑在 Task.Run 里不冻 UI）：
    /// <code>
    /// using var prog = progressCap.Begin("体素算量…");
    /// var r = await Task.Run(() => Build(..., prog.Token, progress: new ... (f => prog.Report(f))));
    /// if (prog.IsCancellationRequested) { /* 已取消 */ }
    /// </code>
    /// </summary>
    public interface IProgressCapability
    {
        /// <summary>
        /// 开始一个进度任务，返回句柄（Dispose 即结束/隐藏进度条）。
        /// indeterminate=true 显示转圈（无法预估百分比的复杂计算）；否则按 Report 的百分比走动。
        /// 进度条出现期间状态栏显示「取消」按钮，点它会取消本任务的 <see cref="IProgressTask.Token"/>。
        /// </summary>
        IProgressTask Begin(string title, bool indeterminate = false);

        /// <summary>当前是否有进度任务在跑（供"忙时拦住另一个重命令"的全局守卫）。</summary>
        bool IsBusy { get; }
    }

    /// <summary>进度任务句柄。Dispose 自动结束（隐藏进度条 / 撤下取消按钮）。推荐 using。</summary>
    public interface IProgressTask : IDisposable
    {
        /// <summary>上报进度。fraction 0..1（indeterminate 模式忽略）；status 为 null 时不动文字。可后台线程调。</summary>
        void Report(double fraction, string? status = null);

        /// <summary>本任务的取消令牌：传给算法（如 CancellationToken），状态栏「取消」按钮会触发它。</summary>
        CancellationToken Token { get; }

        /// <summary>是否已请求取消（= Token.IsCancellationRequested 的便捷读取）。</summary>
        bool IsCancellationRequested { get; }
    }
}
