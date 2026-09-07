namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>「三维地质建模 → 更新地质模型」组窗口登记: 功能项名(= Ribbon Tag) → 打开窗口(单例)。主窗口构造里调用 <see cref="Register"/>。</summary>
public static class ModelUpdateWindows
{
    public static void Register()
    {
        ModelingWindowFactory.Openers["补勘钻孔写实"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new SupplementaryBoreholeWindow(ctx));
        ModelingWindowFactory.Openers["现状写实"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new CurrentStateRealisticWindow(ctx));
        ModelingWindowFactory.Openers["更新煤层面"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new UpdateSeamSurfaceWindow(ctx));
    }
}
