using System;

namespace PitMine3D.Kylin.Cad.SeamOutcrop
{
    /// <summary>
    /// 2.5D 竖直 Z 采样器：对一张三角网建 XY 网格索引，给 (x,y) 反算其竖直落点 Z（重心插值）。
    /// 供煤层露头着色：把现状面每个顶点竖直投影到顶/底板面取 Z，算标量场 d = z现状 − z顶/底板。
    /// 采不到（顶/底板未盖到该 xy）返回 false，上层视为 nodata（带外）。
    ///
    /// 索引结构（性能关键）：CSR 扁平数组（cellStart 前缀和 + cellTris 聚簇），不是 List&lt;int&gt;[]。
    /// 百万级三角网下 List 版要 new 出百万个 List 对象（GC 压力 + 指针跳转），CSR 只有两块连续内存，
    /// 建索引与查询都是顺序访问。<see cref="TrySampleZ"/> 只读无状态，可多线程并发调用。
    /// <para><b>忠实逐字移植</b> <c>MineAssLib.SeamOutcrop.MeshZSampler</c>（仅改命名空间）。</para>
    /// </summary>
    internal sealed class MeshZSampler
    {
        private readonly double[] _v;      // 世界坐标 flat [x,y,z,...]
        private readonly int[] _t;         // 三角形索引 flat
        private readonly double _minX, _minY, _maxX, _maxY;
        private readonly double _invCellX, _invCellY;   // 每轴 1/格边长（按轴独立，保证格网严格盖满包围盒）
        private readonly int _nx, _ny;
        private readonly int[] _cellStart;              // CSR：长度 nx*ny+1，cell i 的三角占 [start[i], start[i+1])
        private readonly int[] _cellTris;               // CSR：三角形起始下标(=f, 指向 _t[f..f+2])，按格聚簇

        public bool IsEmpty { get; }

        /// <summary>本面 XY 包围盒（供上层 O(1) 预剔除盒外点，省一次格查询）。</summary>
        public double MinX => _minX;
        public double MinY => _minY;
        public double MaxX => _maxX;
        public double MaxY => _maxY;

        public MeshZSampler(double[] worldVerts, int[] tris)
        {
            _v = worldVerts ?? Array.Empty<double>();
            _t = tris ?? Array.Empty<int>();
            int triCount = _t.Length / 3;
            if (_v.Length < 9 || triCount == 0)
            {
                IsEmpty = true;
                _cellStart = Array.Empty<int>();
                _cellTris = Array.Empty<int>();
                _invCellX = _invCellY = 1; _nx = _ny = 1; _minX = _minY = 0; _maxX = _maxY = 0;
                return;
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < _v.Length; i += 3)
            {
                double x = _v[i], y = _v[i + 1];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            _minX = minX; _minY = minY; _maxX = maxX; _maxY = maxY;
            double w = Math.Max(maxX - minX, 1e-6), h = Math.Max(maxY - minY, 1e-6);

            // 目标格数 ≈ 三角数（每格约 1 个三角）；上限 400 万格（cellStart ≈ 16MB）防超大网炸内存。
            double target = Math.Min(4_000_000.0, Math.Max(64.0, triCount));
            double cell = Math.Sqrt((w * h) / target);
            if (!(cell > 1e-9)) cell = Math.Max(w, h);
            int nx = Math.Max(1, (int)(w / cell) + 1);
            int ny = Math.Max(1, (int)(h / cell) + 1);
            while ((long)nx * ny > 4_000_000L) { nx = Math.Max(1, nx >> 1); ny = Math.Max(1, ny >> 1); }
            _nx = nx; _ny = ny;
            // 每轴独立格宽 = 跨度/格数：格网严格盖满包围盒（旧版单一 cell + 单边截断会把远端全挤进最后一列）。
            _invCellX = nx / w;
            _invCellY = ny / h;

            int cellCount = nx * ny;
            _cellStart = new int[cellCount + 1];

            // ── CSR 两趟建索引：① 数每格三角数 → 前缀和；② 按游标回填 ──
            for (int f = 0; f < _t.Length; f += 3)
            {
                if (!TriCellRange(f, out int cx0, out int cx1, out int cy0, out int cy1)) continue;
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    int row = cy * nx;
                    for (int cx = cx0; cx <= cx1; cx++) _cellStart[row + cx + 1]++;
                }
            }
            for (int i = 1; i <= cellCount; i++) _cellStart[i] += _cellStart[i - 1];

            _cellTris = new int[_cellStart[cellCount]];
            var cursor = new int[cellCount];
            Array.Copy(_cellStart, cursor, cellCount);
            for (int f = 0; f < _t.Length; f += 3)
            {
                if (!TriCellRange(f, out int cx0, out int cx1, out int cy0, out int cy1)) continue;
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    int row = cy * nx;
                    for (int cx = cx0; cx <= cx1; cx++) _cellTris[cursor[row + cx]++] = f;
                }
            }
        }

        /// <summary>三角 f 覆盖的格范围；索引越界/退化返回 false（两趟建索引须用同一判据）。</summary>
        private bool TriCellRange(int f, out int cx0, out int cx1, out int cy0, out int cy1)
        {
            cx0 = cx1 = cy0 = cy1 = 0;
            int a = _t[f] * 3, b = _t[f + 1] * 3, c = _t[f + 2] * 3;
            if (a < 0 || b < 0 || c < 0 || a + 2 >= _v.Length || b + 2 >= _v.Length || c + 2 >= _v.Length) return false;
            double tminX = Math.Min(_v[a], Math.Min(_v[b], _v[c]));
            double tmaxX = Math.Max(_v[a], Math.Max(_v[b], _v[c]));
            double tminY = Math.Min(_v[a + 1], Math.Min(_v[b + 1], _v[c + 1]));
            double tmaxY = Math.Max(_v[a + 1], Math.Max(_v[b + 1], _v[c + 1]));
            cx0 = CellX(tminX); cx1 = CellX(tmaxX);
            cy0 = CellY(tminY); cy1 = CellY(tmaxY);
            return true;
        }

        private int CellX(double x) => Math.Min(_nx - 1, Math.Max(0, (int)((x - _minX) * _invCellX)));
        private int CellY(double y) => Math.Min(_ny - 1, Math.Max(0, (int)((y - _minY) * _invCellY)));

        /// <summary>竖直采样 (x,y) 处落点 Z（重心插值）；未命中任何三角返回 false。只读，可并发调用。</summary>
        public bool TrySampleZ(double x, double y, out double z)
        {
            z = double.NaN;
            if (IsEmpty) return false;
            if (x < _minX || x > _maxX || y < _minY || y > _maxY) return false;   // 盒外直接否，省格查询

            int cell = CellY(y) * _nx + CellX(x);
            int end = _cellStart[cell + 1];
            const double eps = 1e-7;
            for (int k = _cellStart[cell]; k < end; k++)
            {
                int f = _cellTris[k];
                int a = _t[f] * 3, b = _t[f + 1] * 3, c = _t[f + 2] * 3;
                double ax = _v[a], ay = _v[a + 1], bx = _v[b], by = _v[b + 1], cx = _v[c], cy = _v[c + 1];
                double d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (Math.Abs(d) < 1e-14) continue;   // 退化三角
                double inv = 1.0 / d;
                double wa = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) * inv;
                if (wa < -eps) continue;
                double wb = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) * inv;
                if (wb < -eps) continue;
                double wc = 1.0 - wa - wb;
                if (wc < -eps) continue;
                z = wa * _v[a + 2] + wb * _v[b + 2] + wc * _v[c + 2];
                return true;
            }
            return false;
        }
    }
}
