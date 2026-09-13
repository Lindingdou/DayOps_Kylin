// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/CurrentStateService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>现状写实 CRUD(现状点 + 批次;独立成套)。</summary>
internal sealed class CurrentStateService : ICurrentStateService
{
    private readonly IRepository<CurrentStateBatch> _batches;
    private readonly IRepository<CurrentStatePoint> _points;

    public CurrentStateService(ISqlService sql)
    {
        _batches = sql.Repository<CurrentStateBatch>();
        _points = sql.Repository<CurrentStatePoint>();
    }

    // ─── 批次 ───
    public IReadOnlyList<CurrentStateBatch> AllBatches()
    {
        var list = _batches.Where("1=1 ORDER BY label DESC").ToList();
        foreach (var b in list)
            b.PointCount = _points.Where("batch_id = @b", new { b = b.Id }).Count;
        return list;
    }

    public CurrentStateBatch? GetBatch(long id) => _batches.GetByKey(id);

    public long CreateBatch(string source, string? name = null)
    {
        string label = GenerateUniqueLabel();
        return _batches.Insert(new CurrentStateBatch
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
        int n = _points.Where("batch_id = @b", new { b = id }).Count;
        _points.DeleteWhere("batch_id = @b", new { b = id });
        _batches.Delete(id);
        return n;
    }

    // ─── 现状点 ───
    public IReadOnlyList<CurrentStatePoint> PointsByBatch(long batchId)
        => _points.Where("batch_id = @b ORDER BY id", new { b = batchId });

    public IReadOnlyList<CurrentStatePoint> AllPoints()
        => _points.Where("1=1 ORDER BY id");

    public void ReplacePoints(long batchId, IEnumerable<CurrentStatePoint> points)
    {
        _points.DeleteWhere("batch_id = @b", new { b = batchId });
        foreach (var p in points)
        {
            p.Id = 0;
            p.BatchId = batchId;
            _points.Insert(p);
        }
    }

    // 系统唯一时间标签: CSR-yyyyMMdd-HHmmss,同秒并发再加 -nn 直到唯一。
    private string GenerateUniqueLabel()
    {
        string prefix = "CSR-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string label = prefix;
        int n = 1;
        while (_batches.Where("label = @l", new { l = label }).Count > 0)
            label = prefix + "-" + (++n).ToString("00");
        return label;
    }

    private static string DefaultName(string source, string label)
    {
        string t = label.Length >= 19
            ? $"{label.Substring(4, 4)}-{label.Substring(8, 2)}-{label.Substring(10, 2)} {label.Substring(13, 2)}:{label.Substring(15, 2)}"
            : label;
        return $"{source} {t}";
    }
}
