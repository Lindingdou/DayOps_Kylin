-- 由 build/sqlite2pg.py 从 V039_dump_strip.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- pmgeo.db 潜在排土位置(排土条带网格)
-- 版本: V039
-- 说明: 排土场比采场规整 —— 台阶按标准水平分级、同一级标高不变、坡面角设计给定。
--       所以排土模型走「坡顶线 + 坡底线 → 台阶壳子 → 按分割长度 × 条带宽度切网格」这条路,
--       切出来的每一格 = 一个【潜在排土位置】,本表就是那张位置清单。
--
-- 它给谁用(09.md 的三条算法支撑都落在这张表上):
--   ① 寻径算运距  —— 每个位置有质心 xyz,采矿模型 → 潜在排土条带的运输线路/运距直接算
--   ② 采剥次序匹配 —— 位置带台阶级/幅号/带号,按排土次序取用
--   ③ 多方案比选  —— design_version 存多套排土方案,同一个排土场可以并存几种切法
--
-- 【方量口径】capacity_m3 是【占容方 V容】= 走向长 × 条带宽 × 台阶高,
--       即这个位置在排土场里实际占掉的空间。它能承接多少采场剥离【实方】= V容 / Kr
--       (Kr = 残余膨胀系数,随物料走,见 material_spec)。两个口径不可混用 ——
--       表里只存几何量占容方,换算留给读的人,免得把物料口径焊死在几何里。
--
-- 【截面为什么是平行四边形】顶/底是水平的(同一级台阶),前脸是坡面、后脸是同一坡面平移 W,
--       所以垂直走向的剖面是平行四边形,面积 = W × H 而不是梯形。相邻带首尾相接正好填满
--       整级台阶 —— 这才是"条带"该有的账,梯形会重复计一部分。
--
-- 关联: region_id → mineable_region.id(排土场区域,category = external_dump / internal_dump)。
--       区域删了它的位置一起删 —— 位置是区域的派生物,没有区域的位置是孤儿。
-- =============================================================================

CREATE TABLE IF NOT EXISTS dump_strip (
    id                  BIGSERIAL PRIMARY KEY,

    region_id           BIGINT NOT NULL,           -- → mineable_region.id(排土场)
    region_name         TEXT    NOT NULL DEFAULT '',-- 冗余名字:清单直接可读,不必每次连表
    category            TEXT    NOT NULL DEFAULT 'external_dump',  -- external_dump / internal_dump

    code                TEXT    NOT NULL,           -- 位置编号 '外排1-L3-P02-S05'
    level_index         BIGINT NOT NULL,           -- 台阶级序(1 = 最上一级)
    panel_index         BIGINT NOT NULL,           -- 沿走向第几幅(1 起)
    panel_count         BIGINT NOT NULL DEFAULT 1, -- 本级共几幅
    step_index          BIGINT NOT NULL,           -- 沿推进方向第几带(1 = 当前排土线那一带)

    crest_z             DOUBLE PRECISION    NOT NULL,           -- 坡顶标高(m)
    toe_z               DOUBLE PRECISION    NOT NULL,           -- 坡底标高(m)
    bench_height_m      DOUBLE PRECISION    NOT NULL,           -- 台阶高 = crest_z − toe_z

    strike_len_m        DOUBLE PRECISION    NOT NULL,           -- 走向长(m,水平)
    strip_width_m       DOUBLE PRECISION    NOT NULL,           -- 排土条带宽度 W(m,推进方向)
    capacity_m3         DOUBLE PRECISION    NOT NULL,           -- 库容(m³,占容方)= 走向长 × W × 台阶高

    centroid_x          DOUBLE PRECISION    NOT NULL,           -- 质心(世界坐标)—— 寻径算运距吃它
    centroid_y          DOUBLE PRECISION    NOT NULL,
    centroid_z          DOUBLE PRECISION    NOT NULL,

    -- 前脸轨(扁平 xyz JSON):重建体/重画不必再跑一遍切分。存轨不存体 ——
    -- 体在图上(entity_handle),库里存能复现体的最小输入。
    crest_json          TEXT    NOT NULL DEFAULT '[]',
    toe_json            TEXT    NOT NULL DEFAULT '[]',

    entity_handle       BIGINT NOT NULL DEFAULT 0, -- 图上壳子体的 handle(0 = 还没建体)

    design_version      TEXT,                       -- 多方案比选:同一排土场并存几种切法
    notes               TEXT,
    created_at          TEXT    DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT    DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY (region_id) REFERENCES mineable_region(id) ON DELETE CASCADE ON UPDATE CASCADE
);

-- 一个排土场在一个方案下,同一(级,幅,带)只能有一行(方案为空视作默认方案)
CREATE UNIQUE INDEX idx_dump_strip_key
    ON dump_strip(region_id, COALESCE(design_version, ''), level_index, panel_index, step_index);

-- 按排土场取清单 / 按台阶级取清单,是最常走的两条读法
CREATE INDEX idx_dump_strip_region ON dump_strip(region_id, level_index, step_index);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_dump_strip_updated_at ON dump_strip;
CREATE TRIGGER trg_dump_strip_updated_at
BEFORE UPDATE ON dump_strip
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();
