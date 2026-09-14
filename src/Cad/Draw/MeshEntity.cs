using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 三角网实体（原 MeshEditLib 的 MeshData / 内核 MeshEntity 的托管等价）：顶点(x,y,z) + 三角索引，
/// 作为场景一等对象参与 渲染(唯一边线, 逐顶点真高程)/拾取/选择/图层/隐藏/存档/特性，
/// 供 建模·编辑·剖面·估值·块体 等三维地质建模功能直接以「选中的三角网」为输入输出，不再经 OFF 文件中转。
/// </summary>
public sealed class MeshEntity : SceneEntity
{
    public string Name = "三角网";
    public List<(double x, double y, double z)> Verts = new();
    public List<(int a, int b, int c)> Tris = new();

    /// <summary>
    /// 逐顶点颜色（可空；非空且个数与 Verts 一致时，着色面按顶点色而非实体基色 Cr/Cg/Cb）。
    /// 块体模型这类「一张网格里每块颜色不同」的情形需要它 —— 否则只能按颜色拆成成百上千个实体。
    /// </summary>
    public List<(float r, float g, float b)>? VertColors;

    /// <summary>
    /// 逐顶点真实色（建面时从带 RGB 的点云带过来）。<see cref="VertColors"/> 是"当前显示用的色"，
    /// 会被高程/坡度等着色覆盖；这一份留着，「三角网着色 → 点云真实色」才有得可恢复。
    /// </summary>
    public List<(float r, float g, float b)>? RgbColors;

    /// <summary>
    /// 逐顶点法线（可空；非空且个数与 Verts 一致时，着色面按顶点法线打光，GPU 再插值）。
    /// 给的是**曲面的真法线**而不是三角面法线：圆柱(钻孔柱)这类由多边形逼近的曲面若按面法线打光，
    /// 放大后就是一条条竖直亮暗棱条 —— 看着是棱柱不是圆柱。平面网格(地形/台阶)不必给，留空即可。
    /// </summary>
    public List<(double x, double y, double z)>? VertNormals;

    public bool HasRgbColors => RgbColors != null && RgbColors.Count == Verts.Count;

    private bool HasVertColors => VertColors != null && VertColors.Count == Verts.Count;

    private bool HasVertNormals => VertNormals != null && VertNormals.Count == Verts.Count;

    private List<(int i, int j)>? _edges;

    /// <summary>
    /// 显式边线集合（null = 由三角边推导）。
    /// 块体这种「四边形面被切成两个三角」的网格必须给它：按三角边画会把每个面的**对角线**也画出来，
    /// 看着像斜线交叉网，而不是一格一格的立方体棱。
    /// </summary>
    public List<(int i, int j)>? EdgeOverride;
    private double _minX, _minY, _maxX, _maxY, _minZ, _maxZ;
    private bool _boundsOk;

    public MeshEntity() { Cr = 0.55f; Cg = 0.75f; Cb = 0.85f; }

    public MeshEntity(string name, IEnumerable<(double x, double y, double z)> verts, IEnumerable<(int a, int b, int c)> tris) : this()
    {
        Name = name;
        Verts.AddRange(verts);
        Tris.AddRange(tris);
    }

    /// <summary>顶点/三角改动后调用，重建边集与包围盒缓存。</summary>
    public void Invalidate() { _edges = null; _boundsOk = false; _edgeCache = null; _faceCache = null; _matCache = null; _hlCache = null; }

    /// <summary>唯一无向边(i&lt;j)，按三角遍历去重。</summary>
    public IReadOnlyList<(int i, int j)> Edges
    {
        get
        {
            if (EdgeOverride != null) return EdgeOverride;
            if (_edges != null) return _edges;
            // 预留容量: 三角网的唯一边约为三角数的 1.5 倍。近 20 万三角形的地形网上这一步曾要好几秒
            // (选中/线框首次触发时表现为卡顿)——真凶不是扩容而是 long 默认散列在网格边上撞成链表, 见 PackedKeyComparer。
            var set = new HashSet<long>(Tris.Count * 2, PackedKeyComparer.Instance);
            var list = new List<(int, int)>(Tris.Count * 2);
            void Add(int a, int b)
            {
                if (a == b || a < 0 || b < 0 || a >= Verts.Count || b >= Verts.Count) return;
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                if (set.Add(((long)lo << 32) | (uint)hi)) list.Add((lo, hi));
            }
            foreach (var (a, b, c) in Tris) { Add(a, b); Add(b, c); Add(c, a); }
            _edges = list;
            return list;
        }
    }

    public (double minX, double minY, double maxX, double maxY, double minZ, double maxZ) Bounds
    {
        get
        {
            if (!_boundsOk)
            {
                _minX = _minY = _minZ = double.MaxValue; _maxX = _maxY = _maxZ = double.MinValue;
                foreach (var (x, y, z) in Verts)
                {
                    if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
                    if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
                    if (z < _minZ) _minZ = z; if (z > _maxZ) _maxZ = z;
                }
                if (Verts.Count == 0) _minX = _minY = _minZ = _maxX = _maxY = _maxZ = 0;
                _boundsOk = true;
            }
            return (_minX, _minY, _maxX, _maxY, _minZ, _maxZ);
        }
    }

    /// <summary>边表是否已构建(自检用: 确认拾取路径不再触发大网边表构建)。</summary>
    public bool HasEdgeCache => _edges != null;

    public int VertexCount => Verts.Count;
    public int TriangleCount => Tris.Count;

