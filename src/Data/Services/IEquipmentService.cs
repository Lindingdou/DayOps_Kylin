// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IEquipmentService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
// 不要 using GeoDataBase.Public.Entities;
// 在 GeoDataBase.* 父命名空间里有子命名空间 GeoDataBase.Equipment(老 CsvDataStore 目录),
// 它会让单独的 Equipment 类型名被误解析为命名空间。这里直接用类型别名。
using DbEq = PitMine3D.Kylin.Data.Entities.Equipment;
using DbEm = PitMine3D.Kylin.Data.Entities.EquipmentModel;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Data.Services;

public interface IEquipmentService
{
    // 读
    DbEq? GetById(string equipmentId);
    IReadOnlyList<DbEq> All();
    IReadOnlyList<DbEq> ByCategory(EquipmentCategory category);
    IReadOnlyList<DbEq> ByStatus(EquipmentStatus status);
    IReadOnlyList<DbEq> ActiveOnly();

    // 写
    void Insert(DbEq entity);
    void Update(DbEq entity);
    void Upsert(DbEq entity);
    void Delete(string equipmentId);
    void SetStatus(string equipmentId, EquipmentStatus status, string? reason = null);

    // 派生
    double CalculateCumulativeHours(string equipmentId);
    string? GetCurrentArea(string equipmentId);
}

public interface IEquipmentModelService
{
    DbEm? Get(string model);
    IReadOnlyList<DbEm> All();
    IReadOnlyList<DbEm> ByCategory(EquipmentCategory category);
    void Upsert(DbEm entity);
    void Delete(string model);
}
