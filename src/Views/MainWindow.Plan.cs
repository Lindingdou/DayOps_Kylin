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
        // ①② 短期（ShortTermSchemeStore：已确定 → 已排产首套），逐月 P/V（采出万t / 剥离万m³，Flows 非空时按流聚合）
        var stConf = ShortTermSchemeStore.Confirmed;
        var st = stConf is { Months.Count: > 0 } ? stConf : ShortTermSchemeStore.Schemes.FirstOrDefault(s => s.Result != null && s.Months.Count > 0);
        if (st != null)
        {
            var sr = st.Months.Select(m => (m.Label, Math.Round(m.CoalWanT, 1), Math.Round(m.StripWanM3, 0))).ToList();
            return ($"短期月度计划「{st.Name}」（{(st == stConf ? "已确定" : "未确定，取已排产的首套")}） · {st.Months.Count} 期（逐月）", sr);
        }
        // ③④ 中长远（新库：派生/规划计算/出图共用的 LongTermSchemeStore）
        var conf = LongTermSchemeStore.Confirmed;
        var cur = conf is { Result: not null } ? conf : LongTermSchemeStore.Schemes.FirstOrDefault(s => s.Result != null && s.Periods.Count > 0);
        if (cur != null)
        {
            var rr = cur.Periods.Select(pp => (pp.Label, Math.Round(pp.CoalWanT, 1), Math.Round(pp.StripWanM3, 0))).ToList();
            return ($"中长远进度计划「{cur.Name}」（{(cur == conf ? "已确定" : "未确定，取已排产的首套")}） · {cur.Periods.Count} 期（逐年）", rr);
        }
        // 旧命令行「中长远进度计划 …」排出的方案（会话内）
        var lt = _longTermSchemes.FirstOrDefault(s => s.Periods.Count > 0);
        if (lt == null) return null;
        var rows = lt.Periods.Select(pp => (pp.Label, Math.Round(pp.CoalWanT, 1), Math.Round(pp.StripWanM3, 0))).ToList();
        return ($"中长远进度计划「{lt.Name}」（未确定，取已排产的首套） · {lt.Periods.Count} 期（逐年）", rows);
    }

    // ───────────── 中长远进度计划编制组（原 CreateOpenLongTermConfig/Derive/Solve/Compare/ChartCommand）─────────────
    private LongTermConfigWindow? _longTermConfigWin;
    private LongTermDeriveWindow? _longTermDeriveWin;
    private LongTermSolveWindow? _longTermSolveWin;
    private LongTermCompareWindow? _longTermCompareWin;

    // ───────────── 短期生产计划编制组（原 CreateOpenShortTermConfig/Solve/DeriveCommand）─────────────
    private ShortTermConfigWindow? _shortTermConfigWin;
    private ShortTermSolveWindow? _shortTermSolveWin;
    private ShortTermDeriveWindow? _shortTermDeriveWin;

    /// <summary>短期①「短期生产计划编制」= 原 CreateOpenShortTermConfigCommand：基础约束（月度）窗（单例）；来源候选 = 中长远方案库。</summary>
    private void OpenShortTermConfig()
    {
        if (_shortTermConfigWin != null) { _shortTermConfigWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_shortTermConfigWin); StatusMsg.Text = "短期生产计划编制窗口已在前台"; return; }
        var w = new ShortTermConfigWindow(LongTermSchemeStore.Schemes);
        _shortTermConfigWin = w;
        w.Closed += (_, _) => _shortTermConfigWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "短期生产计划编制：继承中长远年度 → 时间骨架/现场参数/逐月配置表/均衡·约束·比选权重 → 试算 → 保存约束（落盘）；候选方案在「派生计划方案」按作业组织×工作历生成";
    }

    /// <summary>短期②「月度计划编制」= 原 CreateOpenShortTermSolveCommand：排产窗（单例）。「月度计划编制 一键」直通一键编制。</summary>
    private void OpenShortTermSolve(string cmd = "月度计划编制")
    {
        bool oneClick = cmd.Contains("一键");
        if (_shortTermSolveWin != null) { _shortTermSolveWin.Activate(); if (oneClick) _shortTermSolveWin.OneClickSchedule(); StatusMsg.Text = "月度计划编制窗口已在前台"; return; }
        var w = new ShortTermSolveWindow(ShortTermSchemeStore.Schemes);
        _shortTermSolveWin = w;
        w.Closed += (_, _) => _shortTermSolveWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        if (oneClick) w.OneClickSchedule();
        StatusMsg.Text = "月度计划编制：⚡一键编制 / 编制选中 → 逐月计划图 + 九指标 + 逐月配置表(输入)/逐月计划表(结果) → ✔确定月度计划(写 monthly_plan 台账) / 导出报表";
    }

    private ShortTermFieldWindow? _shortTermFieldWin;

    /// <summary>短期「采场参数识别」= 原 CreateOpenShortTermFieldCommand：参数校核 / 按平盘宽度提取区域 窗（单例）。</summary>
    private void OpenShortTermField()
    {
        if (_shortTermFieldWin != null) { _shortTermFieldWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_shortTermFieldWin); StatusMsg.Text = "采场参数识别窗口已在前台"; return; }
        var w = new ShortTermFieldWindow(PlanHost);
        _shortTermFieldWin = w;
        w.Closed += (_, _) => _shortTermFieldWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "采场参数识别：选中坡顶/坡底台阶线 → 提取并校核(H/α/W/β 对规范/模板) → 回写验收库 / 写进短期计划；按平盘宽度识别达标平盘（列表管理 + overlay）";
    }

    /// <summary>短期「派生计划方案」= 原 CreateOpenShortTermDeriveCommand：作业组织×工作历 联合比选窗（单例）。</summary>
    private void OpenShortTermDerive()
    {
        if (_shortTermDeriveWin != null) { _shortTermDeriveWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_shortTermDeriveWin); StatusMsg.Text = "短期派生计划方案窗口已在前台"; return; }
        var w = new ShortTermDeriveWindow(ShortTermSchemeStore.Schemes);
        _shortTermDeriveWin = w;
        w.Closed += (_, _) => _shortTermDeriveWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "派生计划方案（短期）：勾作业组织轴 × 工作历轴 → ⚡生成并编制 → 对比矩阵 + 逐月产量对比 + 雷达 → 确定选中为主方案 / 导出";
    }

    /// <summary>「加载块体模型」小窗（LT7 配套入口）：列出/激活/导入块体。「导入块体…」直通建模页签的导入块体对话框。</summary>
    private void OpenLongTermBlockPicker(Avalonia.Controls.Window owner)
    {
        var w = new LongTermBlockPickerWindow(o => ModelingWindowFactory.TryOpen(this, MdlCtx(), "导入块体"));
        GeoDb.GeoDbWindows.NoteLast(w);
        _ = w.ShowDialog(owner);
    }

    /// <summary>中长远①「中长远进度计划编制」= 原 CreateOpenLongTermConfigCommand：打开「中长远进度计划编制 · 基础约束」窗口（单例）。</summary>
    private void OpenLongTermConfig()
    {
        if (_longTermConfigWin != null) { _longTermConfigWin.Activate(); StatusMsg.Text = "中长远进度计划编制窗口已在前台"; return; }
        var w = new LongTermConfigWindow(PlanHost, MiningProgramStore.Schemes);
        _longTermConfigWin = w;
        w.Closed += (_, _) => _longTermConfigWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "中长远进度计划编制：继承开采程序 → 产能/达产爬坡/剥采比限值/排土/经济/决策权重 → 试算 → 保存基础约束（方案在「派生计划方案」按工作线生成）";
    }

    /// <summary>中长远③「派生计划方案」= 原 CreateOpenLongTermDeriveCommand：打开「派生进度计划方案」窗口（单例）。工作线只认图上选中的实体。</summary>
    private void OpenLongTermDerive()
    {
        if (_longTermDeriveWin != null) { _longTermDeriveWin.Activate(); StatusMsg.Text = "派生计划方案窗口已在前台"; return; }
        var w = new LongTermDeriveWindow(PlanHost, OpenLongTermBlockPicker);
        _longTermDeriveWin = w;
        w.Closed += (_, _) => _longTermDeriveWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "派生计划方案：图上选中工作线 → 拾取 → 勾产能/坡道档 → 生成多套方案（工作线 × 产能 × 坡道）→ 规划计算/综合对比/出图";
    }

    /// <summary>中长远②「规划计算」= 原 CreateOpenLongTermSolveCommand：打开「规划计算 · 排产」窗口（单例）。「规划计算 一键」直通一键排产比选。</summary>
    private void OpenLongTermSolve(string cmd = "规划计算")
    {
        bool oneClick = cmd.Contains("一键");
        if (_longTermSolveWin != null) { _longTermSolveWin.Activate(); if (oneClick) _longTermSolveWin.OneClickAuto(); StatusMsg.Text = "规划计算窗口已在前台"; return; }
        var w = new LongTermSolveWindow(PlanHost, MiningProgramStore.Schemes, OpenLongTermBlockPicker);
        _longTermSolveWin = w;
        w.Closed += (_, _) => _longTermSolveWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        if (oneClick) w.OneClickAuto();
        StatusMsg.Text = "规划计算：⚡一键排产比选 / 排产全部 / 排产选中 → 逐年进度图 + 九指标 + 逐年进度表 → ✔确定进度计划 / 导出报表";
    }

    /// <summary>中长远④「方案综合对比」= 原 CreateOpenLongTermCompareCommand：打开「方案综合对比」窗口（单例）。</summary>
    private void OpenLongTermCompare()
    {
        if (_longTermCompareWin != null) { _longTermCompareWin.Activate(); StatusMsg.Text = "方案综合对比窗口已在前台"; return; }
        var w = new LongTermCompareWindow(PlanHost);
        _longTermCompareWin = w;
        w.Closed += (_, _) => _longTermCompareWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "方案综合对比：联合对比矩阵 + 四维论证图表 + 雷达 + 方向感知加权评分排名 → 推荐方案";
    }

    private MineableAreaWindow? _mineableAreaWin;

    /// <summary>中长远组「采场/排土场圈定」= 原 CreateOpenMineableAreaCommand：区域管理窗（单例）——自动识别 / 圈画 / 笔刷编辑 / 颜色，落 mineable_region。</summary>
    private void OpenMineableArea()
    {
        if (_mineableAreaWin != null) { _mineableAreaWin.Activate(); StatusMsg.Text = "采场/排土场圈定窗口已在前台"; return; }
        var w = new MineableAreaWindow(PlanHost);
        _mineableAreaWin = w;
        w.Closed += (_, _) => _mineableAreaWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = _geoDb == null
            ? "采场/排土场圈定：GeoDataBase 未连接 —— 区域要落 mineable_region 表，请先「连接数据库」"
            : "采场/排土场圈定：自动识别（读现状线）/ 圈画新区域（视口逐点）/ 笔刷编辑（Alt+拖扩、拖缩）/ 按类着色";
    }

    /// <summary>中长远⑤「进度计划方案出图」= 原 CreateOpenLongTermChartCommand：单方案逐年进度图窗（单例）。方案取自 LongTermSchemeStore；⚡一键排产并出图只按已指定工作线重排(LT4)；导出 PNG/CSV。</summary>
    private LongTermChartWindow? _longTermChartWin;
    private void OpenLongTermChart()
    {
        if (_longTermChartWin != null) { _longTermChartWin.Reload(); _longTermChartWin.Activate(); StatusMsg.Text = "进度计划方案出图已在前台"; return; }
        var w = new LongTermChartWindow(PlanHost, programs: MiningProgramStore.Schemes);
        _longTermChartWin = w;
        w.Closed += (_, _) => _longTermChartWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        int n = LongTermSchemeStore.Schemes.Count(s => s.Result != null);
        StatusMsg.Text = n > 0
            ? $"进度计划方案出图：已载入 {n} 个已排产方案"
            : "进度计划方案出图：还没有已排产方案 —— 先到「派生计划方案」指定工作线生成方案, 再「规划计算」或「⚡一键排产并出图」";
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

        /// <summary>选集里恰好 1 条「工作线」层的多段线（原 IPitDesignCapability.TryGetSelectedSingleWorkLineMeta + GetSelectedWorkLineGeometry）。</summary>
        public WorkLineSamples? SelectedWorkLine(out long handle, out string error)
        {
            handle = 0; error = "";
            var wls = _w._selected.OfType<PolylineEntity>().Where(p => p.LayerName == WorkLineLayer && p.Points.Count >= 2).ToList();
            if (wls.Count != 1)
            {
                error = wls.Count == 0
                    ? "请在图上**选中恰好 1 条工作线**（不是普通多段线 —— 用「创建工作线」先转化）"
                    : $"选集里有 {wls.Count} 条工作线，请只选 1 条";
                return null;
            }
            var s = _w.WorkLineSamplesOf(wls[0]);
            if (s == null || !s.Success) { error = "工作线几何读取失败：" + (s?.Error ?? "基线点 < 2"); return null; }
            handle = EntityHandles.Of(wls[0]);
            return s;
        }

        public WorkLineSamples? WorkLineByHandle(long handle)
        {
            if (EntityHandles.Find(_w._scene, handle) is not PolylineEntity pl || pl.LayerName != WorkLineLayer) return null;
            var s = _w.WorkLineSamplesOf(pl);
            return s is { Success: true } ? s : null;
        }
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

        public long[] SelectedHandles() => _w._selected.Select(EntityHandles.Of).ToArray();
        public bool BeginScreenPointPick(Action<double, double, double> onPicked, Action onCancel) => _w.BeginRegionPointPick(onPicked, onCancel);
        public void EndScreenPointPick() => _w.EndRegionPointPick();
        public void ClearScreenPickMarkers() { }
        public void ShowMineableAreaOverlay(IReadOnlyList<double[]> rings, IReadOnlyList<uint> colors) => _w.ShowRegionOverlay(rings, colors);
        public void ClearMineableAreaOverlay() => _w.ClearRegionOverlay();
        public bool BeginRegionBrushEdit(double[] targetRing, uint targetColor, IReadOnlyList<double[]> contextRings, IReadOnlyList<uint> contextColors, Action<double[]> onCommit, Action onCancel)
            => _w.BeginRegionBrush(targetRing, targetColor, contextRings, contextColors, onCommit, onCancel);
        public void EndRegionBrushEdit(bool commit) => _w.EndRegionBrush(commit);
        public int SetRegionBrushRadiusPx(int px) => _w.SetRegionBrushRadius(px);
        public object? OwnerWindow => _w;
        public IReadOnlyList<string> LayerNames() => _w._layers.Layers.Select(l => l.Name).ToList();
        public void ClearSelection() { _w._selected.Clear(); _w.RefreshScene(); }
        public void Echo(string text, bool warn = false) => _w.EditEcho(text, warn ? EchoLevel.Warn : EchoLevel.Info);
        public void Refresh() => _w.RefreshScene();
    }
}

