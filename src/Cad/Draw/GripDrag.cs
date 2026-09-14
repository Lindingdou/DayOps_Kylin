using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 夹点拖拽模式。按空格循环，顺序同 AutoCAD 的夹点模式环：
/// 拉伸 → 移动 → 旋转 → 比例缩放 → 镜像 → 回到拉伸。
/// </summary>
public enum GripMode { Stretch, Move, Rotate, Scale, Mirror }

/// <summary>
/// 夹点拖拽状态 —— 忠实移植原 PitMine3D 内核 GripEditor 的 DragState + ApplyDrag。
///
/// 按下夹点即快照：锚点夹点及其原坐标、鼠标按下时的世界点、本次被拖的【全部】选中夹点(各自记原坐标)。
/// 拖动中按当前光标算出"变换后的实体"(<see cref="Preview"/>)——Kylin 以预览方式呈现，松开才落地替换，
/// 故 Esc 取消无需回滚(原版实时改实体，需 inv(lastApplied) 回退)。
///
/// 各模式语义与原版一致：
///   Stretch：全部选中夹点按【锚点位移】平移(锚点落点精确等于光标，捕捉对锚点仍精确)；
///   Move   ：以锚点位移平移整个实体；
///   Rotate ：绕锚点旋转，角度 = 当前角 − 按下时角(相对角，不跳变)；
///   Scale  ：绕锚点缩放，比例 = 当前距 / 按下时距。
/// </summary>
public sealed class GripDrag
{
    public readonly struct Dragged
    {
        public readonly SceneEntity Owner;
        public readonly int Index;
        public readonly double BaseX, BaseY;
        public Dragged(SceneEntity owner, int index, double bx, double by) { Owner = owner; Index = index; BaseX = bx; BaseY = by; }
    }

    public GripMode Mode { get; set; } = GripMode.Stretch;
    public bool Active { get; private set; }

    /// <summary>锚点夹点原坐标(拖动前)。</summary>
    public (double x, double y) Base { get; private set; }
    /// <summary>鼠标按下时的世界坐标(Rotate/Scale 的起始参照)。</summary>
    public (double x, double y) StartMouse { get; private set; }
    public SceneEntity? AnchorOwner { get; private set; }
    public int AnchorIndex { get; private set; } = -1;

    private readonly List<Dragged> _grips = new();
    public IReadOnlyList<Dragged> Grips => _grips;

    /// <summary>开始拖动：anchorIdx 为被按下的夹点；被拖集 = 表中全部选中夹点(锚点必在其中，选集异常时至少拖锚点)。</summary>
    public void Begin(GripTable table, int anchorIdx, double mouseX, double mouseY)
    {
        _grips.Clear();
        if (anchorIdx < 0 || anchorIdx >= table.Count) { Active = false; return; }
        var a = table.Grips[anchorIdx];
        AnchorOwner = a.Owner; AnchorIndex = a.Index;
        Base = (a.X, a.Y);
        StartMouse = (mouseX, mouseY);
        foreach (int si in table.SelectedIndices())
        {
            var g = table.Grips[si];
            _grips.Add(new Dragged(g.Owner, g.Index, g.X, g.Y));
        }
        if (_grips.Count == 0) _grips.Add(new Dragged(a.Owner, a.Index, a.X, a.Y));
        Active = true;
    }

    public void Cancel() { Active = false; _grips.Clear(); AnchorOwner = null; AnchorIndex = -1; }

    /// <summary>
    /// 改基点（AutoCAD 夹点提示下的「基点(B)」）：后续的位移/旋转/缩放/镜像都以新基点为准。
    /// 起始鼠标点一并挪过去，否则旋转/缩放会按"旧起始角/旧起始距"算出一跳。
    /// </summary>
    public void SetBase(double x, double y)
    {
        if (!Active) return;
        Base = (x, y);
        StartMouse = (x, y);
    }

    /// <summary>模式环：拉伸 → 移动 → 旋转 → 比例缩放 → 镜像 → 拉伸（同 AutoCAD 的夹点模式环）。</summary>
    public static GripMode NextMode(GripMode m) => m switch
    {
        GripMode.Stretch => GripMode.Move,
        GripMode.Move => GripMode.Rotate,
        GripMode.Rotate => GripMode.Scale,
        GripMode.Scale => GripMode.Mirror,
        _ => GripMode.Stretch,
    };

    public void CycleMode() => Mode = NextMode(Mode);

    /// <summary>命令行模式提示(原版 ModePrompt)。</summary>
    public static string ModePrompt(GripMode m) => m switch
    {
        GripMode.Stretch => "** 拉伸 **",
        GripMode.Move => "** 移动 **",
        GripMode.Rotate => "** 旋转 **",
        GripMode.Scale => "** 比例缩放 **",
        _ => "** 镜像 **",
    };

