// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IParameterAcceptanceService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>现场验收服务:实测参数录入与查询。</summary>
public interface IParameterAcceptanceService
{
    ParameterAcceptance? Get(long id);

    /// <summary>某平盘+环节的全部验收(按日期降序)。</summary>
    IReadOnlyList<ParameterAcceptance> ByLocationPhase(string locationCode, long phaseId);

    /// <summary>某平盘+环节+参数的最新一条实测(用于"现状视图"对比)。</summary>
    ParameterAcceptance? LatestForParam(string locationCode, long phaseId, long paramId);

    /// <summary>某平盘所有环节的最新实测(map: paramId → record)。</summary>
    IReadOnlyDictionary<long, ParameterAcceptance> LatestSnapshotByLocation(string locationCode);

    /// <summary>某状态(如 warning/fail)的最近 N 天记录,做报警面板用。</summary>
    IReadOnlyList<ParameterAcceptance> RecentByStatus(string status, int days = 30);

    /// <summary>某参数在指定时间段的所有实测值(画趋势图用)。</summary>
    IReadOnlyList<ParameterAcceptance> SeriesByParam(string locationCode, long phaseId, long paramId, DateTime startDate, DateTime endDate);

    long Insert(ParameterAcceptance entity);
    void Update(ParameterAcceptance entity);
    void Delete(long id);

    /// <summary>给定参数定义和模板值,计算偏差和状态(辅助方法)。</summary>
    (double? deviationPct, string status) ComputeStatus(ParameterDefinition def, double? templateValue, double? measuredValue);
}
