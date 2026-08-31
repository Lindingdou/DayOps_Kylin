-- =============================================================================
-- pmgeo.db 现状写实(现状地表/台阶高程点)
-- 版本: V032
-- 说明: 「补勘钻孔写实」的姊妹功能 —— 记录现状面/点(x,y,z),按「批次」(系统唯一时间标签)组织,
--       供更新地质模型现状面。数据独立成套(current_state_batch + current_state_point)。
-- 备注: created_at/updated_at 由 DB 默认值 + 触发器维护,实体不映射这两列。
--       batch_id 引用完整性由服务层保证(删批次连带删点)。
-- =============================================================================

-- ─── 主表: current_state_batch (现状写实批次) ─────────────────────────────
CREATE TABLE IF NOT EXISTS current_state_batch (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    label       TEXT    NOT NULL UNIQUE,       -- 系统唯一时间标签 CSR-yyyyMMdd-HHmmss[-nn]
    name        TEXT    NOT NULL DEFAULT '',   -- 友好名(可改)
    source      TEXT,                          -- 手工录入 / Excel导入
    remark      TEXT,
    created_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_csbatch_label ON current_state_batch(label);

CREATE TRIGGER IF NOT EXISTS trg_csbatch_updated_at
AFTER UPDATE ON current_state_batch
FOR EACH ROW
BEGIN
    UPDATE current_state_batch SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;

-- ─── 主表: current_state_point (现状高程点) ───────────────────────────────
CREATE TABLE IF NOT EXISTS current_state_point (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    batch_id    INTEGER NOT NULL,
    x           REAL    NOT NULL,              -- 经距 (m, 与工程同坐标系)
    y           REAL    NOT NULL,              -- 纬距 (m)
    z           REAL    NOT NULL,              -- 现状高程 (m, 黄海)
    remark      TEXT,
    created_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_cspoint_batch ON current_state_point(batch_id);

CREATE TRIGGER IF NOT EXISTS trg_cspoint_updated_at
AFTER UPDATE ON current_state_point
FOR EACH ROW
BEGIN
    UPDATE current_state_point SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;
