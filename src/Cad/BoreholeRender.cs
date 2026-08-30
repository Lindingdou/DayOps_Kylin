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

    /// <summary>生成柱状图几何。depthScale=深度→世界单位比例；width=柱宽（世界单位）。</summary>
    public static List<SceneEntity> BuildColumns(IEnumerable<BoreholeImportService.Borehole> holes, double depthScale, double width)
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
        }
        return list;
    }
}
