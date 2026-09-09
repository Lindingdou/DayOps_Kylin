using System;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 把底层的连库失败翻译成用户看得懂、且知道下一步该干什么的说法。
///
/// 为什么值得单独做: 原先直接把异常原文丢进状态栏, 用户看到的是
/// "Failed to connect to 192.168.114.131:5432" 或者 "28P01: password authentication failed"
/// —— 既不知道是自己没配、还是服务器没开、还是密码错, 也不知道去哪儿改。
///
/// 判据只看异常文本, 是纯函数, 可单测。识别不出来的一律归到"未知", 附上原文, 不猜。
/// </summary>
public static class DbConnectionDiagnosis
{
    /// <summary>
    /// 诊断结果。
    /// <para><b>Detail</b> 是给用户看的中文说明, 里面不放英文原文 —— 现场用户看不懂
    /// "28P01: password authentication failed", 混在提示里只会造成困惑。</para>
    /// <para><b>Raw</b> 是底层原始报错(通常是英文), 放在窗口的"详细信息"里折叠起来,
    /// 需要排查时展开给技术人员看。丢掉它会让远程支持无从下手, 所以留着, 但不摆在正文。</para>
    /// <para><b>Actionable</b>=true 表示"去配置连接"这条路能解决。</para>
    /// </summary>
    public readonly record struct Result(string Title, string Detail, bool Actionable, string Raw);

    public static Result Diagnose(Exception ex)
    {
        var r = Classify(ex);
        return r with { Raw = Flatten(ex) };
    }

    /// <summary>
    /// 把异常链上所有消息串起来。
    ///
    /// **必须看内层**: Npgsql 连不上时最外层只有一句 "Exception while connecting",
    /// 真正的原因(SocketException / TimeoutException)在 InnerException 里。
    /// 只看最外层的话, 网络不通会被归到"未知错误", 用户拿到的就是一句没用的兜底话
    /// —— 实测就踩了这个: 指向不存在的地址, 提示却显示"数据库连接失败"。
    /// </summary>
    private static string Flatten(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" ← ");
            sb.Append(e.Message);
        }
        return sb.ToString();
    }

    private static Result Classify(Exception ex)
    {
        string m = Flatten(ex).Replace('\r', ' ').Replace('\n', ' ');

        // 程序自己抛的: 压根没有连接信息
        if (m.Contains("没有可用的连接信息", StringComparison.Ordinal))
            return new Result(
                "还没有配置数据库连接",
                "本程序的数据来自局域网上的 openGauss 数据库。请先填写服务器地址和账号。",
                true, "");

        // 库存在但没建过表 —— 这是部署方的活, 不是用户改连接能解决的
        if (m.Contains("读不到迁移登记表", StringComparison.Ordinal) ||
            m.Contains("schema 落后于本程序", StringComparison.Ordinal))
            return new Result(
                "数据库还没初始化",
                "连接是通的，但这个库还没建表。需要由部署方执行一次建库(PITMINE_DB_MIGRATE=1)，" +
                "在那之前请勿使用，以免写入与结构不符的数据。",
                false, "");

        // 网络层: 连不上。这些字样多半出现在**内层**异常里, 所以 m 用的是整条异常链。
        // "Exception while connecting" 是 Npgsql 的外层措辞, 真正原因在它下面。
        if (m.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Exception while connecting", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("did not properly respond", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("host is unreachable", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return new Result(
                "连不上数据库服务器",
                "地址或端口不对，或者服务器没开、被防火墙挡住。请检查连接设置，或联系管理员确认服务是否在运行。",
                true, "");

        // 认证/权限
        if (m.Contains("28P01", StringComparison.Ordinal) ||
            m.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Invalid username/password", StringComparison.OrdinalIgnoreCase))
            return new Result(
                "账号或密码不对",
                "服务器拒绝了这个账号。请核对用户名和密码。" +
                "（若服务端刚改过认证方式，需要由管理员重设一次该账号的密码才会生效。）",
                true, "");

        if (m.Contains("3D000", StringComparison.Ordinal) ||
            m.Contains("does not exist", StringComparison.OrdinalIgnoreCase) && m.Contains("database", StringComparison.OrdinalIgnoreCase))
            return new Result(
                "库名不存在",
                "服务器连上了，但上面没有这个库。请核对库名，或让管理员先建库。",
                true, "");

        // 认不出来: 也不把英文原文摆到正文, 它会进"详细信息"。
        return new Result(
            "数据库连接失败",
            "无法打开数据库。请检查连接设置是否正确、服务器是否正常运行；" +
            "若仍无法解决，请把「详细信息」中的内容提供给技术支持。",
            true, "");
    }
}
