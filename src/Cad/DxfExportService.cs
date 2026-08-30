using System;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// DXF 导出 —— 把当前视口显示的线段几何（交错 P3_C3）写回 .dxf。
/// 说明：导出的是**已折线化的显示几何**（圆/弧/样条已分段），非原始实体；
/// 圆等会变成多条直线。对应 Windows 版「导出 DXF」的托管路径（几何级）。
/// </summary>
public static class DxfExportService
{
    /// <summary>把交错 P3_C3 线段（每段 2 顶点 × 6 float）写为 DXF 直线。</summary>
    public static int Export(float[] lineVertices, string path)
    {
        var doc = new CadDocument();
        int count = 0;
        for (int i = 0; i + 11 < lineVertices.Length; i += 12)
        {
            var start = new XYZ(lineVertices[i], lineVertices[i + 1], lineVertices[i + 2]);
            var end = new XYZ(lineVertices[i + 6], lineVertices[i + 7], lineVertices[i + 8]);
            doc.Entities.Add(new Line { StartPoint = start, EndPoint = end });
            count++;
        }
        using var writer = new DxfWriter(path, doc, false);
        writer.Write();
        return count;
    }
}
