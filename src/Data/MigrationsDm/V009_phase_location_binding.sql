-- 由 build/sqlite2dm.py 从 V009_phase_location_binding.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V009: 工艺环节 ↔ 平盘 ↔ 模板 三方绑定
--
-- 这是"平盘工艺地图"窗体的核心数据源。
-- 一条绑定记录回答:"1195 平盘的钻孔环节采用哪个模板?"
-- =============================================================================

CREATE TABLE phase_location_binding (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    phase_id            INT NOT NULL,
    location_code       VARCHAR(255) NOT NULL,
    bound_template_id   INT,                      -- 本平盘该环节套用的模板(NULL=用参数默认值)
    started_at          VARCHAR(255) NOT NULL DEFAULT (CURRENT_DATE),
    ended_at            VARCHAR(255),                         -- NULL = 现行
    is_active           INT NOT NULL DEFAULT 1,
    notes               VARCHAR(2000),
    created_at          VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (phase_id, location_code, started_at),
    FOREIGN KEY (phase_id)          REFERENCES process_phase(phase_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (location_code)     REFERENCES mine_location(location_code) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (bound_template_id) REFERENCES process_template(template_id) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX idx_binding_phase    ON phase_location_binding(phase_id);
CREATE INDEX idx_binding_location ON phase_location_binding(location_code);
CREATE INDEX idx_binding_active   ON phase_location_binding(is_active);

CREATE TRIGGER trg_phase_location_binding_updated_at
BEFORE UPDATE ON phase_location_binding
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;
