using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

public partial class MainWindow
{
    private async Task EdPolylineMarkStartAsync()
    {
        var lines = await SelectObjectsAsync<PolylineEntity>("标识起点", "多段线", 1,
            line => line.Points.Count > 0);
        if (lines.Count == 0) return;

        BuildPolylineAnnotations(lines, start: true);
        EditEcho($"标识起点：已标识 {lines.Count} 条多段线", EchoLevel.Success);
    }

    private async Task EdPolylineMarkOrderAsync()
    {
        var lines = await SelectObjectsAsync<PolylineEntity>("标识线序", "多段线", 1,
            line => line.Points.Count > 0);
        if (lines.Count == 0) return;

        BuildPolylineAnnotations(lines, start: false);
        int vertices = lines.Sum(line => line.Points.Count);
        EditEcho($"标识线序：已标识 {lines.Count} 条多段线、{vertices} 个顶点", EchoLevel.Success);
    }

    private void BuildPolylineAnnotations(IReadOnlyList<PolylineEntity> lines, bool start)
    {
        _active.Overlay.Clear();
        double textHeight = PolylineAnnotationBuilder.SuggestTextHeight(lines);
        foreach (var line in lines)
        {
            var annotations = start
                ? PolylineAnnotationBuilder.BuildStart(line, textHeight)
                : PolylineAnnotationBuilder.BuildOrder(line, textHeight);
            foreach (var entity in annotations) _active.Overlay.Add(entity);
        }
        RefreshScene();
    }
}