public partial class MainWindow
{
    /// <summary>
    /// @中长远示例：自检直通 —— 没块体就造「自检块体 20×6×8」（底 30% 层为煤），在块体西缘造 1 条南北向工作线（往 +X 推进）并选中，
    /// 把基础约束的产能压到 15 万t/a（示例块体煤量只有 ~130 万t，按 1000 万t/a 一年就采完），打开「派生计划方案」→ 拾取 → 生成多套方案。
    /// 之后 「规划计算 一键」/「方案综合对比」/「进度计划方案出图」都能直接跑（合成几何，非业务数据）。
    /// </summary>
    private void SelftestLongTermSample()
    {
        if (Modeling.BlockModelStore.Active == null && Modeling.BlockModelStore.Models.Count == 0) SelftestSampleBlockModel(20, 6, 8);
        var m = PlanHost.ActiveBlockModel;
        if (m == null) { StatusMsg.Text = "自检：没有块体模型"; return; }
        var bb = m.Bounds;
        double zTop = bb.maxZ;
        BeginChange();
        foreach (var e in _scene.Entities.Where(e => e.LayerName == WorkLineLayer || e.LayerName == WorkLineLayer + "_结束线").ToList()) _scene.Remove(e);
        _layers.EnsureImported(WorkLineLayer, 0.2f, 0.8f, 0.95f);
        var wl = new PolylineEntity { LayerName = WorkLineLayer, Elevation = zTop, Cr = 0.2f, Cg = 0.8f, Cb = 0.95f };
        wl.Points.Add((bb.minX, bb.minY)); wl.Points.Add((bb.minX, bb.maxY));          // 基线沿 y，往 +x 推进
        var end = new PolylineEntity { LayerName = WorkLineLayer + "_结束线", Elevation = zTop, Cr = 0.2f, Cg = 0.8f, Cb = 0.95f, Dash = new[] { 4.0, 2.0 } };
        double xe = bb.minX + (bb.maxX - bb.minX) * 0.5;
        end.Points.Add((xe, bb.minY)); end.Points.Add((xe, bb.maxY));
        _scene.Add(wl); _scene.Add(end);
        _selected.Clear(); _selected.Add(wl);
        RefreshScene();

        LongTermSchemeStore.Reset();
        LongTermSchemeStore.Base.DesignCapacityWanTa = 15;
        LongTermSchemeStore.Base.BasicStrippingYears = 1;
        LongTermSchemeStore.Base.InnerDumpStartYear = 2;
        OpenLongTermDerive();
        string s = _longTermDeriveWin!.SelftestPickAndGenerate();
        StatusMsg.Text = $"自检：中长远示例 —— 块体「{m.Name}」· 工作线 1 条(西缘, 往 +X) · A_p 15 万t/a｜{s}";
    }
}

