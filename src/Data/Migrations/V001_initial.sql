-- =============================================================================
-- pmgeo.db 初始结构
-- 版本: V001
-- 说明: 15 张业务表 + 索引 + updated_at 触发器
-- =============================================================================

-- ─── 维表:equipment_model(设备型号字典)─────────────────────────────────────
CREATE TABLE IF NOT EXISTS equipment_model (
    model                   TEXT PRIMARY KEY,
    category                TEXT NOT NULL,
    working_weight_t        REAL,
    power_kw                REAL,
    bucket_m3               REAL,
    load_t                  REAL,
    dimensions_lwh          TEXT,
    drill_diameter_mm       REAL,
    tire_spec               TEXT,
    std_daily_cap_wan_m3    REAL
);

-- ─── 维表:mine_location(采区/平盘)─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS mine_location (
    location_code   TEXT PRIMARY KEY,
    name            TEXT,
    elevation_m     REAL,
    team            TEXT,
    is_active       INTEGER NOT NULL DEFAULT 1
);

-- ─── 维表:shift_calendar(班次日历)─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS shift_calendar (
    date              TEXT NOT NULL,
    shift             TEXT NOT NULL CHECK (shift IN ('A','B','C')),
    start_time        TEXT,
    leader_name       TEXT,
    is_blast_shift    INTEGER NOT NULL DEFAULT 0,
    weather           TEXT,
    notes             TEXT,
    PRIMARY KEY (date, shift)
);

