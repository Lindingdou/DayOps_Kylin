using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 数据库连接设置 —— 让用户在界面上配, 而不是靠部署方给每台机器设环境变量。
///
/// 取值优先级(高到低):
///   1. 环境变量 PITMINE_DB / PITMINE_DB_CONN   —— 部署脚本和自动化用, 永远压过界面设置
///   2. 站点配置 /etc/pitmine3d/db.conf          —— 由部署方统一下发, 所有用户共用
///   3. 用户配置 &lt;用户数据目录&gt;/db.json      —— 界面里改的就是这个
///   4. 都没有 → 仍是 openGauss, 但没有连接信息 → 启动时明确报错。
///      **刻意不留"退回本机 SQLite"这条路**: 一库多客户端时静默退回本地文件, 会让每个人
///      各写各的还以为在共享库上, 数据无声无息地分叉。宁可起不来, 也不要这种。
///
/// 站点配置压过用户配置是有意为之: 局域网统一换库时, 部署方放一个文件就能让所有客户端跟着走,
/// 不必挨台去点界面; 而个人想连自己的测试库, 在没有站点配置的机器上仍然可以自己配。
/// </summary>
public sealed class DbConnectionSettings
{
    public string Kind { get; set; } = "opengauss";     // 产品只支持 openGauss(SQLite 已从交付物移除)
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "pitmine";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>是否把密码一起存进配置文件。存的是明文, 界面上要如实告知。</summary>
    public bool SavePassword { get; set; }

    public bool IsRemote => true;   // 产品只有远程库这一种形态

    /// <summary>用户配置路径(界面写这里)。与 geo.db、model_update_settings.json 同目录。</summary>
    public static string UserConfigPath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "PitMine3D.Kylin", "db.json");
    }

    /// <summary>站点配置路径(部署方放这里, 统一下发)。Windows 上没有这个概念, 返回 null。</summary>
    public static string? SiteConfigPath()
        => OperatingSystem.IsLinux() ? "/etc/pitmine3d/db.conf" : null;

    // ── 连接串拼装(纯函数, 可单测) ───────────────────────────────────────────

    /// <summary>
    /// 由各字段拼 Npgsql 连接串。密码里的分号和单引号要转义, 否则连接串会被截断 ——
    /// Npgsql 的规则是: 值里含 ; " ' 或空格时用双引号包起来, 内部的双引号翻倍。
    /// </summary>
    public string BuildConnectionString()
    {
        var sb = new StringBuilder();
        Append(sb, "Host", Host);
        Append(sb, "Port", Port.ToString(CultureInfo.InvariantCulture));
        Append(sb, "Database", Database);
        Append(sb, "Username", Username);
        Append(sb, "Password", Password);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string val)
    {
        if (string.IsNullOrEmpty(val)) return;
        if (sb.Length > 0) sb.Append(';');
        sb.Append(key).Append('=');
        if (val.IndexOfAny(new[] { ';', '"', '\'', ' ', '=' }) >= 0)
            sb.Append('"').Append(val.Replace("\"", "\"\"")).Append('"');
        else
            sb.Append(val);
    }

    // ── 读写(极简 key=value, 不引 JSON 依赖) ─────────────────────────────────

    /// <summary>
    /// 解析 key=value 文本。用最朴素的格式而不是 JSON: 站点配置要由运维手写和审阅,
    /// 一行一项最好读; 出现看不懂的行就忽略, 不因为一个错字让程序起不来。
    /// </summary>
    public static DbConnectionSettings Parse(string text)
    {
        var s = new DbConnectionSettings();

        // 去掉 UTF-8 BOM。不去的话第一行的键会被读成 "﻿kind" 而匹配不上 ——
        // 首行配置静默失效, 且看文件内容完全正常, 极难发现。
        // PowerShell 的 Out-File -Encoding utf8、Windows 记事本另存, 写出来都带 BOM。
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant();
            string v = line.Substring(eq + 1).Trim();
            switch (k)
            {
                case "kind": s.Kind = v; break;
                case "host": s.Host = v; break;
                case "port": if (int.TryParse(v, out int p)) s.Port = p; break;
                case "database": s.Database = v; break;
                case "username": s.Username = v; break;
                case "password": s.Password = v; break;
                case "savepassword": s.SavePassword = v is "1" or "true" or "TRUE" or "yes"; break;
            }
        }
        return s;
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PitMine3D 数据库连接设置。kind = sqlite | opengauss");
        sb.AppendLine("kind=" + Kind);
        if (IsRemote)
        {
            sb.AppendLine("host=" + Host);
            sb.AppendLine("port=" + Port.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("database=" + Database);
            sb.AppendLine("username=" + Username);
            sb.AppendLine("savepassword=" + (SavePassword ? "1" : "0"));
            if (SavePassword)
            {
                sb.AppendLine("# 注意: 下面这行是明文密码。");
                sb.AppendLine("password=" + Password);
            }
        }
        return sb.ToString();
    }

    /// <summary>按优先级读出当前生效的设置。任何一步出错都不致命 —— 退回默认的本机 SQLite。</summary>
    public static DbConnectionSettings LoadEffective()
    {
        // 1. 环境变量优先
        string? envKind = Environment.GetEnvironmentVariable("PITMINE_DB");
        string? envConn = Environment.GetEnvironmentVariable("PITMINE_DB_CONN");
        if (!string.IsNullOrWhiteSpace(envKind) || !string.IsNullOrWhiteSpace(envConn))
            return new DbConnectionSettings { Kind = "opengauss" };

        // 2. 站点配置
        string? site = SiteConfigPath();
        if (site != null && TryRead(site, out var siteCfg)) return siteCfg;

        // 3. 用户配置
        if (TryRead(UserConfigPath(), out var userCfg)) return userCfg;

        // 4. 默认
        return new DbConnectionSettings();
    }

    private static bool TryRead(string path, out DbConnectionSettings cfg)
    {
        cfg = new DbConnectionSettings();
        try
        {
            if (!File.Exists(path)) return false;
            cfg = Parse(File.ReadAllText(path));
            return true;
        }
        catch { return false; }
    }

    /// <summary>写用户配置。目录不存在就建。</summary>
    public void SaveUser()
    {
        string path = UserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize());
    }
}