public partial class MainWindow
{
    /// <summary>
    /// @采排圈定示例：合成一个降深采场（6 级坡顶/坡底环，800×600 m）+ 一个抬升外排土场（4 级，700×500 m）的台阶线入图，
    /// 连本机 SQLite，打开「采场/排土场圈定」→ 自动识别（跳过清空确认）→ 选中首块（合成几何，非业务数据）。
    /// </summary>
    private async Task SelftestMineableAreaSample()
    {
        try { _geoDb ??= Data.GeoDatabase.OpenSeeded(); } catch (Exception ex) { StatusMsg.Text = "自检：连库失败 " + ex.Message; return; }
        BeginChange();
        _layers.EnsureImported("点云_坡顶线", 0.9f, 0.3f, 0.2f); _layers.EnsureImported("点云_坡底线", 0.2f, 0.5f, 0.9f);
        void Ring(double cx, double cy, double hx, double hy, double z, bool crest)
        {
            var pl = new PolylineEntity { LayerName = crest ? "点云_坡顶线" : "点云_坡底线", Closed = true, Elevation = z, Cr = crest ? 0.9f : 0.2f, Cg = crest ? 0.3f : 0.5f, Cb = crest ? 0.2f : 0.9f };
            int n = 24;
            for (int i = 0; i < n; i++)
            {
                double t = 2 * Math.PI * i / n;
                pl.Points.Add((cx + hx * Math.Cos(t), cy + hy * Math.Sin(t)));   // 椭圆环（凹坑/凸堆都是同心环）
            }
            _scene.Add(pl);
        }
        for (int k = 0; k < 6; k++)   // 采场：逐级降深，环逐级内缩
        {
            double hx = 400 - 30 * k, hy = 300 - 30 * k;
            Ring(0, 0, hx, hy, 100 - 10 * k, crest: true);
            Ring(0, 0, hx - 12, hy - 12, 100 - 10 * (k + 1), crest: false);
        }
        for (int k = 0; k < 4; k++)   // 外排土场：逐级抬升（合成两坨时趋势面互相牵扯，分类器只保证识出主采场；真现场按台阶线成对/成级识别）
        {
            double hx = 350 - 30 * k, hy = 250 - 30 * k;
            Ring(1300, 0, hx, hy, 100 + 10 * k, crest: false);
            Ring(1300, 0, hx - 12, hy - 12, 100 + 10 * (k + 1), crest: true);
        }
        _selected.Clear();
        RefreshScene();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Viewport.FitBounds(new[] { -450.0, -400.0, 1900.0, 400.0 }), Avalonia.Threading.DispatcherPriority.Background);
        OpenMineableArea();
        var w = _mineableAreaWin!;
        w.SelftestAutoConfirm = true;
        await w.SelftestAutoIdentifyAsync();
        w.SelftestSelect(0);
        StatusMsg.Text = $"自检：采排圈定示例 —— 台阶线 20 环入图 → 识别 {w.SelftestRowCount} 块｜{w.SelftestStatus}";
    }
}

