using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 无界面的数据导入命令行 —— 服务器上一次把一批 CSV 读进来、写进库。
///
/// 为什么走应用自己的命令行而不是写 SQL 脚本：导入不只是插数据，还要解外键
/// （equipment_id → 实体）、查码表、按去重键更新。用 SQL 重写一遍必然与界面里的逻辑走样，
/// 这里直接复用 <see cref="ImportSpec"/> 派发到同一批 Import* 函数，界面与命令行结果一致。
///
///   pitmine3d --import-list                     打印表格规范
///   pitmine3d --import-template all ./模板       导出全部模板
///   pitmine3d --import ./数据目录                批量导入（按依赖顺序）
///   pitmine3d --import a.csv b.csv --no-overwrite
///   pitmine3d --import ./数据 --db "Host=10.0.0.9;Port=5432;Database=pitmine;Username=pitmine;Password=***"
/// </summary>
public static class ImportCli
{
    /// <summary>识别并执行命令行导入。返回 null 表示不是导入命令，应继续走图形界面。</summary>
    public static int? TryRun(string[] args)
    {
        if (args == null || args.Length == 0) return null;
        switch (args[0])
        {
            case "--import-list": return List();
            case "--import-template": return Template(args.Skip(1).ToArray());
            case "--import": return Import(args.Skip(1).ToArray());
            default: return null;
        }
    }

    private static int List()
    {
        Console.Write(ImportSpec.Describe());
        return 0;
    }

    private static int Template(string[] rest)
    {
        if (rest.Length == 0)
        {
            Console.Error.WriteLine("用法: --import-template <类型|all> [输出目录]");
            Console.Error.WriteLine("类型: " + string.Join(" / ", ImportSpec.All.Select(e => e.Key)));
            return 2;
        }
        string dir = rest.Length > 1 ? rest[1] : ".";
        Directory.CreateDirectory(dir);

        var list = string.Equals(rest[0], "all", StringComparison.OrdinalIgnoreCase)
            ? ImportSpec.All.ToList()
            : new List<ImportSpec.Entry>();
        if (list.Count == 0)
        {
            var one = ImportSpec.Find(rest[0]);
            if (one == null) { Console.Error.WriteLine($"未知类型: {rest[0]}（--import-list 看全部）"); return 2; }
            list.Add(one);
        }
        foreach (var e in list)
        {
            string path = Path.Combine(dir, $"{e.Key}.csv");
            File.WriteAllText(path, e.Template(), new System.Text.UTF8Encoding(true));   // 带 BOM: Excel 打开不乱码
            Console.WriteLine($"已写出 {path}");
        }
        Console.WriteLine($"共 {list.Count} 个模板 → {Path.GetFullPath(dir)}");
        Console.WriteLine("填好数据后: pitmine3d --import <该目录>");
        return 0;
    }

    private static int Import(string[] rest)
    {
        bool overwrite = true;
        string? conn = null;
        var targets = new List<string>();
        for (int i = 0; i < rest.Length; i++)
        {
            string a = rest[i];
            if (a == "--no-overwrite") { overwrite = false; continue; }   // 同键行跳过而非更新
            if (a == "--db")
            {
                if (++i >= rest.Length) { Console.Error.WriteLine("--db 需要连接串"); return 2; }
                conn = rest[i]; continue;
            }
            if (a.StartsWith("--")) { Console.Error.WriteLine($"未知选项: {a}"); return 2; }
            targets.Add(a);
        }
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("用法: --import <目录或 CSV 文件...> [--db <连接串>] [--no-overwrite]");
            return 2;
        }

