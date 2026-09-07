using System;

namespace PitMine3D.Kylin.Controls;

/// <summary>
/// 列主序 4x4，OpenGL 约定（列向量，NDC z ∈ [-1,1]）。
/// 自己实现以避免 System.Numerics 的 DirectX 裁剪空间/行向量约定与 GL 混淆。
/// 存储：m[col*4 + row]。着色器里 gl_Position = uMVP * vec4(pos,1)。
/// </summary>
internal static class Mat4
{
    public static float[] Identity() => new float[]
    {
        1,0,0,0,  0,1,0,0,  0,0,1,0,  0,0,0,1
    };

    // a * b（先施加 b，再施加 a）
    public static float[] Mul(float[] a, float[] b)
    {
        var r = new float[16];
        for (int c = 0; c < 4; c++)
        for (int row = 0; row < 4; row++)
        {
            float s = 0;
            for (int k = 0; k < 4; k++)
                s += a[k * 4 + row] * b[c * 4 + k];
            r[c * 4 + row] = s;
        }
        return r;
    }

    public static float[] Translate(float x, float y, float z) => new float[]
    {
        1,0,0,0,  0,1,0,0,  0,0,1,0,  x,y,z,1
    };

    public static float[] RotateZ(float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new float[]
        {
            c, s, 0, 0,
           -s, c, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        };
    }

