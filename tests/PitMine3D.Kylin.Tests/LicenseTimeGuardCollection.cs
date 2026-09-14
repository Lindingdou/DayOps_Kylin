using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 授权时间基准相关测试的串行集合（同 <see cref="TextGeometryCollection"/> /
/// <see cref="MeshRenderModeCollection"/> 的路子）。
///
/// <c>TimeGuard</c> 的 <c>_effectiveNow</c> / <c>_highWater</c> / <c>_rollbackSeen</c> 是**进程级静态** ——
/// 产品里就该如此（一个进程一套时间基准），但测试要验"高水位比时钟晚时以高水位为准""回拨检测"
/// 这些行为就必须去写它。并行跑时两个用例互相踩，断言必然飘。
///
/// 建集合而不是想办法消灭 static：判据见 [[test-flake-shared-static]] ——
/// 产品里本该全局的，就把相关测试类归入同一集合串行。
/// </summary>
[CollectionDefinition("LicenseTimeGuard")]
public class LicenseTimeGuardCollection { }
