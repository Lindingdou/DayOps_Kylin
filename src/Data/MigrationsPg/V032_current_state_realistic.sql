-- 由 build/sqlite2pg.py 从 V032_current_state_realistic.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

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
    id          SERIAL PRIMARY KEY,
    label       TEXT    NOT NULL UNIQUE,       -- 系统唯一时间标签 CSR-yyyyMMdd-HHmmss[-nn]
    name        TEXT    NOT NULL DEFAULT '',   -- 友好名(可改)
    source      TEXT,                          -- 手工录入 / Excel导入
    remark      TEXT,
    created_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_csbatch_label ON current_state_batch(label);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_csbatch_updated_at ON current_state_batch;
CREATE TRIGGER trg_csbatch_updated_at
BEFORE UPDATE ON current_state_batch
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── 主表: current_state_point (现状高程点) ───────────────────────────────
CREATE TABLE IF NOT EXISTS current_state_point (
    id          SERIAL PRIMARY KEY,
    batch_id    INTEGER NOT NULL,
    x           DOUBLE PRECISION    NOT NULL,              -- 经距 (m, 与工程同坐标系)
    y           DOUBLE PRECISION    NOT NULL,              -- 纬距 (m)
    z           DOUBLE PRECISION    NOT NULL,              -- 现状高程 (m, 黄海)
    remark      TEXT,
    created_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_cspoint_batch ON current_state_point(batch_id);

DROP TRIGGER IF EXISTS trg_cspoint_updated_at ON current_state_point;
CREATE TRIGGER trg_cspoint_updated_at
BEFORE UPDATE ON current_state_point
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();
