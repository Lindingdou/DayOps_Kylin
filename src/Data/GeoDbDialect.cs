using System;
using System.Collections.Generic;
using System.Data.Common;
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
        string kind = (Environment.GetEnvironmentVariable("PITMINE_DB") ?? "sqlite").Trim().ToLowerInvariant();
        return kind switch
        {
            "opengauss" or "og" or "gauss" or "pg" or "postgres" => new OpenGaussDialect(),
            _ => new SqliteDialect(),
        };
    }
}
