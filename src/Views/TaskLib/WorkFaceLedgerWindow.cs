// 忠实移植自原 PitMine3D Modules/TaskLib/Features/WorkFaceLedgerWindow.xaml.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;            // 煤质数值解析（可空 + 容忍单位后缀）
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;     // WorkingFace / WorkingFaceRouting
using PitMine3D.Kylin.Data.Services;     // IWorkingFaceService / IWorkingFaceRoutingService
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
// 煤质草稿已随「台账读取层」提到引擎层（装配盘子也要用它，不能只有本窗口看得见）。
// 保留旧名以别名引入：本文件内的 QualityDraft 语义与位置都不变。
using QualityDraft = PitMine3D.Kylin.TaskLib.Engine.FaceQualityDraft;

namespace PitMine3D.Kylin.Views.TaskLib;
using SinkRegistryLoader = PitMine3D.Kylin.TaskLib.Engine.SinkRegistryLoader;   // 与 Kylin 旧切片 Data.SinkRegistryLoader 同名消歧

/// <summary>
/// 作业面台账：编制裂解装箱的「盘子」。
///
/// 一条作业面记录必须同时答出【源 — 物料 — 汇】三问，编组才有物理意义：
///   源(作业面/台阶) + 物料(MaterialMix) + 汇(SinkNode) → 运距 L_eq → 循环时间 T_c
///   → 最优配车数 n* = T_c/τ_L → 编组班产 → 装箱的 bin 大小。
/// 缺去向的面，运距与编组只能拍脑袋——故本窗口把「去向」做成硬约束下拉：
/// 下拉源是登记簿里在用的汇，且**只列该面物料允许进的**（sink.Accepts(spec)），
/// 「表土只能进表土堆场」这条复垦合规约束在界面上就选不错。
///
/// 改去向 = 真联动：立即重解运距（HaulResolver 三层兜底）与编组（FleetMatcher），
/// 运距 / 荐车 / 编组班产 三列当场刷新。
/// </summary>
public sealed class WorkFaceLedgerWindow : Window
{
    // ── Avalonia 控件（对应原 XAML 里的 x:Name）────────────────────────────
    private readonly DataGrid grid = TaskUi.Grid(readOnly: false, single: true, frozen: 1);
    private readonly TextBlock toolStatus = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock warnLine = new() { Foreground = TaskUi.Red, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock sumLine = new() { TextWrapping = TextWrapping.Wrap };
    private CheckBox chkAdvance = null!, chkCoord = null!, chkQuality = null!;
    private DataGridColumn colAzimuth = null!, colMiningWidth = null!, colSrcX = null!, colSrcY = null!, colSrcZ = null!, colAsh = null!, colCv = null!, colSulfur = null!, colMoisture = null!;

    private ExploderConfig _cfg = new();
    private List<FaceRow> _rows = new();

    /// <summary>
    /// 从台账读回的煤质草稿（face_code → 四项各自可空）。
    /// 单独存一份而不是塞进 FaceInput.Quality：那个契约的四项都是非空 double，
    /// 装不下「只填了灰分」这种半份状态，而 working_face_routing 的四列本就各自可空。
    /// </summary>
    private readonly Dictionary<string, QualityDraft> _drafts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本面的煤质草稿：台账存过就用存档（可能是半份），没存过就从盘子里的目标反推。</summary>
    private QualityDraft DraftFor(FaceInput f)
        => !string.IsNullOrWhiteSpace(f.Zone) && _drafts.TryGetValue(f.Zone.Trim(), out var d)
            ? d
            : QualityDraft.From(f.Quality);

    /// <summary>去向下拉项。Node 为 null 表示「未指定卸点」。</summary>
    public sealed class SinkOption
    {
        public SinkNode? Node { get; init; }
        public string Caption { get; init; } = "";
        public string Id => Node?.Id ?? "";
    }

    /// <summary>台账一行 = 一个 FaceInput 的视图 + 去向改选后的联动重算。</summary>
    public sealed class FaceRow : INotifyPropertyChanged
    {
        private readonly FaceInput _face;
        private readonly Action<FaceRow> _onChanged;
        private SinkOption? _selected;

        /// <summary>本面的煤质目标草稿（四项各自可空；半份也留着，见 <see cref="QualityDraft"/>）。</summary>
        private readonly QualityDraft _quality;

        /// <summary>本面运距的来源层文案（HaulLeg.Source）；坐标列的提示要靠它说明"运距怎么来的"。</summary>
        private string _legSource = "";

        public FaceRow(FaceInput face, IEnumerable<SinkNode> sinks, Action<FaceRow> onChanged, QualityDraft? quality = null)
        {
            _face = face;
            _onChanged = onChanged;
            SinkOptions = BuildOptions(face, sinks);
            _selected = SinkOptions.FirstOrDefault(o => o.Node != null && Matches(o.Node!, face))
                        ?? SinkOptions.FirstOrDefault(o => o.Node == null);

            // 草稿优先（存档里可能是半份，face.Quality 装不下）；没给草稿就从面上的目标反推。
            _quality = quality ?? QualityDraft.From(face.Quality);
            SyncQualityToFace();

            // 这条腿的运距是从哪一层来的（"路网(录入坐标)"/"路网(名称匹配)"/"手填"/"兜底"）——
            // 坐标列的提示要如实说清"你现在这个运距是怎么来的"，故装一份，改坐标/改去向时刷新。
            _legSource = SafeLegSource();
        }

        /// <summary>当前草稿的副本（保存时按四列各自写库，半份也照存）。</summary>
        internal QualityDraft QualitySnapshot => _quality.Clone();

        // ── 只读列 ──
        public string Zone => _face.Zone;
        public string Process => _face.Process.Label();
        public string Bench => Math.Abs(_face.BenchElevationM) > 1e-6 ? $"+{_face.BenchElevationM:0}" : "—";
        public string Material => _face.ResolvedMix.Caption is { Length: > 0 } c ? c : "—";
        public string Ep => string.IsNullOrWhiteSpace(_face.EngineeringPositionId) ? "—" : _face.EngineeringPositionId;
        public string MainEquip => string.IsNullOrWhiteSpace(_face.Group.MainEquipment) ? "—" : _face.Group.MainEquipment;
        public string OnSiteTrucks => _face.Group.Trucks.Count > 0 ? $"{_face.Group.Trucks.Count}" : "—";

        // ── 空间身份与备采家底（V041）──
        //  单元号可编辑：它是本面绑到图上哪个体的唯一钥匙，没有它任务落不回图纸。
        //  不做成下拉：基表可能有几十上百个单元，且改模型后单元会变，硬绑成下拉反而挡人；
        //  合不合法在保存时按基表核（见 UnitIdNote），当场给提示但不拦。
        public string UnitId
        {
            get => _face.UnitId ?? "";
            set
            {
                string v = (value ?? "").Trim();
                if (string.Equals(v, _face.UnitId ?? "", StringComparison.Ordinal)) return;
                _face.UnitId = v;
                Raise(nameof(UnitId));
                Raise(nameof(UnitNote));
                _onChanged(this);
            }
        }

        /// <summary>单元号在基表里认不认得（"—"=没填或没有基表；"✓"=对得上；"?"=基表里没有这个号）。</summary>
        public string UnitNote => MiningUnitLink.Note(_face.UnitId);

        /// <summary>备采储量 m³（原位实方）。空/0 = 未录，<b>不是采空</b>。</summary>
        public string Reserve
        {
            get => _face.AvailableReserveM3 > 1e-6 ? $"{_face.AvailableReserveM3:0}" : "";
            set
            {
                string v = (value ?? "").Trim();
                double d = 0;
                if (v.Length > 0 && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return;
                if (Math.Abs(d - _face.AvailableReserveM3) < 1e-6) return;
                _face.AvailableReserveM3 = Math.Max(0, d);
                Raise(nameof(Reserve));
                Raise(nameof(PreparedDays));
                _onChanged(this);
            }
        }

        /// <summary>按当日目标还能采几天。未录备采或今天不采这个面时显示"—"，绝不显示 0。</summary>
        public string PreparedDays => _face.PreparedDays is { } d ? $"{d:0.0}" : "—";

        /// <summary>
        /// 推进方位 °（正北起顺时针）。空 = 未录 —— <b>0° 是合法方位（正北）</b>，
        /// 所以这一列必须能区分"填了 0"和"没填"，故用 string 而不是 double。
        /// </summary>
        public string Azimuth
        {
            get => _face.AdvanceAzimuthDeg is { } a ? $"{a:0.#}" : "";
            set
            {
                string v = (value ?? "").Trim();
                if (v.Length == 0) { _face.AdvanceAzimuthDeg = null; Raise(nameof(Azimuth)); _onChanged(this); return; }
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return;
                // 归一到 [0,360)：填 370 与 10 是同一个方向，存两个值日后按方位分组会分成两组
                d %= 360; if (d < 0) d += 360;
                _face.AdvanceAzimuthDeg = d;
                Raise(nameof(Azimuth));
                _onChanged(this);
            }
        }

        /// <summary>本面当前推进宽 m。空 = 未录。</summary>
        public string MiningWidth
        {
            get => _face.MiningWidthM is { } w ? $"{w:0.#}" : "";
            set
            {
                string v = (value ?? "").Trim();
                if (v.Length == 0) { _face.MiningWidthM = null; Raise(nameof(MiningWidth)); _onChanged(this); return; }
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d < 0) return;
                _face.MiningWidthM = d;
                Raise(nameof(MiningWidth));
                _onChanged(this);
            }
        }

        // ── 随去向联动刷新的列 ──
        public string Haul => _face.EffectiveHaulKm > 1e-6 ? $"{_face.EffectiveHaulKm:0.##}" : "未解出";
        public string TargetM3 => _face.DayTargetM3 > 1e-6 ? $"{_face.DayTargetM3:0}" : (_face.DerivedFromInbound ? "入方推导" : "0");
        public string TargetT => _face.DayTargetM3 > 1e-6 ? $"{_face.TargetTonnageT:0}" : "—";
        public string RecTrucks => _face.Group.RecommendedTrucks > 0 ? $"{_face.Group.RecommendedTrucks}" : "—";
        public string GroupCap => _face.Group.GroupCapacityM3PerH > 1e-6 ? $"{_face.Group.GroupCapacityM3PerH:0.#}" : "—";

        public bool HasDestination => _face.HasDestination;

        // ── 煤质目标四列（V036，可编辑；留空 = 该项无目标）──────────────────────
        //  绑的是 string 而不是 double?：TextBox 绑 double? 时清空会走进验证失败的分支，
        //  格子里留着红框和旧值，"留空 = 无目标"这条根本录不进去。改成自己解析，
        //  空 → null（清空）、非法 → 保持原值（格子当场弹回，用户看得见没生效）。

        public string AshText
        {
            get => Fmt(_quality.Ash);
            set { if (SetQuality(v => _quality.Ash = v, value)) Raise(nameof(AshText)); }
        }

        public string CvText
        {
            get => Fmt(_quality.Cv);
            set { if (SetQuality(v => _quality.Cv = v, value)) Raise(nameof(CvText)); }
        }

        public string SulfurText
        {
            get => Fmt(_quality.Sulfur);
            set { if (SetQuality(v => _quality.Sulfur = v, value)) Raise(nameof(SulfurText)); }
        }

        public string MoistureText
        {
            get => Fmt(_quality.Moisture);
            set { if (SetQuality(v => _quality.Moisture = v, value)) Raise(nameof(MoistureText)); }
        }

        /// <summary>只填了一部分 ⇒ 界面标琥珀斜体（存得下，但不构成有效目标）。</summary>
        public bool QualityPartial => _quality.IsPartial;

        public string QualityTip
        {
            get
            {
                if (_quality.IsEmpty)
                    return "本面无煤质目标（纯量矿口径）。\n\n"
                         + "四项（灰分% / 热值MJ·kg⁻¹ / 硫% / 水%）**全部**填齐才构成一个有效目标，"
                         + "交给配煤约束使用；留空的项不参与校核。";

                if (_quality.IsComplete)
                    return $"煤质目标：{_face.Quality?.Caption ?? ""}　水 {_quality.Moisture:0.#}%\n\n"
                         + "四项齐全，已作为有效目标交给配煤约束。";

                var miss = new List<string>();
                if (!_quality.Ash.HasValue) miss.Add("灰分");
                if (!_quality.Cv.HasValue) miss.Add("热值");
                if (!_quality.Sulfur.HasValue) miss.Add("硫");
                if (!_quality.Moisture.HasValue) miss.Add("水");

                return $"⚠ 煤质目标只填了一部分，还缺：{string.Join("、", miss)}。\n\n"
                     + "已填的数会照常存进台账（四列各自可空，不会丢），但**本面暂不构成有效的煤质目标**：\n"
                     + "半份目标若硬凑成一条，没填的项会被当成 0——配煤约束就变成「灰分 ≤0」这种几乎恒假、"
                     + "「热值 ≥0」这种恒真的胡话，比不设约束更难查。\n"
                     + "填齐四项即自动生效。";
            }
        }

        // ── 源坐标三列（V037，可编辑）────────────────────────────────────────────
        //  路网求运距要求【源汇两端都能定位到 RoadNode】。汇端的坐标 V036 已补齐，
        //  源端（这个面从哪装车）就是这三列。缺了它，路网只能拿作业面名 / 工程位置号
        //  去碰节点 RefId，碰不上整条链就落到兜底运距 —— 而运距 L_eq 是循环时间的输入：
        //      T_c = t_装 + 60·L_eq/v_重 + t_卸 + 60·L_eq/v_空 + t_调,  n* = T_c/τ_L,
        //  n* 定编组班产、编组班产是编制裂解装箱的 bin，一路偏下去。
        //
        //  解析纪律与煤质四列相同（绑 string 自己解析；绑 double 时清空会卡在验证失败分支），
        //  但两处不同：
        //   ★ 坐标允许负值 —— 矿区坐标系里负坐标合法，把负号当非法会把人的手误变成静默丢数据；
        //   ★ 留空 = 0 = 未录，判据只看 X、Y（Z=0 是合法标高），与 FaceInput.HasSourcePosition 一致。

        public string SrcXText
        {
            get => FmtCoord(_face.SourceX);
            set { if (SetSource(v => _face.SourceX = v, value)) Raise(nameof(SrcXText)); }
        }

        public string SrcYText
        {
            get => FmtCoord(_face.SourceY);
            set { if (SetSource(v => _face.SourceY = v, value)) Raise(nameof(SrcYText)); }
        }

        public string SrcZText
        {
            get => FmtCoord(_face.SourceZ);
            set { if (SetSource(v => _face.SourceZ = v, value)) Raise(nameof(SrcZText)); }
        }

        /// <summary>未录源坐标（X、Y 均为 0）⇒ 界面标琥珀斜体。判据与 HaulResolver 一致。</summary>
        public bool NoSrcCoord => !_face.HasSourcePosition;

        /// <summary>
        /// 坐标是**系统自动推导**的（按可采区域质心），不是人填的 ⇒ 灰斜体。
        /// 用户有权知道哪个数是自己填的、哪个是系统猜的：猜的那份不入库，
        /// 区域边界一改它就跟着变；要钉死就在这儿改一遍，改完即成人工值。
        /// </summary>
        public bool AutoSrcCoord => !NoSrcCoord && HaulResolver.IsAutoSourced(_face, out _);

        public string SrcCoordTip
        {
            get
            {
                string haul = _legSource.Length > 0
                    ? $"\n\n本面当前运距 {_face.EffectiveHaulKm:0.##} km，来自【{_legSource}】。"
                    : "";

                if (NoSrcCoord)
                    return "⚠ 源端无坐标（X、Y 均为 0 = 未录，不是原点）。\n\n"
                         + "后果：路网只能靠名字匹配节点（拿作业面名 / 工程位置号去对节点 RefId），"
                         + "匹配不上运距就落兜底值，循环时间 / 配车数 / 编组班产会跟着偏。\n\n"
                         + "两条补法：① 在此直接录入铲位坐标（与路网节点、去向坐标同一坐标系，单位 m）；"
                         + "② 在「采场/排土场圈定」里圈出同名区域，系统会按区域质心自动推导。"
                         + haul;

                if (AutoSrcCoord)
                {
                    HaulResolver.IsAutoSourced(_face, out string region);
                    return $"◇ 这组坐标是**系统自动推导**的：取自可采区域「{region}」的代表点，"
                         + "标高取本面台阶标高。\n\n"
                         + "代表点按区域多边形的面积质心取——那是整块区域的中心，不是真实铲位。\n\n"
                         + "它**不入库**（working_face_routing.source_x/y/z 只存人工录的）——"
                         + "区域边界改了推导值要跟着改，不该被一份陈旧的猜测钉死。\n\n"
                         + "要精确到铲位，在此直接改成实测坐标即可"
                         + "（改完就成人工值，保存时落库，之后不再被推导覆盖）。"
                         + haul;
                }

                return "源端代表点（铲位/装载点），与路网节点、去向坐标同一坐标系，单位 m。\n"
                     + "人工录入的坐标权威最高：程序不会用区域质心覆盖它，保存时写入 "
                     + "working_face_routing.source_x/y/z。\n"
                     + "Z 是本面台阶标高（Z=0 是合法标高，不参与「有没有坐标」的判定）。"
                     + haul;
            }
        }

        /// <summary>坐标 → 单元格文本。整组没录时零值显示空串（0 是"未录"不是"坐标就是 0"）。</summary>
        private string FmtCoord(double v)
            => _face.HasSourcePosition || Math.Abs(v) > 1e-9 ? v.ToString("0.##") : "";

        /// <summary>
        /// 改一项源坐标：解析成功才落到面上，随后
        /// ① 把本面从"系统推导"里摘出去（人填的从此说了算）；② 重解运距与编组。
        /// 返回 false = 输入非法，坐标不动（格子当场弹回原值，用户看得见没生效）。
        /// </summary>
        private bool SetSource(Action<double> assign, string? raw)
        {
            if (!TryParseCoord(raw, out double v)) return false;
            assign(v);

            try { HaulResolver.ClearAutoSource(_face); } catch { }
            RecalcHaulOnSourceMoved();

            Raise(nameof(NoSrcCoord)); Raise(nameof(AutoSrcCoord)); Raise(nameof(SrcBrush)); Raise(nameof(SrcStyle)); Raise(nameof(SrcCoordTip));
            Raise(nameof(SrcXText)); Raise(nameof(SrcYText)); Raise(nameof(SrcZText));
            Raise(nameof(Haul)); Raise(nameof(RecTrucks)); Raise(nameof(GroupCap));
            Raise(nameof(TruckShortage)); Raise(nameof(TrucksBrush)); Raise(nameof(TrucksWeight)); Raise(nameof(TrucksTip)); Raise(nameof(Tip));
            _onChanged(this);
            return true;
        }

        /// <summary>
        /// 坐标解析：空 = 0（未录）；负数合法；NaN/∞ 非法。
        /// 容忍用户连单位一起敲（"620300 m"）与全角数字外的常见杂字符。
        /// </summary>
        private static bool TryParseCoord(string? raw, out double val)
        {
            val = 0;
            string s = (raw ?? "").Trim();
            if (s.Length == 0 || s == "—" || s == "-" || s == "－") return true;   // 清空 = 未录

            s = s.TrimEnd('m', 'M', '米').Trim();
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && !double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return false;

            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            val = v;
            return true;
        }

        /// <summary>
        /// 源坐标动了 ⇒ 重解运距与编组。
        /// ★ 只在新解**确实来自路网**时才改写运距：解不出路网时清零重解只会拿到兜底值，
        ///   把台账里原有的（存档/手填）运距冲成一个更差的数——录个坐标不该把已有的数弄丢。
        /// </summary>
        private void RecalcHaulOnSourceMoved()
        {
            var node = _selected?.Node;
            if (node == null) return;

            double keepKm = _face.HaulDistanceKm, keepEq = _face.EquivHaulKm;
            try
            {
                _face.HaulDistanceKm = 0;
                _face.EquivHaulKm = 0;

                var leg = HaulResolver.Resolve(_face, node);

                if (leg.Feasible && (leg.Source ?? "").Contains("路网"))
                {
                    _face.HaulDistanceKm = Math.Round(leg.Km, 3);
                    _face.EquivHaulKm = Math.Round(leg.EquivKm, 3);
                }
                else
                {
                    // 路网还是解不出来：保留原值（原值为空才用这次解出的兜底值，免得整行没数），
                    // 再按还原后的口径解一次 —— 否则编组会拿着兜底运距算，台账上显示的却是原值，
                    // 一行里两个运距，配车数就成了对不上账的数。
                    _face.HaulDistanceKm = keepKm > 1e-6 ? keepKm : Math.Round(leg.Km, 3);
                    _face.EquivHaulKm = keepEq > 1e-6 ? keepEq : Math.Round(leg.EquivKm, 3);
                    leg = HaulResolver.Resolve(_face, node);
                }
                _legSource = leg.Source ?? "";

                if (_face.Process == ProcessType.Load)
                {
                    var m = FleetMatcher.Match(_face, leg);
                    _face.Group.RecommendedTrucks = m.OptimalTrucks;
                    _face.Group.GroupCapacityM3PerH = m.Group.GroupCapacityM3PerH;
                }
            }
            catch
            {
                // 求解器不可用 → 原样还回去，绝不因为一次求解失败把台账里的运距抹掉
                _face.HaulDistanceKm = keepKm;
                _face.EquivHaulKm = keepEq;
            }
        }

        /// <summary>当前运距来自哪一层（读一次即可，改坐标/改去向时刷新）。求解器不可用返回空串。</summary>
        private string SafeLegSource()
        {
            try
            {
                var node = _selected?.Node;
                if (node == null) return "";
                return HaulResolver.Resolve(_face, node).Source ?? "";
            }
            catch { return ""; }
        }

        // ── 实配车号（V036，可编辑）──────────────────────────────────────────────
        //  现场实际配的车。与「荐车」（FleetMatcher 求解出的 n*）之差就是运力盈亏：
        //  配 < 荐 ⇒ TaskExploder 报「运力不足·铲将待车」。故这一列必须能录真数据。

        public string TrucksText
        {
            get => string.Join(",", _face.Group.Trucks);
            set
            {
                var list = ParseTrucks(value);
                if (list.SequenceEqual(_face.Group.Trucks, StringComparer.OrdinalIgnoreCase)) return;

                // 直接改编组里的实配车列表：程序只写建议值，实配一律以这里录的为准
                _face.Group.Trucks.Clear();
                _face.Group.Trucks.AddRange(list);

                Raise(nameof(TrucksText)); Raise(nameof(OnSiteTrucks));
                Raise(nameof(TruckShortage)); Raise(nameof(TrucksBrush)); Raise(nameof(TrucksWeight)); Raise(nameof(TrucksTip)); Raise(nameof(Tip));
                _onChanged(this);
            }
        }

        // ── Avalonia 单元格观感（原 XAML 的 ShortageCell / CoordCell / QualityCell 三个 CellStyle）──
        public IBrush TrucksBrush => TruckShortage ? TaskUi.Red : BodyBrush;
        public FontWeight TrucksWeight => TruckShortage ? FontWeight.Bold : FontWeight.Normal;
        public IBrush SrcBrush => NoSrcCoord ? TaskUi.Amber : AutoSrcCoord ? TaskUi.Muted : BodyBrush;
        public FontStyle SrcStyle => NoSrcCoord || AutoSrcCoord ? FontStyle.Italic : FontStyle.Normal;
        public IBrush QualityBrush => QualityPartial ? TaskUi.Amber : BodyBrush;
        public FontStyle QualityStyle => QualityPartial ? FontStyle.Italic : FontStyle.Normal;
        private static IBrush BodyBrush => TaskUi.BodyBrush;

        /// <summary>配 &lt; 荐 ⇒ 运力不足（引擎据此报"铲将待车"）。排土面不编卡车，不参与判定。</summary>
        public bool TruckShortage
            => _face.Process == ProcessType.Load
            && _face.Group.RecommendedTrucks > 0
            && _face.Group.Trucks.Count < _face.Group.RecommendedTrucks;

        public string TrucksTip
        {
            get
            {
                int on = _face.Group.Trucks.Count, rec = _face.Group.RecommendedTrucks;
                string head = $"实配 {on} 辆 / 建议 {rec} 辆" + (on > 0 ? $"：{string.Join(" / ", _face.Group.Trucks)}" : "（未配车）");

                if (TruckShortage)
                    return head + $"\n\n⚠ 运力不足：缺 {rec - on} 辆。铲装能力放着用不上，铲将待车——"
                                + "编制裂解时会报 TruckShortage，班产按车队运力（而非铲装能力）裁。\n\n"
                                + "录入格式：车号逗号分隔，如 T-01,T-02；留空 = 未配车。";

                return head + (on > rec && rec > 0 ? "\n\n配车多于建议：多出来的车会在卸点排队，边际产能有限。" : "")
                            + "\n\n录入格式：车号逗号分隔，如 T-01,T-02；留空 = 未配车。";
            }
        }

        // ── 混采面的分项去向（只读展示）────────────────────────────────────
        //  一台铲挖混采料，煤去破碎站、岩去排土场——一条任务多个去向，拆面会撞「设备双占」。
        //  下拉框只能选一个，所以它选的是**主去向**；完整分项由流向分配求解并写回 face.Splits，
        //  这里如实展示，免得台账上看着"这个面只往破碎站拉"。

        /// <summary>分项摘要："煤 60% → 1号破碎站 · 硬岩 40% → 内排场"；单去向为空串。</summary>
        public string SplitSummary
        {
            get
            {
                if (_face.Splits.Count < 2) return "";
                return string.Join(" · ", _face.Splits.Select(d => d.HasDestination
                    ? d.Caption
                    : $"{d.Spec.Name} {d.Fraction * 100:0.#}% → ⚠ 未定"));
            }
        }

        public bool HasSplits => SplitSummary.Length > 0;

        /// <summary>本面各物料是否都已定去向（混采面缺任一项都算没定完）。</summary>
        public bool AllRouted => _face.AllMaterialsRouted;

        public string SinkTip
        {
            get
            {
                string head = _selected?.Node is { } n
                    ? $"{n.Caption} · {n.CapacityCaption} · 兜底运距 {n.FallbackHaulKm:0.##}km"
                    : "未指定卸点：运距与编组无法核算（下拉只列该面物料允许进的去向）";

                if (!HasSplits) return head;

                // 混采面：说清下拉选的只是主去向，完整去向以求解出的分项为准
                var lines = _face.Splits.Select(d =>
                {
                    double m3 = _face.DayTargetM3 * d.Fraction;
                    return d.HasDestination
                        ? $"　{d.Spec.Name} {d.Fraction * 100:0.#}%　→　"
                          + $"{(string.IsNullOrWhiteSpace(d.DestinationName) ? d.DestinationId : d.DestinationName)}"
                          + $"（{d.DestinationKind.Label()}）　{m3:N0} m³实方 · 运距 {d.EffectiveHaulKm:0.##} km"
                        : $"　{d.Spec.Name} {d.Fraction * 100:0.#}%　→　⚠ 未指定卸点（{m3:N0} m³实方无处可去）";
                });

                return head
                     + "\n\n本面为混采面，完整去向由流向分配求解，见下列明细（此处下拉选的是【主去向】，"
                     + "改选只改主去向，不改其它物料的分项）：\n"
                     + string.Join("\n", lines);
            }
        }

        public string Tip =>
            $"{_face.Group.Caption}\n物料：{_face.ResolvedMix.Caption}"
            + $"\n吨量 {_face.TargetTonnageT / 1e4:0.####} 万t · 松方 {_face.ResolvedMix.ToLooseM3(_face.DayTargetM3):0} m³ · 排弃占容 {_face.WasteDumpM3:0} m³"
            + (_face.Quality != null ? $"\n质量目标：{_face.Quality.Caption}" : "")
            + (_face.DerivedFromInbound ? "\n（排土面：日目标由入方物料流推导，不手工填）" : "");

        public List<SinkOption> SinkOptions { get; }

        /// <summary>改选去向 ⇒ 写回作业面 → 重解运距 → 重解编组 → 刷新相关列。</summary>
        public SinkOption? SelectedSink
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value)) return;
                _selected = value;
                ApplyDestination(value?.Node);
                Raise(nameof(SelectedSink)); Raise(nameof(SinkTip)); Raise(nameof(HasDestination));
                Raise(nameof(Haul)); Raise(nameof(RecTrucks)); Raise(nameof(GroupCap));
                // 荐车重解了 ⇒「配 < 荐」的判定跟着变，运力不足的标红必须同步刷新
                Raise(nameof(TruckShortage)); Raise(nameof(TrucksBrush)); Raise(nameof(TrucksWeight)); Raise(nameof(TrucksTip));
                Raise(nameof(TargetM3)); Raise(nameof(TargetT)); Raise(nameof(Tip));
                Raise(nameof(SplitSummary)); Raise(nameof(HasSplits)); Raise(nameof(AllRouted));
                // 换了汇 ⇒ 运距来自哪一层也可能变（换到有坐标的汇就能走路网了），坐标列的提示要跟上
                Raise(nameof(SrcCoordTip));
                _onChanged(this);
            }
        }

        /// <summary>去向变了，旧运距即作废：先清空再重解，否则 HaulResolver 会把上一个汇的手填值当成有效值返回。</summary>
        private void ApplyDestination(SinkNode? node)
        {
            _face.DestinationId = node?.Id ?? "";
            _face.DestinationName = node?.Name ?? "";
            if (node != null) _face.DestinationKind = node.Kind;
            _face.HaulDistanceKm = 0;
            _face.EquivHaulKm = 0;

            if (node == null) { _legSource = ""; return; }   // 无汇：运距/编组无从算起，保留原班产不动

            try
            {
                var leg = HaulResolver.Resolve(_face, node);
                _legSource = leg.Source ?? "";
                if (leg.Feasible)
                {
                    _face.HaulDistanceKm = Math.Round(leg.Km, 3);
                    _face.EquivHaulKm = Math.Round(leg.EquivKm, 3);
                }

                if (_face.Process == ProcessType.Load)
                {
                    var m = FleetMatcher.Match(_face, leg);
                    // 只写建议值与班产；Trucks 是现场实配，程序不改（配/荐之差正是「运力不足」的判据）。
                    _face.Group.RecommendedTrucks = m.OptimalTrucks;
                    _face.Group.GroupCapacityM3PerH = m.Group.GroupCapacityM3PerH;
                }
            }
            catch { /* 求解器不可用 → 保留已有值，界面显示原数 */ }
        }

        /// <summary>
        /// 候选去向 = 在用 + 接纳本面物料。混采面按**任一组分可进**取并集
        /// （煤6∶岩4 的面既可选破碎站也可选排土场）；单一物料面即严格约束。
        /// 面上已选的汇即便不满足过滤也保留，免得静默丢掉人工判断。
        /// </summary>
        private static List<SinkOption> BuildOptions(FaceInput face, IEnumerable<SinkNode> sinks)
        {
            var list = new List<SinkOption> { new() { Node = null, Caption = "（未指定卸点）" } };
            var specs = face.ResolvedMix.Split(1.0).Select(x => x.Spec).ToList();

            foreach (var s in sinks)
            {
                bool ok = specs.Count == 0 || specs.Any(sp => s.Accepts(sp));
                if (!ok && !Matches(s, face)) continue;
                list.Add(new SinkOption { Node = s, Caption = $"{s.Name}（{s.Kind.Label()}）" });
            }
            return list;
        }

        private static bool Matches(SinkNode s, FaceInput f)
            => (!string.IsNullOrWhiteSpace(f.DestinationId) && string.Equals(s.Id, f.DestinationId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(f.DestinationName) && string.Equals(s.Name, f.DestinationName, StringComparison.OrdinalIgnoreCase));

        // ── 煤质 / 车号的解析与联动 ──────────────────────────────────────────────

        /// <summary>
        /// 改一项煤质：解析成功才落草稿，随后重算「这份草稿够不够格当有效目标」。
        /// 返回 false = 输入非法，草稿不动（格子会弹回原值，用户看得见没生效）。
        /// </summary>
        private bool SetQuality(Action<double?> assign, string? raw)
        {
            if (!TryParseOptional(raw, out double? v)) return false;
            assign(v);
            SyncQualityToFace();
            Raise(nameof(QualityPartial)); Raise(nameof(QualityBrush)); Raise(nameof(QualityStyle)); Raise(nameof(QualityTip)); Raise(nameof(Tip));
            _onChanged(this);
            return true;
        }

        /// <summary>
        /// 草稿 → <c>FaceInput.Quality</c>：★ 四项齐全才给一个 CoalQuality，否则一律 null。
        /// null 的含义是「本面无煤质目标」（纯量矿口径），配煤约束据此跳过本面——
        /// 这正是半份目标该有的待遇：存下来，但不生效。
        /// </summary>
        private void SyncQualityToFace() => _face.Quality = _quality.ToQuality();

        /// <summary>草稿值 → 单元格文本。null（没填）显示空串，不显示 0——0 是"目标就是 0"，含义完全不同。</summary>
        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.###") : "";

        /// <summary>
        /// 可空数值解析。空/破折号 = 清空（返回 true + null）；非法 = 返回 false（调用方保持原值）。
        /// 容忍用户连单位一起敲（"12.5%"）与中英文百分号；负数按非法处理——
        /// 灰分/热值/硫/水没有负值，当成"清空"会把用户的手误变成静默的数据丢失。
        /// </summary>
        private static bool TryParseOptional(string? raw, out double? val)
        {
            val = null;
            string s = (raw ?? "").Trim();
            if (s.Length == 0 || s == "—" || s == "-" || s == "－") return true;   // 清空

            s = s.TrimEnd('%', '％').Trim();
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && !double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return false;

            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) return false;
            val = v;
            return true;
        }

        /// <summary>
        /// 车号串 → 车号表。录入侧与读库侧共用同一套解析（<see cref="FaceLedgerLoader.SplitTrucks"/>），
        /// 免得两处分隔符规则各写一份、日后改一边漏一边。
        /// </summary>
        private static List<string> ParseTrucks(string? raw) => FaceLedgerLoader.SplitTrucks(raw);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public WorkFaceLedgerWindow()
    {
        Title = "作业面台账 — 日常生产组织";
        TaskUi.Place(this, 1560, 660);
        MinWidth = 960; MinHeight = 480;
        Content = BuildLayout();
        ApplyColumnGroups();
        Opened += (_, _) => Reload();
    }

    /// <summary>照原 XAML 的四行：抬头带 / 工具条 / 表格 / 底栏。</summary>
    private Control BuildLayout()
    {
        var header = TaskUi.Header("作业面台账", "源—物料—汇 三者齐备的'盘子'：作业面 + 物料 + 去向 + 运距 + 编组 — 喂编制裂解装箱");

        // 工具条：左两钮 · 右三勾 · 中间状态句
        var tool = new DockPanel();
        var add = TaskUi.Btn("新增作业面", () => OnAdd(), 100);
        var save = TaskUi.Btn("保存台账", () => OnSave(), 90);
        ToolTip.SetTip(save, "写回台账：去向/运距/日目标/混采分项 → working_face_routing；主设备与物料 → working_face（按 face_code 匹配同名作业面）。\n台阶标高不写 working_face.bench_height_m——那是台阶高度，两者口径不同。");
        DockPanel.SetDock(add, Avalonia.Controls.Dock.Left); DockPanel.SetDock(save, Avalonia.Controls.Dock.Left);
        tool.Children.Add(add); tool.Children.Add(save);

        // 列分组开关：26 列合计 2066 DIP，默认窗宽只装得下 1523，横滚是躲不掉的。
        // 三组「录入列」可整组收起：核心列（源—物料—汇—运距—编组）一收就正好一屏摆开。默认全开。
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var showLbl = TaskUi.Hint("显示", 12.5); showLbl.VerticalAlignment = VerticalAlignment.Center; showLbl.Margin = new Thickness(0, 0, 8, 0);
        toggles.Children.Add(showLbl);
        chkAdvance = TaskUi.Check("推进参数", true, ApplyColumnGroups, "方位(°) / 推进宽(m) 两列");
        chkCoord = TaskUi.Check("源坐标", true, ApplyColumnGroups, "源X / 源Y / 源Z 三列 —— 收起不影响已录的值，路网照样按它求运距");
        chkQuality = TaskUi.Check("煤质目标", true, ApplyColumnGroups, "灰分 / 热值 / 硫 / 水 四列 —— 收起不影响已录的值");
        toggles.Children.Add(chkAdvance); toggles.Children.Add(chkCoord); toggles.Children.Add(chkQuality);
        DockPanel.SetDock(toggles, Avalonia.Controls.Dock.Right);
        tool.Children.Add(toggles);

        TaskUi.Theme(toolStatus, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        toolStatus.Bind(ToolTip.TipProperty, new Binding(nameof(TextBlock.Text)) { Source = toolStatus });
        tool.Children.Add(toolStatus);

        BuildColumns();

        var foot = new StackPanel();
        TaskUi.Theme(sumLine, TextBlock.ForegroundProperty, "Theme.Text.Body");
        foot.Children.Add(warnLine); foot.Children.Add(sumLine);

        var g = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var toolBar = TaskUi.Bar(tool, top: true);
        var footBar = TaskUi.Bar(foot, top: false, padY: 8);
        Grid.SetRow(header, 0); Grid.SetRow(toolBar, 1); Grid.SetRow(grid, 2); Grid.SetRow(footBar, 3);
        g.Children.Add(header); g.Children.Add(toolBar); g.Children.Add(grid); g.Children.Add(footBar);
        TaskUi.Theme(this, BackgroundProperty, "Theme.Window.Background");
        return g;
    }

    /// <summary>
    /// 26 列，全部定宽（原 XAML 注释：本表一列星号都不能有——装不下时星号列会把每个定宽列各扣一刀，
    /// 表头全被切；全定宽 + 真横滚 + 钉住第一列，宽度才说了算）。列宽逐一照原值。
    /// </summary>
    private void BuildColumns()
    {
        var c = grid.Columns;
        c.Add(TaskUi.TextCol("作业面/采区", nameof(FaceRow.Zone), 186));
        c.Add(TaskUi.TextCol("工序", nameof(FaceRow.Process), 48));
        c.Add(TaskUi.TextCol("台阶(m)", nameof(FaceRow.Bench), 64));

        // 单元号（V041）：可编辑 + 右侧对号标记（✓ 对得上 · ? 基表里没有 · — 没填或还没有基表）。只标记不拦。
        var unit = new DataGridTemplateColumn { Header = TaskUi.Head("单元号"), Width = new DataGridLength(100) };
        unit.CellTemplate = new FuncDataTemplate<FaceRow>((_, _) =>
        {
            var dp = new DockPanel { LastChildFill = true };
            var note = new TextBlock { Width = 16, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            TaskUi.Theme(note, TextBlock.ForegroundProperty, "Theme.Text.Muted");
            note.Bind(TextBlock.TextProperty, new Binding(nameof(FaceRow.UnitNote)));
            ToolTip.SetTip(note, "✓ 基表里有这个单元　? 基表里没有（模型可能已重算重划）　— 未填或还没有基表");
            DockPanel.SetDock(note, Avalonia.Controls.Dock.Right);
            var tx = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            TaskUi.Theme(tx, TextBlock.ForegroundProperty, "Theme.Text.Body");
            tx.Bind(TextBlock.TextProperty, new Binding(nameof(FaceRow.UnitId)));
            ToolTip.SetTip(tx, "采掘单元号（层-B带号-P幅号），与采矿模型导出的基表同一套编号；填了才能把任务落回图上的体");
            dp.Children.Add(note); dp.Children.Add(tx);
            return dp;
        });
        unit.CellEditingTemplate = new FuncDataTemplate<FaceRow>((_, _) =>
        {
            var tx = new TextBox { VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(6, 2) };
            tx.Bind(TextBox.TextProperty, new Binding(nameof(FaceRow.UnitId)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            return tx;
        });
        c.Add(unit);

        c.Add(TaskUi.TextCol("物料", nameof(FaceRow.Material), 84));

        // 去向：下拉源 = cfg.Sinks.Active 且只列该面物料允许进的汇；下面一行是混采面的分项摘要（只读）。
        var sink = new DataGridTemplateColumn { Header = TaskUi.Head("去向（卸点）"), Width = new DataGridLength(222) };
        sink.CellTemplate = new FuncDataTemplate<FaceRow>((_, _) =>
        {
            var sp = new StackPanel { Margin = new Thickness(1, 1), VerticalAlignment = VerticalAlignment.Center };
            var cb = new ComboBox { MinWidth = 200, FontSize = 12.5, ItemTemplate = new FuncDataTemplate<SinkOption>((o, _) => new TextBlock { Text = o?.Caption ?? "" }) };
            cb.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(FaceRow.SinkOptions)));
            cb.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(FaceRow.SelectedSink)) { Mode = BindingMode.TwoWay });
            cb.Bind(ToolTip.TipProperty, new Binding(nameof(FaceRow.SinkTip)));
            var split = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 2, 0, 0) };
            TaskUi.Theme(split, TextBlock.ForegroundProperty, "Theme.Text.Muted");
            split.Bind(TextBlock.TextProperty, new Binding(nameof(FaceRow.SplitSummary)));
            split.Bind(Visual.IsVisibleProperty, new Binding(nameof(FaceRow.HasSplits)));
            split.Bind(ToolTip.TipProperty, new Binding(nameof(FaceRow.SinkTip)));
            sp.Children.Add(cb); sp.Children.Add(split);
            return sp;
        });
        c.Add(sink);

        c.Add(TaskUi.TextCol("运距(km)", nameof(FaceRow.Haul), 70));
        c.Add(TaskUi.TextCol("日目标(m³)", nameof(FaceRow.TargetM3), 84, "当日采出目标，m³ 原位实方（不是松方）"));
        var tT = TaskUi.TextCol("日目标(t)", nameof(FaceRow.TargetT), 70); tT.FontWeight = FontWeight.Bold; c.Add(tT);

        // 备采家底（V041）：可采天数 = 备采 ÷ 日目标，空着显示「—」而不是 0 —— 未录 ≠ 采空。
        c.Add(TaskUi.StyledCol<FaceRow>("备采(m³)", nameof(FaceRow.Reserve), 76, headerTip: "本面备采储量，m³ 原位实方；空 = 未录，不是采空", rightAlign: true));
        c.Add(TaskUi.TextCol("可采(天)", nameof(FaceRow.PreparedDays), 64));

        // 荐车 / 配车 / 实配车号 三列并排：配 < 荐 ⇒ 运力不足（TaskExploder 报「铲将待车」的判据）。
        c.Add(TaskUi.TextCol("荐车", nameof(FaceRow.RecTrucks), 46, "建议配车数 n*（求解派生值，不入库）"));
        c.Add(TaskUi.TextCol("配车", nameof(FaceRow.OnSiteTrucks), 46, "实配辆数 = 右侧「实配车号」数出来的，不单独录"));
        c.Add(TaskUi.StyledCol<FaceRow>("实配车号", nameof(FaceRow.TrucksText), 120, tipPath: nameof(FaceRow.TrucksTip),
            brushPath: nameof(FaceRow.TrucksBrush), boldPath: nameof(FaceRow.TrucksWeight),
            headerTip: "现场实际配的车，车号逗号分隔（如 T-01,T-02）；留空 = 未配车。\n与「荐车」之差即运力盈亏：配 < 荐 ⇒ 铲将待车（运力不足）。"));

        c.Add(TaskUi.TextCol("编组班产", nameof(FaceRow.GroupCap), 76, "铲-车编组的班产能力，m³/h（实方）"));
        c.Add(TaskUi.TextCol("主设备", nameof(FaceRow.MainEquip), 70));
        c.Add(TaskUi.TextCol("工程位置", nameof(FaceRow.Ep), 70));

        // ↓↓↓ 以下三组是可整组收起的录入列（工具条右侧「显示」）。核心列到此为止。
        colAzimuth = TaskUi.StyledCol<FaceRow>("方位(°)", nameof(FaceRow.Azimuth), 58, headerTip: "推进方位（正北起顺时针，0–360）。空 = 未录；0° 是合法方位，不等于「没填」", rightAlign: true);
        colMiningWidth = TaskUi.StyledCol<FaceRow>("推进宽(m)", nameof(FaceRow.MiningWidth), 76, headerTip: "本面当前推进宽（m）。与 working_face 的设计采宽同名不同义：那边是设计口径，这边是当日实际", rightAlign: true);
        c.Add(colAzimuth); c.Add(colMiningWidth);

        const string srcTip = "源端（铲位/装载点）平面坐标，与路网节点、去向坐标同一坐标系，单位 m。\nX、Y 均为 0 = 未录：路网只能靠名字匹配节点，匹配不上运距落兜底值，循环时间/配车数/编组班产会跟着偏。\n灰斜体 = 系统按可采区域质心自动推的（不入库）；改一下即成人工值。";
        colSrcX = TaskUi.StyledCol<FaceRow>("源X(m)", nameof(FaceRow.SrcXText), 72, tipPath: nameof(FaceRow.SrcCoordTip), brushPath: nameof(FaceRow.SrcBrush), italicPath: nameof(FaceRow.SrcStyle), headerTip: srcTip);
        colSrcY = TaskUi.StyledCol<FaceRow>("源Y(m)", nameof(FaceRow.SrcYText), 76, tipPath: nameof(FaceRow.SrcCoordTip), brushPath: nameof(FaceRow.SrcBrush), italicPath: nameof(FaceRow.SrcStyle), headerTip: srcTip);
        colSrcZ = TaskUi.StyledCol<FaceRow>("源Z(m)", nameof(FaceRow.SrcZText), 62, tipPath: nameof(FaceRow.SrcCoordTip), brushPath: nameof(FaceRow.SrcBrush), italicPath: nameof(FaceRow.SrcStyle),
            headerTip: "源端标高（本面台阶标高）。Z=0 是合法标高，不参与「有没有坐标」的判定。\n只影响按坐标吸附路网节点时的三维距离，不影响「录没录坐标」的判断。");
        c.Add(colSrcX); c.Add(colSrcY); c.Add(colSrcZ);

        // 煤质目标四列（V036）：留空 = 该项无目标；四项齐全才组装成有效 CoalQuality，半份如实入库但不生效（琥珀斜体）。
        colAsh = TaskUi.StyledCol<FaceRow>("灰分(%)", nameof(FaceRow.AshText), 62, tipPath: nameof(FaceRow.QualityTip), brushPath: nameof(FaceRow.QualityBrush), italicPath: nameof(FaceRow.QualityStyle), headerTip: "配煤灰分目标，百分数（填 12.5 不是 0.125）；留空 = 无此项目标");
        colCv = TaskUi.StyledCol<FaceRow>("热值", nameof(FaceRow.CvText), 60, tipPath: nameof(FaceRow.QualityTip), brushPath: nameof(FaceRow.QualityBrush), italicPath: nameof(FaceRow.QualityStyle), headerTip: "配煤热值目标，MJ/kg（不是 kcal/kg；1 MJ/kg ≈ 239 kcal/kg）；留空 = 无此项目标");
        colSulfur = TaskUi.StyledCol<FaceRow>("硫(%)", nameof(FaceRow.SulfurText), 52, tipPath: nameof(FaceRow.QualityTip), brushPath: nameof(FaceRow.QualityBrush), italicPath: nameof(FaceRow.QualityStyle), headerTip: "配煤硫分目标，百分数；留空 = 无此项目标");
        colMoisture = TaskUi.StyledCol<FaceRow>("水(%)", nameof(FaceRow.MoistureText), 52, tipPath: nameof(FaceRow.QualityTip), brushPath: nameof(FaceRow.QualityBrush), italicPath: nameof(FaceRow.QualityStyle), headerTip: "配煤水分目标，百分数；留空 = 无此项目标");
        c.Add(colAsh); c.Add(colCv); c.Add(colSulfur); c.Add(colMoisture);
    }

    /// <summary>
    /// 列分组显隐（工具条右侧「显示」三个勾）。
    ///
    /// 为什么要分组：本表 26 列合计 2066 DIP，默认窗宽的可视区只有 1523。
    /// 收起三组录入列后核心列（源—物料—汇—运距—编组）合计 1496，正好一屏摆开、不横滚。
    ///
    /// 收起只改 Visibility，**不动数据**：已录的坐标/煤质照样存在库里、照样参与求解。
    /// </summary>
    private void ApplyColumnGroups()
    {
        static void Set(bool on, params DataGridColumn?[] cols)
        {
            foreach (var c in cols) { if (c is not null) c.IsVisible = on; }
        }

        Set(chkAdvance?.IsChecked == true, colAzimuth, colMiningWidth);
        Set(chkCoord?.IsChecked == true, colSrcX, colSrcY, colSrcZ);
        Set(chkQuality?.IsChecked == true, colAsh, colCv, colSulfur, colMoisture);
    }

    private void Reload()
    {
        try { _cfg = SampleTaskBoard.Config(); }
        catch (Exception ex)
        {
            toolStatus.Text = $"盘子装载失败：{Short(ex)}";
            return;
        }

        // 反向合并：working_face + working_face_routing → FaceInput。
        // 上一次在本窗口定下的去向是**人工决策**，优先于流向分配的自动解——所以放在
        // Config()（已跑过分配/运距/编组）之后覆盖，再让运距与编组按新去向重解一遍。
        // 煤质草稿单独接出来：存档里可能是半份，FaceInput.Quality 装不下（见 QualityDraft）。
        _drafts.Clear();
        string merged = FaceLedgerLoader.ApplySaved(_cfg, _drafts);

        // 下拉源：登记簿里在用的汇（Active）。登记簿为空时兜一份样例，保证下拉不是空的。
        var sinks = SafeSinks(_cfg);

        _rows = _cfg.Faces.Select(f => new FaceRow(f, sinks, OnRowChanged, DraftFor(f))).ToList();
        grid.ItemsSource = _rows;

        toolStatus.Text = string.Join("　·　", new[] { SafeLabel(), merged }.Where(s => !string.IsNullOrWhiteSpace(s)));
        RefreshSummary();
    }

    private static List<SinkNode> SafeSinks(ExploderConfig cfg)
    {
        try
        {
            var list = cfg.Sinks.Active.ToList();
            if (list.Count > 0) return list;
        }
        catch { }
        try { return SinkRegistry.Sample().Active.ToList(); }
        catch { return new List<SinkNode>(); }
    }

    private static string SafeLabel()
    {
        try { return SampleTaskBoard.SourceLabel; }
        catch { return ""; }
    }

    private void OnRowChanged(FaceRow row) => RefreshSummary();

    /// <summary>底部：无去向红字告警 + 全盘合计（实方/吨量/占容/运输功）。</summary>
    private void RefreshSummary()
    {
        var loads = _cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();

        // 判据是「逐物料都定了」：混采面只定了煤、岩没人管，同样算没定完。
        var noDest = _cfg.Faces
            .Where(f => !f.AllMaterialsRouted)
            .Select(f =>
            {
                var miss = f.ResolvedMix.Normalized().Shares
                    .Where(s => s.Fraction > 1e-6 && !f.DestinationFor(s.MaterialCode).HasDestination)
                    .Select(s => MaterialCatalog.Resolve(s.MaterialCode).Name).Distinct().ToList();
                return f.HasSplits || (miss.Count > 0 && miss.Count < f.ResolvedMix.Normalized().Shares.Count)
                    ? $"{f.Zone}（缺 {string.Join("、", miss)}）"
                    : f.Zone;
            })
            .ToList();

        var warns = new List<string>();

        if (noDest.Count > 0)
            warns.Add($"⚠ 未指定卸点，运距与编组无法核算：{string.Join("、", noDest)}"
                    + "（下拉只列该面物料允许进的去向；表土只能进表土堆场、煤不进排土场。"
                    + "混采面须逐物料都有去向，缺任一项即不得签发）");

        // 运力不足：配 < 荐 ⇒ 铲将待车。这是编制裂解时会报的 TruckShortage，提前在台账上就说清。
        var shortage = _rows.Where(r => r.TruckShortage).Select(r => r.Zone).ToList();
        if (shortage.Count > 0)
            warns.Add($"⚠ 运力不足（实配车数 < 建议车数，铲将待车）：{string.Join("、", shortage)}"
                    + "——在「实配车号」列补车，或调减该面日目标");

        // 煤质半份：存得下但不生效，必须说明白，否则用户以为填了就管用了。
        var partial = _rows.Where(r => r.QualityPartial).Select(r => r.Zone).ToList();
        if (partial.Count > 0)
            warns.Add($"⚠ 煤质目标只填了一部分，尚未生效：{string.Join("、", partial)}"
                    + "——灰分/热值/硫/水四项须填齐才构成有效目标（已填的数照常存台账，不会丢）");

        if (warns.Count > 0)
        {
            warnLine.Text = string.Join("\n", warns);
            warnLine.IsVisible = true;
        }
        else warnLine.IsVisible = false;

        double m3 = loads.Sum(f => f.DayTargetM3);
        double t = loads.Sum(f => f.TargetTonnageT);
        double dump = loads.Sum(f => f.WasteDumpM3);
        // 运输功逐分项算：混采面煤走 2.6km、岩走 1.4km，用主去向运距乘全面吨量会算错
        double work = loads.Sum(f => f.ResolvedMix.Split(f.DayTargetM3)
            .Sum(x => x.Spec.ToTonnage(x.InSituM3) * f.DestinationFor(x.Spec.Code).EffectiveHaulKm));
        double avgKm = t <= 1e-6 ? 0 : work / t;
        double oreT = loads.Sum(f => f.ResolvedMix.Split(f.DayTargetM3).Where(x => x.Spec.IsOre).Sum(x => x.Spec.ToTonnage(x.InSituM3)));
        double stripM3 = loads.Sum(f => f.DayTargetM3 * (1 - f.ResolvedMix.OreFraction));

        // 源坐标接线状况：录入 / 区域质心推导 / 未录。未录的面路网只能靠名字碰节点，
        // 碰不上运距就落兜底值 —— 这句让"今天这盘运距有多少是真解出来的"一眼可见。
        int srcEntered = 0, srcAuto = 0, srcNone = 0;
        foreach (var r in _rows)
        {
            if (r.NoSrcCoord) srcNone++;
            else if (r.AutoSrcCoord) srcAuto++;
            else srcEntered++;
        }

        sumLine.Text =
            $"{_cfg.Faces.Count} 个作业面（采装 {loads.Count} · 排土 {_cfg.Faces.Count - loads.Count}）　|　" +
            $"源坐标 录入 {srcEntered} · 区域质心 {srcAuto} · 未录 {srcNone}" +
            (srcNone > 0 ? "（未录的面路网只能靠名字匹配节点，匹配不上运距落兜底）" : "") + "　|　" +
            $"当日采剥 {m3:0} m³实方 / {t / 1e4:0.##} 万t　|　采出 {oreT / 1e4:0.##} 万t · 剥离 {stripM3 / 1e4:0.##} 万m³实方" +
            (oreT > 1e-6 ? $"（生产剥采比 {stripM3 / oreT:0.##} m³/t）" : "") + "　|　" +
            $"排弃占容 {dump / 1e4:0.##} 万m³　|　吨量加权平均运距 {avgKm:0.##} km";
    }

    private void OnAdd()
        => toolStatus.Text = "新增作业面（接采区/工程位置拾取待接）";

    /// <summary>保存台账：写回 working_face（能落的列）+ working_face_routing（去向/运距/分项），逐条报结果。</summary>
    private async void OnSave()
    {
        // 正在编辑的单元格先落值，否则最后改的那一格（煤质/车号）会存不进去。
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        // 传行：煤质半份状态只在行的草稿上，FaceInput.Quality 装不下（见 QualityDraft）。
        var res = FaceLedgerStore.Save(_cfg, _rows);

        if (res.Aborted)
        {
            toolStatus.Text = res.Caption;
            await TaskUi.Info(this, "保存作业面台账",
                $"作业面台账未保存：{res.AbortReason}\n\n改动还在窗口里，接通数据库后再点一次「保存台账」即可。");
            return;
        }

        toolStatus.Text = res.Caption;

        if (!res.Ok)
        {
            await TaskUi.Info(this, "保存作业面台账",
                $"保存完成，但有 {res.Failed} 条失败（成功 {res.Saved} 条）：\n\n"
                + string.Join("\n", res.Errors.Select(x => "· " + x))
                + (res.Notes.Count > 0 ? "\n\n提示：\n" + string.Join("\n", res.Notes.Select(x => "· " + x)) : ""));
        }
        else if (res.Notes.Count > 0)
        {
            // 有些面在 working_face 里没有建档，只落了扩展档案——这事得说清楚，不能让人以为几何也存了。
            await TaskUi.Info(this, "保存作业面台账",
                $"已保存 {res.Saved} 条。\n\n以下情况请知悉：\n" + string.Join("\n", res.Notes.Select(x => "· " + x)));
        }
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  作业面台账的读写落点
    //
    //  两张表分工（V035）：
    //    working_face          台阶几何的权威（台阶高/采宽/面长/推进度）——别的模块（工艺几何、
    //                          参数验收）在消费它，本窗口只更新自己确实拥有的两列：
    //                          equipment_id（主设备）与 material（物料码），按 face_code 匹配。
    //    working_face_routing  「当日怎么干」：去向 / 运距 / 日目标 / 混采分项 / 工程位置 / 台阶标高。
    //
    //  ★ 两条不写的红线（写了就污染别人的口径）：
    //    · FaceInput.BenchElevationM 是台阶【标高】，working_face.bench_height_m 是台阶【高度】——
    //      量纲同为 m、含义完全不同，故标高只落 working_face_routing.bench_elevation_m。
    //    · FaceInput 没有「面状态」这个字段，故不动 working_face.status——拿在用与否去猜，
    //      会把别人录的 planning/closed 冲掉。
    //
    //  只更新、不新建 working_face 行：本窗口的面名是"主采面·东（剥离）"这类采区叫法，
    //  working_face 用的是 WF-1195-A 这类编号，且 bench_height_m 是 NOT NULL 而台账无从提供——
    //  硬建行就是往几何台账里塞一堆台阶高为 0 的假记录。没有对应行时只落扩展档案并如实提示。
    // ═════════════════════════════════════════════════════════════════════════
    private static class FaceLedgerStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // 读侧（档案 → FaceInput、煤质草稿、车号解析、分项反序列化）已提到引擎层
        // TaskLib.Engine.FaceLedgerLoader：装配当日盘子也要用同一套规则，不能只有本窗口看得见。
        // 本类只留【写侧】——录入是本窗口独有的行为。

        /// <summary>
        /// 写回台账。结果结构复用 <see cref="SinkRegistryLoader.SinkSaveResult"/>
        /// （成功/失败/逐条原因的形状与去向台账完全一致，没必要再造一个）。
        /// </summary>
        internal static SinkRegistryLoader.SinkSaveResult Save(
            ExploderConfig? cfg, IReadOnlyList<FaceRow>? rows = null)
        {
            var res = new SinkRegistryLoader.SinkSaveResult();
            var faces = cfg?.Faces?.Where(f => f != null).ToList() ?? new List<FaceInput>();
            if (faces.Count == 0) { res.Abort("当前盘子里没有作业面"); return res; }

            // 煤质草稿从行上取：FaceInput.Quality 只装得下"齐全的目标"，
            // 半份（只填了灰分）得从行的草稿里拿，否则用户录一半就存不下来。
            var drafts = new Dictionary<string, QualityDraft>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows ?? Array.Empty<FaceRow>())
                if (!string.IsNullOrWhiteSpace(r.Zone)) drafts[r.Zone.Trim()] = r.QualitySnapshot;

            IWorkingFaceRoutingService routings;
            IWorkingFaceService workingFaces;
            try
            {
                var ctx = EquipmentDataContext.Current;
                routings = ctx.WorkingFaceRoutings;
                workingFaces = ctx.WorkingFaces;
                if (routings == null || workingFaces == null) { res.Abort("数据库服务未注册（GeoDataBase 未就绪）"); return res; }
            }
            catch (Exception ex) { res.Abort($"数据库未接通（{Short(ex)}）"); return res; }

            int noBody = 0;
            foreach (var f in faces)
            {
                // 行上没有草稿（比如不是从本窗口存的）就从面上的目标反推，绝不写成一片 NULL
                var draft = !string.IsNullOrWhiteSpace(f.Zone) && drafts.TryGetValue(f.Zone.Trim(), out var d)
                    ? d
                    : QualityDraft.From(f.Quality);
                SaveOne(f, draft, routings, workingFaces, res, ref noBody);
            }

            if (noBody > 0)
                res.Notes.Add($"{noBody} 个面在 working_face 里没有同名建档，只落了去向档案；"
                            + "台阶几何（台阶高/采宽/面长）请到「工作面管理」按 face_code 建档后再关联");
            return res;
        }

        private static void SaveOne(
            FaceInput f, QualityDraft quality, IWorkingFaceRoutingService routings, IWorkingFaceService workingFaces,
            SinkRegistryLoader.SinkSaveResult res, ref int noBody)
        {
            string code = (f.Zone ?? "").Trim();
            if (code.Length == 0) { res.Fail("（无名作业面）", "作业面名称为空，无法作为 face_code 定位台账"); return; }

            // ① 扩展档案：本窗口真正拥有的那批字段
            //    先探一次是不是新档案，只为把"新增几条/更新几条"报准——Upsert 自己分不出这个。
            bool isNew;
            try { isNew = routings.Get(code) == null; }
            catch { isNew = false; }

            try { routings.Upsert(ToRouting(f, code, quality)); }
            catch (Exception ex) { res.Fail(code, $"去向档案写入失败：{Short(ex)}"); return; }

            // ② working_face：只更新确实有对应列、且台账确实拥有的两列（主设备 / 物料）。
            //    没有同名行就不建——理由见本类头部注释。
            try
            {
                var wf = workingFaces.GetByCode(code);
                if (wf == null) { noBody++; }
                else
                {
                    bool touched = false;
                    string equip = (f.Group?.MainEquipment ?? "").Trim();
                    if (equip.Length > 0 && !string.Equals(wf.EquipmentId, equip, StringComparison.OrdinalIgnoreCase))
                    { wf.EquipmentId = equip; touched = true; }

                    string mat = DominantCode(f);
                    if (mat.Length > 0 && !string.Equals(wf.Material, mat, StringComparison.OrdinalIgnoreCase))
                    { wf.Material = mat; touched = true; }

                    if (touched) workingFaces.Update(wf);
                }
            }
            catch (Exception ex)
            {
                // 档案已经落了，本体没更新成——如实报半成功，别让"保存成功"盖住它。
                res.Fail(code, $"去向档案已写入，但 working_face 更新失败：{Short(ex)}");
                return;
            }

            if (isNew) res.Inserted++; else res.Updated++;
        }

        private static WorkingFaceRouting ToRouting(FaceInput f, string code, QualityDraft quality) => new()
        {
            FaceCode = code,
            Process = f.Process.ToString(),
            EngineeringPositionId = (f.EngineeringPositionId ?? "").Trim(),
            BenchElevationM = f.BenchElevationM,      // 台阶【标高】：不进 working_face.bench_height_m
            MaterialCode = DominantCode(f),
            MaterialMix = MixText(f),
            DestinationId = (f.DestinationId ?? "").Trim(),
            DestinationName = (f.DestinationName ?? "").Trim(),
            DestinationKind = f.DestinationKind.ToString(),
            HaulDistanceKm = Sane(f.HaulDistanceKm),
            EquivHaulKm = Sane(f.EquivHaulKm),
            DayTargetM3 = Sane(f.DayTargetM3),
            DerivedFromInbound = f.DerivedFromInbound ? 1 : 0,
            ShovelModelPref = (f.ShovelModelPref ?? "").Trim(),
            MainEquipment = (f.Group?.MainEquipment ?? "").Trim(),
            SplitsJson = SerializeSplits(f.Splits),

            // 空间身份与备采家底（V041）：单元号让任务对得回图上的体，备采量让"还能采几天"算得出来。
            // 方位/推进宽各自可空，没录就存 NULL——0° 是合法方位，不能拿 0 冒充"没录"。
            UnitId = (f.UnitId ?? "").Trim(),
            AvailableReserveM3 = Sane(f.AvailableReserveM3),
            AdvanceAzimuthDeg = f.AdvanceAzimuthDeg,
            MiningWidthM = f.MiningWidthM,

            // 煤质目标（V036）：四项**各自**入库，半份也照存（列本来就各自可空）。
            // 没填就是 NULL，绝不用 0 冒充——0 会让配煤约束把这面判成"灰分 0，达标"。
            QualityAshPct = quality.Ash,
            QualityCvMjKg = quality.Cv,
            QualitySulfurPct = quality.Sulfur,
            QualityMoisturePct = quality.Moisture,

            // 实配车号（V036）：现场实配，与荐车之差即运力盈亏。
            // 荐车 / 编组班产是 FleetMatcher 的求解派生值，故意不入库。
            AssignedTrucks = string.Join(",", f.Group?.Trucks ?? new List<string>()),

            // 源坐标（V037）：★ 只存**人工录的**那一份。
            // 按区域质心自动推导出来的坐标一律落 0（= 库里"没录"）——它是系统猜的：
            //  · 存进去就冒充成了人工值，用户再也分不清哪个数是自己填的、哪个是猜的；
            //  · 区域边界后来改了，库里那份陈旧的猜测反而会盖住新的推导（存档优先）。
            // 猜的那份每次装载现推一遍即可，不占库。
            SourceX = ManualSrc(f) ? Sane(f.SourceX) : 0,
            SourceY = ManualSrc(f) ? Sane(f.SourceY) : 0,
            SourceZ = ManualSrc(f) ? Sane(f.SourceZ) : 0,
        };

        /// <summary>源坐标是不是人工录的（录了 且 不是区域质心推的）。只有它才入库。</summary>
        private static bool ManualSrc(FaceInput f)
        {
            if (!f.HasSourcePosition) return false;
            try { return !HaulResolver.IsAutoSourced(f, out _); }
            catch { return true; }   // 判不出来就当人工值存下：宁可多存一份人填的，也不要把它弄丢
        }

        /// <summary>主物料码：混采面取份额最大的一项（working_face.material 只有一格）。</summary>
        private static string DominantCode(FaceInput f)
        {
            if (MaterialCatalog.Exists(f.MaterialCode)) return f.MaterialCode.Trim();
            try
            {
                var top = f.ResolvedMix.Normalized().Shares
                    .OrderByDescending(s => s.Fraction).FirstOrDefault();
                return top != null && MaterialCatalog.Exists(top.MaterialCode) ? top.MaterialCode : "";
            }
            catch { return ""; }
        }

        /// <summary>混采构成原文（"煤6∶岩4"）；单一物料存空串——MaterialMix.Parse 能原样读回。</summary>
        private static string MixText(FaceInput f)
        {
            try
            {
                var mix = f.ResolvedMix;
                return mix.Shares.Count >= 2 ? mix.Caption : "";
            }
            catch { return ""; }
        }

        private static string SerializeSplits(List<MaterialDestination>? splits)
        {
            if (splits == null || splits.Count == 0) return "[]";
            try { return JsonSerializer.Serialize(splits, Json); }
            catch { return "[]"; }   // 序列化失败退化成"无分项"，不让它把整条保存拖死
        }

        private static double Sane(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
    }
}
