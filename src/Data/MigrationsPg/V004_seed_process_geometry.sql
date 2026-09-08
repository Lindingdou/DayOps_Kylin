-- 由 build/sqlite2pg.py 从 V004_seed_process_geometry.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V004: 工艺几何种子数据
--
-- 预填:
--   · 平盘维表(mine_location)— 6 个常用平盘
--   · 工作面(working_face) — 5 个活跃工作面,关联到现有电铲 1748/1749/1734/3017/1740
--   · 排土场(dump_site) — 2 个(内排 + 外排)
--   · 边坡设计(slope_design) — 4 帮(东/西/南/北 + 工作帮)
--   · 道路网络(haul_road) — 6 段(主道+排土道+支道)
--
-- 数据来源:平朔安太堡矿 2023.12 实际生产配置 + 露天矿设计规范
-- =============================================================================

-- ─── 平盘维表 ───────────────────────────────────────────────────────────────
INSERT INTO mine_location (location_code, name, elevation_m, team, is_active) VALUES
    ('1180', '1180 平盘', 1180, 'team2',     1),
    ('1195', '1195 平盘', 1195, 'team2',     1),
    ('1210', '1210 平盘', 1210, 'team1',     1),
    ('1225', '1225 平盘', 1225, 'team1',     1),
    ('1240', '1240 平盘', 1240, 'team1',     1),
    ('1270', '1270 平盘', 1270, 'team1',     1);

-- ─── 工作面(5 个活跃)──────────────────────────────────────────────────────
INSERT INTO working_face
    (face_code, location_code, equipment_id, bench_height_m, bench_slope_angle_deg,
     working_platform_width_m, safety_platform_width_m, mining_width_m, face_length_m,
     advance_rate_m_per_month, material, rock_hardness, effective_from, status, notes) VALUES
    ('WF-1195-A', '1195', '1748', 15.0, 70, 45, 8, 50, 220, 45, 'rh', 'hard',
        '2023-01-01', 'active', '1748 主力工作面,4 煤顶板剥离'),
    ('WF-1210-A', '1210', '1749', 15.0, 70, 45, 8, 50, 240, 50, 'rh', 'hard',
        '2023-01-01', 'active', '1749 同型号工作面'),
    ('WF-1180-A', '1180', '1734', 12.0, 65, 35, 6, 40, 180, 30, 'rh', 'medium',
        '2023-01-01', 'active', 'PH2800 老电铲工作面,台阶降低适配设备'),
    ('WF-1240-A', '1240', '3017', 13.0, 68, 40, 8, 45, 200, 40, 'rh', 'hard',
        '2023-10-15', 'active', 'WK-35 新国产电铲,试生产工作面'),
    ('WF-1270-A', '1270', '1740', 15.0, 70, 45, 8, 50, 220, 45, 'rh', 'hard',
        '2023-06-01', 'active', '4100XPC 高位剥离工作面');

-- ─── 排土场(2 个)─────────────────────────────────────────────────────────
INSERT INTO dump_site
    (dump_id, name, dump_type, design_capacity_wan_m3, current_filled_wan_m3,
     max_height_m, bench_height_m, overall_slope_angle_deg, bench_slope_angle_deg,
     service_years_remaining, start_date, responsible_dozer_id, status, notes) VALUES
    ('D-N1', '北排土场', 'external', 12000, 8500, 60, 15, 32, 38, 8.5,
        '2010-05-01', 'D10N-01', 'active',
        '外排土场,主排土,容量充足'),
    ('D-I1', '内排土场', 'internal', 5000, 3200, 45, 12, 30, 36, 4.0,
        '2018-09-01', 'D10N-01', 'active',
        '内排土,减少运距,优先排土区');

-- ─── 边坡设计(4 帮)───────────────────────────────────────────────────────
INSERT INTO slope_design
    (side_name, side_type, working_slope_angle_deg, final_slope_angle_deg,
     max_depth_m, safety_factor, cohesion_kpa, friction_angle_deg, rock_type,
     groundwater_level_m, effective_from, design_version, designed_by, notes) VALUES
    ('东帮',   'final',     28, 42, 350, 1.35, 85, 35, '砂岩为主', -45,
        '2020-01-01', 'v2.1', '中煤设计院',
        '最终帮,处于稳定状态,2023 监测无明显位移'),
    ('西帮',   'working',   25, NULL, 280, 1.32, 70, 32, '砂岩+泥岩夹层', -38,
        '2022-06-01', 'v2.2', '中煤设计院',
        '工作帮,正在推进中,泥岩层需注意'),
    ('南帮',   'final',     28, 40, 320, 1.38, 90, 36, '砂岩', -50,
        '2020-01-01', 'v2.1', '中煤设计院',
        '最终帮,坡顶有排土场,需关注堆载效应'),
    ('北帮',   'working',   26, NULL, 300, 1.30, 75, 33, '砂岩为主', -42,
        '2022-06-01', 'v2.2', '中煤设计院',
        '工作帮,推进中');

-- ─── 道路网络(6 段)───────────────────────────────────────────────────────
INSERT INTO haul_road
    (road_id, name, road_type, start_location, end_location, length_m,
     max_slope_pct, avg_slope_pct, road_width_m, turning_radius_m,
     pavement_type, max_load_t, primary_truck_model, maintenance_team,
     last_maintenance_date, condition, notes) VALUES
    ('R-MAIN-01', '主运输大道',     'main',   '1195 平盘', '北排土场', 4800, 8, 6, 36, 35,
        'compacted', 290, '930E', '运修车间', '2023-11-15', 'good',
        '主干道,930E 大卡车通行'),
    ('R-MAIN-02', '南环运输线',     'main',   '1210 平盘', '北排土场', 5200, 8, 7, 36, 35,
        'compacted', 290, '930E', '运修车间', '2023-10-20', 'good',
        '南环线,夏雨季易积水段已铺压'),
    ('R-BR-01',   '1180 支线',     'branch', '1180 平盘', 'R-MAIN-01', 1200, 10, 8, 30, 30,
        'gravel',   172, '730E',  '运修车间', '2023-09-08', 'fair',
        '支线,主要服务 730E 卡车,坡度稍大'),
    ('R-BR-02',   '1240 上山线',   'branch', '1240 平盘', 'R-MAIN-02', 1800, 10, 9, 30, 30,
        'gravel',   172, '730E',  '运修车间', '2023-11-02', 'good',
        '上山支线'),
    ('R-DUMP-01', '排土进场道',     'dump',   '北排土场入口', '北排土场堆体', 900, 6, 4, 32, 40,
        'compacted', 290, '930E', '排土工区', '2023-12-01', 'good',
        '排土场内部道路'),
    ('R-DUMP-02', '内排土通道',     'dump',   '采坑', '内排土场', 1500, 8, 6, 30, 35,
        'gravel',   172, '730E',  '排土工区', '2023-10-12', 'fair',
        '内排土,运距短');
