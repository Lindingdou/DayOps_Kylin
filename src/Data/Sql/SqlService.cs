// 忠实移植自原 PitMine3D Modules/SqlLib/Internal/{SqlService,GenericRepository,EntityMapper,TransactionScope,SqliteDialect}.cs
//（逐行对应；仅命名空间/依赖适配）。
//
// 适配点（全部与"换库"有关，语义不变）：
//  · 原版持 SqliteConnection；Kylin 数据层统一降到 DbConnection（SQLite 测试 / openGauss 产品），
//    Dapper 本来就扩展在 IDbConnection 上，Query/Execute 一字不改。
//  · 标识符转义沿用双引号（SQLite 与 PostgreSQL 系都认）；INSERT 后取自增键
//    SQLite 用 last_insert_rowid()，PG 用 lastval() —— 按连接类型挑。
//  · 原 SqlService 还带 Schema/Migration/GetColumns 等 DDL 反射；Kylin 的迁移由 GeoDatabase 负责，
//    这里只保留 TaskLib/GeoDataBase 服务层用到的那一截：Query / QueryFirstOrDefault / ExecuteScalar /
//    Execute / BeginTransaction / Repository<T> / TableExists。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using Dapper;

namespace PitMine3D.Kylin.Data.Sql;

/// <summary>
/// SqlLib 主门面（Kylin 截取版）。任何模块拿着一条 <see cref="DbConnection"/> 就能造一个。
/// </summary>
public interface ISqlService
{
    /// <summary>执行 SELECT,流式映射到 POCO。参数用匿名对象 new { id = 1 }。</summary>
    IEnumerable<T> Query<T>(string sql, object? parameters = null);

    /// <summary>执行 SELECT 取首行,无行返回 default。</summary>
    T? QueryFirstOrDefault<T>(string sql, object? parameters = null);

    /// <summary>执行 SELECT 取单列单值。</summary>
    T? ExecuteScalar<T>(string sql, object? parameters = null);

    /// <summary>执行 INSERT / UPDATE / DELETE / DDL,返回受影响行数。</summary>
    int Execute(string sql, object? parameters = null);

    /// <summary>开启事务作用域。using 块退出未提交时自动回滚。</summary>
    ITransactionScope BeginTransaction(IsolationLevel level = IsolationLevel.ReadCommitted);

    /// <summary>通用仓储(基于反射,无需手写 SQL)</summary>
    IRepository<T> Repository<T>() where T : class, new();

    bool TableExists(string tableName);

    /// <summary>批量(性能关键)</summary>
    void BulkInsert<T>(string tableName, IEnumerable<T> entities);
    void BulkUpsert<T>(string tableName, IEnumerable<T> entities, params string[] keyColumns);

    /// <summary>底层连接（给还在写裸 ADO.NET 的老代码用）。</summary>
    DbConnection Connection { get; }
}

/// <summary>
/// ISqlService 主实现。持有一个长生命连接 + Dapper。
/// 桌面单进程场景,不引入 connection pool。
/// </summary>
public sealed class SqlService : ISqlService
{
    private readonly DbConnection _conn;
    private readonly object _lock = new();
    private TransactionScope? _currentTx;
    private readonly Action<string>? _logger;

    static SqlService()
    {
        // 全局只设一次:让 Dapper 自动把 snake_case 列名映射到 PascalCase 属性。
        // 否则 [Column("borehole_id")] -> BoreholeId 不会被反射识别(Dapper 不读 [Column]),
        // 所有 entity 反序列化会全 default。
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqlService(DbConnection conn, Action<string>? logger = null)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _logger = logger;
    }

    public DbConnection Connection => _conn;
    internal DbTransaction? CurrentTransaction => _currentTx?.Inner;
    internal object SyncRoot => _lock;

    /// <summary>这条连接是不是 SQLite（自增键取法不同）。</summary>
    internal bool IsSqlite => _conn.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

    internal void Log(string message) => _logger?.Invoke(message);

    // ========== Query (透传 Dapper) ==========

    public IEnumerable<T> Query<T>(string sql, object? parameters = null)
    {
        lock (_lock)
        {
            try { return _conn.Query<T>(sql, parameters, _currentTx?.Inner).ToList(); }
            catch (Exception ex) { throw new SqlLibException($"Query 失败:{sql}\n{ex.Message}", ex); }
        }
    }

    public T? QueryFirstOrDefault<T>(string sql, object? parameters = null)
    {
        lock (_lock)
        {
            try { return _conn.QueryFirstOrDefault<T>(sql, parameters, _currentTx?.Inner); }
            catch (Exception ex) { throw new SqlLibException($"QueryFirstOrDefault 失败:{sql}\n{ex.Message}", ex); }
        }
    }

