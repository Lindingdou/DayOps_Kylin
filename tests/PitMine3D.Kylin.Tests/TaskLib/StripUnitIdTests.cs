// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/StripUnitIdTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 采掘带 UnitId 拼法的判据。
///
/// <para><b>为什么值得单写一组</b>：这一句此前在三处各写一遍
/// （<c>MiningUnitLedger.FromStrips</c> 生成侧、<c>UnitSolidStage</c> 的真轨索引、
/// <c>EquipmentStage</c> 的设备落位），三处注释都写着"必须和台账逐字一致" ——
/// 那句注释本身就是证据：它靠人记着才对。</para>
///
/// <para><b>而且改坏了不会报错</b>：真轨查不到就<b>全退盒子</b>、设备摆不到单元上就<b>一台都不出现</b>，
/// 两种失败在界面上都长得像"数据不全"，谁也想不到是拼法对不上号。
/// 所以判据要钉的不是"格式好不好看"，是<b>三处调用同一个函数</b>这件事。</para>
/// </summary>
public class StripUnitIdTests
{
    // ── U1：格式本身。改坏格式 ⇒ 与磁盘上已有台账的 UnitId 对不上号 ──
    [Theory]
    [InlineData("4", 3, 27, "4-B3-P27")]        // 现场原样：基表里的 4-B3-P27
    [InlineData("11", 0, 0, "11-B0-P0")]
    [InlineData("2-1", 12, 5, "2-1-B12-P5")]    // 煤层码本身带横杠也不能出岔
    public void U1_拼法与磁盘上的台账一字不差(string seam, int band, int panel, string want)
        => Assert.Equal(want, MiningUnitLedger.StripUnitId(seam, band, panel));

    // ── U2：生成侧真的走这个函数（不是自己又拼了一遍）──
    [Fact]
    public void U2_FromStrips生成的UnitId等于StripUnitId()
    {
        var strip = new MiningModelPlanner.Strip
        {
            SeamCode = "4", BandId = 3, PanelIndex = 27, PanelCount = 37,
            StrikeLenM = 98.01, AdvanceWidthM = 40, ThickM = 11.763,
            CrestXyz = new double[] { 0, 0, 10, 100, 0, 10 },
            ToeXyz = new double[] { 0, 0, 0, 100, 0, 0 },
            EstVolumeM3 = 1234,
        };

        var rows = MiningUnitLedger.FromStrips(new[] { strip }, isRock: false, regionName: "采场1");

        Assert.Single(rows);
        // 生成侧若绕开 StripUnitId 自己拼，这条不会红；但它一旦和函数不一致就会红 ——
        // 这正是"三处各写一遍"时无人发现的那种偏差。
        Assert.Equal(MiningUnitLedger.StripUnitId("4", 3, 27), rows[0].UnitId);
    }

    // ── U3：字段顺序不能互换（B 和 P 都是整数，换了不会编译错，只会静静对不上）──
    [Fact]
    public void U3_带号与幅号不能互换()
    {
        Assert.NotEqual(MiningUnitLedger.StripUnitId("4", 3, 27),
                        MiningUnitLedger.StripUnitId("4", 27, 3));
    }

    // ── U4：煤层码为空时不许静默变成合法号 ──
    [Fact]
    public void U4_煤层码为空时拼出来的号能看出不对()
    {
        var id = MiningUnitLedger.StripUnitId(null, 3, 27);
        // 不要求它抛，但要求它不会伪装成一个正常的号：空煤层码必然以 '-' 开头
        Assert.StartsWith("-B", id);
    }
}