    /// <summary>扁平副本(x,y,z…)与(a,b,c…)，供以 double[]/int[] 为参数的算法(BenchFaceExtractor/SurfaceUpdate/MeshVoxelizer…)。</summary>
    public (double[] verts, int[] tris) Flatten()
    {
        var v = new double[Verts.Count * 3]; var t = new int[Tris.Count * 3];
        for (int i = 0; i < Verts.Count; i++) { v[i * 3] = Verts[i].x; v[i * 3 + 1] = Verts[i].y; v[i * 3 + 2] = Verts[i].z; }
        for (int i = 0; i < Tris.Count; i++) { t[i * 3] = Tris[i].a; t[i * 3 + 1] = Tris[i].b; t[i * 3 + 2] = Tris[i].c; }
        return (v, t);
    }

    // ── 显示模式(全局, 原「渲染配置」实体/线框着色管线)：线框 / 着色面 / 着色面+线框 ──
    public enum DisplayMode { Wireframe, Shaded, ShadedWireframe }
    public static DisplayMode RenderMode = DisplayMode.Shaded;   // 默认直接显示面(用户 2026-09-07: 默认不显示网格线)

    /// <summary>
    /// 逐实体显示模式覆盖（null = 跟随全局 <see cref="RenderMode"/>）。
    /// 块体模型需要它：一堆同色立方体只画面就糊成一整块，看不出块界，原版是恒给每块描边的。
    /// </summary>
    public DisplayMode? RenderModeOverride;

    /// <summary>本实体的显示模式（覆盖优先）。</summary>
    public DisplayMode EffectiveMode => RenderModeOverride ?? RenderMode;

    /// <summary>
    /// 固定边色（null = 由填充色推导）。对应原版 BlockEdgeMode：
    /// AutoFromFill 由填充色压暗得到，Fixed 用这里给的色。
    /// </summary>
    public (float r, float g, float b)? EdgeColorFixed;
    /// <summary>
    /// 全局面着色模式（原版视口级 ShadingMode：对场景内所有三角网生效、只改显示不出数值）：
    /// 实体色 / 高程色带 / 按坡度 / 按坡向 / 等高线 / 属性分级。
    /// 「坡度着色」「坡向着色」两个按钮切的是它，「渲染配置」窗体整档都落在这里。
    /// 档位对齐原 Lit.hlsl 的 ShadingMode：Entity=0/1(平面/平滑由 <see cref="SmoothShading"/> 分)、
    /// Contour=10、Slope=11、Aspect=12、Attribute=14；Elevation 是属性分级套死地形色带的老档，留着不动。
    /// </summary>
    public enum FaceShade { Entity, Elevation, Slope, Aspect, Contour, Attribute, Textured, Pbr }

    /// <summary>当前全局面着色模式。</summary>
    public static FaceShade ShadeMode = FaceShade.Entity;

    /// <summary>平滑着色(原 ShadingMode.Smooth=1)：带顶点法线的网按顶点法线打光；关掉即原 Flat=0 恒用面法线。</summary>
    public static bool SmoothShading = true;

    /// <summary>等高线档的等高距(世界单位)，同原 LightConstants.ContourSpacing(默认 5)。</summary>
    public static double ContourSpacing = 5.0;

    /// <summary>属性分级区间：自动 = 每张网按自身高程铺满色带(同原版 NaN 哨兵)；否则用下面两个绝对高程。</summary>
    public static bool AttrAutoRange = true;
    public static double AttrMin = 0, AttrMax = 100;

    /// <summary>属性分级用色带(同原 ColormapLUT)与反转开关。</summary>
    public static (byte r, byte g, byte b)[] AttrColormap = Colormap.Viridis;
    public static bool AttrReverse;

    /// <summary>
    /// 着色参数版本号：等高距/色带/区间/逐对象覆盖 一改就 ++。
    /// 镶嵌缓存拿它作键的一部分 —— 不带它，大网改完参数会命中旧缓存，看着就是「设了不生效」。
    /// </summary>
    public static int ShadeEpoch;

    /// <summary>改完上面任一全局着色参数调它，随后 RefreshScene 即按新参数重建镶嵌。</summary>
    public static void BumpShade() => ShadeEpoch++;

    /// <summary>
    /// 逐对象独立着色（原 <c>PitMine_SetEntityFaceRender</c> 的逐面覆盖）：选中对象改档只套到它，
    /// 色带与区间随档带走 —— 同屏才能一张网按属性色带、另一张按坡度。null = 跟随全局。
    /// </summary>
    public sealed record FaceRenderOverride(
        FaceShade Shade, bool AutoRange, double VMin, double VMax,
        (byte r, byte g, byte b)[] Lut, bool Reverse);

    private FaceRenderOverride? _faceRender;

    /// <summary>本实体的独立着色（null = 跟随全局 <see cref="ShadeMode"/>）。赋值即推进 <see cref="ShadeEpoch"/>。</summary>
    public FaceRenderOverride? FaceRender
    {
        get => _faceRender;
        set { _faceRender = value; ShadeEpoch++; }
    }

    /// <summary>本实体实际生效的面着色档（逐对象覆盖优先）。</summary>
    public FaceShade EffectiveShade => _faceRender?.Shade ?? ShadeMode;

    // ── 材质 / 贴图 / 透明度（原「渲染配置」后三页；对应 Lit.hlsl 的 Metallic / Roughness / TexScale / ObjectColor.a）──

    /// <summary>全局 PBR 金属度 / 粗糙度（原版「材质」页上半部分：整场景一套）。</summary>
    public static double PbrMetallic = 0.0, PbrRoughness = 0.5;

    /// <summary>贴图平铺尺度：每张贴图覆盖多少米（原 LightConstants.TexScale，默认 50）。</summary>
    public static double TexScale = 50.0;

    /// <summary>
    /// 逐实体材质覆盖（原版「材质」页下半部分 + 地学材质预设）：null = 跟随全局。
    /// 与 <see cref="FaceRender"/> 分开存：那条管"这张网用哪个着色档"，这条管"PBR 档下它多金属/多粗糙"。
    /// </summary>
    public double? Metallic, Roughness;

