-- 由 build/sqlite2pg.py 从 V012_seed_equipment_constraints.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V012: 设备约束种子数据
--
-- 给关键工艺参数填设备能力约束,让"平盘工艺地图"中的"适配设备"功能真正生效。
-- consequence:
--   hard         违反 → 不可用(过滤掉)
--   soft         违反 → 降效(打折)
--   informational 仅提示
-- =============================================================================

-- ─── 钻孔环节 / 孔径(param_id=1001)─ 钻机能力限制 ────────────────────────
INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value, consequence, priority, description)
VALUES
    (1001, 'DMH90', 'max', 251, 'hard', 4, 'DMH90 最大孔径 251mm');

-- ─── 工作面规划 / 台阶高度(param_id=2001)─ 电铲最大挖掘高度 ──────────────
-- 4100XPC 最大挖掘高度 ~15.5m / PH2800 ~12m / WK-35 ~13m
INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value, consequence, priority, description)
VALUES
    (2001, '4100XPC', 'max', 15.5, 'hard', 5, '4100XPC 最大挖掘高度 15.5m'),
    (2001, 'PH2800',  'max', 12.0, 'hard', 5, 'PH2800 最大挖掘高度 12m'),
    (2001, 'WK-35',   'max', 13.0, 'hard', 5, 'WK-35 最大挖掘高度 13m'),
    (2001, '994',     'max', 8.0,  'hard', 5, '994 装载机最大装载高度 8m');

-- ─── 主道运输 / 运距(param_id=3001)─ 卡车经济运距 ────────────────────────
INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value, consequence, priority, description)
VALUES
    (3001, '730E', 'max', 4.0, 'soft', 3, '730E 经济运距 ≤ 4 km(超出降效)'),
    (3001, '930E', 'max', 6.0, 'soft', 3, '930E 经济运距 ≤ 6 km(超出降效)');

-- ─── 主道运输 / 最大坡度(param_id=3002)─ 卡车爬坡能力 ────────────────────
INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value, consequence, priority, description)
VALUES
    (3002, '730E', 'max', 10.0, 'hard', 5, '730E 爬坡能力 10%'),
    (3002, '930E', 'max', 9.0,  'hard', 5, '930E 爬坡能力 9%(重型车爬坡稍弱)');

-- ─── 主道运输 / 路宽(param_id=3003)─ 卡车回转半径 ────────────────────────
INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value, consequence, priority, description)
VALUES
    (3003, '730E', 'min', 25, 'hard', 4, '730E 安全通行路宽 ≥ 25m'),
    (3003, '930E', 'min', 30, 'hard', 4, '930E 安全通行路宽 ≥ 30m');

-- ─── 排土场 / 单层堆高(param_id=4002)─ 推土机能力 ────────────────────────
INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value, consequence, priority, description)
VALUES
    (4002, 'D10N', 'max', 15, 'soft', 3, 'D10N 单层推排能力 ≤ 15m');

-- ─── 边坡 / 安全系数(param_id=5002)─ 全矿 informational ────────────────
INSERT INTO equipment_constraint (param_id, equipment_model, constraint_type, limit_value, consequence, priority, description)
VALUES
    (5002, '4100XPC', 'min', 1.30, 'informational', 2, '工作面所在边坡须满足 F≥1.30'),
    (5002, 'PH2800',  'min', 1.30, 'informational', 2, '同上'),
    (5002, 'WK-35',   'min', 1.30, 'informational', 2, '同上');
