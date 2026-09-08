-- 由 build/sqlite2pg.py 从 V052_fk_indexes.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- 外键列补索引
-- 版本: V052
--
-- 【为什么】SQLite 和 PostgreSQL/openGauss 都**不会**给外键列自动建索引(MySQL 才会)。
-- 全库 45 个外键列里有 12 个没被任何索引覆盖，代价有两处：
--   1. 按父表查子表(WHERE equipment_id = ?)要全表扫；
--   2. 父表行删除/更新时，数据库必须扫全子表来检查约束 —— 这些外键多数带
--      ON DELETE CASCADE / ON UPDATE CASCADE，删一台设备就要扫遍所有引用它的表。
--
-- 库还在本机 SQLite 时这点开销看不出来。改成局域网内的共享 openGauss 之后，
-- 全表扫的代价被网络和并发放大，而且会持锁更久、影响其他客户端。
--
-- 【为什么只补这 12 个】审计的是"外键列是否为某个索引的最左列"。
-- 已被主键、唯一键或既有索引覆盖的不重复建；复合外键 (year, month) 已由
-- idx_plan_shovel_ym(year, month) 覆盖，不在此列。
--
-- 代价：写入略慢、占用略增。本库总量 4 万行量级，可忽略。
-- =============================================================================

CREATE INDEX idx_coal_audit_finding_seam_result_id
    ON coal_audit_finding(seam_result_id);

CREATE INDEX idx_dispatch_rule_shovel_model
    ON dispatch_rule(shovel_model);

CREATE INDEX idx_dispatch_rule_truck_model
    ON dispatch_rule(truck_model);

CREATE INDEX idx_dump_site_responsible_dozer_id
    ON dump_site(responsible_dozer_id);

-- equipment.model / operating_area: 设备台账按型号、按采区筛是最常用的两个查询
CREATE INDEX idx_equipment_model
    ON equipment(model);

CREATE INDEX idx_equipment_operating_area
    ON equipment(operating_area);

CREATE INDEX idx_haul_road_primary_truck_model
    ON haul_road(primary_truck_model);

CREATE INDEX idx_monthly_plan_shovel_equipment_id
    ON monthly_plan_shovel(equipment_id);

CREATE INDEX idx_monthly_plan_shovel_location_code
    ON monthly_plan_shovel(location_code);

CREATE INDEX idx_parameter_acceptance_equipment_id
    ON parameter_acceptance(equipment_id);

CREATE INDEX idx_parameter_acceptance_phase_id
    ON parameter_acceptance(phase_id);

CREATE INDEX idx_phase_location_binding_bound_template_id
    ON phase_location_binding(bound_template_id);