    public T? ExecuteScalar<T>(string sql, object? parameters = null)
    {
        lock (_lock)
        {
            try { return _conn.ExecuteScalar<T>(sql, parameters, _currentTx?.Inner); }
            catch (Exception ex) { throw new SqlLibException($"ExecuteScalar 失败:{sql}\n{ex.Message}", ex); }
        }
    }

    public int Execute(string sql, object? parameters = null)
    {
        lock (_lock)
        {
            try { return _conn.Execute(sql, parameters, _currentTx?.Inner); }
            catch (Exception ex) { throw new SqlLibException($"Execute 失败:{sql}\n{ex.Message}", ex); }
        }
    }

    // ========== Transaction ==========

    public ITransactionScope BeginTransaction(IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        lock (_lock)
        {
            if (_currentTx != null && !_currentTx.IsCommitted && !_currentTx.IsRolledBack)
            {
                // 嵌套:直接复用外层(不支持真正的 savepoint)
                return new NestedScope(_currentTx);
            }

            var raw = _conn.BeginTransaction(level);
            _currentTx = new TransactionScope(raw, () => { lock (_lock) _currentTx = null; });
            return _currentTx;
        }
    }

    // ========== Repository ==========

    public IRepository<T> Repository<T>() where T : class, new()
        => new GenericRepository<T>(this);

    public bool TableExists(string tableName)
    {
        try
        {
            if (IsSqlite)
                return ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n", new { n = tableName }) > 0;
            return ExecuteScalar<long>("SELECT COUNT(*) FROM information_schema.tables WHERE table_name=@n", new { n = tableName }) > 0;
        }
        catch { return false; }
    }

    // ========== 批量 ==========

    public void BulkInsert<T>(string tableName, IEnumerable<T> entities)
    {
        var list = entities.ToList();
        if (list.Count == 0) return;

        var mapping = EntityMapper.GetMapping(typeof(T));
        var cols = mapping.Columns.Where(c => !c.IsAutoIncrement).ToList();
        var colNames = string.Join(", ", cols.Select(c => SqlDialect.QuoteIdent(c.ColumnName)));
        var paramNames = string.Join(", ", cols.Select(c => "@" + c.PropertyName));
        var sql = $"INSERT INTO {SqlDialect.QuoteIdent(tableName)} ({colNames}) VALUES ({paramNames})";

        using var tx = BeginTransaction();
        foreach (var e in list)
            Execute(sql, e);
        tx.Commit();
    }

    public void BulkUpsert<T>(string tableName, IEnumerable<T> entities, params string[] keyColumns)
    {
        var list = entities.ToList();
        if (list.Count == 0) return;

        var mapping = EntityMapper.GetMapping(typeof(T));
        var cols = mapping.Columns.Where(c => !c.IsAutoIncrement).ToList();
        var colNames = string.Join(", ", cols.Select(c => SqlDialect.QuoteIdent(c.ColumnName)));
        var paramNames = string.Join(", ", cols.Select(c => "@" + c.PropertyName));

        var keys = keyColumns.Length > 0
            ? keyColumns
            : mapping.Columns.Where(c => c.IsPrimaryKey).Select(c => c.ColumnName).ToArray();
        if (keys.Length == 0)
            throw new SqlLibException("BulkUpsert 需要至少 1 个主键列");

        var updateCols = cols.Where(c => !keys.Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase)).ToList();
        var updateAssign = string.Join(", ",
            updateCols.Select(c => $"{SqlDialect.QuoteIdent(c.ColumnName)} = excluded.{SqlDialect.QuoteIdent(c.ColumnName)}"));
        var conflictTarget = string.Join(", ", keys.Select(SqlDialect.QuoteIdent));

        var sql = $@"INSERT INTO {SqlDialect.QuoteIdent(tableName)} ({colNames}) VALUES ({paramNames})
                     ON CONFLICT({conflictTarget}) DO UPDATE SET {updateAssign}";

        using var tx = BeginTransaction();
        foreach (var e in list)
            Execute(sql, e);
        tx.Commit();
    }

    /// <summary>嵌套事务作用域:不真的开新事务,只在最外层时才提交。</summary>
    private sealed class NestedScope : ITransactionScope
    {
        private readonly TransactionScope _outer;
        public NestedScope(TransactionScope outer) => _outer = outer;
        public bool IsCommitted => _outer.IsCommitted;
        public bool IsRolledBack => _outer.IsRolledBack;
        public void Commit() { /* 由最外层 Commit */ }
        public void Rollback() => _outer.Rollback();
        public void Dispose() { /* 不释放 */ }
    }
}

internal sealed class TransactionScope : ITransactionScope
{
    private readonly DbTransaction _tx;
    private readonly Action _onDispose;
    private bool _disposed;

    public bool IsCommitted { get; private set; }
    public bool IsRolledBack { get; private set; }

    internal DbTransaction Inner => _tx;

