using System;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// 轨道相机（CAD 约定：Z 轴向上）。对应内核 xllAcGi 的 Camera。
/// yaw 绕 Z、pitch 抬头、dist 距离、target 注视点；产出主场景 ViewProj，
/// 以及叠加层（左下角坐标罗盘）用的纯旋转正交 ViewProj。
/// </summary>
internal sealed class Camera
{
    public double Yaw { get; private set; } = 0.9;     // 绕 Z
    public double Pitch { get; private set; } = 0.55;  // 抬头
    public double Dist { get; private set; } = 12.0;
    public float[] Target { get; } = { 0f, 0f, 1f };

    /// <summary>轨道旋转（鼠标拖拽）。</summary>
    public void Orbit(double dYaw, double dPitch)
    {
        Yaw -= dYaw;
        Pitch = Math.Clamp(Pitch + dPitch, -1.4, 1.4);
    }

    /// <summary>缩放（滚轮）。factor &lt;1 拉近，&gt;1 拉远。</summary>
    public void Zoom(double factor) => Dist = Math.Clamp(Dist * factor, 2.0, 100000.0);

    /// <summary>范围缩放：相机对准并纳入包围盒 [minX, minY, maxX, maxY]（对应 ZOOMEXTENTS）。</summary>
    public void FitBounds(double[]? bounds)
    {
        if (bounds == null || bounds.Length < 4) return;
        FitBounds(bounds[0], bounds[1], bounds[2], bounds[3]);
    }

    public void FitBounds(double minX, double minY, double maxX, double maxY)
    {
        Target[0] = (float)((minX + maxX) * 0.5);
        Target[1] = (float)((minY + maxY) * 0.5);
        Target[2] = 0f;
        double span = Math.Max(maxX - minX, maxY - minY);
        if (span < 1e-6) span = 10;
        Dist = Math.Clamp(span * 1.4, 2.0, 100000.0);
    }

    /// <summary>相机位置（球坐标，Z 上）。</summary>
    public float[] Eye()
    {
        float cy = MathF.Cos((float)Pitch);
        return new[]
        {
            Target[0] + (float)(Dist * cy * Math.Cos(Yaw)),
            Target[1] + (float)(Dist * cy * Math.Sin(Yaw)),
            Target[2] + (float)(Dist * Math.Sin(Pitch))
        };
    }

    /// <summary>主场景 ViewProj（透视）。</summary>
    public float[] ViewProj(float aspect)
    {
        float[] proj = Mat4.Perspective(MathF.PI / 4f, aspect, 0.1f, 200f);
        float[] view = Mat4.LookAt(Eye(), Target, new[] { 0f, 0f, 1f });
        return Mat4.Mul(proj, view);
    }

    /// <summary>叠加层坐标罗盘用：随相机朝向的纯旋转 + 小正交，无平移。</summary>
    public float[] GizmoViewProj()
    {
        float[] e = Eye();
        float dx = e[0] - Target[0], dy = e[1] - Target[1], dz = e[2] - Target[2];
        float l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (l < 1e-6f) l = 1f;
        float[] eye = { dx / l * 3f, dy / l * 3f, dz / l * 3f };
        float[] proj = Mat4.Ortho(-1.6f, 1.6f, -1.6f, 1.6f, -10f, 10f);
        float[] view = Mat4.LookAt(eye, new[] { 0f, 0f, 0f }, new[] { 0f, 0f, 1f });
        return Mat4.Mul(proj, view);
    }
}
