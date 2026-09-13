using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Views.Mining;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 排土工程组的后两钮：
/// 「排土场按量推进」= 原 <c>DumpAdvanceDialog.ShowSingleton</c>（非模态单例，窗口见 <see cref="DumpAdvanceWindow"/>；形态画到层「排土推进_形态」）。
/// 「排土场容量校核」= 原 <c>CreateDumpCapacityCommand</c> + <c>DumpCapacityDialog</c>：选【现状面】+【排土场坡面】（图上的三角网）→
/// 填方体积 = 设计形态总容积 → 与所选排土场台账的设计容量对账（差额/占比，超 20% 告警）→ 可选写回台账（改前改后都打出来）。
/// 算量走 <see cref="DumpCapacityCalc"/>（原版走内核两期填挖方，同口径托管）。
/// </summary>
public partial class MainWindow
{
    private DumpAdvanceWindow? _dumpAdvanceWin;
    private const string DumpAdvanceOverlayLayer = "排土推进_形态";

    private void DumpAdvanceCmd()
    {
        var db = EnsureGeoDb();
        if (db == null) return;
        if (_dumpAdvanceWin != null) { _dumpAdvanceWin.Reload(); _dumpAdvanceWin.Activate(); StatusMsg.Text = "排土场按量推进已在前台"; return; }
        var host = new DumpAdvanceHost
        {
            Db = () => _geoDb?.Connection,
            ShowOverlay = (rings, colors) =>
            {
                BeginChange();
                foreach (var e in _scene.Entities.Where(x => x.LayerName == DumpAdvanceOverlayLayer).ToList()) _scene.Remove(e);
                for (int i = 0; i < rings.Count; i++)
                {
                    var ring = rings[i]; uint c = i < colors.Count ? colors[i] : 0x00A0FFu;
                    var pl = new PolylineEntity { Closed = true, LayerName = DumpAdvanceOverlayLayer, Zs = new List<double>(),
                        Cr = ((c >> 16) & 0xFF) / 255f, Cg = ((c >> 8) & 0xFF) / 255f, Cb = (c & 0xFF) / 255f, LineWeight = 40 };
                    for (int k = 0; k + 2 < ring.Length; k += 3) { pl.Points.Add((ring[k], ring[k + 1])); pl.Zs.Add(ring[k + 2]); }
                    if (pl.Points.Count >= 2 && Dist2(pl.Points[0], pl.Points[^1]) < 1e-12) { pl.Points.RemoveAt(pl.Points.Count - 1); pl.Zs.RemoveAt(pl.Zs.Count - 1); }
                    _scene.Add(pl);
                }
                RefreshScene();
            },
            ClearOverlay = () =>
            {
                int n = _scene.Entities.Count(x => x.LayerName == DumpAdvanceOverlayLayer);
                if (n == 0) return;
                BeginChange();
                foreach (var e in _scene.Entities.Where(x => x.LayerName == DumpAdvanceOverlayLayer).ToList()) _scene.Remove(e);
                RefreshScene();
            },
            Echo = (s, warn) => EditEcho(s, warn ? EchoLevel.Warn : EchoLevel.Info),
        };
        var w = new DumpAdvanceWindow(host);
        _dumpAdvanceWin = w;
        w.Closed += (_, _) => _dumpAdvanceWin = null;
        w.Show(this);
        EditEcho("排土场按量推进:给排弃量 → 沿排土条带的带序吃下去 → 反出推进到第几带、形态、剩余库容");
    }