public partial class MainWindow
{
    /// <summary>@短期示例：打开「派生计划方案」(短期) → ⚡生成并编制（默认勾选 3 组织 × 标准工作历）→ 打开「月度计划编制」一键编制（实机核对用）。</summary>
    private void SelftestShortTermSample()
    {
        OpenShortTermDerive();
        _shortTermDeriveWin!.OnGenerate();
        OpenShortTermSolve("月度计划编制 一键");
        StatusMsg.Text = $"自检：短期示例 —— 派生 {_shortTermDeriveWin.SelftestCount} 套｜{_shortTermDeriveWin.SelftestStatus}";
    }
}

public partial class MainWindow
{
    /// <summary>@采场参数示例：合成 6 级降深采场的坡顶/坡底环（台阶高 10 m · 坡面角 ≈ 40° · 平盘 ≈ 18 m）入图并全部选中 → 开「采场参数识别」→ 提取并校核 + 按平盘宽度 ≥ 15 m 识别。</summary>
    private void SelftestShortTermFieldSample()
    {
        SelftestBenchRings();
        OpenShortTermField();
        var w = _shortTermFieldWin!;
        string s1 = w.SelftestExtract();
        string s2 = w.SelftestIdentify(15);
        StatusMsg.Text = $"自检：采场参数示例 —— 12 环选中 → 校核 {w.SelftestRowCount} 行｜{s1}｜{s2}";
    }

