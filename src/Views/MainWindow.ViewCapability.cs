using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Platform.Capabilities;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 原 PitMine.Platform 的 <see cref="IViewCapability"/> 在 Kylin 宿主上的实现 —— 给移植过来的 TaskLib
/// （SimHost.View：影像底图 / 三维推演请求重绘 / 着色模式）用。
///
/// <para>Kylin 的视口没有"全局着色模式 5=正射影像"这种 GPU 通道：正射底图在本仓库一直是
/// <b>把影像色采到三角网/点云顶点上</b>（Cad.OrthoBasemap.Apply）。所以 <see cref="SetOrthophoto"/>
/// 收到 BGRA + 四至后，就地建一个内存采样器逐顶点上色 —— 结果与原版"贴在面上"一致：
/// 没有三角网就贴不出东西，影像外的点是灰。</para>
/// </summary>
internal sealed class KylinViewCapability : IViewCapability
{
    private readonly MainWindow _w;
    private bool _hasOrtho;
    private int _shading = 1;

    public KylinViewCapability(MainWindow w) => _w = w;

    // ── 工具 / 命令：走主窗的命令派发（与功能区按钮同一通路）──
    public bool StartTool(string toolName) => ExecuteCommand(toolName);
    public string[] GetAvailableToolNames() => Array.Empty<string>();
    public bool ExecuteCommand(string commandLine)
    {
        try { _w.RunRibbonCommand(commandLine); return true; }
        catch { return false; }
    }
    public void CancelActiveCommand() { try { _w.RunRibbonCommand("取消"); } catch { } }

    // ── 视图状态：Kylin 视口按各自命令切换；这里只保留读写占位，不假装有对应开关 ──
    public bool IsOrthoEnabled => false;
    public void SetOrthoEnabled(bool enabled) { }
    public bool IsSnapEnabled => true;
    public void SetSnapEnabled(bool enabled) { }
    public bool Is3DViewEnabled => true;
    public void Set3DViewEnabled(bool enabled) { }
    public bool IsFillModeEnabled => true;
    public void SetFillModeEnabled(bool enabled) { }

    public int GetShadingMode() => _shading;
    public void SetShadingMode(int mode) { _shading = mode; if (mode != 5 && _hasOrtho) ClearOrthophoto(); }
    public void SetContourSpacing(float spacing) { }
    public void SetValueRange(float minV, float maxV) { }
    public void SetColormap(byte[] rgba) { }
    public void RequestRender() => _w.RequestSceneRefresh();

    // ── 正射影像：BGRA + 四至 → 内存采样器 → 逐顶点上色 ──
    public bool SetOrthophoto(byte[] bgra, int width, int height, double minX, double minY, double maxX, double maxY)
    {
        if (bgra == null || width <= 0 || height <= 0 || !(maxX > minX) || !(maxY > minY)) return false;
        if (bgra.Length < (long)width * height * 4) return false;
        var sampler = new BgraSampler(bgra, width, height, minX, minY, maxX, maxY);
        string msg = _w.ApplyOrthoSampler(sampler);
        _hasOrtho = true;
        LastMessage = msg;
        return true;
    }

    public void ClearOrthophoto()
    {
        _hasOrtho = false;
        LastMessage = _w.ClearOrthoSampler();
    }

    public bool HasOrthophoto() => _hasOrtho;
    public int GetMaxOrthophotoDimension() => 8192;

    public bool IsSurfaceCoordinateReadoutEnabled => false;
    public void SetSurfaceCoordinateReadoutEnabled(bool enabled) { }

    /// <summary>最近一次贴/清底图的结果文案（窗口状态栏用）。</summary>
    public string LastMessage { get; private set; } = "";

    /// <summary>解码后的正射影像（BGRA8 行主序）按四至采样：世界 (x,y) → 像素 → RGB。影像外返回 null。</summary>
    private sealed class BgraSampler : Cad.OrthoBasemap.ISampler
    {
        private readonly byte[] _px; private readonly int _w, _h;
        private readonly double _minX, _minY, _maxX, _maxY;
        public BgraSampler(byte[] px, int w, int h, double minX, double minY, double maxX, double maxY)
        { _px = px; _w = w; _h = h; _minX = minX; _minY = minY; _maxX = maxX; _maxY = maxY; }
        public (double minX, double minY, double maxX, double maxY) Extent => (_minX, _minY, _maxX, _maxY);
        public (byte r, byte g, byte b)? SampleRgb(double x, double y)
        {
            if (x < _minX || x > _maxX || y < _minY || y > _maxY) return null;
            int col = Math.Clamp((int)((x - _minX) / (_maxX - _minX) * _w), 0, _w - 1);
            int row = Math.Clamp((int)((_maxY - y) / (_maxY - _minY) * _h), 0, _h - 1);   // 影像行从北往南
            long o = ((long)row * _w + col) * 4;
            if (_px[o + 3] == 0) return null;   // 透明 = 采不到（解码时落在影像外的像素）
            return (_px[o + 2], _px[o + 1], _px[o]);
        }
    }
}
