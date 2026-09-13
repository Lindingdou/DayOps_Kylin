// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IDumpStripService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 潜在排土位置服务：排土条带网格的清单读写。
///
/// 与一般 CRUD 的差别只有一条 —— <see cref="ReplaceForRegion"/>：
/// 重切一个排土场必须【先清后写】。切分参数（分割长度 / 条带宽度）一改，位置的编号、
/// 幅数、带数全变，旧行留着就成了两套网格叠在一张表里，寻径和排产会同时看到两套位置。
/// </summary>
/// <summary>
/// 整体替换的结果：写进去多少、丢了多少、第一条错是什么。
///
/// 【为什么不能只返回"写了几行"】先前逐条 <c>try { Insert } catch { }</c> 静默吞异常，
/// 只把成功数累加回去 —— 唯一索引挡掉的行就这么无声无息地没了。
/// 实测栽过两次：位置编号撞号（同一级多条台阶壳子各自从 P01 数）、带内子格
///（(级,幅,带) 相同只差子号，V040 之前索引容不下）。两次都是**清单和图上都还在、
/// 表里少了一批**，而界面只显示"已落库 N 行"，N 少了没人看得出来。
/// 丢行必须让调用方看得见 —— 这就是这个结构存在的全部理由。
/// </summary>
public readonly record struct DumpStripSaveReport(int Inserted, int Failed, string? FirstError)
{
    public int Total => Inserted + Failed;
    public bool AllSaved => Failed == 0;
}

public interface IDumpStripService
{
    /// <summary>全部潜在排土位置(按排土场 → 台阶级 → 带号排序)。</summary>
    IReadOnlyList<DumpStrip> All();

    /// <summary>某个排土场的位置清单。<paramref name="designVersion"/> = null 取全部方案。</summary>
    IReadOnlyList<DumpStrip> ByRegion(long regionId, string? designVersion = null);

    DumpStrip? Get(long id);

    long Insert(DumpStrip entity);

    void Update(DumpStrip entity);

    void Delete(long id);

    /// <summary>
    /// 用新清单【整体替换】某排土场（某方案）的位置 —— 先删该区该方案的全部旧行，再批量写入。
    /// 重切必须走这个，别用 Insert 逐条追加（见接口说明）。
    /// 返回 <see cref="DumpStripSaveReport"/>：丢行必须让调用方看得见，不能只报成功数。
    /// </summary>
    DumpStripSaveReport ReplaceForRegion(long regionId, string? designVersion, IReadOnlyList<DumpStrip> rows);
}
