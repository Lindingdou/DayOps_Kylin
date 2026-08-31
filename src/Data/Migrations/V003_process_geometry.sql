-- =============================================================================
-- V003: 工艺几何与设计参数(Phase 1)
--
-- 4 张表覆盖采装/排土/边坡/运输四个工序的设计参数。
-- 与 equipment / equipment_model / mine_location 全部建立 FK 联动,
-- 支撑"设计方案工作台"做基于参数的设计 + 设备配置推荐。
-- =============================================================================

-- ─── ① working_face(工作面 / 台阶几何)─────────────────────────────────────
CREATE TABLE IF NOT EXISTS working_face (
    id                           INTEGER PRIMARY KEY AUTOINCREMENT,
    face_code                    TEXT NOT NULL UNIQUE,           -- 工作面编号 WF-1195-A
    location_code                TEXT,                            -- 平盘编码 FK→mine_location
    equipment_id                 TEXT,                            -- 主电铲编号 FK→equipment
    bench_height_m               REAL NOT NULL,                   -- 台阶高度(m,标 12-15)
    bench_slope_angle_deg        REAL,                            -- 台阶坡面角(°,标 65-75)
    working_platform_width_m     REAL,                            -- 工作平台宽度(m,30-50)
    safety_platform_width_m      REAL,                            -- 安全平台宽度(m,6-12)
    mining_width_m               REAL,                            -- 采宽(m,35-60)
    face_length_m                REAL,                            -- 工作面长度(m,150-300)
    advance_rate_m_per_month     REAL,                            -- 月推进度(m/月,20-60)
    material                     TEXT,                            -- 物料类型 c4/c9/rh/coal
    rock_hardness                TEXT,                            -- 岩石硬度 hard/medium/soft
    effective_from               TEXT NOT NULL DEFAULT (date('now')),
    effective_to                 TEXT,                            -- 失效日(NULL = 现行)
    status                       TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active','closed','planning')),
    notes                        TEXT,
    created_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (location_code) REFERENCES mine_location(location_code) ON DELETE SET NULL ON UPDATE CASCADE,
    FOREIGN KEY (equipment_id)  REFERENCES equipment(equipment_id) ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_face_location  ON working_face(location_code);
CREATE INDEX IF NOT EXISTS idx_face_equipment ON working_face(equipment_id);
CREATE INDEX IF NOT EXISTS idx_face_status    ON working_face(status);

CREATE TRIGGER IF NOT EXISTS trg_working_face_updated_at
AFTER UPDATE ON working_face FOR EACH ROW BEGIN
    UPDATE working_face SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;

-- ─── ② dump_site(排土场)───────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS dump_site (
    dump_id                      TEXT PRIMARY KEY,                -- 排土场编号 D-N1
    name                         TEXT NOT NULL,                   -- 名称 北排土场
    dump_type                    TEXT NOT NULL CHECK (dump_type IN ('internal','external')),
    design_capacity_wan_m3       REAL NOT NULL,                   -- 设计容量(万 m³)
    current_filled_wan_m3        REAL NOT NULL DEFAULT 0,         -- 当前已堆(万 m³)
    max_height_m                 REAL,                            -- 最大堆高(m)
    bench_height_m               REAL,                            -- 单层堆高(m,10-15)
    overall_slope_angle_deg      REAL,                            -- 整体坡角(°,30-35)
    bench_slope_angle_deg        REAL,                            -- 台阶坡角(°,35-40)
    service_years_remaining      REAL,                            -- 剩余服务年限
    start_date                   TEXT,                            -- 启用日期
    close_date                   TEXT,                            -- 关闭日期(预计)
    responsible_dozer_id         TEXT,                            -- 主推土机 FK→equipment
    status                       TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active','full','closed')),
    notes                        TEXT,
    created_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (responsible_dozer_id) REFERENCES equipment(equipment_id) ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_dump_status ON dump_site(status);

CREATE TRIGGER IF NOT EXISTS trg_dump_site_updated_at
AFTER UPDATE ON dump_site FOR EACH ROW BEGIN
    UPDATE dump_site SET updated_at = CURRENT_TIMESTAMP WHERE dump_id = NEW.dump_id;
END;

-- ─── ③ slope_design(边坡设计)──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS slope_design (
    id                           INTEGER PRIMARY KEY AUTOINCREMENT,
    side_name                    TEXT NOT NULL,                   -- 帮别 东帮/西帮/南帮/北帮 / 工作帮
    side_type                    TEXT NOT NULL CHECK (side_type IN ('working','final','transition')),
    working_slope_angle_deg      REAL,                            -- 工作帮坡角(°,20-35)
    final_slope_angle_deg        REAL,                            -- 最终帮坡角(°,35-45)
    max_depth_m                  REAL,                            -- 最大开采深度(m)
    safety_factor                REAL,                            -- 安全系数 F(≥1.30 安全)
    cohesion_kpa                 REAL,                            -- 内聚力 c(kPa)
    friction_angle_deg           REAL,                            -- 内摩擦角 φ(°,28-40)
    rock_type                    TEXT,                            -- 岩性
    groundwater_level_m          REAL,                            -- 地下水位(m,负值表深)
    effective_from               TEXT NOT NULL DEFAULT (date('now')),
    effective_to                 TEXT,
    design_version               TEXT,                            -- 设计版本号
    designed_by                  TEXT,                            -- 设计单位/人
    notes                        TEXT,
    created_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_slope_side ON slope_design(side_name);
CREATE INDEX IF NOT EXISTS idx_slope_type ON slope_design(side_type);

CREATE TRIGGER IF NOT EXISTS trg_slope_design_updated_at
AFTER UPDATE ON slope_design FOR EACH ROW BEGIN
    UPDATE slope_design SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;

-- ─── ④ haul_road(运输道路网络)─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS haul_road (
    road_id                      TEXT PRIMARY KEY,                -- 路段编号 R-001
    name                         TEXT NOT NULL,                   -- 路段名称
    road_type                    TEXT NOT NULL CHECK (road_type IN ('main','branch','dump','temp')),
    start_location               TEXT,                            -- 起点 平盘/采区
    end_location                 TEXT,                            -- 终点 排土场/破碎站
    length_m                     REAL NOT NULL,                   -- 长度(m)
    max_slope_pct                REAL,                            -- 最大坡度(%,推荐 ≤8)
    avg_slope_pct                REAL,                            -- 平均坡度(%)
    road_width_m                 REAL,                            -- 路宽(m,30-40)
    turning_radius_m             REAL,                            -- 转弯半径(m,≥30)
    pavement_type                TEXT,                            -- 路面 gravel/compacted/paved
    max_load_t                   REAL,                            -- 最大允许载重(t)
    primary_truck_model          TEXT,                            -- 主要服务车型 FK→equipment_model
    maintenance_team             TEXT,                            -- 维护队组
    last_maintenance_date        TEXT,                            -- 最后维护日期
    condition                    TEXT NOT NULL DEFAULT 'good' CHECK (condition IN ('good','fair','poor','closed')),
    notes                        TEXT,
    created_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (primary_truck_model) REFERENCES equipment_model(model) ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_road_type      ON haul_road(road_type);
CREATE INDEX IF NOT EXISTS idx_road_condition ON haul_road(condition);

CREATE TRIGGER IF NOT EXISTS trg_haul_road_updated_at
AFTER UPDATE ON haul_road FOR EACH ROW BEGIN
    UPDATE haul_road SET updated_at = CURRENT_TIMESTAMP WHERE road_id = NEW.road_id;
END;
