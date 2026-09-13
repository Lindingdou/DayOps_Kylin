// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/Geology/VirtualDrillService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;

using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// <see cref="IVirtualDrillService"/> 实现:虚拟钻孔地质模型面的持久化 / 读取。
/// 几何(顶点 + 三角索引)由 <see cref="VirtualDrillGeometry"/> 打包成 base64 存 geometry_b64。
/// </summary>
internal sealed class VirtualDrillService : IVirtualDrillService
{
    private readonly IRepository<VirtualDrillSurface> _repo;

    public VirtualDrillService(ISqlService sql)
    {
        _repo = sql.Repository<VirtualDrillSurface>();
    }

    public IReadOnlyList<VirtualDrillSurface> AllSurfaces()
        => _repo.Where("1=1 ORDER BY seam_order, role");

    public VirtualDrillSurface? GetByKey(string role, string seamName)
        => _repo.Where("role = @r AND seam_name = @s", new { r = role ?? "", s = seamName ?? "" })
                .FirstOrDefault();

    public bool HasAnySurface() => _repo.Count() > 0;

    public long SaveSurface(string role, string seamName, int seamOrder, string colorHex,
                            string sourceLayer, double[] verts, int[] tris)
    {
        string b64 = VirtualDrillGeometry.Pack(verts, tris);
        if (b64.Length == 0) return 0;   // 空 / 非法几何不写

        VirtualDrillGeometry.ComputeBounds(verts,
            out double minX, out double minY, out double minZ,
            out double maxX, out double maxY, out double maxZ);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        role ??= "roof";
        seamName ??= "";
        colorHex = string.IsNullOrWhiteSpace(colorHex) ? "#3C3C3C" : colorHex;
        int vCount = verts.Length / 3, tCount = tris.Length / 3;

        var existing = GetByKey(role, seamName);
        if (existing != null)
        {
            existing.SeamOrder = seamOrder;
            existing.ColorHex = colorHex;
            existing.SourceLayer = sourceLayer ?? "";
            existing.VertexCount = vCount;
            existing.TriangleCount = tCount;
            existing.MinX = minX; existing.MinY = minY; existing.MinZ = minZ;
            existing.MaxX = maxX; existing.MaxY = maxY; existing.MaxZ = maxZ;
            existing.GeometryB64 = b64;
            existing.UpdatedAt = now;
            _repo.Update(existing);
            return existing.Id;
        }

        var row = new VirtualDrillSurface
        {
            Role = role,
            SeamName = seamName,
            SeamOrder = seamOrder,
            ColorHex = colorHex,
            SourceLayer = sourceLayer ?? "",
            VertexCount = vCount,
            TriangleCount = tCount,
            MinX = minX, MinY = minY, MinZ = minZ,
            MaxX = maxX, MaxY = maxY, MaxZ = maxZ,
            GeometryB64 = b64,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return _repo.Insert(row);
    }

    public bool TryGetGeometry(long id, out double[] verts, out int[] tris)
    {
        verts = Array.Empty<double>();
        tris = Array.Empty<int>();
        var row = _repo.GetByKey(id);
        if (row == null) return false;
        return VirtualDrillGeometry.TryUnpack(row.GeometryB64, out verts, out tris);
    }

    public int UpdateSeamMeta(string seamName, int seamOrder, string colorHex)
    {
        colorHex = string.IsNullOrWhiteSpace(colorHex) ? "#3C3C3C" : colorHex;
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        int n = 0;
        foreach (var s in _repo.Where("seam_name = @s", new { s = seamName ?? "" }))
        {
            s.SeamOrder = seamOrder;
            s.ColorHex = colorHex;
            s.UpdatedAt = now;
            _repo.Update(s);
            n++;
        }
        return n;
    }

    public void Delete(long id) => _repo.Delete(id);

    public int ClearAll() => _repo.DeleteWhere("1=1");
}
