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

    /// <summary>2D 平面视图（正交俯视）为 true；3D 轨道为 false。</summary>
    public bool Is2D { get; private set; }

    /// <summary>切换 2D 平面 / 3D 轨道。</summary>
    public void SetMode(bool is2D) => Is2D = is2D;

    /// <summary>鼠标拖拽：3D 轨道旋转 / 2D 平移注视点。</summary>
    public void Orbit(double dYaw, double dPitch)
    {
        if (Is2D)
        {
            Target[0] -= (float)(dYaw * Dist * 0.5);
            Target[1] += (float)(dPitch * Dist * 0.5);
            return;
        }
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

    /// <summary>主场景 ViewProj。3D=透视轨道；2D=正交俯视。远平面随距离放大以纳入大图纸。</summary>
    public float[] ViewProj(float aspect)
    {
        if (Is2D)
        {
            // 正交俯视：相机在注视点正上方看向 -Z，屏幕 X 右 / Y 上
            float[] eye2 = { Target[0], Target[1], Target[2] + (float)Dist };
            float[] view2 = Mat4.LookAt(eye2, Target, new[] { 0f, 1f, 0f });
            float halfH = (float)Dist;
            float[] proj2 = Mat4.Ortho(-halfH * aspect, halfH * aspect, -halfH, halfH, 0.01f, (float)(Dist * 4.0 + 10.0));
            return Mat4.Mul(proj2, view2);
        }
        // 透视：远平面随距离放大，避免大图纸被裁
        float far = (float)(Dist * 4.0 + 200.0);
        float[] proj = Mat4.Perspective(MathF.PI / 4f, aspect, 0.1f, far);
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
