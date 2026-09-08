-- =============================================================================
-- 行版本号 row_version —— 多客户端共享一个数据库时的乐观锁基础
-- 版本: V051
--
-- 背景: 程序原本是单机 + 嵌入式 SQLite, 一人一个库文件, 不存在并发写。
-- 改成局域网内"一个库 + 多客户端"之后, 两个人同时改同一条记录, 后保存的会
-- 直接覆盖先保存的, 且双方都不会收到任何提示 —— 数据就这么没了。
--
-- 为什么不直接用 updated_at 当版本令牌: SQLite 的 CURRENT_TIMESTAMP 只精确到秒,
-- 同一秒内的两次修改看起来时间戳一样, 检测不出冲突。row_version 单调自增,
-- 与方言和时钟精度都无关, 也因此能在 SQLite 上写出可靠的单测。
--
-- 用法: 读取时把 row_version 一并读出, 更新时带 WHERE ... AND row_version=<读到的值>;
-- 影响行数为 0 即说明这条记录已被他人改动, 由上层提示用户。
-- =============================================================================

-- ─── 1. 加列(默认 0, 既有行不受影响)─────────────────────────────────────
ALTER TABLE equipment ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE working_face ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE dump_site ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE slope_design ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE haul_road ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE borehole ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE borehole_seam_result ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE coal_sample ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE coal_observation_point ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE process_system ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE process_phase ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE parameter_definition ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE process_template ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE phase_location_binding ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE parameter_acceptance ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE mineable_region ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE load_unload_point ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE road_network ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE supplementary_borehole ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE supplementary_seam_horizon ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE supplementary_batch ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE current_state_batch ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE current_state_point ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sink_profile ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE working_face_routing ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE seam_bench_param ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE dump_strip ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE maintenance_window ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE drill_plan ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE road_centerline_set ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE process_zone ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;

-- ─── 2. 重建触发器: 除了刷新时间, 再把版本号 +1 ──────────────────────────
-- 先 DROP 再 CREATE: 原触发器只刷 updated_at, 不动版本号。

DROP TRIGGER IF EXISTS trg_equipment_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_equipment_updated_at
AFTER UPDATE ON equipment
FOR EACH ROW
BEGIN
    UPDATE equipment SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE equipment_id = NEW.equipment_id;
END;

DROP TRIGGER IF EXISTS trg_working_face_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_working_face_updated_at
AFTER UPDATE ON working_face
FOR EACH ROW
BEGIN
    UPDATE working_face SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_dump_site_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_dump_site_updated_at
AFTER UPDATE ON dump_site
FOR EACH ROW
BEGIN
    UPDATE dump_site SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE dump_id = NEW.dump_id;
END;

DROP TRIGGER IF EXISTS trg_slope_design_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_slope_design_updated_at
AFTER UPDATE ON slope_design
FOR EACH ROW
BEGIN
    UPDATE slope_design SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_haul_road_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_haul_road_updated_at
AFTER UPDATE ON haul_road
FOR EACH ROW
BEGIN
    UPDATE haul_road SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE road_id = NEW.road_id;
END;

DROP TRIGGER IF EXISTS trg_borehole_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_borehole_updated_at
AFTER UPDATE ON borehole
FOR EACH ROW
BEGIN
    UPDATE borehole SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_bsr_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_bsr_updated_at
AFTER UPDATE ON borehole_seam_result
FOR EACH ROW
BEGIN
    UPDATE borehole_seam_result SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_cs_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_cs_updated_at
AFTER UPDATE ON coal_sample
FOR EACH ROW
BEGIN
    UPDATE coal_sample SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_cop_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_cop_updated_at
AFTER UPDATE ON coal_observation_point
FOR EACH ROW
BEGIN
    UPDATE coal_observation_point SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_process_system_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_process_system_updated_at
AFTER UPDATE ON process_system
FOR EACH ROW
BEGIN
    UPDATE process_system SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE system_id = NEW.system_id;
END;

DROP TRIGGER IF EXISTS trg_process_phase_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_process_phase_updated_at
AFTER UPDATE ON process_phase
FOR EACH ROW
BEGIN
    UPDATE process_phase SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE phase_id = NEW.phase_id;
END;

