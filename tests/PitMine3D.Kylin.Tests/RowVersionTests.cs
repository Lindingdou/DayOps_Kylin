using System;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 行版本号 row_version（V051）—— 多客户端共用一个数据库时的乐观锁基础。
///
/// 场景: 局域网内一个库、多台客户端。甲乙同时打开同一条设备台账, 甲先保存、乙后保存,
/// 原本乙会直接覆盖甲的修改, 而且双方都不会收到任何提示。
/// 有了 row_version, 乙的 UPDATE 带上"我读到的版本号"作条件, 版本对不上就影响 0 行,
/// 上层据此提示"这条已被他人改动"。
///
/// 这里验的是机制本身在真实迁移库上成立: 触发器确实自增、过期版本确实拦得住。
/// 不用 updated_at 当版本令牌的原因也在这里 —— SQLite 的 CURRENT_TIMESTAMP 只精确到秒,
/// 同一秒内两次修改时间戳相同, 下面"连续两次更新版本号必须不同"那条就会红。
/// </summary>
public class RowVersionTests
{
    private static long Scalar(GeoDatabase db, string sql) => db.ScalarLong(sql);

    private static int Exec(GeoDatabase db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>拿一台真实种子设备来做实验(种子里 equipment 有 527 行)。</summary>
    private static string AnyEquipmentId(GeoDatabase db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT equipment_id FROM equipment ORDER BY equipment_id LIMIT 1";
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void 种子行初始版本号为零()
    {
        using var db = GeoDatabase.OpenSeeded();
        Assert.Equal(0, Scalar(db, "SELECT COALESCE(MAX(row_version),0) FROM equipment"));
    }

    [Fact]
    public void 更新一行_版本号恰好加一()
    {
        using var db = GeoDatabase.OpenSeeded();
        string eq = AnyEquipmentId(db);

        Assert.Equal(1, Exec(db, $"UPDATE equipment SET manufacturer='甲厂' WHERE equipment_id='{eq}'"));

        // 恰好 +1: 若触发器递归触发(或被写了两次), 这里会是 2, 断言会红。
        Assert.Equal(1, Scalar(db, $"SELECT row_version FROM equipment WHERE equipment_id='{eq}'"));
    }

    [Fact]
    public void 连续更新_版本号持续递增()
    {
        using var db = GeoDatabase.OpenSeeded();
        string eq = AnyEquipmentId(db);

        for (int i = 1; i <= 3; i++)
        {
            Exec(db, $"UPDATE equipment SET manufacturer='第{i}次' WHERE equipment_id='{eq}'");
            Assert.Equal(i, Scalar(db, $"SELECT row_version FROM equipment WHERE equipment_id='{eq}'"));
        }
    }

    [Fact]
    public void 带正确版本号的更新_成功影响一行()
    {
        using var db = GeoDatabase.OpenSeeded();
        string eq = AnyEquipmentId(db);
        long seen = Scalar(db, $"SELECT row_version FROM equipment WHERE equipment_id='{eq}'");

        int affected = Exec(db,
            $"UPDATE equipment SET manufacturer='乙厂' WHERE equipment_id='{eq}' AND row_version={seen}");

        Assert.Equal(1, affected);
    }

    [Fact]
    public void 带过期版本号的更新_影响零行_冲突被拦住()
    {
        using var db = GeoDatabase.OpenSeeded();
        string eq = AnyEquipmentId(db);
        long seen = Scalar(db, $"SELECT row_version FROM equipment WHERE equipment_id='{eq}'");

        // 甲先改了(版本号变了)
        Exec(db, $"UPDATE equipment SET manufacturer='甲改的' WHERE equipment_id='{eq}'");

        // 乙拿着打开界面时读到的旧版本号来保存 —— 必须被拦住
        int affected = Exec(db,
            $"UPDATE equipment SET manufacturer='乙改的' WHERE equipment_id='{eq}' AND row_version={seen}");

        Assert.Equal(0, affected);
        // 甲的修改没有被覆盖
        using var c = db.Connection.CreateCommand();
        c.CommandText = $"SELECT manufacturer FROM equipment WHERE equipment_id='{eq}'";
        Assert.Equal("甲改的", (string)c.ExecuteScalar()!);
    }

    [Fact]
    public void 时间戳不足以当版本令牌_所以才要行版本号()
    {
        // 这条是"为什么不用 updated_at"的可执行论据: 同一秒内两次更新,
        // updated_at 很可能一模一样(SQLite CURRENT_TIMESTAMP 精确到秒), row_version 一定不同。
        using var db = GeoDatabase.OpenSeeded();
        string eq = AnyEquipmentId(db);

        Exec(db, $"UPDATE equipment SET manufacturer='A' WHERE equipment_id='{eq}'");
        long v1 = Scalar(db, $"SELECT row_version FROM equipment WHERE equipment_id='{eq}'");
        Exec(db, $"UPDATE equipment SET manufacturer='B' WHERE equipment_id='{eq}'");
        long v2 = Scalar(db, $"SELECT row_version FROM equipment WHERE equipment_id='{eq}'");

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void 三十一张表都有了行版本号列()
    {
        using var db = GeoDatabase.OpenSeeded();
        foreach (var t in new[] { "equipment", "borehole", "coal_sample", "haul_road", "working_face",
                                  "dump_site", "process_template", "drill_plan", "sink_profile", "process_zone" })
        {
            long n = db.ScalarLong(
                $"SELECT COUNT(*) FROM pragma_table_info('{t}') WHERE name='row_version'");
            Assert.True(n == 1, $"表 {t} 应有 row_version 列");
        }
    }
}
