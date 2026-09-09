using PitMine3D.Kylin.Tests;
using System.Data.Common;
using System.Collections.Generic;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>
/// 设备能力约束违反判定(ViolatesConstraint) + 参数-实测值→可用/被禁机型(CompatibleModels)已知值回归。
/// 忠实原 GeoDataBase ProcessArchitectureService.Violates/CompatibleModels —— 数据: equipment_constraint(V012 种子)+ equipment_model。
/// </summary>
public class CompatibleModelsTests
{
    [Fact]
    public void Violates_min_max_range_equals_known_values()
    {
        // min: v < 限值 → 违反
        Assert.True(GeoDataQueries.ViolatesConstraint("min", 10, null, null, 8));
        Assert.False(GeoDataQueries.ViolatesConstraint("min", 10, null, null, 12));
        // max: v > 限值 → 违反
        Assert.True(GeoDataQueries.ViolatesConstraint("max", 10, null, null, 12));
        Assert.False(GeoDataQueries.ViolatesConstraint("max", 10, null, null, 8));
        // range: 出 [min,max] → 违反
        Assert.True(GeoDataQueries.ViolatesConstraint("range", null, 5, 15, 20));
        Assert.True(GeoDataQueries.ViolatesConstraint("range", null, 5, 15, 2));
        Assert.False(GeoDataQueries.ViolatesConstraint("range", null, 5, 15, 10));
        // equals: 偏离 → 违反
        Assert.True(GeoDataQueries.ViolatesConstraint("equals", 10, null, null, 11));
        Assert.False(GeoDataQueries.ViolatesConstraint("equals", 10, null, null, 10));
        // 未知类型 / 空限值 → 不违反
        Assert.False(GeoDataQueries.ViolatesConstraint("contains", null, null, null, 999));
        Assert.False(GeoDataQueries.ViolatesConstraint("min", null, null, null, 5));
    }

    [Fact]
    public void CompatibleModels_unknown_param_blocks_none()
    {
        using var db = TestDb.Open();
        var (compatible, blocked) = GeoDataQueries.CompatibleModels(db.Connection, "no_such_param_xyz", 999);
        Assert.Empty(blocked);                               // 无参数→无约束→无被禁
        // 全部机型可用(= equipment_model 全表)
        int total = (int)ScalarLong(db.Connection, "SELECT COUNT(*) FROM equipment_model");
        Assert.Equal(total, compatible.Count);
    }

    [Fact]
    public void CompatibleModels_partitions_all_models_no_overlap()
    {
        using var db = TestDb.Open();
        // 取一个有硬约束的参数 code + 一个极端值, 验 可用/被禁 划分全机型且不重叠。
        string? code;
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = @"SELECT d.code FROM parameter_definition d
                JOIN equipment_constraint c ON c.param_id = d.param_id AND c.consequence='hard' AND c.is_active=1
                WHERE d.is_active=1 LIMIT 1";
            code = cmd.ExecuteScalar() as string;
        }
        Assert.NotNull(code);   // 种子库应有带硬约束的参数(V012)
        int total = (int)ScalarLong(db.Connection, "SELECT COUNT(*) FROM equipment_model");
        // 极大值 + 极小值都试, 至少一端应禁掉某些 min/max/range 约束的机型。
        var (cHi, bHi) = GeoDataQueries.CompatibleModels(db.Connection, code!, 1e12);
        var (cLo, bLo) = GeoDataQueries.CompatibleModels(db.Connection, code!, -1e12);
        Assert.Equal(total, cHi.Count + bHi.Count);          // 划分完整(无重叠, 因 compatible=全表−blocked)
        Assert.Equal(total, cLo.Count + bLo.Count);
        var setHi = new HashSet<string>(cHi);
        Assert.All(bHi, m => Assert.DoesNotContain(m, setHi)); // 不重叠
        Assert.True(bHi.Count > 0 || bLo.Count > 0);          // 极端值至少一端禁掉某机型(证约束生效)
    }

    private static long ScalarLong(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return System.Convert.ToInt64(cmd.ExecuteScalar());
    }
}
