using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Transport;

/// <summary>
/// 「约束条件设置」的方案容器（user 全局配置，持久化键 <see cref="SettingsKey"/>）。
///
/// 为什么要方案：<see cref="TransportConstraintSettings"/> 原先只有 "transport.constraints" 一份全局配置，
/// 一台机器做两个矿（或同一个矿的两套比选口径）就会互相覆盖。本容器把"一份"扩成"多份 + 当前是哪一份"。
///
/// 【镜像不变量】<see cref="MirrorKey"/>（= 老的 "transport.constraints"）恒等于当前方案的内容。
/// 消费侧（MineAssLibPlugin 的运量驱动布线 / 直线坑线 / 坑线落地等）照旧只读那一个键，
/// 完全不必知道"方案"的存在；老版本升上来的单份配置由 <see cref="EnsureUsable"/> 迁成
/// <see cref="DefaultName"/>，不会丢。
///
/// 范式照 <c>PitMine.Platform.Transport.LoadUnloadPointSet</c>：纯 POCO（可被 System.Text.Json 持久化）
/// + <c>const SettingsKey</c>；另附几个只动自身字典的小方法，供对话框做增 / 删 / 改名 / 导入合并。
/// </summary>
public sealed class TransportConstraintProfiles
{
    /// <summary>IUserSettings 持久化键（整个方案容器）。</summary>
    public const string SettingsKey = "transport.constraint.profiles";

    /// <summary>镜像键：老的单份配置键，恒等于当前方案，消费侧只认这一个。</summary>
    public const string MirrorKey = "transport.constraints";

    /// <summary>老单份配置迁移过来的方案名，也是空容器的首个方案名。</summary>
    public const string DefaultName = "默认方案";

    /// <summary>方案名 → 一整份约束配置。</summary>
    public Dictionary<string, TransportConstraintSettings> Profiles { get; set; } = new();

    /// <summary>当前选中的方案名（<see cref="MirrorKey"/> 镜像的就是它）。</summary>
    public string CurrentName { get; set; } = DefaultName;

    /// <summary>按名取方案；名字为空或不存在返回 null。</summary>
    public TransportConstraintSettings? Find(string? name)
        => !string.IsNullOrWhiteSpace(name) && Profiles.TryGetValue(name!, out var s) ? s : null;

    /// <summary>方案名清单（序数排序）。Dictionary 不保证顺序，下拉要稳定就得自己排。</summary>
    public List<string> SortedNames()
    {
        var names = new List<string>(Profiles.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// 把容器补成"可用状态"并返回当前方案：
    ///   ① 空容器 → 用 <paramref name="fallback"/>（老 "transport.constraints" 那份）建
    ///      <see cref="DefaultName"/>，这就是升级迁移，用户原配置原样保住；
    ///   ② <see cref="CurrentName"/> 指向不存在的方案 → 回落到排序后的首个方案。
    /// </summary>
    public TransportConstraintSettings EnsureUsable(TransportConstraintSettings fallback)
    {
        if (Profiles.Count == 0)
        {
            Profiles[DefaultName] = fallback;
            CurrentName = DefaultName;
            return fallback;
        }
        var cur = Find(CurrentName);
        if (cur != null) return cur;
        CurrentName = SortedNames()[0];
        return Profiles[CurrentName];
    }

    /// <summary>写入 / 覆盖一个方案（名字为空当作没写）。</summary>
    public void Put(string? name, TransportConstraintSettings s)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Profiles[name!] = s;
    }

    /// <summary>改名。新名重名 / 任一名为空 / 老名不存在都不动，返回是否改成。</summary>
    public bool Rename(string? oldName, string? newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return false;
        if (!Profiles.TryGetValue(oldName!, out var s)) return false;
        if (Profiles.ContainsKey(newName!)) return false;
        Profiles.Remove(oldName!);
        Profiles[newName!] = s;
        if (string.Equals(CurrentName, oldName, StringComparison.Ordinal)) CurrentName = newName!;
        return true;
    }

    /// <summary>删除一个方案。只剩一个时拒绝删（容器不能空），返回是否删掉。</summary>
    public bool Remove(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Profiles.Count <= 1) return false;
        if (!Profiles.Remove(name!)) return false;
        if (string.Equals(CurrentName, name, StringComparison.Ordinal)) CurrentName = SortedNames()[0];
        return true;
    }

    /// <summary>取一个不与现有方案重名的名字："甲" 已存在则给 "甲(2)"、"甲(3)"…</summary>
    public string UniqueName(string? baseName)
    {
        string b = string.IsNullOrWhiteSpace(baseName) ? DefaultName : baseName!.Trim();
        if (!Profiles.ContainsKey(b)) return b;
        for (int i = 2; i < 1000; i++)
        {
            string cand = $"{b}({i})";
            if (!Profiles.ContainsKey(cand)) return cand;
        }
        return $"{b}({Guid.NewGuid():N})";
    }

    /// <summary>
    /// 合并另一份容器（导入用）：重名的自动加 "(2)"、"(3)"… 后缀，绝不覆盖已有方案。
    /// 返回实际落下的方案名清单（供调用方回显 / 选中最后一个）。
    /// <para><b>忠实逐字移植</b> <c>MineAssLib.Models.TransportConstraintProfiles</c>（仅改命名空间）。
/// 持久化改走 Kylin 的 <c>UserSettings</c>（JSON 文件），键名与原版一致。</para>
/// </summary>
    public List<string> MergeFrom(TransportConstraintProfiles? other)
    {
        var added = new List<string>();
        if (other == null) return added;
        foreach (var name in other.SortedNames())
        {
            var s = other.Profiles[name];
            if (s == null) continue;
            string dst = UniqueName(name);
            Profiles[dst] = s;
            added.Add(dst);
        }
        return added;
    }
}
