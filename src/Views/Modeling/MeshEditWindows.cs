namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「三维地质建模 → 建模/编辑/工具」组(原 MeshEditLib 各窗口)的登记：功能项名(= Ribbon Tag) → 打开窗口。
/// 主窗口构造里调用 <see cref="Register"/> 后，派发时登记的窗口优先于场景版直算。
/// </summary>
public static class MeshEditWindows
{
    public static void Register()
    {
        ModelingWindowFactory.Openers["地质体建模"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new QuickModelDialog(ctx));
        ModelingWindowFactory.Openers["格网质量检测"] = (owner, ctx) => _ = DiagnoseDialog.RunAsync(ctx);
        ModelingWindowFactory.Openers["两期三角网算量"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new VolumeSplitDialog(ctx));
        ModelingWindowFactory.Openers["展点"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new ShowPointsWindow(ctx));
        ModelingWindowFactory.Openers["构建等值线"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new ContourBuilderWindow(ctx));
        ModelingWindowFactory.Openers["创建剖面"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new SectionCutWindow(ctx));
        ModelingWindowFactory.Openers["动态剖面"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new DynamicSectionWindow(ctx));
    }
}
