// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimDumpRowIndexTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  「一笔流查得到它那个排土行」判据
//
//  ── 踩过的坑（2026-08-22）──
//  块体那一层拿【去向码】(`内排土场1-L0-1000100`) 去查【UnitId】表 (`内排土场1-L1-P01-S01`)，
//  两套码永不相等 —— 实测基表 358 个去向码一个都查不到。而它**不报错**：
//  尺寸静默退成源采场块的长宽，走向方位那一列根本没人读，堆填块只好沿用源块的朝向。
//
//  ⇒ 判据必须把【两套键长得不一样】这件事本身钉住（K3），
//     否则以后谁再"顺手"改回按码查 UnitId 表，还是一路绿灯。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimDumpRowIndexTests
{
    private const string Site = "内排土场1";

    /// <summary>一条排土行：UnitId 走 Cell.Code 那一套，去向码由 (场,级,带,幅) 派生。</summary>
    private static MiningUnitLedger.Row DumpRow(int level, int band, int panel, double az)
        => new()
        {
            UnitId = $"{Site}-L{level}-P{panel:00}-S{band:00}",
            Kind = LedgerKind.Dump,
            Region = Site,
            Seam = $"L{level}",
            Band = band, Panel = panel,
            Cx = 621500, Cy = 4380900, Cz = 1150,
            LengthM = 47, WidthM = 40, ThickM = 15,
            AzimuthDeg = az,
            DumpCapM3 = 28200,
        };

    private static List<MiningUnitLedger.Row> Base()
        => new() { DumpRow(1, 1, 1, 80), DumpRow(2, 1, 3, 65) };

    /// <summary>台账行 → 它的去向码（与排产写进「去向」列的是同一处口径）。</summary>
    private static string CodeOf(MiningUnitLedger.Row row, IReadOnlyList<MiningUnitLedger.Row> rows)
        => DumpSlotCode.OfLedgerRow(row, DumpSlotCode.MaxLevelOf(rows));

    // ── K1 O-D 已经把码解成排土行时，按 UnitId 查得到 ─────────────────────────
    [Fact]
    public void K1_按OD解出来的UnitId查得到()
    {
        var rows = Base();
        var idx = SimDumpRowIndex.Build(rows);
        var od = new SimHaulOd
        {
            UnitId = "岩-B01-P01",
            DestinationCode = CodeOf(rows[0], rows),
            DestinationName = rows[0].UnitId,      // LedgerOdBuilder 填的就是排土行的 UnitId
        };

        var hit = idx.Resolve(od, null);
        Assert.NotNull(hit);
        Assert.Equal(rows[0].UnitId, hit!.UnitId);
        Assert.Equal(80, hit.AzimuthDeg);
    }

    // ── K2 只有去向码时也要查得到（O-D 没建 / 老方案）───────────────────────
    [Fact]
    public void K2_只有去向码时按码查得到()
    {
        var rows = Base();
        var idx = SimDumpRowIndex.Build(rows);
        var od = new SimHaulOd { UnitId = "岩-B01-P02", DestinationCode = CodeOf(rows[1], rows) };

        var hit = idx.Resolve(od, null);
        Assert.NotNull(hit);
        Assert.Equal(rows[1].UnitId, hit!.UnitId);
        Assert.Equal(65, hit.AzimuthDeg);
    }

    // ── K3 把【两套键长得不一样】钉住 —— 这正是老 bug 的形状 ────────────────
    [Fact]
    public void K3_去向码不等于UnitId_钉住两套编码()
    {
        var rows = Base();
        foreach (var r in rows)
            Assert.NotEqual(r.UnitId, CodeOf(r, rows));

        // 老写法 = 拿去向码去查 UnitId 表：必然全部落空（且不报错）
        var byUnitId = new Dictionary<string, MiningUnitLedger.Row>(StringComparer.Ordinal);
        foreach (var r in rows) byUnitId[r.UnitId] = r;
        foreach (var r in rows)
            Assert.False(byUnitId.ContainsKey(CodeOf(r, rows)),
                "去向码居然能在 UnitId 表里查到 —— 那本条判据就失去意义了，两套编码的形状变了。");
    }

    // ── K4 查不到就返回 null，不猜 ───────────────────────────────────────────
    [Fact]
    public void K4_查不到返回null()
    {
        var idx = SimDumpRowIndex.Build(Base());
        Assert.Null(idx.Resolve(new SimHaulOd { UnitId = "煤-B01-P01", DestinationName = "（未指定的煤卸点）" }, null));
        Assert.Null(idx.Resolve(null, null));
    }

    // ── K5 只喂月度台账（没有排土行）时索引是空的 —— 现象是"全退默认"，所以要能看见 ──
    [Fact]
    public void K5_没有排土行时索引为空()
    {
        var idx = SimDumpRowIndex.Build(new List<MiningUnitLedger.Row>
        {
            new() { UnitId = "岩-B01-P01", Kind = LedgerKind.Rock, LengthM = 100, WidthM = 40, ThickM = 12 },
        });
        Assert.Equal(0, idx.ByUnitIdCount);
        Assert.Equal(0, idx.ByCodeCount);
    }
}
