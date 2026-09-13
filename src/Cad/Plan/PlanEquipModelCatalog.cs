// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/PlanEquipModelCatalog.cs（逐行对应；仅命名空间/取数层适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;             // EquipmentDataContext（未就绪时全程降级）/ EquipmentCategory / EquipmentStatus
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  「确定开采程序」的设备型号候选 —— equipment_model × equipment 在册台数。
//
//  为什么型号候选一定要带【在册可派台数】：
//    配一个在册 0 台的型号，界面上和配一个在册 12 台的型号长得一模一样，
//    而排产时前者一台设备都挑不出来 —— 结果是「这个面欠产」，原因栏写的是
//    「没有空闲的挖装设备」，谁也想不到根因是三周前在这张表里选错了一个型号。
//    所以台数进下拉文案，选之前就看得见。
//
//  这一层只做【读】。型号字典的维护在「数据库 → 设备型号」，不在计划侧。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个可选型号 —— 型号 + 它在册多少台 + 有没有标准日产能。</summary>
public sealed class PlanEquipModel
{
    public string Model { get; init; } = "";
    public EquipmentCategory Category { get; init; }

    /// <summary>在册台数（<c>equipment</c> 表里这个型号有几台）。</summary>
    public int OnRoll { get; init; }
    /// <summary>其中状态「在用」的台数 —— <b>只有这些排产时挑得出来</b>。</summary>
    public int Dispatchable { get; init; }

    /// <summary>型号字典里的标准日产能（万m³/日）。null = 这个型号连缺省台效都没有。</summary>
    public double? StdDailyCapWanM3 { get; init; }

    /// <summary>下拉里显示的文案 —— 台数与台效缺失都写在脸上。</summary>
    public string PickerText
    {
        get
        {
            if (Model.Length == 0) return "（不约束 · 全矿挑）";
            string n = Dispatchable > 0 ? $"可派 {Dispatchable} 台"
                     : OnRoll > 0 ? $"◆ 在册 {OnRoll} 台但一台都不可派"
                     : "◆ 在册 0 台";
            string cap = StdDailyCapWanM3 is > 0 ? "" : " · 无缺省台效";
            return $"{Model}（{n}{cap}）";
        }
    }

    /// <summary>这个型号选下去排产能不能真挑到设备。</summary>
    public bool Usable => Dispatchable > 0;

    /// <summary>下拉首项：不钉型号 = 排产时全矿在册里挑。<b>必须有这一项</b>，
    /// 否则选过之后就再也退不回"不约束"，只能删行重建。</summary>
    public static PlanEquipModel Any => new();
}

/// <summary>
/// 型号候选按<b>工序</b>分组。采装把电铲与前装机并成一组 —— 现场"这个面用什么挖装设备"
/// 问的就是这一个问题，分成两个下拉只会让人来回试。
/// </summary>
public static class PlanEquipModelCatalog
{
    private static List<PlanEquipModel>? _cache;
    private static List<string>? _faceCodes;

    /// <summary>数据来源文案（窗口上要显示清楚这批型号是台账还是空）。</summary>
    public static string SourceText { get; private set; } = "（尚未读取）";

    /// <summary>全部型号（首次访问自动读一次）。</summary>
    public static IReadOnlyList<PlanEquipModel> All => _cache ??= Load();

    /// <summary>强制重读。</summary>
    public static IReadOnlyList<PlanEquipModel> Reload() { _cache = null; _faceCodes = null; return All; }

    /// <summary>穿孔：钻机。</summary>
    public static List<PlanEquipModel> Drills => Of(EquipmentCategory.Drill);
    /// <summary>采装：电铲 + 前装机（现场问的是同一个问题）。</summary>
    public static List<PlanEquipModel> LoadersAndShovels
        => All.Where(m => m.Category is EquipmentCategory.Shovel or EquipmentCategory.Loader)
              .OrderByDescending(m => m.Dispatchable).ThenBy(m => m.Model, StringComparer.Ordinal).ToList();
    /// <summary>运输：卡车。</summary>
    public static List<PlanEquipModel> Trucks => Of(EquipmentCategory.Truck);
    /// <summary>排土：推土机。</summary>
    public static List<PlanEquipModel> Dozers => Of(EquipmentCategory.Dozer);

    private static List<PlanEquipModel> Of(EquipmentCategory c)
        => All.Where(m => m.Category == c)
              .OrderByDescending(m => m.Dispatchable).ThenBy(m => m.Model, StringComparer.Ordinal).ToList();

    // ── 界面下拉用（首项「不约束」）。XAML 经 x:Static 直接绑，模板实例化时求值，
    //    所以数据库晚一点就绪也读得到。
    public static List<PlanEquipModel> DrillOptions => WithAny(Drills);
    public static List<PlanEquipModel> LoaderOptions => WithAny(LoadersAndShovels);
    public static List<PlanEquipModel> TruckOptions => WithAny(Trucks);
    public static List<PlanEquipModel> DozerOptions => WithAny(Dozers);

    private static List<PlanEquipModel> WithAny(List<PlanEquipModel> pool)
    { pool.Insert(0, PlanEquipModel.Any); return pool; }

    /// <summary>
    /// 库表 <c>working_face.face_code</c> 的候选（活跃面优先）。
    /// 空 = 库未就绪或一条工作面档案都没建 —— 此时 FaceCode 列仍可手填，不挡住人干活。
    /// </summary>
    public static IReadOnlyList<string> FaceCodes => _faceCodes ??= LoadFaceCodes();

