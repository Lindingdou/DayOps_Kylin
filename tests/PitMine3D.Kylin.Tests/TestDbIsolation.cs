using System;
using System.Runtime.CompilerServices;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 把整个测试程序集钉死在本机 SQLite 上。
///
/// **为什么必须有这个**: 数据库连接是**机器级**配置(用户目录下的 db.json / 站点的 db.conf)。
/// 只要开发机上把程序切到了局域网的 openGauss, 所有直接调 <c>GeoDatabase.OpenSeeded()</c> 的测试
/// 就会跟着连到那个共享库上 —— 它们本意是开一个自己的内存库, 结果往真库里写测试数据。
///
/// 这不是假设: 2026-09-08 21:42 的一次全量测试就往远程库里插进了 4 个钻孔(D1/D3/NEW-A/T-NEW-1)、
/// 6 条煤质化验、2 个观测点。测试全绿, 库被污染, 而且没有任何迹象提示出了问题 ——
/// 正是最难发现的那类事故。
///
/// 环境变量在 <see cref="PitMine3D.Kylin.Data.DbConnectionSettings"/> 的优先级里最高,
/// 所以在这里设一次就能压过机器上的任何配置。ModuleInitializer 保证它在任何测试之前执行。
/// </summary>
internal static class TestDbIsolation
{
    [ModuleInitializer]
    internal static void PinToSqlite()
    {
        Environment.SetEnvironmentVariable("PITMINE_DB", "sqlite");
        Environment.SetEnvironmentVariable("PITMINE_DB_CONN", null);
    }
}
