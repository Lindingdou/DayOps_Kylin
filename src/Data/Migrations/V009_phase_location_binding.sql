-- =============================================================================
-- V009: 工艺环节 ↔ 平盘 ↔ 模板 三方绑定
--
-- 这是"平盘工艺地图"窗体的核心数据源。
-- 一条绑定记录回答:"1195 平盘的钻孔环节采用哪个模板?"
-- =============================================================================

CREATE TABLE IF NOT EXISTS phase_location_binding (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    phase_id            INTEGER NOT NULL,
    location_code       TEXT NOT NULL,
    bound_template_id   INTEGER,                      -- 本平盘该环节套用的模板(NULL=用参数默认值)
    started_at          TEXT NOT NULL DEFAULT (date('now')),
    ended_at            TEXT,                         -- NULL = 现行
    is_active           INTEGER NOT NULL DEFAULT 1,
    notes               TEXT,
    created_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE (phase_id, location_code, started_at),
    FOREIGN KEY (phase_id)          REFERENCES process_phase(phase_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (location_code)     REFERENCES mine_location(location_code) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (bound_template_id) REFERENCES process_template(template_id) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_binding_phase    ON phase_location_binding(phase_id);
CREATE INDEX IF NOT EXISTS idx_binding_location ON phase_location_binding(location_code);
CREATE INDEX IF NOT EXISTS idx_binding_active   ON phase_location_binding(is_active);

CREATE TRIGGER IF NOT EXISTS trg_phase_location_binding_updated_at
AFTER UPDATE ON phase_location_binding FOR EACH ROW BEGIN
    UPDATE phase_location_binding SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;
