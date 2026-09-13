using System.Runtime.CompilerServices;
using System.Threading;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 会话内实体 handle（原版 AcDb 实体 handle 的托管等价）：规划类方案（境界圈定的地表面/煤层顶底板/地表界、
/// 采区划分的境界来源…）只按 handle 引用图纸实体，不复制几何。首次取即分配，进程内唯一、随实体对象生命周期。
/// 不入 .pmx（原版 handle 也只在图纸内有效）；跨会话由方案库的名称/图层重新解析。
/// </summary>
public static class EntityHandles
{
    private static readonly ConditionalWeakTable<SceneEntity, StrongBox<long>> _map = new();
    private static long _next = 1000;

    /// <summary>取实体 handle（没有则分配）。</summary>
    public static long Of(SceneEntity e)
    {
        var box = _map.GetValue(e, _ => new StrongBox<long>(Interlocked.Increment(ref _next)));
        return box.Value;
    }

    /// <summary>已分配则返回 handle，否则 0（不分配）。</summary>
    public static long Peek(SceneEntity e) => _map.TryGetValue(e, out var b) ? b.Value : 0;

    /// <summary>按 handle 在场景里找实体（线性扫，规划窗口用；找不到返回 null）。</summary>
    public static SceneEntity? Find(Scene scene, long handle)
    {
        if (handle == 0) return null;
        foreach (var e in scene.Entities)
            if (_map.TryGetValue(e, out var b) && b.Value == handle) return e;
        return null;
    }
}
