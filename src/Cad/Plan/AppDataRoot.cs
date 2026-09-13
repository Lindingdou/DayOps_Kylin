using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// <b>中间文件的落地目录</b> —— 一律在<b>软件目录</b>下，不再往桌面写（2026-08-18 现场令）。
///
/// <para><b>为什么必须收口</b>：在此之前每个功能各写各的
/// （<c>桌面\采矿模型_中间文件</c>、<c>桌面\排土条带_中间文件</c>、<c>桌面\短期计划配置</c>…）。
/// 桌面上的东西<b>会被人随手挪、随手删，也会被下一次运行覆盖</b> ——
/// 实测 2026-08-18：判据用的那份 <c>现状面.tin</c> 被应用的一次中间输出改写成 96 个三角，
/// 16 条真数据判据当场全红，而报出来的话是"现状面与顶底板不相交"，
/// 把人引去查地质模型（地质模型好好的）。数据跟着软件走，这类事才管得住。</para>
///
/// <para><b>三级根目录与台账那条完全同源</b>（复用 <see cref="MonthlyUnitLedgerStore.ResolveRoot"/>）：
/// 软件目录 <c>&lt;app&gt;\Data\&lt;名&gt;</c> → 写不进去退 <c>%LOCALAPPDATA%\PitMine\&lt;名&gt;</c>（并说出来）
/// → 桌面上的老目录<b>只迁一次</b>（复制、不覆盖同名、老目录留指引）。
/// 两套目录逻辑各写一份的话，改了一处忘另一处，用户会在三个地方找同一份文件。</para>
///
/// <para><b>开发期的代价照旧</b>：<c>AppContext.BaseDirectory</c> = <c>bin\Debug\</c>，
/// 所以中间文件落在 <c>bin\Debug\Data\</c> —— 清空 bin\Debug 会把它们一起带走。</para>
/// </summary>
public static class AppDataRoot
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>软件目录下的候选位置（与 <c>pmgeo.db</c>、采掘单元台账同一个 <c>Data</c>）。</summary>
    public static string InstallRootOf(string dirName)
        => Path.Combine(AppContext.BaseDirectory, "Data", dirName);

    /// <summary>软件目录写不进去时的退路。</summary>
    public static string UserRootOf(string dirName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PitMine3D.Kylin", dirName);

    /// <summary>桌面上的老位置 —— <b>只用来迁移一次</b>。</summary>
    public static string LegacyDesktopOf(string dirName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), dirName);

    /// <summary>
    /// 取（并在首次调用时解析 + 迁移）某类中间文件的目录，目录已建好。
    /// <para><b>解析只做一次并缓存</b>：每次调用都探一遍写权限 + 扫一遍老目录，
    /// 在导出这种循环里会把磁盘打满；而且两次调用拿到不同答案时，文件会被写到两个地方。</para>
    /// </summary>
    public static string For(string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName)) dirName = "中间文件";
        lock (_lock)
        {
            if (_resolved.TryGetValue(dirName, out string? hit)) return hit;
            string root;
            string note = "";
            try
            {
                // ⚠ 这里**只借 ResolveRoot 的"选目录 + 写权限退路"**，迁移自己做：
                //   它的 Migrate 是台账专用的 —— 老目录里没有 `基表_采掘单元.csv` 就一个文件都不迁。
                //   通用中间目录（现状面.tin / 露头线csv / 排土条带json…）根本没有那个文件，
                //   直接复用的话迁移**永远不发生**，而目录切过去之后老数据就"消失"了
                //   （实测：判据的现状面 TIN 一下子找不到，16 条从红变成静默跳过 —— 比红更糟）。
                string primary = InstallRootOf(dirName), fallback = UserRootOf(dirName);
                if (CanWrite(primary)) root = primary;
                else if (CanWrite(fallback))
                {
                    root = fallback;
                    note = $"◆ 写不进软件目录（{primary}）—— 中间文件改存 {fallback}。"
                         + "（多半是软件装在 Program Files 下；要放回软件目录请以管理员身份运行，或给该目录写权限）";
                }
                else
                {
                    root = fallback;
                    note = $"◆ 软件目录（{primary}）和用户目录（{fallback}）**都写不进去** —— 中间文件保存会失败。";
                }
                string moved = MigrateFromDesktop(LegacyDesktopOf(dirName), root);
                if (moved.Length > 0) note = (note.Length > 0 ? note + "\n" : "") + moved;
            }
            catch (Exception ex)
            {
                // 解析本身出岔子也不能把功能弄死：退到软件目录，把原因留在 Note 里
                root = InstallRootOf(dirName);
                note = $"◆ 目录解析失败（{ex.GetType().Name}：{ex.Message}），退回 {root}。";
                try { Directory.CreateDirectory(root); } catch { }
            }
            _resolved[dirName] = root;
            _notes[dirName] = note;
            return root;
        }
    }

    /// <summary>
    /// 这个目录是怎么定下来的（换过位置 / 从桌面迁移过）。没有话说时是空串。
    /// <para><b>调用方要把它显示出来</b>：悄悄换目录和悄悄写失败一样坏 —— 人会在两个地方找同一份文件。</para>
    /// </summary>
    public static string NoteOf(string dirName)
    {
        For(dirName);
        lock (_lock) return _notes.TryGetValue(dirName, out string? n) ? n : "";
    }

    /// <summary>拼一个中间文件的完整路径（目录已建好）。</summary>
    public static string Path2(string dirName, string fileName) => Path.Combine(For(dirName), fileName);

    /// <summary>把桌面老目录里的文件迁到新目录（<b>复制、同名不覆盖</b>），并在老目录留一张指引。</summary>
    /// <remarks>
    /// 三条与台账那次搬家同源的纪律：
    /// <list type="number">
    ///   <item><b>复制不是移动</b> —— 一次目录切换不该让老位置上的原件消失；</item>
    ///   <item><b>同名不覆盖</b> —— 新目录里已经在用的那份永远赢（桌面上的多半是旧的）；</item>
    ///   <item><b>不按扩展名白名单迁</b> —— 白名单只覆盖"今天知道的类型"，
    ///         新增一种就静默漏一类数据。全迁，只排除 <c>.tmp</c>／写权限探针／指引本身。</item>
    /// </list>
    /// </remarks>
    private static string MigrateFromDesktop(string legacy, string root)
    {
        const string notice = "_中间文件已迁到软件目录.txt";
        try
        {
            if (string.IsNullOrWhiteSpace(legacy) || !Directory.Exists(legacy)) return "";
            if (string.Equals(Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),
                              Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase)) return "";

            Directory.CreateDirectory(root);
            int copied = 0, skipped = 0;
            foreach (var f in Directory.EnumerateFiles(legacy, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(f);
                if (string.Equals(name, notice, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith(".写权限探针_", StringComparison.Ordinal)) continue;

                string dst = Path.Combine(root, name);
                if (File.Exists(dst)) { skipped++; continue; }
                File.Copy(f, dst);
                copied++;
            }
            if (copied == 0) return "";

            try
            {
                File.WriteAllText(Path.Combine(legacy, notice),
                    "这个目录里的中间文件已经复制到软件目录：\r\n  " + root +
                    "\r\n\r\n从 2026-08-18 起，软件只往软件目录写中间文件（数据跟着软件走）。\r\n" +
                    "这里的原件保留着，确认新位置没问题后可以自行删除。\r\n");
            }
            catch { /* 指引写不上不影响迁移本身 */ }

            return $"· 已把桌面「{Path.GetFileName(legacy)}」里的 {copied} 个文件复制到软件目录：{root}"
                 + (skipped > 0 ? $"（另有 {skipped} 个同名的没覆盖，新目录里那份赢）" : "")
                 + "。桌面原件保留，并留了一张指引。";
        }
        catch (Exception ex) { return $"◆ 从桌面迁移中间文件失败（{ex.GetType().Name}：{ex.Message}）。"; }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".写权限探针_" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
