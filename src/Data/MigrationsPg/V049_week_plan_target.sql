-- 由 build/sqlite2pg.py 从 V049_week_plan_target.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V049: 周计划目标(week_plan_target) —— 月 → 周 → 日 这条链上缺的那一层
--
-- 【为什么要这张表】在它之前「周计划编制」是一张**只读投影**：七行 = 月量 ÷ 当月作业日，
-- 打开就已经算好了，没有任何输入口。于是"这一周要干多少"这件事在系统里根本不存在 ——
-- 日目标直接由月量摊出来，把一个月里的每一天当成一模一样。
-- 而现场的周目标是**人定的**：这周要抢一个采区、下周设备大修、月底冲量，
-- 这些信息月计划表达不了，摊平的除法更表达不了。
--
-- 【口径：全矿两条腿】一周只填两个数 —— 采出(万t，煤) + 剥离(万m³，岩)，
-- 与 monthly_plan.plan_coal_wan_t / plan_strip_wan_m3 **同一口径同一单位**，
-- 这样"四周之和 vs 月计划"才对得起来。逐面/逐工序的周目标不在这里：
-- 面与工序的归属是空间维，由工序作业区定；本表只管**时间维的量**。
--
-- 【主键 = 周一日期】自然周、周一起，与 TaskLib 的 WeekPlanLink.MondayOf 同一口径。
-- 存周一那天的 yyyy-MM-dd，不存"第几周"——ISO 周号跨年那两周的定义有两套，
-- 存进去以后没人能确定当初指的是哪一周。
--
-- 【跨月的周怎么算】一周可能横跨两个月（周一 7/29、周日 8/4）。本表**不拆**：
-- 周目标就是这七天的总量，拆到日以后各天自然落到各自的月份里，
-- 月度对账按日归月累计。在这里按月拆会多出一份口径，而两份口径迟早对不上。
--
-- 【与月计划冲突时以周为准】(2026-08-20 定) 差额不拦、不改月计划，
-- 由读方逐周累计后在月度对账里点名。理由：拦下来现场就会去改月计划把差额抹掉，
-- 那样"计划与实际差了多少"这件事就永远查不到了。
--
-- 【为什么不加外键指向 monthly_plan】周可以先于月定（月计划还在审批、这周活照干），
-- 硬外键会逼人先编一份月计划出来。差额靠对账查，不靠外键挡。
-- =============================================================================

CREATE TABLE IF NOT EXISTS week_plan_target (
    monday            TEXT NOT NULL,                   -- 该周周一 yyyy-MM-dd(周口径的唯一键)
    target_coal_wan_t DOUBLE PRECISION NOT NULL DEFAULT 0,         -- 本周采出目标 万t(与 monthly_plan 同单位)
    target_strip_wan_m3 DOUBLE PRECISION NOT NULL DEFAULT 0,       -- 本周剥离目标 万m³
    source            TEXT NOT NULL DEFAULT '人工下达', -- 人工下达 / 按月摊算后确认 / 其它
    note              TEXT,
    created_at        TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at        TEXT DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (monday)
);

-- 按月份区间查这几周（'2026-08-01' <= monday <= '2026-08-31' 这类文本比较，
-- 与 shift_calendar 的 InRange 同一手法：yyyy-MM-dd 的字典序即时间序）
CREATE INDEX idx_week_target_monday ON week_plan_target(monday);
