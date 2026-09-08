-- 由 build/sqlite2dm.py 从 V007_seed_process_architecture.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V007: 工艺架构种子数据
-- 预填 8 个工艺系统 / 28 个工艺环节 / 60+ 个参数定义
-- =============================================================================

-- ─── 8 个工艺系统 ───────────────────────────────────────────────────────────
REPLACE INTO process_system (system_id, code, name, category, description, display_order) VALUES
    (1, 'blasting',  '穿爆系统',       'drilling',  '钻孔 + 装药 + 起爆 + 爆破效果',                       10),
    (2, 'mining',    '采装系统',       'mining',    '工作面规划 + 剥离作业 + 采煤作业',                   20),
    (3, 'transport', '运输系统',       'transport', '装车 + 主道运输 + 卸车 + 路况维护',                  30),
    (4, 'dumping',   '排土系统',       'dumping',   '内排作业 + 外排作业 + 排土场监测',                   40),
    (5, 'slope',     '边坡稳定系统',   'slope',     '工作帮设计 + 最终帮设计 + 边坡监测',                 50),
    (6, 'aux',       '辅助系统',       'aux',       '道路维护 + 给排水 + 防尘洒水',                       60),
    (7, 'economic',  '经济系统',       'economic',  '成本核算 + 单价管理',                                70),
    (8, 'safety',    '安全环保系统',   'safety',    '振动 / 粉尘 / 噪声监测 + 安全规程',                  80);

-- ─── 28 个工艺环节 ─────────────────────────────────────────────────────────
REPLACE INTO process_phase (phase_id, system_id, code, name, sequence_order, typical_equipment_category, description) VALUES
    -- 穿爆系统
    (101, 1, 'drilling',         '钻孔',            1, 'Drill',     '炮孔布置与施钻'),
    (102, 1, 'charging',         '装药',            2, NULL,        '炸药装填与堵塞'),
    (103, 1, 'detonation',       '起爆',            3, NULL,        '雷管联网与起爆执行'),
    (104, 1, 'blast_acceptance', '爆破效果验收',    4, NULL,        '大块率/根底率/方量验收'),
    -- 采装系统
    (201, 2, 'face_planning',    '工作面规划',      1, NULL,        '台阶/平台/工作线布置'),
    (202, 2, 'stripping',        '剥离作业',        2, 'Shovel',    '岩石剥离装车'),
    (203, 2, 'coal_mining',      '采煤作业',        3, 'Shovel',    '煤层采装'),
    -- 运输系统
    (301, 3, 'loading',          '装车',            1, 'Truck',     '电铲→卡车装载'),
    (302, 3, 'haul_main',        '主道运输',        2, 'Truck',     '大卡车主干线运输'),
    (303, 3, 'unloading',        '卸车',            3, 'Truck',     '排土点/破碎点卸料'),
    (304, 3, 'road_maintenance', '路况维护',        4, 'Grader',    '平路 + 压实 + 洒水'),
    -- 排土系统
    (401, 4, 'inner_dumping',    '内排作业',        1, 'Dozer',     '矿坑内排土'),
    (402, 4, 'outer_dumping',    '外排作业',        2, 'Dozer',     '矿坑外排土'),
    (403, 4, 'dump_monitoring',  '排土场监测',      3, NULL,        '稳定性/沉降/渗水监测'),
    -- 边坡稳定系统
    (501, 5, 'working_slope',    '工作帮设计',      1, NULL,        '工作帮坡角/采空区控制'),
    (502, 5, 'final_slope',      '最终帮设计',      2, NULL,        '最终帮稳定性'),
    (503, 5, 'slope_monitoring', '边坡监测',        3, NULL,        '位移/水位/降雨监测'),
    -- 辅助系统
    (601, 6, 'road_aux',         '道路维护',        1, 'Grader',    '路面养护'),
    (602, 6, 'water_drainage',   '给排水',          2, NULL,        '矿坑涌水/泵房/沉淀池'),
    (603, 6, 'dust_control',     '防尘洒水',        3, 'WaterTruck','作业区/道路洒水'),
    -- 经济系统
    (701, 7, 'cost_accounting',  '成本核算',        1, NULL,        '剥离/采煤/外委成本'),
    (702, 7, 'unit_pricing',     '单价管理',        2, NULL,        '油/电/炸药/钻头单价'),
    -- 安全环保
    (801, 8, 'vibration_mon',    '振动监测',        1, NULL,        '爆破振动速度'),
    (802, 8, 'dust_mon',         '粉尘监测',        2, NULL,        '工作区粉尘浓度'),
    (803, 8, 'noise_mon',        '噪声监测',        3, NULL,        '边界噪声'),
    (804, 8, 'safety_rules',     '安全规程',        4, NULL,        '操作规程与隐患排查');

