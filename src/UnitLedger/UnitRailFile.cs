// 忠实移植自原 PitMine3D Modules/BlockModelLib/Domain/UnitRailFile.cs（逐行对应；仅命名空间适配）
using System;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Cad.Dump;   // DumpStripPlanner.Cell（Kylin 侧已按原版移植，见 Cad/Dump）

/// <summary>
/// <b>真轨伴生文件</b> —— 把采掘单元 / 排土位置的前脸轨落盘，与台账 CSV 放在同一个目录。
///
/// <para><b>为什么要有它</b>：真轨此前<b>只在会话内</b>（<see cref="MiningModelStore"/> /
/// <see cref="DumpStripStore"/>）。台账 CSV 存的是质心 + 长宽厚，没有折点 ⇒
/// <b>关一次软件，三维动态模拟里 2312 个单元全部退成轴对齐盒子</b>，
/// 而且不报错 —— 屏幕上只是"形状不对"，没人会知道是真轨丢了。
/// 实测：期次 2026-08 的 97 个体全部是盒子。</para>
///
/// <para><b>为什么不并进台账 CSV</b>：台账是<b>给人看、给人改</b>的表（L1–L10 那套规矩都建立在这上面）；
/// 一个单元几十个折点塞进去，行会长到没法看，编码/引号/换行的坑也全都放大。
/// 伴生文件与 `.mprof` 中间文件同一个路子：<b>只存"重算一次才能得到、而重算很贵"的东西</b>。</para>
///
/// <para><b>它是可以过期的</b>：模型重跑后轨会变。所以文件里带生成时刻，读回来<b>必须把时刻显示出来</b>
/// —— 与会话内交接件同一条纪律（让人知道这份几何是什么时候生成的）。</para>
/// </summary>
public static class UnitRailFile
{
    /// <summary>文件名（与基表同目录）。</summary>
    public const string FileName = "真轨_采掘单元.txt";
    private const string Magic = "PMRAIL";
    /// <summary>
    /// 格式版本。<b>2 = 每行多一列「本行的生成时刻」</b>（第 7 列）。
    /// <para>为什么要逐行记：落盘不再是"整文件按本次会话重写"，而是<b>按 UnitId 并</b>
    /// （<see cref="SaveFromStores"/>）—— 一份文件里于是同时有本次刚生成的轨和上次沿用下来的轨。
    /// 只有文件头一个时刻的话，沿用下来的那批会顶着"刚生成"的时刻，
    /// 而"这份几何是什么时候生成的"正是这个文件唯一要守住的话。</para>
    /// <para>读端对 1 与 2 都收：第 7 列缺 ⇒ 该行取文件头的时刻（老文件本来就是整份同一时刻）。</para>
    /// </summary>
    private const string Version = "2";

    /// <summary>一个单元的真轨。字段与 <c>UnitSolidStage.RailIndex</c> 需要的那一组一一对应。</summary>
    public sealed class RailRow
    {
        public string UnitId = "";
        /// <summary>前脸轨（坡顶线）扁平 xyz。</summary>
        public double[] Crest = Array.Empty<double>();
        /// <summary>坡底线扁平 xyz。</summary>
        public double[] Toe = Array.Empty<double>();
        /// <summary>推进宽 / 条带宽（m）。</summary>
        public double WidthM;
        /// <summary>排土位置整级不变的顶/底标高；采掘单元为 null（逐点取轨上的 z）。</summary>
        public double? CrestZ, ToeZ;

        /// <summary>
        /// <b>这一行</b>的几何是什么时候生成的。null = 本次落盘现取（写的时候按"现在"落）。
        /// <para>并入落盘时，沿用下来的行带的是它<b>原来</b>那个时刻 —— 一份文件里两种时刻是常态，
        /// 把它们统一成"现在"等于宣称陈的几何是新的。</para>
        /// </summary>
        public DateTime? StampedAt;

        public bool IsValid => UnitId.Length > 0 && Crest.Length >= 6 && Toe.Length >= 6 && WidthM > 1e-6;
    }

    /// <summary>默认路径 = 台账目录下。</summary>
    public static string PathIn(string? ledgerRoot)
        => System.IO.Path.Combine(string.IsNullOrWhiteSpace(ledgerRoot) ? "." : ledgerRoot!, FileName);

