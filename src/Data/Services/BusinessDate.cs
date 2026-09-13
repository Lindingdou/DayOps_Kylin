// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/BusinessDate.cs（逐行对应；仅命名空间/依赖适配）
using System;

namespace PitMine3D.Kylin.Data.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  业务日期列的查询参数口径 —— 一个地方说清，十二处引用。
//
//  ── 症状 ──
//  `ByDate(DateTime)` / `InRange(DateTime, DateTime)` 这一族**一行都取不到**，且不报错。
//  实测（2026-08-11，真库 Data/pmgeo.db，走真实的 ISqlService + Dapper + Microsoft.Data.Sqlite）：
//      select count(*) from production_record where date = @d
//        传 DateTime            → 0 行
//        传 "2026-05-04" 字符串 → 918 行
//
//  ── 根因 ──
//  本库所有**业务日期列**（date / blast_date / measure_date / start_date …）存的是
//  **只有日期的文本**：`2026-05-01`。而 Microsoft.Data.Sqlite 绑定 DateTime 参数时会写成
//  带时分秒的形式（`2026-05-01 00:00:00`），等值比对必然落空，BETWEEN 同理。
//  SQLite 是弱类型 + 文本比较，两边格式不一样就是不等，不会有任何报错。
//
//  ── 为什么这个错特别值得单列一个类 ──
//  它**静默**：查询成功、返回空表，调用方按"这天没有记录"处理，界面显示"无数据"。
//  于是报表的设备工时/故障工时恒为零、作业日口径回落写死的 25 天、日级时间轴的工时全空 ——
//  每一处看起来都自洽，没有一处报错。全库扫过一遍，业务日期列的存储格式是**统一的**
//  （只有审计列 updated_at 带时分秒，而它从不作为查询参数），所以这一条口径可以放心统一。
//
//  ── 纪律 ──
//  查业务日期列，参数一律经本类转成 `yyyy-MM-dd`；**不要**在各个服务里各写各的 ToString。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>业务日期列（date / blast_date / measure_date …）的查询参数格式。</summary>
internal static class BusinessDate
{
    /// <summary>本库业务日期列的存储格式。</summary>
    public const string Format = "yyyy-MM-dd";

    /// <summary>
    /// 把 <see cref="DateTime"/> 转成能与业务日期列比对的字符串。
    /// <para>直接传 DateTime 会被绑成带时分秒的形式，与库里的纯日期文本永远不等。</para>
    /// </summary>
    public static string P(DateTime d) => d.ToString(Format,
        System.Globalization.CultureInfo.InvariantCulture);
}
