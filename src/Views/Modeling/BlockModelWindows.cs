using Avalonia.Controls;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「三维地质建模 → 块体模型」组窗口登记（原 BlockModelLibPlugin 的 12 个 Ribbon 按钮 + 块体浏览器）。
/// 原为模态(ShowDialog)的用 ShowDialog；原非模态单例(删除/着色/实体转块体/体素格网体积)与需视口拾取的(约束)走 <see cref="ModelingWindows.Show"/>。
/// </summary>
public static class BlockModelWindows
{
    public static void Register()
    {
        var o = ModelingWindowFactory.Openers;
        o["创建块体"] = (owner, ctx) => Modal(new CreateBlockModelWindow(ctx), ctx.Owner);
        o["约束块体"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new ConstrainBlockModelWindow(ctx));
        o["导入块体"] = (owner, ctx) => Modal(new ImportBlockModelWindow(ctx), ctx.Owner);
        o["导出块体"] = (owner, ctx) => Modal(new ExportBlockModelWindow(ctx), ctx.Owner);
        o["属性赋值"] = (owner, ctx) => Modal(new AssignAttributeWindow(ctx), ctx.Owner);
        o["块体着色"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new ColoringWindow(ctx));
        o["删除块体"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new DeleteBlockModelWindow(ctx));
        o["筛选块体"] = (owner, ctx) => Modal(new FilterBlocksWindow(ctx), ctx.Owner);
        o["切面剖切"] = (owner, ctx) => Modal(new SliceWindow(ctx), ctx.Owner);
        o["输出报告"] = (owner, ctx) => Modal(new ReportWindow(ctx), ctx.Owner);
        o["实体转块体"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new EntityToBlocksWindow(ctx));
        o["体素格网体积"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new VoxelVolumeWindow(ctx));
        o["块体模型浏览器"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new BlockModelBrowserWindow(ctx));
        o["体积算量"] = (owner, ctx) => SumSelectedMeshVolume(ctx);
    }

    /// <summary>
    /// 「采矿模型」组 · 体积算量（忠实原 BlockModelLibPlugin.CreateVolumeCommand → IPitDesignCapability.SumSelectedMeshVolume）：
    /// 拿视口当前选中的采矿模型体，逐个算体积、只累计算得出（&gt;0，即封闭体）的那些，汇总方量回显。
    /// 与「三角网体积」不同：那个是逐网的顶点/三角/面积/Z 明细，这个只报个数与合计方量。
    /// </summary>
    private static void SumSelectedMeshVolume(ModelingContext ctx)
    {
        double sum = 0; int n = 0;
        foreach (var me in ctx.SelectedMeshes())
        {
            double v = 0;
            try { if (Cad.MeshDiagnose.Analyze(me.Verts, me.Tris).IsClosed) v = Cad.MeshMetrics.RobustVolume(me.Verts, me.Tris); }
            catch { v = 0; }
            if (v > 0) { sum += v; n++; }
        }
        ctx.Status(n == 0 ? "体积算量:请先选中采矿模型体(封闭三角网)" : $"✓ 体积算量:{n} 个体,合计 {sum:N0} m³");
    }

    private static void Modal(Window dlg, Window owner) => _ = dlg.ShowDialog<bool>(owner);
}
