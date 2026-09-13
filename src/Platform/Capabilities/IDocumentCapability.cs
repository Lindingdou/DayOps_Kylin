// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IDocumentCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 文档能力域：图层 / 线型 / 文档 IO 的受控访问。
    ///
    /// 设计原则：
    ///   - 替代 IEngineService 中 GetLayerNames / CreateLayer / SaveDocument 等 ~15 个方法
    ///   - 用 string[] / 强类型 record 替代 raw P/Invoke 字符串协议
    ///   - 第三方插件无需知道 plugin.json 中 "engineMethod" 的字符串名
    /// </summary>
    public interface IDocumentCapability
    {
        // ── 图层 ─────────────────────────────────────────────────
        /// <summary>所有图层名称。</summary>
        string[] GetLayerNames();

        /// <summary>创建图层；名称已存在时返回 false。</summary>
        bool CreateLayer(string name);

        /// <summary>删除图层；不存在或为当前层时返回 false。</summary>
        bool DeleteLayer(string name);

        /// <summary>设置当前层。</summary>
        bool SetCurrentLayer(string name);

        // ── 文档 IO ──────────────────────────────────────────────
        /// <summary>保存当前文档到指定路径。</summary>
        bool SaveDocument(string path);

        /// <summary>从指定路径加载文档。</summary>
        bool LoadDocument(string path);

        /// <summary>从 JSON 字符串导入实体（追加到 ModelSpace）。</summary>
        bool ImportEntitiesJson(string json);

        /// <summary>
        /// 从 PMTB 二进制载荷导入单个 TriangleMesh（追加到 ModelSpace）。
        /// 相比 <see cref="ImportEntitiesJson"/>，省去字符串编解码，几百万顶点的 mesh 推送显著加速。
        /// </summary>
        bool ImportTriangleMeshBinary(ReadOnlySpan<byte> payload);

        /// <summary>
        /// 从 PMBI 二进制载荷批量导入混合实体（Line / Polyline / Point / Text / MText / Hatch / TriangleMesh）。
        /// PMBI 是 DwgDxfImportService 用同一通道的成熟路径，header 'P','M','B','I' + 12B 头 + N 条实体记录。
        /// 比 ImportEntitiesJson 快 1-2 个数量级且零序列化崩溃风险。
        /// </summary>
        bool ImportEntitiesBinary(ReadOnlySpan<byte> payload);
    }
}
