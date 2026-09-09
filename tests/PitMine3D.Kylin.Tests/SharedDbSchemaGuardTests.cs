using System.Collections.Generic;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 共享库(局域网内的 openGauss)上的 schema 版本守卫。
///
/// 嵌入式 SQLite 是一人一个文件, 启动时顺手把迁移跑完没问题。
/// 共享库不行: 十几台客户端早上同时开机, 会同时建表、灌 3 万多行种子, 首次部署直接打架。
/// 所以共享库上迁移是**部署动作**(PITMINE_DB_MIGRATE=1 跑一次), 客户端启动只校验版本,
/// 缺了就明确报错拒绝启动 —— 好过带着不匹配的 schema 半死不活地跑、写坏数据。
///
/// 这里测的是版本比对那段纯逻辑。刻意不去改 GeoDbDialect 的全局状态来构造场景:
/// 本仓库有过共享可变 static 引发并发测试偶发失败的前科, 不再往里加。
/// </summary>
public class SharedDbSchemaGuardTests
{
    [Fact]
    public void 库里版本齐全时_不报缺()
    {
        var embedded = new[] { "V001_a", "V002_b", "V003_c" };
        var applied = new HashSet<string> { "V001_a", "V002_b", "V003_c" };

        Assert.Empty(GeoDatabase.MissingMigrations(embedded, applied));
    }

    [Fact]
    public void 库里少了迁移_按顺序列出缺的()
    {
        var embedded = new[] { "V001_a", "V002_b", "V003_c", "V004_d" };
        var applied = new HashSet<string> { "V001_a", "V003_c" };

        Assert.Equal(new[] { "V002_b", "V004_d" }, GeoDatabase.MissingMigrations(embedded, applied));
    }

    [Fact]
    public void 全新空库_缺全部()
    {
        var embedded = new[] { "V001_a", "V002_b" };

        Assert.Equal(embedded, GeoDatabase.MissingMigrations(embedded, new HashSet<string>()));
    }

    [Fact]
    public void 库里版本比程序新_不算缺()
    {
        // 部署方已升级数据库、个别客户端还是旧程序: 这不是"缺迁移", 不该拦。
        // (旧客户端能不能安全用新 schema 是另一个问题, 由发版纪律管, 不在本守卫职责内。)
        var embedded = new[] { "V001_a", "V002_b" };
        var applied = new HashSet<string> { "V001_a", "V002_b", "V003_future" };

        Assert.Empty(GeoDatabase.MissingMigrations(embedded, applied));
    }

    [Fact]
    public void 嵌入式方言启动即迁移_共享方言不迁移()
    {
        // 这条锁住"谁在启动时跑迁移"这个策略本身 —— 将来若有人给 openGauss 方言
        // 把 MigrateOnOpen 改回 true, 会在这里红。
        Assert.True(new SqliteDialect().MigrateOnOpen);
        Assert.False(new OpenGaussDialect().MigrateOnOpen);
    }

    [Fact]
    public void 嵌入式库照旧能自足建库()
    {
        // 回归: 改了启动逻辑后, SQLite 路径必须还是"打开即可用"。
        using var db = TestDb.Open();
        Assert.True(db.AppliedMigrationCount() >= 50);
    }
}
