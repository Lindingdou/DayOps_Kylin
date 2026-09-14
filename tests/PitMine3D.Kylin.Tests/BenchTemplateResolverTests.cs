using System;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 台阶参数自动解析（§三四三）：匹配模板 → 规范默认 → 硬兜底。
///
/// 头号判据是原版踩过的那个坑：**排土场也要读得到排土模板**。
/// 早先"排土场永不读模板"，于是「排土模板」里配好的 H/α/W 存进库却永远取不到，
/// 放坡恒用硬编码默认 —— 改了没反应、还不报错。反方向同样要挡：
/// 采场不能悄悄选中排土模板（35° 安息角当成采场坡角）。
/// </summary>
public class BenchTemplateResolverTests
{
    private static void Exec(DbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ClearTemplates(DbConnection c)
    {
        Exec(c, "DELETE FROM template_param_value");
        Exec(c, "DELETE FROM process_template");
    }

    private static long AddTemplate(DbConnection c, string code, string? material = null, string? hardness = null,
                                    bool current = true, string? desc = null, string status = "active")
    {
        Exec(c, $"INSERT INTO process_template (code, name, description, applicable_material, applicable_hardness, "
              + $"is_current, status) VALUES ('{code}','{code}',"
              + (desc == null ? "NULL" : $"'{desc}'") + ","
              + (material == null ? "NULL" : $"'{material}'") + ","
              + (hardness == null ? "NULL" : $"'{hardness}'") + ","
              + (current ? 1 : 0) + $",'{status}')");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(template_id) FROM process_template";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void SetParam(DbConnection c, long tplId, string code, double value)
    {
        using var q = c.CreateCommand();
        q.CommandText = $"SELECT param_id FROM parameter_definition WHERE code = '{code}'";
        var v = q.ExecuteScalar();
        Assert.False(v == null || v is DBNull, $"种子里应有参数码 {code}");
        long pid = Convert.ToInt64(v);
        Exec(c, $"DELETE FROM template_param_value WHERE template_id={tplId} AND param_id={pid}");
        Exec(c, $"INSERT INTO template_param_value (template_id, param_id, recommended_value) "
              + $"VALUES ({tplId},{pid},{value.ToString(CultureInfo.InvariantCulture)})");
    }

    // ── 头号判据：排土场读得到排土模板 ──────────────────────
    [Fact]
    public void 排土场_读得到排土模板而不是恒用硬编码默认()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        long id = AddTemplate(db.Connection, "dump_v1", desc: BenchTemplateResolver.DumpTemplateMark + " 外排土场");
        SetParam(db.Connection, id, "bench_height", 22);
        SetParam(db.Connection, id, "bench_slope_angle", 33);
        SetParam(db.Connection, id, "safety_platform_width", 9);

        var r = BenchTemplateResolver.Resolve(db.Connection, isDump: true);
        Assert.Equal(id, r.TemplateId);
        Assert.Equal(22, r.BenchHeight, 6);
        Assert.Equal(33, r.FaceAngleDeg, 6);
        Assert.Equal(9, r.BermWidth, 6);
        Assert.Contains("排土模板", r.Provenance);
        // 不该还是硬编码的 (10,35,3)
        Assert.NotEqual(10, r.BenchHeight, 6);
    }

    [Fact]
    public void 采场_不会悄悄选中排土模板()
    {
        // ★ 反方向：35° 安息角当成采场坡角，整帮坡角就整体错了
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        long id = AddTemplate(db.Connection, "dump_only", desc: BenchTemplateResolver.DumpTemplateMark + " 外排");
        SetParam(db.Connection, id, "bench_slope_angle", 35);

        var r = BenchTemplateResolver.Resolve(db.Connection, isDump: false);
        Assert.Null(r.TemplateId);                    // 一条采场模板都没有 ⇒ 落规范默认
        Assert.Contains("规范默认", r.Provenance);
        Assert.Equal(70, r.FaceAngleDeg, 6);
    }

    [Fact]
    public void 排土场_没有排土模板时落排土规范默认()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        AddTemplate(db.Connection, "pit_v1");         // 只有采场模板
        var r = BenchTemplateResolver.Resolve(db.Connection, isDump: true);
        Assert.Null(r.TemplateId);
        Assert.Equal((10.0, 35.0, 3.0), (r.BenchHeight, r.FaceAngleDeg, r.BermWidth));
        Assert.Contains("排土场", r.Provenance);
    }

