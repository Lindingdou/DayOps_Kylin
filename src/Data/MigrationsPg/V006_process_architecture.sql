-- 由 build/sqlite2pg.py 从 V006_process_architecture.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V006: 工艺架构(工艺参数管理 4 按钮的第 1 层 — 字典/Schema)
--
-- 本迁移建立"工艺参数管理"的元数据骨架,后续 V007 / V008 在此基础上
-- 加模板、平盘绑定、实测验收。
--
-- 4 张表:
--   process_system          工艺系统(穿爆/采装/运输/排土/边坡/经济/安全/辅助)
--   process_phase           工艺环节(钻孔/装药/起爆/剥离/采煤/...)
--   parameter_definition    参数定义(孔径/孔深/单耗/台阶高度/...)
--   equipment_constraint    参数→设备能力约束(决定可用设备清单)
-- =============================================================================

-- ─── ① process_system(工艺系统字典)───────────────────────────────────────
CREATE TABLE IF NOT EXISTS process_system (
    system_id        SERIAL PRIMARY KEY,
    code             TEXT NOT NULL UNIQUE,        -- 系统编码 'blasting' / 'mining' / ...
    name             TEXT NOT NULL,                -- 显示名 穿爆系统 / 采装系统 / ...
    category         TEXT,                          -- 主类:drilling/mining/transport/dumping/slope/safety/aux/economic
    description      TEXT,
    display_order    INTEGER NOT NULL DEFAULT 0,    -- UI 排序
    is_active        INTEGER NOT NULL DEFAULT 1,
    created_at       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_process_system_updated_at ON process_system;
CREATE TRIGGER trg_process_system_updated_at
BEFORE UPDATE ON process_system
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── ② process_phase(工艺环节)─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS process_phase (
    phase_id                       SERIAL PRIMARY KEY,
    system_id                      INTEGER NOT NULL,
    code                           TEXT NOT NULL,    -- 'drilling' / 'charging' / 'blasting_exec' / ...
    name                           TEXT NOT NULL,    -- 显示名 钻孔 / 装药 / 起爆 / ...
    sequence_order                 INTEGER NOT NULL DEFAULT 0,   -- 系统内环节先后顺序
    typical_equipment_category     TEXT,             -- 该环节典型适用设备类别 Drill/Shovel/Truck/Dozer/...
    description                    TEXT,
    is_active                      INTEGER NOT NULL DEFAULT 1,
    created_at                     TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                     TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (system_id, code),
    FOREIGN KEY (system_id) REFERENCES process_system(system_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_phase_system ON process_phase(system_id);

DROP TRIGGER IF EXISTS trg_process_phase_updated_at ON process_phase;
CREATE TRIGGER trg_process_phase_updated_at
BEFORE UPDATE ON process_phase
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── ③ parameter_definition(参数定义)──────────────────────────────────────
CREATE TABLE IF NOT EXISTS parameter_definition (
    param_id             SERIAL PRIMARY KEY,
    phase_id             INTEGER NOT NULL,
    code                 TEXT NOT NULL UNIQUE,     -- 'hole_diameter' / 'hole_depth' / ...(全表唯一,供公式引用)
    name                 TEXT NOT NULL,             -- 显示名 孔径 / 孔深 / ...
    unit                 TEXT,                       -- 单位 mm / m / kg / kg/m³ / °  / ...
    value_type           TEXT NOT NULL DEFAULT 'numeric' CHECK (value_type IN ('numeric','text','boolean','enum')),
    standard_min         DOUBLE PRECISION,                       -- 标准范围下限
    standard_max         DOUBLE PRECISION,                       -- 标准范围上限
    standard_default     DOUBLE PRECISION,                       -- 默认推荐值
    alarm_low            DOUBLE PRECISION,                       -- 报警下限(超低则红)
    alarm_high           DOUBLE PRECISION,                       -- 报警上限
    is_required          INTEGER NOT NULL DEFAULT 0, -- 是否必填
    calc_formula         TEXT,                       -- 派生公式(可空,如 "explosive_kg / blast_volume_m3")
    source_table         TEXT,                       -- 可派生自哪张事实表(可空)
    source_column        TEXT,                       -- 派生字段
    description          TEXT,
    display_order        INTEGER NOT NULL DEFAULT 0,
    is_active            INTEGER NOT NULL DEFAULT 1,
    created_at           TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at           TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (phase_id) REFERENCES process_phase(phase_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_param_phase ON parameter_definition(phase_id);
CREATE INDEX idx_param_code  ON parameter_definition(code);

DROP TRIGGER IF EXISTS trg_parameter_definition_updated_at ON parameter_definition;
CREATE TRIGGER trg_parameter_definition_updated_at
BEFORE UPDATE ON parameter_definition
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── ④ equipment_constraint(参数 → 设备能力约束)──────────────────────────
CREATE TABLE IF NOT EXISTS equipment_constraint (
    id                SERIAL PRIMARY KEY,
    param_id          INTEGER NOT NULL,
    equipment_model   TEXT NOT NULL,                 -- equipment_model.model FK
    constraint_type   TEXT NOT NULL CHECK (constraint_type IN ('min','max','range','equals','contains')),
    limit_value       DOUBLE PRECISION,                          -- 单值约束
    limit_min         DOUBLE PRECISION,                          -- 范围约束下限
    limit_max         DOUBLE PRECISION,                          -- 范围约束上限
    text_value        TEXT,                          -- 文本/枚举型约束
    consequence       TEXT NOT NULL DEFAULT 'hard' CHECK (consequence IN ('hard','soft','informational')),
                                                    -- hard: 违反→不可用
                                                    -- soft: 违反→降效
                                                    -- informational: 仅提示
    priority          INTEGER NOT NULL DEFAULT 3,    -- 1-5,5 最严
    description       TEXT,
    is_active         INTEGER NOT NULL DEFAULT 1,
    created_at        TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (param_id)        REFERENCES parameter_definition(param_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (equipment_model) REFERENCES equipment_model(model) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_constraint_param ON equipment_constraint(param_id);
CREATE INDEX idx_constraint_model ON equipment_constraint(equipment_model);
