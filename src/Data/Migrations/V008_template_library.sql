-- =============================================================================
-- V008: 参数模板库
--
-- 2 张表:
--   process_template       模板主表(硬岩区/软岩区/煤层 等场景预设)
--   template_param_value   模板下的具体参数值
-- =============================================================================

CREATE TABLE IF NOT EXISTS process_template (
    template_id          INTEGER PRIMARY KEY AUTOINCREMENT,
    code                 TEXT NOT NULL UNIQUE,           -- 'rh_standard_v1.2'
    name                 TEXT NOT NULL,                  -- 显示名 '硬岩区标准 v1.2'
    description          TEXT,
    applicable_material  TEXT,                            -- 'rh' / 'c4' / 'c9' / 'coal' / NULL=通用
    applicable_hardness  TEXT,                            -- 'hard' / 'medium' / 'soft' / NULL
    version              TEXT NOT NULL DEFAULT 'v1.0',
    is_current           INTEGER NOT NULL DEFAULT 1,      -- 是否为现行版本
    status               TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active','archived','draft')),
    created_by           TEXT,
    created_at           TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at           TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    notes                TEXT
);

CREATE INDEX IF NOT EXISTS idx_template_material ON process_template(applicable_material);
CREATE INDEX IF NOT EXISTS idx_template_status   ON process_template(status);

CREATE TRIGGER IF NOT EXISTS trg_process_template_updated_at
AFTER UPDATE ON process_template FOR EACH ROW BEGIN
    UPDATE process_template SET updated_at = CURRENT_TIMESTAMP WHERE template_id = NEW.template_id;
END;

CREATE TABLE IF NOT EXISTS template_param_value (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    template_id           INTEGER NOT NULL,
    param_id              INTEGER NOT NULL,
    recommended_value     REAL,                            -- 本模板推荐值
    min_value             REAL,                            -- 本模板下限(可覆盖参数定义的 standard_min)
    max_value             REAL,                            -- 本模板上限
    text_value            TEXT,                            -- 文本/枚举型参数的值
    notes                 TEXT,
    UNIQUE (template_id, param_id),
    FOREIGN KEY (template_id) REFERENCES process_template(template_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (param_id)    REFERENCES parameter_definition(param_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_tpv_template ON template_param_value(template_id);
CREATE INDEX IF NOT EXISTS idx_tpv_param    ON template_param_value(param_id);
