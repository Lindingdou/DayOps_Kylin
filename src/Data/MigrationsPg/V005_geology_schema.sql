-- 由 build/sqlite2pg.py 从 V005_geology_schema.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- pmgeo.db 地质模块扩展
-- 版本: V005
-- 说明: 钻孔 + 煤层成果 + 煤芯化验 + 见煤点 + 岩性分层 + 3 张字典 + 审核
--       共 10 张业务表 + 3 张视图 + 字典 seed
-- 用途: 支撑钻孔柱状图绘制 + 煤层数据分析（统计/空间/审核/看板）
-- 编号说明: V001 设备初始 / V002 设备 seed / V003 工艺几何 / V004 工艺几何 seed / V005 地质
-- =============================================================================

-- ─── 字典:coal_seam_def(煤层定义)──────────────────────────────────────────
CREATE TABLE IF NOT EXISTS coal_seam_def (
    id            BIGSERIAL PRIMARY KEY,
    code          TEXT    NOT NULL UNIQUE,         -- "4-1"/"9"/"11"/...
    name          TEXT    NOT NULL,                -- 显示名 "4-1 号煤层"
    sort_order    BIGINT NOT NULL DEFAULT 0,
    avg_thickness DOUBLE PRECISION,                            -- 矿区平均厚度 (柱状图比例尺)
    color_hex     TEXT,                            -- 显示颜色
    description   TEXT
);

CREATE INDEX idx_csd_sort ON coal_seam_def(sort_order);

-- ─── 字典:coal_classification(GB/T 5751 煤类)─────────────────────────────
CREATE TABLE IF NOT EXISTS coal_classification (
    code        TEXT    PRIMARY KEY,               -- WY1/PM/SM/JM/FM/QM/CY/...
    name_cn     TEXT    NOT NULL,                  -- 无烟煤一号/贫煤/瘦煤/焦煤
    name_short  TEXT,                              -- 短名 "无烟/贫/瘦/焦"
    vdaf_min    DOUBLE PRECISION,
    vdaf_max    DOUBLE PRECISION,
    g_min       DOUBLE PRECISION,
    g_max       DOUBLE PRECISION,
    y_min       DOUBLE PRECISION,
    y_max       DOUBLE PRECISION,
    sort_order  BIGINT NOT NULL DEFAULT 0,
    description TEXT
);

CREATE INDEX idx_cc_sort ON coal_classification(sort_order);

-- ─── 字典:coal_grade_rule(灰分/硫分/发热量分级规则)─────────────────────────
CREATE TABLE IF NOT EXISTS coal_grade_rule (
    id          BIGSERIAL PRIMARY KEY,
    rule_type   TEXT    NOT NULL,                  -- ash/sulfur/qnet/volatile
    level_code  TEXT    NOT NULL,                  -- extra_low/low/medium/high/extra_high
    level_name  TEXT    NOT NULL,                  -- 显示名 "特低灰"/"低灰"/...
    value_min   DOUBLE PRECISION,
    value_max   DOUBLE PRECISION,
    color_hex   TEXT,
    sort_order  BIGINT NOT NULL DEFAULT 0
);

CREATE INDEX idx_cgr_type ON coal_grade_rule(rule_type, sort_order);

