// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/MonthlyUnitLedgerStore.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Dump;   // DumpStripPlanner.Cell（Kylin 侧已按原版移植，见 Cad/Dump）

/// <summary>
/// 【按月管理的采掘单元台账】保存 / 删除 / 列月份 / 切换。
///
/// <para><b>两层，分工是硬的</b>：
/// <list type="bullet">
///   <item><b>基表</b> <c>基表_采掘单元.csv</c> —— 全量煤 + 岩 + 排土位置，
///     跟着采矿模型 / 排土条带重算刷新。它是<b>几何与量</b>的唯一来源。</item>
///   <item><b>月度台账</b> <c>2026-08.csv</c> —— 一个月一份，只存<b>那个月涉及的单元</b>
///     及其计划列（期次/状态/完成度/去向/运距/备注）。保存和删除的是它。</item>
/// </list>
/// 这样<b>重算模型不会毁掉已排的月份</b>（几何刷新，计划列按 UnitId 回填），
/// <b>删掉某个月也不会动到模型</b>。混成一张表的话，这两件事互相牵连：
/// 重算一次就要重排，删一个月就得在全量表里逐行清 —— 清漏了没人看得出来。</para>
///
/// <para><b>删除是可恢复的</b>：文件挪进 <c>已删除/</c> 并加时间戳，不是真删。
/// 台账是人工填了几十上百行的东西，一次误点不该让它没了。<see cref="Delete"/> 会把
/// 归档路径返回给调用方<b>显示出来</b> —— 说"已删除"却悄悄留了备份，
/// 和说"已删除"真的删了，都不如说清楚。</para>
///
/// <para><b>保存是原子的</b>：先写 <c>.tmp</c> 再替换。台账是唯一事实来源，
/// 写到一半断了比没写危险得多 —— 半份台账能读出来，还能算出一份"看上去正常"的月计划。</para>
/// </summary>
public sealed class MonthlyUnitLedgerStore
{
    /// <summary>基表文件名。</summary>
    public const string BaseFileName = "基表_采掘单元.csv";
    private const string RecycleDir = "已删除";
    /// <summary>覆盖前的留档目录 —— 与「已删除」分开：一个是"我不要了"，一个是"被新的顶掉了"。</summary>
    private const string HistoryDir = "历史";

    /// <summary>台账目录名（三个候选根共用这一个名字）。</summary>
    public const string DirName = "采掘单元台账";

    /// <summary>台账根目录。</summary>
    public string Root { get; }

    // ══════════════════════════════════════════════════════════════
    //  默认根目录 —— 软件自己的 Data 目录，不再放桌面
    // ══════════════════════════════════════════════════════════════

    /// <summary>首选根：<b>软件安装目录</b> <c>Data\采掘单元台账</c>（与 <c>pmgeo.db</c> 同一个 Data）。</summary>
    public static string InstallRoot => Path.Combine(AppContext.BaseDirectory, "Data", DirName);

    /// <summary>
    /// 退路：<c>%LOCALAPPDATA%\PitMine\采掘单元台账</c>（沿用 <c>TaskPersistence</c> 那套约定）。
    /// <para>软件装在 <c>Program Files</c> 下时 <see cref="InstallRoot"/> 默认<b>不可写</b> ——
    /// 那时候台账每一次保存都会失败，而失败点在原子写里，界面上只看到一句"保存失败"。
    /// 所以开工前先探一次写权限，写不进去就用这里，并<b>把换了目录这件事说出来</b>。</para>
    /// </summary>
    public static string UserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitMine", DirName);

