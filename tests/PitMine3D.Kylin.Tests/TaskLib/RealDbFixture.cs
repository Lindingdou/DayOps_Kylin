// 移植自原 PitMine3D Tests/Shared/RealDbFixture.cs —— 契约（DbPath / Sql / Ready / Label / "RealDb" 串行集合）
// 逐行对应；库的来源改了：原版拷桌面上一份 54MB 的 pmgeo.db 真库副本，Kylin 侧 GeoDatabase 自带同一套
// 50 个迁移（含真实矿山种子数据），直接在临时目录现建一份 SQLite 即是"真库副本"，判据照旧走真实台账。
using System;
using System.IO;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Sql;
using Xunit;

namespace PitMine3D.Kylin.Tests.Shared;

/// <summary>
/// 「接真库副本」的台架夹具 —— 让判据能走<b>真实的设备/装卸点/路网台账</b>，而不是兜底值。
///
/// <para><b>为什么必须有它</b>：裸台架不初始化数据库门面，于是
/// <c>EquipmentDataContext</c> 是空的，所有走库的路径**静默退到兜底**：
/// 设备库读不出一台 → 设备指派失败 → 工序量 0；装卸点空表 → 运距用采场质心；
/// 路网不可用 → 运距全按直线×1.3。
/// 这些在报告里都写着，但读的人会以为那是矿上的真实情况。</para>
/// </summary>
public sealed class RealDbFixture : IDisposable
{
    /// <summary>真库副本永远可建（种子随程序集走）。</summary>
    public static bool RealDbAvailable => true;

    private readonly string _dir;
    private readonly GeoDatabase? _db;

    public string DbPath { get; }
    public ISqlService? Sql { get; }
    public bool Ready { get; }
    public string Label { get; } = "";

    public RealDbFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pmgeo_diag_" + Guid.NewGuid().ToString("N")[..8]);
        DbPath = Path.Combine(_dir, "pmgeo.db");
        try
        {
            Directory.CreateDirectory(_dir);
            // GeoDatabase 构造时就把 IGeoDataContext 交给了 EquipmentDataContext 静态门面（同原版 Initialize）
            _db = GeoDatabase.OpenWith(new SqliteDialect(), DbPath);
            Sql = new SqlService(_db.Connection);
            Ready = true;
            Label = $"真库副本：{DbPath}（{new FileInfo(DbPath).Length / 1024 / 1024} MB，{_db.AppliedMigrationCount()} 个迁移）";
        }
        catch (Exception ex) { Label = $"副本建不起来（{ex.GetType().Name}：{ex.Message}）"; }
    }

    public void Dispose()
    {
        try { _db?.Dispose(); } catch { }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}

/// <summary>
/// 用真库副本的判据放同一个集合 —— <b>必须串行</b>。
/// <para><see cref="EquipmentDataContext"/> 是**静态门面**：并行跑时后一个 fixture 的
/// Initialize 会把前一个的上下文顶掉，于是前一个判据读到的是另一个库的表 ——
/// 而它照样有数，只是不是自己那份。</para>
/// </summary>
[CollectionDefinition("RealDb", DisableParallelization = true)]
public sealed class RealDbCollection : ICollectionFixture<RealDbFixture> { }
