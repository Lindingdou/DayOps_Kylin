-- 由 build/sqlite2dm.py 从 V033_current_state_seam_horizon.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- pmgeo.db 现状写实·见煤点(哪层煤 + 顶/底板)
-- 版本: V033
-- 说明: 现状写实改为「视口拾取见煤点」——每点带 煤层编号 + 顶/底板。
--       给 current_state_point 加 seam_code + horizon 两列(V032 已建表,这里增列)。
-- 备注: 已有 V032 期间录入的现状点(若有) seam_code/horizon 为 NULL,视为未标注。
-- =============================================================================

ALTER TABLE current_state_point ADD COLUMN seam_code VARCHAR(255);   -- 煤层编号(4/7-1/9/11/...)
ALTER TABLE current_state_point ADD COLUMN horizon VARCHAR(255);   -- '顶板' / '底板'
