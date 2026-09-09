using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views.Modeling;
using PitMine3D.Kylin.Views.PointCloud;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 点云处理（原 PointCloudLib 六组 32 个命令）接入系统。
///
/// 与原版一致的三条骨架：
///   ① <b>点云是数据集不是散点</b> —— 一份点云 = 一个 <see cref="PointCloudEntity"/>，可整份显隐/删除/重命名；
///   ② <b>算子非破坏</b> —— 每个算子读「当前点云」，结果作为**新点云**入场景，原点云保留可对比；
///   ③ <b>产物自动成为当前点云</b> —— 抽稀 → 去噪 → 滤波 → 建面 一路串得下去（原版这条曾因各算子
///      硬编码 datasetIndex=0 而断掉，「抽稀完再去噪」其实还在动原始点云）。
///
/// 没有点云时，各命令回落到原有的「选 CSV 文件」通路（返回 false 交回 <c>OnRibbonCommand</c>），
/// 既保住既有能力，也保证有点云时行为与原版一致。
/// </summary>
public partial class MainWindow
{
    // ═══════════════════ 点云数据集注册表 ═══════════════════

    private PointCloudEntity? _pcCurrent;
    private PointCloudManagerWindow? _pcManager;

    /// <summary>场景中的全部点云（含隐藏的：管理面板要能把隐藏的再显出来）。</summary>
    internal List<PointCloudEntity> PcClouds() => _scene.Entities.OfType<PointCloudEntity>().ToList();

    /// <summary>
    /// 当前点云：显式设过且还在场景里就用它；否则退回场景里最后一份点云。
    /// 退回这条不能省 —— 撤销/重做/打开工程都会整份重建场景实体，
    /// 记着的那个引用当场变成孤儿，不退回就成了「有点云却说没有」。
    /// </summary>
    internal PointCloudEntity? PcCurrentCloud
    {
        get
        {
            var all = PcClouds();
            if (all.Count == 0) return null;
            if (_pcCurrent != null && all.Contains(_pcCurrent)) return _pcCurrent;
            _pcCurrent = all[^1];
            return _pcCurrent;
        }
    }

    /// <summary>切「当前点云」（算子跑完自动切到产物上，链式操作靠它成立）。</summary>
    internal void PcSetCurrent(PointCloudEntity? pc)
    {
        _pcCurrent = pc;
        _pcManager?.RefreshFromOutside();
    }

    /// <summary>点云在视口里的点径（像素）：取各点云的最大值，缺省 2。</summary>
    private float PcPointPixels()
    {
        float px = 2f;
        foreach (var c in PcClouds()) if (c.Visible && c.PointPixels > px) px = c.PointPixels;
        return px;
    }

    /// <summary>点云清单的下拉项文本（带序号与点数，重名也能分清）。</summary>
    private static string[] PcChoices(IReadOnlyList<PointCloudEntity> clouds)
    {
        var a = new string[clouds.Count];
        for (int i = 0; i < clouds.Count; i++)
            a[i] = $"{i + 1}. {clouds[i].Name} ({clouds[i].PointCount:N0} 点)";
        return a;
    }

    private static string PcLabel(IReadOnlyList<PointCloudEntity> clouds, PointCloudEntity? pick)
    {
        int i = pick == null ? -1 : clouds.ToList().IndexOf(pick);
        return i < 0 ? (clouds.Count > 0 ? PcChoices(clouds)[0] : "") : PcChoices(clouds)[i];
    }

    private static PointCloudEntity? PcResolve(IReadOnlyList<PointCloudEntity> clouds, string label)
    {
        var choices = PcChoices(clouds);
        for (int i = 0; i < choices.Length; i++) if (choices[i] == label) return clouds[i];
        int dot = label.IndexOf('.');
        if (dot > 0 && int.TryParse(label.Substring(0, dot), out int n) && n >= 1 && n <= clouds.Count) return clouds[n - 1];
        return clouds.Count > 0 ? clouds[0] : null;
    }

    /// <summary>
    /// 取当前点云。没有点云时按原版口径报「请先加载点云」并返回 false ——
    /// 点云组的命令一律不去弹"选个 CSV"的文件框：原版这些命令只吃已加载的点云，
    /// 弹文件框既不是原版行为，也会让用户以为这条命令跟点云无关。
    /// </summary>
    private bool PcNeedCloud(string what, out PointCloudEntity cur, out List<PointCloudEntity> all)
    {
        all = PcClouds();
        cur = PcCurrentCloud!;
        if (cur != null) return true;
        cur = null!;
        StatusMsg.Text = $"{what}：场景中没有已加载的点云，请先「加载点云」。";
        return false;
    }

    /// <summary>「源点云」下拉行（同原版各算子对话框首项）。</summary>
    private static PcRow PcSourceRow(IReadOnlyList<PointCloudEntity> clouds, PointCloudEntity cur, double labelWidth = 110)
        => PcRow.Combo("src", "源点云", PcChoices(clouds), PcLabel(clouds, cur), null,
                       "算子的输入点云；结果作为新点云入场景，原点云保留", labelWidth, 300);

    /// <summary>
    /// 算子产物入场景：新点云挂在源点云同层同样式上，成为当前点云，并刷新管理面板。
    /// 调用方负责先 <c>BeginChange()</c>（可撤销）。
    /// </summary>
    private PointCloudEntity PcCommit(PointCloudEntity? from, string name,
                                      List<(double x, double y, double z)> pts,
                                      List<(float r, float g, float b)>? colors = null)
    {
        var pc = new PointCloudEntity(name, pts) { Colors = colors };
        if (from != null) { pc.CopyStyleFrom(from); pc.PointPixels = from.PointPixels; }
        else AssignLayer(pc);
        pc.Visible = true;
        _scene.Add(pc);
        PcSetCurrent(pc);
        return pc;
    }

    /// <summary>
    /// 取命令要用的场景对象，按 AutoCAD 的动词-名词流程（同「编辑」组）：
    /// 已有预选就直接用；没预选但场景里有候选 → 先激活命令再提示「选择对象」，右键/回车确定；
    /// 场景里压根没有这类对象 → 返回空表，调用方回落到既有的「选文件」通路。
    ///
    /// 不写成「请先选中 X」然后什么都不做 —— 那是死胡同，用户点了按钮却无事发生。
    /// </summary>
    private async Task<List<T>> PcSelectAsync<T>(string cmdName, string what, Func<T, bool>? filter = null)
        where T : SceneEntity
    {
        var pre = _selected.OfType<T>().Where(e => filter == null || filter(e)).ToList();
        if (pre.Count > 0) return pre;
        bool any = _scene.Entities.OfType<T>().Any(e => e.Visible && _layers.IsSelectable(e.LayerName) && (filter == null || filter(e)));
        if (!any) return new List<T>();
        return await SelectObjectsAsync<T>(cmdName, what, 1, filter);
    }

    /// <summary>取一条剖面线（多段线优先，其次直线）；同 <see cref="PcSelectAsync{T}"/> 的动词-名词流程。</summary>
    private async Task<List<(double x, double y)>?> PcSelectLineAsync(string cmdName)
    {
        var pre = PcSelectedLine();
        if (pre != null) return pre;
        bool anyPl = _scene.Entities.OfType<PolylineEntity>().Any(e => e.Visible && e.Points.Count >= 2 && _layers.IsSelectable(e.LayerName));
        bool anyLn = _scene.Entities.OfType<LineEntity>().Any(e => e.Visible && _layers.IsSelectable(e.LayerName));
        if (!anyPl && !anyLn) return null;
        var got = await SelectObjectsAsync<SceneEntity>(cmdName, "剖面线（多段线/直线）", 1,
            e => e is PolylineEntity { Points.Count: >= 2 } or LineEntity);
        if (got.Count == 0) return null;
        return PcSelectedLine();
    }

    /// <summary>把点云缩放到视口（点云不进线段通道，得显式给包围盒）。</summary>
    private void PcZoomTo(PointCloudEntity pc)
    {
        if (pc.PointCount == 0) return;
        Viewport.FitBounds(pc.Bounds2D);
    }

    private static List<(double x, double y, double z)> PcPts(PointCloudEntity pc) => new(pc.Pts);

    /// <summary>
    /// 格网默认值：按点密度取「平均点间距 × 2」，不小于 <paramref name="floor"/>。
    /// 不能照几何跨度定 —— 格子比点间距还细时, 大半格子是空的, 而"只统计有数据的格"这条
    /// (不虚构地形)会让面积/方量成倍偏小。
    /// </summary>
    private static double PcAutoCell(PointCloudEntity pc, double floor)
    {
        var b = pc.Bounds;
        double spacing = PointCloudOps.MeanSpacing(pc.PointCount, (b.maxX - b.minX) * (b.maxY - b.minY));
        return Math.Max(floor, Math.Round(spacing * 2, 2));
    }

    // ═══════════════════ 命令派发 ═══════════════════

    /// <summary>
    /// 点云处理命令派发。返回 false = 本命令这次不由点云通路处理（没有点云时回落到既有 CSV 通路）。
    /// 整体兜底同建模组：这里被 async void 的 OnRibbonCommand 调用，抛异常会被静默吞掉。
    /// </summary>
    private async Task<bool> TryPointCloudCommandAsync(string cmd)
    {
        try { return await PointCloudCommandCoreAsync(cmd); }
        catch (Exception ex)
        {
            StatusMsg.Text = $"「{cmd}」执行出错：{ex.Message}";
            PitMine3D.Kylin.CrashLog.Write("点云", $"{cmd} 失败: {ex}");
            return true;
        }
    }

    private async Task<bool> PointCloudCommandCoreAsync(string cmd)
    {
        switch (cmd)
        {
            // ── 1. 点云数据 ──
            case "加载点云": case "导入点云": await PcLoadAsync(); return true;
            case "点云管理": PcOpenManager(); return true;
            case "显示/隐藏": case "点云显隐": case "点云显示隐藏": PcToggleVisible(); return true;
            case "点云着色": return await PcColorizeAsync();
            case "清除全部": case "清除点云": PcClearAll(); return true;

            // ── 2. 点云修复 ──
            case "地面点滤波": case "地面滤波": return await PcGroundFilterAsync();
            case "补洞(三角网)": return await PcFillHoleAsync();
            case "剔面(三角网)": return await PcRemoveObstaclesAsync();
            case "SOR去噪": case "SOR": return await PcDenoiseAsync(false);
            case "ROR去噪": case "ROR": return await PcDenoiseAsync(true);

            // ── 3. 点云编辑 ──
            case "点云抽稀": return await PcDecimateAsync();
            case "分割点云": return await PcSegmentAsync();
            case "高程截断": return await PcZClipAsync();
            case "坐标转换": return await PcTransformAsync();

            // ── 4. 点云分析（逐点）──
            case "逐点坡度/坡向": case "逐点坡度坡向": return await PcPointAttribAsync();
            case "点云剖面": return await PcCloudProfileAsync();
            case "位移监测 C2C": case "位移监测": case "C2C": return await PcC2cAsync();
            case "法向估计": case "点云法向": return await PcEstimateNormalsAsync();
            case "质量统计": return await PcQualityStatsAsync();
            case "坡顶底线提取": return await PcSlopeLinesAsync();

            // ── 5. 三角网重建 ──
            case "2.5D TIN": return await PcBuildTinAsync();
            case "两期点云算量": return await PcTwoEpochVolumeAsync();
            case "圈范围算量": return await PcRangeVolumeAsync();
            case "三角网着色": return await PcTinShadingAsync();

            // ── 6. 三角网分析（都要求先选中三角网；没选中就回落既有通路）──
            case "等高线生产": return await PcContourAsync();
            case "剖面分析": return await PcMeshProfileAsync();
            case "工艺参数分析": return await PcProcessParamAsync();
            case "坡度着色": PcSetShadeMode(MeshEntity.FaceShade.Slope, "坡度"); return true;
            case "坡向着色": PcSetShadeMode(MeshEntity.FaceShade.Aspect, "坡向"); return true;
            case "粗糙度": return await PcMeshAttribAsync(false);
            case "曲率": return await PcMeshAttribAsync(true);
        }
        return false;
    }

    // ═══════════════════ 1. 点云数据 ═══════════════════

