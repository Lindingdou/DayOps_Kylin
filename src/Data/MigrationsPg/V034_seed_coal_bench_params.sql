-- 由 build/sqlite2pg.py 从 V034_seed_coal_bench_params.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- pmgeo.db 煤台阶几何参数(煤岩分层放坡)
-- 版本: V034
-- 说明: 放坡到煤层里那几级用的是【煤台阶】参数,和上覆【岩台阶】不是一套 ——
--       煤比岩软、可采厚度受煤层控制,H/α/平盘都另有取值。
--       原先 bench_height / bench_slope_angle / safety_platform_width 只有一套,
--       描述里混着"黄土10m/基岩15m/采煤≤15m""松散层60°/岩石65°/煤65°"(见 V026),
--       实际用的时候没法按岩性分开取。这里把煤台阶那套单独立成指标。
--
--       岩台阶继续用原来的 bench_height / bench_slope_angle / safety_platform_width;
--       煤台阶用这里新增的 coal_* 三项。批量扩坑逐级判在顶板上还是下,自动切换。
-- 取值: 初设5-4 —— 采煤台阶高度≤15m(与基岩同级,便于同一套设备),煤层坡面角 65°,
--       煤台阶安全平台按岩石台阶同档 6m(煤帮稳定性略优于松散层,但按保安要求不低于 6m)。
-- 备注: phase_id=201(采装/工作面规划),与 bench_height 等同组,便于模板编辑器一屏取全。
-- =============================================================================

INSERT INTO parameter_definition
    (param_id, phase_id, code, name, unit, value_type,
     standard_min, standard_max, standard_default, alarm_low, alarm_high, is_required,
     calc_formula, source_table, source_column, description, display_order) VALUES
    (2007, 201, 'coal_bench_height',      '煤台阶高度',   'm', 'numeric',  5, 15, 15,  3, 20, 0, NULL, NULL, NULL,
        '采煤台阶垂直高度(初设5-4 采煤≤15m);煤层放坡专用,岩台阶走 bench_height',              7),
    (2008, 201, 'coal_bench_slope_angle', '煤台阶坡面角', '°', 'numeric', 60, 70, 65, 55, 80, 0, NULL, NULL, NULL,
        '采煤台阶面与水平夹角(初设5-4 煤65°);煤层放坡专用,岩台阶走 bench_slope_angle',        8),
    (2009, 201, 'coal_platform_width',    '煤台阶平盘宽', 'm', 'numeric',  6, 12,  6,  5, 15, 0, NULL, NULL, NULL,
        '采煤台阶平盘净宽(按保安平台不低于 6m);煤层放坡专用,岩台阶走 safety_platform_width', 9);
