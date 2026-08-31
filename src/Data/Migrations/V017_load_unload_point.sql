-- =============================================================================
-- V017: 装卸点 — 开拓运输系统的源(采剥点)/汇(卸载点)
--
-- 每条记录 = 一个命名的装卸点;项目级持久化(随工程 .db),取代早先 user 设置 blob。
-- RoadLib「装卸点设置」手动插入;kind 区分源/汇,unload_sub 细分卸载点子类。
-- ref_region_id 可关联 mineable_region(0=未关联);纯几何 + 元数据,自包含。
-- 见 docs/开拓运输系统_窗体设计.md W1 + 需求07落地决策。
-- =============================================================================

CREATE TABLE IF NOT EXISTS load_unload_point (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    name           TEXT    NOT NULL DEFAULT '',
    kind           TEXT    NOT NULL DEFAULT 'loading',   -- loading(采剥点/源) | unloading(卸载点/汇)
    unload_sub     TEXT,                                 -- crusher|dump|stockpile (仅卸载点)
    x              REAL    NOT NULL DEFAULT 0,
    y              REAL    NOT NULL DEFAULT 0,
    z              REAL    NOT NULL DEFAULT 0,
    throughput_tph REAL    NOT NULL DEFAULT 0,
    ref_region_id  INTEGER NOT NULL DEFAULT 0,           -- mineable_region.id, 0=未关联
    visible        INTEGER NOT NULL DEFAULT 1,
    note           TEXT,
    created_at     TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at     TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TRIGGER IF NOT EXISTS trg_load_unload_point_updated_at
AFTER UPDATE ON load_unload_point FOR EACH ROW BEGIN
    UPDATE load_unload_point SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;
