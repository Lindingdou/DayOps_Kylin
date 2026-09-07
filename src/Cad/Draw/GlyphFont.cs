using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 真字体字形轮廓（TrueType glyf 解析）—— 替代只有 0-9/A-Z 的 <see cref="StrokeFont"/>，
/// 让注记能显示中文与任意字符（孔号、煤层名、图名等）。
///
/// 做法：向宿主要一份系统字体的 TrueType 字节表（cmap/loca/glyf/head/hmtx/hhea/maxp），
/// 自己解析字形轮廓 → 二次贝塞尔按弦高细分 → 折线段。轮廓按 em 归一化(0..1)，
/// 与 StrokeFont 同一坐标口径（字高 1、基线 y=0），故排版/对齐逻辑不变。
///
/// 纯逻辑、无 UI 依赖：字体字节由 <see cref="Provider"/> 注入(界面层用 Avalonia 的
/// IGlyphTypeface.TryGetTable 提供)，未注入时自动退回 StrokeFont。
/// </summary>
public static class GlyphFont
{
    /// <summary>取字体表的回调：tag → 表字节；宿主未设置则不启用真字体。</summary>
    public static Func<uint, byte[]?>? Provider;

    private static readonly Dictionary<char, List<List<(double x, double y)>>?> Cache = new();
    private static readonly Dictionary<char, double> AdvCache = new();
    private static bool _init, _ok;
    private static byte[]? _glyf, _loca, _cmap, _hmtx;
    private static int _unitsPerEm = 1000, _indexToLocFormat, _numGlyphs, _numHMetrics;

    public static bool Available { get { EnsureInit(); return _ok; } }

    /// <summary>换字体/宿主重新设置 Provider 后调用，丢弃缓存。</summary>
    public static void Reset()
    {
        lock (Cache) { Cache.Clear(); AdvCache.Clear(); _init = false; _ok = false; }
    }

    private static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    private static void EnsureInit()
    {
        if (_init) return;
        _init = true;
        try
        {
            var p = Provider;
            if (p == null) return;
            var head = p(Tag("head")); var maxp = p(Tag("maxp"));
            _loca = p(Tag("loca")); _glyf = p(Tag("glyf")); _cmap = p(Tag("cmap"));
            var hhea = p(Tag("hhea")); _hmtx = p(Tag("hmtx"));
            if (head == null || maxp == null || _loca == null || _glyf == null || _cmap == null) return;
            _unitsPerEm = Math.Max(16, U16(head, 18));
            _indexToLocFormat = (short)U16(head, 50);
            _numGlyphs = U16(maxp, 4);
            _numHMetrics = hhea != null ? U16(hhea, 34) : 0;
            _ok = true;
        }
        catch { _ok = false; }
    }

    private static int U8(byte[] b, int o) => b[o];
    private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static int S16(byte[] b, int o) => (short)U16(b, o);
    private static uint U32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    /// <summary>字符 → 字形号(cmap 格式 4 / 12)；找不到返回 0。</summary>
    private static int GlyphIndex(char c)
    {
        var cm = _cmap!;
        int n = U16(cm, 2), best = -1, bestScore = -1;
        for (int i = 0; i < n; i++)
        {
            int rec = 4 + i * 8;
            int plat = U16(cm, rec), enc = U16(cm, rec + 2);
            int off = (int)U32(cm, rec + 4);
            int score = (plat == 3 && enc == 10) ? 4 : (plat == 3 && enc == 1) ? 3 : (plat == 0) ? 2 : 1;
            if (score > bestScore) { bestScore = score; best = off; }
        }
        if (best < 0 || best + 4 > cm.Length) return 0;
        int fmt = U16(cm, best);
        if (fmt == 4)
        {
            int segX2 = U16(cm, best + 6), seg = segX2 / 2;
            int endBase = best + 14, startBase = endBase + segX2 + 2, deltaBase = startBase + segX2, rangeBase = deltaBase + segX2;
            for (int s = 0; s < seg; s++)
            {
                int end = U16(cm, endBase + s * 2);
                if (c > end) continue;
                int start = U16(cm, startBase + s * 2);
                if (c < start) return 0;
                int idDelta = S16(cm, deltaBase + s * 2), idRangeOff = U16(cm, rangeBase + s * 2);
                if (idRangeOff == 0) return (c + idDelta) & 0xFFFF;
                int gi = rangeBase + s * 2 + idRangeOff + (c - start) * 2;
                if (gi + 1 >= cm.Length) return 0;
                int g = U16(cm, gi);
                return g == 0 ? 0 : (g + idDelta) & 0xFFFF;
            }
            return 0;
        }
        if (fmt == 12)
        {
            int groups = (int)U32(cm, best + 12);
            for (int g = 0; g < groups; g++)
            {
                int o = best + 16 + g * 12;
                uint s0 = U32(cm, o), e0 = U32(cm, o + 4), gi = U32(cm, o + 8);
                if (c < s0) return 0;
                if (c <= e0) return (int)(gi + (c - s0));
            }
        }
        return 0;
    }

