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

CREATE INDEX IF NOT EXISTS idx_coal_audit_finding_seam_result_id
    ON coal_audit_finding(seam_result_id);

CREATE INDEX IF NOT EXISTS idx_dispatch_rule_shovel_model
    ON dispatch_rule(shovel_model);

CREATE INDEX IF NOT EXISTS idx_dispatch_rule_truck_model
    ON dispatch_rule(truck_model);

CREATE INDEX IF NOT EXISTS idx_dump_site_responsible_dozer_id
    ON dump_site(responsible_dozer_id);

-- equipment.model / operating_area: 设备台账按型号、按采区筛是最常用的两个查询
CREATE INDEX IF NOT EXISTS idx_equipment_model
    ON equipment(model);

CREATE INDEX IF NOT EXISTS idx_equipment_operating_area
    ON equipment(operating_area);

CREATE INDEX IF NOT EXISTS idx_haul_road_primary_truck_model
    ON haul_road(primary_truck_model);

CREATE INDEX IF NOT EXISTS idx_monthly_plan_shovel_equipment_id
    ON monthly_plan_shovel(equipment_id);

CREATE INDEX IF NOT EXISTS idx_monthly_plan_shovel_location_code
    ON monthly_plan_shovel(location_code);

CREATE INDEX IF NOT EXISTS idx_parameter_acceptance_equipment_id
    ON parameter_acceptance(equipment_id);

CREATE INDEX IF NOT EXISTS idx_parameter_acceptance_phase_id
    ON parameter_acceptance(phase_id);

CREATE INDEX IF NOT EXISTS idx_phase_location_binding_bound_template_id
    ON phase_location_binding(bound_template_id);
