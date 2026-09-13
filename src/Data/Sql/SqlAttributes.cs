// 忠实移植自原 PitMine3D Modules/SqlLib/Public/Attributes.cs + IRepository.cs + ITransactionScope.cs + SqlLibException.cs
//（逐行对应；仅命名空间适配）。GeoDataBase 的实体 POCO 靠这些特性映射到表/列。
using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Data.Sql;

/// <summary>把 POCO 类映射到数据库表。配合 IRepository 使用。</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute : Attribute
{
    public string Name { get; }
    public TableAttribute(string name) => Name = name;
}

/// <summary>把属性映射到数据库列。若属性名等于列名,可省略本特性。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute : Attribute
{
    public string Name { get; }
    public ColumnAttribute(string name) => Name = name;
}

/// <summary>标记主键属性。复合主键标多个。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PrimaryKeyAttribute : Attribute
{
}

/// <summary>标记自增主键(配合 INTEGER PRIMARY KEY)。仅一个属性可标。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AutoIncrementAttribute : Attribute
{
}

/// <summary>该属性不映射到数据库列(用于派生/瞬时字段)。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotMappedAttribute : Attribute
{
}

/// <summary>列的中文/业务描述,用于数据字典生成。</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class ColumnDescriptionAttribute : Attribute
{
    public string Description { get; }
    public ColumnDescriptionAttribute(string description) => Description = description;
}

/// <summary>
/// 泛型仓储。基于实体类的 [Table] / [Column] / [PrimaryKey] 特性反射生成 SQL。
/// 适合简单 CRUD,复杂查询请直接走 ISqlService.Query&lt;T&gt;。
/// </summary>
public interface IRepository<T> where T : class, new()
{
    /// <summary>按主键查找。复合主键传 new { col1 = ..., col2 = ... }。</summary>
    T? GetByKey(object key);

    /// <summary>全表(谨慎用于大表)。</summary>
    IReadOnlyList<T> All();

    /// <summary>条件查询。where 子句以 "WHERE" 开头省略,直接写 "category = @cat"。</summary>
    IReadOnlyList<T> Where(string whereClause, object? parameters = null);

    /// <summary>插入一行,返回新主键(自增时)或 1。</summary>
    long Insert(T entity);

    /// <summary>批量插入,内部走事务。</summary>
    int BulkInsert(IEnumerable<T> entities);

    /// <summary>更新一行,按主键。返回受影响行数。</summary>
    int Update(T entity);

    /// <summary>主键存在则 UPDATE,否则 INSERT。返回 1。</summary>
    int Upsert(T entity);

    /// <summary>按主键删除。</summary>
    int Delete(object key);

    /// <summary>条件删除。</summary>
    int DeleteWhere(string whereClause, object? parameters = null);

    /// <summary>计数。</summary>
    long Count(string? whereClause = null, object? parameters = null);
}

/// <summary>
/// 事务作用域。using 块退出未 Commit 时自动 Rollback。
/// 嵌套调用会复用最外层事务。
/// </summary>
public interface ITransactionScope : IDisposable
{
    void Commit();
    void Rollback();
    bool IsCommitted { get; }
    bool IsRolledBack { get; }
}

/// <summary>SqlLib 抛出的基础异常。</summary>
public class SqlLibException : Exception
{
    public SqlLibException(string message) : base(message) { }
    public SqlLibException(string message, Exception inner) : base(message, inner) { }
}
