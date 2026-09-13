using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Tests.Synth;

// ─────────────────────────────────────────────────────────────────────────────
//  原 Tests.MineAssLib / Tests.PitMineApp 用 BlockModelLib.Domain.BlockModel（规则格网 + 逐列单元格数据）
//  合成剖面用例；Kylin 的倾斜算量引擎吃的是 InclineBlockSource（块列表 + 属性数组）。
//  这里照原 API 形状给一个最小实现（Spec/Vec3/EnsureCellData/SetConstant/SetCell），
//  隐式转成 InclineBlockSource —— 判据文件逐行照抄，几何与属性完全一致。
// ─────────────────────────────────────────────────────────────────────────────

public readonly record struct Vec3d(double X, double Y, double Z);
public readonly record struct Vec3i(int X, int Y, int Z);

public sealed class BlockModelSpec
{
    public Vec3d Origin { get; init; }
    public Vec3d BlockSize { get; init; }
    public Vec3i Dimensions { get; init; }
    public long CellCount => (long)Dimensions.X * Dimensions.Y * Dimensions.Z;
}

public sealed class CellData
{
    private readonly long _n;
    public readonly Dictionary<string, double[]> Columns = new(StringComparer.Ordinal);
    public CellData(long n) => _n = n;
    public void SetConstant(string col, double v) { var a = new double[_n]; if (v != 0) Array.Fill(a, v); Columns[col] = a; }
    public void SetCell(string col, long idx, double v) { if (!Columns.TryGetValue(col, out var a)) { a = new double[_n]; Columns[col] = a; } a[idx] = v; }
}

public sealed class BlockModel
{
    public string Name { get; init; } = "";
    public BlockModelSpec Spec { get; init; } = new();
    private CellData? _cd;
    public CellData EnsureCellData() => _cd ??= new CellData(Spec.CellCount);

    /// <summary>规则格网 → 块列表（k 外、jy 中、i 内，与原 cell 索引 k·nxy + jy·NX + i 同序）。</summary>
    public InclineBlockSource ToSource()
    {
        var s = Spec; int nx = s.Dimensions.X, ny = s.Dimensions.Y, nz = s.Dimensions.Z;
        var blocks = new List<Cad.BlockModel.Block>(checked((int)s.CellCount));
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                    blocks.Add(new Cad.BlockModel.Block
                    {
                        X = s.Origin.X + (i + 0.5) * s.BlockSize.X, Y = s.Origin.Y + (j + 0.5) * s.BlockSize.Y, Z = s.Origin.Z + (k + 0.5) * s.BlockSize.Z,
                        Size = s.BlockSize.X, Grade = 0,
                    });
        var attrs = new Dictionary<string, double[]>(StringComparer.Ordinal);
        if (_cd != null) foreach (var kv in _cd.Columns) attrs[kv.Key] = kv.Value;
        return new InclineBlockSource { Name = Name, Blocks = blocks, Attrs = attrs };
    }

    public static implicit operator InclineBlockSource(BlockModel m) => m.ToSource();
}