    public TransactionScope(DbTransaction tx, Action onDispose)
    {
        _tx = tx;
        _onDispose = onDispose;
    }

    public void Commit()
    {
        if (IsCommitted || IsRolledBack) return;
        _tx.Commit();
        IsCommitted = true;
    }

    public void Rollback()
    {
        if (IsCommitted || IsRolledBack) return;
        _tx.Rollback();
        IsRolledBack = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!IsCommitted && !IsRolledBack)
        {
            // 隐式回滚
            try { _tx.Rollback(); } catch { /* 已被外部释放等情况吞掉 */ }
            IsRolledBack = true;
        }
        _tx.Dispose();
        _onDispose();
    }
}

/// <summary>标识符转义（SQLite 与 PostgreSQL 系都认双引号）。</summary>
internal static class SqlDialect
{
    public static string QuoteIdent(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return "\"" + name.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>
/// 把 POCO 类的属性 ↔ 数据库列做映射缓存。
/// 读 [Table] / [Column] / [PrimaryKey] / [AutoIncrement] / [NotMapped] 特性。
/// </summary>
internal static class EntityMapper
{
    private static readonly ConcurrentDictionary<Type, TableMapping> _cache = new();

    public static TableMapping GetMapping(Type type) => _cache.GetOrAdd(type, BuildMapping);

    private static TableMapping BuildMapping(Type type)
    {
        var tableAttr = type.GetCustomAttribute<TableAttribute>();
        var tableName = tableAttr?.Name ?? type.Name;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<NotMappedAttribute>() == null
                     && p.CanRead && p.CanWrite)
            .ToList();

        var cols = new List<ColumnMapping>();
        foreach (var p in props)
        {
            var colAttr = p.GetCustomAttribute<ColumnAttribute>();
            cols.Add(new ColumnMapping
            {
                PropertyName = p.Name,
                ColumnName = colAttr?.Name ?? p.Name,
                ClrType = p.PropertyType,
                IsPrimaryKey = p.GetCustomAttribute<PrimaryKeyAttribute>() != null,
                IsAutoIncrement = p.GetCustomAttribute<AutoIncrementAttribute>() != null,
                Property = p,
                Description = p.GetCustomAttribute<ColumnDescriptionAttribute>()?.Description
            });
        }

        return new TableMapping
        {
            ClrType = type,
            TableName = tableName,
            Columns = cols,
            TableDescription = type.GetCustomAttribute<ColumnDescriptionAttribute>()?.Description
        };
    }
}

internal sealed class TableMapping
{
    public Type ClrType { get; init; } = typeof(object);
    public string TableName { get; init; } = "";
    public string? TableDescription { get; init; }
    public List<ColumnMapping> Columns { get; init; } = new();

    public IEnumerable<ColumnMapping> PrimaryKeys => Columns.Where(c => c.IsPrimaryKey);
}

internal sealed class ColumnMapping
{
    public string PropertyName { get; init; } = "";
    public string ColumnName { get; init; } = "";
    public Type ClrType { get; init; } = typeof(object);
    public bool IsPrimaryKey { get; init; }
    public bool IsAutoIncrement { get; init; }
    public string? Description { get; init; }
    public PropertyInfo Property { get; init; } = default!;
}

/// <summary>
/// 基于反射的通用仓储。
/// 简单 CRUD 用这个,复杂查询直接走 ISqlService.Query。
/// </summary>
internal sealed class GenericRepository<T> : IRepository<T> where T : class, new()
{
    private readonly SqlService _sql;
    private readonly TableMapping _map;

    public GenericRepository(SqlService sql)
    {
        _sql = sql;
        _map = EntityMapper.GetMapping(typeof(T));
        if (_map.Columns.Count == 0)
            throw new SqlLibException($"实体 {typeof(T).Name} 没有可映射的属性");
    }

    private static string Q(string n) => SqlDialect.QuoteIdent(n);

    public T? GetByKey(object key)
    {
        var pks = _map.PrimaryKeys.ToList();
        if (pks.Count == 0)
            throw new SqlLibException($"{_map.TableName} 没有 [PrimaryKey],无法 GetByKey");

        string sql;
        object param;
        if (pks.Count == 1)
        {
            sql = $"SELECT * FROM {Q(_map.TableName)} WHERE {Q(pks[0].ColumnName)} = @key";
            var dp = new DynamicParameters();
            dp.Add("key", key);
            param = dp;
        }
        else
        {
            // 复合主键传 new { col1 = ..., col2 = ... }
            var cond = string.Join(" AND ", pks.Select(p => $"{Q(p.ColumnName)} = @{p.PropertyName}"));
            sql = $"SELECT * FROM {Q(_map.TableName)} WHERE {cond}";
            param = key;
        }
        return _sql.QueryFirstOrDefault<T>(sql, param);
    }