    /// <summary>
    /// 按 face_code 取库表里的台阶高（m）。0 = 没建档 / 没填 / 库未就绪。
    /// <para>工艺算穿孔量要它。<b>取不到就返回 0，不给一个"常见值"</b> ——
    /// 拿 15m 顶上算出来的月延米看着挺像回事，而它跟这个面没有任何关系。</para>
    /// </summary>
    public static double BenchHeightOf(string? faceCode)
    {
        if (string.IsNullOrWhiteSpace(faceCode)) return 0;
        try
        {
            var w = EquipmentDataContext.WorkingFaces.GetByCode(faceCode.Trim());
            return w != null && w.BenchHeightM > 0 ? w.BenchHeightM : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 校验一个面的四个型号配置 —— 返回每一条<b>有问题</b>的说明（空 = 没问题）。
    /// <para>只报「选了但用不了」，不报「没选」：留空是合法的（= 不约束，全矿挑）。</para>
    /// </summary>
    public static List<string> Check(WorkingFace f)
    {
        var bad = new List<string>();
        if (f == null) return bad;
        Chk(f.DrillModel, "穿孔", Drills);
        Chk(f.LoaderModel, "采装", LoadersAndShovels);
        Chk(f.TruckModel, "运输", Trucks);
        Chk(f.DozerModel, "排土", Dozers);
        if (f.TrucksPerLoader < 0)
            bad.Add($"「{f.Name}」配车数填的是 {f.TrucksPerLoader}（须 ≥ 0，0 = 按现场编组规则）");
        return bad;

        void Chk(string? model, string process, List<PlanEquipModel> pool)
        {
            if (string.IsNullOrWhiteSpace(model)) return;                 // 留空 = 不约束，合法
            var hit = pool.FirstOrDefault(m => string.Equals(m.Model, model.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                bad.Add($"「{f.Name}」{process}型号「{model.Trim()}」不在型号字典的该类别里 —— 排产时挑不到设备");
            else if (!hit.Usable)
                bad.Add($"「{f.Name}」{process}型号「{hit.Model}」在册 {hit.OnRoll} 台、可派 {hit.Dispatchable} 台 —— "
                      + "排产时一台都挑不出来，这个面会欠产");
        }
    }

    // ──────────────────────────────────────────────────────────────────

    private static List<PlanEquipModel> Load()
    {
        var res = new List<PlanEquipModel>();

        List<PitMine3D.Kylin.Data.Entities.EquipmentModel> models;
        try { models = EquipmentDataContext.Models.All().ToList(); }
        catch (Exception ex)
        {
            SourceText = "◆ 型号字典读不出来（" + ex.Message + "）—— 设备型号列只能手填";
            return res;
        }

        // 在册台数：按型号统计。读不出来不算致命 —— 型号仍可选，只是台数显示为 0 并在来源文案里说明。
        var onRoll = new Dictionary<string, (int all, int ok)>(StringComparer.OrdinalIgnoreCase);
        bool eqOk = true;
        try
        {
            foreach (var e in EquipmentDataContext.Equipment.All())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Model)) continue;
                var cur = onRoll.TryGetValue(e.Model, out var v) ? v : (0, 0);
                onRoll[e.Model] = (cur.Item1 + 1, cur.Item2 + (IsInUse(e.Status) ? 1 : 0));
            }
        }
        catch { eqOk = false; }

        foreach (var m in models)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Model)) continue;
            if (!Enum.TryParse<EquipmentCategory>(m.Category, true, out var cat)) cat = EquipmentCategory.Other;
            var n = onRoll.TryGetValue(m.Model, out var v) ? v : (0, 0);
            res.Add(new PlanEquipModel
            {
                Model = m.Model, Category = cat,
                OnRoll = n.Item1, Dispatchable = n.Item2,
                StdDailyCapWanM3 = m.StdDailyCapWanM3,
            });
        }

        SourceText = eqOk
            ? $"型号字典 {res.Count} 条（穿 {res.Count(x => x.Category == EquipmentCategory.Drill)} · "
              + $"采 {res.Count(x => x.Category is EquipmentCategory.Shovel or EquipmentCategory.Loader)} · "
              + $"运 {res.Count(x => x.Category == EquipmentCategory.Truck)} · "
              + $"排 {res.Count(x => x.Category == EquipmentCategory.Dozer)}）· 括号内是在册可派台数"
            : $"型号字典 {res.Count} 条 · ◆ 设备在册表读不出来，台数一律显示 0（不代表真的没有设备）";
        return res;
    }

    private static List<string> LoadFaceCodes()
    {
        try
        {
            return EquipmentDataContext.WorkingFaces.All()
                   .Where(w => w != null && !string.IsNullOrWhiteSpace(w.FaceCode))
                   .OrderByDescending(w => string.Equals(w.Status, "active", StringComparison.OrdinalIgnoreCase))
                   .ThenBy(w => w.FaceCode, StringComparer.Ordinal)
                   .Select(w => w.FaceCode).Distinct(StringComparer.Ordinal).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>状态列存的是中文，枚举名是英文 —— 两种都认，认不出的按不可派（与取数层同口径）。</summary>
    private static bool IsInUse(string? status)
    {
        string s = (status ?? "").Trim();
        if (s.Length == 0) return false;
        if (string.Equals(s, EquipmentStatus.InUse.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
        return s is "在用" or "在役" or "正常";
    }
}