-- ─── 主表:borehole(钻孔基础)──────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS borehole (
    id                  BIGSERIAL PRIMARY KEY,
    hole_id             TEXT    NOT NULL UNIQUE,           -- 孔号 (1610/B2812/J702/ATB15-08)
    x                   DOUBLE PRECISION    NOT NULL,                  -- 纬距 (CGCS2000, m)
    y                   DOUBLE PRECISION    NOT NULL,                  -- 经距 (含 37 带号, m)
    z_collar            DOUBLE PRECISION,                              -- 孔口高程 (m, 黄海)
    depth_total         DOUBLE PRECISION,                              -- 总孔深 (m)
    terminate_horizon   TEXT,                              -- 终孔层位 (Ct3/Cb/O)
    drill_date          TEXT,                              -- 施工时间 (原格式 "1959.5" / "1982.6.27-7.5")
    drill_unit          TEXT,                              -- 施工单位
    drill_rating        TEXT,                              -- 钻探评级 甲/乙/丙
    log_rating          TEXT,                              -- 测井评级
    overall_rating      TEXT,                              -- 综合评级
    category            TEXT,                              -- 类别 (2007核实/2014补勘/生产)
    coord_filled        TEXT    NOT NULL DEFAULT '原始',   -- 原始/经距补/高程补/+
    coord_fill_basis    TEXT,                              -- IDW 邻孔依据
    source_page         BIGINT,
    remark              TEXT,
    created_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_bh_hole_id  ON borehole(hole_id);
CREATE INDEX idx_bh_xy       ON borehole(x, y);
CREATE INDEX idx_bh_category ON borehole(category);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_borehole_updated_at ON borehole;
CREATE TRIGGER trg_borehole_updated_at
BEFORE UPDATE ON borehole
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── 主表:borehole_seam_result(钻孔煤层成果, 附表2)───────────────────────
CREATE TABLE IF NOT EXISTS borehole_seam_result (
    id                          BIGSERIAL PRIMARY KEY,
    borehole_id                 BIGINT NOT NULL,
    seam_code                   TEXT    NOT NULL,
    -- 钻探
    drill_end_depth             DOUBLE PRECISION,                       -- 钻探-止煤深度 (m)
    drill_seam_thickness        DOUBLE PRECISION,                       -- 钻探-煤层厚度
    drill_structure             TEXT,                       -- 钻探-煤层结构
    drill_recovery_rate         DOUBLE PRECISION,                       -- 钻探-采取率 %
    drill_quality               TEXT,                       -- 钻探-质量评价 甲/乙/丙
    -- 测井
    log_end_depth               DOUBLE PRECISION,
    log_seam_thickness          DOUBLE PRECISION,
    log_structure               TEXT,
    log_quality                 TEXT,
    -- 综合采用
    overall_thickness           DOUBLE PRECISION,                       -- 综合-煤层厚度
    adopted_thickness           DOUBLE PRECISION,                       -- 综合-采用厚度 (合计/估算厚)
    weathered_coal_thickness    DOUBLE PRECISION,                       -- 风氧化煤厚
    parting_thickness           DOUBLE PRECISION,                       -- 夹石厚度
    -- 顶底板 (Type B)
    roof_lithology              TEXT,
    floor_lithology             TEXT,
    floor_elevation             DOUBLE PRECISION,
    overall_rating              TEXT,                       -- 综合级别 "钻甲"/"测乙"
    -- 状态
    status                      TEXT    NOT NULL DEFAULT '正常',
        -- 正常/未达/尖灭/全风化/陷落柱/空巷/未测/不取芯/废/合并/参考/风/风化/风氧化
    source_page                 BIGINT,
    source_layout               TEXT,                       -- A20/A19/B17/D6
    remark                      TEXT,
    created_at                  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(borehole_id, seam_code),
    FOREIGN KEY (borehole_id) REFERENCES borehole(id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (seam_code)   REFERENCES coal_seam_def(code) ON UPDATE CASCADE
);

CREATE INDEX idx_bsr_seam     ON borehole_seam_result(seam_code);
CREATE INDEX idx_bsr_status   ON borehole_seam_result(status);
CREATE INDEX idx_bsr_borehole ON borehole_seam_result(borehole_id);

DROP TRIGGER IF EXISTS trg_bsr_updated_at ON borehole_seam_result;
CREATE TRIGGER trg_bsr_updated_at
BEFORE UPDATE ON borehole_seam_result
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── 主表:coal_sample(煤芯煤样化验明细, 附表4)────────────────────────────
CREATE TABLE IF NOT EXISTS coal_sample (
    id                  BIGSERIAL PRIMARY KEY,
    borehole_id         BIGINT NOT NULL,
    seam_code           TEXT    NOT NULL,
    -- 采样位置
    depth_from          DOUBLE PRECISION,                              -- 采样起深 (m)
    depth_to            DOUBLE PRECISION,                              -- 采样止深 (m)
    sample_thickness    DOUBLE PRECISION,                              -- 采样厚度
    z_sample            DOUBLE PRECISION,                              -- 样品中心高程 (m) — 入库时算好
    -- 密度
    apparent_density    DOUBLE PRECISION,                              -- 视密度 t/m³
    true_density        DOUBLE PRECISION,                              -- 真密度 t/m³
    -- 工业分析 原煤
    mad_raw             DOUBLE PRECISION,                              -- M_ad %
    ad_raw              DOUBLE PRECISION,                              -- A_d  %
    vdaf_raw            DOUBLE PRECISION,                              -- V_daf %
    fcd_raw             DOUBLE PRECISION,                              -- FC_d %
    -- 工业分析 浮煤
    mad_clean           DOUBLE PRECISION,
    ad_clean            DOUBLE PRECISION,
    vdaf_clean          DOUBLE PRECISION,
    fcd_clean           DOUBLE PRECISION,
    -- 全硫
    std_raw             DOUBLE PRECISION,                              -- 原煤 S_t,d %
    std_clean           DOUBLE PRECISION,                              -- 浮煤 S_t,d %
    -- 发热量
    qgr_d               DOUBLE PRECISION,                              -- 干基弹筒 MJ/kg
    qnet_ad             DOUBLE PRECISION,                              -- 空干基低位 MJ/kg
    -- 胶质层
    plastic_x_mm        DOUBLE PRECISION,                              -- 胶质层 X mm
    plastic_y_mm        DOUBLE PRECISION,                              -- 胶质层 Y mm
    plastometric_curve  TEXT,                              -- 曲线形状/熔合状况
    -- 粘结 + 焦渣
    caking_g            DOUBLE PRECISION,                              -- 粘结指数 G
    char_residue_raw    BIGINT,                           -- 原煤焦渣 1-8
    char_residue_clean  BIGINT,                           -- 浮煤焦渣 1-8
    -- 洗选 + 煤类
    clean_coal_yield    DOUBLE PRECISION,                              -- 浮煤回收率 %
    coal_type           TEXT,                              -- GB/T 5751
    -- 元数据
    source_page         BIGINT,
    remark              TEXT,
    created_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(borehole_id, seam_code, depth_from),
    FOREIGN KEY (borehole_id) REFERENCES borehole(id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (seam_code)   REFERENCES coal_seam_def(code) ON UPDATE CASCADE,
    FOREIGN KEY (coal_type)   REFERENCES coal_classification(code) ON UPDATE CASCADE
);

CREATE INDEX idx_cs_borehole  ON coal_sample(borehole_id);
CREATE INDEX idx_cs_seam      ON coal_sample(seam_code);
CREATE INDEX idx_cs_type      ON coal_sample(coal_type);
CREATE INDEX idx_cs_z         ON coal_sample(z_sample);

DROP TRIGGER IF EXISTS trg_cs_updated_at ON coal_sample;
CREATE TRIGGER trg_cs_updated_at
BEFORE UPDATE ON coal_sample
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── 衍生表:coal_sample_summary(每孔每层化验平均)────────────────────────
CREATE TABLE IF NOT EXISTS coal_sample_summary (
    id                  BIGSERIAL PRIMARY KEY,
    borehole_id         BIGINT NOT NULL,
    seam_code           TEXT    NOT NULL,
    sample_count        BIGINT NOT NULL DEFAULT 0,
    avg_thickness       DOUBLE PRECISION,
    avg_mad_raw         DOUBLE PRECISION,
    avg_mad_clean       DOUBLE PRECISION,
    avg_ad_raw          DOUBLE PRECISION,
    avg_ad_clean        DOUBLE PRECISION,
    avg_vdaf_raw        DOUBLE PRECISION,
    avg_vdaf_clean      DOUBLE PRECISION,
    avg_fcd_raw         DOUBLE PRECISION,
    avg_fcd_clean       DOUBLE PRECISION,
    avg_std_raw         DOUBLE PRECISION,
    avg_std_clean       DOUBLE PRECISION,
    avg_qgr_d           DOUBLE PRECISION,
    avg_qnet_ad         DOUBLE PRECISION,
    avg_caking_g        DOUBLE PRECISION,
    avg_plastic_y       DOUBLE PRECISION,
    avg_clean_yield     DOUBLE PRECISION,
    dominant_coal_type  TEXT,
    is_from_source      BIGINT NOT NULL DEFAULT 0,        -- 1=PDF 原"平均"行, 0=程序聚合
    last_built_at       TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(borehole_id, seam_code),
    FOREIGN KEY (borehole_id) REFERENCES borehole(id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (seam_code)   REFERENCES coal_seam_def(code) ON UPDATE CASCADE
);

CREATE INDEX idx_css_seam ON coal_sample_summary(seam_code);

-- ─── 主表:coal_observation_point(见煤点, 附表3)──────────────────────────
CREATE TABLE IF NOT EXISTS coal_observation_point (
    id                  BIGSERIAL PRIMARY KEY,
    point_id            TEXT    NOT NULL,
    seam_code           TEXT    NOT NULL,
    x                   DOUBLE PRECISION    NOT NULL,
    y                   DOUBLE PRECISION    NOT NULL,
    original_y_format   BIGINT NOT NULL DEFAULT 8,        -- 8 含带号, 6 无带号(已补 +37000000)
    seam_thickness      DOUBLE PRECISION,
    coal_structure      TEXT,
    estimated_thickness DOUBLE PRECISION,
    floor_elevation     DOUBLE PRECISION,
    annual_report       TEXT,                              -- "2014年年报"/"2023年年报"
    source_page         BIGINT,
    remark              TEXT,
    created_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(point_id, seam_code),
    FOREIGN KEY (seam_code) REFERENCES coal_seam_def(code) ON UPDATE CASCADE
);

CREATE INDEX idx_cop_seam ON coal_observation_point(seam_code);
CREATE INDEX idx_cop_xy   ON coal_observation_point(x, y);

DROP TRIGGER IF EXISTS trg_cop_updated_at ON coal_observation_point;
CREATE TRIGGER trg_cop_updated_at
BEFORE UPDATE ON coal_observation_point
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── 主表:borehole_lithology_segment(钻孔岩性分层, 柱状图核心)────────────
CREATE TABLE IF NOT EXISTS borehole_lithology_segment (
    id              BIGSERIAL PRIMARY KEY,
    borehole_id     BIGINT NOT NULL,
    depth_from      DOUBLE PRECISION    NOT NULL,
    depth_to        DOUBLE PRECISION    NOT NULL,
    lithology_code  TEXT    NOT NULL,                      -- sand/silt/mud/coal/limestone/...
    lithology_name  TEXT,                                  -- 粗砂岩/细砂岩/泥岩/煤/...
    color_hex       TEXT,
    pattern         TEXT,                                  -- SVG pattern key
    description     TEXT,
    source          TEXT    NOT NULL DEFAULT '推断',       -- '附表2推断'/'外部录入'/'人工'
    sort_order      BIGINT NOT NULL DEFAULT 0,
    FOREIGN KEY (borehole_id) REFERENCES borehole(id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_bls_bid_depth ON borehole_lithology_segment(borehole_id, depth_from);

-- ─── 衍生表:coal_audit_finding(煤质数据审核发现)────────────────────────
CREATE TABLE IF NOT EXISTS coal_audit_finding (
    id              BIGSERIAL PRIMARY KEY,
    audit_run_id    TEXT,                                  -- 一次审核的批次 ID
    sample_id       BIGINT,
    seam_result_id  BIGINT,
    severity        TEXT    NOT NULL,                      -- 严重/警告/信息
    category        TEXT    NOT NULL,                      -- 工分自洽/煤类反推/同层离群/...
    message         TEXT    NOT NULL,
    suggestion      TEXT,
    status          TEXT    NOT NULL DEFAULT 'open',       -- open/processed/ignored
    processed_at    TEXT,
    processed_by    TEXT,
    process_note    TEXT,
    created_at      TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (sample_id)      REFERENCES coal_sample(id) ON DELETE SET NULL ON UPDATE CASCADE,
    FOREIGN KEY (seam_result_id) REFERENCES borehole_seam_result(id) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX idx_caf_status ON coal_audit_finding(status);
CREATE INDEX idx_caf_run    ON coal_audit_finding(audit_run_id);
CREATE INDEX idx_caf_sample ON coal_audit_finding(sample_id);


-- =============================================================================
-- 视图 (便于应用层查询)
-- =============================================================================

-- 视图: 化验段 3D 位置 + 关键指标（空间分析直接读）
DROP VIEW IF EXISTS v_coal_sample_3d;
CREATE VIEW v_coal_sample_3d AS
SELECT
    cs.id,
    cs.borehole_id,
    bh.hole_id,
    cs.seam_code,
    bh.x  AS x,
    bh.y  AS y,
    cs.z_sample,
    cs.depth_from,
    cs.depth_to,
    cs.sample_thickness,
    cs.ad_raw, cs.ad_clean,
    cs.vdaf_raw, cs.vdaf_clean,
    cs.std_raw, cs.std_clean,
    cs.qgr_d, cs.qnet_ad,
    cs.caking_g, cs.plastic_y_mm,
    cs.coal_type,
    cc.name_cn AS coal_type_name
FROM coal_sample cs
JOIN borehole bh ON cs.borehole_id = bh.id
LEFT JOIN coal_classification cc ON cs.coal_type = cc.code;

-- 视图: 钻孔柱状图所需数据
DROP VIEW IF EXISTS v_borehole_column;
CREATE VIEW v_borehole_column AS
SELECT
    bh.id AS borehole_id,
    bh.hole_id,
    bh.x, bh.y, bh.z_collar, bh.depth_total,
    bh.terminate_horizon,
    bsr.seam_code,
    csd.name      AS seam_name,
    csd.color_hex AS seam_color,
    csd.sort_order AS seam_order,
    bsr.log_end_depth,
    bsr.overall_thickness,
    bsr.adopted_thickness,
    -- 段顶 Z = z_collar - (log_end_depth - thickness)
    bh.z_collar - (COALESCE(bsr.log_end_depth, 0) - COALESCE(bsr.overall_thickness, 0)) AS seam_top_z,
    -- 段底 Z 优先 floor_elevation, 退化用 z_collar - log_end_depth
    COALESCE(bsr.floor_elevation, bh.z_collar - bsr.log_end_depth) AS seam_floor_z,
    bsr.roof_lithology,
    bsr.floor_lithology,
    bsr.overall_rating,
    bsr.status
FROM borehole bh
LEFT JOIN borehole_seam_result bsr ON bsr.borehole_id = bh.id
LEFT JOIN coal_seam_def csd ON bsr.seam_code = csd.code;

-- 视图: 单孔段序列 (柱状图按深度排好, union 岩性分层 + 煤层段)
DROP VIEW IF EXISTS v_borehole_segments;
CREATE VIEW v_borehole_segments AS
SELECT
    borehole_id,
    depth_from,
    depth_to,
    lithology_name AS layer_name,
    color_hex,
    '岩性' AS layer_type
FROM borehole_lithology_segment
UNION ALL
SELECT
    bsr.borehole_id,
    bsr.log_end_depth - COALESCE(bsr.overall_thickness, 0) AS depth_from,
    bsr.log_end_depth AS depth_to,
    csd.name AS layer_name,
    csd.color_hex,
    '煤层' AS layer_type
FROM borehole_seam_result bsr
JOIN coal_seam_def csd ON bsr.seam_code = csd.code
WHERE bsr.status NOT IN ('未达', '尖灭', '空巷');


-- =============================================================================
-- 字典 seed (coal_seam_def, coal_classification, coal_grade_rule)
-- =============================================================================

-- 煤层定义 (按沉积深度排序，浅 → 深)
INSERT INTO coal_seam_def (code, name, sort_order, color_hex, description) VALUES
    ('4',        '4 号煤层',         10, '#D4A017', '主采煤层'),
    ('4-1',      '4-1 号煤层',       11, '#E5B622', '主采煤层（4 号分层）'),
    ('4-2',      '4-2 号煤层',       12, '#C99518', '4 号分层'),
    ('4（4-1）', '4(4-1) 合并煤层',  13, '#D4A017', '4 与 4-1 合并'),
    ('7-1',      '7-1 号煤层',       20, '#8B6914', ''),
    ('9',        '9 号煤层',         30, '#4A7C2E', '主采煤层'),
    ('11',       '11 号煤层',        40, '#2C5F8D', '主采煤层') ON DUPLICATE KEY UPDATE name = name;

-- GB/T 5751 煤类 (节选 14 种)
INSERT INTO coal_classification (code, name_cn, name_short, vdaf_min, vdaf_max, g_min, g_max, y_min, y_max, sort_order, description) VALUES
    ('WY1',   '无烟煤一号',  '无烟', 0,    3.5,  NULL, NULL, NULL, NULL, 10, ''),
    ('WY2',   '无烟煤二号',  '无烟', 3.5,  6.5,  NULL, NULL, NULL, NULL, 11, ''),
    ('WY3',   '无烟煤三号',  '无烟', 6.5,  10,   NULL, NULL, NULL, NULL, 12, ''),
    ('PM',    '贫煤',        '贫',   10,   20,   0,    5,    NULL, NULL, 20, ''),
    ('PS',    '贫瘦煤',      '贫瘦', 10,   20,   5,    20,   NULL, NULL, 21, ''),
    ('SM',    '瘦煤',        '瘦',   10,   20,   20,   65,   NULL, NULL, 22, ''),
    ('JM',    '焦煤',        '焦',   18,   28,   50,   65,   7,    NULL, 30, ''),
    ('FM',    '肥煤',        '肥',   18,   37,   85,   NULL, 25,   NULL, 31, ''),
    ('1/3JM', '1/3 焦煤',    '1/3焦',28,   37,   65,   NULL, 5,    25,   32, ''),
    ('QF',    '气肥煤',      '气肥', 37,   NULL, 85,   NULL, 25,   NULL, 33, ''),
    ('QM',    '气煤',        '气',   28,   37,   35,   65,   NULL, NULL, 40, ''),
    ('1/2ZN', '1/2 中粘煤',  '1/2中粘', 28, NULL, 35,  65,   NULL, NULL, 41, ''),
    ('RN',    '弱粘煤',      '弱粘', 20,   37,   5,    35,   NULL, NULL, 50, ''),
    ('BN',    '不粘煤',      '不粘', 20,   37,   0,    5,    NULL, NULL, 51, ''),
    ('CY',    '长焰煤',      '长焰', 37,   NULL, 0,    35,   NULL, NULL, 60, ''),
    ('HM',    '褐煤',        '褐',   37,   NULL, NULL, NULL, NULL, NULL, 70, '另需透光率指标') ON DUPLICATE KEY UPDATE name_cn = name_cn;

-- 分级规则 (灰分 / 硫分 / 发热量, 颜色用于空间分布着色)
INSERT INTO coal_grade_rule (rule_type, level_code, level_name, value_min, value_max, color_hex, sort_order) VALUES
    -- 灰分 (GB/T 15224.1)
    ('ash',    'extra_low',  '特低灰', NULL, 10,   '#1B5E20', 10),
    ('ash',    'low',        '低灰',   10,   20,   '#558B2F', 20),
    ('ash',    'medium',     '中灰',   20,   30,   '#FBC02D', 30),
    ('ash',    'high',       '中高灰', 30,   40,   '#F57C00', 40),
    ('ash',    'extra_high', '高灰',   40,   NULL, '#C62828', 50),
    -- 硫分 (GB/T 15224.2)
    ('sulfur', 'extra_low',  '特低硫', NULL, 0.5,  '#1B5E20', 10),
    ('sulfur', 'low',        '低硫',   0.5,  1.0,  '#558B2F', 20),
    ('sulfur', 'medium',     '中硫',   1.0,  2.0,  '#FBC02D', 30),
    ('sulfur', 'high',       '中高硫', 2.0,  3.0,  '#F57C00', 40),
    ('sulfur', 'extra_high', '高硫',   3.0,  NULL, '#C62828', 50),
    -- 发热量 (GB/T 15224.3, MJ/kg)
    ('qnet',   'extra_low',  '低热值', NULL, 17,   '#C62828', 10),
    ('qnet',   'low',        '中低',   17,   21,   '#F57C00', 20),
    ('qnet',   'medium',     '中',     21,   24,   '#FBC02D', 30),
    ('qnet',   'high',       '中高',   24,   27,   '#558B2F', 40),
    ('qnet',   'extra_high', '高热值', 27,   NULL, '#1B5E20', 50) ON DUPLICATE KEY UPDATE rule_type = rule_type;
