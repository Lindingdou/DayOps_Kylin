// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/ILongTermService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface ILongTermService
{
    IReadOnlyList<LongTermMetric> Query(MetricSource source, string item, int? year = null);
    IReadOnlyList<LongTermMetric> AnnualSeries(MetricSource source, string item);
    IReadOnlyList<LongTermMetric> AllBySource(MetricSource source);
    IReadOnlyList<string> ListItems(MetricSource source);
    void Insert(LongTermMetric entity);
}
