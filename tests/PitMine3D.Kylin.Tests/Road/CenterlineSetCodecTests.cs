// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/CenterlineSetCodecTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 「中心线管理」存档的打包 / 解包口径。存档是<b>唯一</b>能把整理成果带出本次会话的东西，
/// 所以判三件事：<b>坐标一个 bit 都不许变</b>（存回图层要与原图完全一致）、
/// <b>坏数据一律判失败而不是吐半份几何</b>（半份中线载回图层就是悄悄篡改）、
/// <b>汇总量与几何自洽</b>（列表列不解包直接显示，错了没人会发现）。
/// </summary>
public class CenterlineSetCodecTests
{
    /// <summary>现场量级的坐标（620000/4380000）：float 只有 7 位有效数字，这里必须全程 double。</summary>
    private static List<double[]> Sample() => new()
    {
        new double[] { 622013.125, 4380221.5, 1042.75,  622113.125, 4380321.5, 1046.25,  622213.0, 4380421.5, 1051.5 },
        new double[] { 622213.0,   4380421.5, 1051.5,   622313.0,   4380521.5, 1060.0 },
    };

    [Fact]
    public void PackUnpack_RoundTrips_BitExact()
    {
        var src = Sample();
        Assert.True(CenterlineSetCodec.TryUnpack(CenterlineSetCodec.Pack(src), out var back));

        Assert.Equal(src.Count, back.Count);
        for (int i = 0; i < src.Count; i++)
        {
            Assert.Equal(src[i].Length, back[i].Length);
            for (int k = 0; k < src[i].Length; k++)
                Assert.True(src[i][k].Equals(back[i][k]),
                    $"第 {i} 条第 {k} 个坐标变了：{src[i][k]:R} → {back[i][k]:R}");
        }
    }

    [Fact]
    public void Pack_SkipsDegenerateLines_AndEmptyInputGivesEmptyString()
    {
        Assert.Equal("", CenterlineSetCodec.Pack(null));
        Assert.Equal("", CenterlineSetCodec.Pack(new List<double[]>()));
        Assert.Equal("", CenterlineSetCodec.Pack(new List<double[]> { new double[] { 1, 2, 3 } }));   // 只有一个顶点

        // 一条好线 + 一条退化线 → 只存好的那条（退化线进不了图层，也不该占存档一行）
        var mixed = new List<double[]> { Sample()[0], new double[] { 1, 2, 3 } };
        Assert.True(CenterlineSetCodec.TryUnpack(CenterlineSetCodec.Pack(mixed), out var back));
        Assert.Single(back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("这不是 base64")]
    [InlineData("AAAA")]                 // 合法 base64，但不是 GZip 流
    public void TryUnpack_Garbage_FailsAndYieldsNothing(string? bad)
    {
        Assert.False(CenterlineSetCodec.TryUnpack(bad, out var lines));
        Assert.Empty(lines);
    }

    [Fact]
    public void TryUnpack_TruncatedPayload_Fails_NotHalfAGeometry()
    {
        string good = CenterlineSetCodec.Pack(Sample());
        var bytes = Convert.FromBase64String(good);
        var cut = new byte[bytes.Length / 2];
        Array.Copy(bytes, cut, cut.Length);

        Assert.False(CenterlineSetCodec.TryUnpack(Convert.ToBase64String(cut), out var lines));
        Assert.Empty(lines);   // 关键：不是"能读几条算几条"
    }

    [Fact]
    public void Stats_MatchesGeometry()
    {
        var st = CenterlineSetCodec.Stats(Sample());

        Assert.Equal(2, st.LineCount);
        Assert.Equal(5, st.VertexCount);          // 3 + 2
        Assert.Equal(622013.125, st.MinX, 6);
        Assert.Equal(622313.0, st.MaxX, 6);
        Assert.Equal(1042.75, st.MinZ, 6);
        Assert.Equal(1060.0, st.MaxZ, 6);

        double expect = 0;
        foreach (var l in Sample()) expect += CenterlinePick.Length3d(l);
        Assert.Equal(expect, st.LengthM, 6);
        Assert.Equal(expect / 1000.0, st.LengthKm, 9);
    }

    [Fact]
    public void Stats_Empty_IsAllZero_NotSentinelBounds()
    {
        var st = CenterlineSetCodec.Stats(new List<double[]>());
        Assert.Equal(0, st.LineCount);
        Assert.Equal(0.0, st.LengthM);
        // 包围盒不能留 double.MaxValue 那种哨兵值：它会原样写进存档行的 min_x 列
        Assert.Equal(0.0, st.MinX);
        Assert.Equal(0.0, st.MaxX);
    }

    [Fact]
    public void Pack_IsSmallerThanRawDoubles()
    {
        // 存档的立意就是"别把几十 MB 数字串塞进库"：压完的 base64 至少不该比裸 double 还大。
        var many = new List<double[]>();
        for (int i = 0; i < 200; i++)
        {
            var line = new double[60 * 3];
            for (int k = 0; k < 60; k++)
            {
                line[3 * k] = 622000 + i * 7 + k * 3.0;
                line[3 * k + 1] = 4380000 + i * 5 + k * 2.0;
                line[3 * k + 2] = 1040 + (k % 9) * 0.5;
            }
            many.Add(line);
        }
        string b64 = CenterlineSetCodec.Pack(many);
        long raw = 200L * 60 * 3 * 8;
        Assert.True(b64.Length < raw, $"打包后 {b64.Length} 字符 ≥ 裸 double {raw} 字节，压缩没起作用");
        Assert.True(CenterlineSetCodec.TryUnpack(b64, out var back) && back.Count == 200);
    }
}