-- ─── 序列对齐 ────────────────────────────────────────────────────────────
-- 种子数据是带显式主键插入的, SERIAL 的序列不会自动前进;
-- 不在这里对齐, 之后第一条自增插入就会撞主键。
SELECT setval(pg_get_serial_sequence('blast_event','id'), COALESCE((SELECT MAX(id) FROM blast_event), 1));
SELECT setval(pg_get_serial_sequence('borehole','id'), COALESCE((SELECT MAX(id) FROM borehole), 1));
SELECT setval(pg_get_serial_sequence('borehole_lithology_segment','id'), COALESCE((SELECT MAX(id) FROM borehole_lithology_segment), 1));
SELECT setval(pg_get_serial_sequence('borehole_seam_result','id'), COALESCE((SELECT MAX(id) FROM borehole_seam_result), 1));
SELECT setval(pg_get_serial_sequence('coal_audit_finding','id'), COALESCE((SELECT MAX(id) FROM coal_audit_finding), 1));
SELECT setval(pg_get_serial_sequence('coal_grade_rule','id'), COALESCE((SELECT MAX(id) FROM coal_grade_rule), 1));
SELECT setval(pg_get_serial_sequence('coal_observation_point','id'), COALESCE((SELECT MAX(id) FROM coal_observation_point), 1));
SELECT setval(pg_get_serial_sequence('coal_sample','id'), COALESCE((SELECT MAX(id) FROM coal_sample), 1));
SELECT setval(pg_get_serial_sequence('coal_sample_summary','id'), COALESCE((SELECT MAX(id) FROM coal_sample_summary), 1));
SELECT setval(pg_get_serial_sequence('coal_seam_def','id'), COALESCE((SELECT MAX(id) FROM coal_seam_def), 1));
SELECT setval(pg_get_serial_sequence('current_state_batch','id'), COALESCE((SELECT MAX(id) FROM current_state_batch), 1));
SELECT setval(pg_get_serial_sequence('current_state_point','id'), COALESCE((SELECT MAX(id) FROM current_state_point), 1));
SELECT setval(pg_get_serial_sequence('dispatch_rule','id'), COALESCE((SELECT MAX(id) FROM dispatch_rule), 1));
SELECT setval(pg_get_serial_sequence('dump_strip','id'), COALESCE((SELECT MAX(id) FROM dump_strip), 1));
SELECT setval(pg_get_serial_sequence('equipment_constraint','id'), COALESCE((SELECT MAX(id) FROM equipment_constraint), 1));
SELECT setval(pg_get_serial_sequence('fault_event','id'), COALESCE((SELECT MAX(id) FROM fault_event), 1));
SELECT setval(pg_get_serial_sequence('load_unload_point','id'), COALESCE((SELECT MAX(id) FROM load_unload_point), 1));
SELECT setval(pg_get_serial_sequence('long_term_metric','id'), COALESCE((SELECT MAX(id) FROM long_term_metric), 1));
SELECT setval(pg_get_serial_sequence('mineable_region','id'), COALESCE((SELECT MAX(id) FROM mineable_region), 1));
SELECT setval(pg_get_serial_sequence('monthly_plan_shovel','id'), COALESCE((SELECT MAX(id) FROM monthly_plan_shovel), 1));
SELECT setval(pg_get_serial_sequence('parameter_acceptance','id'), COALESCE((SELECT MAX(id) FROM parameter_acceptance), 1));
SELECT setval(pg_get_serial_sequence('parameter_definition','param_id'), COALESCE((SELECT MAX(param_id) FROM parameter_definition), 1));
SELECT setval(pg_get_serial_sequence('phase_location_binding','id'), COALESCE((SELECT MAX(id) FROM phase_location_binding), 1));
SELECT setval(pg_get_serial_sequence('process_phase','phase_id'), COALESCE((SELECT MAX(phase_id) FROM process_phase), 1));
SELECT setval(pg_get_serial_sequence('process_system','system_id'), COALESCE((SELECT MAX(system_id) FROM process_system), 1));
SELECT setval(pg_get_serial_sequence('process_template','template_id'), COALESCE((SELECT MAX(template_id) FROM process_template), 1));
SELECT setval(pg_get_serial_sequence('process_zone','id'), COALESCE((SELECT MAX(id) FROM process_zone), 1));
SELECT setval(pg_get_serial_sequence('production_record','id'), COALESCE((SELECT MAX(id) FROM production_record), 1));
SELECT setval(pg_get_serial_sequence('road_centerline_set','id'), COALESCE((SELECT MAX(id) FROM road_centerline_set), 1));
SELECT setval(pg_get_serial_sequence('road_network','id'), COALESCE((SELECT MAX(id) FROM road_network), 1));
SELECT setval(pg_get_serial_sequence('seam_bench_param','id'), COALESCE((SELECT MAX(id) FROM seam_bench_param), 1));
SELECT setval(pg_get_serial_sequence('sink_stocktake','id'), COALESCE((SELECT MAX(id) FROM sink_stocktake), 1));
SELECT setval(pg_get_serial_sequence('slope_design','id'), COALESCE((SELECT MAX(id) FROM slope_design), 1));
SELECT setval(pg_get_serial_sequence('supplementary_batch','id'), COALESCE((SELECT MAX(id) FROM supplementary_batch), 1));
SELECT setval(pg_get_serial_sequence('supplementary_borehole','id'), COALESCE((SELECT MAX(id) FROM supplementary_borehole), 1));
SELECT setval(pg_get_serial_sequence('supplementary_seam_horizon','id'), COALESCE((SELECT MAX(id) FROM supplementary_seam_horizon), 1));
SELECT setval(pg_get_serial_sequence('template_param_value','id'), COALESCE((SELECT MAX(id) FROM template_param_value), 1));
SELECT setval(pg_get_serial_sequence('virtual_drill_surface','id'), COALESCE((SELECT MAX(id) FROM virtual_drill_surface), 1));
SELECT setval(pg_get_serial_sequence('working_face','id'), COALESCE((SELECT MAX(id) FROM working_face), 1));