    /// <summary>本实体实际生效的金属度 / 粗糙度。</summary>
    public double EffMetallic => Math.Clamp(Metallic ?? PbrMetallic, 0, 1);
    public double EffRoughness => Math.Clamp(Roughness ?? PbrRoughness, 0.04, 1);

    /// <summary>
    /// 所在图层的透明度（0..90 百分比），由 <see cref="Scene.SyncLayerProps"/> 在每次建面前下发。
    /// 不入存档：它是图层表的属性，存在 LayerDto 那边；这里只是「随层」解析的缓存。
    /// </summary>
    public short LayerTransp;

    /// <summary>
    /// 本实体的不透明度 0~1 —— 由既有的「透明度」特性来（-1 随层 / 0 不透明 / 1..90 百分比），
    /// 不另立字段：原版「透明度」页改的就是实体这条特性，存档/特性面板/导出一路都已经在走它。
    /// 特性为 -1(随层) 时取 <see cref="LayerTransp"/> —— 这才是「图层特性管理器」那一列透明度的落点。
    /// </summary>
    public float EffAlpha
    {
        get
        {
            int t = Transparency < 0 ? LayerTransp : Transparency;
            return t <= 0 ? 1f : 1f - Math.Clamp(t, 0, 99) / 100f;
        }
    }

    /// <summary>
    /// 这张网要不要走**材质通道**（单独的 GL 程序：法线 + 逐片元 PBR/三平面贴图 + alpha 混合）。
    /// 普通面仍走便宜的 P3_C3 通道 —— 大图纸上多数网都是普通面，没必要为它们多传一条法线。
    /// </summary>
    public bool UsesMaterialPass
        => EffectiveMode != DisplayMode.Wireframe
           && (EffectiveShade is FaceShade.Textured or FaceShade.Pbr || EffAlpha < 0.999f);

    /// <summary>材质通道的档号（同 GlRenderer.MatPlain/MatTextured/MatPbr，也同原 Lit.hlsl 的 ShadingMode）。</summary>
    public int MaterialMode => EffectiveShade switch
    {
        FaceShade.Textured => 4,
        FaceShade.Pbr => 6,
        _ => 0,   // 只为透明而走材质通道: 顶点色照常在 CPU 打好光, shader 只叠 alpha
    };

