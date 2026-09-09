-- 由 build/sqlite2pg.py 从 V040_dump_strip_sub_index.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- 排土条带：位置编号补上【带内子号】，并把唯一索引改到能容下它。
--
-- 【为什么要有这一列】分割长度 L 定的是"一个位置沿走向多长"。台阶线上一处外凸拐角，
-- 推到第 k 带时那儿会多出一段半径 k·W 的圆弧 —— 带跟着变长，实测最长的一带是本幅源长的
-- 28 倍。那样的"位置"既派不出活，也不是用户填的 L=100m，所以超过 L 的带会在带内再切。
-- 切出来的几个位置 (级,幅,带) 完全相同，只差子号。
--
-- V039 的唯一索引是 (region_id, design_version, level_index, panel_index, step_index)，
-- 容不下同一带的多个子格 —— 不补这一列的话，一带切成 5 个位置，落库只进得去 1 个，
-- 剩下 4 个连同它们的库容一起消失，而清单和图上都还在。

ALTER TABLE dump_strip ADD COLUMN sub_index BIGINT NOT NULL DEFAULT 0;   -- 带内子号(1 起;0 = 该带没切)
ALTER TABLE dump_strip ADD COLUMN sub_count BIGINT NOT NULL DEFAULT 1;   -- 本带共切成几个位置

DROP INDEX IF EXISTS idx_dump_strip_key;

-- 一个排土场在一个方案下,同一(级,幅,带,子号)只能有一行(方案为空视作默认方案)
CREATE UNIQUE INDEX idx_dump_strip_key
    ON dump_strip(region_id, COALESCE(design_version, ''), level_index, panel_index, step_index, sub_index);
