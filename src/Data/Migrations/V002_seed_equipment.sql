-- =============================================================================
-- V002: 设备种子数据 + 型号字典
--
-- 目的:capacity_history.csv 的 model 列填的是 category 字符串(如 "Shovel"),
-- 而非真实型号(如 "4100XPC"),导致 equipment.model 不可用。
-- 本迁移:
--   1) 把 9 个真实设备型号灌进 equipment_model(技术参数)
--   2) 把 15 台典型设备的完整台账信息 UPSERT 到 equipment
--   3) 对 capacity_history 已经占位插入但 model="Shovel"/"Truck" 的设备,
--      根据 ID 区间智能映射到具体型号
--
-- 数据来源:平朔露天矿 2023.12 在籍清单 + 厂家公开规格(原 EquipmentRepository.SeedData)
-- =============================================================================

-- ─── ① 设备型号字典 ─────────────────────────────────────────────────────────
INSERT OR REPLACE INTO equipment_model
    (model, category, working_weight_t, power_kw, bucket_m3, load_t, dimensions_lwh, drill_diameter_mm, tire_spec, std_daily_cap_wan_m3)
VALUES
    ('4100XPC',  'Shovel', 1440, 4480,  57,  113, '13.5 × 9.6 × 14.2', NULL, NULL, NULL),
    ('PH2800',   'Shovel',  630, 1490,  30,   56, '10.2 × 7.6 × 11.8', NULL, NULL, NULL),
    ('WK-35',    'Shovel',  720, 1800,  35,   60, '11.0 × 8.2 × 12.4', NULL, NULL, NULL),
    ('994',      'Loader',  196,  990,  16,   36, '16.4 × 5.8 × 6.7',  NULL, '55/80R63',  NULL),
    ('730E',     'Truck',   325, 1491, NULL, 172, '11.0 × 7.4 × 6.4',  NULL, '37.00R57',  NULL),
    ('930E',     'Truck',   500, 2610, NULL, 290, '15.6 × 9.7 × 7.4',  NULL, '53/80R63',  NULL),
    ('DMH90',    'Drill',    92,  559, NULL, NULL, '12.8 × 6.5 × 13.6', 200, NULL, NULL),
    ('D10N',     'Dozer',    66,  354, NULL, NULL, '8.7 × 4.7 × 4.2',  NULL, NULL, NULL),
    ('GD825A-2', 'Grader',   26,  209, NULL, NULL, '10.0 × 3.4 × 3.6', NULL, '23.5R25', NULL),
    ('水鹤',     'WaterTruck', NULL, NULL, NULL, NULL, '4.5 × 1.2 × 6.8', NULL, NULL, NULL);

-- ─── ② 智能修补现有 equipment 表的 model/category ────────────────────────────
-- capacity_history.csv 的 model 列是 "Shovel"/"Truck" 占位,
-- 这里按 ID 区间映射到真实型号。

-- 1730-1739 老电铲 → PH2800
UPDATE equipment SET category = 'Shovel', model = 'PH2800'
WHERE CAST(equipment_id AS INTEGER) BETWEEN 1730 AND 1739
  AND (model = 'Shovel' OR model IS NULL);

-- 1740-1750 新一代电铲 → 4100XPC
UPDATE equipment SET category = 'Shovel', model = '4100XPC'
WHERE CAST(equipment_id AS INTEGER) BETWEEN 1740 AND 1750
  AND (model = 'Shovel' OR model IS NULL);

-- 3010-3019 / 9402 国产电铲 → WK-35
UPDATE equipment SET category = 'Shovel', model = 'WK-35'
WHERE (CAST(equipment_id AS INTEGER) BETWEEN 3010 AND 3019
       OR equipment_id = '9402')
  AND (model = 'Shovel' OR model IS NULL);

-- 1660-1670 卡车 → 730E
UPDATE equipment SET category = 'Truck', model = '730E'
WHERE CAST(equipment_id AS INTEGER) BETWEEN 1658 AND 1670
  AND (model IS NULL OR model = 'Truck' OR model = 'Other');

-- 2020-2039 大吨位卡车 → 930E
UPDATE equipment SET category = 'Truck', model = '930E'
WHERE CAST(equipment_id AS INTEGER) BETWEEN 2020 AND 2039
  AND (model IS NULL OR model = 'Truck' OR model = 'Other');

-- 1420 水鹤
UPDATE equipment SET category = 'WaterTruck', model = '水鹤'
WHERE equipment_id = '1420';

-- ─── ③ UPSERT 15 台典型设备的完整台账 ────────────────────────────────────────
INSERT INTO equipment
    (equipment_id, category, model, manufacturer, origin, serial_number, asset_code,
     status, acquisition_date, cumulative_hours, last_overhaul_date, operating_area, notes)
