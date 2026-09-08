-- 由 build/sqlite2pg.py 从 V017_load_unload_point.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V017: 装卸点 — 开拓运输系统的源(采剥点)/汇(卸载点)
--
-- 每条记录 = 一个命名的装卸点;项目级持久化(随工程 .db),取代早先 user 设置 blob。
-- RoadLib「装卸点设置」手动插入;kind 区分源/汇,unload_sub 细分卸载点子类。
-- ref_region_id 可关联 mineable_region(0=未关联);纯几何 + 元数据,自包含。
-- 见 docs/开拓运输系统_窗体设计.md W1 + 需求07落地决策。
-- =============================================================================

CREATE TABLE IF NOT EXISTS load_unload_point (
    id             SERIAL PRIMARY KEY,
    name           TEXT    NOT NULL DEFAULT '',
    kind           TEXT    NOT NULL DEFAULT 'loading',   -- loading(采剥点/源) | unloading(卸载点/汇)
    unload_sub     TEXT,                                 -- crusher|dump|stockpile (仅卸载点)
    x              DOUBLE PRECISION    NOT NULL DEFAULT 0,
    y              DOUBLE PRECISION    NOT NULL DEFAULT 0,
    z              DOUBLE PRECISION    NOT NULL DEFAULT 0,
    throughput_tph DOUBLE PRECISION    NOT NULL DEFAULT 0,
    ref_region_id  INTEGER NOT NULL DEFAULT 0,           -- mineable_region.id, 0=未关联
    visible        INTEGER NOT NULL DEFAULT 1,
    note           TEXT,
    created_at     TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at     TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_load_unload_point_updated_at ON load_unload_point;
CREATE TRIGGER trg_load_unload_point_updated_at
BEFORE UPDATE ON load_unload_point
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();