-- ─── 维表:equipment(设备台账)──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS equipment (
    equipment_id        TEXT PRIMARY KEY,
    category            TEXT NOT NULL,
    model               TEXT,
    manufacturer        TEXT,
    origin              TEXT,
    serial_number       TEXT UNIQUE,
    asset_code          TEXT UNIQUE,
    status              TEXT,
    acquisition_date    TEXT,
    commission_year     INTEGER,
    cumulative_hours    REAL,
    last_overhaul_date  TEXT,
    operating_area      TEXT,
    notes               TEXT,
    created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (model) REFERENCES equipment_model(model) ON DELETE SET NULL ON UPDATE CASCADE,
    FOREIGN KEY (operating_area) REFERENCES mine_location(location_code) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_equipment_category ON equipment(category);
CREATE INDEX IF NOT EXISTS idx_equipment_status   ON equipment(status);

-- ─── 事实表:production_record(班次生产记录)──────────────────────────────
CREATE TABLE IF NOT EXISTS production_record (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    equipment_id    TEXT NOT NULL,
    date            TEXT NOT NULL,
    shift           TEXT NOT NULL,
    output_m3       REAL NOT NULL DEFAULT 0,
    work_hours      REAL NOT NULL DEFAULT 0,
    fault_hours     REAL NOT NULL DEFAULT 0,
    fault_reason    TEXT,
    created_at      TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (equipment_id, date, shift),
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_prod_dev_date ON production_record(equipment_id, date);
CREATE INDEX IF NOT EXISTS idx_prod_date     ON production_record(date);

-- ─── 事实表:fault_event(故障事件)────────────────────────────────────────
CREATE TABLE IF NOT EXISTS fault_event (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    equipment_id    TEXT NOT NULL,
    date            TEXT NOT NULL,
    shift           TEXT,
    fault_type      TEXT NOT NULL,
    duration_hours  REAL NOT NULL DEFAULT 0,
    description     TEXT,
    is_resolved     INTEGER NOT NULL DEFAULT 0,
    repair_team     TEXT,
    created_at      TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_fault_dev_date ON fault_event(equipment_id, date);
CREATE INDEX IF NOT EXISTS idx_fault_type     ON fault_event(fault_type);

-- ─── 事实表:equipment_kpi_monthly(设备月度指标)──────────────────────────
CREATE TABLE IF NOT EXISTS equipment_kpi_monthly (
    equipment_id              TEXT NOT NULL,
    year                      INTEGER NOT NULL,
    month                     INTEGER NOT NULL,
    plan_hours                REAL NOT NULL DEFAULT 0,
    work_hours                REAL NOT NULL DEFAULT 0,
    fault_hours               REAL NOT NULL DEFAULT 0,
    idle_hours                REAL NOT NULL DEFAULT 0,
    delay_hours               REAL NOT NULL DEFAULT 0,
    availability              REAL NOT NULL DEFAULT 0,
    actual_run_rate           REAL NOT NULL DEFAULT 0,
    utilization_rate          REAL NOT NULL DEFAULT 0,
    internal_fault_rate_pct   REAL NOT NULL DEFAULT 0,
    external_fault_rate_pct   REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (equipment_id, year, month),
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_kpi_ym ON equipment_kpi_monthly(year, month);

-- ─── 事实表:capacity_monthly(月度产能)──────────────────────────────────
CREATE TABLE IF NOT EXISTS capacity_monthly (
    equipment_id    TEXT NOT NULL,
    year            INTEGER NOT NULL,
    month           INTEGER NOT NULL,
    output_m3       REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (equipment_id, year, month),
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_cap_dev_year ON capacity_monthly(equipment_id, year);

-- ─── 事实表:blast_event(爆破事件)────────────────────────────────────────
CREATE TABLE IF NOT EXISTS blast_event (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    blast_date                  TEXT NOT NULL,
    blast_time                  TEXT,
    blast_seq                   INTEGER,
    drill_id                    TEXT,
    location_code               TEXT,
    material                    TEXT,
    diameter_mm                 REAL,
    hole_count                  INTEGER,
    total_hole_length_m         REAL,
    explosive_kg                REAL,
    blast_volume_m3             REAL,
    unit_consumption_kg_m3      REAL,
    FOREIGN KEY (drill_id)      REFERENCES equipment(equipment_id) ON DELETE SET NULL ON UPDATE CASCADE,
    FOREIGN KEY (location_code) REFERENCES mine_location(location_code) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_blast_date     ON blast_event(blast_date);
CREATE INDEX IF NOT EXISTS idx_blast_drill    ON blast_event(drill_id);
CREATE INDEX IF NOT EXISTS idx_blast_location ON blast_event(location_code);

-- ─── 事实表:daily_mine_summary(矿山日汇总)──────────────────────────────
CREATE TABLE IF NOT EXISTS daily_mine_summary (
    date                    TEXT PRIMARY KEY,
    big_belt_coal_t         REAL NOT NULL DEFAULT 0,
    small_belt_coal_t       REAL NOT NULL DEFAULT 0,
    longhua_coal_t          REAL NOT NULL DEFAULT 0,
    truck_coal_export_t     REAL NOT NULL DEFAULT 0,
    winnowed_coal_t         REAL NOT NULL DEFAULT 0,
    big_truck_pile_coal_t   REAL NOT NULL DEFAULT 0,
    stripping_total_m3      REAL NOT NULL DEFAULT 0,
    silo_1_t                REAL NOT NULL DEFAULT 0,
    silo_2_t                REAL NOT NULL DEFAULT 0,
    silo_3_t                REAL NOT NULL DEFAULT 0
);

-- ─── 事实表:monthly_plan(月度计划)──────────────────────────────────────
CREATE TABLE IF NOT EXISTS monthly_plan (
    year                          INTEGER NOT NULL,
    month                         INTEGER NOT NULL,
    plan_strip_wan_m3             REAL NOT NULL DEFAULT 0,
    plan_coal_wan_t               REAL NOT NULL DEFAULT 0,
    plan_outsource_strip_wan_m3   REAL NOT NULL DEFAULT 0,
    ratio_strip_coal              REAL NOT NULL DEFAULT 0,
    avg_distance_km               REAL NOT NULL DEFAULT 0,
    avg_height_m                  REAL NOT NULL DEFAULT 0,
    team1_distance_km             REAL NOT NULL DEFAULT 0,
    team2_distance_km             REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (year, month)
);

-- ─── 子表:monthly_plan_shovel(月度铲位安排)─────────────────────────────
CREATE TABLE IF NOT EXISTS monthly_plan_shovel (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    year            INTEGER NOT NULL,
    month           INTEGER NOT NULL,
    equipment_id    TEXT NOT NULL,
    location_code   TEXT NOT NULL,
    FOREIGN KEY (year, month)    REFERENCES monthly_plan(year, month) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (equipment_id)   REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (location_code)  REFERENCES mine_location(location_code) ON DELETE RESTRICT ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_plan_shovel_ym ON monthly_plan_shovel(year, month);

-- ─── 事实表:workforce_monthly(工种月度效率)─────────────────────────────
CREATE TABLE IF NOT EXISTS workforce_monthly (
    year                         INTEGER NOT NULL,
    month                        INTEGER NOT NULL,
    headcount                    REAL NOT NULL DEFAULT 0,
    attendance_workdays          REAL NOT NULL DEFAULT 0,
    coal_headcount               REAL NOT NULL DEFAULT 0,
    coal_workdays                REAL NOT NULL DEFAULT 0,
    coal_output_t                REAL NOT NULL DEFAULT 0,
    overall_eff_t_per_workday    REAL NOT NULL DEFAULT 0,
    coal_eff_t_per_workday       REAL NOT NULL DEFAULT 0,
    overall_eff_t_per_person     REAL NOT NULL DEFAULT 0,
    coal_eff_t_per_person        REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (year, month)
);

-- ─── 事实表:long_term_metric(长周期指标 EAV)───────────────────────────
CREATE TABLE IF NOT EXISTS long_term_metric (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    source  TEXT NOT NULL,
    item    TEXT NOT NULL,
    unit    TEXT,
    year    INTEGER NOT NULL,
    month   INTEGER,
    value   REAL NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_lt_src_item_yr ON long_term_metric(source, item, year);
CREATE INDEX IF NOT EXISTS idx_lt_ym          ON long_term_metric(year, month);

-- ─── 配置表:dispatch_rule(编组规则)─────────────────────────────────────
CREATE TABLE IF NOT EXISTS dispatch_rule (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    shovel_model                TEXT NOT NULL,
    truck_model                 TEXT NOT NULL,
    bucket_loads_per_truck      REAL NOT NULL DEFAULT 0,
    recommended_truck_count     INTEGER NOT NULL DEFAULT 0,
    cycle_time_min              REAL NOT NULL DEFAULT 0,
    efficiency_score            INTEGER NOT NULL DEFAULT 0,
    effective_from              TEXT,
    is_active                   INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (shovel_model) REFERENCES equipment_model(model) ON UPDATE CASCADE,
    FOREIGN KEY (truck_model)  REFERENCES equipment_model(model) ON UPDATE CASCADE
);

-- ─── 触发器:自动维护 equipment.updated_at ───────────────────────────────
CREATE TRIGGER IF NOT EXISTS trg_equipment_updated_at
AFTER UPDATE ON equipment
FOR EACH ROW
BEGIN
    UPDATE equipment SET updated_at = CURRENT_TIMESTAMP WHERE equipment_id = NEW.equipment_id;
END;