    /// <summary>合成 6 级降深采场坡顶/坡底环入图并全部选中（标注台阶标高 / 平盘标高清单 / 采场参数识别 自检共用）。</summary>
    private void SelftestBenchRings()
    {
        BeginChange();
        _layers.EnsureImported("点云_坡顶线", 0.9f, 0.3f, 0.2f); _layers.EnsureImported("点云_坡底线", 0.2f, 0.5f, 0.9f);
        var made = new List<SceneEntity>();
        PolylineEntity Ring(double hx, double hy, double z, bool crest)
        {
            var pl = new PolylineEntity { LayerName = crest ? "点云_坡顶线" : "点云_坡底线", Closed = true, Elevation = z, Cr = crest ? 0.9f : 0.2f, Cg = crest ? 0.3f : 0.5f, Cb = crest ? 0.2f : 0.9f };
            for (int i = 0; i < 24; i++) { double t = 2 * Math.PI * i / 24; pl.Points.Add((hx * Math.Cos(t), hy * Math.Sin(t))); }
            _scene.Add(pl); made.Add(pl); return pl;
        }
        // 每级：坡顶环 → 内缩 run=H/tanα≈12 m 的坡底环（低 10 m）→ 再内缩平盘 18 m 是下一级坡顶
        for (int k = 0; k < 6; k++)
        {
            double top = 400 - 30 * k;
            Ring(top, top - 100, 100 - 10 * k, crest: true);
            Ring(top - 12, top - 112, 100 - 10 * (k + 1), crest: false);
        }
        _selected.Clear(); _selected.AddRange(made);
        RefreshScene();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Viewport.FitBounds(new[] { -450.0, -350.0, 450.0, 350.0 }), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>@标注台阶标高示例：合成坡顶/坡底环并选中 → 开「标注台阶标高」一键标注（用选中的线）→ 开「平盘标高清单」全图统计。</summary>
    private void SelftestBenchElevationSample()
    {
        SelftestBenchRings();
        OpenBenchElevation();
        _benchElevWin!.OnAnnotate();
        string s1 = _benchElevWin.SelftestStatus;
        OpenBenchLevel();
        _benchLevelWin!.SelftestSource(all: true);
        _benchLevelWin.OnCompute();
        StatusMsg.Text = $"自检：标注台阶标高示例 —— {s1}｜平盘标高清单 {_benchLevelWin.SelftestLevelCount} 级：{_benchLevelWin.SelftestSummary}";
    }
}

public partial class MainWindow
{
    // ───────────── 短期组「标注台阶标高」SplitButton：主钮 + 查询台阶平盘标高 / 平盘标高清单… / 标注设置…（原 CreateOpenBenchElevation*Command）─────────────
    private BenchElevationWindow? _benchElevWin;
    private BenchLevelWindow? _benchLevelWin;
    private TinSampler? _queryTin;   // 「查询台阶平盘标高」的后端 TIN（只驻内存、不入场景、建一次复用）

    /// <summary>主钮「标注台阶标高」：一键给台阶线标 ▽ + 整数高程（单例窗）。</summary>
    private void OpenBenchElevation()
    {
        if (_benchElevWin != null) { _benchElevWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_benchElevWin); StatusMsg.Text = "标注台阶标高窗口已在前台"; return; }
        var w = new BenchElevationWindow(PlanHost);
        _benchElevWin = w;
        w.Closed += (_, _) => _benchElevWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "标注台阶标高：全部工作帮 / 按区域勾选 → 一键标注（▽ + 整数高程，落平盘中央）；样式在下拉「标注设置…」";
    }

