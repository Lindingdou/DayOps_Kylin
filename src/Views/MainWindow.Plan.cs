using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Plan;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「生产计划编制」页签的窗口入口 + 规划模块对图纸的依赖实现（原 PlanLibPlugin 里那批 Create*Command + 各能力接口）。
/// 窗口全部单例（已开则前置），方案集会话级共享（<see cref="BoundarySchemeStore"/> 等）。
/// </summary>
public partial class MainWindow
{
    private PitSchemeConfigWindow? _pitConfigWin;
    private PitOptimizeWindow? _pitOptimizeWin;
    private PlanEntityHost? _planHost;

    private PlanEntityHost PlanHost => _planHost ??= new PlanEntityHost(this);

    /// <summary>优化开采设计①「境界圈定」= 原 CreateOpenPitConfigCommand：打开「境界圈定设置」窗口（单例）。</summary>
    private void OpenPitSchemeConfig()
    {
        if (_pitConfigWin != null) { _pitConfigWin.Activate(); StatusMsg.Text = "境界圈定设置已在前台"; return; }
        var w = new PitSchemeConfigWindow(PlanHost);
        _pitConfigWin = w;
        w.Closed += (_, _) => _pitConfigWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "境界圈定设置：编辑「境界优化方案」——矿床/原则 · 经济合理剥采比 · 分帮边坡角 · 面与界线 · 底宽/台阶 · 横剖面";
    }

    /// <summary>优化开采设计②「确定境界」= 原 CreateOpenPitOptimizeCommand：打开「计算·方案比选·确定最终境界」窗口（单例）。</summary>
    private void OpenPitOptimize(string cmd = "确定境界")
    {
        bool oneClick = cmd.Contains("一键");
        if (_pitOptimizeWin != null) { _pitOptimizeWin.Activate(); if (oneClick) _ = _pitOptimizeWin.OneClickAutoAsync(); StatusMsg.Text = "境界优化窗口已在前台"; return; }
        var w = new PitOptimizeWindow(PlanHost);
        _pitOptimizeWin = w;
        w.Closed += (_, _) => _pitOptimizeWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        if (oneClick) _ = w.OneClickAutoAsync();   // 「确定境界 一键」= 原窗「⚡ 一键圈定(全自动)」直通(自检用)
        StatusMsg.Text = "确定境界：求解 → 方案对比矩阵 → 选定后「确定最终境界」落地三维台阶面 + 台阶线";
    }

    private MiningProgramConfigWindow? _miningProgramConfigWin;
    private MiningProgramSolveWindow? _miningProgramSolveWin;

    /// <summary>优化开采设计③「采区划分」= 原 CreateOpenMiningProgramConfigCommand：打开「采区划分设置」窗口（单例）。境界来源 = ①②确定的方案。</summary>
    private void OpenMiningProgramConfig()
    {
        if (_miningProgramConfigWin != null) { _miningProgramConfigWin.Activate(); StatusMsg.Text = "采区划分设置已在前台"; return; }
        var w = new MiningProgramConfigWindow(PlanHost);
        _miningProgramConfigWin = w;
        w.Closed += (_, _) => _miningProgramConfigWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "采区划分设置：产状/策略 · 境界来源(必选已确定境界) · 首采区权重 · 拉沟·推进候选 · 采区数/内排 · 产能约束 · 经济";
    }

    /// <summary>优化开采设计④「开采程序确定」= 原 CreateOpenMiningProgramSolveCommand：打开「计算·比选·确定开采程序」窗口（单例）。「开采程序确定 一键」直通一键划分。</summary>
    private void OpenMiningProgramSolve(string cmd = "开采程序确定")
    {
        bool oneClick = cmd.Contains("一键");
        if (_miningProgramSolveWin != null) { _miningProgramSolveWin.Activate(); if (oneClick) _miningProgramSolveWin.OneClickAuto(); StatusMsg.Text = "开采程序窗口已在前台"; return; }
        var w = new MiningProgramSolveWindow(PlanHost);
        _miningProgramSolveWin = w;
        w.Closed += (_, _) => _miningProgramSolveWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        if (oneClick) w.OneClickAuto();
        StatusMsg.Text = "开采程序确定：一键划分 → 对比矩阵 + 拉沟候选 + 四图表 → ✔确定 落地采区/拉沟/推进箭头";
    }

    private VpCurveWindow? _vpCurveWin;

