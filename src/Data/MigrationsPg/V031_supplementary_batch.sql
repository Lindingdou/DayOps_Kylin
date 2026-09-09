-- 由 build/sqlite2pg.py 从 V031_supplementary_batch.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- pmgeo.db 补勘写实批次(系统唯一时间标签)
-- 版本: V031
-- 说明: 补勘写实数据按「批次」组织,每批一个系统唯一时间标签(SBW-yyyyMMdd-HHmmss[-nn])。
--       supplementary_borehole 加 batch_id 归批;已有未分批孔归入一个「历史写实」批次。
--       label 唯一 = 系统唯一;name 可改名;source 记录来源(手工/Excel/迁移)。
-- 备注: SQLite ALTER 不支持加外键,batch_id 引用完整性由服务层保证(删批次连带删孔+层位)。
-- =============================================================================

-- ─── 主表: supplementary_batch (补勘写实批次) ──────────────────────────────
CREATE TABLE IF NOT EXISTS supplementary_batch (
    id          BIGSERIAL PRIMARY KEY,
    label       TEXT    NOT NULL UNIQUE,       -- 系统唯一时间标签 SBW-yyyyMMdd-HHmmss[-nn]
    name        TEXT    NOT NULL DEFAULT '',   -- 友好名(可改)
    source      TEXT,                          -- 手工录入 / Excel导入 / 迁移归集
    remark      TEXT,
    created_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_supbatch_label ON supplementary_batch(label);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_supbatch_updated_at ON supplementary_batch;
CREATE TRIGGER trg_supbatch_updated_at
BEFORE UPDATE ON supplementary_batch
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- ─── 补勘孔归批: supplementary_borehole 加 batch_id ────────────────────────
ALTER TABLE supplementary_borehole ADD COLUMN batch_id BIGINT;
CREATE INDEX idx_supbh_batch ON supplementary_borehole(batch_id);

-- ─── 已有未分批补勘孔 → 归入「历史写实」批次(无未分批孔则不建) ────────────
INSERT INTO supplementary_batch(label, name, source)
SELECT 'SBW-LEGACY', '历史写实(未分批)', '迁移归集'
WHERE EXISTS (SELECT 1 FROM supplementary_borehole WHERE batch_id IS NULL);

UPDATE supplementary_borehole
   SET batch_id = (SELECT id FROM supplementary_batch WHERE label = 'SBW-LEGACY')
 WHERE batch_id IS NULL;