    private async Task DumpCapacityCheckCmd(string cmd)
    {
        var meshes = _scene.Entities.OfType<MeshEntity>().Where(m => m.Visible && m.Tris.Count > 0 && _layers.IsShown(m.LayerName)).ToList();
        if (meshes.Count < 2)
        {
            StatusMsg.Text = "排土场容积：图里不足两张三角网 —— 先跑【排土场放坡】出「排土场_坡面」，并确保现状面已在图上。";
            EditEcho(StatusMsg.Text, EchoLevel.Warn); return;
        }
        var db = EnsureGeoDb();
        if (db == null) return;
        var names = meshes.Select((m, i) => $"{i + 1}. {m.Name}（{m.LayerName}，{m.Tris.Count} 三角）").ToList();
        var sites = new List<Data.GeoDataQueries.DumpSiteRow>();
        try { sites = Data.GeoDataQueries.GetDumpSites(db.Connection); } catch { }
        var siteNames = new List<string> { "（不对账）" };
        siteNames.AddRange(sites.Select(s => $"{s.Name}（{s.DumpId}）· 台账设计容量 {s.DesignCapacityWanM3:N0} 万m³"));

        // 预选：选中集里的两张网（先现状面后坡面）；否则按层名猜排土场坡面
        var sel = _selected.OfType<MeshEntity>().Where(meshes.Contains).ToList();
        int iTer = sel.Count >= 1 ? meshes.IndexOf(sel[0]) : Math.Max(0, meshes.FindIndex(m => !m.LayerName.Contains("排土")));
        int iFace = sel.Count >= 2 ? meshes.IndexOf(sel[1]) : Math.Max(0, meshes.FindIndex(m => m.LayerName.Contains("排土") && m.LayerName.Contains("坡面")));
        if (iFace == iTer) iFace = iTer == 0 ? 1 : 0;
        var inv = CultureInfo.InvariantCulture;
        var v = await Views.Modeling.PromptDialog.AskAsync(this, "排土场容量校核", new List<Views.Modeling.PromptDialog.Field>
        {
            new("ter", "现状面", names[iTer], Choices: names.ToArray(), Numeric: false),
            new("face", "排土场坡面", names[iFace], Choices: names.ToArray(), Numeric: false),
            new("cell", "格网 (m)", "2", "m"),
            new("dz", "最小高差 (m)", "0.05", "m", Hint: "|坡面−现状| 小于此不计"),
            new("site", "对账排土场", siteNames[0], Choices: siteNames.ToArray(), Numeric: false),
            new("wb", "把算出的容积写回台账的设计容量", "", Bool: true),
        }, "算【排土场坡面】相对【现状面】的填方体积 —— 这就是这个设计形态的总容积。\n与【排土条带】的逐带库容不是一个口径：容积是形态总量(上限)，条带库容受分割长度/条带宽度/区域边界约束，必然更小。");
        if (v == null) { StatusMsg.Text = "排土场容积：用户取消"; EditEcho("排土场容积:用户取消"); return; }
        int a = names.IndexOf(v.S("ter")), b = names.IndexOf(v.S("face"));
        if (a < 0 || b < 0 || a == b) { StatusMsg.Text = "排土场容积：现状面与排土场坡面须是两张不同的网。"; return; }
        double cell = v.D("cell", 2), dz = v.D("dz", 0.05);
        if (cell <= 0) { StatusMsg.Text = "格网必须 > 0。"; return; }
        if (dz < 0) { StatusMsg.Text = "最小高差必须 ≥ 0。"; return; }
        int si = siteNames.IndexOf(v.S("site")) - 1;
        var site = si >= 0 && si < sites.Count ? sites[si] : null;
        bool writeBack = v.B("wb");
        if (writeBack && site == null) { StatusMsg.Text = "要写回台账得先选一个对账排土场。"; return; }

        var ter = meshes[a]; var face = meshes[b];
        var st = SamplerOf(ter); var sf = SamplerOf(face);
        if (st == null || sf == null) { StatusMsg.Text = "排土场容积：面无法采样。"; return; }
        double minX = face.Verts.Min(p => p.x), maxX = face.Verts.Max(p => p.x), minY = face.Verts.Min(p => p.y), maxY = face.Verts.Max(p => p.y);
        EditEcho($"> 排土场容积:现状面「{ter.Name}」 → 排土场坡面「{face.Name}」(格网 {cell:0.##}m · 最小高差 {dz:0.###}m),计算中…");
        var res = await Task.Run(() => DumpCapacityCalc.Compute(st, sf, minX, minY, maxX, maxY, cell, dz));
        if (!res.Ok) { StatusMsg.Text = "排土场容积失败：两张面在平面上没有重叠区域，一格都采不到。"; EditEcho(StatusMsg.Text, EchoLevel.Error); return; }

        EditEcho($"✓ 排土场容积(填方)= {res.FillM3:N0} m³ = {res.FillM3 / 1e4:N1} 万m³(投影面积 {res.AreaFillM2:N0} m² · 采样 {res.Sampled}/{res.Cells} 格)", EchoLevel.Success);
        if (res.CutM3 > 1e-6)
        {
            bool big = res.CutM3 > 0.01 * Math.Max(res.FillM3, 1e-9);
            EditEcho($"  ⚠ 同时算出挖方 {res.CutM3:N0} m³({res.AreaCutM2:N0} m²) —— 排土场坡面有一片落在现状面【之下】。"
                   + (big ? "占比已超填方 1%,基本可以确定放坡穿地了,先查境界线的起始标高。" : "占比很小,多半是格网在边界上的锯齿。"), big ? EchoLevel.Warn : EchoLevel.Info);
        }
        double geoWan = res.FillM3 / 1e4;
        string status = $"排土场容积(填方) {res.FillM3:N0} m³ = {geoWan:N1} 万m³";
        if (site == null)
            EditEcho($"  (未选对账排土场 —— 这个 {geoWan:N1} 万m³ 只是几何数,没跟任何台账对上。要对账请在窗口里选一个排土场。)");
        else
        {
            double led = site.DesignCapacityWanM3, diff = geoWan - led;
            string rel = led > 1e-9 ? $"{diff / led * 100:+0.#;-0.#;0}%" : "台账为 0,无从算占比";
            EditEcho($"  【对账】{site.Name}({site.DumpId}):几何容积 {geoWan:N1} 万m³ vs 台账设计容量 {led:N1} 万m³ ⇒ 差 {diff:+N1;-N1;0} 万m³({rel})");
            status += $" · 对账 {site.Name}: 差 {diff:+N1;-N1;0} 万m³({rel})";
            if (led <= 1e-9) EditEcho("    台账里的设计容量是 0 —— 那一列本来就是人手填的,多半从没填过。勾上「写回台账」可以用这个几何数把它补上。", EchoLevel.Warn);
            else if (Math.Abs(diff) > 0.2 * led) EditEcho("    ⚠ 差额超过 20%。先别急着改台账 —— 也可能是这次选的两张面不对,或者台账那个数对应的是另一期的形态。", EchoLevel.Warn);
            if (writeBack)
            {
                try
                {
                    using var q = db.Connection.CreateCommand();
                    q.CommandText = $"UPDATE dump_site SET design_capacity_wan_m3 = {geoWan.ToString("R", inv)} WHERE dump_id = '{site.DumpId.Replace("'", "''")}'";
                    int n = q.ExecuteNonQuery();
                    if (n == 0) EditEcho("    写回失败:没有更新到任何行。", EchoLevel.Warn);
                    else { EditEcho($"    ✓ 已写回台账:设计容量 {led:N1} → {geoWan:N1} 万m³", EchoLevel.Success); status += " · 已写回台账"; }
                }
                catch (Exception ex) { EditEcho($"    写回台账异常:{ex.Message}", EchoLevel.Error); }
            }
        }
        StatusMsg.Text = "✓ " + status;
    }
}
