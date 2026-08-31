-- =============================================================================
-- pmgeo.db 补勘钻孔写实 (三维地质建模 · 模型更新)
-- 版本: V028
-- 说明: 补勘钻孔写实数据独立成套 —— 补勘孔 + 逐煤层顶/底板高程(顶、底板均显式存)。
--       与原始 borehole / borehole_seam_result 隔离;供「补勘钻孔写实」录入 + 自动重建煤层三维面。
-- 编号: V027 已被 V027_purge_params_absent_from_chushe.sql 占用,本迁移取 V028。
-- 备注: created_at/updated_at 由 DB 默认值 + 触发器维护,实体不映射这两列(INSERT 自动省略→默认填充)。
-- =============================================================================

-- ─── 主表: supplementary_borehole (补勘钻孔) ────────────────────────────────
CREATE TABLE IF NOT EXISTS supplementary_borehole (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    hole_id     TEXT    NOT NULL UNIQUE,          -- 孔号 (业务唯一键)
    x           REAL    NOT NULL,                 -- 经距 (与 borehole.x 同坐标系, 已去带号)
    y           REAL    NOT NULL,                 -- 纬距
    z_collar    REAL,                             -- 孔口高程 (m, 黄海)
    remark      TEXT,                             -- 备注
    created_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_supbh_hole_id ON supplementary_borehole(hole_id);
CREATE INDEX IF NOT EXISTS idx_supbh_xy      ON supplementary_borehole(x, y);

CREATE TRIGGER IF NOT EXISTS trg_supbh_updated_at
AFTER UPDATE ON supplementary_borehole
FOR EACH ROW
BEGIN
    UPDATE supplementary_borehole SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;

-- ─── 主表: supplementary_seam_horizon (补勘煤层顶/底板高程写实) ──────────────
-- 一孔一层一条;顶板 / 底板高程都直接存(写实),厚度 = 顶 - 底 由上层派生,不落库。
-- 不加 seam_code→coal_seam_def 外键:写实允许用户自定义煤层结构,保持灵活。
CREATE TABLE IF NOT EXISTS supplementary_seam_horizon (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    sup_borehole_id   INTEGER NOT NULL,
    seam_code         TEXT    NOT NULL,           -- 煤层编号 (4/7-1/9/11/...)
    roof_elevation    REAL,                       -- 顶板高程 (m, 显式存)
    floor_elevation   REAL,                       -- 底板高程 (m, 显式存)
    sort_order        INTEGER NOT NULL DEFAULT 0, -- 层序 (浅→深, 决定上下关系)
    remark            TEXT,
    created_at        TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(sup_borehole_id, seam_code),
    FOREIGN KEY (sup_borehole_id) REFERENCES supplementary_borehole(id) ON DELETE CASCADE ON UPDATE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_supsh_borehole ON supplementary_seam_horizon(sup_borehole_id);
CREATE INDEX IF NOT EXISTS idx_supsh_seam     ON supplementary_seam_horizon(seam_code);

CREATE TRIGGER IF NOT EXISTS trg_supsh_updated_at
AFTER UPDATE ON supplementary_seam_horizon
FOR EACH ROW
BEGIN
    UPDATE supplementary_seam_horizon SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;