    public static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovY / 2f);
        var m = new float[16];
        m[0] = f / aspect;
        m[5] = f;
        m[10] = (far + near) / (near - far);
        m[11] = -1f;
        m[14] = (2f * far * near) / (near - far);
        return m;
    }

    public static float[] Ortho(float l, float r, float b, float t, float near, float far)
    {
        var m = new float[16];
        m[0] = 2f / (r - l);
        m[5] = 2f / (t - b);
        m[10] = -2f / (far - near);
        m[12] = -(r + l) / (r - l);
        m[13] = -(t + b) / (t - b);
        m[14] = -(far + near) / (far - near);
        m[15] = 1f;
        return m;
    }

    public static float[] LookAt(float[] eye, float[] center, float[] up)
    {
        float[] f = Norm(Sub(center, eye));
        float[] s = Norm(Cross(f, up));
        float[] u = Cross(s, f);
        return new float[]
        {
            s[0], u[0], -f[0], 0,
            s[1], u[1], -f[1], 0,
            s[2], u[2], -f[2], 0,
            -Dot(s, eye), -Dot(u, eye), Dot(f, eye), 1
        };
    }

    /// <summary>4x4 逆矩阵（列主序 OpenGL 布局）。不可逆返回 null。用于屏幕→世界反投影。</summary>
    public static float[]? Invert(float[] m)
    {
        var inv = new float[16];
        inv[0]  =  m[5]*m[10]*m[15] - m[5]*m[11]*m[14] - m[9]*m[6]*m[15] + m[9]*m[7]*m[14] + m[13]*m[6]*m[11] - m[13]*m[7]*m[10];
        inv[4]  = -m[4]*m[10]*m[15] + m[4]*m[11]*m[14] + m[8]*m[6]*m[15] - m[8]*m[7]*m[14] - m[12]*m[6]*m[11] + m[12]*m[7]*m[10];
        inv[8]  =  m[4]*m[9]*m[15]  - m[4]*m[11]*m[13] - m[8]*m[5]*m[15] + m[8]*m[7]*m[13] + m[12]*m[5]*m[11] - m[12]*m[7]*m[9];
        inv[12] = -m[4]*m[9]*m[14]  + m[4]*m[10]*m[13] + m[8]*m[5]*m[14] - m[8]*m[6]*m[13] - m[12]*m[5]*m[10] + m[12]*m[6]*m[9];
        inv[1]  = -m[1]*m[10]*m[15] + m[1]*m[11]*m[14] + m[9]*m[2]*m[15] - m[9]*m[3]*m[14] - m[13]*m[2]*m[11] + m[13]*m[3]*m[10];
        inv[5]  =  m[0]*m[10]*m[15] - m[0]*m[11]*m[14] - m[8]*m[2]*m[15] + m[8]*m[3]*m[14] + m[12]*m[2]*m[11] - m[12]*m[3]*m[10];
        inv[9]  = -m[0]*m[9]*m[15]  + m[0]*m[11]*m[13] + m[8]*m[1]*m[15] - m[8]*m[3]*m[13] - m[12]*m[1]*m[11] + m[12]*m[3]*m[9];
        inv[13] =  m[0]*m[9]*m[14]  - m[0]*m[10]*m[13] - m[8]*m[1]*m[14] + m[8]*m[2]*m[13] + m[12]*m[1]*m[10] - m[12]*m[2]*m[9];
        inv[2]  =  m[1]*m[6]*m[15]  - m[1]*m[7]*m[14]  - m[5]*m[2]*m[15] + m[5]*m[3]*m[14] + m[13]*m[2]*m[7]  - m[13]*m[3]*m[6];
        inv[6]  = -m[0]*m[6]*m[15]  + m[0]*m[7]*m[14]  + m[4]*m[2]*m[15] - m[4]*m[3]*m[14] - m[12]*m[2]*m[7]  + m[12]*m[3]*m[6];
        inv[10] =  m[0]*m[5]*m[15]  - m[0]*m[7]*m[13]  - m[4]*m[1]*m[15] + m[4]*m[3]*m[13] + m[12]*m[1]*m[7]  - m[12]*m[3]*m[5];
        inv[14] = -m[0]*m[5]*m[14]  + m[0]*m[6]*m[13]  + m[4]*m[1]*m[14] - m[4]*m[2]*m[13] - m[12]*m[1]*m[6]  + m[12]*m[2]*m[5];
        inv[3]  = -m[1]*m[6]*m[11]  + m[1]*m[7]*m[10]  + m[5]*m[2]*m[11] - m[5]*m[3]*m[10] - m[9]*m[2]*m[7]   + m[9]*m[3]*m[6];
        inv[7]  =  m[0]*m[6]*m[11]  - m[0]*m[7]*m[10]  - m[4]*m[2]*m[11] + m[4]*m[3]*m[10] + m[8]*m[2]*m[7]   - m[8]*m[3]*m[6];
        inv[11] = -m[0]*m[5]*m[11]  + m[0]*m[7]*m[9]   + m[4]*m[1]*m[11] - m[4]*m[3]*m[9]  - m[8]*m[1]*m[7]   + m[8]*m[3]*m[5];
        inv[15] =  m[0]*m[5]*m[10]  - m[0]*m[6]*m[9]   - m[4]*m[1]*m[10] + m[4]*m[2]*m[9]  + m[8]*m[1]*m[6]   - m[8]*m[2]*m[5];

        double det = m[0]*inv[0] + m[1]*inv[4] + m[2]*inv[8] + m[3]*inv[12];
        // 阈值必须相对于矩阵量级：大场景的 2D 正交投影(halfH≈万, far≈26万)各轴尺度悬殊,
        // 行列式本就 ~1e-14 却完全可逆; 固定 1e-12 会误判为奇异 → ScreenToWorld 全线 null
        // (2D 下点选/框选/捕捉/坐标读数全失效, 大坐标模型如 .3dm 必中)。
        // 用 Hadamard 上界(各列范数之积)归一, 真奇异时 |det|/上界 ≈ 浮点噪声(~1e-7), 可靠区分。
        double bound = 1;
        for (int c = 0; c < 4; c++)
        {
            double s = 0;
            for (int r = 0; r < 4; r++) { double a = m[c * 4 + r]; s += a * a; }
            bound *= Math.Sqrt(s);
        }
        if (double.IsNaN(det) || double.IsInfinity(det) || Math.Abs(det) <= 1e-9 * bound) return null;
        float invDet = (float)(1.0 / det);
        if (float.IsNaN(invDet) || float.IsInfinity(invDet)) return null;
        for (int i = 0; i < 16; i++) inv[i] *= invDet;
        return inv;
    }

    private static float[] Sub(float[] a, float[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static float[] Cross(float[] a, float[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0]
    };
    private static float[] Norm(float[] a)
    {
        float l = MathF.Sqrt(Dot(a, a));
        return l < 1e-8f ? a : new[] { a[0] / l, a[1] / l, a[2] / l };
    }
}
