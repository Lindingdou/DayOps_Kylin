-- 由 build/sqlite2dm.py 从 V001_initial.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- pmgeo.db 初始结构
-- 版本: V001
-- 说明: 15 张业务表 + 索引 + updated_at 触发器
-- =============================================================================

-- ─── 维表:equipment_model(设备型号字典)─────────────────────────────────────
CREATE TABLE equipment_model (
    model                   VARCHAR(255) PRIMARY KEY,
    category                VARCHAR(255) NOT NULL,
    working_weight_t        DOUBLE,
    power_kw                DOUBLE,
    bucket_m3               DOUBLE,
    load_t                  DOUBLE,
    dimensions_lwh          VARCHAR(255),
    drill_diameter_mm       DOUBLE,
    tire_spec               VARCHAR(255),
    std_daily_cap_wan_m3    DOUBLE
);

-- ─── 维表:mine_location(采区/平盘)─────────────────────────────────────────
CREATE TABLE mine_location (
    location_code   VARCHAR(255) PRIMARY KEY,
    name            VARCHAR(255),
    elevation_m     DOUBLE,
    team            VARCHAR(255),
    is_active       INT NOT NULL DEFAULT 1
);

-- ─── 维表:shift_calendar(班次日历)─────────────────────────────────────────
CREATE TABLE shift_calendar (
    "date"              VARCHAR(255) NOT NULL,
    shift             VARCHAR(255) NOT NULL CHECK (shift IN ('A','B','C')),
    start_time        VARCHAR(255),
    leader_name       VARCHAR(255),
    is_blast_shift    INT NOT NULL DEFAULT 0,
    weather           VARCHAR(255),
    notes             VARCHAR(2000),
    PRIMARY KEY ("date", shift)
);