    private static (int off, int len) GlyfRange(int gid)
    {
        var loca = _loca!;
        if (gid < 0 || gid + 1 > _numGlyphs) return (0, 0);
        if (_indexToLocFormat == 0)
        {
            if (gid * 2 + 3 >= loca.Length) return (0, 0);
            int a = U16(loca, gid * 2) * 2, b = U16(loca, gid * 2 + 2) * 2;
            return (a, b - a);
        }
        if (gid * 4 + 7 >= loca.Length) return (0, 0);
        int a4 = (int)U32(loca, gid * 4), b4 = (int)U32(loca, gid * 4 + 4);
        return (a4, b4 - a4);
    }

    /// <summary>字形轮廓(em 归一化, 基线 y=0, 字高 1)。无字形/未启用返回 null。</summary>
    public static List<List<(double x, double y)>>? Outline(char c)
    {
        EnsureInit();
        if (!_ok) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(c, out var hit)) return hit;
            List<List<(double x, double y)>>? res = null;
            try { res = BuildOutline(GlyphIndex(c), 0); } catch { res = null; }
            Cache[c] = res;
            return res;
        }
    }

    /// <summary>字符步进宽度(em 归一化)；取不到时按 0.6 估。</summary>
    public static double Advance(char c)
    {
        EnsureInit();
        if (!_ok) return 0.6;
        lock (AdvCache)
        {
            if (AdvCache.TryGetValue(c, out var w)) return w;
            double adv = 0.6;
            try
            {
                int gid = GlyphIndex(c);
                if (_hmtx != null && _numHMetrics > 0)
                {
                    int i = Math.Min(gid, _numHMetrics - 1);
                    if (i * 4 + 1 < _hmtx.Length) adv = U16(_hmtx, i * 4) / (double)_unitsPerEm;
                }
            }
            catch { }
            if (adv <= 0) adv = 0.6;
            AdvCache[c] = adv;
            return adv;
        }
    }

    // glyf 解析：简单字形(点/标志/端点) + 复合字形(引用其它字形, 带偏移)
    private static List<List<(double x, double y)>>? BuildOutline(int gid, int depth)
    {
        if (gid <= 0 && depth == 0) return null;
        var (off, len) = GlyfRange(gid);
        if (len <= 0) return new List<List<(double x, double y)>>();   // 空字形(如空格)
        var g = _glyf!;
        if (off + 10 > g.Length) return null;
        int nc = S16(g, off);
        var contours = new List<List<(double x, double y)>>();
        double scale = 1.0 / _unitsPerEm;

        if (nc < 0)   // 复合字形
        {
            if (depth > 4) return contours;
            int p = off + 10;
            while (p + 4 <= g.Length)
            {
                int flags = U16(g, p), glyphIndex = U16(g, p + 2);
                p += 4;
                double dx, dy;
                if ((flags & 1) != 0) { dx = S16(g, p); dy = S16(g, p + 2); p += 4; }
                else { dx = (sbyte)g[p]; dy = (sbyte)g[p + 1]; p += 2; }
                if ((flags & 8) != 0) p += 2;
                else if ((flags & 0x40) != 0) p += 4;
                else if ((flags & 0x80) != 0) p += 8;
                var sub = BuildOutline(glyphIndex, depth + 1);
                if (sub != null)
                    foreach (var cont in sub)
                    {
                        var moved = new List<(double x, double y)>(cont.Count);
                        foreach (var (x, y) in cont) moved.Add((x + dx * scale, y + dy * scale));
                        contours.Add(moved);
                    }
                if ((flags & 0x20) == 0) break;   // MORE_COMPONENTS
            }
            return contours;
        }

        int pEnd = off + 10;
        var endPts = new int[nc];
        for (int i = 0; i < nc; i++) { endPts[i] = U16(g, pEnd); pEnd += 2; }
        int nPts = nc == 0 ? 0 : endPts[nc - 1] + 1;
        int insLen = U16(g, pEnd); pEnd += 2 + insLen;

        var flagsArr = new byte[nPts];
        for (int i = 0; i < nPts && pEnd < g.Length;)
        {
            byte f = g[pEnd++]; flagsArr[i++] = f;
            if ((f & 8) != 0 && pEnd < g.Length) { int rep = g[pEnd++]; for (int r = 0; r < rep && i < nPts; r++) flagsArr[i++] = f; }
        }
        var xs = new int[nPts]; var ys = new int[nPts];
        int v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte f = flagsArr[i];
            if ((f & 2) != 0) { int d = g[pEnd++]; v += ((f & 16) != 0) ? d : -d; }
            else if ((f & 16) == 0) { v += S16(g, pEnd); pEnd += 2; }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte f = flagsArr[i];
            if ((f & 4) != 0) { int d = g[pEnd++]; v += ((f & 32) != 0) ? d : -d; }
            else if ((f & 32) == 0) { v += S16(g, pEnd); pEnd += 2; }
            ys[i] = v;
        }

        int start = 0;
        for (int ci = 0; ci < nc; ci++)
        {
            int end = endPts[ci];
            int n = end - start + 1;
            if (n > 0) contours.Add(FlattenContour(flagsArr, xs, ys, start, n, scale));
            start = end + 1;
        }
        return contours;
    }

    /// <summary>一条轮廓：on/off 点序列 → 二次贝塞尔细分成折线(闭合)。</summary>
    private static List<(double x, double y)> FlattenContour(byte[] flags, int[] xs, int[] ys, int start, int n, double scale)
    {
        var pts = new List<(double x, double y, bool on)>(n);
        for (int i = 0; i < n; i++)
            pts.Add((xs[start + i] * scale, ys[start + i] * scale, (flags[start + i] & 1) != 0));

        var outp = new List<(double x, double y)>();
        if (pts.Count == 0) return outp;

        // 起点：第一个 on 点；全 off 时取两 off 中点
        int s0 = pts.FindIndex(p => p.on);
        (double x, double y) startPt;
        if (s0 < 0) { startPt = ((pts[0].x + pts[^1].x) / 2, (pts[0].y + pts[^1].y) / 2); s0 = 0; }
        else startPt = (pts[s0].x, pts[s0].y);
        outp.Add(startPt);

        var cur = startPt;
        (double x, double y)? ctrl = null;
        for (int k = 1; k <= pts.Count; k++)
        {
            var p = pts[(s0 + k) % pts.Count];
            if (p.on)
            {
                if (ctrl == null) outp.Add((p.x, p.y));
                else { Quad(outp, cur, ctrl.Value, (p.x, p.y)); ctrl = null; }
                cur = (p.x, p.y);
            }
            else
            {
                if (ctrl != null)
                {
                    var mid = ((ctrl.Value.x + p.x) / 2, (ctrl.Value.y + p.y) / 2);
                    Quad(outp, cur, ctrl.Value, mid);
                    cur = mid;
                }
                ctrl = (p.x, p.y);
            }
        }
        if (ctrl != null) Quad(outp, cur, ctrl.Value, startPt);
        else if (outp.Count > 1) outp.Add(startPt);   // 闭合
        return outp;
    }

    /// <summary>二次贝塞尔细分(固定 8 段, em 尺度下足够平滑)。</summary>
    private static void Quad(List<(double x, double y)> outp, (double x, double y) p0, (double x, double y) c, (double x, double y) p1)
    {
        const int steps = 8;
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps, mt = 1 - t;
            outp.Add((mt * mt * p0.x + 2 * mt * t * c.x + t * t * p1.x,
                      mt * mt * p0.y + 2 * mt * t * c.y + t * t * p1.y));
        }
    }
}
