using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Reflection;
using System.Text;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 数据库方言接缝 —— 让 <see cref="GeoDatabase"/> 既能跑嵌入式 SQLite, 也能连 openGauss。
///
/// 为什么要这层: 原程序(及本移植的第一版)把 SQLite 焊死在数据层, 换库要动 269 处。
/// 现在调用点统一降到 <see cref="DbConnection"/>/<see cref="DbCommand"/>(见 DbCompat.cs),
/// 剩下真正"每家不一样"的东西——建连、建表 DDL、外键开关、参数前缀——
/// 全部收敛到这里。加一种库 = 加一个子类, 不再满仓库改。
///
/// 选哪种由环境变量决定(默认 SQLite, 行为与移植前逐字一致):
///   PITMINE_DB=sqlite | opengauss   缺省 sqlite
///   PITMINE_DB_CONN=&lt;连接串&gt;   opengauss 必填,
///        例: Host=192.168.1.10;Port=5432;Database=pitmine;Username=pitmine;Password=***
/// </summary>
public abstract class GeoDbDialect
{
    public abstract string Name { get; }

    /// <summary>建立(未打开的)连接。<paramref name="path"/> 仅嵌入式库用得上。</summary>
    public abstract DbConnection CreateConnection(string? path);

    /// <summary>本方言的迁移脚本在资源名里的标记段, 用来挑对应的一套 SQL。</summary>
    public abstract string MigrationMarker { get; }

    /// <summary>
    /// 确保迁移登记表存在。做成方法而不是裸 DDL: 各家的建表语法与"存在则跳过"写法不同,
    /// 有的要先查数据字典再决定建不建。
    /// </summary>
    public abstract void EnsureSchemaMigrationTable(DbConnection conn);

    /// <summary>迁移登记表在 SQL 里的写法(某些库对下划线开头的标识符需加引号)。</summary>
    public virtual string SchemaMigrationTable => "_schema_migration";

    /// <summary>
    /// 打开连接时是否顺便把迁移跑掉。
    ///
    /// 嵌入式库(SQLite): true —— 一人一个文件, 各跑各的, 本来就该自足。
    /// 服务器库(openGauss 等): false —— 库是**共享**的。若每个客户端启动都跑迁移,
    ///   N 台机器早上同时开机就会同时建表、灌 3 万多行种子, 首次部署直接打架。
    ///   共享库上迁移是**部署动作**, 不是启动副作用: 由部署方跑一次
    ///   (PITMINE_DB_MIGRATE=1), 客户端只校验版本对不对。
    /// </summary>
    public virtual bool MigrateOnOpen => true;

    /// <summary>
    /// 迁移脚本嵌在哪个程序集里。默认是主程序集。
    /// 留这个口子是因为 SQLite 已降级为**测试专用**依赖(交付物里不含 SQLite),
    /// 它那套迁移脚本随测试程序集走, 不进产品。
    /// </summary>
    public virtual Assembly MigrationAssembly => typeof(GeoDatabase).Assembly;

    /// <summary>
    /// 按**这条连接本身**判断该用哪种方言, 而不是看全局的 <see cref="Current"/>。
    ///
    /// 需要区分的场合: 调用方可能显式拿着另一种库的连接(集成测试就是这样), 此时全局设置
    /// 说的是"程序默认连哪个库", 与手上这条连接是什么东西无关。拿全局去解释一条外来的连接,
    /// 就会出现"用 sqlite_master 去查 PG 连接"这类错配。
    /// </summary>
    public static GeoDbDialect For(DbConnection conn)
    {
        if (Current.MatchesConnection(conn)) return Current;
        var og = new OpenGaussDialect();
        if (og.MatchesConnection(conn)) return og;

        // 认不出来就**明确报错**, 不猜。猜错的后果是拿 information_schema 去查 SQLite 这类
        // 莫名其妙的错误, 排查成本远高于在这里直接说清楚。
        // 拿着非产品方言的连接(测试里的 SQLite)调用时, 请用带 dialect 参数的重载。
        throw new InvalidOperationException(
            $"认不出这条连接属于哪种方言({conn.GetType().Name})。产品只支持 openGauss; " +
            "若在测试中使用其它库, 请调用显式传 GeoDbDialect 的重载。");
    }

    /// <summary>这条连接是不是本方言建的。用于 <see cref="For"/> 区分外来连接。</summary>
    public virtual bool MatchesConnection(DbConnection conn)
        => conn.GetType().Name.StartsWith("Npgsql", StringComparison.Ordinal);

    /// <summary>
    /// 库里的用户表名(按名排序)。数据字典功能要用。
    /// 各家的系统目录完全不同(SQLite 是 sqlite_master, PG 系是 information_schema),
    /// 所以这件事归方言管, 调用方只管拿结果。
    /// </summary>
    public abstract List<string> ListTables(DbConnection conn);

    /// <summary>
    /// 一张表的列信息, 规范成 (列名, 类型, 是否非空, 是否主键)。
    /// 让方言返回规范化的元组而不是裸 reader —— SQLite 的 PRAGMA table_info 与
    /// PG 的 information_schema 列序、列数都不一样, 把下标处理关在各自实现里, 调用方不必知道。
    /// </summary>
    public abstract List<(string Name, string Type, bool NotNull, bool Pk)> TableColumns(DbConnection conn, string table);

    /// <summary>参数前缀。SQLite 与 Npgsql 都用 '@', 故当前两种方言一致; 留着是为将来换库。</summary>
    public abstract char ParamPrefix { get; }

    /// <summary>迁移开始前/结束后的钩子(SQLite 用来临时关外键)。</summary>
    public virtual void BeforeMigrations(DbConnection conn) { }
    public virtual void AfterMigrations(DbConnection conn) { }

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

    private static GeoDbDialect FromEnvironment()
    {
        // 不再只认环境变量: 局域网多客户端时连接串靠界面配置下发, 见 DbConnectionSettings。
        // 优先级仍是 环境变量 > 站点配置 > 用户配置 > 本机 SQLite。
        // 产品里只有 openGauss 一种方言 —— SQLite 已从交付物中移除(信创要求: 不含非国产数据库),
        // 只作为测试专用依赖留在测试程序集里。这里不再有"退回本机 SQLite"的分支:
        // 中心库连不上时必须明确失败, 而不是让每个客户端各写各的本地文件还以为在共享库上。
        return new OpenGaussDialect();
    }
}