-- ─── 60+ 参数定义 ─────────────────────────────────────────────────────────
-- 穿爆 / 钻孔
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (1001, 101, 'hole_diameter',         '孔径',         'mm',     'numeric', 165, 250, 220, 150, 280, 1, NULL, 'blast_event', 'diameter_mm',  '炮孔直径',           1),
    (1002, 101, 'hole_depth',            '孔深',         'm',      'numeric',  10,  18,  15,   8,  20, 1, NULL, NULL,           NULL,            '台阶高度 + 超钻',    2),
    (1003, 101, 'hole_spacing',          '孔距',         'm',      'numeric',   5,   9,   7,   4,  10, 1, NULL, NULL,           NULL,            '同排炮孔间距',       3),
    (1004, 101, 'row_spacing',           '排距',         'm',      'numeric',   4,   8,   6,   3,   9, 1, NULL, NULL,           NULL,            '排间距',             4),
    (1005, 101, 'subdrill_depth',        '超钻',         'm',      'numeric',   1,   2, 1.5, 0.5,  2.5, 1, NULL, NULL,           NULL,            '抗反弹超钻',         5),
    (1006, 101, 'hole_inclination',      '钻孔倾角',     '°',      'numeric',   0,  20,   0, -10,  30, 0, NULL, NULL,           NULL,            '0=垂直',             6),
    (1007, 101, 'hole_count',            '单炮孔数',     '个',     'numeric',  50, 300, 174,  20, 500, 0, NULL, 'blast_event', 'hole_count',    '单次爆破炮孔总数',   7);

-- 穿爆 / 装药
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (1101, 102, 'charge_density',        '装药密度',     'kg/m',   'numeric',  6,  10,  8.5,   5,  12, 1, NULL, NULL,           NULL,            '单米孔装药量',      1),
    (1102, 102, 'stemming_length',       '堵塞长度',     'm',      'numeric',  3,   5,   4,   2,   6, 1, NULL, NULL,           NULL,            '孔口堵塞段',         2),
    (1103, 102, 'charge_total_kg',       '总装药量',     'kg',     'numeric',  NULL, NULL, NULL, NULL, NULL, 0, NULL, 'blast_event', 'explosive_kg', '单炮总炸药',         3),
    (1104, 102, 'explosive_type',        '炸药类型',     '',       'text',     NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL,           NULL,            '乳化/铵油/电子',     4);

-- 穿爆 / 起爆
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (1201, 103, 'detonation_method',     '起爆方式',     '',       'enum',     NULL, NULL, NULL, NULL, NULL, 1, NULL, NULL, NULL, '电雷管/导爆管/电子雷管', 1),
    (1202, 103, 'delay_ms',               '雷管延期',     'ms',     'numeric',  25, 100,  50,  10, 200, 0, NULL, NULL, NULL, '毫秒延期',                 2);

