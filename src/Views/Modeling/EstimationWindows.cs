namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>「三维地质建模 → 地质统计学分析」组窗口登记：快速估值 / 克里金估值（原 MeshEditLib Estimation 对话框，非模态单例）。</summary>
public static class EstimationWindows
{
    public static void Register()
    {
        ModelingWindowFactory.Openers["快速估值"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new QuickEstimateWindow(ctx));
        ModelingWindowFactory.Openers["克里金估值"] = (owner, ctx) => ModelingWindows.Show(ctx, () => new KrigingWindow(ctx));
    }
}