    /// <summary>下拉「平盘标高清单…」（单例窗）。</summary>
    private void OpenBenchLevel()
    {
        if (_benchLevelWin != null) { _benchLevelWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_benchLevelWin); StatusMsg.Text = "平盘标高清单窗口已在前台"; return; }
        var w = new BenchLevelWindow(PlanHost);
        _benchLevelWin = w;
        w.Closed += (_, _) => _benchLevelWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "平盘标高清单：视口选中 / 指定图层 / 全图 → 可限定可采区域 → 按标高归并成级 → 统计 / 选中该级 / 复制 / 导出";
    }

    /// <summary>下拉「标注设置…」：模态配置窗（大小/字体/颜色/倾斜/落平盘），保存后写用户设置，标注时自动套用。</summary>
    private async Task OpenBenchElevationConfigAsync()
    {
        var dlg = new BenchElevationConfigWindow(BenchElevationConfig.Load());
        GeoDb.GeoDbWindows.NoteLast(dlg);
        await dlg.ShowDialog(this);
        if (dlg.Result != null) { BenchElevationConfig.Save(dlg.Result); StatusMsg.Text = $"标注设置已保存：大小 {(dlg.Result.SizeMeters > 0 ? dlg.Result.SizeMeters.ToString("0.##") + "m" : "自动")} · {(dlg.Result.AutoColorByCategory ? "按类别配色" : "#" + dlg.Result.FixedColorRgb.ToString("X6"))} · 倾斜 轴{dlg.Result.TiltAxis} {dlg.Result.TiltDeg:0.#}°"; }
    }

    /// <summary>
    /// 下拉「查询台阶平盘标高」：连续取点，每点放一个「点 + 整数高程」标记（图层 台阶标高查询）；Esc/右键结束。
    /// 取 Z 的来源：① 已建好的后端 TIN；② 场景可见三角网；③ 都没有 → 问是否用全图多段线顶点建一张隐藏 TIN（只驻内存，建一次复用）。
    /// </summary>
    private async Task BenchElevationQueryAsync()
    {
        bool useBackendTin = _queryTin != null;
        if (!useBackendTin && !_scene.Entities.OfType<MeshEntity>().Any(m => m.Visible && m.Tris.Count > 0))
        {
            bool yes = await GeoDb.CoalMsgBox.ConfirmAsync(this, "需要三角网",
                "「查询台阶平盘标高」靠三角网取点高程。当前没有可用的三角网。\n\n是否用当前视图中的所有图元创建一张三角网？（只在后端用于取高程，不显示、创建一次后续复用）\n\n「是」= 建后端 TIN 后继续取点；「否」= 终止命令。", "是", "否");
            if (!yes) return;
            if (!TryBuildBackendTin()) return;
            useBackendTin = true;
        }
        var cfg = BenchElevationConfig.Load();
        int n = 0;
        while (true)
        {
            var (kind, x, y) = await PickPointOrConfirmAsync($"查询台阶平盘标高：左键点平盘取高程（已标 {n}）· 右键/回车/Esc 结束", confirmable: true, quiet: n > 0);
            if (kind != PickKind.Picked) break;
            double? z = useBackendTin ? (_queryTin!.TrySampleZ(x, y, out double tz) ? tz : null) : SampleSurfaceZ(x, y);
            if (z == null) { EditEcho("查询台阶平盘标高：该点不在三角网内，忽略", EchoLevel.Warn); continue; }
            var m = BenchElevationAnnotator.BuildQueryMarker(x, y, z.Value, cfg.ToOptions());
            double size = cfg.SizeMeters > 0 ? cfg.SizeMeters : 10.0;
            var batch = new PlanEntityBatch { Layer = BenchElevationAnnotator.QueryLayer, LayerColor = (0x00, 0xC8, 0xFF) };
            var (tx, ty) = BenchElevationAnnotator.QueryTextAnchorXY(x, y, size);
            batch.Texts.Add((tx, ty, z.Value, size, m.Label, 0, 0, m.R, m.G, m.B));
            var ids = PlanHost.Import(batch);
            BeginChange();
            _scene.Add(new PointEntity { X = x, Y = y, Elevation = z.Value, LayerName = BenchElevationAnnotator.QueryLayer, Cr = m.R / 255f, Cg = m.G / 255f, Cb = m.B / 255f });
            RefreshScene();
            n++;
            EditEcho($"查询台阶平盘标高：({x:0.##}, {y:0.##}) → {m.Label} m", EchoLevel.Info);
        }
        StatusMsg.Text = n > 0 ? $"查询台阶平盘标高：已放 {n} 个标记（图层「{BenchElevationAnnotator.QueryLayer}」，可整层删除）" : "查询台阶平盘标高：已结束（未放标记）";
    }

    /// <summary>用全图多段线顶点做 2.5D Delaunay，解析成只驻内存的后端 TIN（原 TryBuildBackendTin：ALL → 约束 CDT → QueryTin）。</summary>
    private bool TryBuildBackendTin()
    {
        var pts = new List<(double x, double y, double z)>();
        foreach (var e in _scene.Entities)
        {
            if (e is not PolylineEntity pl || !pl.Visible || pl.LayerName == BenchElevationAnnotator.Layer || pl.LayerName == BenchElevationAnnotator.QueryLayer) continue;
            for (int i = 0; i < pl.Points.Count; i++) pts.Add((pl.Points[i].x, pl.Points[i].y, pl.Elevation + (pl.Zs != null && i < pl.Zs.Count ? pl.Zs[i] : 0)));
        }
        if (pts.Count < 3) { StatusMsg.Text = "建三角网未成功（请确认视图中有带节点的台阶线 / 多段线）。"; return false; }
        try
        {
            var xy = pts.Select(p => (p.x, p.y)).ToList();
            var tris = Cad.Delaunay.Triangulate(xy);
            var tin = TinSampler.TryBuild(pts, tris);
            if (tin == null || tin.TriangleCount < 1) { StatusMsg.Text = "建三角网未成功（顶点共线或过少）。"; return false; }
            _queryTin = tin;
            StatusMsg.Text = $"已用 {pts.Count} 个顶点建后端三角网（{tin.TriangleCount} 三角，不入场景）→ 开始取点";
            return true;
        }
        catch (Exception ex) { StatusMsg.Text = "建三角网失败：" + ex.Message; return false; }
    }
}

public partial class MainWindow
{
    // ───────────── 短期组「确定开采程序」：作业面 / 设备类型 / 工艺流程（原 CreateOpenShortTermSequenceCommand → ShortTermSequenceWindow）─────────────
    private ShortTermSequenceWindow? _shortTermSeqWin;

    /// <summary>短期「确定开采程序」= 原 <c>ShortTermSequenceWindow</c>：编辑 <see cref="Cad.Plan.ShortTermSchemeStore.Base"/> 的作业面清单（份额/备采/物料/去向）+ 设备工艺树（型号约束/配车/工艺链）。非模态单例。</summary>
    private void OpenShortTermSequence()
    {
        if (_shortTermSeqWin != null) { _shortTermSeqWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_shortTermSeqWin); StatusMsg.Text = "确定开采程序窗口已在前台"; return; }
        if (EnsureGeoDb() == null) return;   // 型号字典/工作面台账/去向台账全在库里（原版插件加载即已连库）；未连则先连库、连上后照这条命令重跑
        var w = new ShortTermSequenceWindow(Cad.Plan.ShortTermSchemeStore.Base);
        _shortTermSeqWin = w;
        w.Closed += (_, _) => _shortTermSeqWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "确定开采程序：① 设定工作面(份额/备采/面编号) → ② 制定设备类型(穿·采·运·排型号约束 + 配车) → ③ 物料与去向；右侧设备工艺树就地改；按本期单元派生作业面 / 编辑工艺流程 / 校核设备配置 / 保存开采程序";
    }

    /// <summary>@确定开采程序示例：开窗 → 增加一个作业面 → 归一份额 → 校核设备配置 → 打开第 1 面的工艺对话框（供截图；随后 @确定开采程序确认 关掉）。</summary>
    private void SelftestShortTermSequenceSample()
    {
        try { _geoDb ??= Data.GeoDatabase.OpenSeeded(); } catch (Exception ex) { StatusMsg.Text = "自检：连库失败 " + ex.Message; return; }
        OpenShortTermSequence();
        var w = _shortTermSeqWin!;
        w.SelftestAddFace();
        w.SelftestMakeRock(2, "KY-250");
        w.SelftestNormalize();
        w.SelftestCheckEquip();
        string s1 = w.SelftestStatus;
        w.SelftestSelect(0);
        w.SelftestOpenProcessDialog();
        StatusMsg.Text = $"自检：确定开采程序示例 —— {w.SelftestFaceCount} 面 / 树 {w.SelftestTreeRoots} 根｜{s1.Replace('\n', ' ')}";
    }

    /// <summary>@确定开采程序确认：按「确定」关掉工艺对话框并保存开采程序。</summary>
    private void SelftestShortTermSequenceConfirm()
    {
        var w = _shortTermSeqWin; if (w == null) { StatusMsg.Text = "自检：确定开采程序窗口未开"; return; }
        w.SelftestCloseProcessDialog(ok: true);
        w.SelftestCollapseExcept(2);
        w.SelftestSave();
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = $"自检：确定开采程序确认 —— {w.SelftestStatus.Replace('\n', ' ')}｜{w.SelftestTreeStatus.Replace('\n', ' ')}";
    }
}

