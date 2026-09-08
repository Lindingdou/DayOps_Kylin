-- 由 build/sqlite2dm.py 从 V003_process_geometry.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V003: 工艺几何与设计参数(Phase 1)
--
-- 4 张表覆盖采装/排土/边坡/运输四个工序的设计参数。
-- 与 equipment / equipment_model / mine_location 全部建立 FK 联动,
-- 支撑"设计方案工作台"做基于参数的设计 + 设备配置推荐。
-- =============================================================================

-- ─── ① working_face(工作面 / 台阶几何)─────────────────────────────────────
CREATE TABLE working_face (
    id                           INT IDENTITY(1,1) PRIMARY KEY,
    face_code                    VARCHAR(255) NOT NULL UNIQUE,           -- 工作面编号 WF-1195-A
    location_code                VARCHAR(255),                            -- 平盘编码 FK→mine_location
    equipment_id                 VARCHAR(255),                            -- 主电铲编号 FK→equipment
    bench_height_m               DOUBLE NOT NULL,                   -- 台阶高度(m,标 12-15)
    bench_slope_angle_deg        DOUBLE,                            -- 台阶坡面角(°,标 65-75)
    working_platform_width_m     DOUBLE,                            -- 工作平台宽度(m,30-50)
    safety_platform_width_m      DOUBLE,                            -- 安全平台宽度(m,6-12)
    mining_width_m               DOUBLE,                            -- 采宽(m,35-60)
    face_length_m                DOUBLE,                            -- 工作面长度(m,150-300)
    advance_rate_m_per_month     DOUBLE,                            -- 月推进度(m/月,20-60)
    material                     VARCHAR(255),                            -- 物料类型 c4/c9/rh/coal
    rock_hardness                VARCHAR(255),                            -- 岩石硬度 hard/medium/soft
    effective_from               VARCHAR(255) NOT NULL DEFAULT (CURRENT_DATE),
    effective_to                 VARCHAR(255),                            -- 失效日(NULL = 现行)
    "status"                       VARCHAR(255) NOT NULL DEFAULT 'active' CHECK ("status" IN ('active','closed','planning')),
    notes                        VARCHAR(2000),
    created_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (location_code) REFERENCES mine_location(location_code) ON DELETE SET NULL ON UPDATE CASCADE,
    FOREIGN KEY (equipment_id)  REFERENCES equipment(equipment_id) ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX idx_face_location  ON working_face(location_code);
CREATE INDEX idx_face_equipment ON working_face(equipment_id);
CREATE INDEX idx_face_status    ON working_face("status");

CREATE TRIGGER trg_working_face_updated_at
BEFORE UPDATE ON working_face
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;

-- ─── ② dump_site(排土场)───────────────────────────────────────────────────
CREATE TABLE dump_site (
    dump_id                      VARCHAR(255) PRIMARY KEY,                -- 排土场编号 D-N1
    name                         VARCHAR(255) NOT NULL,                   -- 名称 北排土场
    dump_type                    VARCHAR(255) NOT NULL CHECK (dump_type IN ('internal','external')),
    design_capacity_wan_m3       DOUBLE NOT NULL,                   -- 设计容量(万 m³)
    current_filled_wan_m3        DOUBLE NOT NULL DEFAULT 0,         -- 当前已堆(万 m³)
    max_height_m                 DOUBLE,                            -- 最大堆高(m)
    bench_height_m               DOUBLE,                            -- 单层堆高(m,10-15)
    overall_slope_angle_deg      DOUBLE,                            -- 整体坡角(°,30-35)
    bench_slope_angle_deg        DOUBLE,                            -- 台阶坡角(°,35-40)
    service_years_remaining      DOUBLE,                            -- 剩余服务年限
    start_date                   VARCHAR(255),                            -- 启用日期
    close_date                   VARCHAR(255),                            -- 关闭日期(预计)
    responsible_dozer_id         VARCHAR(255),                            -- 主推土机 FK→equipment
    "status"                       VARCHAR(255) NOT NULL DEFAULT 'active' CHECK ("status" IN ('active','full','closed')),
    notes                        VARCHAR(2000),
    created_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (responsible_dozer_id) REFERENCES equipment(equipment_id) ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX idx_dump_status ON dump_site("status");

CREATE TRIGGER trg_dump_site_updated_at
BEFORE UPDATE ON dump_site
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;

-- ─── ③ slope_design(边坡设计)──────────────────────────────────────────────
CREATE TABLE slope_design (
    id                           INT IDENTITY(1,1) PRIMARY KEY,
    side_name                    VARCHAR(255) NOT NULL,                   -- 帮别 东帮/西帮/南帮/北帮 / 工作帮
    side_type                    VARCHAR(255) NOT NULL CHECK (side_type IN ('working','final','transition')),
    working_slope_angle_deg      DOUBLE,                            -- 工作帮坡角(°,20-35)
    final_slope_angle_deg        DOUBLE,                            -- 最终帮坡角(°,35-45)
    max_depth_m                  DOUBLE,                            -- 最大开采深度(m)
    safety_factor                DOUBLE,                            -- 安全系数 F(≥1.30 安全)
    cohesion_kpa                 DOUBLE,                            -- 内聚力 c(kPa)
    friction_angle_deg           DOUBLE,                            -- 内摩擦角 φ(°,28-40)
    rock_type                    VARCHAR(255),                            -- 岩性
    groundwater_level_m          DOUBLE,                            -- 地下水位(m,负值表深)
    effective_from               VARCHAR(255) NOT NULL DEFAULT (CURRENT_DATE),
    effective_to                 VARCHAR(255),
    design_version               VARCHAR(255),                            -- 设计版本号
    designed_by                  VARCHAR(255),                            -- 设计单位/人
    notes                        VARCHAR(2000),
    created_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX idx_slope_side ON slope_design(side_name);
CREATE INDEX idx_slope_type ON slope_design(side_type);

CREATE TRIGGER trg_slope_design_updated_at
BEFORE UPDATE ON slope_design
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;

-- ─── ④ haul_road(运输道路网络)─────────────────────────────────────────────
CREATE TABLE haul_road (
    road_id                      VARCHAR(255) PRIMARY KEY,                -- 路段编号 R-001
    name                         VARCHAR(255) NOT NULL,                   -- 路段名称
    road_type                    VARCHAR(255) NOT NULL CHECK (road_type IN ('main','branch','dump','temp')),
    start_location               VARCHAR(255),                            -- 起点 平盘/采区
    end_location                 VARCHAR(255),                            -- 终点 排土场/破碎站
    length_m                     DOUBLE NOT NULL,                   -- 长度(m)
    max_slope_pct                DOUBLE,                            -- 最大坡度(%,推荐 ≤8)
    avg_slope_pct                DOUBLE,                            -- 平均坡度(%)
    road_width_m                 DOUBLE,                            -- 路宽(m,30-40)
    turning_radius_m             DOUBLE,                            -- 转弯半径(m,≥30)
    pavement_type                VARCHAR(255),                            -- 路面 gravel/compacted/paved
    max_load_t                   DOUBLE,                            -- 最大允许载重(t)
    primary_truck_model          VARCHAR(255),                            -- 主要服务车型 FK→equipment_model
    maintenance_team             VARCHAR(255),                            -- 维护队组
    last_maintenance_date        VARCHAR(255),                            -- 最后维护日期
    condition                    VARCHAR(255) NOT NULL DEFAULT 'good' CHECK (condition IN ('good','fair','poor','closed')),
    notes                        VARCHAR(2000),
    created_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                   VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (primary_truck_model) REFERENCES equipment_model(model) ON DELETE SET NULL ON UPDATE CASCADE
);
CREATE INDEX idx_road_type      ON haul_road(road_type);
CREATE INDEX idx_road_condition ON haul_road(condition);

CREATE TRIGGER trg_haul_road_updated_at
BEFORE UPDATE ON haul_road
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;
