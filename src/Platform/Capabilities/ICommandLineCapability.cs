// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/ICommandLineCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 命令行 / 信息栏能力。所有模块向用户报告操作过程与结果的统一通道。
    ///
    /// 设计意图（2026-05-25 拍板）：
    ///   - 在此之前命令行（MainWindow commandPanel）只服务 Jig 的 placeholder 提示；
    ///     算子完成 / 失败的信息都流到 Logger，用户看不到 → 命令行"是摆设"。
    ///   - 该 capability 统一三类输出：
    ///       Echo       —— 写入历史区，常驻可滚动
    ///       SetPrompt  —— 设置输入框上方的灰色占位提示（含可选关键字）
    ///       ClearPrompt—— 算子结束清空提示
    ///       EnsureVisible — 关键事件强制浮出（用户可能折叠了面板）
    ///   - severity 用于上色 / 前缀符号：Info(默认) / Success(✓绿) / Warn(⚠橙) / Error(✗红)
    ///
    /// 错误处理（[[feedback-error-handling]]）：
    ///   - 业务失败一律走 Echo(msg, Error)，不弹 MessageBox。
    ///   - 调用方传裸文本即可，时间戳前缀由实现添加。
    /// </summary>
    public interface ICommandLineCapability
    {
        /// <summary>追加一行到命令行历史区（带时间戳 + severity 上色）。</summary>
        void Echo(string text, CommandLineSeverity severity = CommandLineSeverity.Info);

        /// <summary>设置命令输入框上方的灰色占位提示。</summary>
        void SetPrompt(string prompt);

        /// <summary>设置占位提示并附带可选关键字（显示为 "prompt [A/B/C]"）。</summary>
        void SetPrompt(string prompt, IReadOnlyList<string> keywords);

        /// <summary>清空占位提示。</summary>
        void ClearPrompt();

        /// <summary>确保命令行面板可见（用户可能折叠了，关键错误时强制浮出）。</summary>
        void EnsureVisible();
    }

    /// <summary>命令行消息的严重度，用于上色与前缀符号。</summary>
    public enum CommandLineSeverity
    {
        /// <summary>默认信息，无前缀，默认前景色。</summary>
        Info,

        /// <summary>成功完成，✓ 前缀 + 绿色。</summary>
        Success,

        /// <summary>警告，⚠ 前缀 + 橙色（用户应注意但操作可继续）。</summary>
        Warn,

        /// <summary>错误，✗ 前缀 + 红色（业务失败，触发 EnsureVisible）。</summary>
        Error
    }
}