-- 穿爆 / 爆破效果验收
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (1301, 104, 'unit_consumption',      '单耗',         'kg/m³',  'numeric',  0.3, 0.6, 0.50, 0.2, 0.8, 1,
        'charge_total_kg / blast_volume_m3', 'blast_event', 'unit_consumption_kg_m3', '炸药单耗',             1),
    (1302, 104, 'blast_volume',          '爆破方量',     'm³',     'numeric',  NULL, NULL, NULL, NULL, NULL, 0, NULL,
        'blast_event', 'blast_volume_m3', '单炮爆破方量',                                                  2),
    (1303, 104, 'oversized_rate_pct',    '大块率',       '%',      'numeric',  0,   5,    3,   0,   8, 1, NULL, NULL, NULL, '大块占比',                3),
    (1304, 104, 'toe_rate_pct',          '根底率',       '%',      'numeric',  0,   3,    2,   0,   5, 1, NULL, NULL, NULL, '根底占比',                4);

-- 采装 / 工作面规划
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (2001, 201, 'bench_height',          '台阶高度',     'm',      'numeric',  12, 15,   15,  10,  18, 1, NULL, 'working_face', 'bench_height_m',        '台阶垂直高度',     1),
    (2002, 201, 'bench_slope_angle',     '台阶坡面角',   '°',      'numeric',  65, 75,   70,  60,  80, 1, NULL, 'working_face', 'bench_slope_angle_deg', '台阶面与水平夹角', 2),
    (2003, 201, 'safety_platform_width', '安全平台宽',   'm',      'numeric',   6, 12,    8,   5,  15, 1, NULL, 'working_face', 'safety_platform_width_m', '安全平台净宽',    3),
    (2004, 201, 'working_platform_width','工作平台宽',   'm',      'numeric',  30, 50,   45,  25,  60, 1, NULL, 'working_face', 'working_platform_width_m','作业平台净宽',    4),
    (2005, 201, 'mining_width',          '采宽',         'm',      'numeric',  35, 60,   50,  30,  70, 1, NULL, 'working_face', 'mining_width_m',         '工作面采宽',     5),
    (2006, 201, 'face_length',           '工作面长度',   'm',      'numeric', 150,300,  220, 120, 400, 0, NULL, 'working_face', 'face_length_m',          '工作面延展长度', 6);

-- 采装 / 剥离
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (2101, 202, 'advance_rate',          '月推进度',     'm/月',   'numeric',  20, 60,   45,  15,  80, 1, NULL, 'working_face', 'advance_rate_m_per_month','月推进距离',     1),
    (2102, 202, 'shovel_cycle_time',     '电铲循环时间', 's',      'numeric',  35, 55,   45,  30,  70, 0, NULL, NULL, NULL, '装-回转-卸-回转',           2),
    (2103, 202, 'bucket_fill_factor',    '装满系数',     '',       'numeric', 0.85, 1.0, 0.92, 0.7, 1.1, 0, NULL, NULL, NULL, '铲斗实际装满率',           3);

-- 运输 / 主道运输
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (3001, 302, 'haul_distance_km',      '运距',         'km',     'numeric',  2,    6,    4,   1,    8, 1, NULL, 'haul_road', 'length_m',         '平均运距',           1),
    (3002, 302, 'road_max_slope_pct',    '最大坡度',     '%',      'numeric',  0,   10,    8,   0,   12, 1, NULL, 'haul_road', 'max_slope_pct',     '道路最大坡度',       2),
    (3003, 302, 'road_width',            '路宽',         'm',      'numeric',  30,  40,   36,  25,   50, 1, NULL, 'haul_road', 'road_width_m',      '路面宽度',           3),
    (3004, 302, 'cycle_time_min',        '单循环分钟',   'min',    'numeric',  15,  30,   20,  10,   45, 0, NULL, 'dispatch_rule', 'cycle_time_min', '装-运-卸-返循环时长', 4);