        // 收文件：目录则取其下全部 csv/txt
        var files = new List<string>();
        foreach (var t in targets)
        {
            if (Directory.Exists(t))
                files.AddRange(Directory.EnumerateFiles(t, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
            else if (File.Exists(t)) files.Add(t);
            else { Console.Error.WriteLine($"找不到: {t}"); return 2; }
        }
        if (files.Count == 0) { Console.Error.WriteLine("没有 .csv/.txt 文件"); return 2; }

        // 按文件名认类型；认不出的列出来但不中断（其余照常导）
        var plan = new List<(ImportSpec.Entry entry, string file)>();
        var unknown = new List<string>();
        foreach (var f in files)
        {
            var e = ImportSpec.FromFileName(f);
            if (e == null) unknown.Add(f); else plan.Add((e, f));
        }
        foreach (var f in unknown)
            Console.Error.WriteLine($"[跳过] 文件名认不出导入类型: {Path.GetFileName(f)}" +
                                    "（文件名里要含类型名，如 煤质化验_2025.csv；--import-list 看全部类型）");
        if (plan.Count == 0) return 2;

        // **按依赖顺序排**：设备台账不先入库，后面按设备统计的行就落不到实体上
        var order = ImportSpec.All.Select((e, i) => (e.Key, i)).ToDictionary(x => x.Key, x => x.i);
        plan = plan.OrderBy(p => order[p.entry.Key]).ToList();

        GeoDatabase db;
        try { db = GeoDatabase.OpenSeeded(conn); }
        catch (Exception ex)
        {
            // 产品只连中心库(openGauss), 连不上必须明确失败 —— 不能悄悄写到别处去
            Console.Error.WriteLine($"打开数据库失败: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("连接串按下列优先级取: --db 参数 > 环境变量 > 站点配置 > 用户配置");
            Console.Error.WriteLine($"  站点配置: {DbConnectionSettings.SiteConfigPath() ?? "(本平台无)"}");
            Console.Error.WriteLine($"  用户配置: {DbConnectionSettings.UserConfigPath()}");
            Console.Error.WriteLine("  显式指定: --db \"Host=中心库IP;Port=5432;Database=pitmine;Username=pitmine;Password=***\"");
            return 3;
        }

        Console.WriteLine($"数据库: {(conn != null ? "(--db 指定)" : "按配置")}");
        Console.WriteLine($"待导入 {plan.Count} 个文件，同键行{(overwrite ? "更新" : "跳过")}");
        Console.WriteLine();
        Console.WriteLine($"{"类型",-10} {"文件",-32} {"行数",6} {"新增",6} {"更新",6} {"跳过",6} {"错误",6}");
        Console.WriteLine(new string('-', 80));

        int totIns = 0, totUpd = 0, totSkip = 0, totErr = 0, failed = 0;
        foreach (var (e, f) in plan)
        {
            List<IReadOnlyDictionary<string, string>> rows;
            try { rows = GeoDataQueries.ParseCsv(File.ReadAllText(f)); }
            catch (Exception ex) { Console.Error.WriteLine($"[失败] {Path.GetFileName(f)} 读取: {ex.Message}"); failed++; continue; }
            if (rows.Count == 0) { Console.WriteLine($"{e.Key,-10} {Path.GetFileName(f),-32} {0,6}  (无数据行)"); continue; }

            // 必填列缺一个就整份不导 —— 半份数据入库比不导更麻烦
            var missing = e.Required_.Select(c => c.Name).Where(n => !rows[0].ContainsKey(n)).ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"[失败] {Path.GetFileName(f)} 缺必填列: {string.Join(", ", missing)}");
                failed++; continue;
            }

            try
            {
                var o = e.Run(db.Connection, rows, overwrite);
                totIns += o.Inserted; totUpd += o.Updated; totSkip += o.Skipped; totErr += o.Errors;
                Console.WriteLine($"{e.Key,-10} {Path.GetFileName(f),-32} {rows.Count,6} {o.Inserted,6} {o.Updated,6} {o.Skipped,6} {o.Errors,6}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[失败] {Path.GetFileName(f)} 导入: {ex.Message}");
                failed++;
            }
        }
        Console.WriteLine(new string('-', 80));
        Console.WriteLine($"{"合计",-10} {"",-32} {"",6} {totIns,6} {totUpd,6} {totSkip,6} {totErr,6}");
        if (failed > 0) Console.Error.WriteLine($"\n{failed} 个文件未导入（见上面 [失败] 行）");
        if (unknown.Count > 0) Console.Error.WriteLine($"{unknown.Count} 个文件类型认不出，已跳过");
        return failed > 0 || totErr > 0 ? 1 : 0;
    }
}
