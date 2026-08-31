-- =============================================================================
-- V044: 穿孔作业计划(drill_plan) —— 工序链「穿孔 → 爆破 → 采装」缺的那一环
--
-- 【为什么要这张表】ExploderConfig.Drills 此前**只有样例盘子里有**：装配层明写
-- "穿孔计划暂无台账，本日不排"，于是真库上钻机一条任务都排不出来，甘特里没有穿孔条、
-- 工序进度跟踪的穿孔一栏恒 0%、「钻爆计划衔接」拿不到穿孔窗口。
-- 下游已经有 blast_event（爆了什么）与 maintenance_window（几点到几点不能干），
-- 唯独"今天哪台钻机在哪个待爆区打几点到几点"没有落点。
--
-- 【与 blast_event 的分工】blast_event 是**已发生的事实**（炮次/装药/方量/单耗，事后填）；
-- 本表是**计划**（明天谁去哪打孔）。两者按 待爆区(zone) + 日期 相互对照：
-- 有穿孔计划却迟迟没有对应炮次 = 采准脱节，这正是「衔接」二字要看的东西。
-- 不做外键：现场先打孔后补炮记录、或一次穿孔分两次爆破都是常态，硬约束会逼人造假数据。
--
-- 【为什么按 设备 × 日期 × 起始时刻 做主键】与 maintenance_window 同构：
-- 一台钻机一天可能打两个区（上午 A 区、下午 B 区），起始时刻进主键才存得下。
-- 待爆区 zone 不进主键——同一台机同一时刻不可能在两个区。
--
-- 【时刻用 TEXT 'HH:mm'】与 shift_calendar.start_time / blast_event.blast_time /
-- maintenance_window.start_time 同一口径，由 TaskLib 侧统一 ParseHour 解析成 0..24 小时数。
-- 跨零点请拆两条：装箱的时窗是同一天内的 [start,end)，绕回 0 点会把次日的活算进今天。
--
-- 【bench_elevation_m 可空】台阶标高有就填（甘特与衔接判定会显示 +72），没有不拦：
-- 0 是合法标高，用 NULL 表达"没录"，不许拿 0 冒充（同 mineable_region 的 z 陷阱）。
-- =============================================================================

CREATE TABLE IF NOT EXISTS drill_plan (
    equipment_id      TEXT NOT NULL,                   -- 钻机编号(= equipment.equipment_id)
    plan_date         TEXT NOT NULL,                   -- 作业日期 yyyy-MM-dd
    start_time        TEXT NOT NULL,                   -- 起 HH:mm
    end_time          TEXT NOT NULL,                   -- 止 HH:mm(同日内;跨零点拆两条)
    zone              TEXT NOT NULL DEFAULT '',        -- 待爆区/平盘(对 blast_event.location_code 或作业面名)
    bench_elevation_m REAL,                            -- 台阶标高 m(NULL=未录,0 是合法值)
    hole_count        INTEGER,                         -- 计划孔数(NULL=未录)
    hole_length_m     REAL,                            -- 计划延米 m(NULL=未录)
    status            TEXT NOT NULL DEFAULT '计划',    -- 计划 / 进行中 / 完成 / 取消
    note              TEXT,
    created_at        TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at        TEXT DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (equipment_id, plan_date, start_time)
);

-- 按日取本日全部穿孔计划（装配盘子时每天问一次）
CREATE INDEX IF NOT EXISTS idx_drill_plan_date ON drill_plan(plan_date);

-- 按待爆区回查（钻爆衔接：这个区的孔谁在打、打完没有）
CREATE INDEX IF NOT EXISTS idx_drill_plan_zone ON drill_plan(zone);

CREATE TRIGGER IF NOT EXISTS trg_drill_plan_updated_at
AFTER UPDATE ON drill_plan FOR EACH ROW BEGIN
    UPDATE drill_plan SET updated_at = CURRENT_TIMESTAMP
    WHERE equipment_id = NEW.equipment_id AND plan_date = NEW.plan_date AND start_time = NEW.start_time;
END;
