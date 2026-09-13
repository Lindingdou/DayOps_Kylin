// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IProcessTemplateService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>参数模板库服务。</summary>
public interface IProcessTemplateService
{
    // 模板主表
    IReadOnlyList<ProcessTemplate> AllTemplates(bool activeOnly = true);
    ProcessTemplate? Get(long templateId);
    ProcessTemplate? GetByCode(string code);
    IReadOnlyList<ProcessTemplate> ByMaterial(string material);
    long Insert(ProcessTemplate entity);
    void Update(ProcessTemplate entity);
    void Delete(long templateId);
    void Archive(long templateId);          // 归档(status='archived')

    // 模板参数值
    IReadOnlyList<TemplateParamValue> ValuesByTemplate(long templateId);
    TemplateParamValue? GetValue(long templateId, long paramId);
    void UpsertValue(TemplateParamValue entity);
    void DeleteValue(long id);
    void DeleteAllValuesOfTemplate(long templateId);
}