    public IReadOnlyList<T> All()
        => _sql.Query<T>($"SELECT * FROM {Q(_map.TableName)}").ToList();

    public IReadOnlyList<T> Where(string whereClause, object? parameters = null)
        => _sql.Query<T>($"SELECT * FROM {Q(_map.TableName)} WHERE {whereClause}", parameters).ToList();

    public long Insert(T entity)
    {
        var cols = _map.Columns.Where(c => !c.IsAutoIncrement).ToList();
        var colSql = string.Join(", ", cols.Select(c => Q(c.ColumnName)));
        var paramSql = string.Join(", ", cols.Select(c => "@" + c.PropertyName));
        var hasAuto = _map.Columns.Any(c => c.IsAutoIncrement);

        var sql = $"INSERT INTO {Q(_map.TableName)} ({colSql}) VALUES ({paramSql}); "
                + (hasAuto ? (_sql.IsSqlite ? "SELECT last_insert_rowid();" : "SELECT lastval();") : "SELECT 1;");

        return _sql.ExecuteScalar<long>(sql, entity);
    }

    public int BulkInsert(IEnumerable<T> entities)
    {
        var list = entities.ToList();
        if (list.Count == 0) return 0;
        var cols = _map.Columns.Where(c => !c.IsAutoIncrement).ToList();
        var colSql = string.Join(", ", cols.Select(c => Q(c.ColumnName)));
        var paramSql = string.Join(", ", cols.Select(c => "@" + c.PropertyName));
        var sql = $"INSERT INTO {Q(_map.TableName)} ({colSql}) VALUES ({paramSql})";
        using var tx = _sql.BeginTransaction();
        foreach (var e in list) _sql.Execute(sql, e);
        tx.Commit();
        return list.Count;
    }

    public int Update(T entity)
    {
        var pks = _map.PrimaryKeys.ToList();
        if (pks.Count == 0)
            throw new SqlLibException($"{_map.TableName} 没有 [PrimaryKey],无法 Update");

        var nonPk = _map.Columns.Where(c => !c.IsPrimaryKey).ToList();
        var setSql = string.Join(", ", nonPk.Select(c => $"{Q(c.ColumnName)} = @{c.PropertyName}"));
        var whereSql = string.Join(" AND ", pks.Select(c => $"{Q(c.ColumnName)} = @{c.PropertyName}"));

        return _sql.Execute($"UPDATE {Q(_map.TableName)} SET {setSql} WHERE {whereSql}", entity);
    }

    public int Upsert(T entity)
    {
        var pks = _map.PrimaryKeys.ToList();
        if (pks.Count == 0)
            throw new SqlLibException($"{_map.TableName} 没有 [PrimaryKey],无法 Upsert");

        var cols = _map.Columns.Where(c => !c.IsAutoIncrement).ToList();
        var colSql = string.Join(", ", cols.Select(c => Q(c.ColumnName)));
        var paramSql = string.Join(", ", cols.Select(c => "@" + c.PropertyName));
        var conflictCols = string.Join(", ", pks.Select(c => Q(c.ColumnName)));
        var updateSql = string.Join(", ", cols
            .Where(c => !c.IsPrimaryKey)
            .Select(c => $"{Q(c.ColumnName)} = excluded.{Q(c.ColumnName)}"));

        var sql = $@"INSERT INTO {Q(_map.TableName)} ({colSql}) VALUES ({paramSql})
                     ON CONFLICT({conflictCols}) DO UPDATE SET {updateSql}";
        return _sql.Execute(sql, entity);
    }

    public int Delete(object key)
    {
        var pks = _map.PrimaryKeys.ToList();
        if (pks.Count == 0)
            throw new SqlLibException($"{_map.TableName} 没有 [PrimaryKey],无法 Delete");

        if (pks.Count == 1)
        {
            var p = new DynamicParameters();
            p.Add("key", key);
            return _sql.Execute($"DELETE FROM {Q(_map.TableName)} WHERE {Q(pks[0].ColumnName)} = @key", p);
        }
        var cond = string.Join(" AND ", pks.Select(p => $"{Q(p.ColumnName)} = @{p.PropertyName}"));
        return _sql.Execute($"DELETE FROM {Q(_map.TableName)} WHERE {cond}", key);
    }

    public int DeleteWhere(string whereClause, object? parameters = null)
        => _sql.Execute($"DELETE FROM {Q(_map.TableName)} WHERE {whereClause}", parameters);

    public long Count(string? whereClause = null, object? parameters = null)
    {
        var sql = $"SELECT COUNT(*) FROM {Q(_map.TableName)}";
        if (!string.IsNullOrEmpty(whereClause)) sql += " WHERE " + whereClause;
        return _sql.ExecuteScalar<long>(sql, parameters);
    }
}
