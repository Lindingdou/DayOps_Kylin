-- 由 build/sqlite2dm.py 从 V008_template_library.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V008: 参数模板库
--
-- 2 张表:
--   process_template       模板主表(硬岩区/软岩区/煤层 等场景预设)
--   template_param_value   模板下的具体参数值
-- =============================================================================

CREATE TABLE process_template (
    template_id          INT IDENTITY(1,1) PRIMARY KEY,
    code                 VARCHAR(255) NOT NULL UNIQUE,           -- 'rh_standard_v1.2'
    name                 VARCHAR(255) NOT NULL,                  -- 显示名 '硬岩区标准 v1.2'
    description          VARCHAR(2000),
    applicable_material  VARCHAR(255),                            -- 'rh' / 'c4' / 'c9' / 'coal' / NULL=通用
    applicable_hardness  VARCHAR(255),                            -- 'hard' / 'medium' / 'soft' / NULL
    version              VARCHAR(255) NOT NULL DEFAULT 'v1.0',
    is_current           INT NOT NULL DEFAULT 1,      -- 是否为现行版本
    "status"               VARCHAR(255) NOT NULL DEFAULT 'active' CHECK ("status" IN ('active','archived','draft')),
    created_by           VARCHAR(255),
    created_at           VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at           VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    notes                VARCHAR(2000)
);

CREATE INDEX idx_template_material ON process_template(applicable_material);
CREATE INDEX idx_template_status   ON process_template("status");

CREATE TRIGGER trg_process_template_updated_at
BEFORE UPDATE ON process_template
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;

CREATE TABLE template_param_value (
    id                    INT IDENTITY(1,1) PRIMARY KEY,
    template_id           INT NOT NULL,
    param_id              INT NOT NULL,
    recommended_value     DOUBLE,                            -- 本模板推荐值
    min_value             DOUBLE,                            -- 本模板下限(可覆盖参数定义的 standard_min)
    max_value             DOUBLE,                            -- 本模板上限
    text_value            VARCHAR(255),                            -- 文本/枚举型参数的值
    notes                 VARCHAR(2000),
    UNIQUE (template_id, param_id),
    FOREIGN KEY (template_id) REFERENCES process_template(template_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (param_id)    REFERENCES parameter_definition(param_id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX idx_tpv_template ON template_param_value(template_id);
CREATE INDEX idx_tpv_param    ON template_param_value(param_id);
