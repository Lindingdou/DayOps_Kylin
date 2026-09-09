-- 由 build/sqlite2pg.py 从 V051_row_version.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

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
ALTER TABLE equipment ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE working_face ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE dump_site ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE slope_design ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE haul_road ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE borehole ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE borehole_seam_result ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE coal_sample ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE coal_observation_point ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE process_system ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE process_phase ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE parameter_definition ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE process_template ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE phase_location_binding ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE parameter_acceptance ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE mineable_region ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE load_unload_point ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE road_network ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE supplementary_borehole ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE supplementary_seam_horizon ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE supplementary_batch ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE current_state_batch ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE current_state_point ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE sink_profile ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE working_face_routing ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE seam_bench_param ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE dump_strip ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE maintenance_window ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE drill_plan ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE road_centerline_set ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;
ALTER TABLE process_zone ADD COLUMN row_version BIGINT NOT NULL DEFAULT 0;

-- ─── 2. 重建触发器: 除了刷新时间, 再把版本号 +1 ──────────────────────────
-- 先 DROP 再 CREATE: 原触发器只刷 updated_at, 不动版本号。

CREATE OR REPLACE FUNCTION fn_touch_row() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    NEW.row_version := OLD.row_version + 1;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_equipment_updated_at ON equipment;
CREATE TRIGGER trg_equipment_updated_at
BEFORE UPDATE ON equipment
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_working_face_updated_at ON working_face;
CREATE TRIGGER trg_working_face_updated_at
BEFORE UPDATE ON working_face
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_dump_site_updated_at ON dump_site;
CREATE TRIGGER trg_dump_site_updated_at
BEFORE UPDATE ON dump_site
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_slope_design_updated_at ON slope_design;
CREATE TRIGGER trg_slope_design_updated_at
BEFORE UPDATE ON slope_design
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_haul_road_updated_at ON haul_road;
CREATE TRIGGER trg_haul_road_updated_at
BEFORE UPDATE ON haul_road
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_borehole_updated_at ON borehole;
CREATE TRIGGER trg_borehole_updated_at
BEFORE UPDATE ON borehole
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_bsr_updated_at ON borehole_seam_result;
CREATE TRIGGER trg_bsr_updated_at
BEFORE UPDATE ON borehole_seam_result
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_cs_updated_at ON coal_sample;
CREATE TRIGGER trg_cs_updated_at
BEFORE UPDATE ON coal_sample
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_cop_updated_at ON coal_observation_point;
CREATE TRIGGER trg_cop_updated_at
BEFORE UPDATE ON coal_observation_point
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_process_system_updated_at ON process_system;
CREATE TRIGGER trg_process_system_updated_at
BEFORE UPDATE ON process_system
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_process_phase_updated_at ON process_phase;
CREATE TRIGGER trg_process_phase_updated_at
BEFORE UPDATE ON process_phase
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_parameter_definition_updated_at ON parameter_definition;
CREATE TRIGGER trg_parameter_definition_updated_at
BEFORE UPDATE ON parameter_definition
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_process_template_updated_at ON process_template;
CREATE TRIGGER trg_process_template_updated_at
BEFORE UPDATE ON process_template
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_phase_location_binding_updated_at ON phase_location_binding;
CREATE TRIGGER trg_phase_location_binding_updated_at
BEFORE UPDATE ON phase_location_binding
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_parameter_acceptance_updated_at ON parameter_acceptance;
CREATE TRIGGER trg_parameter_acceptance_updated_at
BEFORE UPDATE ON parameter_acceptance
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_mineable_region_updated_at ON mineable_region;
CREATE TRIGGER trg_mineable_region_updated_at
BEFORE UPDATE ON mineable_region
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_load_unload_point_updated_at ON load_unload_point;
CREATE TRIGGER trg_load_unload_point_updated_at
BEFORE UPDATE ON load_unload_point
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_road_network_updated_at ON road_network;
CREATE TRIGGER trg_road_network_updated_at
BEFORE UPDATE ON road_network
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_supbh_updated_at ON supplementary_borehole;
CREATE TRIGGER trg_supbh_updated_at
BEFORE UPDATE ON supplementary_borehole
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_supsh_updated_at ON supplementary_seam_horizon;
CREATE TRIGGER trg_supsh_updated_at
BEFORE UPDATE ON supplementary_seam_horizon
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_supbatch_updated_at ON supplementary_batch;
CREATE TRIGGER trg_supbatch_updated_at
BEFORE UPDATE ON supplementary_batch
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_csbatch_updated_at ON current_state_batch;
CREATE TRIGGER trg_csbatch_updated_at
BEFORE UPDATE ON current_state_batch
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_cspoint_updated_at ON current_state_point;
CREATE TRIGGER trg_cspoint_updated_at
BEFORE UPDATE ON current_state_point
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_sink_profile_updated_at ON sink_profile;
CREATE TRIGGER trg_sink_profile_updated_at
BEFORE UPDATE ON sink_profile
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_working_face_routing_updated_at ON working_face_routing;
CREATE TRIGGER trg_working_face_routing_updated_at
BEFORE UPDATE ON working_face_routing
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_seam_bench_param_updated_at ON seam_bench_param;
CREATE TRIGGER trg_seam_bench_param_updated_at
BEFORE UPDATE ON seam_bench_param
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_dump_strip_updated_at ON dump_strip;
CREATE TRIGGER trg_dump_strip_updated_at
BEFORE UPDATE ON dump_strip
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_maintenance_window_updated_at ON maintenance_window;
CREATE TRIGGER trg_maintenance_window_updated_at
BEFORE UPDATE ON maintenance_window
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_drill_plan_updated_at ON drill_plan;
CREATE TRIGGER trg_drill_plan_updated_at
BEFORE UPDATE ON drill_plan
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_road_centerline_set_updated_at ON road_centerline_set;
CREATE TRIGGER trg_road_centerline_set_updated_at
BEFORE UPDATE ON road_centerline_set
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();

DROP TRIGGER IF EXISTS trg_process_zone_updated_at ON process_zone;
CREATE TRIGGER trg_process_zone_updated_at
BEFORE UPDATE ON process_zone
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_row();