public partial class MainWindow
{
    // ───────────── 短期组「采排配对」：源—汇流向矩阵 + 去向库容条 + 本月汇总（原 CreateOpenDumpPairingCommand → PlanLib.ShortTerm.DumpPairingWindow）─────────────
    private Views.Plan.DumpPairingWindow? _dumpPairingPlanWin;

    /// <summary>短期「采排配对」= 原 <c>PlanLib.ShortTerm.DumpPairingWindow</c>：月度方案 × 期次 → 行=作业面·物料 / 列=去向 的矩阵（流只取单元链对位 UnitPlanStore，不自建），库容按占容方逐月累扣。非模态单例。此前命中的是 Views.GeoDb 下按 Cad.Tasks 台账做的切片。</summary>
    private void OpenDumpPairingPlan()
    {
        if (_dumpPairingPlanWin != null) { _dumpPairingPlanWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_dumpPairingPlanWin); StatusMsg.Text = "采排配对窗口已在前台"; return; }
        if (EnsureGeoDb() == null) return;   // 去向台账（dump_site / load_unload_point）在库里
        var w = new Views.Plan.DumpPairingWindow(Cad.Plan.ShortTermSchemeStore.Schemes);
        _dumpPairingPlanWin = w;
        w.Closed += (_, _) => _dumpPairingPlanWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "采排配对：选方案/期次 → 源—汇流向矩阵（格可改、整行改投）+ 各去向库容条（占容方 Kr 口径，逐月累扣）+ 本月汇总；⇩ 取单元链的对位结果 / 重读去向台账 / 导出";
    }

    /// <summary>@采排配对示例：先跑一套短期方案（无则一键编制）→ 开采排配对 → 选第 1 行整行改投第 1 个合规去向。</summary>
    private void SelftestDumpPairingSample()
    {
        try { _geoDb ??= Data.GeoDatabase.OpenSeeded(); } catch (Exception ex) { StatusMsg.Text = "自检：连库失败 " + ex.Message; return; }
        if (Cad.Plan.PlanDestinationCatalog.Current.Count == 0)   // 空库：用合成去向核对矩阵列/库容条/改投（台账有货时照台账）
            Cad.Plan.PlanDestinationCatalog.SelftestOverride(new System.Collections.Generic.List<Cad.Plan.PlanDestination>
            {
                new() { Id = "D-IN", Name = "内排土场", Kind = Cad.Plan.PlanSinkKind.InternalDump, DesignCapacityWanM3 = 320, FilledWanM3 = 40, FallbackHaulKm = 1.6 },
                new() { Id = "D-OUT", Name = "外排土场", Kind = Cad.Plan.PlanSinkKind.ExternalDump, DesignCapacityWanM3 = 900, FilledWanM3 = 120, FallbackHaulKm = 3.4 },
                new() { Id = "D-TOP", Name = "表土堆场", Kind = Cad.Plan.PlanSinkKind.TopsoilYard, DesignCapacityWanM3 = 60, FilledWanM3 = 10, FallbackHaulKm = 2.2 },
                new() { Id = "S-CR", Name = "1号破碎站", Kind = Cad.Plan.PlanSinkKind.Crusher, FallbackHaulKm = 2.6 },
            }, "自检合成去向 4 个（库里一个去向都没有）");
        OpenDumpPairingPlan();
        var w = _dumpPairingPlanWin!;
        w.SelftestSelectRow(0);
        w.SelftestApplyRowDest(0);
        StatusMsg.Text = $"自检：采排配对示例 —— {w.SelftestRowCount} 行 × {w.SelftestColCount} 去向 · 库容条 {w.SelftestBarCount}｜{w.SelftestSummary}｜{w.SelftestStatus.Replace('\n', ' ')}";
    }
}

public partial class MainWindow
{
    // ───────────── 短期组「量驱动采剥接续」：剖面 → 排产 → 采排配对 → 外循环 → 契约 → 填月度方案（原 CreateOpenMonthlyStripCommand → MonthlyStripWindow）─────────────
    private Views.Plan.MonthlyStripWindow? _monthlyStripWin;

