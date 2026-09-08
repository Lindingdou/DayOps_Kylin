using System.Collections.Generic;

namespace System.Data.Common;

/// <summary>
/// ADO.NET 基类缺口的补齐（移植到达梦 DM 的地基）。
///
/// 全库原先直接用 <c>SqliteConnection</c>/<c>SqliteCommand</c>，换库要改 269 处。
/// 改法是统一降到 <see cref="DbConnection"/>/<see cref="DbCommand"/> 这层 ——
/// 156 处 <c>conn.CreateCommand()</c> 本来就返回 <see cref="DbCommand"/>，天然可移植；
/// 唯一挡路的是 <c>AddWithValue</c>：它只在各家 Provider 的具体参数集合上有，
/// 基类 <see cref="DbParameterCollection"/> 没有。这里补一个同名扩展把这 72 处接住，
/// 调用点一个字都不用改。
///
/// 放在 <c>System.Data.Common</c> 命名空间下是刻意的：这样任何 <c>using System.Data.Common;</c>
/// 的文件都自动可见，不必再额外 import 一个项目命名空间。
/// </summary>
public static class DbCompatExtensions
{
    /// <summary>
    /// 等价于各 Provider 的 <c>AddWithValue</c>：按名字建参数、赋值、入集合。
    /// null 一律落 <see cref="DBNull"/> —— 直接塞 C# null 会被 Provider 当"未设置"而报错。
    /// </summary>
    public static DbParameter AddWithValue(this DbParameterCollection ps, string name, object? value)
    {
        var cmd = ps.GetType();  // 仅为取用具体 Provider 的参数对象, 见下
        throw new NotImplementedException(cmd.Name);
    }
}
