using PitMine3D.Kylin.Data;

namespace System.Data.Common;

/// <summary>
/// ADO.NET 基类的缺口补齐 —— 换库(SQLite → openGauss)的地基。
///
/// 全库原先直接吃 <c>SqliteConnection</c> 具体类型（269 处），换库要改到吐血。
/// 改法是统一降到 <see cref="DbConnection"/>/<see cref="DbCommand"/> 这一层：
/// 156 处 <c>conn.CreateCommand()</c> 本来就返回 <see cref="DbCommand"/>，天然可移植，
/// 且全库 0 处 <c>new SqliteCommand</c>，没有硬构造要拆。
///
/// 唯一挡路的是 <c>AddWithValue</c>(129 处)：它只长在各家 Provider 的具体参数集合上，
/// 基类 <see cref="DbParameterCollection"/> 没有，而且集合本身也造不出参数对象
/// （造参数得走 <see cref="DbCommand.CreateParameter"/>）。所以这里把扩展挂在
/// <see cref="DbCommand"/> 上，调用点由 <c>cmd.Parameters.AddWithValue(..)</c>
/// 机械改成 <c>cmd.AddWithValue(..)</c>，语义一字不差。
///
/// 命名空间刻意用 <c>System.Data.Common</c>：任何 <c>using System.Data.Common;</c>
/// 的文件都自动可见，不必再逐个 import 项目命名空间。
/// </summary>
public static class DbCompatExtensions
{
    /// <summary>
    /// 等价于各 Provider 的 <c>AddWithValue</c>：按名建参、赋值、入集合。
    /// null 一律落 <see cref="DBNull"/> —— 直接塞 C# null 会被 Provider 当"参数未设置"而抛错。
    /// </summary>
    public static DbParameter AddWithValue(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        // 调用点一律写 '@x'。Npgsql 也认 '@', 故当前两种方言无需改写; 留这个唯一入口是为将来换库
        // (某些驱动要 ":x"), 129 处调用点因此不必关心自己跑在哪种库上。
        p.ParameterName = GeoDbDialect.Current.NormalizeParamName(name);
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }
}