    /// <summary>优化开采设计⑥「剥采比均衡」= 原 CreateOpenVpCurveCommand：打开 VP 曲线窗口（单例）。「从计划提取」按 短期确定→短期已排→中长远确定→中长远已排 取逐期 P/V。</summary>
    private void OpenVpCurve()
    {
        if (_vpCurveWin != null) { _vpCurveWin.Activate(); StatusMsg.Text = "剥采比均衡窗口已在前台"; return; }
        var w = new VpCurveWindow(ExtractPlanPeriodsForVp, (s, warn) => EditEcho(s, warn ? EchoLevel.Warn : EchoLevel.Info));
        _vpCurveWin = w;
        w.Closed += (_, _) => _vpCurveWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "剥采比均衡：逐期录入/从计划提取 P·V → 累计 VP 曲线 + 投产/达产 + 分阶段均衡折线 + 校核";
    }

    /// <summary>「从计划提取」取数（原 LoadFromEntities 的优先级）：① 已确定短期月度方案（逐月按物料流聚合）② 已排产首套 ③ 已确定中长远 ④ 已排产中长远；都没有 → null。</summary>
    private (string src, List<(string Label, double Coal, double Strip)> rows)? ExtractPlanPeriodsForVp()
    {
        var lt = _longTermSchemes.FirstOrDefault(s => s.Periods.Count > 0);
        if (lt == null) return null;
        var rows = lt.Periods.Select(pp => (pp.Label, Math.Round(pp.CoalWanT, 1), Math.Round(pp.StripWanM3, 0))).ToList();
        return ($"中长远进度计划「{lt.Name}」（未确定，取已排产的首套） · {lt.Periods.Count} 期（逐年）", rows);
    }

    /// <summary>
    /// 规划模块对宿主的依赖（原 IEntityCapability / ISelectionCapability / IPitDesignCapability 被 PlanLib 用到的那一截）。
    /// 全部按 <see cref="EntityHandles"/> 的会话 handle 说话。
    /// </summary>
    private sealed class PlanEntityHost : IPlanEntityHost
    {
        private readonly MainWindow _w;
        public PlanEntityHost(MainWindow w) => _w = w;

        public BlockModelMeta? ActiveBlockModel => Modeling.BlockModelStore.Active ?? Modeling.BlockModelStore.PickDefault();
        public System.Data.Common.DbConnection? Db => _w._geoDb?.Connection;

        public bool TryGetPolylineWorldVertices(long handle, out double[] xyz, out bool closed)
        {
            xyz = Array.Empty<double>(); closed = false;
            if (EntityHandles.Find(_w._scene, handle) is not PolylineEntity pl || pl.Points.Count < 2) return false;
            xyz = new double[pl.Points.Count * 3];
            for (int i = 0; i < pl.Points.Count; i++) { xyz[i * 3] = pl.Points[i].x; xyz[i * 3 + 1] = pl.Points[i].y; xyz[i * 3 + 2] = pl.ZAt(i); }
            closed = pl.Closed;
            return true;
        }

        public bool TryGetEntityAabb(long handle, out double[] mn, out double[] mx)
        {
            mn = new double[3]; mx = new double[3];
            var e = EntityHandles.Find(_w._scene, handle);
            switch (e)
            {
                case MeshEntity me:
                {
                    var b = me.Bounds;
                    mn = new[] { b.minX, b.minY, b.minZ }; mx = new[] { b.maxX, b.maxY, b.maxZ };
                    return me.Verts.Count > 0;
                }
                case PolylineEntity pl when pl.Points.Count > 0:
                {
                    double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
                    for (int i = 0; i < pl.Points.Count; i++)
                    {
                        var p = pl.Points[i]; double z = pl.ZAt(i);
                        x0 = Math.Min(x0, p.x); y0 = Math.Min(y0, p.y); z0 = Math.Min(z0, z);
                        x1 = Math.Max(x1, p.x); y1 = Math.Max(y1, p.y); z1 = Math.Max(z1, z);
                    }
                    mn = new[] { x0, y0, z0 }; mx = new[] { x1, y1, z1 };
                    return true;
                }
                default: return false;
            }
        }

        public bool TryGetMesh(long handle, out List<(double x, double y, double z)> verts, out List<(int a, int b, int c)> tris)
        {
            verts = new(); tris = new();
            if (EntityHandles.Find(_w._scene, handle) is not MeshEntity me) return false;
            verts = me.Verts; tris = me.Tris;
            return true;
        }