    /// <summary>
    /// 老位置：桌面 <c>采掘单元台账</c>。<b>只用来迁移一次</b>，不再作为工作目录。
    /// </summary>
    public static string LegacyDesktopRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), DirName);

    /// <summary>迁移后留在老目录里的指引文件 —— 人去桌面找台账时能看到东西挪哪儿去了。</summary>
    public const string LegacyNoticeFileName = "_台账已迁到软件目录.txt";

    private static readonly object _rootLock = new();
    private static string? _resolvedRoot;
    private static string _resolvedNote = "";

    /// <summary>
    /// 默认根目录 = <see cref="InstallRoot"/>，写不进去时退到 <see cref="UserRoot"/>；
    /// 首次解析时把桌面老目录里的台账<b>复制</b>过来（见 <see cref="ResolveRoot"/>）。
    ///
    /// <para><b>只解析一次并缓存</b>：探写权限要真的写一个探针文件、迁移要遍历老目录，
    /// 而 <c>new MonthlyUnitLedgerStore()</c> 在日计划对号、真轨索引、逐帧解算里被反复构造
    /// （<c>StageSimPlayer</c> 明说过"逐帧读盘会把动画拖死"）。</para>
    /// </summary>
    public static string DefaultRoot { get { EnsureResolved(); return _resolvedRoot!; } }

    /// <summary>
    /// 默认根目录是怎么定下来的（换过目录 / 迁移了几个文件）。空 = 首选目录直接可用、没有老数据。
    /// <b>取用方要显示出来</b> —— 台账换了地方不说，人只会以为数据丢了。
    /// </summary>
    public static string DefaultRootNote { get { EnsureResolved(); return _resolvedNote; } }

    private static void EnsureResolved()
    {
        lock (_rootLock)
        {
            if (_resolvedRoot != null) return;
            try { _resolvedRoot = ResolveRoot(InstallRoot, UserRoot, LegacyDesktopRoot, CanWrite, out _resolvedNote); }
            catch (Exception ex)
            {
                // 解析本身出意外时也得有个能用的目录：宁可用用户目录，不许把异常抛给调用方
                _resolvedRoot = UserRoot;
                _resolvedNote = $"◆ 台账目录解析出意外（{ex.GetType().Name}：{ex.Message}）—— 暂用 {UserRoot}。";
            }
        }
    }

    /// <summary>
    /// 定根目录 + 一次性迁移。<b>纯参数化</b>：三个候选根与写权限探测都从参数进来，
    /// 判据拿临时目录直接调它 —— 不能让判据去碰真的 <c>Program Files</c>，也不许它往真桌面写东西。
    ///
    /// <para>三条口径：
    /// <list type="number">
    ///   <item><paramref name="primary"/> 写不进去就用 <paramref name="fallback"/>，<b>并在 note 里说</b>。
    ///     静默换目录与静默写失败一样坏 —— 人会在两个目录之间找不到自己的表。</item>
    ///   <item>目标目录<b>还没有基表</b>、而 <paramref name="legacy"/> 有，才迁移。
    ///     反过来（目标已有基表）绝不迁 —— 那会拿桌面上的旧基表盖掉正在用的新基表。</item>
    ///   <item>迁移是<b>复制</b>不是移动，且同名文件不覆盖。台账是人工填了几十上百行的东西，
    ///     一次目录切换不该让老位置上的原件消失；老目录留一个指引文件说明东西在哪儿。</item>
    /// </list></para>
    /// </summary>
    public static string ResolveRoot(string primary, string fallback, string legacy,
                                     Func<string, bool> canWrite, out string note)
    {
        note = "";
        string root;
        if (canWrite(primary)) root = primary;
        else if (canWrite(fallback))
        {
            root = fallback;
            note = $"◆ 写不进软件目录（{primary}）—— 台账改存 {fallback}。"
                 + "（多半是软件装在 Program Files 下；要放回软件目录请以管理员身份运行，或给该目录写权限）";
        }
        else
        {
            root = fallback;
            note = $"◆ 软件目录（{primary}）和用户目录（{fallback}）**都写不进去** —— "
                 + "台账保存会失败。请检查这两个目录的写权限。";
        }

        string migrated = Migrate(legacy, root);
        if (migrated.Length > 0) note = (note.Length > 0 ? note + "\n" : "") + migrated;
        return root;
    }

    /// <summary>把老目录里的台账复制到新目录。返回人读说明（没干事就返回空串）。</summary>
    private static string Migrate(string legacy, string root)
    {
        if (string.IsNullOrWhiteSpace(legacy) || string.IsNullOrWhiteSpace(root)) return "";
        if (string.Equals(Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),
                          Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            if (!Directory.Exists(legacy)) return "";
            if (!File.Exists(Path.Combine(legacy, BaseFileName))) return "";       // 老目录里没有基表 ⇒ 没什么可迁的
            if (File.Exists(Path.Combine(root, BaseFileName))) return "";          // 新目录已在用 ⇒ 绝不覆盖（口径②）

            Directory.CreateDirectory(root);
            int copied = 0, skipped = 0;

            // ⚠ **按扩展名白名单迁是会漏的**：这个目录今天有 `*.csv`（基表 + 各期次）、
            //   `真轨_采掘单元.txt`、`煤卸点.txt` 三类，但它一直在长 ——
            //   白名单只覆盖"我写这行代码时知道的扩展名"，哪天新增一种（比如 json）就静默漏一类数据，
            //   而漏掉的表现是"某个功能的数据没了"，没人会想到是迁移漏了。
            //   所以反过来做：**全都迁，只排除明确不该迁的**。
            void CopyInto(string fromDir, string toDir)
            {
                if (!Directory.Exists(fromDir)) return;
                foreach (var f in Directory.EnumerateFiles(fromDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(f);
                    // 指引文件是我们自己写的（上一次迁移留的）· .tmp 是原子写的半成品 · 探针是权限探测的残留
                    if (string.Equals(name, LegacyNoticeFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith(".写权限探针_", StringComparison.Ordinal)) continue;

                    string dst = Path.Combine(toDir, name);
                    if (File.Exists(dst)) { skipped++; continue; }                 // 同名不覆盖（口径③）
                    Directory.CreateDirectory(toDir);
                    File.Copy(f, dst);
                    copied++;
                }
            }

            CopyInto(legacy, root);                                                          // 基表 · 各期次 · 真轨 · 煤卸点 …
            CopyInto(Path.Combine(legacy, RecycleDir), Path.Combine(root, RecycleDir));      // 回收站一起带走

            if (copied == 0) return "";

            // 老目录留指引：人去桌面找表时，看到的是"东西在哪儿"，不是一个空目录
            try
            {
                File.WriteAllText(Path.Combine(legacy, LegacyNoticeFileName),
                    "采掘单元台账已迁到软件目录。\r\n\r\n"
                  + $"现在的位置：{root}\r\n\r\n"
                  + "本目录里的文件是迁移时的原件，已经复制过去了，软件此后【不再读这里】——\r\n"
                  + "在这儿改动不会生效。确认新目录没问题后可以删掉本目录。\r\n",
                    new System.Text.UTF8Encoding(true));
            }
            catch { /* 指引写不进去不影响迁移本身 */ }

            return $"· 台账已从老位置复制过来：{copied} 个文件（{legacy} → {root}）"
                 + (skipped > 0 ? $"，{skipped} 个同名文件按原样保留没覆盖" : "")
                 + "。老目录的原件**保留**着，但软件此后不再读它。";
        }
        catch (Exception ex)
        {
            return $"◆ 从老位置（{legacy}）迁移台账失败（{ex.GetType().Name}：{ex.Message}）—— "
                 + "老文件没动，可以手工复制到新目录。";
        }
    }

    /// <summary>探一次真写权限。<b>只看 <c>Directory.Exists</c> 判不出来</b>：能建目录不等于能写文件。</summary>
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

    public MonthlyUnitLedgerStore(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root!;
    }

    public string BasePath => Path.Combine(Root, BaseFileName);
    public string MonthPath(string month) => Path.Combine(Root, SafeName(month) + ".csv");
    public bool HasBase => File.Exists(BasePath);
    public bool Exists(string month) => File.Exists(MonthPath(month));

    /// <summary>
    /// 月份标签 → 安全文件名。<b>非法字符不是"清理掉"而是"拒绝"</b>：
    /// 把 <c>2026/08</c> 悄悄改成 <c>2026_08</c>，用户下次按 <c>2026/08</c> 找就找不到，
    /// 而列表里明明有一个长得差不多的。
    /// </summary>
    public static bool IsValidMonth(string? month, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(month)) { reason = "期次不能是空的。"; return false; }
        var m = month!.Trim();
        if (m.Length > 40) { reason = "期次太长（超过 40 字）。"; return false; }
        foreach (char ch in Path.GetInvalidFileNameChars())
            if (m.IndexOf(ch) >= 0) { reason = $"期次里不能有「{ch}」这种字符（它进不了文件名）。"; return false; }
        if (m.StartsWith(".") || m.EndsWith(".")) { reason = "期次不能以点开头或结尾。"; return false; }
        if (string.Equals(m, Path.GetFileNameWithoutExtension(BaseFileName), StringComparison.Ordinal))
        { reason = $"期次不能叫「{m}」—— 那是基表的名字。"; return false; }
        return true;
    }

    private static string SafeName(string month) => (month ?? "").Trim();

    /// <summary>列出已有的月份（按名排序）。基表和回收站不在其中。</summary>
    public List<string> ListMonths()
    {
        var list = new List<string>();
        if (!Directory.Exists(Root)) return list;
        foreach (var f in Directory.EnumerateFiles(Root, "*.csv", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (string.Equals(Path.GetFileName(f), BaseFileName, StringComparison.Ordinal)) continue;
            list.Add(name);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    // ══════════════════════════════════════════════════════════════
    //  基表
    // ══════════════════════════════════════════════════════════════

    public bool TryLoadBase(out List<MiningUnitLedger.Row> rows, out List<string> issues)
    {
        rows = new List<MiningUnitLedger.Row>(); issues = new List<string>();
        if (!File.Exists(BasePath)) { issues.Add($"还没有基表（{BasePath}）—— 先从「采矿模型」和「排土条带」各生成一次。"); return false; }
        string text = ReadAll(BasePath, out string encNote);
        bool ok = MiningUnitLedger.TryRead(text, out rows, out issues);
        if (encNote.Length > 0) issues.Insert(0, encNote);
        return ok;
    }

    /// <summary>
    /// 写基表。<paramref name="fresh"/> 是刚算出来的全量行；已有基表里人填的列按 UnitId 保住。
    /// <paramref name="orphans"/> = 模型里已经没有、但填过排产的行 —— <b>由调用方决定怎么办，不静默丢</b>。
    /// </summary>
    public void SaveBase(IReadOnlyList<MiningUnitLedger.Row> fresh, out List<MiningUnitLedger.Row> orphans)
    {
        orphans = new List<MiningUnitLedger.Row>();
        List<MiningUnitLedger.Row>? old = null;
        if (File.Exists(BasePath) && MiningUnitLedger.TryRead(ReadAll(BasePath), out var prev, out _)) old = prev;
        var merged = MiningUnitLedger.Merge(fresh, old, out orphans);
        WriteAtomic(BasePath, MiningUnitLedger.ToCsv(merged, "基表（全量）"));
    }

    /// <summary>只写不合并（调用方已经自己合过了）。</summary>
    public void SaveBaseRaw(IReadOnlyList<MiningUnitLedger.Row> rows)
        => WriteAtomic(BasePath, MiningUnitLedger.ToCsv(rows, "基表（全量）"));

    // ══════════════════════════════════════════════════════════════
    //  月度台账
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 读一个月。<paramref name="refreshFromBase"/> 为真时用基表的几何与量<b>刷新</b>本月各行
    /// （计划列保留）——「几何以模型为准」这条分工在跨月读取时同样成立。
    /// </summary>
    public bool TryLoad(string month, out List<MiningUnitLedger.Row> rows, out List<string> issues,
                        bool refreshFromBase = true)
    {
        rows = new List<MiningUnitLedger.Row>(); issues = new List<string>();
        if (!IsValidMonth(month, out string why)) { issues.Add(why); return false; }
        string p = MonthPath(month);
        if (!File.Exists(p)) { issues.Add($"没有这一期的台账：{month}"); return false; }
        string text = ReadAll(p, out string encNote);
        if (!MiningUnitLedger.TryRead(text, out rows, out issues))
        { if (encNote.Length > 0) issues.Insert(0, encNote); return false; }
        if (encNote.Length > 0) issues.Insert(0, encNote);

        if (!refreshFromBase) { /* 调用方明确不要刷新 */ }
        else if (!TryLoadBase(out var bases, out var baseIssues))
        {
            // ⚠ 静默跳过刷新是最难发现的一种：用户拿到的是【上次存下来的旧几何】，
            //    界面上一个字都不会提，而"几何以模型为准"这条分工在这一跳上悄悄失效了。
            issues.Add("◆ 读不到基表，本期各行的几何与量是【上次存下来的旧值】，不是当前模型的。"
                     + (baseIssues.Count > 0 ? "（" + baseIssues[0] + "）" : ""));
        }
        else
        {
            // ⚠ 不能用 ToDictionary：它在键重复时直接抛 ArgumentException。
            //    重号本身已经在 MiningUnitLedger.TryRead 那一层抓住并报过了（读进来时就丢掉重复行），
            //    所以走到这里 bases 天然无重号 —— 但仍然不用 ToDictionary：
            //    这一层不该假设上一层的去重永远有效，一个 foreach 的成本换掉一条"能读出来却一读就崩"的路。
            //    （早先这里还统计并报了一次重号，那条提示在去重上移之后【永远不会触发】，已删 ——
            //      报不出来的警告和没有警告一样，还多骗人一次。）
            var byId = new Dictionary<string, MiningUnitLedger.Row>(StringComparer.Ordinal);
            foreach (var b in bases) byId[b.UnitId] = b;
            int missing = 0, refreshed = 0, rescaled = 0;
            double rescaleMaxPct = 0;
            foreach (var r in rows)
            {
                if (!byId.TryGetValue(r.UnitId, out var b)) { missing++; continue; }
                // 几何与量以基表为准；计划列（本行自己的）不动
                r.Kind = b.Kind; r.Region = b.Region; r.Seam = b.Seam;
                r.Band = b.Band; r.Panel = b.Panel; r.PanelCount = b.PanelCount;
                r.Cx = b.Cx; r.Cy = b.Cy; r.Cz = b.Cz; r.ZLo = b.ZLo; r.ZHi = b.ZHi;
                r.LengthM = b.LengthM; r.WidthM = b.WidthM; r.ThickM = b.ThickM;
                r.CoalM3 = b.CoalM3; r.CoalT = b.CoalT;
                r.GrossM3 = b.GrossM3; r.InCoalM3 = b.InCoalM3; r.NetRockM3 = b.NetRockM3;
                r.DumpCapM3 = b.DumpCapM3;
                refreshed++;

                // ★ 量刷新了，【流】也得跟着对上（2026-08-18）。
                //
                //   流是计划列、量是几何列：这里只刷量不动流的话，一旦模型重算过，
                //   同一行就出现"量列是新的、流是上一次排产写的"——两个数各自都正常，合不上。
                //   实测 2026-08：13 个煤单元全 100% 完成，量列合计 79.46 万t，
                //   而流合计只有 72.15 万t，且逐单元有多有少（9-B1-P9 整幅 7.80、流 8.82 —— 流比整幅还大）。
                //   清单看量列、三维模拟看流，于是"排了多少煤"有两个答案。
                //
                //   做法：按【本月末完成度 × 新的量】等比缩放这一行的流（保留去向与各去向的占比），
                //   缩放过的行要记账 —— 悄悄缩等于替用户认定"旧的分配比例仍然成立"。
                if (r.Flows.Count > 0)
                {
                    double want = r.Kind switch
                    {
                        LedgerKind.Coal => (r.CoalM3 ?? 0),
                        LedgerKind.Rock => (r.NetRockM3 ?? r.GrossM3 ?? 0),
                        _ => 0,
                    } * Math.Clamp(r.Done <= 0 ? 1.0 : r.Done, 0, 1);
                    double have = r.FlowSumM3;
                    if (want > 1e-6 && have > 1e-6 && Math.Abs(have - want) > Math.Max(1.0, 0.005 * want))
                    {
                        double k = want / have;
                        foreach (var fl in r.Flows) fl.InSituM3 *= k;
                        rescaled++;
                        rescaleMaxPct = Math.Max(rescaleMaxPct, Math.Abs(k - 1) * 100);
                    }
                }
            }
            if (missing > 0)
                // 基表里没有 = 模型重算之后这个单元不存在了。它的排产是照着一个不存在的体排的。
                issues.Add($"◆ 本期有 {missing} 个单元在基表里找不到 —— 模型重算后它们没有了，"
                         + "这些行的几何与量是【上次存下来的旧值】，不是当前模型的。");
            if (refreshed > 0) issues.Add($"· 已按基表刷新 {refreshed} 行的几何与量（计划列保留）。");
            if (rescaled > 0)
                issues.Add($"◆ 有 {rescaled} 行的【流】与刷新后的量对不上（最大差 {rescaleMaxPct:0.#}%），"
                         + "已按新量等比缩放（去向与各去向占比保留）——"
                         + "那是上一次排产写的流、量却已经跟着模型重算变了。**要以最新几何为准，请重排一次**。");
        }
        return true;
    }

    /// <summary>存一个月。只存<b>本月涉及的单元</b>（有期次/状态/完成度/去向的那些）。</summary>
    public bool Save(string month, IReadOnlyList<MiningUnitLedger.Row> rows, out string error)
        => Save(month, rows, out error, out _);

    /// <summary>
    /// 存一个月，并报出<b>这次保存把哪些单元从原文件里去掉了</b>。
    ///
    /// <para><b>为什么必须报</b>：保存是<b>整文件替换</b>，写进去的就是这一批行。
    /// 原文件里有、这一批里没有的单元<b>会消失</b>，而消失的原因可能是
    /// ① 用户确实把它挪出了本期（正常），也可能是
    /// ② 表被筛过 / 从基表重载过 / 另一个人刚加的行本地还没有（<b>丢数据</b>）。
    /// 两种情形在文件系统上长得一模一样，所以只能由引擎把差异摆出来，让人自己判。</para>
    /// </summary>
    public bool Save(string month, IReadOnlyList<MiningUnitLedger.Row> rows, out string error,
                     out List<string> droppedUnitIds)
    {
        error = "";
        droppedUnitIds = new List<string>();
        if (!IsValidMonth(month, out error)) return false;
        string p = MonthPath(month);
        try
        {
            if (File.Exists(p) && MiningUnitLedger.TryRead(ReadAll(p), out var prev, out _))
            {
                var now = new HashSet<string>(rows.Select(r => r.UnitId), StringComparer.Ordinal);
                foreach (var q in prev) if (!now.Contains(q.UnitId)) droppedUnitIds.Add(q.UnitId);
            }
            WriteAtomic(p, MiningUnitLedger.ToCsv(rows, $"期次 {month}"));
            return true;
        }
        catch (Exception ex) { error = "保存失败：" + ex.Message; return false; }
    }

    /// <summary>
    /// 删一个月 —— <b>挪进 <c>已删除/</c> 并加时间戳，不是真删</b>。
    /// 返回归档路径，调用方要<b>显示出来</b>（说了"删除"就得说清东西在哪儿）。
    /// </summary>
    public bool Delete(string month, out string archivedPath, out string error)
    {
        archivedPath = ""; error = "";
        if (!IsValidMonth(month, out error)) return false;
        string p = MonthPath(month);
        if (!File.Exists(p)) { error = $"没有这一期的台账：{month}"; return false; }
        try
        {
            string bin = Path.Combine(Root, RecycleDir);
            Directory.CreateDirectory(bin);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            archivedPath = Path.Combine(bin, $"{SafeName(month)}_{stamp}.csv");
            File.Move(p, archivedPath);
            return true;
        }
        catch (Exception ex) { error = "删除失败：" + ex.Message; return false; }
    }

    /// <summary>
    /// 覆盖前先<b>留一份旧的</b> —— 拷进 <c>历史/</c> 并加时间戳（<b>拷贝不是移动</b>，原文件原地不动）。
    ///
    /// <para><b>为什么单独一个方法而不是塞进 <see cref="Save(string, IReadOnlyList{MiningUnitLedger.Row}, out string)"/></b>：
    /// 保存是整文件替换，一期一份文件，第二次排产写下去，第一次那份计划就<b>不存在了</b>——
    /// 而两次的文件名一模一样，事后没有任何东西能说出"这里原来是另一套方案"。
    /// 留档必须发生在写之前，所以由调用方在确认覆盖那一刻显式调它；
    /// 塞进 Save 里的话，每一次例行保存都会堆一个副本，历史目录很快就没法看了。</para>
    ///
    /// <para>没有旧文件时返回 <c>false</c> 且 <paramref name="error"/> 为空 —— 那不是错误，是"没什么可备份的"。</para>
    /// </summary>
    public bool Backup(string month, out string archivedPath, out string error)
    {
        archivedPath = ""; error = "";
        if (!IsValidMonth(month, out error)) return false;
        string p = MonthPath(month);
        if (!File.Exists(p)) { error = ""; return false; }
        try
        {
            string dir = Path.Combine(Root, HistoryDir);
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            archivedPath = Path.Combine(dir, $"{SafeName(month)}_{stamp}.csv");
            File.Copy(p, archivedPath, overwrite: false);
            return true;
        }
        catch (Exception ex) { error = "留档失败：" + ex.Message; return false; }
    }

    /// <summary>某期已存台账的一行摘要（行数 + 煤/岩量），给"要不要覆盖"那道确认用。读不出来时返回空。</summary>
    public string PeekSummary(string month)
    {
        if (!Exists(month)) return "";
        try
        {
            if (!MiningUnitLedger.TryRead(ReadAll(MonthPath(month)), out var rows, out _)) return "";
            return $"{rows.Count} 行 · {MiningUnitLedger.Summary(rows)}";
        }
        catch { return ""; }
    }

    /// <summary>另存为新期次（复制一份当模板）。目标已存在时<b>不覆盖</b>，如实报。</summary>
    public bool CopyTo(string fromMonth, string toMonth, out string error)
    {
        error = "";
        if (!IsValidMonth(toMonth, out error)) return false;
        if (!Exists(fromMonth)) { error = $"源期次不存在：{fromMonth}"; return false; }
        if (Exists(toMonth)) { error = $"期次「{toMonth}」已经有了 —— 不覆盖。要换请先删除或改名。"; return false; }
        try { File.Copy(MonthPath(fromMonth), MonthPath(toMonth)); return true; }
        catch (Exception ex) { error = "另存失败：" + ex.Message; return false; }
    }

    /// <summary>一屏摘要：目录、基表状态、各期次的行数与量。</summary>
    public string Overview()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("台账目录：" + Root);
        if (!HasBase) sb.AppendLine("基表：还没有 —— 先从「采矿模型」和「排土条带」各生成一次");
        else if (MiningUnitLedger.TryRead(ReadAll(BasePath), out var b, out _))
            sb.AppendLine($"基表：{b.Count} 行 · {MiningUnitLedger.Summary(b)}");
        else sb.AppendLine("基表：读不出来（文件在，但格式不对）");

        var months = ListMonths();
        if (months.Count == 0) sb.AppendLine("期次：还没有");
        else
            foreach (var m in months)
            {
                if (MiningUnitLedger.TryRead(ReadAll(MonthPath(m)), out var rs, out _))
                    sb.AppendLine($"  {m}：{rs.Count} 行 · {MiningUnitLedger.Summary(rs)}");
                else sb.AppendLine($"  {m}：读不出来");
            }
        return sb.ToString();
    }

    // ── 文件基础件 ──────────────────────────────────────────────

    /// <summary>
    /// 读文本。<b>本格式一律 UTF-8</b>，但别人拿 Excel（中文环境默认 GB18030）另存过的文件照样会送进来。
    ///
    /// <para><b>⚠ 固定按 UTF-8 读是会静默毁数据的</b>：<c>Encoding.UTF8</c> 默认<b>替换回退</b>，
    /// 非法字节变成 U+FFFD 而<b>不抛异常</b>。GB18030 的尾字节 ≥0x40，永远不会与逗号相撞 ⇒
    /// 列数不变、纯 ASCII 的 <c>UnitId</c> 完好无损、中文列名全成乱码。
    /// 于是文件"读成功"了，只是每一列都对不上 —— 而这正是最难发现的那种失败。
    ///
    /// 所以这里用<b>严格 UTF-8</b>（非法字节即抛），抛了再退 GB18030 并<b>把这件事说出来</b>。</para>
    /// </summary>
    private static string ReadAll(string path) => ReadAll(path, out _);

    private static string ReadAll(string path, out string encodingNote)
    {
        encodingNote = "";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { encodingNote = "读文件失败：" + ex.Message; return ""; }

        var strict = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);
        try { return StripBom(strict.GetString(bytes)); }
        catch (System.Text.DecoderFallbackException) { /* 不是 UTF-8，往下退 */ }

        try
        {
            // ⚠ .NET Core 起，GB18030 不在默认编码表里 —— 不注册 CodePagesEncodingProvider 的话
            //   GetEncoding 直接抛 ArgumentException，这条退路就形同虚设。
            //   本模块里唯一注册过它的是 BlkReader（读 .blk 时），
            //   也就是说这条退路此前【只有用户先打开过 .blk 文件才生效】—— 那是个偶然依赖。
            //   注册是进程级、幂等的，重复调无害。
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
            catch { /* 已注册 */ }
            var gb = System.Text.Encoding.GetEncoding("GB18030");
            encodingNote = "· 这份文件不是 UTF-8，已按 GB18030 读回（多半是用 Excel 另存过）。"
                         + "存回去时会写成 UTF-8。";
            return StripBom(gb.GetString(bytes));
        }
        catch (Exception ex)
        {
            // 连退路都没有时，宁可返回空让上层"读不出来"，也不返回一份带替换字符的假文本
            encodingNote = $"◆ 这份文件既不是 UTF-8 也读不成 GB18030（{ex.Message}）—— 编码不认识。";
            return "";
        }
    }

    private static string StripBom(string s) => s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;

    /// <summary>原子写：先写 .tmp，再替换。中途失败时原文件<b>一个字节都没动</b>。</summary>
    private static void WriteAtomic(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(true));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
