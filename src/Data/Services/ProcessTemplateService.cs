// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/ProcessTemplateService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class ProcessTemplateService : IProcessTemplateService
{
    private readonly ISqlService _sql;
    private readonly IRepository<ProcessTemplate> _tplRepo;
    private readonly IRepository<TemplateParamValue> _valRepo;

    public ProcessTemplateService(ISqlService sql)
    {
        _sql = sql;
        _tplRepo = sql.Repository<ProcessTemplate>();
        _valRepo = sql.Repository<TemplateParamValue>();
    }

    // 模板主表
    public IReadOnlyList<ProcessTemplate> AllTemplates(bool activeOnly = true)
        => activeOnly
            ? _tplRepo.Where("status = 'active' ORDER BY applicable_material, name")
            : _tplRepo.Where("1=1 ORDER BY applicable_material, name");

    public ProcessTemplate? Get(long templateId) => _tplRepo.GetByKey(templateId);

    public ProcessTemplate? GetByCode(string code)
        => _tplRepo.Where("code = @c", new { c = code }).FirstOrDefault();

    public IReadOnlyList<ProcessTemplate> ByMaterial(string material)
        => _tplRepo.Where("applicable_material = @m AND status = 'active'", new { m = material });

    public long Insert(ProcessTemplate entity) => _tplRepo.Insert(entity);
    public void Update(ProcessTemplate entity) => _tplRepo.Update(entity);
    public void Delete(long templateId) => _tplRepo.Delete(templateId);

    public void Archive(long templateId)
    {
        var t = _tplRepo.GetByKey(templateId);
        if (t == null) return;
        t.Status = "archived";
        t.IsCurrent = false;
        _tplRepo.Update(t);
    }

    // 模板参数值
    public IReadOnlyList<TemplateParamValue> ValuesByTemplate(long templateId)
        => _valRepo.Where("template_id = @t", new { t = templateId });

    public TemplateParamValue? GetValue(long templateId, long paramId)
        => _valRepo.Where("template_id = @t AND param_id = @p",
            new { t = templateId, p = paramId }).FirstOrDefault();

    public void UpsertValue(TemplateParamValue entity)
    {
        var existing = GetValue(entity.TemplateId, entity.ParamId);
        if (existing != null)
        {
            entity.Id = existing.Id;
            _valRepo.Update(entity);
        }
        else
        {
            _valRepo.Insert(entity);
        }
    }

    public void DeleteValue(long id) => _valRepo.Delete(id);

    public void DeleteAllValuesOfTemplate(long templateId)
        => _sql.Execute("DELETE FROM template_param_value WHERE template_id = @t", new { t = templateId });
}
