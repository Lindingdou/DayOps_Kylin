-- 由 build/sqlite2pg.py 从 V010_seed_templates_and_bindings.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V010: 模板库 + 平盘绑定 种子数据
--
-- 3 套现行模板 + 5 个平盘各绑定 4 个核心环节(默认套用对应模板)
-- =============================================================================

-- ─── 3 套现行模板 ───────────────────────────────────────────────────────────
INSERT INTO process_template (template_id, code, name, description, applicable_material, applicable_hardness, version, is_current, status) VALUES
    (1, 'rh_hard_v1.0',     '硬岩区标准 v1.0',  '安太堡矿砂岩为主区域的标准工艺参数',  'rh',   'hard',   'v1.0', 1, 'active'),
    (2, 'c4_medium_v1.0',   '4 煤标准 v1.0',    '4 煤顶板及夹层的中硬岩参数',          'c4',   'medium', 'v1.0', 1, 'active'),
    (3, 'coal_soft_v1.0',   '煤层标准 v1.0',    '纯煤层直采,无爆破',                  'coal', 'soft',   'v1.0', 1, 'active') ON DUPLICATE KEY UPDATE name = VALUES(name), description = VALUES(description), applicable_material = VALUES(applicable_material), applicable_hardness = VALUES(applicable_hardness), version = VALUES(version), is_current = VALUES(is_current), status = VALUES(status);

-- ─── 硬岩区模板的参数推荐值 ────────────────────────────────────────────────
-- 钻孔环节(101)
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 1001, 250, 220, 280, '硬岩取大孔径'),
    (1, 1002, 15,  13,  17,  '台阶 15m + 超钻 1.5m'),
    (1, 1003, 7,   6,   8,   '匹配孔径与单耗'),
    (1, 1004, 6,   5,   7,   '梅花布孔'),
    (1, 1005, 1.5, 1,   2,   '抗反弹超钻'),
    (1, 1006, 0,   NULL, NULL, '垂直钻孔') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 装药 102
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 1101, 8.5, 7, 10, '硬岩高密度装药'),
    (1, 1102, 4,   3, 5,  '足够堵塞抑制飞石') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 起爆 103 (text-only,跳过)

-- 爆破效果 104
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 1301, 0.50, 0.40, 0.60, '历史实测均值校准'),
    (1, 1303, 3,    0,    5,    '硬岩可接受大块率'),
    (1, 1304, 2,    0,    3,    '根底率控制') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 工作面规划 201
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 2001, 15, 13, 17, '4100XPC 适配最大台阶'),
    (1, 2002, 70, 65, 75, '硬岩稳定坡角'),
    (1, 2003, 8,  6,  12, '满足卡车回转'),
    (1, 2004, 45, 35, 55, '工作平台净宽'),
    (1, 2005, 50, 40, 60, '采宽兼顾效率与安全') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 剥离 202
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 2101, 45, 30, 60, '硬岩月推进度') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 主道运输 302
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 3001, 4,  2, 6,  '安太堡矿平均运距'),
    (1, 3002, 8,  6, 10, '主道最大坡度'),
    (1, 3003, 36, 30,40, '930E 车宽 + 2 倍裕度') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 装车 301
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 3101, 4, 3, 5,  '4100XPC + 930E 配比'),
    (1, 3102, 6, 5, 8,  '推荐配车数') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 排土 402
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 4001, 45, 30, 60, '排土场最大堆高'),
    (1, 4002, 12, 10, 15, '单层堆高'),
    (1, 4003, 32, 30, 35, '整体坡角') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- 边坡 501/502
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (1, 5001, 28, 25, 32, '硬岩工作帮坡角'),
    (1, 5002, 1.35, 1.30, 2.0, 'F ≥ 1.30 安全'),
    (1, 5101, 42, 40, 45, '最终帮硬岩坡角'),
    (1, 5102, 300, 200, 400, '矿坑最大深度') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- ─── 4 煤模板(c4)─ 简化版,只填关键参数差异 ──────────────────────────────
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (2, 1001, 200, 165, 220, '中硬岩用较小孔径'),
    (2, 1002, 12,  10,  14,  '台阶 12m'),
    (2, 1003, 6,   5,   7,   ''),
    (2, 1004, 5,   4,   6,   ''),
    (2, 1101, 7.5, 6, 9,    '中硬装药'),
    (2, 1301, 0.31, 0.25, 0.40, '4 煤顶板单耗'),
    (2, 2001, 12,  10, 14,   '匹配 PH2800/WK-35'),
    (2, 2002, 68,  60, 72,   ''),
    (2, 5001, 26,  22, 30,   ''),
    (2, 5002, 1.32, 1.30, 1.8, '') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- ─── 煤层模板(coal-soft)─ 直采,无爆破 ───────────────────────────────────
INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, notes) VALUES
    (3, 2001, 10, 8, 12, '煤层直采小台阶'),
    (3, 2002, 65, 60, 70, ''),
    (3, 2101, 60, 40, 80, '煤层推进度高') ON DUPLICATE KEY UPDATE recommended_value = VALUES(recommended_value), min_value = VALUES(min_value), max_value = VALUES(max_value), notes = VALUES(notes);

-- ─── 5 个平盘 × 8 个核心环节绑定 ──────────────────────────────────────────
-- 1195 平盘:硬岩区(rh) → 套用 模板 1
-- 1210 平盘:硬岩区(rh) → 套用 模板 1
-- 1180 平盘:中硬岩(PH2800) → 套用 模板 2(c4 模板兼容中硬岩)
-- 1240 平盘:硬岩 + 新铲 → 套用 模板 1
-- 1270 平盘:硬岩 + 高位 → 套用 模板 1
INSERT INTO phase_location_binding (phase_id, location_code, bound_template_id, is_active, started_at) VALUES
    -- 1195 平盘
    (101, '1195', 1, 1, '2024-01-01'),  -- 钻孔
    (102, '1195', 1, 1, '2024-01-01'),  -- 装药
    (103, '1195', 1, 1, '2024-01-01'),  -- 起爆
    (104, '1195', 1, 1, '2024-01-01'),  -- 爆破效果
    (201, '1195', 1, 1, '2024-01-01'),  -- 工作面规划
    (202, '1195', 1, 1, '2024-01-01'),  -- 剥离
    (301, '1195', 1, 1, '2024-01-01'),  -- 装车
    (302, '1195', 1, 1, '2024-01-01'),  -- 主道运输
    -- 1210 平盘
    (101, '1210', 1, 1, '2024-01-01'),
    (102, '1210', 1, 1, '2024-01-01'),
    (201, '1210', 1, 1, '2024-01-01'),
    (202, '1210', 1, 1, '2024-01-01'),
    (301, '1210', 1, 1, '2024-01-01'),
    (302, '1210', 1, 1, '2024-01-01'),
    -- 1180 平盘(PH2800,中硬)
    (101, '1180', 2, 1, '2024-01-01'),
    (201, '1180', 2, 1, '2024-01-01'),
    (202, '1180', 2, 1, '2024-01-01'),
    (301, '1180', 2, 1, '2024-01-01'),
    -- 1240 平盘(新铲)
    (101, '1240', 1, 1, '2024-01-01'),
    (201, '1240', 1, 1, '2024-01-01'),
    (202, '1240', 1, 1, '2024-01-01'),
    -- 1270 平盘
    (101, '1270', 1, 1, '2024-01-01'),
    (201, '1270', 1, 1, '2024-01-01'),
    (202, '1270', 1, 1, '2024-01-01'),
    -- 边坡(全矿统一)
    (501, '1195', 1, 1, '2024-01-01'),
    (502, '1210', 1, 1, '2024-01-01'),
    -- 排土场进出(取 1195 代表)
    (402, '1195', 1, 1, '2024-01-01') ON DUPLICATE KEY UPDATE bound_template_id = VALUES(bound_template_id), is_active = VALUES(is_active);
