using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 真字形文字：TrueType glyf 轮廓解析(cmap 定位 → loca 取范围 → 二次贝塞尔细分)。
/// 用测试内构造的最小 TTF 验证解析正确, 不依赖系统字体(CI/麒麟都能跑)。
/// </summary>
[Collection("TextGeometry")]
public class GlyphFontTests : IDisposable
{
    public void Dispose() { GlyphFont.Provider = null; GlyphFont.Reset(); }

    // 造一个只含 'A'(方框轮廓, 500×700 单位)与空格的最小字体表集合
    private static void InstallTinyFont(int unitsPerEm = 1000)
    {
        byte[] head = new byte[54];
        Put16(head, 18, unitsPerEm);      // unitsPerEm
        Put16(head, 50, 0);                // indexToLocFormat = short
        byte[] maxp = new byte[6];
        Put16(maxp, 4, 3);                 // numGlyphs = 3 (.notdef, space, A)
        byte[] hhea = new byte[36]; Put16(hhea, 34, 3);
        byte[] hmtx = new byte[12];
        Put16(hmtx, 0, 600); Put16(hmtx, 4, 300); Put16(hmtx, 8, 500);   // .notdef/space/A 步进

        // glyf: gid0 空, gid1 空(space), gid2 = 方框
        var box = SimpleBox(50, 0, 450, 700);
        var glyf = new List<byte>();
        int off0 = 0;                       // gid0 长度 0
        int off1 = 0;                       // gid1 长度 0
        glyf.AddRange(box);
        byte[] loca = new byte[8];
        Put16(loca, 0, off0 / 2); Put16(loca, 2, off1 / 2); Put16(loca, 4, 0); Put16(loca, 6, box.Length / 2);

        // cmap 格式 4: 'A'(0x41) → gid 2, ' '(0x20) → gid 1
        byte[] cmap = Cmap4(new (char, int)[] { (' ', 1), ('A', 2) });

        var tables = new Dictionary<string, byte[]>
        { ["head"] = head, ["maxp"] = maxp, ["loca"] = loca, ["glyf"] = glyf.ToArray(), ["cmap"] = cmap, ["hhea"] = hhea, ["hmtx"] = hmtx };
        GlyphFont.Provider = tag =>
        {
            string s = new string(new[] { (char)(tag >> 24), (char)((tag >> 16) & 0xFF), (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF) });
            return tables.TryGetValue(s, out var b) ? b : null;
        };
        GlyphFont.Reset();
    }

