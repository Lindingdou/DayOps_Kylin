using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 钻孔柱状图渲染 —— 把 <see cref="BoreholeImportService.Borehole"/> 生成线/矩形几何：
/// 孔口向下(−Y)一条中轴线 + 每分层一个按岩性配色的矩形。无需文字（颜色区分岩性）。
/// 纯逻辑、可单测。产出 SceneEntity 直接入现有视口。
/// </summary>
public static class BoreholeRender
{
    /// <summary>岩性 → 颜色（常见岩性固定色，未知按字符和散列到调色板，运行内稳定）。</summary>
    public static (float r, float g, float b) LithoColor(string rock)
    {
        switch (rock)
        {
            case "粘土": case "黏土": return (0.55f, 0.40f, 0.25f);
            case "砂岩": return (0.85f, 0.75f, 0.45f);
            case "泥岩": return (0.50f, 0.45f, 0.35f);
            case "粉砂岩": return (0.70f, 0.65f, 0.45f);
            case "煤": case "煤层": return (0.12f, 0.12f, 0.12f);
            case "灰岩": case "石灰岩": return (0.70f, 0.72f, 0.78f);
            case "砾石": case "砂砾": return (0.80f, 0.60f, 0.40f);
            case "花岗岩": return (0.85f, 0.55f, 0.55f);
        }
        var pal = new (float r, float g, float b)[]
        {
            (0.60f,0.80f,0.90f),(0.75f,0.85f,0.55f),(0.85f,0.70f,0.85f),(0.60f,0.85f,0.70f),(0.90f,0.80f,0.55f)
        };
        int h = 0;
        foreach (char c in rock ?? "") h += c;
        return pal[(rock == null || rock.Length == 0) ? 0 : h % pal.Length];
    }

    /// <summary>生成柱状图几何。depthScale=深度→世界单位比例；width=柱宽（世界单位）；labelH&gt;0 则加深度刻度+标签(左侧)。忠实原版"柱状图+深度刻度"。</summary>
    public static List<SceneEntity> BuildColumns(IEnumerable<BoreholeImportService.Borehole> holes, double depthScale, double width, double labelH = 0)
    {
        var list = new List<SceneEntity>();
        foreach (var h in holes)
        {
            double depth = h.TotalDepth > 0 ? h.TotalDepth : 1;
            list.Add(new LineEntity { X0 = h.X, Y0 = h.Y, X1 = h.X, Y1 = h.Y - depth * depthScale, Cr = 0.70f, Cg = 0.70f, Cb = 0.72f });   // 中轴
            foreach (var iv in h.Intervals)
            {
                var (r, g, b) = LithoColor(iv.Rock);
                list.Add(new RectEntity
                {
                    X0 = h.X, Y0 = h.Y - iv.From * depthScale,
                    X1 = h.X + width, Y1 = h.Y - iv.To * depthScale,
                    Cr = r, Cg = g, Cb = b
                });
            }
            if (labelH > 0)   // 深度刻度(左侧短横)+深度值标签, 间隔取整
            {
                double step = MapDecor.NiceLength(depth / 5); if (step <= 0) step = depth;
                for (double d = 0; d <= depth + 1e-9; d += step)
                {
                    double yy = h.Y - d * depthScale;
                    list.Add(new LineEntity { X0 = h.X - width * 0.35, Y0 = yy, X1 = h.X, Y1 = yy, Cr = 0.7f, Cg = 0.7f, Cb = 0.72f });   // 刻度
                    list.Add(new TextEntity { X = h.X - width * 0.35 - labelH * 2.5, Y = yy - labelH * 0.4, Height = labelH, Text = d.ToString("0", System.Globalization.CultureInfo.InvariantCulture), Cr = 0.7f, Cg = 0.7f, Cb = 0.72f });   // 深度值
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 虚拟钻孔单孔 2D 柱状图 —— 忠实原「虚拟钻孔 出 2D 柱状预览」/「原始钻孔柱状图」: 岩柱中轴 +
    /// 每见煤层按顶/底板标高映射成深度矩形(按煤层稳定配色) + 深度刻度 + 煤层/厚度/底板标注。
    /// hits 为 <see cref="VirtualBorehole.SeamHit"/>(顶/底板标高); 顶板最高者作孔口(深度 0)。纯逻辑、可单测。
    /// </summary>
    public static List<SceneEntity> BuildVirtualColumn(
        IReadOnlyList<VirtualBorehole.SeamHit> hits, double x, double y, double depthScale, double width, double labelH = 0)
    {
        var list = new List<SceneEntity>();
        if (hits == null || hits.Count == 0) return list;

        double topZ = double.MinValue, botZ = double.MaxValue;
        foreach (var h in hits) { if (h.RoofZ > topZ) topZ = h.RoofZ; if (h.FloorZ < botZ) botZ = h.FloorZ; }
        double depth = topZ - botZ; if (depth <= 0) depth = 1;

        list.Add(new LineEntity { X0 = x, Y0 = y, X1 = x, Y1 = y - depth * depthScale, Cr = 0.70f, Cg = 0.70f, Cb = 0.72f });   // 岩柱中轴

        foreach (var h in hits)
        {
            var (r, g, b) = SeamColor(h.SeamCode);
            double yTop = y - (topZ - h.RoofZ) * depthScale;    // 顶板对应深度
            double yBot = y - (topZ - h.FloorZ) * depthScale;   // 底板对应深度
            list.Add(new RectEntity { X0 = x, Y0 = yTop, X1 = x + width, Y1 = yBot, Cr = r, Cg = g, Cb = b });
            if (labelH > 0)   // 煤层号 + 厚度(右侧)
                list.Add(new TextEntity
                {
                    X = x + width + labelH * 0.5, Y = (yTop + yBot) * 0.5 - labelH * 0.4, Height = labelH,
                    Text = $"{h.SeamCode} {h.Thickness:0.##}m", Cr = 0.2f, Cg = 0.2f, Cb = 0.2f,
                });
        }

        if (labelH > 0)   // 深度刻度(左侧短横+深度值)
        {
            double step = MapDecor.NiceLength(depth / 5); if (step <= 0) step = depth;
            for (double d = 0; d <= depth + 1e-9; d += step)
            {
                double yy = y - d * depthScale;
                list.Add(new LineEntity { X0 = x - width * 0.35, Y0 = yy, X1 = x, Y1 = yy, Cr = 0.7f, Cg = 0.7f, Cb = 0.72f });
                list.Add(new TextEntity { X = x - width * 0.35 - labelH * 2.5, Y = yy - labelH * 0.4, Height = labelH, Text = d.ToString("0", System.Globalization.CultureInfo.InvariantCulture), Cr = 0.7f, Cg = 0.7f, Cb = 0.72f });
            }
        }
        return list;
    }

    /// <summary>煤层号 → 稳定配色(暗色系, 煤层观感)。</summary>
    public static (float r, float g, float b) SeamColor(string seam)
    {
        var pal = new (float r, float g, float b)[]
        {
            (0.15f,0.15f,0.18f),(0.25f,0.20f,0.15f),(0.18f,0.22f,0.28f),(0.28f,0.24f,0.20f),(0.20f,0.18f,0.24f),(0.22f,0.26f,0.22f),
        };
        int h = 0;
        foreach (char c in seam ?? "") h += c;
        return pal[(seam == null || seam.Length == 0) ? 0 : System.Math.Abs(h) % pal.Length];
    }
}
