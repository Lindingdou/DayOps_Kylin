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
    }

    private static void Modal(Window dlg, Window owner) => _ = dlg.ShowDialog<bool>(owner);
}