-- 运输 / 装车
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (3101, 301, 'bucket_loads_per_truck','装满铲数',     '铲',     'numeric',   3,   6,    4,   2,   8, 1, NULL, 'dispatch_rule', 'bucket_loads_per_truck', '几铲装满一车',  1),
    (3102, 301, 'recommended_truck_cnt','推荐配车数',   '辆',     'numeric',   3,   8,    5,   2,  12, 1, NULL, 'dispatch_rule', 'recommended_truck_count', '单铲推荐车数',  2);

-- 排土 / 外排
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (4001, 402, 'dump_max_height',       '最大堆高',     'm',      'numeric',  30,  60,   45,  20,   80, 1, NULL, 'dump_site', 'max_height_m',         '排土场最大堆高',     1),
    (4002, 402, 'dump_bench_height',     '单层堆高',     'm',      'numeric',  10,  15,   12,   8,   18, 1, NULL, 'dump_site', 'bench_height_m',       '单层排土高度',       2),
    (4003, 402, 'dump_slope_angle',      '排土场坡角',   '°',      'numeric',  30,  35,   32,  25,   40, 1, NULL, 'dump_site', 'overall_slope_angle_deg', '排土场整体坡角',  3),
    (4004, 402, 'dump_fill_rate',        '充填率',       '',       'numeric',   0,   1, 0.6,   0,    1, 0, NULL, NULL, NULL, '已堆/设计',                4);

-- 边坡 / 工作帮设计
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (5001, 501, 'working_slope_angle',   '工作帮坡角',   '°',      'numeric',  20,  35,   28,  15,   40, 1, NULL, 'slope_design', 'working_slope_angle_deg', '工作帮整体坡角', 1),
    (5002, 501, 'safety_factor_F',       '安全系数 F',   '',       'numeric',  1.30, 2.0, 1.35, 1.0, 3.0, 1, NULL, 'slope_design', 'safety_factor',           'F ≥ 1.30 安全', 2);

-- 边坡 / 最终帮设计
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (5101, 502, 'final_slope_angle',     '最终帮坡角',   '°',      'numeric',  35,  45,   42,  30,   50, 1, NULL, 'slope_design', 'final_slope_angle_deg', '最终帮整体坡角', 1),
    (5102, 502, 'max_depth',             '最大开采深度', 'm',      'numeric', 200, 400,  300, 100,  500, 0, NULL, 'slope_design', 'max_depth_m',           '矿坑最大深度',   2);

-- 经济 / 单价管理
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (7001, 702, 'diesel_unit_price',     '柴油单价',     '元/L',   'numeric',   5,   9,    7,   3,   12, 0, NULL, NULL, NULL, '柴油采购单价',     1),
    (7002, 702, 'electric_unit_price',   '电单价',       '元/kWh', 'numeric', 0.4, 0.8, 0.55, 0.2,   1, 0, NULL, NULL, NULL, '工业电价',         2),
    (7003, 702, 'explosive_unit_price',  '炸药单价',     '元/kg',  'numeric',   5,  12,    8,   3,   15, 0, NULL, NULL, NULL, '炸药采购单价',     3),
    (7004, 702, 'outsource_unit_price',  '外委单价',     '元/m³',  'numeric',   8,  18,   12,   5,   25, 0, NULL, NULL, NULL, '外委剥离单价',     4);

-- 安全 / 振动监测
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (8001, 801, 'vibration_velocity',    '振动速度',     'cm/s',   'numeric',   0,   3,    1,   0,    5, 1, NULL, NULL, NULL, '地震波速,< 3 安全', 1),
    (8002, 801, 'flying_rock_distance',  '飞石距离',     'm',      'numeric',   0, 200,  100,   0,  500, 0, NULL, NULL, NULL, '爆破飞石最远距',    2);

-- 安全 / 粉尘监测
REPLACE INTO parameter_definition
    (param_id, phase_id, code, name, "unit", value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (8101, 802, 'dust_concentration',    '粉尘浓度',     'mg/m³',  'numeric',   0,  10,    4,   0,   20, 1, NULL, NULL, NULL, '工作区呼吸粉尘',   1);