-- ─── 维表:equipment(设备台账)──────────────────────────────────────────────
CREATE TABLE equipment (
    equipment_id        VARCHAR(255) PRIMARY KEY,
    category            VARCHAR(255) NOT NULL,
    model               VARCHAR(255),
    manufacturer        VARCHAR(255),
    origin              VARCHAR(255),
    serial_number       VARCHAR(255) UNIQUE,
    asset_code          VARCHAR(255) UNIQUE,
    "status"              VARCHAR(255),
    acquisition_date    VARCHAR(255),
    commission_year     INT,
    cumulative_hours    DOUBLE,
    last_overhaul_date  VARCHAR(255),
    operating_area      VARCHAR(255),
    notes               VARCHAR(2000),
    created_at          VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (model) REFERENCES equipment_model(model) ON DELETE SET NULL ON UPDATE CASCADE,
    FOREIGN KEY (operating_area) REFERENCES mine_location(location_code) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX idx_equipment_category ON equipment(category);
CREATE INDEX idx_equipment_status   ON equipment("status");

-- ─── 事实表:production_record(班次生产记录)──────────────────────────────
CREATE TABLE production_record (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    equipment_id    VARCHAR(255) NOT NULL,
    "date"            VARCHAR(255) NOT NULL,
    shift           VARCHAR(255) NOT NULL,
    output_m3       DOUBLE NOT NULL DEFAULT 0,
    work_hours      DOUBLE NOT NULL DEFAULT 0,
    fault_hours     DOUBLE NOT NULL DEFAULT 0,
    fault_reason    VARCHAR(2000),
    created_at      VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (equipment_id, "date", shift),
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_prod_dev_date ON production_record(equipment_id, "date");
CREATE INDEX idx_prod_date     ON production_record("date");

-- ─── 事实表:fault_event(故障事件)────────────────────────────────────────
CREATE TABLE fault_event (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    equipment_id    VARCHAR(255) NOT NULL,
    "date"            VARCHAR(255) NOT NULL,
    shift           VARCHAR(255),
    fault_type      VARCHAR(255) NOT NULL,
    duration_hours  DOUBLE NOT NULL DEFAULT 0,
    description     VARCHAR(2000),
    is_resolved     INT NOT NULL DEFAULT 0,
    repair_team     VARCHAR(255),
    created_at      VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_fault_dev_date ON fault_event(equipment_id, "date");
CREATE INDEX idx_fault_type     ON fault_event(fault_type);

-- ─── 事实表:equipment_kpi_monthly(设备月度指标)──────────────────────────
CREATE TABLE equipment_kpi_monthly (
    equipment_id              VARCHAR(255) NOT NULL,
    "year"                      INT NOT NULL,
    "month"                     INT NOT NULL,
    plan_hours                DOUBLE NOT NULL DEFAULT 0,
    work_hours                DOUBLE NOT NULL DEFAULT 0,
    fault_hours               DOUBLE NOT NULL DEFAULT 0,
    idle_hours                DOUBLE NOT NULL DEFAULT 0,
    delay_hours               DOUBLE NOT NULL DEFAULT 0,
    availability              DOUBLE NOT NULL DEFAULT 0,
    actual_run_rate           DOUBLE NOT NULL DEFAULT 0,
    utilization_rate          DOUBLE NOT NULL DEFAULT 0,
    internal_fault_rate_pct   DOUBLE NOT NULL DEFAULT 0,
    external_fault_rate_pct   DOUBLE NOT NULL DEFAULT 0,
    PRIMARY KEY (equipment_id, "year", "month"),
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_kpi_ym ON equipment_kpi_monthly("year", "month");

-- ─── 事实表:capacity_monthly(月度产能)──────────────────────────────────
CREATE TABLE capacity_monthly (
    equipment_id    VARCHAR(255) NOT NULL,
    "year"            INT NOT NULL,
    "month"           INT NOT NULL,
    output_m3       DOUBLE NOT NULL DEFAULT 0,
    PRIMARY KEY (equipment_id, "year", "month"),
    FOREIGN KEY (equipment_id) REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_cap_dev_year ON capacity_monthly(equipment_id, "year");

-- ─── 事实表:blast_event(爆破事件)────────────────────────────────────────
CREATE TABLE blast_event (
    id                          INT IDENTITY(1,1) PRIMARY KEY,
    blast_date                  VARCHAR(255) NOT NULL,
    blast_time                  VARCHAR(255),
    blast_seq                   INT,
    drill_id                    VARCHAR(255),
    location_code               VARCHAR(255),
    material                    VARCHAR(255),
    diameter_mm                 DOUBLE,
    hole_count                  INT,
    total_hole_length_m         DOUBLE,
    explosive_kg                DOUBLE,
    blast_volume_m3             DOUBLE,
    unit_consumption_kg_m3      DOUBLE,
    FOREIGN KEY (drill_id)      REFERENCES equipment(equipment_id) ON DELETE SET NULL ON UPDATE CASCADE,
    FOREIGN KEY (location_code) REFERENCES mine_location(location_code) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX idx_blast_date     ON blast_event(blast_date);
CREATE INDEX idx_blast_drill    ON blast_event(drill_id);
CREATE INDEX idx_blast_location ON blast_event(location_code);

-- ─── 事实表:daily_mine_summary(矿山日汇总)──────────────────────────────
CREATE TABLE daily_mine_summary (
    "date"                    VARCHAR(255) PRIMARY KEY,
    big_belt_coal_t         DOUBLE NOT NULL DEFAULT 0,
    small_belt_coal_t       DOUBLE NOT NULL DEFAULT 0,
    longhua_coal_t          DOUBLE NOT NULL DEFAULT 0,
    truck_coal_export_t     DOUBLE NOT NULL DEFAULT 0,
    winnowed_coal_t         DOUBLE NOT NULL DEFAULT 0,
    big_truck_pile_coal_t   DOUBLE NOT NULL DEFAULT 0,
    stripping_total_m3      DOUBLE NOT NULL DEFAULT 0,
    silo_1_t                DOUBLE NOT NULL DEFAULT 0,
    silo_2_t                DOUBLE NOT NULL DEFAULT 0,
    silo_3_t                DOUBLE NOT NULL DEFAULT 0
);

-- ─── 事实表:monthly_plan(月度计划)──────────────────────────────────────
CREATE TABLE monthly_plan (
    "year"                          INT NOT NULL,
    "month"                         INT NOT NULL,
    plan_strip_wan_m3             DOUBLE NOT NULL DEFAULT 0,
    plan_coal_wan_t               DOUBLE NOT NULL DEFAULT 0,
    plan_outsource_strip_wan_m3   DOUBLE NOT NULL DEFAULT 0,
    ratio_strip_coal              DOUBLE NOT NULL DEFAULT 0,
    avg_distance_km               DOUBLE NOT NULL DEFAULT 0,
    avg_height_m                  DOUBLE NOT NULL DEFAULT 0,
    team1_distance_km             DOUBLE NOT NULL DEFAULT 0,
    team2_distance_km             DOUBLE NOT NULL DEFAULT 0,
    PRIMARY KEY ("year", "month")
);

-- ─── 子表:monthly_plan_shovel(月度铲位安排)─────────────────────────────
CREATE TABLE monthly_plan_shovel (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    "year"            INT NOT NULL,
    "month"           INT NOT NULL,
    equipment_id    VARCHAR(255) NOT NULL,
    location_code   VARCHAR(255) NOT NULL,
    FOREIGN KEY ("year", "month")    REFERENCES monthly_plan("year", "month") ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (equipment_id)   REFERENCES equipment(equipment_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (location_code)  REFERENCES mine_location(location_code) ON DELETE RESTRICT ON UPDATE CASCADE
);

CREATE INDEX idx_plan_shovel_ym ON monthly_plan_shovel("year", "month");

-- ─── 事实表:workforce_monthly(工种月度效率)─────────────────────────────
CREATE TABLE workforce_monthly (
    "year"                         INT NOT NULL,
    "month"                        INT NOT NULL,
    headcount                    DOUBLE NOT NULL DEFAULT 0,
    attendance_workdays          DOUBLE NOT NULL DEFAULT 0,
    coal_headcount               DOUBLE NOT NULL DEFAULT 0,
    coal_workdays                DOUBLE NOT NULL DEFAULT 0,
    coal_output_t                DOUBLE NOT NULL DEFAULT 0,
    overall_eff_t_per_workday    DOUBLE NOT NULL DEFAULT 0,
    coal_eff_t_per_workday       DOUBLE NOT NULL DEFAULT 0,
    overall_eff_t_per_person     DOUBLE NOT NULL DEFAULT 0,
    coal_eff_t_per_person        DOUBLE NOT NULL DEFAULT 0,
    PRIMARY KEY ("year", "month")
);

-- ─── 事实表:long_term_metric(长周期指标 EAV)───────────────────────────
CREATE TABLE long_term_metric (
    id      INT IDENTITY(1,1) PRIMARY KEY,
    "source"  VARCHAR(255) NOT NULL,
    item    VARCHAR(255) NOT NULL,
    "unit"    VARCHAR(255),
    "year"    INT NOT NULL,
    "month"   INT,
    "value"   DOUBLE NOT NULL DEFAULT 0
);

CREATE INDEX idx_lt_src_item_yr ON long_term_metric("source", item, "year");
CREATE INDEX idx_lt_ym          ON long_term_metric("year", "month");

-- ─── 配置表:dispatch_rule(编组规则)─────────────────────────────────────
CREATE TABLE dispatch_rule (
    id                          INT IDENTITY(1,1) PRIMARY KEY,
    shovel_model                VARCHAR(255) NOT NULL,
    truck_model                 VARCHAR(255) NOT NULL,
    bucket_loads_per_truck      DOUBLE NOT NULL DEFAULT 0,
    recommended_truck_count     INT NOT NULL DEFAULT 0,
    cycle_time_min              DOUBLE NOT NULL DEFAULT 0,
    efficiency_score            INT NOT NULL DEFAULT 0,
    effective_from              VARCHAR(255),
    is_active                   INT NOT NULL DEFAULT 1,
    FOREIGN KEY (shovel_model) REFERENCES equipment_model(model) ON UPDATE CASCADE,
    FOREIGN KEY (truck_model)  REFERENCES equipment_model(model) ON UPDATE CASCADE
);

-- ─── 触发器:自动维护 equipment.updated_at ───────────────────────────────
CREATE TRIGGER trg_equipment_updated_at
BEFORE UPDATE ON equipment
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;
