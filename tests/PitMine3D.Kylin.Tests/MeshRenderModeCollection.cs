using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 三角网**全局显示档**相关测试的串行集合（同 <see cref="TextGeometryCollection"/> 的路子）。
///
/// 起因：<c>MeshEntity</c> 的显示档是**进程级静态** —— `RenderMode`(线框/着色面/面+线框)、
/// `ShadeMode`(高程/坡度/坡向/等高线/属性/PBR/贴图…)、`SmoothShading`、`PbrMetallic/Roughness`、
/// `TexScale` 等。产品里这样设是对的（一个视图一套显示档），但测试并行跑时就成了共享可变状态：
/// 某个用例把 `RenderMode` 切到线框、或把 `ShadeMode` 切到 PBR 的那一瞬，另一个并发用例去调
/// <c>TessellateFaces</c>，拿到的要么是**空缓冲**（线框档直接 return）、要么是走了材质通道的另一套颜色。
///
/// **这个集合名此前一直在用，却没有任何 <c>CollectionDefinition</c> 声明它** ——
/// 于是"改全局显示档的测试要串行"这条约定无处可查，新写的读侧用例自然想不到要加进来。
/// 2026-09-11 的实测就是这么撞上的：`BlockMeshBuilderTests.ToMesh_carriesPerVertexColoursAndBaseColour`
/// 约每三轮全套失败一次（断言"着色面应带逐顶点色"落空），单独跑必过。补上本文件把约定写明。
///
/// **该进来的两类**：① 会写上述任一全局静态的；② 会读 <c>TessellateFaces</c>/<c>TessellateFacesMat</c>
/// 结果并对颜色或顶点数作断言的。只写不读、或只用逐实体 <c>RenderModeOverride</c> 的可以不进。
/// 同一 xUnit 集合内的测试类彼此串行，其余测试类照常并行，全套耗时基本不变。
/// </summary>
[CollectionDefinition("MeshRenderMode")]
public class MeshRenderModeCollection { }