    private static void Put16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)(v & 0xFF); }

    /// <summary>一个简单字形: 4 个 on 曲线点组成的矩形轮廓。</summary>
    private static byte[] SimpleBox(int x0, int y0, int x1, int y1)
    {
        var b = new List<byte>();
        void W16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)(v & 0xFF)); }
        W16(1);                       // numberOfContours
        W16(x0); W16(y0); W16(x1); W16(y1);   // bbox
        W16(3);                       // endPt of contour 0 = 3
        W16(0);                       // instructionLength
        for (int i = 0; i < 4; i++) b.Add(0x01);   // flags: on-curve, 16 位 delta
        int[] xs = { x0, x1, x1, x0 }, ys = { y0, y0, y1, y1 };
        int prev = 0;
        foreach (var x in xs) { W16(x - prev); prev = x; }
        prev = 0;
        foreach (var y in ys) { W16(y - prev); prev = y; }
        return b.ToArray();
    }

    private static byte[] Cmap4((char c, int gid)[] map)
    {
        int seg = map.Length + 1;                       // 各字符一段 + 0xFFFF 收尾段
        int subLen = 16 + seg * 8;
        var b = new byte[4 + 8 + subLen];
        Put16(b, 0, 0); Put16(b, 2, 1);                  // version, numTables
        Put16(b, 4, 3); Put16(b, 6, 1);                  // platform 3 / encoding 1
        b[8] = 0; b[9] = 0; b[10] = 0; b[11] = 12;       // offset = 12
        int o = 12;
        Put16(b, o, 4); Put16(b, o + 2, subLen); Put16(b, o + 4, 0);
        Put16(b, o + 6, seg * 2);
        int endB = o + 14, startB = endB + seg * 2 + 2, deltaB = startB + seg * 2, rangeB = deltaB + seg * 2;
        for (int i = 0; i < map.Length; i++)
        {
            Put16(b, endB + i * 2, map[i].c);
            Put16(b, startB + i * 2, map[i].c);
            Put16(b, deltaB + i * 2, (map[i].gid - map[i].c) & 0xFFFF);
            Put16(b, rangeB + i * 2, 0);
        }
        Put16(b, endB + map.Length * 2, 0xFFFF);
        Put16(b, startB + map.Length * 2, 0xFFFF);
        Put16(b, deltaB + map.Length * 2, 1);
        Put16(b, rangeB + map.Length * 2, 0);
        return b;
    }

    [Fact]
    public void Outline_ParsesSimpleGlyph_NormalizedToEm()
    {
        InstallTinyFont();
        Assert.True(GlyphFont.Available);
        var contours = GlyphFont.Outline('A');
        Assert.NotNull(contours);
        var c = Assert.Single(contours!);
        // 方框 4 角 + 闭合点; 归一化到 em(1000)
        Assert.True(c.Count >= 5);
        Assert.Equal(0.05, c.Min(p => p.x), 6);
        Assert.Equal(0.45, c.Max(p => p.x), 6);
        Assert.Equal(0.0, c.Min(p => p.y), 6);
        Assert.Equal(0.70, c.Max(p => p.y), 6);
        Assert.Equal(c[0], c[^1]);                  // 闭合
        Assert.Equal(0.5, GlyphFont.Advance('A'), 6);   // hmtx 500/1000
    }

    [Fact]
    public void MissingGlyph_FallsBackToStrokeFont()
    {
        InstallTinyFont();
        Assert.Null(GlyphFont.Outline('1'));        // 该字体只有 'A' 和空格
        var t = new TextEntity { X = 0, Y = 0, Height = 10, Text = "1" };
        Assert.NotEmpty(t.LocalStrokes());           // 数字退回 StrokeFont 仍画得出
        // 中文两边都没有 → 画不出(这正是要接真字体的原因; 装了含中文的系统字体后由 GlyphFont 出轮廓)
        Assert.Null(GlyphFont.Outline('中'));
        Assert.Empty(new TextEntity { X = 0, Y = 0, Height = 10, Text = "中" }.LocalStrokes());
    }

    [Fact]
    public void TextEntity_UsesGlyphOutlines_AndPerCharAdvance()
    {
        InstallTinyFont();
        var t = new TextEntity { X = 0, Y = 0, Height = 10, Text = "AA" };
        var segs = t.LocalStrokes();
        Assert.NotEmpty(segs);
        // 字宽按 hmtx 步进(0.5×字高=5), 两字 → 第二字整体右移 5
        double maxX = segs.Max(s => Math.Max(s.x0, s.x1));
        Assert.Equal(5 + 4.5, maxX, 6);              // 第二字起点 5 + 方框右边 0.45×10
        // 空格无轮廓但占步进
        var sp = new TextEntity { X = 0, Y = 0, Height = 10, Text = " A" }.LocalStrokes();
        Assert.Equal(3 + 4.5, sp.Max(s => Math.Max(s.x0, s.x1)), 6);   // 空格步进 300/1000×10
    }

    [Fact]
    public void NoProvider_KeepsStrokeFontBehaviour()
    {
        GlyphFont.Provider = null; GlyphFont.Reset();
        Assert.False(GlyphFont.Available);
        Assert.Null(GlyphFont.Outline('A'));
        var t = new TextEntity { X = 0, Y = 0, Height = 10, Text = "A1" };
        Assert.NotEmpty(t.LocalStrokes());
    }

    [Fact]
    public void FillTriangles_CoversGlyphArea_AndTextEntityEmitsFaces()
    {
        InstallTinyFont();
        var tris = GlyphFont.FillTriangles('A');
        Assert.NotNull(tris);
        Assert.True(tris!.Length % 6 == 0 && tris.Length >= 6);
        // 方框 0.05..0.45 × 0..0.70 → 填充面积应逼近 0.4×0.7 = 0.28
        double area = 0;
        for (int i = 0; i + 5 < tris.Length; i += 6)
            area += Math.Abs((tris[i + 2] - tris[i]) * (tris[i + 5] - tris[i + 1])
                           - (tris[i + 4] - tris[i]) * (tris[i + 3] - tris[i + 1])) / 2;
        Assert.InRange(area, 0.28 * 0.97, 0.28 * 1.03);
        // 不越出字形包围盒
        for (int i = 0; i + 1 < tris.Length; i += 2)
        {
            Assert.InRange(tris[i], 0.05f - 1e-4f, 0.45f + 1e-4f);
            Assert.InRange(tris[i + 1], -1e-4f, 0.70f + 1e-4f);
        }
        // TextEntity: 实心三角进面缓冲(P3_C3), 且该字不再重复出轮廓线
        var t = new TextEntity { X = 100, Y = 200, Height = 10, Text = "A", Cr = 1, Cg = 1, Cb = 0 };
        var faces = new List<float>();
        t.TessellateFaces(faces);
        Assert.Equal(tris.Length / 6 * 18, faces.Count);
        Assert.InRange(faces[0], 100 + 0.05 * 10 - 1e-3, 100 + 0.45 * 10 + 1e-3);
        Assert.Equal(1f, faces[3]); Assert.Equal(0f, faces[5]);
        Assert.Empty(t.LocalStrokes(skipFilled: true));
        Assert.NotEmpty(t.LocalStrokes());               // 默认口径仍给轮廓(兼容旧调用)
    }

    [Fact]
    public void Billboards_CarryFills_AndFallBackToStrokes()
    {
        InstallTinyFont();
        var sc = new Scene();
        sc.Entities.Add(new TextEntity { X = 1, Y = 2, Elevation = 3, Height = 5, Text = "A", ScreenFacing = true });
        sc.Entities.Add(new TextEntity { X = 0, Y = 0, Height = 5, Text = "1", ScreenFacing = true });   // 该字体无 '1' → 简笔画
        var bb = sc.BuildBillboards();
        Assert.Equal(2, bb.Count);
        Assert.NotEmpty(bb[0].Fills);                     // 真字形 → 实心
        Assert.Empty(bb[0].Strokes);
        Assert.Empty(bb[1].Fills);                        // 回退简笔画 → 仍是线
        Assert.NotEmpty(bb[1].Strokes);
    }
    /// <summary>
    /// 真字体下字形走面通道、Tessellate 什么都不出 —— 拾取/框选/高亮若仍走 Tessellate 就"文字点不中"。
    /// 现在这三路都走 TessellatePick(轮廓)。方框字形 'A' 在 100.5..104.5 × 200..207 (0.05..0.45 × 0..0.7 × 字高 10)。
    /// </summary>
    [Fact]
    public void RealFontText_IsPickable_BoxSelectable_AndHighlightable()
    {
        InstallTinyFont();
        var t = new TextEntity { X = 100, Y = 200, Height = 10, Text = "A" };
        var lines = new List<float>(); t.Tessellate(lines);
        Assert.Empty(lines);                                 // 显示走面通道(前提成立)
        var pick = new List<float>(); t.TessellatePick(pick);
        Assert.NotEmpty(pick);                               // 拾取/高亮有轮廓可用

        Assert.InRange(t.DistanceTo(100.5, 203), 0, 1e-6);   // 左边线上
        Assert.InRange(t.DistanceTo(102.5, 207), 0, 1e-6);   // 顶边线上
        Assert.InRange(t.DistanceTo(102.5, 209), 1.99, 2.01); // 顶边上方 2

        var sc = new Scene(); sc.Add(t);
        Assert.Same(t, sc.Pick(101, 203, tol: 1));           // 点选命中
        Assert.Null(sc.Pick(120, 203, tol: 1));

        Assert.True(SelectionBox.Match(t, 99, 199, 106, 208, crossing: false));    // 窗口选: 整字在框内
        Assert.True(SelectionBox.Match(t, 103, 203, 120, 220, crossing: true));    // 交叉选: 框穿过字
        Assert.False(SelectionBox.Match(t, 103, 203, 120, 220, crossing: false));
        Assert.False(SelectionBox.Match(t, 110, 210, 120, 220, crossing: true));

        var bb = new TextEntity { X = 100, Y = 200, Height = 10, Text = "A", ScreenFacing = true, Rotation = 1.0 };
        var bbPick = new List<float>(); bb.TessellatePick(bbPick);
        Assert.NotEmpty(bbPick);                             // 公告板文字同样可拾取(按未旋转排版)
        Assert.InRange(bb.DistanceTo(100.5, 203), 0, 1e-6);
    }
    /// <summary>
    /// 实心字形内部也算命中(同 AutoCAD 的 TrueType 填充字)：大字放大后笔画比拾取框宽, 只量轮廓会点在字上却选不中。
    /// 方框字形 'A' 在 100.5..104.5 × 200..207：中心 (102.5, 203.5) 离最近边 2, 但落在实心内 → 0。
    /// </summary>
    [Fact]
    public void RealFontText_FilledInteriorCountsAsHit_AndMovesKeepScreenFacing()
    {
        InstallTinyFont();
        var t = new TextEntity { X = 100, Y = 200, Height = 10, Text = "A" };
        Assert.InRange(t.DistanceTo(102.5, 203.5), 0, 1e-9);        // 实心内
        Assert.InRange(t.DistanceTo(102.5, 209), 1.99, 2.01);       // 字形外仍量轮廓
        Assert.InRange(t.DistanceTo(99, 203.5), 1.49, 1.51);        // 左边线外 1.5
        var rot = new TextEntity { X = 100, Y = 200, Height = 10, Text = "A", Rotation = System.Math.PI / 2 };   // 转 90°: 字形落在 x 93..100, y 200.5..204.5
        Assert.InRange(rot.DistanceTo(96.5, 202.5), 0, 1e-9);
        Assert.InRange(rot.DistanceTo(102.5, 203.5), 2.49, 2.51);

        var sf = new TextEntity { X = 1, Y = 2, Height = 3, Text = "A", ScreenFacing = true };
        Assert.True(((TextEntity)sf.MoveGrip(0, 5, 6)!).ScreenFacing);                       // 夹点拖 / 拖放移动 / 变换都不该把公告板文字变成平面字
        Assert.True(((TextEntity)sf.Apply(Affine2.Translate(3, 4))).ScreenFacing);
    }
}


