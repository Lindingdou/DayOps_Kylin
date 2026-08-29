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
