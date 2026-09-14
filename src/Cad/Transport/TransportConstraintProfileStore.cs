using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PitMine3D.Kylin.Cad.Transport;

/// <summary>
/// <see cref="TransportConstraintProfiles"/> 的读写（持久化 + 导入导出），
/// 移植 <c>MineAssLib.Models.TransportConstraintProfileStore</c>。
///
/// 【镜像不变量】存容器时顺带把**当前方案**镜像回 <see cref="TransportConstraintProfiles.MirrorKey"/>
/// （= 老的单份配置键）。消费侧（布线 / 坑线 / 寻径口径）照旧只读那一个键，
/// 完全不必知道"方案"的存在。
///
/// ── Kylin 侧的实现差异（登记）──
/// 原版有两条落盘路径（注入了 <c>IUserSettings</c> 走平台配置，否则落 exe 边上的 Config/ 文件），
/// 是为了迁就 Plugin 那边没注入配置服务的调用。Kylin 的 <see cref="UserSettings.Current"/> 是进程内单例、
/// 永远可用，**故只保留平台配置这一条路** —— 少一条路就少一处"两份配置对不上"的来路。
/// （另：原版把 Config/ 放 exe 边上求便携，麒麟上程序常装在只读目录，<c>UserSettings</c> 已按用户目录落盘。）
/// </summary>
public static class TransportConstraintProfileStore
{
    /// <summary>JSON 选项：缩进（导出的文件人要看 / 要手改）；中文方案名不转义成 \uXXXX。</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读容器：没有（或读不出方案）返回 null，由调用方 seed。</summary>
    public static TransportConstraintProfiles? Load(UserSettings? settings)
    {
        try
        {
            var p = (settings ?? UserSettings.Current).Get<TransportConstraintProfiles>(
                TransportConstraintProfiles.SettingsKey);
            if (p is { Profiles.Count: > 0 }) return p;
        }
        catch
        {
            // 配置读坏不该拦着人开窗：当作"没有方案"，由调用方用老的单份配置 seed 出默认方案
        }
        return null;
    }

    /// <summary>读老的单份配置（升级迁移的来源；没有就返回一份全默认值）。</summary>
    public static TransportConstraintSettings LoadMirrorOrDefault(UserSettings? settings)
    {
        try
        {
            var s = (settings ?? UserSettings.Current).Get<TransportConstraintSettings>(
                TransportConstraintProfiles.MirrorKey);
            if (s != null) return s;
        }
        catch { }
        return new TransportConstraintSettings();
    }

    /// <summary>存容器 + 写镜像键。失败只回 false + 原因，不抛。</summary>
    public static bool TrySave(UserSettings? settings, TransportConstraintProfiles profiles, out string? error)
    {
        error = null;
        try
        {
            var st = settings ?? UserSettings.Current;
            st.Set(TransportConstraintProfiles.SettingsKey, profiles);
            // 镜像不变量：当前方案 → 老键，消费侧照旧读它
            var cur = profiles.Find(profiles.CurrentName);
            if (cur != null) st.Set(TransportConstraintProfiles.MirrorKey, cur);
            st.Flush();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>导出整个容器到 JSON 文件。</summary>
    public static bool TryExport(string path, TransportConstraintProfiles profiles, out string? error)
    {
        error = null;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(profiles, Json));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 从 JSON 文件导入。兼容两种文件：
    ///   ① 本对话框导出的方案容器（多方案）；
    ///   ② 一份裸的 <see cref="TransportConstraintSettings"/>（从别处扒下来的单份配置）——
    ///      按文件名当作一个方案收进来。
    /// 读不出任何方案返回 null + 原因。
    /// </summary>
    public static TransportConstraintProfiles? TryImport(string path, out string? error)
    {
        error = null;
        try
        {
            string json = File.ReadAllText(path);
            var box = JsonSerializer.Deserialize<TransportConstraintProfiles>(json, Json);
            if (box is { Profiles.Count: > 0 })
            {
                // 字典里混进 null 值（手改过的文件）会在后面炸，这里先剔掉
                foreach (var name in new List<string>(box.Profiles.Keys))
                    if (box.Profiles[name] == null) box.Profiles.Remove(name);
                if (box.Profiles.Count > 0) return box;
            }
            // 退一步：当作单份配置。注意 System.Text.Json 默认忽略未知字段，
            // 所以上面那次反序列化对"裸单份配置"也会成功，只是 Profiles 为空 —— 正好落到这里。
            // 但同理，**任何一个不相干的 JSON 也能"成功"反出一份全默认值的配置**，
            // 故先认一眼字段：根对象里得有约束配置的标志性字段，才当它是单份配置。
            using var doc = JsonDocument.Parse(json);
            bool looksLikeSettings = doc.RootElement.ValueKind == JsonValueKind.Object
                && (doc.RootElement.TryGetProperty(nameof(TransportConstraintSettings.TruckClass), out _)
                 || doc.RootElement.TryGetProperty(nameof(TransportConstraintSettings.MaxGradePct), out _));
            if (!looksLikeSettings)
            { error = "文件里没有可识别的约束配置（既不是方案文件，也不是单份配置）"; return null; }

            var one = JsonSerializer.Deserialize<TransportConstraintSettings>(json, Json);
            if (one == null) { error = "文件里没有可识别的约束配置"; return null; }
            var wrap = new TransportConstraintProfiles();
            string nm = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(nm)) nm = TransportConstraintProfiles.DefaultName;
            wrap.Profiles[nm] = one;
            wrap.CurrentName = nm;
            return wrap;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