    // ── 模板评分 ────────────────────────────────────────────
    [Fact]
    public void 评分_物料对上比硬度对上更重要()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        long byMat = AddTemplate(db.Connection, "mat", material: "rh", hardness: null, current: false);
        AddTemplate(db.Connection, "hard", material: null, hardness: "hard", current: true);

        // 物料 +2 > 硬度 +1 + 现行 +0.5
        var r = BenchTemplateResolver.Resolve(db.Connection, false, material: "rh", hardness: "hard");
        Assert.Equal(byMat, r.TemplateId);
    }

    [Fact]
    public void 评分_同分时现行版胜出()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        AddTemplate(db.Connection, "old_v", material: "rh", current: false);
        long cur = AddTemplate(db.Connection, "cur_v", material: "rh", current: true);
        var r = BenchTemplateResolver.Resolve(db.Connection, false, material: "rh");
        Assert.Equal(cur, r.TemplateId);
    }

    [Fact]
    public void 评分_归档的模板不参与()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        AddTemplate(db.Connection, "archived", material: "rh", status: "archived");
        var r = BenchTemplateResolver.Resolve(db.Connection, false, material: "rh");
        Assert.Null(r.TemplateId);
    }

    [Fact]
    public void 评分_没有上下文时也挑得出现行模板()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        long id = AddTemplate(db.Connection, "any", current: true);
        Assert.Equal(id, BenchTemplateResolver.Resolve(db.Connection, false).TemplateId);
    }

    // ── 模板缺项回落 ────────────────────────────────────────
    [Fact]
    public void 缺项_模板没配的项回落规范默认而不是零()
    {
        // ★ 模板通常只配 H/α/W；工作平盘宽按 0 处理会让工作帮坡角算成 90°
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        long id = AddTemplate(db.Connection, "partial", current: true);
        SetParam(db.Connection, id, "bench_height", 15);

        var r = BenchTemplateResolver.Resolve(db.Connection, false, hardness: "hard");
        Assert.Equal(id, r.TemplateId);
        Assert.Equal(15, r.BenchHeight, 6);
        Assert.Equal(70, r.FaceAngleDeg, 6);            // 回落硬岩规范
        Assert.True(r.MinWorkingBermWidth > 0, "工作平盘宽不能落成 0");
        Assert.True(r.WorkingSlopeAngleDeg is > 0 and < 45, $"工作帮坡角应当远缓, 实得 {r.WorkingSlopeAngleDeg}");
    }

    [Fact]
    public void 缺项_采宽没配时是空而不是零()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        AddTemplate(db.Connection, "nomw", current: true);
        Assert.Null(BenchTemplateResolver.Resolve(db.Connection, false).MiningWidth);
    }

    // ── 规范默认表 ──────────────────────────────────────────
    [Theory]
    [InlineData("hard", 15.0, 70.0, 8.0)]
    [InlineData("medium", 12.0, 68.0, 6.0)]
    [InlineData("soft", 10.0, 60.0, 5.0)]
    [InlineData(null, 12.0, 70.0, 4.0)]
    [InlineData("乱写", 12.0, 70.0, 4.0)]
    public void 规范_按硬度取采场默认(string? hardness, double h, double a, double w)
        => Assert.Equal((h, a, w), BenchTemplateResolver.Norm(false, hardness));

    [Fact]
    public void 规范_排土场不看硬度()
    {
        foreach (var hd in new[] { "hard", "soft", null })
            Assert.Equal((10.0, 35.0, 3.0), BenchTemplateResolver.Norm(true, hd));
    }

    [Theory]
    [InlineData("hard", "硬岩")]
    [InlineData("medium", "中硬岩")]
    [InlineData("soft", "软岩")]
    [InlineData(null, "通用")]
    public void 规范_硬度中文名(string? h, string label)
        => Assert.Equal(label, BenchTemplateResolver.HardnessLabel(h));

    // ── 帮坡角推导 ──────────────────────────────────────────
    [Fact]
    public void 帮坡角_按台阶几何推导()
    {
        // H=15, α=90°(直立) ⇒ run = 0 + W; β = atan(15/W)
        Assert.Equal(Math.Atan(15.0 / 10.0) * 180 / Math.PI,
                     BenchTemplateResolver.OverallSlopeAngleDeg(15, 90, 10), 6);
    }

    [Fact]
    public void 帮坡角_平盘越宽帮越缓()
    {
        double narrow = BenchTemplateResolver.OverallSlopeAngleDeg(15, 70, 5);
        double wide = BenchTemplateResolver.OverallSlopeAngleDeg(15, 70, 80);
        Assert.True(wide < narrow);
        Assert.True(wide < 15, $"80m 工作平盘应当很缓, 实得 {wide:0.#}°");
    }

    [Fact]
    public void 帮坡角_退化情形不炸()
    {
        Assert.Equal(0, BenchTemplateResolver.OverallSlopeAngleDeg(0, 70, 5), 9);     // 无台阶高
        Assert.Equal(0, BenchTemplateResolver.OverallSlopeAngleDeg(15, 0, 5), 9);     // 坡面角 0
        Assert.Equal(90, BenchTemplateResolver.OverallSlopeAngleDeg(15, 90, 0), 6);   // 直立无平盘
    }

    [Fact]
    public void 帮坡角_反算平盘宽是它的逆()
    {
        double w = BenchTemplateResolver.SolveBermForOverallAngle(15, 70, 30);
        Assert.Equal(30.0, BenchTemplateResolver.OverallSlopeAngleDeg(15, 70, w), 6);
    }

    [Fact]
    public void 帮坡角_目标角比坡面角还陡时平盘取零()
    {
        // 台阶本身就没那么陡, 再削平盘也到不了 ⇒ 钳到 0 而不是负数
        Assert.Equal(0, BenchTemplateResolver.SolveBermForOverallAngle(15, 70, 85), 9);
        Assert.Equal(0, BenchTemplateResolver.SolveBermForOverallAngle(15, 70, 0), 9);
        Assert.Equal(0, BenchTemplateResolver.SolveBermForOverallAngle(15, 70, 90), 9);
    }

    [Fact]
    public void 帮坡角_最终帮比工作帮陡()
    {
        // 最终帮用安全平台(米级)、工作帮用最小工作平盘(几十米级) ⇒ 前者明显陡
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        var r = BenchTemplateResolver.Resolve(db.Connection, false, hardness: "hard");
        Assert.True(r.OverallSlopeAngleDeg > r.WorkingSlopeAngleDeg,
            $"最终帮 {r.OverallSlopeAngleDeg:0.#}° 应陡于工作帮 {r.WorkingSlopeAngleDeg:0.#}°");
    }

    // ── 越界校验 ────────────────────────────────────────────
    [Fact]
    public void 校验_模板值超规范上限要报出来()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        long id = AddTemplate(db.Connection, "over", current: true);
        SetParam(db.Connection, id, "bench_height", 999);   // 远超 standard_max

        var r = BenchTemplateResolver.Resolve(db.Connection, false);
        Assert.Contains(r.Warnings, w => w.Contains("高于规范上限"));
    }

    [Fact]
    public void 校验_合规时不报警()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        var r = BenchTemplateResolver.Resolve(db.Connection, false, hardness: "medium");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("台阶高度"));
    }

    // ── 兜底 ────────────────────────────────────────────────
    [Fact]
    public void 兜底_没有连接时仍给完整结果()
    {
        var r = BenchTemplateResolver.Resolve(null, isDump: false, hardness: "hard");
        Assert.Null(r.TemplateId);
        Assert.Equal((15.0, 70.0, 8.0), (r.BenchHeight, r.FaceAngleDeg, r.BermWidth));
        Assert.Equal(BenchTemplateResolver.DefaultMinWorkingBerm, r.MinWorkingBermWidth, 6);
        Assert.Equal(BenchTemplateResolver.DefaultCoalH, r.CoalBenchHeight, 6);
        Assert.Empty(r.Warnings);                       // 校验也跳过, 不硬报
        Assert.True(r.OverallSlopeAngleDeg > 0);        // 推导量照样算得出
    }

    [Fact]
    public void 兜底_煤台阶那一套读得到V034的规范默认()
    {
        using var db = TestDb.Open();
        ClearTemplates(db.Connection);
        var r = BenchTemplateResolver.Resolve(db.Connection, false);
        Assert.True(r.CoalBenchHeight > 0);
        Assert.True(r.CoalFaceAngleDeg > 0);
        Assert.True(r.CoalBermWidth > 0);
        // 煤台阶通常比岩台阶缓
        Assert.True(r.CoalFaceAngleDeg <= r.FaceAngleDeg + 1e-9);
    }

    [Fact]
    public void 兜底_参数码查不到时返回空而不是零()
        => Assert.Null(BenchTemplateResolver.StandardDefault(null, "bench_height"));
}