VALUES
    ('1748', 'Shovel', '4100XPC', 'Joy Global / Komatsu Mining', '美国', 'JG-4100-08742', 'ATB-S-1748',
        '在用', '2014-08-16', 56230, '2022-11-05', '1195 平盘 · 生产二队',
        '2023 年主力电铲,4 煤顶板与 1195 平盘作业;12 月年累产量约 110 万 m³'),
    ('1749', 'Shovel', '4100XPC', 'Joy Global / Komatsu Mining', '美国', 'JG-4100-08813', 'ATB-S-1749',
        '在用', '2015-03-10', 52480, '2023-04-18', '1210 平盘 · 生产一队',
        '1210 平盘作业,7 月避炮 1.5 小时'),
    ('1734', 'Shovel', 'PH2800', 'P&H / Joy Global', '美国', 'PH-2800-12057', 'ATB-S-1734',
        '在用', '2008-06-01', 78950, '2021-09-12', '1180 平盘 · 待调度',
        '12 月年初在籍,部分时段无工作线备用,待大修评估'),
    ('3017', 'Shovel', 'WK-35', '太原重工', '中国', 'TZ-WK35-2310-017', 'ATB-S-3017',
        '在用', '2023-10-15', 1280, NULL, '1240 平盘 · 生产一队',
        '2023 年 10 月起入籍,国产化电铲,开门绳偶有故障'),
    ('9402', 'Shovel', 'WK-35', '太原重工', '中国', 'TZ-WK35-2311-024', 'ATB-S-9402',
        '在用', '2023-11-02', 720, NULL, '1225 平盘',
        '11 月起入籍,新车磨合期'),
    ('CAT-994', 'Loader', '994', 'Caterpillar', '美国', 'CAT-994H-3GR00521', 'ATB-L-0994',
        '在用', '2017-05-22', 28640, '2023-06-30', '辅助装运 · 流动',
        '前装机,辅助装运,12 月操作延误率较高'),
    ('1670', 'Truck', '730E', 'Komatsu', '日本', 'KM-730E-A30482', 'ATB-T-1670',
        '在用', '2016-11-08', 41280, '2023-08-14', '西帮运输',
        '日报中频繁出现发动机/低马力故障记录'),
    ('1662', 'Truck', '730E', 'Komatsu', '日本', 'KM-730E-A30519', 'ATB-T-1662',
        '大修', '2017-02-19', 39860, '2024-01-05', '维修车间',
        '730E 卡车,小车滑块故障 2.75 小时,待修复'),
    ('2030', 'Truck', '930E', 'Komatsu', '日本', 'KM-930E-B12056', 'ATB-T-2030',
        '在用', '2019-04-30', 22640, '2023-03-08', '主运输线',
        '930E 大吨位卡车,电传动'),
    ('3416', 'Drill', 'DMH90', 'Atlas Copco / Epiroc', '瑞典', 'AC-DMH90-78104', 'ATB-D-3416',
        '在用', '2015-09-12', 33450, '2023-05-20', '采空区探测 · 流动',
        '探孔主力钻机,12 月探孔频次最高;曾发卡钳故障 5 小时'),
    ('3429', 'Drill', 'DMH90', 'Atlas Copco / Epiroc', '瑞典', 'AC-DMH90-79217', 'ATB-D-3429',
        '在用', '2017-04-28', 24180, '2022-12-09', '1195 平盘 · 炮区设计',
        '炮区作业钻机'),
    ('D10N-01', 'Dozer', 'D10N', 'Caterpillar', '美国', 'CAT-D10N-2YD03891', 'ATB-Z-D10N-01',
        '在用', '2012-07-14', 51280, '2022-10-22', '排土场作业',
        '履带推土机(共 8 台),单机编号需机修科补齐'),
    ('GD-01', 'Grader', 'GD825A-2', 'Komatsu', '日本', 'KM-GD825A-22107', 'ATB-G-01',
        '在用', '2018-09-03', 18920, '2023-07-11', '矿区道路养护',
        '平路机(共 8 台),铲刀宽 4.3m'),
    ('1420', 'WaterTruck', '水鹤', '自制 / 现场配套', '中国', '—', 'ATB-W-1420',
        '在用', '2010-05-01', 0, NULL, '坑下加水点',
        '坑下加水水鹤,日报中加水次数记录;非自走设备'),
    ('1750', 'Shovel', '4100XPC', 'Joy Global / Komatsu Mining', '美国', 'JG-4100-08891', 'ATB-S-1750',
        '在用', '2016-07-22', 47120, '2023-09-04', '1195 平盘 · 生产二队',
        '2023 年主力电铲,与 1748 同型')
ON CONFLICT(equipment_id) DO UPDATE SET
    category = excluded.category,
    model = excluded.model,
    manufacturer = excluded.manufacturer,
    origin = excluded.origin,
    serial_number = excluded.serial_number,
    asset_code = excluded.asset_code,
    status = excluded.status,
    acquisition_date = excluded.acquisition_date,
    cumulative_hours = excluded.cumulative_hours,
    last_overhaul_date = excluded.last_overhaul_date,
    operating_area = excluded.operating_area,
    notes = excluded.notes;
