-- 由 build/sqlite2dm.py 从 V016_mineable_region_category.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V016: 可采区域表升级为统一「区域」表 — 加 category 列。
--
-- 件一「自动识别采场/排土场」与手动「确定可采区域」共用 mineable_region：
--   mineable        = 可采区域（采场内，手动圈画的默认值）
--   pit             = 采场（凹·逐级降深）
--   external_dump   = 外排土场（凸·境界外）
--   internal_dump   = 内排土场（凸·坑内回填）
-- 见 docs/采场排土场_自动识别与参数校核_设计.md §4。
-- =============================================================================

ALTER TABLE mineable_region ADD COLUMN category VARCHAR(255) NOT NULL DEFAULT 'mineable';