    /// <summary>面按高程着色(地形色带), 否则按实体颜色。等价于 <see cref="ShadeMode"/> 的高程档。</summary>
    public static bool ColorByElevation
    {
        get => ShadeMode == FaceShade.Elevation;
        set
        {
            if (value) ShadeMode = FaceShade.Elevation;
            else if (ShadeMode == FaceShade.Elevation) ShadeMode = FaceShade.Entity;
        }
    }
    /// <summary>平行光方向(世界系, 单位化)；两面受光。</summary>
    private static readonly (double x, double y, double z) Light = Normalize((0.35, 0.25, 0.9));
    private static (double x, double y, double z) Normalize((double x, double y, double z) v)
    { double l = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z); return l < 1e-12 ? (0, 0, 1) : (v.x / l, v.y / l, v.z / l); }

    /// <summary>边线(不看显示模式, 供选择高亮/导出)。</summary>
    public void TessellateEdges(List<float> o)
    {
        foreach (var (i, j) in Edges)
        {
            var p = Verts[i]; var q = Verts[j];
            Seg3(o, p.x, p.y, p.z + Elevation, q.x, q.y, q.z + Elevation);
        }
    }

    /// <summary>
    /// 忠实原版 AcDbTriangleMesh::previewCost —— 索引数 ×2。必须 O(1)，
    /// 所以按三角数算而不去碰 <see cref="Edges"/>(那个 getter 会建整张边表)。
    /// </summary>
    public override int PreviewCost() => EdgeOverride != null ? EdgeOverride.Count * 2 : Tris.Count * 6;

    /// <summary>
    /// 拖拽幽灵一律走边线：纯着色面档的 <see cref="Tessellate"/> 直接 return(见其注释)，
    /// 照搬过来就是"拖着一张网走，屏幕上什么都没有"。
    /// </summary>
    public override void TessellatePreview(List<float> o) => TessellateEdges(o);

    public override (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)? PreviewAabb()
    {
        if (Verts.Count == 0) return null;
        var b = Bounds;
        return (b.minX, b.minY, b.minZ + Elevation, b.maxX, b.maxY, b.maxZ + Elevation);
    }

    /// <summary>
    /// 拖拽预览的抽稀替身。忠实原版 AcDbTriangleMesh::buildPreviewProxy：
    /// 把包围盒的 XY 面切成 M×M 格，每格挑一个【真实顶点】当代表(取该格内 z 最大的那个 ——
    /// 对地形＝上表面，对封闭体＝俯视看得见的那层，与用户所见一致)，再把相邻格的代表点连起来。
    /// 连出来的是一张贴着面走的粗网：格网还是格网，只是稀了；每个点都真的在这张网上。
    /// 边数上界 2·M·(M−1) &lt; 2M²，取 M = √(maxSegments/2) 即保证不超预算。
    /// 完全退化(顶点挤在一格 / 连不出边)时退回包围盒，不交白卷。
    /// </summary>
    public override void BuildPreviewProxy(List<(double x, double y, double z)> o, int maxSegments)
    {
        int vertCount = Verts.Count;
        if (vertCount == 0 || Tris.Count < 1 || maxSegments < 12) { base.BuildPreviewProxy(o, maxSegments); return; }
        var bb = Bounds;
        double spanX = bb.maxX - bb.minX, spanY = bb.maxY - bb.minY;

        // 格数: 受预算约束, 也受顶点数约束 —— 顶点比格子还少时大半格是空的, 连不出边, 白把网切碎。
        int M = (int)Math.Sqrt(maxSegments / 2.0);
        int byVerts = (int)Math.Sqrt((double)vertCount);
        if (M > byVerts) M = byVerts;
        if (M < 2) M = 2;
        if (M > 256) M = 256;

        var cell = new int[M * M];
        for (int i = 0; i < cell.Length; i++) cell[i] = -1;
        double invX = spanX > 1e-12 ? M / spanX : 0.0;
        double invY = spanY > 1e-12 ? M / spanY : 0.0;
        int last = M - 1;
        for (int i = 0; i < vertCount; i++)
        {
            var v = Verts[i];
            int ix = invX > 0 ? (int)((v.x - bb.minX) * invX) : 0;
            int iy = invY > 0 ? (int)((v.y - bb.minY) * invY) : 0;
            ix = ix < 0 ? 0 : (ix > last ? last : ix);   // 上边界那一点会算出 M, 钳回来
            iy = iy < 0 ? 0 : (iy > last ? last : iy);
            int c = iy * M + ix;
            if (cell[c] < 0 || v.z > Verts[cell[c]].z) cell[c] = i;
        }

        (double x, double y, double z) W(int i) { var v = Verts[i]; return (v.x, v.y, v.z + Elevation); }
        int before = o.Count;
        for (int iy = 0; iy < M; iy++)
            for (int ix = 0; ix < M; ix++)
            {
                int here = cell[iy * M + ix];
                if (here < 0) continue;
                var p = W(here);
                if (ix + 1 < M) { int rt = cell[iy * M + ix + 1]; if (rt >= 0) { o.Add(p); o.Add(W(rt)); } }
                if (iy + 1 < M) { int up = cell[(iy + 1) * M + ix]; if (up >= 0) { o.Add(p); o.Add(W(up)); } }
            }

        // 一条边都连不出来(所有顶点挤在同一格, 如极小网格 / 纯竖直的一线墙) → 退包围盒
        if (o.Count == before) base.BuildPreviewProxy(o, maxSegments);
    }

    /// <summary>
    /// 边线，逐边取色：固定边色 &gt; 逐顶点色压暗 &gt; 实体基色压暗。
    /// 忠实原版 Voxel.hlsl 的 AutoFromFill（edgeColor = litColor·0.55）与 Fixed 两种边色口径 ——
    /// 块体一张网里每块颜色不同，边线不能统一用实体基色，否则块界仍看不清。
    /// </summary>
    private void TessellateEdgesColored(List<float> o, float dim)
    {
        bool perVert = HasVertColors;
        var fix = EdgeColorFixed;
        foreach (var (i, j) in Edges)
        {
            var p = Verts[i]; var q = Verts[j];
            float r, g, b;
            if (fix is { } f) { r = f.r; g = f.g; b = f.b; }
            else if (perVert) { var c = VertColors![i]; r = c.r * dim; g = c.g * dim; b = c.b * dim; }
            else { r = Cr * dim; g = Cg * dim; b = Cb * dim; }
            double ox = RenderOrigin.X, oy = RenderOrigin.Y;   // 先减原点再转 float, 见 RenderOrigin
            o.Add((float)(p.x - ox)); o.Add((float)(p.y - oy)); o.Add((float)(p.z + Elevation)); o.Add(r); o.Add(g); o.Add(b);
            o.Add((float)(q.x - ox)); o.Add((float)(q.y - oy)); o.Add((float)(q.z + Elevation)); o.Add(r); o.Add(g); o.Add(b);
        }
    }

    // 镶嵌缓存：大网(数万三角)每次 RefreshScene 都重算边线/着色面太慢；按 (模式, 高程着色, 颜色, 标高) 键缓存, 几何改动 Invalidate() 清。
    private float[]? _edgeCache, _faceCache, _matCache;
    private (DisplayMode mode, FaceShade shade, float r, float g, float b, double elev, int epoch) _edgeKey;
    // 面/材质面的键多带一个 alpha: 图层透明度一改, 同一张网的顶点没变但该走哪条通道/什么不透明度变了,
    // 不带它就会命中旧缓存 —— 用户看到的就是"图层特性管理器里改了透明度不生效"。
    private (DisplayMode mode, FaceShade shade, float r, float g, float b, double elev, int epoch, float alpha) _faceKey, _matKey;

    public override void Tessellate(List<float> o)
    {
        var mode = EffectiveMode;
        var shade = EffectiveShade;
        // 等高线档要在面上叠一层深色等值线。原版逐像素画得出来，托管侧只有 P3_C3 顶点色管线，
        // 只能出真几何走边线通道 —— 所以纯着色面模式这里不能直接 return，否则等高线永远不出现。
        bool contour = shade == FaceShade.Contour;
        if (mode == DisplayMode.Shaded && !contour) return;   // 纯着色面模式不画边线
        var key = (mode, shade, Cr, Cg, Cb, Elevation, ShadeEpoch);
        if (_edgeCache == null || _edgeKey != key)
        {
            var tmp = new List<float>(Edges.Count * 12);
            if (mode != DisplayMode.Shaded)
                // 面+线框：边线压暗, 与着色面区分（逐顶点色的网格逐边取自己的色）
                TessellateEdgesColored(tmp, mode == DisplayMode.ShadedWireframe ? 0.45f : 1f);
            if (contour) AppendContourLines(tmp);
            _edgeCache = tmp.ToArray(); _edgeKey = key;
        }
        o.AddRange(_edgeCache);
    }

    /// <summary>
    /// 等高线档(原 Lit.hlsl mode 10)：面照常打光，另在 Z = k·等高距 处叠深色等值线(色同 shader 的 0.08/0.08/0.10)。
    /// 逐三角 marching：只遍历该三角 Z 跨度内的层位，与三条边求交连一段。
    /// 不复用 <see cref="ContourEngine"/>：那条是「层位 × 全网三角」外循环，大网上要多花层数倍的时间，
    /// 而这里只要线段，不需要连链/简化/圆滑。
    /// </summary>
    private void AppendContourLines(List<float> o)
    {
        double s = ContourSpacing > 1e-3 ? ContourSpacing : 5.0;
        double ox = RenderOrigin.X, oy = RenderOrigin.Y;
        var cx = new double[3]; var cy = new double[3];
        foreach (var (ia, ib, ic) in Tris)
        {
            if (ia >= Verts.Count || ib >= Verts.Count || ic >= Verts.Count) continue;
            var p = Verts[ia]; var q = Verts[ib]; var r = Verts[ic];
            double zmin = Math.Min(p.z, Math.Min(q.z, r.z)), zmax = Math.Max(p.z, Math.Max(q.z, r.z));
            long k0 = (long)Math.Ceiling(zmin / s), k1 = (long)Math.Floor(zmax / s);
            if (k1 < k0 || k1 - k0 > 4096) continue;   // 4096: 等高距填得比网还小时的防爆闸
            for (long k = k0; k <= k1; k++)
            {
                double lv = k * s;
                int n = 0;
                n = Cross(p, q, lv, cx, cy, n);
                n = Cross(q, r, lv, cx, cy, n);
                n = Cross(r, p, lv, cx, cy, n);
                if (n != 2) continue;
                float zz = (float)(lv + Elevation);
                o.Add((float)(cx[0] - ox)); o.Add((float)(cy[0] - oy)); o.Add(zz); o.Add(0.08f); o.Add(0.08f); o.Add(0.10f);
                o.Add((float)(cx[1] - ox)); o.Add((float)(cy[1] - oy)); o.Add(zz); o.Add(0.08f); o.Add(0.08f); o.Add(0.10f);
            }
        }
    }

    /// <summary>三角一条边与层位 lv 求交，交点写进 cx/cy 并返回新的交点数。</summary>
    private static int Cross((double x, double y, double z) a, (double x, double y, double z) b,
                             double lv, double[] cx, double[] cy, int n)
    {
        // 半开判号(0 算正侧)：顶点恰落在层位上时，共用它的两条边只算一次，否则一个三角出三个交点。
        double za = a.z - lv, zb = b.z - lv;
        if ((za < 0 && zb < 0) || (za >= 0 && zb >= 0)) return n;
        double t = za / (za - zb);
        if (n < cx.Length) { cx[n] = a.x + (b.x - a.x) * t; cy[n] = a.y + (b.y - a.y) * t; }
        return n + 1;
    }

    /// <summary>着色三角面：逐三角平面法线 × 平行光(两面受光) 调制基色(或高程色带)。</summary>
    public override void TessellateFaces(List<float> o)
    {
        if (EffectiveMode == DisplayMode.Wireframe) return;
        if (UsesMaterialPass) return;   // 交给材质通道画; 两边都画就是同一张网叠两遍(半透会变实)
        var key = (EffectiveMode, EffectiveShade, Cr, Cg, Cb, Elevation, ShadeEpoch, EffAlpha);
        if (_faceCache != null && _faceKey == key) { o.AddRange(_faceCache); return; }
        // 逐顶点色不进 key（改色一律走 Invalidate 重建），只在这里选取色源
        var tmp = new List<float>(Tris.Count * 18);
        BuildFaces(tmp, withNormals: false);
        _faceCache = tmp.ToArray(); _faceKey = key;
        o.AddRange(_faceCache);
    }

    /// <summary>
    /// 材质通道的三角面（P3_C3_N3，9 float/顶点）：多带一条**面法线**，供 shader 逐片元算
    /// PBR 光照 / 三平面贴图投影。贴图与 PBR 档下顶点色给的是**原色(albedo)**、不预乘光照 ——
    /// 否则 shader 再打一次光就是"上了两遍釉"。
    /// </summary>
    public void TessellateFacesMat(List<float> o)
    {
        if (!UsesMaterialPass) return;
        var key = (EffectiveMode, EffectiveShade, Cr, Cg, Cb, Elevation, ShadeEpoch, EffAlpha);
        if (_matCache != null && _matKey == key) { o.AddRange(_matCache); return; }
        var tmp = new List<float>(Tris.Count * 27);
        BuildFaces(tmp, withNormals: true);
        _matCache = tmp.ToArray(); _matKey = key;
        o.AddRange(_matCache);
    }

    /// <summary>
    /// 逐顶点法线的光照系数：环绕漫反射 (0.5 + 0.5·N·L)，背光侧仍有梯度。
    /// 这里**不能**用面法线那条 |N·L| —— 取绝对值会让圆柱正反两侧各出一条亮带、正中一条暗带，
    /// 越放大越像被压扁的棱柱；曲面法线朝外可靠，按有向点积一圈单调过渡才是圆柱该有的样子。
    /// </summary>
    private float VertLight(int vi)
    {
        var n = Normalize(VertNormals![vi]);
        double d = n.x * Light.x + n.y * Light.y + n.z * Light.z;
        return (float)(0.42 + 0.58 * (0.5 + 0.5 * d));
    }

    private void BuildFaces(List<float> o, bool withNormals)
    {
        var shade = EffectiveShade;
        bool rawAlbedo = shade is FaceShade.Textured or FaceShade.Pbr;   // 光交给 shader 打, CPU 只给原色
        var ov = _faceRender;
        var stops = ov?.Lut ?? AttrColormap;
        bool reverse = ov?.Reverse ?? AttrReverse;
        bool autoRange = ov?.AutoRange ?? AttrAutoRange;
        var b = Bounds; double zr = b.maxZ - b.minZ;
        // 属性分级区间：自动 = 按本网自身高程铺满色带(原版 NaN 哨兵的托管等价)；否则用窗体填的绝对高程。
        // 写死区间碰上 Z≈千米的地形会整片钳到色带一端(全红)——原版注释点名的就是这个坑。
        double lo = autoRange ? b.minZ + Elevation : (ov?.VMin ?? AttrMin);
        double hi = autoRange ? b.maxZ + Elevation : (ov?.VMax ?? AttrMax);
        if (hi - lo < 1e-4) hi = lo + 1.0;   // 同高退化区间兜底(shader 侧是 max(range, 0.001))
        bool perVert = HasVertColors;
        // 曲面(圆柱)自带真法线: 光照逐顶点算, 由 GPU 插值 → 侧面连续无棱条。
        // 平面着色(原 Flat=0)恒用面法线, 平滑着色(Smooth=1)才认顶点法线 —— 这是原版两档的唯一分别。
        bool perVertN = HasVertNormals && SmoothShading;
        foreach (var (a, bb, c) in Tris)
        {
            if (a >= Verts.Count || bb >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[bb]; var r = Verts[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            var n = Normalize((uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx));
            double lambert = Math.Abs(n.x * Light.x + n.y * Light.y + n.z * Light.z);
            float k = (float)(0.42 + 0.58 * lambert);
            double nz = Math.Clamp(Math.Abs(n.z), 0, 1);
            // 坡度/坡向是**面**属性(由三角面法向来), 三个顶点同色; 高程/属性分级/逐顶点色才逐顶点取。
            // 这三档的取色与打光口径直接照搬原 Lit.hlsl，不再叠通用平行光 —— 它们要的是"色即量值",
            // 再乘一次 lambert 会让同坡度的面因朝向不同而变色, 色带就读不出量了。
            (float r, float g, float b)? faceColor = null;
            float faceK = k;
            bool geoShade = true;
            if (shade == FaceShade.Slope)
            {
                faceColor = SlopeRamp(Math.Acos(nz) * 180.0 / Math.PI);
                faceK = (float)(0.85 + 0.15 * nz);      // shader: c *= 0.85 + 0.15*saturate(N.z)
            }
            else if (shade == FaceShade.Aspect)
            {
                faceColor = AspectColor(n.x, n.y, n.z);
                faceK = 1f;                             // shader 直接返回色环色, 不叠光
            }
            else if (shade == FaceShade.Attribute)
                faceK = (float)(0.7 + 0.3 * nz);        // shader: shade = 0.7 + 0.3*saturate(|N.z|)
            else
                geoShade = false;

            void V((double x, double y, double z) w, int vi)
            {
                float kv = rawAlbedo ? 1f : (geoShade ? faceK : (perVertN ? VertLight(vi) : k));
                float cr = Cr, cg = Cg, cb = Cb;
                if (perVert) (cr, cg, cb) = VertColors![vi];
                if (shade == FaceShade.Elevation) { var t = zr > 1e-9 ? (w.z - b.minZ) / zr : 0.5; (cr, cg, cb) = TerrainRamp(t); }
                else if (shade == FaceShade.Attribute) (cr, cg, cb) = AttributeColor(w.z + Elevation, lo, hi, stops, reverse);
                else if (faceColor is { } fc) (cr, cg, cb) = fc;
                o.Add((float)(w.x - RenderOrigin.X)); o.Add((float)(w.y - RenderOrigin.Y)); o.Add((float)(w.z + Elevation));
                o.Add(Math.Min(1f, cr * kv)); o.Add(Math.Min(1f, cg * kv)); o.Add(Math.Min(1f, cb * kv));
                if (!withNormals) return;
                // 法线：曲面(圆柱等)给顶点法线好让 GPU 插值, 平面网给面法线
                var vn = perVertN ? Normalize(VertNormals![vi]) : n;
                o.Add((float)vn.x); o.Add((float)vn.y); o.Add((float)vn.z);
            }
            V(p, a); V(q, bb); V(r, c);
        }
    }

    /// <summary>
    /// 属性分级取色(原 Lit.hlsl mode 14)：绝对高程 z 归一化到 [lo,hi] → 采样色带(可反转)。
    /// </summary>
    public static (float r, float g, float b) AttributeColor(
        double z, double lo, double hi, (byte r, byte g, byte b)[] stops, bool reverse)
    {
        double range = Math.Max(hi - lo, 1e-3);         // 同 shader 的 max(range, 0.001)
        double t = Math.Clamp((z - lo) / range, 0, 1);
        if (reverse) t = 1 - t;
        var c = Colormap.Sample(stops, t);
        return (c.r / 255f, c.g / 255f, c.b / 255f);
    }

    /// <summary>
    /// 坡度色带 —— 逐档对齐原 Lit.hlsl mode 11：0°蓝 → 22.5°绿 → 45°黄 → 67.5°橙 → 90°红。
    /// 原先是 0~70° 四档(没有橙档、70° 就到顶)，帮坡一过 70° 全糊成红，分不出 70° 与 85°。
    /// </summary>
    public static (float r, float g, float b) SlopeRamp(double slopeDeg)
    {
        (float r, float g, float b) Lerp((float r, float g, float b) a, (float r, float g, float b) b2, double t)
        {
            float f = (float)Math.Clamp(t, 0, 1);
            return (a.r + (b2.r - a.r) * f, a.g + (b2.g - a.g) * f, a.b + (b2.b - a.b) * f);
        }
        (float, float, float) blue = (0.10f, 0.30f, 1.00f), green = (0.10f, 0.85f, 0.20f);
        (float, float, float) yellow = (1.00f, 0.95f, 0.10f), orange = (1.00f, 0.55f, 0.10f), red = (0.90f, 0.10f, 0.10f);
        if (slopeDeg < 22.5) return Lerp(blue, green, slopeDeg / 22.5);
        if (slopeDeg < 45.0) return Lerp(green, yellow, (slopeDeg - 22.5) / 22.5);
        if (slopeDeg < 67.5) return Lerp(yellow, orange, (slopeDeg - 45.0) / 22.5);
        return Lerp(orange, red, (slopeDeg - 67.5) / 22.5);
    }

    /// <summary>
    /// 坡向色带 —— 对齐原 Lit.hlsl mode 12：hue = (atan2(Ny,Nx)+π)/2π 的 HSV 色环(S=0.85, V=0.95)。
    /// 早前按 atan2(Nx,-Ny) 起算、S=0.75，整环相对原版转了 90° 且偏淡，同一坡面两边颜色对不上。
    /// </summary>
    public static (float r, float g, float b) AspectRamp(double azDeg)
    {
        double h = ((azDeg % 360) + 360) % 360;
        return HsvToRgb(h, 0.85, 0.95);
    }

    /// <summary>
    /// 按三角面法向取坡向色：色环之外还照原版把平坦处按 pow(|Nz|,8) 淡入灰 ——
    /// 平地本来就没有坡向，不淡成灰就会满屏乱色(平台上的噪声法向全被染成饱和色)。
    /// </summary>
    public static (float r, float g, float b) AspectColor(double nx, double ny, double nz)
    {
        double hue = (Math.Atan2(ny, nx) + Math.PI) / (2 * Math.PI);
        var (r, g, b) = AspectRamp(hue * 360.0);
        double flat = Math.Pow(Math.Clamp(Math.Abs(nz), 0, 1), 8.0);
        return ((float)(r + (0.70 - r) * flat), (float)(g + (0.70 - g) * flat), (float)(b + (0.70 - b) * flat));
    }

    private static (float r, float g, float b) HsvToRgb(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60.0) % 2 - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return ((float)(r + m), (float)(g + m), (float)(b + m));
    }

    /// <summary>地形色带：低=绿 → 黄 → 棕 → 高=白。</summary>
    public static (float r, float g, float b) TerrainRamp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        (float, float, float)[] stops = { (0.20f, 0.55f, 0.25f), (0.85f, 0.85f, 0.35f), (0.65f, 0.45f, 0.25f), (0.95f, 0.95f, 0.95f) };
        double s = t * (stops.Length - 1); int i = Math.Min(stops.Length - 2, (int)Math.Floor(s)); float f = (float)(s - i);
        var (r0, g0, b0) = stops[i]; var (r1, g1, b1) = stops[i + 1];
        return (r0 + (r1 - r0) * f, g0 + (g1 - g0) * f, b0 + (b1 - b0) * f);
    }

    /// <summary>拾取距离：先包围盒粗排斥(避免大网逐边计算)，再逐边 2D 距离取最小。</summary>
    public override double DistanceTo(double px, double py)
    {
        var b = Bounds;
        double outside = 0;
        if (px < b.minX) outside = Math.Max(outside, b.minX - px);
        if (px > b.maxX) outside = Math.Max(outside, px - b.maxX);
        if (py < b.minY) outside = Math.Max(outside, b.minY - py);
        if (py > b.maxY) outside = Math.Max(outside, py - b.maxY);
        // 包围盒外：真距离 ≥ outside；用 outside 作下界即可(超容差就落选)
        if (outside > 0) return outside + 1e9;   // 明确落选(不做逐边), 避免误选大网外围
        // 逐三角(不走 Edges——为一次拾取构建整张去重边表会让首次点选明显卡顿)，
        // 先用三角包围盒早退, 点落在三角内直接判 0(面上任意处都可点中)。
        double best = double.MaxValue;
        foreach (var (a, b2, c) in Tris)
        {
            if (a >= Verts.Count || b2 >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[b2]; var r = Verts[c];
            double tx0 = Math.Min(p.x, Math.Min(q.x, r.x)), tx1 = Math.Max(p.x, Math.Max(q.x, r.x));
            double ty0 = Math.Min(p.y, Math.Min(q.y, r.y)), ty1 = Math.Max(p.y, Math.Max(q.y, r.y));
            if (px < tx0 - best || px > tx1 + best || py < ty0 - best || py > ty1 + best) continue;
            double d = (q.y - r.y) * (p.x - r.x) + (r.x - q.x) * (p.y - r.y);
            if (Math.Abs(d) > 1e-15)
            {
                double w0 = ((q.y - r.y) * (px - r.x) + (r.x - q.x) * (py - r.y)) / d;
                double w1 = ((r.y - p.y) * (px - r.x) + (p.x - r.x) * (py - r.y)) / d;
                double w2 = 1 - w0 - w1;
                if (w0 >= -1e-9 && w1 >= -1e-9 && w2 >= -1e-9) return 0;   // 点在面内
            }
            double e0 = SegDist(px, py, p.x, p.y, q.x, q.y); if (e0 < best) best = e0;
            double e1 = SegDist(px, py, q.x, q.y, r.x, r.y); if (e1 < best) best = e1;
            double e2 = SegDist(px, py, r.x, r.y, p.x, p.y); if (e2 < best) best = e2;
        }
        return best;
    }

    public override SceneEntity Apply(Affine2 m)
    {
        var me = new MeshEntity { Name = Name };
        foreach (var (x, y, z) in Verts) { var (nx, ny) = m.Map(x, y); me.Verts.Add((nx, ny, z)); }
        me.Tris.AddRange(Tris);
        return Colored(me);
    }

    /// <summary>分解 → 每条边一条直线(标高取两端均值)。</summary>
    public override List<SceneEntity>? Explode()
    {
        if (Edges.Count == 0) return null;
        var list = new List<SceneEntity>(Edges.Count);
        foreach (var (i, j) in Edges)
        {
            var p = Verts[i]; var q = Verts[j];
            var l = Colored(new LineEntity { X0 = p.x, Y0 = p.y, X1 = q.x, Y1 = q.y });
            l.Elevation = (p.z + q.z) / 2 + Elevation;
            list.Add(l);
        }
        return list;
    }

    /// <summary>三角网无夹点(顶点编辑走 修改高程点/焊接 等命令)。</summary>
    public override List<(double x, double y)> Grips() => new();

    /// <summary>深拷贝(几何 + 样式)。</summary>
    public MeshEntity Clone()
    {
        var me = new MeshEntity(Name, Verts, Tris);
        me.CopyStyleFrom(this);
        return me;
    }

    /// <summary>
    /// 选中高亮面：三角面按高亮色出，仍保留平行光明暗(看得出起伏), 不受显示模式影响(线框模式选中也上色)。
    /// </summary>
    public void TessellateHighlightFaces(List<float> o, float hr, float hg, float hb)
    {
        var key = (hr, hg, hb, Elevation);
        if (_hlCache != null && _hlKey == key) { o.AddRange(_hlCache); return; }
        var tmp = new List<float>(Tris.Count * 18);
        BuildHighlightFaces(tmp, hr, hg, hb);
        _hlCache = tmp.ToArray(); _hlKey = key;
        o.AddRange(_hlCache);
    }

    private float[]? _hlCache;
    private (float r, float g, float b, double elev) _hlKey;

    private void BuildHighlightFaces(List<float> o, float hr, float hg, float hb)
    {
        foreach (var (a, bb, c) in Tris)
        {
            if (a >= Verts.Count || bb >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[bb]; var r = Verts[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            var n = Normalize((uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx));
            double lambert = Math.Abs(n.x * Light.x + n.y * Light.y + n.z * Light.z);
            float k = (float)(0.55 + 0.45 * lambert);
            void V((double x, double y, double z) w)
            {
                o.Add((float)(w.x - RenderOrigin.X)); o.Add((float)(w.y - RenderOrigin.Y)); o.Add((float)(w.z + Elevation));
                o.Add(Math.Min(1f, hr * k)); o.Add(Math.Min(1f, hg * k)); o.Add(Math.Min(1f, hb * k));
            }
            V(p); V(q); V(r);
        }
    }

    /// <summary>XY 点是否落在任一三角的平面投影内(框选/点选面内判定用)。</summary>
    public bool ContainsXY(double x, double y)
    {
        var b = Bounds;
        if (x < b.minX || x > b.maxX || y < b.minY || y > b.maxY) return false;
        foreach (var (a, bb, c) in Tris)
        {
            if (a >= Verts.Count || bb >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[bb]; var r = Verts[c];
            double d = (q.y - r.y) * (p.x - r.x) + (r.x - q.x) * (p.y - r.y);
            if (Math.Abs(d) < 1e-15) continue;
            double w0 = ((q.y - r.y) * (x - r.x) + (r.x - q.x) * (y - r.y)) / d;
            double w1 = ((r.y - p.y) * (x - r.x) + (p.x - r.x) * (y - r.y)) / d;
            double w2 = 1 - w0 - w1;
            if (w0 >= -1e-9 && w1 >= -1e-9 && w2 >= -1e-9) return true;
        }
        return false;
    }

    /// <summary>
    /// XY 处网面的最高高程(含 Elevation)：盖住该点的所有三角里插值 z 的最大者 —— 闭合体有顶有底、地形上又叠着设计面时,
    /// 俯视看得见的正是最高的那层。没有三角盖住返回 null。逐三角扫, 只在点选时用。
    /// </summary>
    public double? TopZAt(double x, double y)
    {
        var b = Bounds;
        if (x < b.minX || x > b.maxX || y < b.minY || y > b.maxY) return null;
        double? best = null;
        foreach (var (a, bb, c) in Tris)
        {
            if (a >= Verts.Count || bb >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[bb]; var r = Verts[c];
            double d = (q.y - r.y) * (p.x - r.x) + (r.x - q.x) * (p.y - r.y);
            if (Math.Abs(d) < 1e-15) continue;
            double w0 = ((q.y - r.y) * (x - r.x) + (r.x - q.x) * (y - r.y)) / d;
            double w1 = ((r.y - p.y) * (x - r.x) + (p.x - r.x) * (y - r.y)) / d;
            double w2 = 1 - w0 - w1;
            if (w0 < -1e-9 || w1 < -1e-9 || w2 < -1e-9) continue;
            double z = w0 * p.z + w1 * q.z + w2 * r.z + Elevation;
            if (best == null || z > best) best = z;
        }
        return best;
    }

    /// <summary>三角总面积(三维真面积)。</summary>
    public double SurfaceArea()
    {
        double s = 0;
        foreach (var (a, b, c) in Tris)
        {
            if (a >= Verts.Count || b >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[b]; var r = Verts[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            s += Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
        }
        return s;
    }

    /// <summary>
    /// 有向体积(散度定理: 各三角与原点张成四面体的有向体积求和)。
    /// 闭合网格给真实体积(绕向决定正负, 调用方取绝对值); 非闭合网格无物理意义, 同原版内核口径。
    /// </summary>
    public double Volume()
    {
        double v = 0;
        foreach (var (a, b, c) in Tris)
        {
            if (a >= Verts.Count || b >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[b]; var r = Verts[c];
            v += (p.x * (q.y * r.z - q.z * r.y)
                - p.y * (q.x * r.z - q.z * r.x)
                + p.z * (q.x * r.y - q.y * r.x)) / 6.0;
        }
        return v;
    }
}