        public IReadOnlyList<(long handle, string layer, string name)> ListEntities(int wantType)
        {
            var res = new List<(long, string, string)>();
            foreach (var e in _w._scene.Entities)
            {
                if (!e.Visible) continue;
                if (wantType == PlanEntityType.TriangleMesh && e is MeshEntity me)
                    res.Add((EntityHandles.Of(e), e.LayerName, me.Name));
                else if (wantType == PlanEntityType.Polyline && e is PolylineEntity pl)
                    res.Add((EntityHandles.Of(e), e.LayerName, $"多段线({pl.Points.Count} 点{(pl.Closed ? "·闭合" : "")})"));
            }
            return res;
        }

        public long[] GetHandlesByLayer(string layer)
            => _w._scene.Entities.Where(e => e.LayerName == layer).Select(EntityHandles.Of).ToArray();

        public void DeleteEntities(IEnumerable<long> handles)
        {
            var set = new HashSet<long>(handles);
            var victims = _w._scene.Entities.Where(e => set.Contains(EntityHandles.Peek(e))).ToList();
            if (victims.Count == 0) return;
            _w.BeginChange();
            foreach (var v in victims) _w._scene.Remove(v);
            _w.RefreshScene();
        }

        public long[] Import(PlanEntityBatch batch)
        {
            var (lr, lg, lb) = batch.LayerColor;
            if (_w._layers.Get(batch.Layer) == null) _w._layers.EnsureImported(batch.Layer, lr / 255f, lg / 255f, lb / 255f);
            _w.BeginChange();
            var made = new List<SceneEntity>();
            foreach (var (x0, y0, z0, x1, y1, z1, r, g, b) in batch.Lines)
            {
                var pl = new PolylineEntity { LayerName = batch.Layer, Cr = r / 255f, Cg = g / 255f, Cb = b / 255f, Zs = new List<double> { z0, z1 } };
                pl.Points.Add((x0, y0)); pl.Points.Add((x1, y1));
                _w._scene.Add(pl); made.Add(pl);
            }
            foreach (var (xs, ys, z, r, g, b) in batch.Rings)
            {
                var pl = new PolylineEntity { Closed = true, LayerName = batch.Layer, Cr = r / 255f, Cg = g / 255f, Cb = b / 255f, Zs = new List<double>() };
                for (int i = 0; i < xs.Length; i++) { pl.Points.Add((xs[i], ys[i])); pl.Zs.Add(z); }
                _w._scene.Add(pl); made.Add(pl);
            }
            foreach (var (name, verts, tris, r, g, b) in batch.Meshes)
            {
                var me = new MeshEntity(name, verts, tris) { LayerName = batch.Layer, Cr = r / 255f, Cg = g / 255f, Cb = b / 255f };
                _w._scene.Add(me); made.Add(me);
            }
            foreach (var (x, y, z, height, text, ha, va, r, g, b) in batch.Texts)
            {
                var te = new TextEntity { X = x, Y = y, Elevation = z, Height = height, Text = text, HAlign = ha, VAlign = va, LayerName = batch.Layer, Cr = r / 255f, Cg = g / 255f, Cb = b / 255f };
                _w._scene.Add(te); made.Add(te);
            }
            _w.RefreshScene();
            return made.Select(EntityHandles.Of).ToArray();
        }

        public async Task<long?> PickInViewportAsync(int wantType, string prompt)
        {
            SceneEntity? e = wantType == PlanEntityType.TriangleMesh
                ? await _w.PickEntityInViewportAsync<MeshEntity>(prompt)
                : await _w.PickEntityInViewportAsync<PolylineEntity>(prompt);
            return e == null ? null : EntityHandles.Of(e);
        }

        public Task<(double x, double y)?> PickPointAsync(string prompt) => _w.PickWorldPointAsync(prompt);

        public void SelectByHandle(long handle, bool addToSelection = false)
        {
            var e = EntityHandles.Find(_w._scene, handle);
            if (e == null) return;
            var list = addToSelection ? _w._selected.Concat(new[] { e }).Distinct().ToList() : new List<SceneEntity> { e };
            _w.SelectEntities(list);
        }

        public void Echo(string text, bool warn = false) => _w.EditEcho(text, warn ? EchoLevel.Warn : EchoLevel.Info);
        public void Refresh() => _w.RefreshScene();
    }
}