    // ══════════════════════════════════════════════════════════════
    //  写
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 落盘。<b>原子写</b>（先写临时文件再替换）—— 写一半断电会毁掉上一份好的真轨，
    /// 而下次打开只表现为"全是盒子"。
    /// </summary>
    /// <returns>写了几条。</returns>
    public static int Write(string path, IEnumerable<RailRow>? rows, out string note)
    {
        note = "";
        var list = (rows ?? Enumerable.Empty<RailRow>()).Where(r => r != null && r.IsValid).ToList();
        if (list.Count == 0) { note = "没有可落盘的真轨（一条都不合格）。"; return 0; }

        DateTime now = DateTime.Now;
        var sb = new StringBuilder();
        sb.Append(Magic).Append('\t').Append(Version).Append('\t')
          .Append(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\t')
          .Append(list.Count).Append('\n');
        sb.Append("# UnitId\t宽m\t顶Z(空=逐点)\t底Z\t坡顶线 x,y,z;…\t坡底线 x,y,z;…\t本行生成时刻\n");
        foreach (var r in list)
        {
            sb.Append(Esc(r.UnitId)).Append('\t')
              .Append(F(r.WidthM)).Append('\t')
              .Append(r.CrestZ.HasValue ? F(r.CrestZ.Value) : "").Append('\t')
              .Append(r.ToeZ.HasValue ? F(r.ToeZ.Value) : "").Append('\t')
              .Append(Pts(r.Crest)).Append('\t')
              .Append(Pts(r.Toe)).Append('\t')
              .Append((r.StampedAt ?? now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append('\n');
        }

        string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        // 严格 UTF-8、不带 BOM（读端也按严格 UTF-8 —— 见 L2：默认那个是替换回退，会把编码错误变成静默的数据损坏）
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        // ★ 替换而不是「先删再挪」：删完到挪完之间那一瞬**两份都不在**，
        //   赶上崩溃就是"真轨没了 → 全退盒子"。何况现在文件里有【沿用下来、重跑不回来】的行。
        //   与 MonthlyUnitLedgerStore.WriteAtomic 同一条路子。
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        note = $"真轨已落盘 {list.Count} 条 → {path}";
        return list.Count;
    }

    /// <summary>
    /// 把<b>会话内</b>两个交接件里的真轨收集起来落盘（「从采矿模型取」「从排土条带取」之后调）。
    ///
    /// <para><b>UnitId 一律复用既有那份拼法</b>（<see cref="MiningUnitLedger.StripUnitId"/> /
    /// <see cref="DumpStripPlanner.Cell.Code"/>）—— 本仓库出过"三处各拼一遍 UnitId"的账，
    /// 对不上号的后果是"真轨查不到 → 全退盒子"，而且不报错。</para>
    ///
    /// <para><b>★ 是「并」不是「重写」（2026-08-18）</b>：会话内的两个交接件<b>各自独立</b>——
    /// 开一次软件只跑了「排土条带」，<c>MiningModelStore</c> 就是空的。原来这里按会话内容整文件重写，
    /// 于是那一次落盘<b>把上一次落下的煤/岩真轨整批抹掉</b>，而三维那边只表现为"这些体变成盒子了"。
    /// 实测这正是盘上真轨文件 2112 条<b>全是排土</b>、煤 109 + 岩 824 一条都没有的原因。</para>
    ///
    /// <para>所以：本次会话生成的按 UnitId <b>覆盖</b>同名的旧行（模型重跑了就该换），
    /// 本次没生成的<b>原样沿用</b>并保留它<b>原来的生成时刻</b>（<see cref="RailRow.StampedAt"/>）——
    /// 沿用不等于"刚生成"，这两件事必须在文件里分得开。</para>
    /// </summary>
    public static int SaveFromStores(string? ledgerRoot, out string note)
    {
        var rows = new List<RailRow>();

        void AddStrips(IReadOnlyList<MiningModelPlanner.Strip>? strips)
        {
            if (strips == null) return;
            foreach (var s in strips)
            {
                if (s == null || s.CrestXyz.Length < 6 || s.ToeXyz.Length < 6) continue;
                double w = s.AdvanceWidthM > 1e-6 ? s.AdvanceWidthM : 0;
                if (w <= 1e-6) continue;           // 推进宽不知道就不落（不拿 40 冒充，与 RailIndex 同口径）
                rows.Add(new RailRow
                {
                    UnitId = MiningUnitLedger.StripUnitId(s.SeamCode, s.BandId, s.PanelIndex),
                    Crest = s.CrestXyz, Toe = s.ToeXyz, WidthM = w,
                });
            }
        }

        AddStrips(MiningModelStore.Coal?.Strips);
        AddStrips(MiningModelStore.Rock?.Strips);

        var cells = DumpStripStore.Last?.Cells;
        if (cells != null)
            foreach (var c in cells)
            {
                if (c == null || c.CrestXyz.Length < 6 || c.ToeXyz.Length < 6) continue;
                if (!(c.StripWidthM > 1e-6) || !(c.CrestZ - c.ToeZ > 1e-6)) continue;
                rows.Add(new RailRow
                {
                    UnitId = string.IsNullOrWhiteSpace(c.Code)
                        ? $"排土场-L{c.LevelIndex}-P{c.PanelIndex:00}-S{c.StepIndex:00}" + (c.SubIndex > 0 ? $"-{c.SubIndex}" : "")
                        : c.Code,
                    Crest = c.CrestXyz, Toe = c.ToeXyz, WidthM = c.StripWidthM,
                    CrestZ = c.CrestZ, ToeZ = c.ToeZ,
                });
            }

        if (rows.Count == 0)
        {
            // ⚠ 会话内一条都没有时【绝不动文件】：原来这里也只是不写，但现在要把话说全 ——
            //   盘上那份还在，三维照样有真轨用。
            note = "会话内没有真轨可落盘 —— 先跑一次「采矿模型」或「排土条带」。（盘上原有的那份没动）";
            return 0;
        }

        string path = PathIn(ledgerRoot);
        int carried = 0;
        DateTime oldestCarried = default;
        string readNote = "";
        if (TryRead(path, out var disk, out DateTime diskAt, out readNote))
        {
            var mine = new HashSet<string>(rows.Select(r => r.UnitId), StringComparer.Ordinal);
            foreach (var kv in disk)
            {
                if (mine.Contains(kv.Key)) continue;              // 本次会话重生成了 ⇒ 用新的
                var old = kv.Value;
                old.StampedAt ??= diskAt == default ? (DateTime?)null : diskAt;
                rows.Add(old);
                carried++;
                if (old.StampedAt.HasValue && (oldestCarried == default || old.StampedAt.Value < oldestCarried))
                    oldestCarried = old.StampedAt.Value;
            }
        }

        int n = Write(path, rows, out note);
        if (carried > 0)
            note += $"　· 其中 {carried} 条是本次会话没重生成、**从上一份原样沿用**的"
                  + (oldestCarried != default ? $"（最早 {oldestCarried:MM-dd HH:mm} 生成）" : "")
                  + " —— 那批模型重跑过的话，去对应的生成器再跑一次再取。";
        else if (readNote.Length > 0 && !readNote.StartsWith("没有真轨文件", StringComparison.Ordinal)
                                     && !readNote.StartsWith("真轨读回", StringComparison.Ordinal))
            // 盘上有文件却读不出来 ⇒ 这一次是【整份换掉】它，必须说；不说的话别的类别就这么没了
            note += "　◆ 盘上原有的真轨文件读不出来（" + readNote + "）—— 本次是整份替换，"
                  + "上一份里别的类别的真轨不再存在。";
        return n;
    }

    // ══════════════════════════════════════════════════════════════
    //  读
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 读回来。<b>读不出就是读不出</b>：绝不返回半份（半份真轨会让一部分单元是真形状、
    /// 另一部分是盒子，而看上去像"模型没生成全"）。
    /// </summary>
    /// <param name="stampedAt">文件里记的生成时刻 —— <b>必须显示给人看</b>，它可能已经过期。</param>
    public static bool TryRead(string path, out Dictionary<string, RailRow> map, out DateTime stampedAt, out string note)
    {
        map = new Dictionary<string, RailRow>(StringComparer.Ordinal);
        stampedAt = default;
        note = "";
        if (!File.Exists(path)) { note = $"没有真轨文件（{path}）"; return false; }

        string text;
        try
        {
            // 严格 UTF-8：非法字节当场抛，不做替换回退（L2）
            // ⚠ using 不能省：读完不关句柄的话，紧接着的落盘会撞上"文件被另一个进程占用"——
            //    并入落盘（SaveFromStores）正是"先读再写同一个文件"。
            using var sr = new StreamReader(path, new UTF8Encoding(false, throwOnInvalidBytes: true));
            text = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            note = $"真轨文件读不出来（{ex.GetType().Name}：{ex.Message}）—— 不按半份用。";
            return false;
        }

        var lines = text.Split('\n');
        if (lines.Length < 2 || !lines[0].StartsWith(Magic, StringComparison.Ordinal))
        { note = "真轨文件的头不认识（不是 PMRAIL）—— 拒收。"; return false; }

        var head = lines[0].Split('\t');
        int declared = 0;
        if (head.Length >= 3) DateTime.TryParse(head[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out stampedAt);
        if (head.Length >= 4) int.TryParse(head[3], out declared);

        int bad = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            if (c.Length < 6) { bad++; continue; }
            var row = new RailRow
            {
                UnitId = Unesc(c[0]),
                WidthM = D(c[1]),
                CrestZ = c[2].Length > 0 ? D(c[2]) : null,
                ToeZ = c[3].Length > 0 ? D(c[3]) : null,
                Crest = Pts(c[4]),
                Toe = Pts(c[5]),
                // 第 7 列（v2）= 本行自己的生成时刻；老文件没有这一列 ⇒ 整份同一个时刻，取文件头的
                StampedAt = c.Length >= 7 && DateTime.TryParse(c[6], CultureInfo.InvariantCulture,
                                                              DateTimeStyles.None, out var rowAt)
                            ? rowAt : (stampedAt == default ? (DateTime?)null : stampedAt),
            };
            if (!row.IsValid) { bad++; continue; }
            map[row.UnitId] = row;
        }

        // 声明条数与实际对不上要说 —— 这是"文件被截断"的唯一征兆，而截断读起来完全正常
        note = $"真轨读回 {map.Count} 条（{stampedAt:MM-dd HH:mm} 生成）";
        // ★ 一份文件里可以有两批时刻（并入落盘会沿用上一次的行）——【最早的那一批要说出来】。
        //   只报文件头那个时刻的话，沿用下来的陈轨会顶着"刚生成"的招牌。
        DateTime fileAt = stampedAt;                 // out 参数进不了 lambda，先落一个本地量
        var oldest = map.Values.Where(r => r.StampedAt.HasValue).Select(r => r.StampedAt!.Value)
                        .DefaultIfEmpty(default).Min();
        if (oldest != default && fileAt != default && (fileAt - oldest).TotalMinutes > 1)
            note += $"　· 其中 {map.Values.Count(r => r.StampedAt.HasValue && (fileAt - r.StampedAt!.Value).TotalMinutes > 1)} 条"
                  + $"是**更早**沿用下来的（最早 {oldest:MM-dd HH:mm}）—— 那批模型重跑过就不作数了。";
        if (declared > 0 && declared != map.Count)
            note += $"　◆ 文件头声明 {declared} 条，实际解出 {map.Count} 条"
                  + (bad > 0 ? $"（{bad} 行不合格）" : "") + " —— 文件可能被截断或改过。";
        else if (bad > 0) note += $"　◆ 有 {bad} 行不合格已跳过。";
        return map.Count > 0;
    }

    // ── 小工具：数与点串 ─────────────────────────────────────────────
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    private static string Esc(string s) => s.Replace('\t', ' ').Replace('\n', ' ');
    private static string Unesc(string s) => s.Trim();

    private static string Pts(double[] xyz)
    {
        if (xyz == null || xyz.Length < 3) return "";
        var sb = new StringBuilder(xyz.Length * 8);
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            if (i > 0) sb.Append(';');
            sb.Append(F(xyz[i])).Append(',').Append(F(xyz[i + 1])).Append(',').Append(F(xyz[i + 2]));
        }
        return sb.ToString();
    }

    private static double[] Pts(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<double>();
        var parts = s.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var v = new List<double>(parts.Length * 3);
        foreach (var p in parts)
        {
            var q = p.Split(',');
            if (q.Length < 3) continue;
            v.Add(D(q[0])); v.Add(D(q[1])); v.Add(D(q[2]));
        }
        return v.ToArray();
    }
}
