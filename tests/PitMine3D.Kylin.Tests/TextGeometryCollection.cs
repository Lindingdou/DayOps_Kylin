using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 文字几何相关测试的串行集合。
///
/// 起因：GlyphFont 的字体 Provider 是**进程级静态**（产品里就该如此：一个进程一套系统字体）。
/// GlyphFontTests 为验证 TrueType 解析会临时装一份测试用最小字体，装着的这段时间里，
/// 另一个并发跑的集合若去量 TextEntity 的排版宽度，量到的就是那份假字体的步进 ——
/// 表现为 DrawToolsTests 的对齐用例偶发失败（实测踩到）。
///
/// 同一 xUnit 集合内的测试类彼此串行，故把「装假字体的」与「量文字几何的」放进同一集合即可根除；
/// 其余 200 多个测试类仍并行，全套耗时基本不变。
/// </summary>
[CollectionDefinition("TextGeometry")]
public class TextGeometryCollection { }
