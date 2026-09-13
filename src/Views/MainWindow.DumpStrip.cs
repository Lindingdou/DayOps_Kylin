using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Views.Mining;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「排土条带」入口（忠实原 <c>DumpStripDialog.ShowSingleton</c>：非模态单例）。窗口逻辑见 <see cref="DumpStripWindow"/>，
/// 这里只把图上多段线（稳定编号同「创建工程位置」的 <see cref="EpHandle"/>）/图层名/壳体入图/库连接交给它。
/// 首次用库要先连库：<see cref="EnsureGeoDb"/> 连上后会把本命令重跑一遍（那里的约定）。
/// </summary>
public partial class MainWindow
{
    private DumpStripWindow? _dumpStripWin;

    private void DumpStripCmd()
    {
        var db = EnsureGeoDb();
        if (db == null) return;   // 正在连库：连上后重跑
        if (_dumpStripWin != null) { _dumpStripWin.Reload(); _dumpStripWin.Activate(); StatusMsg.Text = "排土条带已在前台"; return; }
        var host = new DumpStripHost
        {
            Db = () => _geoDb?.Connection,
            LayerNames = () => _layers.Layers.Select(l => l.Name).ToList(),
            Lines = () =>
            {
                var list = new List<DumpSourceLine>();
                foreach (var e in _scene.Entities)
                    if (e is PolylineEntity pl && pl.Visible && _layers.IsShown(pl.LayerName) && pl.Points.Count >= 2)
                        list.Add(new DumpSourceLine { Id = EpHandle(pl), Layer = pl.LayerName, Xyz = PolylineToFlatXyz(pl), Closed = pl.Closed, Selected = _selected.Contains(pl) });
                return list;
            },
            AddShells = (layer, meshes) =>
            {
                var hs = new ulong[meshes.Count];
                BeginChange();
                int k = 0;
                foreach (var e in _scene.Entities.Where(x => x.LayerName == layer).ToList()) _scene.Remove(e);   // 重切：同排土场上一批壳子换掉
                for (int i = 0; i < meshes.Count; i++)
                {
                    var m = meshes[i];
                    if (m == null || !m.Ok) continue;
                    var me = new MeshEntity($"{layer}#{i + 1}", m.Verts, m.Tris) { LayerName = layer, Cr = 0.62f, Cg = 0.52f, Cb = 0.40f };
                    _scene.Add(me); hs[i] = EpHandle(me); k++;
                }
                RefreshScene();
                EditEcho($"排土条带：壳子体入图 {k} 个（层「{layer}」）");
                return hs;
            },
            Echo = (s, warn) => EditEcho(s, warn ? EchoLevel.Warn : EchoLevel.Success),
        };
        var w = new DumpStripWindow(host);
        _dumpStripWin = w;
        w.Closed += (_, _) => _dumpStripWin = null;
        w.Show(this);
        StatusMsg.Text = "排土条带：选排土场 + 台阶线来源 → 识别台阶 → 生成位置（壳子体入图 + 位置清单落库 dump_strip）";
    }
}
