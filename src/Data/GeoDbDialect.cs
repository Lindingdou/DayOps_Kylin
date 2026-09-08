using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 数据库方言接缝 —— 让 <see cref="GeoDatabase"/> 既能跑嵌入式 SQLite, 也能连达梦 DM。
///
/// 为什么要这层: 原程序(及本移植的第一版)把 SQLite 焊死在数据层, 换库要动 269 处。
/// 现在调用点统一降到 <see cref="DbConnection"/>/<see cref="DbCommand"/>(见 DbCompat.cs),
/// 剩下真正"每家不一样"的东西——建连、建表 DDL、外键开关、批量语句怎么拆、参数前缀——
/// 全部收敛到这里。加一种库 = 加一个子类, 不再满仓库改。
///
/// 选哪种由环境变量决定(默认 SQLite, 行为与移植前逐字一致):
///   PITMINE_DB=sqlite | dm          缺省 sqlite
///   PITMINE_DB_CONN=&lt;连接串&gt;   dm 必填, 例: Server=192.168.1.10:5236;User Id=SYSDBA;PWD=***
/// </summary>
public abstract class GeoDbDialect
{
    public abstract string Name { get; }

    /// <summary>建立(未打开的)连接。<paramref name="path"/> 仅嵌入式库用得上。</summary>
    public abstract DbConnection CreateConnection(string? path);

    /// <summary>本方言的迁移脚本在资源名里的标记段, 用来挑对应的一套 SQL。</summary>
    public abstract string MigrationMarker { get; }

    /// <summary>
    /// 确保迁移登记表存在。做成方法而不是裸 DDL: 达梦没有 CREATE TABLE IF NOT EXISTS,
    /// 得先查数据字典再决定建不建, 各家做法不一样。
    /// </summary>
    public abstract void EnsureSchemaMigrationTable(DbConnection conn);

    /// <summary>迁移登记表在 SQL 里的写法(达梦下标识符以下划线开头需加引号)。</summary>
    public virtual string SchemaMigrationTable => "_schema_migration";

    /// <summary>参数前缀。SQLite/SQLServer 系是 '@', 达梦是 ':'。</summary>
    public abstract char ParamPrefix { get; }

    /// <summary>迁移开始前/结束后的钩子(SQLite 用来临时关外键)。</summary>
    public virtual void BeforeMigrations(DbConnection conn) { }
    public virtual void AfterMigrations(DbConnection conn) { }

    /// <summary>
    /// 把一份迁移脚本拆成可逐条执行的语句。
    /// SQLite 的 Provider 允许一次 ExecuteNonQuery 吃整批, 达梦不行, 必须拆开喂。
    /// </summary>
    public virtual IReadOnlyList<string> SplitBatch(string script) => new[] { script };

    /// <summary>
    /// 把参数名规范到本方言的前缀。调用点写死了 '@v' 这种, 这里统一改写,
    /// 避免 129 处 AddWithValue 各自去关心跑在哪种库上。
    /// </summary>
    public string NormalizeParamName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        char c = name[0];
        if (c == '@' || c == ':')
            return ParamPrefix + name.Substring(1);
        return ParamPrefix + name;
    }

    // ── 当前进程使用的方言(读环境变量, 只解析一次) ────────────────────────────
    private static GeoDbDialect? _current;
    public static GeoDbDialect Current => _current ??= FromEnvironment();

    /// <summary>测试用: 显式指定方言。</summary>
    public static void Override(GeoDbDialect dialect) => _current = dialect;

    private static GeoDbDialect FromEnvironment()
    {
        string kind = (Environment.GetEnvironmentVariable("PITMINE_DB") ?? "sqlite").Trim().ToLowerInvariant();
        return kind switch
        {
            "dm" or "dameng" or "dm8" or "dm9" => new DmDialect(),
            _ => new SqliteDialect(),
        };
    }

    /// <summary>
    /// 按 SQL 词法拆语句: 分号断句, 但跳过字符串字面量、行/块注释,
    /// 并且不在触发器的 BEGIN…END 体内断开(那里面的分号是语句体的一部分)。
    /// 纯函数, 可单测 —— 不依赖任何数据库实例。
    /// </summary>
    protected static IReadOnlyList<string> SplitOnSemicolons(string script)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        int beginDepth = 0;          // BEGIN…END 嵌套深度
        int i = 0, n = script.Length;

        while (i < n)
        {
            char ch = script[i];

            // 单引号字符串: '' 是转义的单引号
            if (ch == '\'')
            {
                int j = i + 1;
                sb.Append(ch);
                while (j < n)
                {
                    sb.Append(script[j]);
                    if (script[j] == '\'')
                    {
                        if (j + 1 < n && script[j + 1] == '\'') { sb.Append(script[++j]); }
                        else break;
                    }
                    j++;
                }
                i = j + 1;
                continue;
            }

            // 行注释
            if (ch == '-' && i + 1 < n && script[i + 1] == '-')
            {
                while (i < n && script[i] != '\n') { sb.Append(script[i]); i++; }
                continue;
            }

            // 块注释
            if (ch == '/' && i + 1 < n && script[i + 1] == '*')
            {
                while (i < n && !(script[i] == '*' && i + 1 < n && script[i + 1] == '/')) { sb.Append(script[i]); i++; }
                if (i < n) { sb.Append("*/"); i += 2; }
                continue;
            }

            // 关键字 BEGIN / END(仅在独立词边界上计数)
            if ((ch == 'B' || ch == 'b') && IsWordAt(script, i, "BEGIN")) { beginDepth++; sb.Append(script, i, 5); i += 5; continue; }
            if ((ch == 'E' || ch == 'e') && IsWordAt(script, i, "END"))   { if (beginDepth > 0) beginDepth--; sb.Append(script, i, 3); i += 3; continue; }

            if (ch == ';' && beginDepth == 0)
            {
                string stmt = sb.ToString().Trim();
                if (stmt.Length > 0) outp.Add(stmt);
                sb.Clear();
                i++;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        string tail = sb.ToString().Trim();
        if (tail.Length > 0) outp.Add(tail);
        return outp;
    }

    private static bool IsWordAt(string s, int i, string word)
    {
        if (i + word.Length > s.Length) return false;
        if (string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        if (i > 0 && (char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_')) return false;
        int after = i + word.Length;
        if (after < s.Length && (char.IsLetterOrDigit(s[after]) || s[after] == '_')) return false;
        return true;
    }
}