    /// <summary>短期「量驱动采剥接续」= 原 <c>PlanLib.ShortTerm.MonthlyStripWindow</c>：整条链唯一入口（备料：岩量剖面文件 + 排土条带位置；规则：逐月配置表只读回显 + N/剥离节奏/配对策略/期初姿态 → 排产 / 派生比选 / 按实绩重排 / 报表 / 契约 JSON / 确定为月度方案）。非模态单例。此前该钮无处理器。</summary>
    private void OpenMonthlyStrip()
    {
        if (_monthlyStripWin != null) { _monthlyStripWin.Activate(); GeoDb.GeoDbWindows.NoteLast(_monthlyStripWin); StatusMsg.Text = "量驱动采剥接续窗口已在前台"; return; }
        var w = new Views.Plan.MonthlyStripWindow();
        _monthlyStripWin = w;
        w.Closed += (_, _) => _monthlyStripWin = null;
        w.Show(this);
        GeoDb.GeoDbWindows.NoteLast(w);
        StatusMsg.Text = "量驱动采剥接续：① 备料（岩量剖面 .case/.mprof + 排土条带位置）② 规则（逐月配置表 + N/剥离节奏/配对策略/期初姿态）→ 排产 → 逐月计划/过程与结论/派生比选/实绩与重排/物料流 → 导出报表/契约 JSON → 确定为月度方案";
    }

    /// <summary>@量驱动采剥接续示例：开窗 → 装回一份合成契约 JSON（MinePlanExport 样例）看逐月表/物料流铺得出来；有真剖面与排土条带时再点排产。</summary>
    private void SelftestMonthlyStripSample()
    {
        OpenMonthlyStrip();
        var w = _monthlyStripWin!;
        var inp = SelftestMonthlyStripInput(out string err);
        if (inp == null) { StatusMsg.Text = "自检：量驱动采剥接续示例 —— 合成剖面失败：" + err; return; }
        w.SelftestRunWith(inp);
        string s1 = w.SelftestStatus;
        w.SelftestDeriveWith(inp);
        StatusMsg.Text = $"自检：量驱动采剥接续示例 —— 排产「{s1}」｜比选 {w.SelftestSchemeRows} 套「{w.SelftestStatus}」｜逐月 {w.SelftestMonthRows} 行 · 日志 {w.SelftestLogLines} 条";
    }

    /// <summary>合成一份两层煤的岩量剖面（同 S 组判据夹具：60×5×20 块 · 10 m · A 煤 120–140 · B 煤 60–80 · α 20° · 台阶 20 m）+ 内/外排土位置，装成会话输入。</summary>
    private static Cad.Plan.MonthlyStripSessionInput? SelftestMonthlyStripInput(out string err)
    {
        err = "";
        const int NX = 60, NY = 5, NZ = 20; const double CELL = 10, ROCK_H = 20, DENS = 1.35, ALPHA = 20, ZDATUM = 100;
        const double A_ROOF = 140, A_FLOOR = 120, B_ROOF = 80, B_FLOOR = 60;
        static TinSampler Plane(double z) => TinSampler.TryBuild(new[] { -5000.0, -5000.0, z, 5000.0, -5000.0, z, 5000.0, 5000.0, z, -5000.0, 5000.0, z }, new[] { 0, 1, 2, 0, 2, 3 })!;
        var blocks = new List<BlockModel.Block>(); var cA = new List<double>(); var cB = new List<double>();
        for (int k = 0; k < NZ; k++)
        {
            double cz = k * CELL + CELL * 0.5; bool a = cz >= A_FLOOR && cz <= A_ROOF, b = cz >= B_FLOOR && cz <= B_ROOF;
            for (int j = 0; j < NY; j++) for (int i = 0; i < NX; i++)
            { blocks.Add(new BlockModel.Block { X = i * CELL + CELL / 2, Y = j * CELL + CELL / 2, Z = cz, Size = CELL }); cA.Add(a ? 1 : 0); cB.Add(b ? 1 : 0); }
        }
        var src = new InclineBlockSource { Name = "自检合成块体", Blocks = blocks, Attrs = new Dictionary<string, double[]> { ["cA"] = cA.ToArray(), ["cB"] = cB.ToArray() } };
        var wl = new WorkLineSamples { Success = true }; wl.Baseline.Add((-2000, -100, ZDATUM)); wl.Baseline.Add((-2000, 200, ZDATUM)); wl.Samples.Add((-2000, 50, ZDATUM, 1, 0));
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "A煤", Attribute = "cA", Density = DENS, Roof = Plane(A_ROOF), Floor = Plane(A_FLOOR) },
            new() { Name = "B煤", Attribute = "cB", Density = DENS, Roof = Plane(B_ROOF), Floor = Plane(B_FLOOR) },
        };
        var p = InclineVolumeEngine.BuildProfile(src, new[] { wl }, Plane(1000), seams, ALPHA, 1, 0.5, ROCK_H);
        if (!p.Success || p.Rock == null) { err = p.Error ?? "无岩剖面"; return null; }
        var slots = new List<Cad.Units.DumpSlot>();
        for (int lv = 0; lv < 6; lv++) for (int b = 0; b < 5; b++)
        {
            slots.Add(new Cad.Units.DumpSlot { DumpName = "内排土场", Level = lv, Order = b, CapacityM3 = 10e4, IsInternal = true, AvailableFromMonth = 4, HaulKm = 1.2, Cx = -170 + b * 60, Cy = 100, Cz = 20 + lv * 20 });
            slots.Add(new Cad.Units.DumpSlot { DumpName = "外排土场", Level = lv, Order = b, CapacityM3 = 20e4, IsInternal = false, AvailableFromMonth = 1, HaulKm = 3.8, Cx = -900 + b * 60, Cy = 1400, Cz = 60 + lv * 20 });
        }
        var q = new double[12]; for (int i = 0; i < 12; i++) q[i] = 8;
        return new Cad.Plan.MonthlyStripSessionInput
        {
            Rock = p.Rock, CoalTargetWt = q, ZDatum = ZDATUM, Slots = slots, WorkLines = new[] { wl },
            LookaheadMonths = 3, RecoveryTotalWt = 24, StartInSteadyState = true, PlanName = "自检合成算例",
        };
    }
}
