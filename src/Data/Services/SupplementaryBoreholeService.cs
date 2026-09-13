// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/SupplementaryBoreholeService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>补勘钻孔写实 CRUD(独立于原始 borehole / borehole_seam_result)。</summary>
internal sealed class SupplementaryBoreholeService : ISupplementaryBoreholeService
{
    private readonly IRepository<SupplementaryBorehole> _holes;
    private readonly IRepository<SupplementarySeamHorizon> _horizons;
    private readonly IRepository<SupplementaryBatch> _batches;

    public SupplementaryBoreholeService(ISqlService sql)
    {
        _holes = sql.Repository<SupplementaryBorehole>();
        _horizons = sql.Repository<SupplementarySeamHorizon>();
        _batches = sql.Repository<SupplementaryBatch>();
    }

    // ─── 批次(系统唯一时间标签) ───
    public IReadOnlyList<SupplementaryBatch> AllBatches()
    {
        var list = _batches.Where("1=1 ORDER BY label DESC").ToList();
        foreach (var b in list)
            b.HoleCount = _holes.Where("batch_id = @b", new { b = b.Id }).Count;
        return list;
    }

    public SupplementaryBatch? GetBatch(long id) => _batches.GetByKey(id);

    public long CreateBatch(string source, string? name = null)
    {
        string label = GenerateUniqueLabel();
        return _batches.Insert(new SupplementaryBatch
        {
            Label = label,
            Name = string.IsNullOrWhiteSpace(name) ? DefaultName(source, label) : name!,
            Source = source,
        });
    }

    public void RenameBatch(long id, string name)
    {
        var b = _batches.GetByKey(id);
        if (b == null) return;
        b.Name = name ?? "";
        _batches.Update(b);
    }

    public int DeleteBatch(long id)
    {
        var holeIds = _holes.Where("batch_id = @b", new { b = id }).Select(h => h.Id).ToList();
        foreach (var hid in holeIds)
            _horizons.DeleteWhere("sup_borehole_id = @h", new { h = hid });
        _holes.DeleteWhere("batch_id = @b", new { b = id });
        _batches.Delete(id);
        return holeIds.Count;
    }

    // 系统唯一时间标签: SBW-yyyyMMdd-HHmmss,同秒并发再加 -nn 直到唯一。
    private string GenerateUniqueLabel()
    {
        string prefix = "SBW-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string label = prefix;
        int n = 1;
        while (_batches.Where("label = @l", new { l = label }).Count > 0)
            label = prefix + "-" + (++n).ToString("00");
        return label;
    }

    private static string DefaultName(string source, string label)
    {
        // 从 label(SBW-yyyyMMdd-HHmmss) 取时刻做友好名
        string t = label.Length >= 19
            ? $"{label.Substring(4, 4)}-{label.Substring(8, 2)}-{label.Substring(10, 2)} {label.Substring(13, 2)}:{label.Substring(15, 2)}"
            : label;
        return $"{source} {t}";
    }

    // ─── 补勘孔 ───
    public IReadOnlyList<SupplementaryBorehole> AllHoles(long? batchId = null)
        => batchId is { } b
            ? _holes.Where("batch_id = @b ORDER BY hole_id", new { b })
            : _holes.Where("1=1 ORDER BY hole_id");

    public SupplementaryBorehole? GetHole(long id) => _holes.GetByKey(id);

    public SupplementaryBorehole? GetHoleByHoleId(string holeId)
        => _holes.Where("hole_id = @h", new { h = holeId }).FirstOrDefault();

    public long InsertHole(SupplementaryBorehole hole) => _holes.Insert(hole);
    public void UpdateHole(SupplementaryBorehole hole) => _holes.Update(hole);

    public void DeleteHole(long id)
    {
        // 显式先删层位(不依赖 PRAGMA foreign_keys 是否开启),再删孔。
        _horizons.DeleteWhere("sup_borehole_id = @id", new { id });
        _holes.Delete(id);
    }

    // ─── 煤层顶/底板层位 ───
    public IReadOnlyList<SupplementarySeamHorizon> HorizonsByHole(long supBoreholeId)
        => _horizons.Where("sup_borehole_id = @id ORDER BY sort_order", new { id = supBoreholeId });

    public void ReplaceHorizons(long supBoreholeId, IEnumerable<SupplementarySeamHorizon> horizons)
    {
        _horizons.DeleteWhere("sup_borehole_id = @id", new { id = supBoreholeId });
        foreach (var h in horizons)
        {
            h.Id = 0;                       // 强制新插(忽略传入 Id)
            h.SupBoreholeId = supBoreholeId;
            _horizons.Insert(h);
        }
    }

    public IReadOnlyList<SupplementarySeamHorizon> AllHorizons()
        => _horizons.Where("1=1 ORDER BY sup_borehole_id, sort_order");
}
