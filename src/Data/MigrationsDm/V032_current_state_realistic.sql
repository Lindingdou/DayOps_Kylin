-- 由 build/sqlite2dm.py 从 V032_current_state_realistic.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- pmgeo.db 现状写实(现状地表/台阶高程点)
-- 版本: V032
-- 说明: 「补勘钻孔写实」的姊妹功能 —— 记录现状面/点(x,y,z),按「批次」(系统唯一时间标签)组织,
--       供更新地质模型现状面。数据独立成套(current_state_batch + current_state_point)。
-- 备注: created_at/updated_at 由 DB 默认值 + 触发器维护,实体不映射这两列。
--       batch_id 引用完整性由服务层保证(删批次连带删点)。
-- =============================================================================

-- ─── 主表: current_state_batch (现状写实批次) ─────────────────────────────
CREATE TABLE current_state_batch (
    id          INT IDENTITY(1,1) PRIMARY KEY,
    label       VARCHAR(255)    NOT NULL UNIQUE,       -- 系统唯一时间标签 CSR-yyyyMMdd-HHmmss[-nn]
    name        VARCHAR(255)    NOT NULL DEFAULT '',   -- 友好名(可改)
    "source"      VARCHAR(255),                          -- 手工录入 / Excel导入
    remark      VARCHAR(2000),
    created_at  VARCHAR(255)    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  VARCHAR(255)    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_csbatch_label ON current_state_batch(label);

CREATE TRIGGER trg_csbatch_updated_at
BEFORE UPDATE ON current_state_batch
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;

-- ─── 主表: current_state_point (现状高程点) ───────────────────────────────
CREATE TABLE current_state_point (
    id          INT IDENTITY(1,1) PRIMARY KEY,
    batch_id    INT NOT NULL,
    x           DOUBLE    NOT NULL,              -- 经距 (m, 与工程同坐标系)
    y           DOUBLE    NOT NULL,              -- 纬距 (m)
    z           DOUBLE    NOT NULL,              -- 现状高程 (m, 黄海)
    remark      VARCHAR(2000),
    created_at  VARCHAR(255)    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  VARCHAR(255)    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_cspoint_batch ON current_state_point(batch_id);

CREATE TRIGGER trg_cspoint_updated_at
BEFORE UPDATE ON current_state_point
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;
