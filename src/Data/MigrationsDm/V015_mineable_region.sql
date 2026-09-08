-- 由 build/sqlite2dm.py 从 V015_mineable_region.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V015: 可采区域边界 — 短期「确定可采区域」逐点圈画的采场可采区域多边形
--
-- 每条记录 = 一块命名的可采区域；边界顶点以扁平 xyz JSON 数组存 points_json。
-- 纯几何 + 元数据，不引用其它表（短期生产计划自包含）。
-- =============================================================================

CREATE TABLE mineable_region (
    id           INT IDENTITY(1,1) PRIMARY KEY,
    name         VARCHAR(255)    NOT NULL DEFAULT '',
    points_json  VARCHAR(2000)    NOT NULL DEFAULT '[]',        -- 扁平顶点 [x0,y0,z0,x1,y1,z1,...]
    visible      INT NOT NULL DEFAULT 1,           -- 是否在视口显示(0/1)
    note         VARCHAR(2000),
    created_at   VARCHAR(255)    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at   VARCHAR(255)    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TRIGGER trg_mineable_region_updated_at
BEFORE UPDATE ON mineable_region
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;
