using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 把数据层里"只需要一个连接"的查询方法, 对着**真实 openGauss** 全部跑一遍。
///
/// 为什么需要这个: 迁移能建起库, 只证明 DDL 和种子没问题, 不证明**应用层的 SQL** 能跑。
/// 应用层里有一批东西是移植时没法静态断定的 ——
///   · 87 处把 date / status / value / year 这类词直接当列名用(PG 里多数是非保留字, 但不是全部);
///   · 407 个 '@' 参数(Npgsql 认, 但个别写法可能被解析成别的东西);
///   · SQLite 特有函数(IFNULL / strftime / julianday)残留在应用层 SQL 里。
/// 这些只有真连上去跑才会暴露, 而且暴露的方式通常是运行时报错, 不是编译错。
///
/// 一个个手点 24 个数据库页面既慢又漏, 反射扫一遍几秒钟且不漏。
/// 失败会把**方法名 + 错误**一并列出, 直接就是待修清单。
///
/// 默认不跑: 只有设了 PITMINE_TEST_PG_CONN 才连库。
/// </summary>
// 同一个 collection = 不并行。扫描依赖迁移先把表建好, 并行跑会扫到空库,
// 报一堆 relation does not exist —— 那是测试顺序问题, 不是移植问题。
[Collection("openGauss")]
public class OpenGaussQuerySweepTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("PITMINE_TEST_PG_CONN");

    [Fact]
    public void 全部单连接参数的查询方法都能在openGauss上跑通()
    {
        if (string.IsNullOrWhiteSpace(Conn)) return;

        Environment.SetEnvironmentVariable("PITMINE_DB_CONN", Conn);
        using var conn = new OpenGaussDialect().CreateConnection(null);
        conn.Open();

        var targets = typeof(GeoDataQueries).Assembly.GetTypes()
            .Where(t => t.Namespace == "PitMine3D.Kylin.Data" && t.IsPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == 1 && typeof(DbConnection).IsAssignableFrom(ps[0].ParameterType);
            })
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();

        Assert.True(targets.Count >= 50, $"应当扫到几十个方法, 实际只有 {targets.Count} 个 —— 反射条件写错了");

        var fails = new List<string>();
        foreach (var m in targets)
        {
            try
            {
                m.Invoke(null, new object[] { conn });
            }
            catch (TargetInvocationException tie)
            {
                var e = tie.InnerException ?? tie;
                // 只取首行: 后面是 DETAIL/HINT, 列表里太长看不清
                string msg = e.Message.Split('\n')[0].Trim();
                fails.Add($"{m.DeclaringType!.Name}.{m.Name}  →  {msg}");
            }
        }

        Assert.True(fails.Count == 0,
            $"{targets.Count} 个方法里有 {fails.Count} 个在 openGauss 上跑不通:\n  " +
            string.Join("\n  ", fails));
    }
}