    /// <summary>加载点云：LAS（含真实色/强度/分类）或 CSV/TXT/XYZ → 一份 <see cref="PointCloudEntity"/> 入场景并设为当前。</summary>
    private async Task PcLoadAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "加载点云：选 LAS 或点文件 (x,y[,z])",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("点云 (LAS/CSV/TXT/XYZ)") { Patterns = new[] { "*.las", "*.csv", "*.txt", "*.xyz" } },
                new FilePickerFileType("LAS 点云") { Patterns = new[] { "*.las" } },
            },
        });
        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        StatusMsg.Text = $"加载点云：正在读取 {name} …";

        List<(double x, double y, double z)> pts;
        List<(float r, float g, float b)>? rgb = null;
        string extra = "";
        if (path.EndsWith(".las", StringComparison.OrdinalIgnoreCase))
        {
            // 大文件读盘 + 解码放后台，UI 不假死（原版把 LAS→缓存构建挪到后台也是这个理由）
            var r = await Task.Run(() => LasImportService.Load(path, 2_000_000));
            if (!r.Success) { StatusMsg.Text = $"加载点云：LAS 读取失败 {r.Error}"; return; }
            if (r.Points.Count == 0) { StatusMsg.Text = "加载点云：LAS 头有效但无点"; return; }
            pts = r.Points;
            if (r.Colors != null && r.Colors.Count == pts.Count) rgb = r.Colors;
            extra = $" · LAS {r.VersionMajor}.{r.VersionMinor} 格式{r.PointFormat}"
                  + (r.PointCount > pts.Count ? $"（头声明 {r.PointCount:N0} 点，读入 {pts.Count:N0}）" : "")
                  + (rgb != null ? " · 含真实色" : "");
        }
        else
        {
            var r = await Task.Run(() => PointDataImportService.Load(path));
            if (!r.Success) { StatusMsg.Text = $"加载点云：导入失败 {r.Error}"; return; }
            if (r.Points.Count == 0) { StatusMsg.Text = "加载点云：无点"; return; }
            pts = r.Points;
        }

        BeginChange();
        var pc = PcCommit(null, name, pts, rgb);
        pc.RgbColors = rgb;
        pc.Source = path;
        RefreshScene();
        PcZoomTo(pc);
        StatusMsg.Text = $"加载点云「{pc.Name}」：{pc.PointCount:N0} 点已入场景{extra} · 已设为当前点云";
    }

    /// <summary>点云管理面板（非模态；已开着就前置刷新，不叠第二个窗口）。</summary>
    private void PcOpenManager()
    {
        if (_pcManager != null)
        {
            try { _pcManager.RefreshFromOutside(); _pcManager.Activate(); return; }
            catch { _pcManager = null; }
        }
        _pcManager = new PointCloudManagerWindow(
            PcClouds, () => PcCurrentCloud, PcSetCurrent,
            pc => { BeginChange(); _scene.Remove(pc); if (ReferenceEquals(pc, _pcCurrent)) _pcCurrent = null; _selected.Remove(pc); RefreshScene(); StatusMsg.Text = $"点云管理：已移除「{pc.Name}」"; },
            PcZoomTo, RefreshScene);
        _pcManager.Closed += (_, _) => _pcManager = null;
        _pcManager.Show(this);   // 非模态: 自检脚本也照开(不阻塞), 截图才核对得到面板本身
        StatusMsg.Text = $"点云管理：{PcClouds().Count} 份点云（设为当前 / 重命名 / 显隐 / 移除）";
    }

    /// <summary>显示/隐藏：全局切换所有点云的显示（不卸载数据；单独显隐走「点云管理」）。</summary>
    private void PcToggleVisible()
    {
        var all = PcClouds();
        if (all.Count == 0) { StatusMsg.Text = "显示/隐藏：场景中没有点云"; return; }
        bool anyVisible = all.Any(c => c.Visible);
        foreach (var c in all) c.Visible = !anyVisible;
        RefreshScene();
        _pcManager?.RefreshFromOutside();
        StatusMsg.Text = $"点云已{(anyVisible ? "隐藏" : "显示")}（{all.Count} 份 · 数据未卸载，可再点恢复）";
    }

    /// <summary>点云着色：真实色 (RGB) / 任意单色 / 高程色带。忠实原 ColorModeDialog 的十个预设色。</summary>
    private async Task<bool> PcColorizeAsync()
    {
        if (!PcNeedCloud("点云着色", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        string[] modes = { "恢复真实颜色 (RGB)", "单色", "高程色带" };
        string[] presets = { "白", "红", "橙", "黄", "绿", "青", "蓝", "紫", "灰", "黑" };
        // 窗体组织忠实原 Coloring/ColorModeDialog：模式单选 + 十个预设色
        var form = new PcForm
        {
            Title = "点云着色", OkText = "应用", Width = 430,
            CliDescription = "把点云切换为任意单色 / 按高程分带上色 / 恢复真实颜色 (RGB)。",
        };
        form.Text("把点云切换为任意单色或恢复真实颜色 (RGB)；也可按高程分带上色。")
            .Rows(PcSourceRow(all, cur, 70))
            .Rows(PcRow.Radios("mode", "着色方式", modes, modes[1]))
            .Rows(PcRow.Combo("color", "单色", presets, "灰", "（「单色」档用；同原版十个预设色）", null, 70, 120));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "点云着色：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        string mode = v.S("mode");

        BeginChange();
        if (mode == modes[0])
        {
            if (!pc.HasRgb) { StatusMsg.Text = $"点云着色：「{pc.Name}」没有真实色（源文件不含 RGB），可改用单色或高程色带"; return true; }
            pc.Colors = new List<(float, float, float)>(pc.RgbColors!);
            pc.Invalidate(); RefreshScene();
            StatusMsg.Text = $"点云着色：「{pc.Name}」已恢复真实颜色 (RGB)";
        }
        else if (mode == modes[2])
        {
            var b = pc.Bounds; double zr = b.maxZ - b.minZ;
            var cols = new List<(float, float, float)>(pc.PointCount);
            foreach (var p in pc.Pts) cols.Add(MeshEntity.TerrainRamp(zr > 1e-9 ? (p.z - b.minZ) / zr : 0.5));
            pc.Colors = cols; pc.Invalidate(); RefreshScene();
            StatusMsg.Text = $"点云着色：「{pc.Name}」按高程分带（{b.minZ:0.#}~{b.maxZ:0.#} m）";
        }
        else
        {
            var (r, g, bl) = PcPresetColor(v.S("color"));
            pc.SetSolidColor(r, g, bl); RefreshScene();
            StatusMsg.Text = $"点云着色：「{pc.Name}」已设为单色 {v.S("color")}";
        }
        return true;
    }

    /// <summary>原 ColorModeDialog 的十个预设色（同 RGB 值）。</summary>
    private static (float r, float g, float b) PcPresetColor(string name) => name switch
    {
        "白" => (1f, 1f, 1f),
        "红" => (220 / 255f, 60 / 255f, 60 / 255f),
        "橙" => (240 / 255f, 150 / 255f, 40 / 255f),
        "黄" => (240 / 255f, 220 / 255f, 60 / 255f),
        "绿" => (80 / 255f, 200 / 255f, 100 / 255f),
        "青" => (60 / 255f, 200 / 255f, 210 / 255f),
        "蓝" => (70 / 255f, 130 / 255f, 220 / 255f),
        "紫" => (160 / 255f, 110 / 255f, 220 / 255f),
        "黑" => (30 / 255f, 30 / 255f, 30 / 255f),
        _ => (160 / 255f, 160 / 255f, 160 / 255f),   // 灰
    };

    /// <summary>清除全部：只清点云（原版此按钮就是「一键清除场景中所有点云数据」，不动其它实体），可撤销。</summary>
    private void PcClearAll()
    {
        var all = PcClouds();
        if (all.Count == 0) { StatusMsg.Text = "清除全部：场景中没有点云"; return; }
        BeginChange();
        long n = 0;
        foreach (var pc in all) { n += pc.PointCount; _scene.Remove(pc); _selected.Remove(pc); }
        _pcCurrent = null;
        Viewport.SetHighlight(null);
        RefreshScene();
        _pcManager?.RefreshFromOutside();
        StatusMsg.Text = $"清除全部：已移除 {all.Count} 份点云 · {n:N0} 点（可 Ctrl+Z 撤销；其它实体不受影响）";
    }

    // ═══════════════════ 2. 点云修复 ═══════════════════

    /// <summary>
    /// 地面点滤波（渐进形态学 PMF）：点级剔除车辆/设备/植被/临时堆料，
    /// 输出「地面点」+「非地面点」两份新点云 —— 之后任何建面天然干净。
    /// </summary>
    private async Task<bool> PcGroundFilterAsync()
    {
        if (!PcNeedCloud("地面点滤波", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        // 窗体组织忠实原 Repair/GroundFilterDialog：说明 → 源点云 → 「按最大待剔物体尺寸」预设 → 参数 → 输出开关
        var form = new PcForm
        {
            Title = "地面点滤波（移除障碍物 · 点级）",
            OkText = "开始滤波",
            Width = 560,
            CliDescription = "点级剔除车辆/设备/植被/临时堆料（渐进形态学 PMF）：直接删点，输出「地面点」+「非地面点」两份新点云。",
        };
        form.Text("点级剔除车辆 / 设备 / 植被 / 临时堆料（渐进形态学 PMF）：直接删点，输出「地面点」+「非地面点」两份新点云，之后任何建面天然干净。")
            .Rows(PcSourceRow(all, cur, 118))
            .Presets("预设（按最大待剔物体尺寸）：",
                PcPreset.Of("植被/小杂物", "窗口 5m · 坡度允许 30°", ("win", "5"), ("slope", "30")),
                PcPreset.Of("轻型车辆", "窗口 8m · 坡度允许 40°", ("win", "8"), ("slope", "40")),
                PcPreset.Of("矿卡", "窗口 15m · 坡度允许 45°", ("win", "15"), ("slope", "45")),
                PcPreset.Of("电铲/钻机", "窗口 25m · 坡度允许 50°", ("win", "25"), ("slope", "50")))
            .Rows(
                PcRow.Num("cell", "DEM 格网", "1.0", "m", "0 = 按点密度自动；越小越慢越细", null, 118, 70),
                PcRow.Num("win", "最大物体尺寸", "15", "m", "比这更宽的东西剔不掉（形态学窗口上限）", null, 118, 70),
                PcRow.Num("slope", "地形坡度允许", "45", "°", "必须 ≥ 真实最陡台阶坡面角，否则陡壁会被当障碍剔掉", null, 118, 70),
                PcRow.Num("dh0", "初始高差阈值", "0.3", "m", "裸地面的起伏容差；越大保留越多", null, 118, 70),
                PcRow.Num("dhmax", "离地高阈值", "3.0", "m", "高出地面超过此值即判非地面", null, 118, 70),
                PcRow.Check("keep", "同时输出「非地面点/障碍物」图层（建议勾选，用于核对是否误剔）", true));

        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "地面点滤波：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;

        // 坡度允许给得过小是这个算法最常见、也最难自查的误用：整片台阶坡面会被判成障碍物剔掉。
        if (v.D("slope", 45) < 25.0)
        {
            bool go = await BlockMsgBox.ConfirmAsync(this, "坡度允许过小",
                $"「地形坡度允许」只设了 {v.D("slope", 45).ToString("0.#", Inv)}°。\n\n" +
                "露天矿台阶坡面角常在 45–70°，低于真实坡面角会把整片陡壁当成障碍物剔掉。\n\n仍要按此参数计算吗？");
            if (!go) { StatusMsg.Text = "地面点滤波：已取消"; return true; }
        }
        var pts = PcPts(pc);
        double cell = v.D("cell", 1);
        if (cell <= 0)   // 0 = 按点密度自动：格边取平均点间距的 2 倍
        {
            var bb = pc.Bounds;
            cell = Math.Max(PointCloudOps.MeanSpacing(pts.Count, (bb.maxX - bb.minX) * (bb.maxY - bb.minY)) * 2, 0.25);
        }
        double slopeDeg = v.D("slope", 45), dh0 = v.D("dh0", 0.3), dhMax = v.D("dhmax", 3), win = v.D("win", 15);
        bool keepNon = v.B("keep", true);
        double slopeTan = Math.Tan(Math.Clamp(slopeDeg, 1, 89) * Math.PI / 180.0);

        StatusMsg.Text = "地面点滤波：栅格化 + 多轮形态学开运算…";
        var res = await Task.Run(() => ProgressiveMorphFilter.Filter(pts, cell, slopeTan, dh0, dhMax, win));
        if (res.GroundCount == 0) { StatusMsg.Text = "地面点滤波：没有点被判为地面（参数过严，请调大「地形坡度允许」或「离地高阈值」）"; return true; }

        BeginChange();
        var ground = PcCommit(pc, pc.Name + "·地面点", res.Ground);
        ground.SetSolidColor(0.55f, 0.42f, 0.24f);                      // 地面 棕
        if (keepNon && res.NonGroundCount > 0)
        {
            var non = PcCommit(pc, pc.Name + "·非地面点", res.NonGround);
            non.SetSolidColor(0.9f, 0.2f, 0.2f);                        // 非地面(障碍) 红
        }
        PcSetCurrent(ground);                                           // 主线产物是地面点
        RefreshScene();
        string warn = (double)res.GroundCount / Math.Max(pts.Count, 1) < 0.5
            ? " · 提示：地面点不足输入一半，通常是「地形坡度允许」低于真实台阶坡面角、把陡壁当障碍剔掉了，请对照「非地面点」核对"
            : "";
        StatusMsg.Text = $"地面点滤波：{pts.Count:N0} → 地面 {res.GroundCount:N0}(棕) / 非地面 {res.NonGroundCount:N0}(红)"
                       + $" · 格网 {cell.ToString("0.##", Inv)}m · 坡度允许 {slopeDeg.ToString("0.#", Inv)}°{warn}";
        return true;
    }

    /// <summary>
    /// 补洞(三角网)：【需先选中三角网】补充网格内部空洞（带面积阈值，避免把矿坑大空洞/外轮廓也填上）。
    /// 原位修改、可 Ctrl+Z 撤销 —— 同原版 Repair/FillHoleDialog。
    /// </summary>
    private async Task<bool> PcFillHoleAsync()
    {
        var meshes = await PcSelectAsync<MeshEntity>("补洞(三角网)", "三角网");
        if (meshes.Count == 0) { StatusMsg.Text = "补洞(三角网)：场景里没有三角网，请先用「2.5D TIN」把点云建成面。"; return true; }
        var m = meshes[0];
        var form = new PcForm
        {
            Title = "补充空洞", OkText = "确定", Width = 460,
            CliDescription = "对选中三角网补充内部空洞（仅填面积不超过阈值的洞）。",
        };
        form.Text($"对选中三角网「{m.Name}」补充内部空洞（数据缺失区域）。仅填面积不超过阈值的洞，"
                + "避免把矿坑大空洞/坑底也填上；带 Undo。")
            .Rows(PcRow.Num("area", "最大补洞面积", "100", "m²", "（越大填得越多）", null, 100, 80));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "补洞(三角网)：已取消"; return true; }
        double maxArea = Math.Max(v.D("area", 100), 0);

        var (verts, tris, holes) = MeshHoleFill.Fill(m.Verts, m.Tris, maxArea <= 0 ? double.MaxValue : maxArea);
        if (holes == 0)
        { StatusMsg.Text = $"补洞(三角网)「{m.Name}」：没有面积 ≤ {maxArea.ToString("0.#", Inv)} m² 的内部空洞（阈值调大可多填）"; return true; }

        BeginChange();
        int tri0 = m.Tris.Count;
        m.Verts.Clear(); m.Verts.AddRange(verts);
        m.Tris.Clear(); m.Tris.AddRange(tris);
        m.VertColors = null; m.RgbColors = null;   // 顶点数变了, 旧的逐顶点色对不上, 清掉(可重新着色)
        m.Invalidate();
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"补洞(三角网)「{m.Name}」：补了 {holes} 个洞（面积 ≤ {maxArea.ToString("0.#", Inv)} m²）· "
                       + $"三角 {tri0:N0} → {m.Tris.Count:N0} · 可 Ctrl+Z 撤销";
        return true;
    }

    /// <summary>
    /// 剔面(三角网)：【需先选中三角网】按离地高 / 坡面角丢弃三角面（旧版「移除障碍物」）。
    /// 只删面不删点 —— 重建 TIN 后障碍物会回来，要真正去掉得用「地面点滤波」（同原版提示口径）。
    /// </summary>
    private async Task<bool> PcRemoveObstaclesAsync()
    {
        var meshes = await PcSelectAsync<MeshEntity>("剔面(三角网)", "三角网");
        if (meshes.Count == 0) { StatusMsg.Text = "剔面(三角网)：场景里没有三角网，请先用「2.5D TIN」把点云建成面。"; return true; }
        var m = meshes[0];
        var form = new PcForm
        {
            Title = "移除障碍物", OkText = "确定", Width = 470,
            CliDescription = "对选中三角网按离地高/坡面角剔除车辆/设备/植被/高Z倒刺。",
        };
        form.Text($"对选中的 2.5D 三角网「{m.Name}」剔除车辆 / 设备 / 植被 / 高Z倒刺。原位修改，可 Ctrl+Z 撤销。")
            .Rows(
                PcRow.Num("height", "高于地面阈值", "1.5", "m", "（高出周围地面超此值的三角视为障碍）", null, 96, 70),
                PcRow.Num("slope", "坡面角上限", "60", "°", "（法向角超此值视为障碍）", null, 96, 70))
            .Small("提示：只删面不删点 —— 重建 TIN 后障碍物会回来。要真正去掉请用「地面点滤波」。");
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "剔面(三角网)：已取消"; return true; }
        double maxH = Math.Max(v.D("height", 1.5), 0), maxSlope = Math.Clamp(v.D("slope", 60), 1, 89);

        // 两条判据都过才留：坡面角超限(陡刺) 或 局部高出周围(设备/车辆/植被顶) 任一命中即剔。
        // 「高于地面」按**局部**判(高出最高邻接顶点 > 阈值)，不按全场最低点判 —— 后者在多台阶地形上
        // 会把整片高处台阶当成"障碍"一起剔掉（308 个三角只剩 40 个就是这么来的）。
        var bySlope = new HashSet<(int, int, int)>(MeshFaceCull.BySlope(m.Verts, m.Tris, maxSlope));
        var bySpike = new HashSet<(int, int, int)>(MeshFaceCull.BySpike(m.Verts, m.Tris, maxH));
        var kept = m.Tris.Where(t => bySlope.Contains(t) && bySpike.Contains(t)).ToList();
        int removed = m.Tris.Count - kept.Count;
        if (removed == 0)
        { StatusMsg.Text = $"剔面(三角网)「{m.Name}」：没有三角超出阈值（局部高出 {maxH.ToString("0.##", Inv)}m / 坡面角 {maxSlope.ToString("0.#", Inv)}°）"; return true; }
        if (kept.Count == 0)
        { StatusMsg.Text = $"剔面(三角网)「{m.Name}」：按此阈值会把整张网剔光，已放弃（请调大阈值）"; return true; }

        BeginChange();
        int tri0 = m.Tris.Count;
        m.Tris.Clear(); m.Tris.AddRange(kept);
        m.Invalidate();
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"剔面(三角网)「{m.Name}」：剔除 {removed:N0} 个三角（局部高出周围 >{maxH.ToString("0.##", Inv)}m 或 坡面角 >{maxSlope.ToString("0.#", Inv)}°）· "
                       + $"三角 {tri0:N0} → {kept.Count:N0} · 只删面不删点，可 Ctrl+Z 撤销";
        return true;
    }

    /// <summary>SOR（统计离群，相对）/ ROR（半径离群，绝对稀疏）去噪 —— 结果作为新点云，非破坏。</summary>
    private async Task<bool> PcDenoiseAsync(bool ror)
    {
        string what = ror ? "ROR 去噪" : "SOR 去噪";
        if (!PcNeedCloud(what, out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        PcForm form;
        if (ror)
        {
            // 窗体组织忠实原 Repair/RorDialog：说明 → 蓝框(与 SOR 的互补关系) → 源点云 → 半径/最少邻点
            form = new PcForm
            {
                Title = "半径离群去噪 (ROR)", OkText = "开始去噪", Width = 540,
                CliDescription = "半径内邻点数不足即剔除。结果作为新点云加入场景（原点云保留）。",
            };
            form.Text("半径内邻点数不足即剔除。结果作为新点云加入场景（原点云保留）。")
                .Info("与「SOR 去噪」互补：SOR 判相对离群（邻距超过全局 μ+kσ），飞点成片时会把 μ 一起抬起来，于是谁都不算离群；"
                    + "ROR 判绝对稀疏，对孤立飞点 / 飞鸟 / 扬尘 / 配准鬼影更干净。代价是会一并抽薄真正稀疏的远景数据 —— 调大「最少邻点数」时要留意这一点。")
                .Rows(
                    PcSourceRow(all, cur, 110),
                    PcRow.Num("radius", "搜索半径", "0", "m", "0 = 自动取 3× 平均点间距（推荐）", null, 110, 70),
                    PcRow.Num("minPts", "最少邻点数", "4", null, "越大剔除越多", null, 110, 70));
        }
        else
        {
            // 忠实原 Repair/SorDialog：只有两个旋钮、无源下拉（作用于当前点云）
            form = new PcForm
            {
                Title = "SOR 点云去噪", OkText = "确定", Width = 420,
                CliDescription = "统计离群点去噪：按 k 邻域平均距离剔除离群噪点，结果作为新点云加入场景（原点云保留）。",
            };
            form.Text($"统计离群点去噪：按 k 邻域平均距离剔除离群噪点，结果作为新点云加入场景（原点云保留）。当前点云「{cur.Name}」。")
                .Rows(
                    PcRow.Num("k", "邻域点数 k", "16", null, null, "统计每点到最近 k 个点的平均距离", 100, 70),
                    PcRow.Num("sigma", "标准差倍数", "2.0", null, "（越小剔除越多）", "均距超过 均值+σ×标准差 即判噪点", 100, 70));
        }

        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = $"{what}：已取消"; return true; }
        var pc = ror ? (PcResolve(all, v.S("src")) ?? cur) : cur;
        var pts = PcPts(pc);

        // 半径 0 = 自动取 3× 平均点间距（同原版推荐口径）
        var pb = pc.Bounds;
        double autoRad = Math.Max(PointCloudOps.MeanSpacing(pc.PointCount, (pb.maxX - pb.minX) * (pb.maxY - pb.minY)) * 3, 1e-6);
        double rad = v.D("radius", 0); if (rad <= 0) rad = autoRad;
        int minPts = v.I("minPts", 4); if (minPts < 1) minPts = 4;
        int k = v.I("k", 16); if (k < 3) k = 16;
        double sigma = v.D("sigma", 2); if (sigma <= 0) sigma = 2;

        StatusMsg.Text = $"{what}：计算中…";
        var kept = await Task.Run(() => ror ? PointDenoise.Ror(pts, rad, minPts) : PointDenoise.Sor(pts, k, sigma));
        if (kept.Count == 0) { StatusMsg.Text = $"{what}：全部被剔除（参数过严）"; return true; }

        BeginChange();
        var np = PcCommit(pc, pc.Name + (ror ? "·ROR" : "·SOR"), kept);
        RefreshScene();
        string how = ror ? $"半径 {rad.ToString("0.###", Inv)}m{(v.D("radius", 0) <= 0 ? "(自动=3×平均间距)" : "")} · 最少邻点 {minPts}"
                         : $"k={k} · σ={sigma.ToString("0.##", Inv)}";
        StatusMsg.Text = $"{what}「{np.Name}」：{pts.Count:N0} → 保留 {kept.Count:N0}（剔除 {pts.Count - kept.Count:N0} · {how}）· 已设为当前点云";
        return true;
    }

    // ═══════════════════ 3. 点云编辑 ═══════════════════

    /// <summary>点云抽稀：体素 / 随机 / 距离(最小间距) / 自适应保特征。结果作为新点云。</summary>
    private async Task<bool> PcDecimateAsync()
    {
        if (!PcNeedCloud("点云抽稀", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        string[] modes =
        {
            "体素抽稀（均匀降密）", "随机抽稀（按比例）", "距离抽稀（最小间距）",
            "自适应保特征（平地稀 / 坡面密）— 露天矿推荐",
        };
        // 窗体组织忠实原 Decimate/DecimateDialog：说明 → 4 个单选 → 尺寸 / 比例
        var form = new PcForm
        {
            Title = "点云抽稀", OkText = "确定", Width = 460,
            CliDescription = "对已加载点云抽稀，结果作为新点云加入场景（原点云保留，可对比/删除）。",
        };
        form.Text("对已加载点云抽稀，结果作为新点云加入场景（原点云保留，可对比/删除）。")
            .Rows(PcSourceRow(all, cur, 130))
            .Rows(PcRow.Radios("mode", "抽稀方式", modes, modes[3]))
            .Rows(
                PcRow.Num("size", "体素尺寸 / 最小间距", "2", "m", "（体素/距离/自适应用）", null, 130, 70),
                PcRow.Num("ratio", "随机保留比例", "0.2", null, "0–1（随机用）", null, 130, 70));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "点云抽稀：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        var pts = PcPts(pc);
        double size = v.D("size", 2); if (size <= 0) size = 2;
        double ratio = Math.Clamp(v.D("ratio", 0.2), 0.0001, 1);
        string mode = v.S("mode");

        StatusMsg.Text = "点云抽稀：计算中…";
        var thin = await Task.Run(() => mode == modes[0] ? PointThin.Thin(pts, size)
                                      : mode == modes[1] ? PointThin.ThinRandom(pts, ratio, new Random(12345))
                                      : mode == modes[2] ? PointThin.ThinUniform(pts, size)
                                      : PointThin.ThinAdaptive(pts, size));
        if (thin.Count == 0) { StatusMsg.Text = "点云抽稀：结果为空（参数过大）"; return true; }

        BeginChange();
        var np = PcCommit(pc, pc.Name + "·抽稀", thin);
        RefreshScene();
        string how = mode == modes[1] ? $"比例 {ratio.ToString("0.###", Inv)}" : $"{size.ToString("0.##", Inv)}m";
        StatusMsg.Text = $"点云抽稀「{np.Name}」（{mode}，{how}）：{pts.Count:N0} → {thin.Count:N0}"
                       + $"（压缩 {100.0 * (1 - (double)thin.Count / Math.Max(pts.Count, 1)):0.#}%）· 已设为当前点云";
        return true;
    }

    /// <summary>分割点云：用选中的闭合多段线裁剪（圈内/圈外），结果作为新点云。</summary>
    private async Task<bool> PcSegmentAsync()
    {
        if (!PcNeedCloud("分割点云", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        var bnds = await PcSelectAsync<PolylineEntity>("分割点云", "闭合多段线（裁剪边界）", p => p.Closed && p.Points.Count >= 3);
        if (bnds.Count == 0) { StatusMsg.Text = "分割点云：场景里没有闭合多段线可作裁剪边界（先画一条，或用「高程截断」按高程裁）"; return true; }
        var bnd = bnds[0];
        string[] sides = { "保留圈内（多边形内部）", "保留圈外（多边形外部）" };
        var form = new PcForm
        {
            Title = "分割点云", OkText = "确定", Width = 440,
            CliDescription = "用选中的闭合多段线裁剪点云，结果作为新点云加入场景（原点云保留）。",
        };
        form.Text("用选中的闭合多段线裁剪点云，结果作为新点云加入场景（原点云保留）。")
            .Rows(PcSourceRow(all, cur, 96))
            .Rows(PcRow.Radios("side", "保留侧", sides, sides[0]))
            .Small($"裁剪边界：已选中的闭合多段线（{bnd.Points.Count} 个顶点）。");
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "分割点云：已取消"; return true; }
        bool inside = v.S("side") == sides[0];
        var pc = PcResolve(all, v.S("src")) ?? cur;
        var pts = PcPts(pc);
        var poly = new List<(double x, double y)>(bnd.Points);

        var kept = await Task.Run(() => PointCloudCrop.ByPolygon(pts, poly, inside));
        if (kept.Count == 0) { StatusMsg.Text = $"分割点云：{(inside ? "圈内" : "圈外")}无点（共 {pts.Count:N0} 点）"; return true; }

        BeginChange();
        var np = PcCommit(pc, $"{pc.Name}·{(inside ? "圈内" : "圈外")}", kept);
        RefreshScene();
        StatusMsg.Text = $"分割点云「{np.Name}」：保留{(inside ? "圈内" : "圈外")} {kept.Count:N0}/{pts.Count:N0} 点 · 已设为当前点云";
        return true;
    }

    /// <summary>高程截断：按高程区间裁剪点云，结果作为新点云（非破坏）。</summary>
    private async Task<bool> PcZClipAsync()
    {
        if (!PcNeedCloud("高程截断", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        var b = cur.Bounds;
        // 窗体组织忠实原 Segment/ZClipDialog：一句说明 + 最低/最高两行
        var form = new PcForm
        {
            Title = "高程截断", OkText = "确定", Width = 430,
            CliDescription = "保留高程在 [最低, 最高] 区间内的点，结果作为新点云加入场景（原点云保留）。",
        };
        form.Text("保留高程在 [最低, 最高] 区间内的点，结果作为新点云加入场景（原点云保留）。")
            .Rows(PcSourceRow(all, cur, 70))
            .Rows(
                PcRow.Num("zmin", "最低高程", b.minZ.ToString("F2", Inv), "m", null, null, 70, 90),
                PcRow.Num("zmax", "最高高程", b.maxZ.ToString("F2", Inv), "m", null, null, 70, 90));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "高程截断：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        double lo = v.D("zmin", b.minZ), hi = v.D("zmax", b.maxZ);
        if (hi < lo) (lo, hi) = (hi, lo);
        var kept = pc.Pts.Where(p => p.z >= lo && p.z <= hi).ToList();
        if (kept.Count == 0) { StatusMsg.Text = $"高程截断：区间 [{lo.ToString("0.##", Inv)}, {hi.ToString("0.##", Inv)}] 内无点"; return true; }

        BeginChange();
        var np = PcCommit(pc, pc.Name + "·Z截断", kept);
        RefreshScene();
        StatusMsg.Text = $"高程截断「{np.Name}」：保留 {kept.Count:N0}/{pc.PointCount:N0} 点"
                       + $"（Z {lo.ToString("0.##", Inv)} ~ {hi.ToString("0.##", Inv)} m）· 已设为当前点云";
        return true;
    }

    /// <summary>坐标转换：点云平移 + 绕 Z 旋转（仿射），结果作为新点云（非破坏）。</summary>
    private async Task<bool> PcTransformAsync()
    {
        if (!PcNeedCloud("坐标转换", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        var b = cur.Bounds;
        double cx = (b.minX + b.maxX) / 2, cy = (b.minY + b.maxY) / 2;
        // 窗体组织忠实原 Segment/TransformDialog：说明 → 旋转角 → 旋转中心(X/Y) → 平移(ΔX/ΔY/ΔZ)
        var form = new PcForm
        {
            Title = "坐标转换（平移 + 旋转）", OkText = "确定", Width = 460,
            CliDescription = "对点云绕中心旋转并平移，结果作为新点云加入场景（原点云保留）。",
        };
        form.Text("对点云绕中心旋转并平移，结果作为新点云加入场景（原点云保留）。仿射变换底层基础（后续可扩 CRS 投影）。")
            .Rows(PcSourceRow(all, cur, 70))
            .Rows(
                PcRow.Num("rot", "旋转角度", "0", "°", "（绕 Z 逆时针）", null, 70, 90),
                PcRow.Num("cx", "旋转中心 X", cx.ToString("F3", Inv), null, null, null, 70, 110),
                PcRow.Num("cy", "旋转中心 Y", cy.ToString("F3", Inv), null, null, null, 70, 110),
                PcRow.Num("dx", "平移 ΔX", "0", "m", null, null, 70, 90),
                PcRow.Num("dy", "平移 ΔY", "0", "m", null, null, 70, 90),
                PcRow.Num("dz", "平移 ΔZ", "0", "m", null, null, 70, 90));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "坐标转换：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        double dx = v.D("dx"), dy = v.D("dy"), dz = v.D("dz"), rot = v.D("rot");
        double rx = v.D("cx", cx), ry = v.D("cy", cy);
        double ca = Math.Cos(rot * Math.PI / 180.0), sa = Math.Sin(rot * Math.PI / 180.0);

        var outPts = new List<(double x, double y, double z)>(pc.PointCount);
        foreach (var p in pc.Pts)
        {
            double ox = p.x - rx, oy = p.y - ry;
            outPts.Add((rx + ox * ca - oy * sa + dx, ry + ox * sa + oy * ca + dy, p.z + dz));
        }

        BeginChange();
        var np = PcCommit(pc, pc.Name + "·转换", outPts, pc.HasColors ? new List<(float, float, float)>(pc.Colors!) : null);
        RefreshScene();
        PcZoomTo(np);
        StatusMsg.Text = $"坐标转换「{np.Name}」：平移 ({dx.ToString("0.###", Inv)}, {dy.ToString("0.###", Inv)}, {dz.ToString("0.###", Inv)})"
                       + $" · 绕 Z {rot.ToString("0.##", Inv)}° · {np.PointCount:N0} 点 · 已设为当前点云";
        return true;
    }

    // ═══════════════════ 4. 点云分析（逐点）═══════════════════

    /// <summary>
    /// 逐点坡度/坡向/曲率：kNN PCA 法向 → 逐点属性 → 着色成新点云 + 区间统计。
    /// 不建三角网，故垂直壁面与反坡（悬挑）同样有效 —— 2.5D TIN 连这类几何都表达不了。
    /// </summary>
    private async Task<bool> PcPointAttribAsync()
    {
        if (!PcNeedCloud("逐点属性分析", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        string[] attrs = { "坡度 (°)", "坡向 (罗盘°)", "曲率 (表面变异度)" };
        string[] maps = { "地形色（蓝→红）", "灰阶", "分歧色（蓝-白-红）" };
        // 窗体组织忠实原 Analysis/PointAttribDialog：说明 → 蓝框(为何不建面) → 源点云 → 分析项单选
        // → 色带 → 显示区间；切换分析项时区间跟着切到该项量程，并提示法向缓存是否命中。
        var form = new PcForm
        {
            Title = "逐点坡度 / 坡向 / 曲率分析", OkText = "开始分析", Width = 560,
            CliDescription = "由 kNN PCA 法向算逐点坡度/坡向/曲率并着色成新点云。",
            OnChanged = (key, vals) => key != "attr" ? null : vals.S("attr") == attrs[1]
                ? new Dictionary<string, string> { ["lo"] = "0", ["hi"] = "360" }
                : vals.S("attr") == attrs[2]
                    ? new Dictionary<string, string> { ["lo"] = "0", ["hi"] = "0" }
                    : new Dictionary<string, string> { ["lo"] = "0", ["hi"] = "90" },
        };
        form.Hints = vals =>
        {
            var src = PcResolve(all, vals.S("src")) ?? cur;
            string rangeHint = vals.S("attr") == attrs[1] ? "坡向默认 0–360°（罗盘方位，跨数据集可比）"
                             : vals.S("attr") == attrs[2] ? "曲率填 0/0 即按 P99 自动截断（少数尖点会把色带压平）"
                             : "坡度默认 0–90°（跨数据集可比）";
            string normalHint = src.HasNormals
                ? $"点云「{src.Name}」已缓存法向 → 秒出结果。"
                : $"点云「{src.Name}」尚未缓存法向 → 本次先算一遍 kNN PCA（大点云较慢），算完自动缓存供后续复用。";
            return new Dictionary<string, string> { ["range"] = rangeHint, ["normal"] = normalHint };
        };
        form.Text("由 kNN PCA 法向直接算逐点坡度 / 坡向 / 曲率，结果作为着色点云加入场景。")
            .Info("不建三角网，所以垂直壁面与反坡（悬挑）同样有效 —— 2.5D TIN 每个 (x,y) 只能有一个 Z，这类几何它连表达都做不到，更算不出坡度。")
            .Rows(PcSourceRow(all, cur, 96))
            .Rows(PcRow.Radios("attr", "分析项", attrs, attrs[0]))
            .Rows(
                PcRow.Combo("map", "色带", maps, maps[0], null, null, 96, 220),
                PcRow.Num("k", "邻域点数 k", "16", null, "PCA 拟合局部平面用的近邻数", null, 96, 70),
                PcRow.Num("lo", "显示区间下限", "0", null, null, null, 96, 70),
                PcRow.Num("hi", "显示区间上限", "90", null, null, null, 96, 70))
            .Hint("range", "坡度默认 0–90°（跨数据集可比）")
            .Small("上限 ≤ 下限时按分析项取默认区间（坡度 0–90 / 坡向 0–360 / 曲率按 P99 截断）")
            .Hint("normal", "");
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "逐点属性分析：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        int attr = Array.IndexOf(attrs, v.S("attr")); if (attr < 0) attr = 0;
        int map = Array.IndexOf(maps, v.S("map")); if (map < 0) map = 0;
        int k = Math.Max(3, v.I("k", 16));
        double lo = v.D("lo", 0), hi = v.D("hi", 90);
        var pts = PcPts(pc);

        StatusMsg.Text = $"逐点{PointCloudOps.AttrName(attr)}分析中（kNN PCA）…";
        var attribs = await PcAttribsAsync(pc, k);
        if (attribs.Count != pts.Count) { StatusMsg.Text = "逐点属性分析：点太少（需 ≥3 点）"; return true; }
        var values = PointCloudOps.AttributeValues(attribs, attr);
        if (hi <= lo) (lo, hi) = PointCloudOps.DefaultRange(attr, values);
        var cols = PointCloudOps.Colorize(values, map, lo, hi);

        double vmin = double.MaxValue, vmax = double.MinValue, sum = 0;
        foreach (double d in values) { if (d < vmin) vmin = d; if (d > vmax) vmax = d; sum += d; }

        BeginChange();
        var np = PcCommit(pc, $"{pc.Name}·{PointCloudOps.AttrName(attr)}", pts, cols);
        RefreshScene();
        string u = PointCloudOps.AttrUnit(attr);
        StatusMsg.Text = $"逐点{PointCloudOps.AttrName(attr)}分析「{np.Name}」：{pts.Count:N0} 点 · 实测 {vmin.ToString("0.##", Inv)}~{vmax.ToString("0.##", Inv)}{u}"
                       + $"（均 {(sum / Math.Max(values.Length, 1)).ToString("0.##", Inv)}{u}）· 色带区间 [{lo.ToString("0.##", Inv)}, {hi.ToString("0.##", Inv)}]{u}";
        return true;
    }

    /// <summary>逐点法向/坡度/坡向/曲率：优先用点云上缓存的法向，没有才算（并缓存）。</summary>
    private async Task<List<PointNormals.PointAttrib>> PcAttribsAsync(PointCloudEntity pc, int k)
    {
        var pts = PcPts(pc);
        var attribs = await Task.Run(() => PointNormals.ComputeFull(pts, k));
        if (attribs.Count == pts.Count)
        {
            var nrm = new List<(double x, double y, double z)>(attribs.Count);
            foreach (var a in attribs) nrm.Add(a.Normal);
            pc.Normals = nrm;   // 缓存：坡度/坡向/曲率三个分析共用同一次 kNN PCA
        }
        return attribs;
    }

    /// <summary>法向估计：kNN PCA 逐点法向 → 缓存在点云上，供坡度/坡向/曲率复用不再重算。</summary>
    private async Task<bool> PcEstimateNormalsAsync()
    {
        if (!PcNeedCloud("法向估计", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        // 原版无参数框：直接对当前点云按 k=16 估法向并缓存（已有缓存时先问要不要覆盖）
        var pc = cur;
        const int kNormals = 16;
        if (pc.HasNormals)
        {
            bool go = await BlockMsgBox.ConfirmAsync(this, "已有法向缓存",
                $"点云「{pc.Name}」已缓存过法向，重算会覆盖旧结果。\n\n继续吗？");
            if (!go) { StatusMsg.Text = "法向估计：已取消"; return true; }
        }
        StatusMsg.Text = "法向估计：逐点 kNN PCA 计算中…";
        var attribs = await PcAttribsAsync(pc, kNormals);
        if (attribs.Count != pc.PointCount) { StatusMsg.Text = "法向估计：点太少（需 ≥3 点）"; return true; }
        double sum = 0; foreach (var a in attribs) sum += a.SlopeDeg;
        StatusMsg.Text = $"法向估计「{pc.Name}」：{attribs.Count:N0} 个法向已缓存（平均坡度 {(sum / attribs.Count).ToString("0.#", Inv)}°）"
                       + " · 坡度/坡向/曲率分析可直接复用";
        return true;
    }

    /// <summary>
    /// 点云剖面：选 1 条多段线，在点云上开缓冲带取点成剖面。
    /// 不建面不插值 —— 没测到的站点留成缺口，而不是被 TIN 长边桥接成一段假地面。
    /// </summary>
    private async Task<bool> PcCloudProfileAsync()
    {
        if (!PcNeedCloud("点云剖面", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        var line = await PcSelectLineAsync("点云剖面");
        if (line == null) { StatusMsg.Text = "点云剖面：场景里没有可作剖面线的线（先画一条穿过点云的线）"; return true; }
        string[] aggs = { "最低点（地面）", "均值", "最高点（顶面）" };
        // 窗体组织忠实原 Profile/CloudProfileDialog：说明 → 源点云 → 缓冲半宽 / 站点间距 / 取值方式
        var form = new PcForm
        {
            Title = "点云直接剖面", OkText = "生成剖面", Width = 556,
            CliDescription = "在点云上开缓冲带取点成剖面（不建面、不插值；没测到的站点留缺口）。",
        };
        form.Text("【需先选中 1 条多段线】在点云上开缓冲带取点成剖面。")
            .Info("不建面不插值：没测到的站点留成缺口，而不是被 TIN 长边桥接成一段假地面。")
            .Rows(
                PcSourceRow(all, cur, 110),
                PcRow.Num("half", "缓冲半宽", "2.0", "m", "离剖面线多远的点参与取值", null, 110, 70),
                PcRow.Num("step", "站点间距", "1.0", "m", "沿线采样密度", null, 110, 70),
                PcRow.Combo("agg", "取值方式", aggs, aggs[0], "带植被/设备时取最低点更接近地面", null, 110, 160));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "点云剖面：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        double half = Math.Max(v.D("half", 2), 1e-3), step = Math.Max(v.D("step", 1), 1e-3);
        int agg = Math.Max(0, Array.IndexOf(aggs, v.S("agg")));
        var pts = PcPts(pc);

        StatusMsg.Text = "点云剖面：缓冲带取点中…";
        var prof = await Task.Run(() => PointCloudOps.CloudProfile(pts, line, half, step, agg));
        var good = prof.Where(s => s.Count > 0).ToList();
        if (good.Count < 2) { StatusMsg.Text = "点云剖面：缓冲带内点太少（把半宽调大试试）"; return true; }

        double zmin = good.Min(s => s.Z), zmax = good.Max(s => s.Z), len = prof[^1].Dist;
        PcDrawProfile(line, good.Select(s => (s.Dist, s.Z)).ToList(), zmin, zmax, len, "点云剖面", 0.35f, 0.85f, 0.95f);
        int gap = prof.Count - good.Count;
        // 剖面图窗（同原版 Profile/ProfileChartDialog）：缺口站点按 NaN 传进去，图上断开不连假地面
        PcProfileChartWindow.Popup(this, $"剖面图 · {pc.Name}",
            $"点云「{pc.Name}」缓冲半宽 {half.ToString("0.##", Inv)} m · 站点间距 {step.ToString("0.##", Inv)} m · 取值 {v.S("agg")}",
            prof.Select(x => new PcProfileChartWindow.Station(x.Dist, x.Count > 0 ? x.Z : double.NaN)).ToList());
        StatusMsg.Text = $"点云剖面「{pc.Name}」：{good.Count} 站有效"
                       + (gap > 0 ? $"（{gap} 站无点，按缺口留白不插值）" : "")
                       + $" · 里程 {len.ToString("0.#", Inv)}m · 高程 {zmin.ToString("0.##", Inv)}~{zmax.ToString("0.##", Inv)}m";
        return true;
    }

    /// <summary>选中的剖面线（直线/多段线）；没有返回 null。</summary>
    private List<(double x, double y)>? PcSelectedLine()
    {
        foreach (var e in _selected)
        {
            if (e is PolylineEntity pl && pl.Points.Count >= 2) return new List<(double x, double y)>(pl.Points);
            if (e is LineEntity l) return new List<(double x, double y)> { (l.X0, l.Y0), (l.X1, l.Y1) };
        }
        return null;
    }

    /// <summary>剖面曲线 + 图框画到剖面线下方（与既有「剖面分析」同一套呈现）。</summary>
    private void PcDrawProfile(IReadOnlyList<(double x, double y)> line, IReadOnlyList<(double dist, double z)> prof,
                               double zmin, double zmax, double len, string layer, float cr, float cg, float cb)
    {
        double baseX = line[0].x, baseY = line.Min(p => p.y) - (zmax - zmin) - 10;
        var curve = new PolylineEntity { Cr = cr, Cg = cg, Cb = cb, LayerName = layer };
        foreach (var (dist, z) in prof) curve.Points.Add((baseX + dist, baseY + (z - zmin)));
        BeginChange();
        _scene.Add(curve);
        double textH = Math.Max(Math.Max((zmax - zmin) * 0.06, len * 0.02), 1e-3);
        foreach (var fe in ProfilePlot.Frame(baseX, baseY, len, zmin, zmax, textH)) { fe.LayerName = layer; _scene.Add(fe); }
        RefreshScene();
    }

    /// <summary>
    /// 位移监测 C2C：两期点云逐点最近邻位移 + 统计与分布直方图。
    /// 区别于「两期算量」（格网求体积、垂直壁面量不出来）—— C2C 直接在 3D 量位移，陡壁鼓包/后退测得到。
    /// </summary>
    private async Task<bool> PcC2cAsync()
    {
        if (!PcNeedCloud("位移监测 C2C", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        if (all.Count < 2) { StatusMsg.Text = "位移监测 C2C：需要两期点云，请先把两期都加载进场景"; return true; }
        string[] maps = { "地形色（蓝→红）", "灰阶", "分歧色（蓝-白-红）" };
        var choices = PcChoices(all);
        // 窗体组织忠实原 Monitor/C2cDialog：两期下拉 → 说明 → 最大匹配距离 → 色带 → 按高差定正负
        var form = new PcForm
        {
            Title = "两期点云差异 C2C（边坡位移监测）", OkText = "开始比较", Width = 580,
            CliDescription = "两期点云逐点最近邻位移 + 统计与分布直方图。",
        };
        form.Rows(
                PcRow.Combo("a", "基准期 (前)", choices, choices[0], null, null, 110, 340),
                PcRow.Combo("b", "对比期 (后)", choices, choices[Math.Min(1, choices.Length - 1)], null, null, 110, 340))
            .Small("着色与统计针对「对比期」的点：正 = 相对基准隆起/前进，负 = 沉降/后退")
            .Rows(
                PcRow.Num("max", "最大匹配距离", "0", "m", "0 = 不限；超限的点计为未匹配，不会伪造出巨大位移", null, 110, 70),
                PcRow.Combo("map", "色带", maps, maps[2], "有正负时用分歧色，零位移居中显白", null, 110, 180),
                PcRow.Check("signed", "按高差定正负（隆起为正 / 沉降为负）；取消则只报绝对位移量", true));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "位移监测 C2C：已取消"; return true; }
        var ca = PcResolve(all, v.S("a"))!;
        var cb = PcResolve(all, v.S("b"))!;
        if (ReferenceEquals(ca, cb)) { StatusMsg.Text = "位移监测 C2C：两期不能是同一份点云"; return true; }
        double maxDist = Math.Max(0, v.D("max", 0));
        bool signed = v.B("signed", true);
        int map = Math.Max(0, Array.IndexOf(maps, v.S("map")));

        StatusMsg.Text = "位移监测 C2C：两期逐点最近邻计算中…";
        var ptsA = PcPts(ca); var ptsB = PcPts(cb);
        var res = await Task.Run(() => PointCloudOps.CloudToCloud(ptsA, ptsB, maxDist, signed));
        if (res.Matched == 0) { StatusMsg.Text = $"位移监测 C2C：没有点匹配上（最大匹配距离 {maxDist.ToString("0.##", Inv)}m 过小？）"; return true; }

        // 着色：未匹配点画成中性灰，不参与色带（否则色带会被"假位移"拉满）
        double lo = signed ? -Math.Max(Math.Abs(res.Min), Math.Abs(res.Max)) : res.Min;
        double hi = signed ? Math.Max(Math.Abs(res.Min), Math.Abs(res.Max)) : res.Max;
        var cols = new List<(float r, float g, float b)>(ptsB.Count);
        for (int i = 0; i < ptsB.Count; i++)
        {
            double d = res.Dist[i];
            if (double.IsNaN(d)) cols.Add((0.45f, 0.45f, 0.45f));   // 未匹配点：中性灰, 不参与色带
            else cols.Add(PointCloudOps.Ramp(map, hi - lo < 1e-12 ? 0.5 : (d - lo) / (hi - lo)));
        }

        BeginChange();
        var np = PcCommit(cb, $"{cb.Name}·C2C位移", ptsB, cols);
        RefreshScene();

        var rows = new List<(string, string)>
        {
            ("基准期 (前)", $"{ca.Name}（{ca.PointCount:N0} 点）"),
            ("对比期 (后)", $"{cb.Name}（{cb.PointCount:N0} 点）"),
            ("匹配 / 未匹配", $"{res.Matched:N0} / {res.Unmatched:N0}"),
            ("最大匹配距离", maxDist > 0 ? maxDist.ToString("0.##", Inv) + " m" : "不限"),
            ("位移 最小 / 最大", $"{res.Min.ToString("0.###", Inv)} / {res.Max.ToString("0.###", Inv)} m"),
            ("位移 均值 / 标准差", $"{res.Mean.ToString("0.###", Inv)} / {res.Std.ToString("0.###", Inv)} m"),
            ("|位移| P95", res.AbsP95.ToString("0.###", Inv) + " m"),
            ("正负口径", signed ? "按高差定正负（隆起为正 / 沉降为负）" : "只报绝对位移量"),
        };
        PcResultWindow.Popup(this, "位移监测 C2C · 统计", rows, res.Histogram, res.HistLo, res.HistHi,
                             "位移分布直方图（20 桶）", "着色点云已入场景；未匹配点显中性灰，不参与色带。");
        StatusMsg.Text = $"位移监测 C2C「{np.Name}」：匹配 {res.Matched:N0}/{ptsB.Count:N0} · 均值 {res.Mean.ToString("0.###", Inv)}m"
                       + $" · |位移|P95 {res.AbsP95.ToString("0.###", Inv)}m · 标准差 {res.Std.ToString("0.###", Inv)}m";
        return true;
    }

    /// <summary>质量统计：点数 / 密度 / 平均点间距 / 包围盒 / 高程分布。原版无参数框，直接对当前点云出报告。</summary>
    private async Task<bool> PcQualityStatsAsync()
    {
        if (!PcNeedCloud("质量统计", out var cur, out _)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        var pc = cur;
        var pts = PcPts(pc);
        var s = await Task.Run(() => PointCloudStats.Compute(pts));
        var (hist, zlo, zhi) = PointCloudOps.ZHistogram(pts, 20);
        double spacing = PointCloudOps.MeanSpacing(s.Count, s.AreaXY);

        var rows = new List<(string, string)>
        {
            ("点云", pc.Name + (pc.Source != null ? $"（{System.IO.Path.GetFileName(pc.Source)}）" : "")),
            ("点数", s.Count.ToString("N0", Inv)),
            ("X 范围", $"{s.MinX.ToString("0.##", Inv)} ~ {s.MaxX.ToString("0.##", Inv)} m（跨度 {(s.MaxX - s.MinX).ToString("0.##", Inv)}）"),
            ("Y 范围", $"{s.MinY.ToString("0.##", Inv)} ~ {s.MaxY.ToString("0.##", Inv)} m（跨度 {(s.MaxY - s.MinY).ToString("0.##", Inv)}）"),
            ("Z 范围", $"{s.MinZ.ToString("0.##", Inv)} ~ {s.MaxZ.ToString("0.##", Inv)} m（跨度 {(s.MaxZ - s.MinZ).ToString("0.##", Inv)}）"),
            ("XY 投影面积", s.AreaXY.ToString("N0", Inv) + " m²"),
            ("点密度", s.DensityXY.ToString("0.###", Inv) + " 点/m²"),
            ("平均点间距", spacing.ToString("0.###", Inv) + " m"),
            ("高程 均值 / 标准差", $"{s.MeanZ.ToString("0.##", Inv)} / {s.StdZ.ToString("0.##", Inv)} m"),
            ("真实色 (RGB)", pc.HasRgb ? "有" : "无"),
            ("法向缓存", pc.HasNormals ? "有" : "无（可用「法向估计」预生成）"),
        };
        var h = new double[hist.Length];
        for (int i = 0; i < hist.Length; i++) h[i] = hist[i];
        PcResultWindow.Popup(this, $"点云质量统计 · {pc.Name}", rows, h, zlo, zhi, "高程分布直方图（20 桶）");
        StatusMsg.Text = $"质量统计「{pc.Name}」：{s.Count:N0} 点 · 密度 {s.DensityXY.ToString("0.###", Inv)} 点/m²"
                       + $" · 平均间距 {spacing.ToString("0.###", Inv)}m · Z {s.MinZ.ToString("0.##", Inv)}~{s.MaxZ.ToString("0.##", Inv)}m";
        return true;
    }

    /// <summary>
    /// 坡顶底线提取：在点云上栅格化 DEM → 断棱线检测 → 坡顶线/坡底线连成多段线入图层。
    /// 短孤线按最短线长过滤（原版同款：不然一地碎线没法用）。
    /// </summary>
    private async Task<bool> PcSlopeLinesAsync()
    {
        if (!PcNeedCloud("坡顶底线提取", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        // 窗体组织忠实原 SlopeLine/SlopeLineDialog：说明 → DEM 网格 / 挡墙最小高 / 最小台阶高 / 陡坡阈值 / 最短线长
        var form = new PcForm
        {
            Title = "坡顶/坡底线提取（点云·挡墙法）", OkText = "确定", Width = 500,
            CliDescription = "在点云上栅格化 DEM，自动提取坡顶/坡底断棱线为多段线（图层 点云_坡顶线 / 点云_坡底线）。",
        };
        form.Text("直接在已加载点云上栅格化 DEM：按台阶边的坡度突变提坡顶线 / 坡面下沿提坡底线，并按最短线长过滤短孤线，"
                + "入库为多段线（图层 点云_坡顶线 / 点云_坡底线）。")
            .Small("说明：本实现按 DEM 断棱线检测，未提供原版挡墙法的「挡墙最小高 / 最小台阶高」两个旋钮 —— 界面上不摆不起作用的参数。")
            .Rows(
                PcSourceRow(all, cur, 120),
                PcRow.Num("cell", "DEM 网格尺寸", PcAutoCell(cur, 1).ToString("0.##", Inv), "m", "（越大越快越粗，整场建议 1~2；默认按点密度取）", null, 120, 70),
                PcRow.Num("slope", "陡坡阈值", "24", "°", "（≥此值判为坡面，用于坡底）", null, 120, 70),
                PcRow.Num("minlen", "最短线长", "60", "m", "（主要过滤短孤线）", null, 120, 70));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "坡顶底线提取：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        double cell = Math.Max(v.D("cell", 1), 1e-3), thr = v.D("slope", 24), minLen = Math.Max(v.D("minlen", 60), 0);

        StatusMsg.Text = "坡顶底线提取：栅格化 DEM + 断棱线检测…";
        var pts = PcPts(pc);
        var (crest, toe, gridPts) = await Task.Run(() =>
        {
            var r = PointCloudOps.Rasterize(pts, cell, 0);           // 取每格最低点 = 地面
            var gp = PointCloudOps.RasterPoints(r);
            var xy = gp.Select(p => (p.x, p.y)).ToList();
            var tris = Delaunay.Triangulate(xy);
            if (tris.Count == 0) return (new List<CrestToe.Edge>(), new List<CrestToe.Edge>(), gp);
            var (c, t) = CrestToe.Extract(gp, tris, thr);
            return (c, t, gp);
        });
        if (gridPts.Count < 3) { StatusMsg.Text = "坡顶底线提取：栅格点太少（把 DEM 格网调小）"; return true; }
        if (crest.Count == 0 && toe.Count == 0) { StatusMsg.Text = $"坡顶底线提取：未检出断棱线（陡坡阈值 {thr.ToString("0.#", Inv)}° 可能偏大）"; return true; }

        BeginChange();
        int nc = PcAddEdgeLines(crest, minLen, "点云_坡顶线", 0.95f, 0.55f, 0.2f);
        int nt = PcAddEdgeLines(toe, minLen, "点云_坡底线", 0.2f, 0.7f, 0.9f);
        RefreshScene();
        StatusMsg.Text = $"坡顶底线提取「{pc.Name}」：坡顶线 {nc} 条(橙) / 坡底线 {nt} 条(青)"
                       + $" · DEM 网格 {cell.ToString("0.##", Inv)}m · 陡坡阈值 {thr.ToString("0.#", Inv)}°"
                       + $" · 最短线长 {minLen.ToString("0.#", Inv)}m";
        return true;
    }

    /// <summary>断棱线段 → 连成多段线 → 按最短线长过滤 → 入图层。返回入库条数。</summary>
    private int PcAddEdgeLines(IReadOnlyList<CrestToe.Edge> edges, double minLen, string layer, float cr, float cg, float cb)
    {
        if (edges.Count == 0) return 0;
        var segs = edges.Select(e => (IReadOnlyList<(double x, double y)>)new[] { (e.X0, e.Y0), (e.X1, e.Y1) }).ToList();
        var chains = PolylineJoin.Join(segs, 1e-6);
        int n = 0;
        foreach (var ch in chains)
        {
            double len = 0;
            for (int i = 0; i + 1 < ch.Count; i++)
                len += Math.Sqrt(Math.Pow(ch[i + 1].x - ch[i].x, 2) + Math.Pow(ch[i + 1].y - ch[i].y, 2));
            if (len < minLen || ch.Count < 2) continue;
            var pl = new PolylineEntity { Cr = cr, Cg = cg, Cb = cb, LayerName = layer };
            pl.Points.AddRange(ch);
            _scene.Add(pl);
            n++;
        }
        return n;
    }

    // ═══════════════════ 5. 三角网重建 ═══════════════════

    /// <summary>2.5D TIN：把当前点云转成 2.5D 三角网入场景（可先抽稀、可剔长边不桥接大空洞）。</summary>
    private async Task<bool> PcBuildTinAsync()
    {
        // 没有点云时：选了点/线就交回建模组的「创建三角网」(那是另一条真实通路)；否则按原版报"请先加载点云"。
        if (PcCurrentCloud == null)
        {
            bool hasMdlInput = _selected.OfType<PointEntity>().Any() || _selected.OfType<PolylineEntity>().Any() || _selected.OfType<LineEntity>().Any();
            if (hasMdlInput) return false;
            StatusMsg.Text = "2.5D TIN：场景中没有已加载的点云，请先「加载点云」。";
            return true;
        }
        if (!PcNeedCloud("2.5D TIN", out var cur, out var all)) return true;
        // 窗体组织忠实原 Views/TinOptionsDialog：标题行 → 「三角网参数」组 → 预设按钮 → 折叠的「高级（数据源）」
        var form = new PcForm
        {
            Title = "生成 2.5D 三角网",
            OkText = "生成",
            Width = 520,
            CliDescription = "把当前点云转成 2.5D 三角网（结果作为三角网实体入场景，可再着色/算量/出等高线）。",
            Validate = v =>
            {
                if (v.D("voxel", -1) < 0) return "采样精度必须 ≥ 0";
                if (v.D("gap", -1) < 0) return "空洞桥接必须 ≥ 0";
                if (v.D("maxpts", -1) < 0) return "最大输入点数必须是非负整数";
                return null;
            },
        };
        form.Head("2.5D 三角剖分")
            .Group("三角网参数",
                PcRow.Num("voxel", "采样精度", "3.0", "m", "越小越细 · 0=全密度",
                          "体素下采样尺寸：越小三角网越细。建议 1–5m；0 = 全密度（密集点云慎用，会很慢很占内存）"),
                PcRow.Num("gap", "空洞桥接", "0", "m", "0=全填充(完整) · >0=剔超长边",
                          "0 = 不剔长边、凸包全填充（默认，表面最连续完整，推荐）；" + "大于 0 = 三角面最长边的绝对上限（米），"
                          + "跨度超过此值的三角面被剔除——用于裁掉凹边界/蛛网三角，但点云内部的大数据空缺也会被剜成洞。该值与采样精度无关。"),
                PcRow.Check("adaptive", "保留坡面细节（自适应抽稀：平地稀疏 / 坡面加密，省三角形又留台阶线）", true))
            .Presets("预设：",
                PcPreset.Of("默认 (3m)", "平衡：采样 3m，凸包全填充(不破洞)，自适应", ("voxel", "3"), ("gap", "0"), ("adaptive", "是")),
                PcPreset.Of("高密度 (1m)", "精细：采样 1m，凸包全填充(不破洞)，自适应", ("voxel", "1"), ("gap", "0"), ("adaptive", "是")),
                PcPreset.Of("快速预览 (8m)", "粗：采样 8m，凸包全填充，速度最快", ("voxel", "8"), ("gap", "0"), ("adaptive", "否")),
                PcPreset.Of("全密度", "不下采样、不剔长边：仅小数据集", ("voxel", "0"), ("gap", "0"), ("adaptive", "否")))
            .Advanced("高级（数据源）",
                PcSourceRow(all, cur),
                PcRow.Num("maxpts", "最大输入点数", "0", null, "0 = 全部点；>0 时按等间隔下采样",
                          "一般无需设置，采样精度已经在控制点数"));

        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "2.5D TIN：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        double voxel = Math.Max(v.D("voxel", 3), 0), gap = Math.Max(v.D("gap", 0), 0);
        long maxPts = (long)Math.Max(v.D("maxpts", 0), 0);
        bool adaptive = v.B("adaptive", true);
        const bool shade = true;   // 原版建完即按高程分带上色(ApplyDefaultTinShading), 不给开关

        StatusMsg.Text = "2.5D TIN：剖分中…";
        var src = PcPts(pc);
        var (verts, tris) = await Task.Run(() =>
        {
            var raw = src;
            if (maxPts > 0 && raw.Count > maxPts)   // 「最大输入点数」：等间隔下采样(同原版语义)
            {
                int stride = (int)Math.Ceiling(raw.Count / (double)maxPts);
                var cut = new List<(double x, double y, double z)>((int)maxPts + 1);
                for (int i = 0; i < raw.Count; i += stride) cut.Add(raw[i]);
                raw = cut;
            }
            // 自适应：平地稀疏 / 坡面加密（原版的 FineVoxelRatio=0.25、FlatZRange=1m 是内部经验常量，不给用户调）
            var input = voxel <= 0 ? raw : (adaptive ? PointThin.ThinAdaptive(raw, voxel) : PointThin.Thin(raw, voxel));
            var xy = input.Select(p => (p.x, p.y)).ToList();
            var t = Delaunay.Triangulate(xy);
            if (gap > 0)   // 剔长边：不把水面/暗煤/陡壁遮挡这类数据空洞桥接成假地面
            {
                double g2 = gap * gap;
                bool Long(int i, int j)
                {
                    double dx = input[i].x - input[j].x, dy = input[i].y - input[j].y;
                    return dx * dx + dy * dy > g2;
                }
                t = t.Where(f => !Long(f.a, f.b) && !Long(f.b, f.c) && !Long(f.c, f.a)).ToList();
            }
            return (input, t);
        });
        if (tris.Count == 0) { StatusMsg.Text = "2.5D TIN：剖分为空（点太少/共线，或空洞桥接上限过小）"; return true; }

        BeginChange();
        var mesh = new MeshEntity(pc.Name + "·TIN", verts, tris);
        AssignLayer(mesh);
        // 源点云带真实色、且这次没抽稀(顶点与源点一一对应)时，把真实色带到三角网顶点上 ——
        // 「三角网着色 · 点云真实色」那一档要的就是它。显示色仍按高程分带(下面 shade)。
        if (pc.HasRgb && verts.Count == src.Count && voxel <= 0 && maxPts <= 0)
            mesh.RgbColors = new List<(float r, float g, float b)>(pc.RgbColors!);
        if (shade)
        {
            var bb = mesh.Bounds; double zr = bb.maxZ - bb.minZ;
            mesh.VertColors = verts.Select(p => MeshEntity.TerrainRamp(zr > 1e-9 ? (p.z - bb.minZ) / zr : 0.5)).ToList();
            mesh.Invalidate();
        }
        _scene.Add(mesh);
        _selected.Clear(); _selected.Add(mesh); HighlightSelection();
        RefreshScene();
        StatusMsg.Text = $"2.5D TIN「{mesh.Name}」：{verts.Count:N0} 顶点 / {tris.Count:N0} 三角"
                       + (voxel > 0 ? $"（采样精度 {voxel.ToString("0.##", Inv)}m{(adaptive ? "·保坡面细节" : "")}，源 {src.Count:N0} 点）" : $"（全密度 {src.Count:N0} 点）")
                       + (gap > 0 ? $" · 空洞桥接 {gap.ToString("0.##", Inv)}m" : " · 凸包全填充")
                       + (maxPts > 0 ? $" · 限输入 {maxPts:N0} 点" : "") + " · 已选中";
        return true;
    }

    /// <summary>两期点云算量：两期点云 → 同格网差值 → 挖方/填方/净值 + 分标高带/连通块明细。</summary>
    private async Task<bool> PcTwoEpochVolumeAsync()
    {
        if (!PcNeedCloud("两期点云算量", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        if (all.Count < 2) { StatusMsg.Text = "两期点云算量：需要两期点云，请先把两期都加载进场景"; return true; }
        var choices = PcChoices(all);
        var ca0 = all[0];
        var cb0 = all[Math.Min(1, all.Count - 1)];
        string[] methods = { "直接栅格（不建 TIN，推荐）", "常规 TIN 建面" };
        string[] aggs = { "最低点（取地面，抗噪）", "均值" };
        string[] scopes = { "整体（两期重叠区）", "仅选定区域内（用选中的闭合多段线）" };
        double defCell = Math.Max(PcAutoCell(ca0, 2), PcAutoCell(cb0, 2));

        // 窗体组织忠实原 Volume/TwoEpochVolumeDialog：两期来源 → 建面方式 → 格网与口径 → 计算范围 → 说明
        var form = new PcForm
        {
            Title = "两期点云算量", OkText = "确定", Width = 580,
            CliDescription = "两期点云算量：格网法生成挖/填独立水密封闭体(挖红/填蓝)并报体积。",
        };
        form.Group("两期来源",
                PcRow.Combo("a", "第一期 (原始/较早)", choices, choices[0], null, null, 130, 340),
                PcRow.Combo("b", "第二期 (现状/较新)", choices, choices[Math.Min(1, choices.Length - 1)], null, null, 130, 340))
            .Rows(PcRow.Radios("method", "建面方式", methods, methods[0]))
            .Group("格网与口径",
                PcRow.Num("cell", "体积格网", defCell.ToString("0.##", Inv), "m", "算体积用的细格；默认按点密度取", null, 130, 80),
                PcRow.Num("render", "渲染格网", Math.Max(defCell * 3, 6).ToString("0.##", Inv), "m", "生成封闭体的粗格（≥体积格网，降三角数）", null, 130, 80),
                PcRow.Num("mindz", "最小高差", "1.0", "m", "小于此的高差视为未变化", null, 130, 80),
                PcRow.Num("bench", "最小台阶高", "3.0", "m", "块内最大高差不足即整块丢弃", null, 130, 80),
                PcRow.Num("open", "去噪半径", "1", "格", "形态学开运算；0 = 不去噪", null, 130, 80),
                PcRow.Num("patch", "最小图斑面积", "500", "m²", "小于此的连通块整块丢弃（杀零散碎斑）", null, 130, 80),
                PcRow.Combo("agg", "点云→格网取高程", aggs, aggs[0], null, null, 130, 200),
                PcRow.Num("fillr", "有界小洞填充", "1", "格", "补被包住的小洞；0 = 不补。大无数据区始终排除", null, 130, 80))
            .Rows(PcRow.Radios("scope", "计算范围", scopes, scopes[0]))
            .Small("直接栅格（不建 TIN）：只统计两期都有数据的格，大无数据区不虚构地形。"
                 + "挖方 = 第二期低于第一期（红），填方 = 第二期高于第一期（蓝），各连通块生成独立水密封闭体入场景。");
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "两期点云算量：已取消"; return true; }
        var ca = PcResolve(all, v.S("a"))!;
        var cb = PcResolve(all, v.S("b"))!;
        if (ReferenceEquals(ca, cb)) { StatusMsg.Text = "两期点云算量：两期不能是同一份点云"; return true; }

        bool viaTin = v.S("method") == methods[1];
        double cell = Math.Max(v.D("cell", 2), 1e-3);
        double render = Math.Max(v.D("render", 6), cell);
        double minDz = Math.Max(v.D("mindz", 1), 0);
        double benchH = Math.Max(v.D("bench", 3), 0);
        int openR = Math.Max(v.I("open", 1), 0);
        double minPatch = Math.Max(v.D("patch", 500), 0);
        int agg = Math.Max(0, Array.IndexOf(aggs, v.S("agg")));
        int fillR = Math.Max(v.I("fillr", 1), 0);
        bool clip = v.S("scope") == scopes[1];

        var ptsA = PcPts(ca); var ptsB = PcPts(cb);
        string scopeText = "整体（两期重叠区）";
        if (clip)
        {
            var rings = await PcSelectAsync<PolylineEntity>("两期点云算量", "闭合多段线（计算范围）", p => p.Closed && p.Points.Count >= 3);
            if (rings.Count == 0) { StatusMsg.Text = "两期点云算量：场景里没有闭合多段线可作计算范围，请改用「整体」或先画一条"; return true; }
            var poly = new List<(double x, double y)>(rings[0].Points);
            ptsA = PointCloudCrop.ByPolygon(ptsA, poly, true);
            ptsB = PointCloudCrop.ByPolygon(ptsB, poly, true);
            scopeText = $"仅选定区域内（{rings[0].Points.Count} 点闭合线）";
            if (ptsA.Count == 0 || ptsB.Count == 0)
            { StatusMsg.Text = "两期点云算量：所选区域内某一期没有点（区域与点云坐标是否一致？）"; return true; }
        }

        StatusMsg.Text = $"两期点云算量（{(viaTin ? "常规 TIN" : "直接栅格")}）：后台计算中…";
        var job = await Task.Run(() =>
        {
            // ① 两期各自成面：直接栅格 = 格网面(无数据格留孔)；常规 TIN = 抽稀后 Delaunay
            var (va, ta, fa) = PcEpochSurface(ptsA, cell, agg, fillR, viaTin);
            var (vb, tb, fb) = PcEpochSurface(ptsB, cell, agg, fillR, viaTin);
            if (ta.Length == 0 || tb.Length == 0)
                return (res: (CutFillSolids.Result?)null, holesA: fa, holesB: fb, err: "两期至少有一期建不出面（点太少 / 格网过细）");
            // ② 与「两期三角网算量」共用同一套：细格差值 → 最小高差 → 开运算去噪 → 连通块 → 最小台阶高 → 逐块水密封闭体
            var o = new CutFillSolids.Options { GridCell = cell, RenderCell = render, MinDz = minDz, OpenRadius = openR, MinBenchH = benchH };
            var r = CutFillSolids.Compute(va, ta, vb, tb, o);
            return (res: r, holesA: fa, holesB: fb, err: r.Error);
        });
        if (job.res == null || !string.IsNullOrEmpty(job.err))
        { StatusMsg.Text = $"两期点云算量：{(string.IsNullOrEmpty(job.err) ? "计算失败" : job.err)}"; return true; }
        var res = job.res;

        // ③ 最小图斑面积：小块整块丢弃，扣掉的量单独回报 —— 绝不让"体积少了"这件事无声发生（同原版口径过滤）
        var kept = new List<CutFillSolids.Body>();
        double dropCut = 0, dropFill = 0; int dropN = 0;
        foreach (var b in res.Bodies)
        {
            if (minPatch > 0 && b.AreaM2 < minPatch)
            {
                dropN++;
                if (b.IsFill) dropFill += b.VolumeM3; else dropCut += b.VolumeM3;
                continue;
            }
            kept.Add(b);
        }
        double fillM3 = 0, cutM3 = 0;
        foreach (var b in kept) { if (b.IsFill) fillM3 += b.VolumeM3; else cutM3 += b.VolumeM3; }

        // ④ 挖/填封闭体入场景：填方蓝(0,120,255) / 挖方红(255,60,0)，图层「填方」/「挖方」（同原版配色）
        BeginChange();
        var made = new List<SceneEntity>();
        int nFill = 0, nCut = 0;
        foreach (var b in kept)
        {
            if (b.Tris.Count == 0) continue;
            var me = new MeshEntity((b.IsFill ? "填方体" : "挖方体") + b.Index, b.Verts, b.Tris)
            {
                LayerName = b.IsFill ? "填方" : "挖方",
                Cr = b.IsFill ? 0f : 1f, Cg = b.IsFill ? 120 / 255f : 60 / 255f, Cb = b.IsFill ? 1f : 0f,
            };
            _scene.Add(me); made.Add(me);
            if (b.IsFill) nFill++; else nCut++;
        }
        RefreshScene();
        if (made.Count > 0) { _selected.Clear(); _selected.AddRange(made); HighlightSelection(); }

        // ⑤ 结果窗：总量 + 口径过滤扣除 + 逐块明细（同原版 VolumeResultWindow 的角色）
        var raw = res.Raw;
        var rows = new List<(string, string)>
        {
            ("第一期 → 第二期", $"{ca.Name} → {cb.Name}"),
            ("建面方式", viaTin ? "常规 TIN 建面" : "点云直接栅格"),
            ("计算范围", scopeText),
            ("挖方 / 填方", $"{cutM3.ToString("N1", Inv)} / {fillM3.ToString("N1", Inv)} m³"),
            ("净值（填 − 挖）", (fillM3 - cutM3).ToString("N1", Inv) + " m³"),
            ("封闭体", $"{made.Count} 个（填 {nFill} / 挖 {nCut}）· 已入图层「填方」/「挖方」"),
            ("重叠区", $"{raw.OverlapAreaM2.ToString("N0", Inv)} m²（{raw.ValidCells:N0} 格 · 体积格网 {raw.CellSize.ToString("0.##", Inv)}m）"),
            ("高差范围", $"{raw.MinDz.ToString("0.##", Inv)} ~ {raw.MaxDz.ToString("0.##", Inv)} m"),
            ("原始格级（未过滤）", $"挖 {raw.CutM3.ToString("N1", Inv)} / 填 {raw.FillM3.ToString("N1", Inv)} m³"),
            ("口径过滤扣除", $"去噪去格 {res.DroppedNoise} · 台阶过滤丢块 {res.DroppedLowBench}"
                           + (dropN > 0 ? $" · 碎斑丢弃 {dropN} 块（挖 {dropCut.ToString("N1", Inv)} + 填 {dropFill.ToString("N1", Inv)} m³）" : "")),
            ("小洞填充", $"第一期 {job.holesA} 格 / 第二期 {job.holesB} 格"),
        };
        foreach (var b in kept.OrderByDescending(x => x.VolumeM3).Take(20))
            rows.Add(($"{(b.IsFill ? "填方体" : "挖方体")}{b.Index}",
                      $"{b.VolumeM3.ToString("N1", Inv)} m³ · 投影 {b.AreaM2.ToString("N0", Inv)} m² · 最大高差 {b.MaxDz.ToString("0.##", Inv)} m"));
        if (kept.Count > 20) rows.Add(("…", $"另有 {kept.Count - 20} 个封闭体未列出"));
        PcResultWindow.Popup(this, "两期点云算量 · 结果", rows, null, 0, 0, null,
                             "挖方（红）= 第二期低于第一期；填方（蓝）= 第二期高于第一期。各连通块已生成独立水密封闭体入场景。");

        StatusMsg.Text = $"两期点云算量（{ca.Name} → {cb.Name}，{(viaTin ? "常规TIN" : "直接栅格")}，体积格网 {raw.CellSize.ToString("0.##", Inv)}m）："
                       + $"挖方 {cutM3.ToString("N1", Inv)} m³ · 填方 {fillM3.ToString("N1", Inv)} m³ · 净 {(fillM3 - cutM3).ToString("N1", Inv)} m³"
                       + $" · {made.Count} 个封闭体入场景（填 {nFill} 蓝 / 挖 {nCut} 红）"
                       + (dropN > 0 ? $" · 碎斑丢弃 {dropN} 块" : "");
        return true;
    }

    /// <summary>
    /// 一期点云 → 面（供两期算量求交）。直接栅格：每格取地面/均值高程 + 补有界小洞 → 格网面；
    /// 常规 TIN：按格网尺寸抽稀后 Delaunay。返回 (顶点扁平, 三角扁平, 补洞格数)。
    /// </summary>
    private static (double[] verts, int[] tris, int holes) PcEpochSurface(
        List<(double x, double y, double z)> pts, double cell, int agg, int fillRadius, bool viaTin)
    {
        if (viaTin)
        {
            var input = cell > 0 ? PointThin.Thin(pts, cell) : pts;
            var xy = input.Select(p => (p.x, p.y)).ToList();
            var tris = Delaunay.Triangulate(xy);
            var vs = new double[input.Count * 3];
            for (int i = 0; i < input.Count; i++) { vs[i * 3] = input[i].x; vs[i * 3 + 1] = input[i].y; vs[i * 3 + 2] = input[i].z; }
            var ts = new int[tris.Count * 3];
            for (int i = 0; i < tris.Count; i++) { ts[i * 3] = tris[i].a; ts[i * 3 + 1] = tris[i].b; ts[i * 3 + 2] = tris[i].c; }
            return (vs, ts, 0);
        }
        var r = PointCloudOps.Rasterize(pts, cell, agg == 1 ? 1 : 0);
        int holes = PointCloudOps.FillSmallHoles(r, fillRadius);
        var (v, t) = PointCloudOps.RasterToMesh(r);
        return (v, t, holes);
    }

    /// <summary>
    /// 圈范围算量：选中闭合多段线圈定范围 → 填向下深度 H → 在当前点云上直接栅格算「顶部 H 米」方量
    /// （底面 = 圈内最高地表 − H，免建 TIN）。只统计圈内落到点的格，大无数据区不虚构地形。
    /// </summary>
    private async Task<bool> PcRangeVolumeAsync()
    {
        if (!PcNeedCloud("圈范围算量", out var cur, out var all)) return true;   // 无点云 → 报"请先加载点云"(同原版)，不落到选 CSV
        var bnds = await PcSelectAsync<PolylineEntity>("圈范围算量", "闭合多段线（范围边界）", p => p.Closed && p.Points.Count >= 3);
        if (bnds.Count == 0) { StatusMsg.Text = "圈范围算量：场景里没有闭合多段线可作范围边界（先画一条圈定范围）"; return true; }
        var bnd = bnds[0];
        // 窗体组织忠实原 Volume/RangeVolumeDialog：说明 → 向下深度 H → 格网尺寸 → 取高程方式 → 结论说明
        string[] aggs = { "最高点（顶面，默认）", "均值" };
        var form = new PcForm
        {
            Title = "圈范围算量", OkText = "确定", Width = 500,
            CliDescription = "在当前点云上直接栅格算「顶部 H 米」方量：底面 = 圈内最高地表 − H。",
            Validate = fv => fv.D("h", 0) <= 0 ? "向下深度 H 必须 > 0" : null,
        };
        form.Text("范围已圈定。填「向下深度 H」——底面 = 圈内最高地表往下 H 米；只算圈内、且高于底面的那一层方量。")
            .Rows(
                PcSourceRow(all, cur, 150),
                PcRow.Num("h", "向下深度 H（m，> 0）", "10.0", "m", null, null, 150, 80),
                PcRow.Num("cell", "格网尺寸（m，越小越精）", PcAutoCell(cur, 2).ToString("0.##", Inv), "m", "默认按点密度取", null, 150, 80),
                PcRow.Combo("agg", "点云→格网取高程方式", aggs, aggs[0], null, null, 150, 170))
            .Small("说明：只统计圈内落到点的格（cell² 计面积），大无数据区不虚构地形；结果只读回显，不改点云、不入图。");
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "圈范围算量：已取消"; return true; }
        var pc = PcResolve(all, v.S("src")) ?? cur;
        int topAgg = v.S("agg") == aggs[1] ? 1 : 2;
        double h = v.D("h", 10), cell = Math.Max(v.D("cell", 2), 1e-3);
        if (h <= 0) { StatusMsg.Text = "圈范围算量：向下深度 H 必须 > 0"; return true; }

        var poly = new List<(double x, double y)>(bnd.Points);
        var pts = PcPts(pc);
        StatusMsg.Text = "圈范围算量：圈内取点 + 栅格化…";
        var r = await Task.Run(() =>
        {
            var inside = PointCloudCrop.ByPolygon(pts, poly, true);
            if (inside.Count == 0) return (vol: 0.0, cells: 0, area: 0.0, top: 0.0, baseZ: 0.0, n: 0);
            // 顶面取每格最高点：算「顶部 H 米」量的是地表以上那一层, 取最低点会把台阶顶算矮
            var ras = PointCloudOps.Rasterize(inside, cell, topAgg);   // 顶面默认取每格最高点
            double top = double.MinValue;
            for (int i = 0; i < ras.Z.Length; i++) if (ras.Has[i] && ras.Z[i] > top) top = ras.Z[i];
            double bz = top - h, vol = 0; int cells = 0;
            double a = cell * cell;
            for (int i = 0; i < ras.Z.Length; i++)
            {
                if (!ras.Has[i]) continue;
                double dz = ras.Z[i] - bz;
                if (dz > 0) { vol += dz * a; cells++; }
            }
            return (vol: vol, cells: cells, area: cells * a, top: top, baseZ: bz, n: inside.Count);
        });
        if (r.n == 0) { StatusMsg.Text = "圈范围算量：圈内没有点"; return true; }

        double polyArea = TerrainAnalysis.PolygonAreaXY(poly);
        StatusMsg.Text = $"圈范围算量「{pc.Name}」：方量 {r.vol.ToString("N0", Inv)} m³"
                       + $"（顶面最高 {r.top.ToString("0.##", Inv)}m → 底面 {r.baseZ.ToString("0.##", Inv)}m，深度 {h.ToString("0.##", Inv)}m）"
                       + $" · 高于底面的格 {r.cells:N0}（{r.area.ToString("N0", Inv)} m²，圈面积 {polyArea.ToString("N0", Inv)} m²）· 圈内 {r.n:N0} 点"
                       + $" · 格网 {cell.ToString("0.##", Inv)}m";
        return true;
    }

    /// <summary>三角网着色：对选中三角网按 素色/高程/坡度/坡向/等高线 逐顶点上色（随工程存档）。</summary>
    private async Task<bool> PcTinShadingAsync()
    {
        var meshes = await PcSelectAsync<MeshEntity>("三角网着色", "三角网");
        if (meshes.Count == 0) { StatusMsg.Text = "三角网着色：场景里没有三角网（先用「2.5D TIN」把点云建成面）"; return true; }
        // 窗体组织忠实原 Shading/TinShadingDialog：说明 → 模式单选(素色/真实色/高程/坡度/坡向/等高线/清除)
        // → 色带 → 等高线间距。正射影像贴图那一档需要纹理管线，本版渲染器无纹理，故不摆这个选项。
        string[] modes =
        {
            "原始三角网素色（平滑单色）",
            "平面（单色·显示三角面）",
            "点云真实色（按 TIN 顶点 RGB）",
            "高程（色带）",
            "坡度（蓝→绿→黄→红）",
            "坡向（方位色环）",
            "等高线",
            "清除选中面的独立着色（退回三角网默认显示）",
        };
        string[] maps = { "地形（绿→黄→棕→白）", "地形色（蓝→红）", "灰阶", "分歧色（蓝-白-红）" };
        var form = new PcForm
        {
            Title = "三角网着色", OkText = "应用", Width = 470,
            CliDescription = $"给选中的 {meshes.Count} 张三角网按面着色（逐顶点色，随工程存档）。",
        };
        form.Text($"给三角网着色：选好着色方式后应用到已选中的 {meshes.Count} 张三角网（逐顶点色，随工程存档）。")
            .Rows(PcRow.Radios("mode", "着色方式", modes, modes[0]))   // 默认「原始三角网素色」——直接确定即清掉分析着色(同原版 rbPlain)
            .Rows(
                PcRow.Combo("map", "色带", maps, maps[0], "（高程 / 坡度 / 坡向 / 等高线 分级用）", null, 96, 220),
                PcRow.Num("spacing", "等高线间距", "5", "m", "（「等高线」档用）", null, 96, 72));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "三角网着色：已取消"; return true; }
        int mode = Math.Max(0, Array.IndexOf(modes, v.S("mode")));
        int map = Math.Max(0, Array.IndexOf(maps, v.S("map")));
        double spacing = Math.Max(v.D("spacing", 5), 1e-6);

        BeginChange();
        int noRgb = 0;
        foreach (var m in meshes)
        {
            if (mode == 0 || mode == 7)   // 素色 / 清除：去掉逐顶点色，回到实体基色与全局显示模式
            {
                m.VertColors = null; m.RenderModeOverride = null; m.Invalidate(); continue;
            }
            if (mode == 1)   // 平面（单色·显示三角面）：单色 + 逐实体强制"面+线框"，三角面看得出来
            {
                m.VertColors = null; m.RenderModeOverride = MeshEntity.DisplayMode.ShadedWireframe; m.Invalidate(); continue;
            }
            if (mode == 2)   // 点云真实色：用建面时从点云带来的顶点 RGB
            {
                if (!m.HasRgbColors) { noRgb++; continue; }
                m.VertColors = new List<(float r, float g, float b)>(m.RgbColors!);
                m.Invalidate();
                continue;
            }
            var bb = m.Bounds; double zr = bb.maxZ - bb.minZ;
            var cols = new List<(float r, float g, float b)>(m.Verts.Count);
            var vn = PcVertexNormals(m);
            for (int i = 0; i < m.Verts.Count; i++)
            {
                double t;
                switch (mode)
                {
                    case 4:   // 坡度
                        t = Math.Clamp(Math.Acos(Math.Clamp(Math.Abs(vn[i].z), 0, 1)) * 180.0 / Math.PI / 70.0, 0, 1); break;
                    case 5:   // 坡向
                    {
                        double az = Math.Atan2(vn[i].x, -vn[i].y) * 180.0 / Math.PI; az %= 360; if (az < 0) az += 360;
                        t = az / 360.0; break;
                    }
                    case 6:   // 等高线：按等高距取带号分级上色
                        t = zr > 1e-9 ? (Math.Floor((m.Verts[i].z - bb.minZ) / spacing) * spacing) / zr : 0.5; break;
                    default:  // 高程
                        t = zr > 1e-9 ? (m.Verts[i].z - bb.minZ) / zr : 0.5; break;
                }
                cols.Add(map == 0 ? MeshEntity.TerrainRamp(t) : PointCloudOps.Ramp(map - 1, t));
            }
            m.VertColors = cols;
            m.Invalidate();
        }
        RefreshScene();
        StatusMsg.Text = mode is 0 or 7
            ? $"三角网着色：已清除 {meshes.Count} 张三角网的独立着色（回到素色）"
            : mode == 1
                ? $"三角网着色：{meshes.Count} 张三角网按「平面（单色·显示三角面）」显示（逐实体面+线框）"
            : mode == 2
                ? $"三角网着色：{meshes.Count} 张三角网按顶点真实色显示"
                  + (noRgb > 0 ? $"（其中 {noRgb} 张没有真实色 —— 要有真实色，得先用带 RGB 的点云建 2.5D TIN）" : "")
                : $"三角网着色：{meshes.Count} 张三角网按「{modes[mode]}」着色（{v.S("map")}）";
        return true;
    }

    /// <summary>逐顶点法向（三角面法向按面积无关的简单平均）—— 三角网着色/粗糙度/曲率共用。</summary>
    private static List<(double x, double y, double z)> PcVertexNormals(MeshEntity m)
    {
        var acc = new (double x, double y, double z)[m.Verts.Count];
        foreach (var (a, b, c) in m.Tris)
        {
            if (a >= m.Verts.Count || b >= m.Verts.Count || c >= m.Verts.Count) continue;
            var p = m.Verts[a]; var q = m.Verts[b]; var r = m.Verts[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z;
            double vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-15) continue;
            nx /= len; ny /= len; nz /= len;
            if (nz < 0) { nx = -nx; ny = -ny; nz = -nz; }
            foreach (int i in new[] { a, b, c }) acc[i] = (acc[i].x + nx, acc[i].y + ny, acc[i].z + nz);
        }
        var res = new List<(double x, double y, double z)>(m.Verts.Count);
        foreach (var n in acc)
        {
            double len = Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
            res.Add(len < 1e-15 ? (0, 0, 1) : (n.x / len, n.y / len, n.z / len));
        }
        return res;
    }

    // ═══════════════════ 6. 三角网分析 ═══════════════════

    /// <summary>
    /// 等高线生产：【需先选中三角网】按等高距生成等高线（多段线入库）。等高距默认 5m，同原版 ContourDialog。
    /// 算法与建模组「构建等值线」共用一份（栅格采样 + Marching Squares + 连段）。
    /// </summary>
    private async Task<bool> PcContourAsync()
    {
        var meshes = await PcSelectAsync<MeshEntity>("等高线生产", "三角网");
        if (meshes.Count == 0) { StatusMsg.Text = "等高线生产：场景里没有三角网，请先用「2.5D TIN」把点云建成面。"; return true; }
        var m = meshes[0];
        // 窗体组织忠实原 Contour/ContourDialog：一句说明 + 等高距一行（采样格距是本实现的必要项，收进「高级」）
        var b = m.Bounds;
        var form = new PcForm
        {
            Title = "等高线生产", OkText = "确定", Width = 430,
            CliDescription = "对选中的 2.5D 三角网按等高距生成等高线，入库为多段线。",
        };
        form.Text($"对选中的 2.5D 三角网「{m.Name}」按等高距生成等高线，入库为多段线（图层随三角网）。")
            .Rows(PcRow.Num("dz", "等高距", "5", "m", null, null, 60, 80))
            .Advanced("高级（采样）",
                PcRow.Num("cell", "采样格距", (Math.Max(b.maxX - b.minX, b.maxY - b.minY) / 200).ToString("0.###", Inv), "m",
                          "栅格采样步长：越小越贴合、越慢", null, 80, 80));
        var v = await form.AskAsync(this);
        if (v == null) { StatusMsg.Text = "等高线生产：已取消"; return true; }
        await MdlContourCoreAsync(m, "等高线生产", Math.Max(v.D("dz", 5), 1e-6), Math.Max(v.D("cell", 1), 1e-6));
        return true;
    }

    /// <summary>剖面分析：选中三角网 + 1 条剖面线 → 沿线在网格上采样出高程剖面图。</summary>
    private async Task<bool> PcMeshProfileAsync()
    {
        var meshes = await PcSelectAsync<MeshEntity>("剖面分析", "三角网");
        if (meshes.Count == 0) { StatusMsg.Text = "剖面分析：场景里没有三角网，请先用「2.5D TIN」把点云建成面。"; return true; }
        var line = await PcSelectLineAsync("剖面分析");
        if (line == null) { StatusMsg.Text = "剖面分析：场景里没有可作剖面线的线，请先画一条穿过三角网的线。"; return true; }
        var prof = PcSampleMeshProfile(meshes[0], line);
        if (prof.Count < 2) { StatusMsg.Text = "剖面分析：剖面线与三角网无交（线是否在网格范围内？）"; return true; }
        double zmin = prof.Min(p => p.z), zmax = prof.Max(p => p.z), len = prof[^1].dist;
        PcDrawProfile(line, prof, zmin, zmax, len, "剖面分析", 0.30f, 0.90f, 0.50f);
        PcProfileChartWindow.Popup(this, $"剖面图 · {meshes[0].Name}",
            $"沿选中剖面线在三角网「{meshes[0].Name}」上采样 {prof.Count} 点",
            prof.Select(x => new PcProfileChartWindow.Station(x.dist, x.z)).ToList());
        StatusMsg.Text = $"剖面分析「{meshes[0].Name}」：{prof.Count} 个采样点 · 里程 {len.ToString("0.#", Inv)}m"
                       + $" · 高程 {zmin.ToString("0.##", Inv)}~{zmax.ToString("0.##", Inv)}m";
        return true;
    }

    /// <summary>沿多段线逐段切网格，拼成一条按里程递增的剖面。</summary>
    private static List<(double dist, double z)> PcSampleMeshProfile(MeshEntity m, IReadOnlyList<(double x, double y)> line)
    {
        var prof = new List<(double dist, double z)>();
        double acc = 0;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var seg = MeshPlaneSection.Profile(m.Verts, m.Tris, line[i], line[i + 1]);
            foreach (var (d, z) in seg) if (d >= -1e-9) prof.Add((acc + d, z + m.Elevation));
            acc += Math.Sqrt(Math.Pow(line[i + 1].x - line[i].x, 2) + Math.Pow(line[i + 1].y - line[i].y, 2));
        }
        prof.Sort((a, b) => a.dist.CompareTo(b.dist));
        return prof;
    }

    /// <summary>工艺参数分析：选中三角网 + 1 条剖面线 → 台阶高/坡面角/平盘宽/整体帮坡角。</summary>
    private async Task<bool> PcProcessParamAsync()
    {
        var meshes = await PcSelectAsync<MeshEntity>("工艺参数分析", "三角网");
        if (meshes.Count == 0) { StatusMsg.Text = "工艺参数分析：场景里没有三角网，请先用「2.5D TIN」把点云建成面。"; return true; }
        var line = await PcSelectLineAsync("工艺参数分析");
        if (line == null) { StatusMsg.Text = "工艺参数分析：场景里没有可作剖面线的线，请先画一条穿过三角网的线。"; return true; }
        var v = await PromptDialog.AskAsync(this, "工艺参数分析", new[]
        {
            new PromptDialog.Field("flat", "平盘判定坡度", "15", "°", "缓于此坡度的段判为平盘，陡于此判为坡面"),
        }, $"沿选中剖面线在三角网「{meshes[0].Name}」上取剖面，提取台阶高 / 坡面角 / 平盘宽 / 整体帮坡角。");
        if (v == null) { StatusMsg.Text = "工艺参数分析：已取消"; return true; }
        var prof = PcSampleMeshProfile(meshes[0], line);
        if (prof.Count < 2) { StatusMsg.Text = "工艺参数分析：剖面线与三角网无交"; return true; }
        var res = BenchAnalyzer.Analyze(prof.Select(p => p.dist).ToList(), prof.Select(p => p.z).ToList(), v.D("flat", 15));
        if (res.Rows.Count == 0) { StatusMsg.Text = "工艺参数分析：剖面上未识别出台阶结构"; return true; }

        var rows = new List<(string, string)>
        {
            ("三角网 / 剖面线", $"{meshes[0].Name} · {prof.Count} 个采样点"),
            ("坡面 / 平盘 段数", $"{res.FaceCount} / {res.BermCount}"),
            ("总水平距 / 总高差", $"{res.TotalRun.ToString("0.##", Inv)} m / {res.TotalHeight.ToString("0.##", Inv)} m"),
            ("整体帮坡角", res.OverallSlopeDeg.ToString("0.##", Inv) + " °"),
        };
        foreach (var r in res.Rows.Take(24))
            rows.Add(($"#{r.Index} {r.Kind}", r.Kind == "坡面"
                ? $"台阶高 {r.Height.ToString("0.##", Inv)} m · 坡面角 {r.FaceAngleDeg.ToString("0.#", Inv)}° · 水平投影 {r.Width.ToString("0.##", Inv)} m"
                : $"平盘宽 {r.Width.ToString("0.##", Inv)} m · 标高 {r.TopZ.ToString("0.##", Inv)} m"));
        if (res.Rows.Count > 24) rows.Add(("…", $"另有 {res.Rows.Count - 24} 段未列出"));
        PcResultWindow.Popup(this, "工艺参数分析", rows, null, 0, 0, null, "沿剖面按坡度分平盘/坡面段（行程编码合并连续同类）。");
        StatusMsg.Text = $"工艺参数分析「{meshes[0].Name}」：{res.FaceCount} 个坡面 / {res.BermCount} 个平盘"
                       + $" · 整体帮坡角 {res.OverallSlopeDeg.ToString("0.##", Inv)}° · 总高差 {res.TotalHeight.ToString("0.##", Inv)}m";
        return true;
    }

    /// <summary>坡度/坡向着色：切视口【全局】面着色模式（只改显示、不出数值、对场景内所有三角网生效）。</summary>
    private void PcSetShadeMode(MeshEntity.FaceShade mode, string label)
    {
        bool off = MeshEntity.ShadeMode == mode;          // 再点一次 = 关掉，回到实体色
        MeshEntity.ShadeMode = off ? MeshEntity.FaceShade.Entity : mode;
        foreach (var m in AllMeshes()) m.Invalidate();    // 面缓存按着色模式建的, 换模式要重建
        RefreshScene();
        int n = AllMeshes().Count;
        StatusMsg.Text = off
            ? $"{label}着色：已关闭，回到实体色（{n} 张三角网）"
            : $"{label}着色：视口全局按{label}着色（GPU 实时，只改显示、不出数值，对场景内 {n} 张三角网生效）";
    }

    /// <summary>粗糙度 / 曲率：选中三角网 → 逐顶点算 → 着色（原版这两项都要求先选中三角网）。</summary>
    private async Task<bool> PcMeshAttribAsync(bool curvature)
    {
        string what = curvature ? "曲率" : "粗糙度";
        var meshes = await PcSelectAsync<MeshEntity>(what + "分析", "三角网");
        if (meshes.Count == 0) { StatusMsg.Text = $"{what}分析：场景里没有三角网，请先用「2.5D TIN」把点云建成面。"; return true; }
        // 原版这两项无参数框：选中三角网后直接算并着色（曲率有正负用分歧色，粗糙度用地形色）
        int map = curvature ? 2 : 0;

        BeginChange();
        int total = 0;
        foreach (var m in meshes)
        {
            var vals = PcVertexAttrib(m, curvature);
            if (vals.Length == 0) continue;
            double lo, hi;
            if (curvature)
            {
                double amax = 0; foreach (double d in vals) amax = Math.Max(amax, Math.Abs(d));
                lo = -amax; hi = amax;                     // 曲率有正负(凸/凹)，对称区间配分歧色才读得出
            }
            else
            {
                lo = double.MaxValue; hi = double.MinValue;
                foreach (double d in vals) { if (d < lo) lo = d; if (d > hi) hi = d; }
            }
            m.VertColors = PointCloudOps.Colorize(vals, map, lo, hi);
            m.Invalidate();
            total += vals.Length;
        }
        RefreshScene();
        StatusMsg.Text = $"{what}分析：{meshes.Count} 张三角网 · {total:N0} 个顶点已按{what}着色"
                       + (curvature ? "（分歧色：蓝=凹 / 白=平 / 红=凸）" : "（局部高程起伏 RMS，越红越粗糙）");
        return true;
    }

    // ═══════════════ 自检辅助 ═══════════════

    /// <summary>
    /// 自检：合成一份露天矿台阶地形点云直接入场景（免文件对话框），供渲染与各算子核对。
    /// 地形 = 4 级台阶（平盘 + 坡面）+ 测量噪声，另在平盘上放一团"矿卡"点（离地 3~6m），
    /// 用来检验地面点滤波/去噪确实把它剔掉。
    /// </summary>
    private void SelftestSamplePointCloud(int cols, int rows, double dz = 0, string? name = null)
    {
        var rnd = new Random(20260909);
        var pts = new List<(double x, double y, double z)>(cols * rows + 400);
        const double baseZ = 1200, benchH = 15, benchW = 40, slopeW = 15;
        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
            {
                double x = i * 200.0 / cols, y = j * 120.0 / rows;
                double period = benchW + slopeW;
                double t = x % period;
                int level = (int)(x / period);
                double z = baseZ - level * benchH - (t > benchW ? (t - benchW) / slopeW * benchH : 0);
                pts.Add((x, y, z + dz + (rnd.NextDouble() - 0.5) * 0.25));   // ±12.5cm 测量噪声
            }
        for (int k = 0; k < 400; k++)   // 平盘上的一台矿卡(离地 3~6m)
            pts.Add((20 + rnd.NextDouble() * 10, 50 + rnd.NextDouble() * 6, baseZ + dz + 3 + rnd.NextDouble() * 3));

        BeginChange();
        var pc = PcCommit(null, name ?? (dz == 0 ? "自检点云" : $"自检点云+{dz:0.#}m"), pts);
        pc.SetSolidColor(0.75f, 0.78f, 0.82f);
        RefreshScene();
        PcZoomTo(pc);
        Title += $" [自检 点云 {pc.PointCount} 点]";
        StatusMsg.Text = $"自检点云「{pc.Name}」：{pc.PointCount:N0} 点（4 级台阶 + 噪声 + 矿卡）已入场景并设为当前点云";
    }

    /// <summary>
    /// 自检：往场景放一条线并选中它 —— 剖面线（横穿台阶的直线）或闭合边界（矩形圈）。
    /// 点云剖面 / 分割点云 / 圈范围算量 / 剖面分析 / 工艺参数分析 都要求"先选中一条线"，
    /// 自检脚本没法用鼠标画，故直接造出来选上。
    /// </summary>
    private void SelftestSampleLine(bool closed)
    {
        var pl = new PolylineEntity { Closed = closed, Cr = 0.95f, Cg = 0.8f, Cb = 0.3f, LayerName = closed ? "自检边界" : "自检剖面线" };
        if (closed) pl.Points.AddRange(new[] { (40.0, 30.0), (140.0, 30.0), (140.0, 90.0), (40.0, 90.0) });
        else pl.Points.AddRange(new[] { (0.0, 60.0), (200.0, 60.0) });
        BeginChange();
        _scene.Add(pl);
        _selected.Add(pl);   // 追加不清空: 剖面分析/工艺参数分析要求「三角网 + 剖面线」同时选中
        RefreshScene(); HighlightSelection();
        StatusMsg.Text = $"自检{(closed ? "闭合边界" : "剖面线")}：{pl.Points.Count} 点已入场景并选中（当前共选 {_selected.Count} 个）";
    }

    /// <summary>逐顶点 粗糙度(一环高差 RMS) 或 曲率(高度 Laplacian)。</summary>
    private static double[] PcVertexAttrib(MeshEntity m, bool curvature)
    {
        int n = m.Verts.Count;
        if (n == 0) return Array.Empty<double>();
        var sum = new double[n]; var cnt = new int[n];
        foreach (var (i, j) in m.Edges)
        {
            if (i >= n || j >= n) continue;
            sum[i] += m.Verts[j].z; cnt[i]++;
            sum[j] += m.Verts[i].z; cnt[j]++;
        }
        var res = new double[n];
        if (!curvature)
        {
            var sq = new double[n];
            foreach (var (i, j) in m.Edges)
            {
                if (i >= n || j >= n) continue;
                double d1 = m.Verts[j].z - m.Verts[i].z, d2 = -d1;
                sq[i] += d1 * d1; sq[j] += d2 * d2;
            }
            for (int i = 0; i < n; i++) res[i] = cnt[i] > 0 ? Math.Sqrt(sq[i] / cnt[i]) : 0;
            return res;
        }
        for (int i = 0; i < n; i++) res[i] = cnt[i] > 0 ? m.Verts[i].z - sum[i] / cnt[i] : 0;
        return res;
    }
}
