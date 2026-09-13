// 忠实移植自原 PitMine3D Modules/PointCloudLib/Shading/OrthophotoConfig.cs（逐行对应；仅命名空间适配）
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PitMine3D.Kylin.Shading
{
    // ─────────────────────────────────────────────────────────────────────────
    //  正射影像（GeoTIFF）的**工程级配置** —— 配一次，后续功能都读它。
    //
    //  ── 在它出现之前 ──
    //  同一张影像要选三遍，三处各记各的：
    //    · TIN 着色对话框（PointCloudLib）：靠 IUserSettings 记「上次用过的路径」，只当预填；
    //    · 推演底图（TaskLib/Adjust/OrthophotoBasemap）：自己一套 FileFilter + 自己弹框；
    //    · 作业区划分底图（TaskLib/Zoning/ZoneBasemap）：又一套，FileFilter 与上面一字不差。
    //  共用的解析（GeoTiffInfo / OrthophotoLoader）倒是没重造，重造的是"路径从哪来"。
    //  后果：换一张新航拍要去三个地方各选一次，漏掉一处就出现"同一天两个窗口踩着两张影像"。
    //
    //  ── 现在 ──
    //  路径存这里（<c>%LOCALAPPDATA%/PitMine/orthophoto.json</c>，与执行期落盘同一个根）。
    //  三处一律先问它，**没配才弹框；弹框选完立刻写回来**——所以"配置"这件事不需要用户
    //  专门去某个界面做一次，在哪儿选的都算配上了；「影像底图」窗口只是那个正式入口。
    //
    //  ── 两条纪律 ──
    //  ① <b>只存路径，不存配准</b>。四至/分辨率/坐标系一律现读文件头（GeoTiffInfo 只读几 KB，
    //     上百 MB 的影像也是毫秒级）。存一份配准的快照，等文件被换成同名的新版本时，
    //     缓存里的旧四至会让覆盖范围判断悄悄错掉——而这种错在画面上就是"一片空白"，
    //     人只会以为是渲染坏了。
    //  ② <b>文件不在了不清配置</b>。盘没插、网络路径没连上都会让 File.Exists 为假，
    //     此时把配置抹掉，等盘插回来还得重配。如实报"配了但当前读不到"，让人自己判断。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>工程级正射影像配置（当前只有一项：路径）。</summary>
    public sealed class OrthophotoSettings
    {
        /// <summary>影像文件绝对路径。空 = 没配过。</summary>
        public string Path { get; set; } = "";

        /// <summary>需要底图的窗口打开时是否自动装载（关掉则每次手动点一下）。</summary>
        public bool AutoLoad { get; set; } = true;
    }

    /// <summary>正射影像配置的读写。所有方法永不抛。</summary>
    public static class OrthophotoConfig
    {
        private static readonly JsonSerializerOptions Opt = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static OrthophotoSettings? _cache;

        /// <summary>选文件对话框的过滤串 —— 全仓唯一一份（此前三处各抄了一遍）。</summary>
        public const string FileFilter =
            "正射影像 (*.tif;*.tiff)|*.tif;*.tiff|所有图像 (*.tif;*.tiff;*.png;*.jpg)|*.tif;*.tiff;*.png;*.jpg|所有文件 (*.*)|*.*";

        /// <summary>最近一次读写的结果文案（供 UI 显示"存到哪了 / 为什么没存上"）。</summary>
        public static string LastIoLabel { get; private set; } = "";

        /// <summary>配置文件路径。</summary>
        public static string FilePath => System.IO.Path.Combine(RootDir(), "orthophoto.json");

        private static string RootDir()
        {
            string root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PitMine");
            try { Directory.CreateDirectory(root); } catch { }
            return root;
        }

        /// <summary>当前配置（首次访问读盘；读不到/读坏了返回空配置，不抛）。</summary>
        public static OrthophotoSettings Current
        {
            get
            {
                if (_cache != null) return _cache;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        _cache = JsonSerializer.Deserialize<OrthophotoSettings>(File.ReadAllText(FilePath), Opt)
                                 ?? new OrthophotoSettings();
                        LastIoLabel = $"已读配置：{FilePath}";
                    }
                    else
                    {
                        _cache = new OrthophotoSettings();
                        LastIoLabel = "还没有配过正射影像";
                    }
                }
                catch (Exception ex)
                {
                    _cache = new OrthophotoSettings();
                    LastIoLabel = $"配置读不出（{Short(ex)}），按未配置";
                }
                return _cache;
            }
        }

        /// <summary>配过路径没有（不代表文件现在读得到，见 <see cref="Available"/>）。</summary>
        public static bool HasPath => !string.IsNullOrWhiteSpace(Current.Path);

        /// <summary>配了路径且文件当前确实在（盘没插/网络路径断了会是 false，但**不会**清掉配置）。</summary>
        public static bool Available
        {
            get
            {
                string p = Current.Path;
                if (string.IsNullOrWhiteSpace(p)) return false;
                try { return File.Exists(p); } catch { return false; }
            }
        }

        /// <summary>
        /// 取配置好的路径。返回空串表示没配或当前读不到，<paramref name="why"/> 说明是哪一种——
        /// 调用方据此决定"直接用"还是"弹框让人选"。
        /// </summary>
        public static string ResolvePath(out string why)
        {
            string p = Current.Path;
            if (string.IsNullOrWhiteSpace(p)) { why = "未配置正射影像（在「基础数据 · 影像底图」里配一次，之后各功能直接用）"; return ""; }
            try
            {
                if (!File.Exists(p))
                {
                    why = $"已配置但当前读不到：{p}（盘/网络路径没连上？配置保留，不自动清除）";
                    return "";
                }
            }
            catch (Exception ex) { why = $"已配置但访问失败（{Short(ex)}）：{p}"; return ""; }

            why = $"取自工程配置：{System.IO.Path.GetFileName(p)}";
            return p;
        }

        /// <summary>
        /// 记住这个路径。<b>任何一处选完文件都应当调它</b> —— 这样"配置"不必是一次专门的操作，
        /// 在哪儿选的都算配上了，下一个功能直接就有。
        /// </summary>
        public static bool Remember(string path)
        {
            string p = (path ?? "").Trim();
            if (p.Length == 0) return false;
            var s = Current;
            if (string.Equals(s.Path, p, StringComparison.OrdinalIgnoreCase)) return true;   // 没变就不写盘
            s.Path = p;
            return Save(s);
        }

        /// <summary>保存整份配置。</summary>
        public static bool Save(OrthophotoSettings settings)
        {
            try
            {
                var s = settings ?? new OrthophotoSettings();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Opt));
                _cache = s;
                LastIoLabel = $"已保存：{FilePath}";
                return true;
            }
            catch (Exception ex)
            {
                LastIoLabel = $"保存失败（{Short(ex)}）";
                return false;
            }
        }

        /// <summary>清除配置（人主动点「清除」才调；文件读不到时**不要**调它）。</summary>
        public static bool Clear() => Save(new OrthophotoSettings());

        /// <summary>丢弃内存缓存（外部改过文件后调用）。</summary>
        public static void Invalidate() { _cache = null; LastIoLabel = ""; }

        private static string Short(Exception ex)
        {
            string m = ex.Message ?? ex.GetType().Name;
            return m.Length <= 60 ? m : m[..60] + "…";
        }
    }
}