DROP TRIGGER IF EXISTS trg_parameter_definition_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_parameter_definition_updated_at
AFTER UPDATE ON parameter_definition
FOR EACH ROW
BEGIN
    UPDATE parameter_definition SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE param_id = NEW.param_id;
END;

DROP TRIGGER IF EXISTS trg_process_template_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_process_template_updated_at
AFTER UPDATE ON process_template
FOR EACH ROW
BEGIN
    UPDATE process_template SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE template_id = NEW.template_id;
END;

DROP TRIGGER IF EXISTS trg_phase_location_binding_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_phase_location_binding_updated_at
AFTER UPDATE ON phase_location_binding
FOR EACH ROW
BEGIN
    UPDATE phase_location_binding SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_parameter_acceptance_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_parameter_acceptance_updated_at
AFTER UPDATE ON parameter_acceptance
FOR EACH ROW
BEGIN
    UPDATE parameter_acceptance SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_mineable_region_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_mineable_region_updated_at
AFTER UPDATE ON mineable_region
FOR EACH ROW
BEGIN
    UPDATE mineable_region SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_load_unload_point_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_load_unload_point_updated_at
AFTER UPDATE ON load_unload_point
FOR EACH ROW
BEGIN
    UPDATE load_unload_point SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_road_network_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_road_network_updated_at
AFTER UPDATE ON road_network
FOR EACH ROW
BEGIN
    UPDATE road_network SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_supbh_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_supbh_updated_at
AFTER UPDATE ON supplementary_borehole
FOR EACH ROW
BEGIN
    UPDATE supplementary_borehole SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_supsh_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_supsh_updated_at
AFTER UPDATE ON supplementary_seam_horizon
FOR EACH ROW
BEGIN
    UPDATE supplementary_seam_horizon SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_supbatch_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_supbatch_updated_at
AFTER UPDATE ON supplementary_batch
FOR EACH ROW
BEGIN
    UPDATE supplementary_batch SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_csbatch_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_csbatch_updated_at
AFTER UPDATE ON current_state_batch
FOR EACH ROW
BEGIN
    UPDATE current_state_batch SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_cspoint_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_cspoint_updated_at
AFTER UPDATE ON current_state_point
FOR EACH ROW
BEGIN
    UPDATE current_state_point SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_sink_profile_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_sink_profile_updated_at
AFTER UPDATE ON sink_profile
FOR EACH ROW
BEGIN
    UPDATE sink_profile SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE sink_id = NEW.sink_id;
END;

DROP TRIGGER IF EXISTS trg_working_face_routing_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_working_face_routing_updated_at
AFTER UPDATE ON working_face_routing
FOR EACH ROW
BEGIN
    UPDATE working_face_routing SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE face_code = NEW.face_code;
END;

DROP TRIGGER IF EXISTS trg_seam_bench_param_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_seam_bench_param_updated_at
AFTER UPDATE ON seam_bench_param
FOR EACH ROW
BEGIN
    UPDATE seam_bench_param SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_dump_strip_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_dump_strip_updated_at
AFTER UPDATE ON dump_strip
FOR EACH ROW
BEGIN
    UPDATE dump_strip SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_maintenance_window_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_maintenance_window_updated_at
AFTER UPDATE ON maintenance_window
FOR EACH ROW
BEGIN
    UPDATE maintenance_window SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE equipment_id = NEW.equipment_id AND plan_date = NEW.plan_date AND start_time = NEW.start_time;
END;

DROP TRIGGER IF EXISTS trg_drill_plan_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_drill_plan_updated_at
AFTER UPDATE ON drill_plan
FOR EACH ROW
BEGIN
    UPDATE drill_plan SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE equipment_id = NEW.equipment_id AND plan_date = NEW.plan_date AND start_time = NEW.start_time;
END;

DROP TRIGGER IF EXISTS trg_road_centerline_set_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_road_centerline_set_updated_at
AFTER UPDATE ON road_centerline_set
FOR EACH ROW
BEGIN
    UPDATE road_centerline_set SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;

DROP TRIGGER IF EXISTS trg_process_zone_updated_at;
CREATE TRIGGER IF NOT EXISTS trg_process_zone_updated_at
AFTER UPDATE ON process_zone
FOR EACH ROW
BEGIN
    UPDATE process_zone SET updated_at = CURRENT_TIMESTAMP, row_version = NEW.row_version + 1 WHERE id = NEW.id;
END;