    /// <summary>各模式可在命令行键入的选项（同 AutoCAD：旋转/缩放多一项 参照(R)）。</summary>
    public static string OptionHint(GripMode m) => m is GripMode.Rotate or GripMode.Scale
        ? "[基点(B)/复制(C)/放弃(U)/参照(R)/退出(X)]"
        : "[基点(B)/复制(C)/放弃(U)/退出(X)]";

    /// <summary>拖动中的命令行提示(原版 "&lt;mode&gt; Specify point or [...]:")。</summary>
    public string Prompt => Mode switch
    {
        GripMode.Stretch => $"{ModePrompt(Mode)} 指定拉伸点 或 {OptionHint(Mode)}",
        GripMode.Move => $"{ModePrompt(Mode)} 指定移动点 或 {OptionHint(Mode)}",
        GripMode.Rotate => $"{ModePrompt(Mode)} 指定旋转角度 或 {OptionHint(Mode)}",
        GripMode.Scale => $"{ModePrompt(Mode)} 指定比例因子 或 {OptionHint(Mode)}",
        _ => $"{ModePrompt(Mode)} 指定镜像线的第二点 或 {OptionHint(Mode)}",
    };

    /// <summary>当前模式下光标处的"量"：Rotate=角度(弧度) / Scale=比例 / 其余=锚点位移长度。</summary>
    public double ValueAt(double wx, double wy)
    {
        switch (Mode)
        {
            case GripMode.Rotate:
            {
                double a0 = Math.Atan2(StartMouse.y - Base.y, StartMouse.x - Base.x);
                double a1 = Math.Atan2(wy - Base.y, wx - Base.x);
                return a1 - a0;
            }
            case GripMode.Scale:
            {
                double d0 = Math.Max(Dist(StartMouse.x, StartMouse.y, Base.x, Base.y), 1e-6);
                double d1 = Dist(wx, wy, Base.x, Base.y);
                return Math.Max(d1 / d0, 1e-6);
            }
            case GripMode.Mirror:
            {
                // 镜像的"量"= 镜像线方向角(弧度)。浮标上显示这个角, 便于配合正交拉出水平/垂直镜像线。
                return Math.Atan2(wy - Base.y, wx - Base.x);
            }
            default:
                return Dist(wx, wy, Base.x, Base.y);
        }
    }

    /// <summary>
    /// 按当前光标世界点计算变换后的实体：返回 (原实体, 新实体) 对，每个受影响实体一项。
    /// 不支持该夹点的实体(MoveGrip 返回 null)跳过。
    /// </summary>
    public List<(SceneEntity old, SceneEntity moved)> Preview(double wx, double wy)
    {
        var result = new List<(SceneEntity, SceneEntity)>();
        if (!Active) return result;

        if (Mode == GripMode.Stretch)
        {
            // 多夹点：全部选中项按锚点位移平移；同一实体的多个夹点依序套 MoveGrip(每次只改该夹点)。
            double dx = wx - Base.x, dy = wy - Base.y;
            var byOwner = new List<(SceneEntity owner, List<Dragged> gs)>();
            foreach (var g in _grips)
            {
                int k = byOwner.FindIndex(t => ReferenceEquals(t.owner, g.Owner));
                if (k < 0) byOwner.Add((g.Owner, new List<Dragged> { g }));
                else byOwner[k].gs.Add(g);
            }
            foreach (var (owner, gs) in byOwner)
            {
                SceneEntity cur = owner; bool ok = true;
                foreach (var g in gs)
                {
                    var m = cur.MoveGrip(g.Index, g.BaseX + dx, g.BaseY + dy);
                    if (m == null) { ok = false; break; }
                    cur = m;
                }
                if (ok && !ReferenceEquals(cur, owner)) result.Add((owner, cur));
            }
            return result;
        }

        Affine2 t;
        switch (Mode)
        {
            case GripMode.Move: t = Affine2.Translate(wx - Base.x, wy - Base.y); break;
            case GripMode.Rotate: t = Affine2.Rotate(ValueAt(wx, wy), Base.x, Base.y); break;
            // 镜像: 热夹点 = 镜像线第一点, 光标 = 第二点(同 AutoCAD)
            case GripMode.Mirror: t = Affine2.MirrorLine(Base.x, Base.y, wx, wy); break;
            default: t = Affine2.Scale(ValueAt(wx, wy), Base.x, Base.y); break;
        }
        // Move/Rotate/Scale 作用于被拖夹点所属的实体(单夹点时即锚点实体，与原版一致)。
        var seen = new List<SceneEntity>();
        foreach (var g in _grips)
        {
            if (seen.Contains(g.Owner)) continue;
            seen.Add(g.Owner);
            result.Add((g.Owner, g.Owner.Apply(t)));
        }
        return result;
    }

    private static double Dist(double x0, double y0, double x1, double y1)
    { double dx = x1 - x0, dy = y1 - y0; return Math.Sqrt(dx * dx + dy * dy); }
}
