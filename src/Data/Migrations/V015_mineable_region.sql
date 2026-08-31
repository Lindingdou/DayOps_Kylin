-- =============================================================================
-- V015: 可采区域边界 — 短期「确定可采区域」逐点圈画的采场可采区域多边形
--
-- 每条记录 = 一块命名的可采区域；边界顶点以扁平 xyz JSON 数组存 points_json。
-- 纯几何 + 元数据，不引用其它表（短期生产计划自包含）。
-- =============================================================================

CREATE TABLE IF NOT EXISTS mineable_region (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    name         TEXT    NOT NULL DEFAULT '',
    points_json  TEXT    NOT NULL DEFAULT '[]',        -- 扁平顶点 [x0,y0,z0,x1,y1,z1,...]
    visible      INTEGER NOT NULL DEFAULT 1,           -- 是否在视口显示(0/1)
    note         TEXT,
    created_at   TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at   TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TRIGGER IF NOT EXISTS trg_mineable_region_updated_at
AFTER UPDATE ON mineable_region FOR EACH ROW BEGIN
    UPDATE mineable_region SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;
