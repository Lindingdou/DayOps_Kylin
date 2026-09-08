-- 由 build/sqlite2pg.py 从 V042_maintenance_window.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V042: 检修档期(maintenance_window) —— 编制装箱的第三个时间输入
--
-- 装箱的有效作业时窗 = 班起 − 【检修】 − 爆破清场 − 交接班损失。
-- 三个减项里，爆破有 blast_event 表、交接班是常数，**只有检修一直没有表**：
-- ExploderConfig.Maintenance 在台账模式下恒为空，样例盘子里有、真实盘子里没有。
-- 后果是一台今天上午定修的电铲，计划里从 0 点起就在满负荷干活。
--
-- 【与设备状态的分工】equipment.status='Maintenance' 说的是"这台现在处于检修状态"，
-- 是个**当前态**，答不了"几点到几点"；本表是**有起止时刻的档期**，能进时窗计算。
-- 两者都要：状态用于"该不该给它排活"(EquipmentAvailability)，档期用于"哪几个小时排不了"。
--
-- 【为什么按 日 × 设备 × 起始时刻 做主键】同一台设备一天可能有多段（上午定修、下午保养），
-- 起始时刻进主键才存得下；kind 不进主键——同一时段不可能既是定修又是保养。
--
-- 【时刻用 TEXT 'HH:mm'】与 shift_calendar.start_time、blast_event.blast_time 同一口径，
-- 由 TaskLib 侧统一解析成 0..24 的小时数（ParseHour：认 'HH:mm' 也认纯小时数）。
-- 跨零点的档期请拆成两条（当日 22:00–24:00 + 次日 00:00–02:00）：
-- 装箱的时窗是同一天内的 [start,end)，让它绕回 0 点会把次日的活算进今天。
-- =============================================================================

CREATE TABLE IF NOT EXISTS maintenance_window (
    equipment_id  TEXT NOT NULL,                       -- 设备编号(= equipment.equipment_id)
    plan_date     TEXT NOT NULL,                       -- 检修日期 yyyy-MM-dd
    start_time    TEXT NOT NULL,                       -- 起 HH:mm
    end_time      TEXT NOT NULL,                       -- 止 HH:mm(同日内;跨零点拆两条)
    kind          TEXT NOT NULL DEFAULT '定修',        -- 定修 / 保养 / 临修 / 年检
    note          TEXT,                                -- 备注(检修内容、承修班组)
    created_at    TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at    TEXT DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (equipment_id, plan_date, start_time)
);

-- 按日取本日全部档期（装配盘子时每天问一次）
CREATE INDEX idx_maintenance_window_date
    ON maintenance_window(plan_date);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_maintenance_window_updated_at ON maintenance_window;
CREATE TRIGGER trg_maintenance_window_updated_at
BEFORE UPDATE ON maintenance_window
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();
