using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>TIFF-LZW 解压回归 —— 用 PIL(Pillow) 生成的 LZW 参照独立强验(非自编码器自验)。</summary>
public class TiffLzwTests
{
    [Fact]
    public void Decodes_pil_reference_lzw_stream()
    {
        // PIL 生成: 4×4 RGB, 像素 [红,绿,蓝,白]×4, tiff_lzw 压缩的条带字节
        byte[] compressed =
        {
            128, 63, 192, 16, 56, 20, 17, 255, 7, 130, 128, 33, 48,
            136, 60, 14, 21, 14, 134, 66, 226, 16, 136, 8,
        };
        byte[] expected =
        {
            255,0,0, 0,255,0, 0,0,255, 255,255,255,
            255,0,0, 0,255,0, 0,0,255, 255,255,255,
            255,0,0, 0,255,0, 0,0,255, 255,255,255,
            255,0,0, 0,255,0, 0,0,255, 255,255,255,
        };
        var got = TiffLzw.Decode(compressed, expected.Length);
        Assert.Equal(expected, got);   // 逐字节等 → LZW 变体(码宽/EarlyChange)正确
    }

    [Fact]
    public void Horizontal_predictor_undo()
    {
        // 一行 2 像素 RGB, 差分编码 [R0,G0,B0, dR,dG,dB]; 撤销后 = 累加
        byte[] data = { 10, 20, 30, 5, 6, 7 };   // 像素1=(10,20,30), 差分=(5,6,7)
        TiffLzw.UndoHorizontalPredictor(data, width: 2, height: 1, samplesPerPixel: 3);
        Assert.Equal(new byte[] { 10, 20, 30, 15, 26, 37 }, data);   // 像素2 = 像素1 + 差分
    }

    [Fact]
    public void Empty_input_safe()
    {
        Assert.Empty(TiffLzw.Decode(new byte[0]));
    }
}
