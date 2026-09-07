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
    public bool Is2D { get; private set; } = true;   // 默认 2D 平面(俯视)模式

    /// <summary>切换 2D 平面 / 3D 轨道。</summary>
    public void SetMode(bool is2D) => Is2D = is2D;

    /// <summary>相机完整状态（供上一视图历史）。</summary>
    public readonly record struct State(double Yaw, double Pitch, double Dist, float Tx, float Ty, float Tz, bool Is2D);
    public State Snapshot() => new(Yaw, Pitch, Dist, Target[0], Target[1], Target[2], Is2D);
    public void Restore(State s)
    {
        Yaw = s.Yaw; Pitch = s.Pitch; Dist = s.Dist;
        Target[0] = s.Tx; Target[1] = s.Ty; Target[2] = s.Tz; Is2D = s.Is2D;
    }

    /// <summary>直接设相机朝向（标准视图预设用）：Yaw 绕 Z、Pitch 抬头，切到 3D。</summary>
    public void SetOrientation(double yaw, double pitch)
    {
        Is2D = false;
        Yaw = yaw;
        Pitch = Math.Clamp(pitch, -1.4, 1.4);
    }

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

    public void FitBounds(double minX, double minY, double maxX, double maxY, double zCenter = 0)
    {
        Target[0] = (float)((minX + maxX) * 0.5);
        Target[1] = (float)((minY + maxY) * 0.5);
        Target[2] = (float)zCenter;   // 注视点取几何高程中心(三维地形/三角网), 轨道旋转绕模型而非 Z=0
        double span = Math.Max(maxX - minX, maxY - minY);
        if (span < 1e-6) span = 10;
        Dist = Math.Clamp(span * 1.4, 2.0, 100000.0);
    }

    /// <summary>平移注视点（世界 XY）。</summary>
    public void ShiftTarget(double dx, double dy)
    {
        Target[0] += (float)dx;
        Target[1] += (float)dy;
    }

    /// <summary>
    /// 屏幕拖拽平移。2D：光标抓住的 Z=0 平面世界点跟随光标（反投影）。
    /// 3D：在过注视点、垂直视线的视平面内平移(相机右/上向量 × 注视距离处的每像素世界量)——
    /// 不再对 Z=0 平面反投影：斜视时该平面交点离模型很远、近地平线射线一像素跨越巨大距离，会把模型直接甩出视图。
    /// </summary>
    public void PanScreen(double sx0, double sy0, double sx1, double sy1, double vw, double vh)
    {
        if (vw < 1 || vh < 1) return;
        if (Is2D)
        {
            var w0 = ScreenToWorldOnZPlane(sx0, sy0, vw, vh);
            var w1 = ScreenToWorldOnZPlane(sx1, sy1, vw, vh);
            if (w0 != null && w1 != null)
                ShiftTarget(w0.Value.x - w1.Value.x, w0.Value.y - w1.Value.y);
            return;
        }
        double unitsPerPx = 2.0 * Dist * Math.Tan(Math.PI / 8) / vh;   // fovY=45° 时注视距离处一像素的世界长度
        double dx = (sx1 - sx0) * unitsPerPx, dy = (sy1 - sy0) * unitsPerPx;
        var (rx, ry, rz, ux, uy, uz) = ViewAxes();
        // 光标向右拖 → 场景跟着向右 → 注视点向左(−右向量); 向下拖 → 注视点向上(+上向量)
        Target[0] += (float)(-dx * rx + dy * ux);
        Target[1] += (float)(-dx * ry + dy * uy);
        Target[2] += (float)(-dx * rz + dy * uz);
    }

    /// <summary>3D 相机的右向量与上向量（世界系, 单位化）。</summary>
    public (double rx, double ry, double rz, double ux, double uy, double uz) ViewAxes()
    {
        float[] e = Eye();
        double fx = Target[0] - e[0], fy = Target[1] - e[1], fz = Target[2] - e[2];
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz); if (fl < 1e-12) fl = 1;
        fx /= fl; fy /= fl; fz /= fl;
        // right = forward × worldUp(0,0,1)
        double rx = fy * 1 - fz * 0, ry = fz * 0 - fx * 1, rz = fx * 0 - fy * 0;
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rl < 1e-9) { rx = 1; ry = 0; rz = 0; rl = 1; }
        rx /= rl; ry /= rl; rz /= rl;
        // up = right × forward
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        return (rx, ry, rz, ux, uy, uz);
    }

    /// <summary>朝光标缩放：缩放后保持光标下的世界点不动（CAD 标准）。</summary>
    public void ZoomAtScreen(double sx, double sy, double vw, double vh, double factor)
    {
        if (vw < 1 || vh < 1) { Zoom(factor); return; }
        if (Is2D)
        {
            var before = ScreenToWorldOnZPlane(sx, sy, vw, vh);
            Zoom(factor);
            var after = ScreenToWorldOnZPlane(sx, sy, vw, vh);
            if (before != null && after != null)
                ShiftTarget(before.Value.x - after.Value.x, before.Value.y - after.Value.y);
            return;
        }
        // 3D：让光标所指、位于注视深度视平面上的点在缩放后仍在光标下（与 3D 平移同一视平面口径）。
        // 旧实现对 Z=0 平面反投影，斜视时交点飘到远处，缩放后注视点大幅跳动——看起来像"模型旋转后消失"。
        double upp0 = 2.0 * Dist * Math.Tan(Math.PI / 8) / vh;
        double dx = (sx - vw / 2) * upp0, dy = (sy - vh / 2) * upp0;   // 光标相对屏幕中心在视平面上的世界偏移
        var (rx, ry, rz, ux, uy, uz) = ViewAxes();
        double px = dx * rx - dy * ux, py = dx * ry - dy * uy, pz = dx * rz - dy * uz;
        double d0 = Dist;
        Zoom(factor);
        double k = 1.0 - Dist / d0;   // 实际缩放比(受 Dist 夹紧影响)
        Target[0] += (float)(px * k); Target[1] += (float)(py * k); Target[2] += (float)(pz * k);
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
            // 正交俯视：相机远置于注视点上方看向 -Z，屏幕 X 右 / Y 上。眼高与 near/far 覆盖大 Z 跨度——
            // 否则抬升到高程的几何(等高线/三维地形, Z 可达千米级)会被裁出视锥, 切 2D 后看着空白(“无法切回 2D”)。
            // ortho 尺寸(缩放)仍由 Dist 决定, 与眼高解耦。
            double camZ = Dist + 100000.0;
            float[] eye2 = { Target[0], Target[1], (float)(Target[2] + camZ) };
            float[] view2 = Mat4.LookAt(eye2, Target, new[] { 0f, 1f, 0f });
            float halfH = (float)Dist;
            float far2 = (float)(2.0 * camZ + Dist * 4.0 + 10.0);
            float[] proj2 = Mat4.Ortho(-halfH * aspect, halfH * aspect, -halfH, halfH, 0.01f, far2);
            return Mat4.Mul(proj2, view2);
        }
        // 透视：远平面随距离放大，避免大图纸被裁
        float far = (float)(Dist * 4.0 + 200.0);
        float[] proj = Mat4.Perspective(MathF.PI / 4f, aspect, 0.1f, far);
        float[] view = Mat4.LookAt(Eye(), Target, new[] { 0f, 0f, 1f });
        return Mat4.Mul(proj, view);
    }

    /// <summary>屏幕像素 → Z=0 平面上的世界坐标（坐标读数 / 拾取）。不可逆或射线平行于平面时返回 null。</summary>
    public (double x, double y)? ScreenToWorldOnZPlane(double sx, double sy, double vw, double vh)
    {
        if (vw < 1 || vh < 1) return null;
        double ndcX = 2.0 * sx / vw - 1.0;
        double ndcY = 1.0 - 2.0 * sy / vh;                 // 屏幕 Y 下 → NDC Y 上
        float[]? inv = Mat4.Invert(ViewProj((float)(vw / vh)));
        if (inv == null) return null;
        var near = UnprojectNdc(inv, ndcX, ndcY, -1.0);    // 近裁面点
        var far = UnprojectNdc(inv, ndcX, ndcY, 1.0);      // 远裁面点
        double dz = far.z - near.z;
        if (Math.Abs(dz) < 1e-12) return null;
        double t = (0.0 - near.z) / dz;                    // 沿射线交 Z=0
        return (near.x + t * (far.x - near.x), near.y + t * (far.y - near.y));
    }

    /// <summary>世界(局部)点 → 屏幕像素(左上原点)。相机后方(w≤0)返回 null。供 3D 屏幕空间框选。</summary>
    public (double sx, double sy)? WorldToScreen(double x, double y, double z, double vw, double vh)
        => MakeProjector(vw, vh)(x, y, z);

    /// <summary>一次算好 ViewProj 的投影函数(大网逐顶点投影时避免每点重建矩阵)。</summary>
    public Func<double, double, double, (double sx, double sy)?> MakeProjector(double vw, double vh)
    {
        if (vw < 1 || vh < 1) return (_, _, _) => null;
        var m = ViewProj((float)(vw / vh));
        return (x, y, z) =>
        {
            double cx = m[0] * x + m[4] * y + m[8] * z + m[12];
            double cy = m[1] * x + m[5] * y + m[9] * z + m[13];
            double cw = m[3] * x + m[7] * y + m[11] * z + m[15];
            if (cw <= 1e-9) return null;
            return ((cx / cw + 1.0) * 0.5 * vw, (1.0 - cy / cw) * 0.5 * vh);
        };
    }

    private static (double x, double y, double z) UnprojectNdc(float[] inv, double nx, double ny, double nz)
    {
        double x = inv[0] * nx + inv[4] * ny + inv[8] * nz + inv[12];
        double y = inv[1] * nx + inv[5] * ny + inv[9] * nz + inv[13];
        double z = inv[2] * nx + inv[6] * ny + inv[10] * nz + inv[14];
        double w = inv[3] * nx + inv[7] * ny + inv[11] * nz + inv[15];
        if (Math.Abs(w) < 1e-12) w = 1.0;
        return (x / w, y / w, z / w);
    }

    /// <summary>叠加层坐标罗盘用：随相机朝向的纯旋转 + 小正交，无平移。</summary>
    public float[] GizmoViewProj()
    {
        float[] eye, up;
        if (Is2D)
        {
            // 2D 正交俯视：从正上方看向原点 → X 轴朝右、Y 轴朝上、Z 轴朝观察者(收成一点)。
            eye = new[] { 0f, 0f, 3f };
            up = new[] { 0f, 1f, 0f };
        }
        else
        {
            // 3D 轨道：罗盘随相机朝向(纯旋转)。
            float[] e = Eye();
            float dx = e[0] - Target[0], dy = e[1] - Target[1], dz = e[2] - Target[2];
            float l = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (l < 1e-6f) l = 1f;
            eye = new[] { dx / l * 3f, dy / l * 3f, dz / l * 3f };
            up = new[] { 0f, 0f, 1f };
        }
        float[] proj = Mat4.Ortho(-1.6f, 1.6f, -1.6f, 1.6f, -10f, 10f);
        float[] view = Mat4.LookAt(eye, new[] { 0f, 0f, 0f }, up);
        return Mat4.Mul(proj, view);
    }
}
