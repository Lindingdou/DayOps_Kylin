-- 由 build/sqlite2pg.py 从 V027_purge_params_absent_from_chushe.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V027: 按《东露天矿初步设计说明书》核对工艺参数 —— 初设无则删、有则对齐值
--
-- 规则(用户指令):逐条核对参数是否见于初设;初设未涉及者删除,
-- 初设有者其值须与初设一致。设备台账/产能/KPI 为2026实际数据,不在本次范围。
--
-- 主要新增依据:第6章第3节 表6-3-1「穿爆参数表」(岩石台阶/采煤台阶)、
-- 表6-3-2 爆破材料单耗量、第3章 表3-1-2(最大开采深度270m)、平均运距3.8km、
-- 满斗系数0.88(第5章设备选型)。
-- 全部 UPDATE/DELETE 幂等;DELETE 级联清除对应 template_param_value 与 parameter_acceptance。
-- =============================================================================

-- ── A. 对齐初设值(补 V026 未覆盖 / 修正来源为单耗分析的值) ────────────────
-- 表6-3-1 穿爆参数(岩石台阶为主):孔深16.5、孔距8.0、行距(排距)9.0、超深1.5、单耗0.5
UPDATE parameter_definition SET standard_min=5, standard_max=18, standard_default=16.5,
       alarm_low=4, alarm_high=20, description='岩石台阶孔深16.5m/采煤5~15m(初设表6-3-1)'
WHERE code='hole_depth';
UPDATE parameter_definition SET standard_min=6, standard_max=9, standard_default=8.0,
       alarm_low=4, alarm_high=10, description='孔距 岩8.0m/煤6~7m(初设表6-3-1)'
WHERE code='hole_spacing';
UPDATE parameter_definition SET standard_min=6, standard_max=9, standard_default=9.0,
       alarm_low=3, alarm_high=10, description='行距(排距) 岩9.0m/煤7.0m(初设表6-3-1)'
WHERE code='row_spacing';
UPDATE parameter_definition SET standard_min=0, standard_max=2, standard_default=1.5,
       alarm_low=0, alarm_high=2.5, description='超深 岩1.5m/煤0(初设表6-3-1)'
WHERE code='subdrill_depth';
UPDATE parameter_definition SET standard_min=0.24, standard_max=0.55, standard_default=0.5,
       alarm_low=0.2, alarm_high=0.7, description='炸药单耗 岩0.5(半连续)/煤0.24 kg/m³(初设表6-3-1/6-3-2)'
WHERE code='unit_consumption';
-- 平均运距3.8km(初设);替换此前取自单耗分析的3.0
UPDATE parameter_definition SET standard_default=3.8,
       description='设计平均运距3.8km(初设第6章)'
WHERE code='haul_distance_km';
-- 装满系数(满斗系数)0.88
UPDATE parameter_definition SET standard_min=0.85, standard_max=0.95, standard_default=0.88,
       description='满斗系数0.85~0.95,取0.88(初设第5章设备选型)'
WHERE code='bucket_fill_factor';
-- 最大开采深度270m(表3-1-2)
UPDATE parameter_definition SET standard_min=200, standard_max=400, standard_default=270,
       description='最大开采深度270m(初设表3-1-2)'
WHERE code='max_depth';
-- 文本/枚举类(初设有,记录设计取值于说明)
UPDATE parameter_definition SET description='主炸药多孔粒状铵油炸药,起爆药2号岩石炸药,水孔用乳化炸药(初设第6章)'
WHERE code='explosive_type';
UPDATE parameter_definition SET description='非电起爆系统,行间多排孔毫秒微差,前排至后排依次起爆(初设第6章)'
WHERE code='detonation_method';
UPDATE parameter_definition SET description='多排垂直深孔,钻孔方向90°(垂直),0=垂直(初设表6-3-1)'
WHERE code='hole_inclination';

-- 同步模板推荐值(硬岩t1/4煤t2=岩石值;煤层t3=采煤值)
UPDATE template_param_value SET recommended_value=16.5, min_value=15, max_value=18 WHERE param_id=1002;
UPDATE template_param_value SET recommended_value=8.0, min_value=6, max_value=9 WHERE param_id=1003;
UPDATE template_param_value SET recommended_value=9.0, min_value=7, max_value=9 WHERE param_id=1004;
UPDATE template_param_value SET recommended_value=1.5, min_value=1, max_value=2 WHERE param_id=1005;
UPDATE template_param_value SET recommended_value=0.5 WHERE param_id=1301;
-- 煤层模板(soft)取采煤台阶值
UPDATE template_param_value SET recommended_value=15 WHERE param_id=1002
  AND template_id IN (SELECT template_id FROM process_template WHERE applicable_hardness='soft');
UPDATE template_param_value SET recommended_value=7.0 WHERE param_id=1003
  AND template_id IN (SELECT template_id FROM process_template WHERE applicable_hardness='soft');
UPDATE template_param_value SET recommended_value=7.0 WHERE param_id=1004
  AND template_id IN (SELECT template_id FROM process_template WHERE applicable_hardness='soft');
UPDATE template_param_value SET recommended_value=0, min_value=0, max_value=0 WHERE param_id=1005
  AND template_id IN (SELECT template_id FROM process_template WHERE applicable_hardness='soft');
UPDATE template_param_value SET recommended_value=0.24 WHERE param_id=1301
  AND template_id IN (SELECT template_id FROM process_template WHERE applicable_hardness='soft');

-- ── B. 删除初设未涉及的参数(级联清除模板值与验收记录) ─────────────────────
-- 穿爆操作/衍生参数(初设表6-3-1未列):单炮孔数/装药密度/堵塞长度/总装药量/雷管延期/
--   爆破方量/大块率/根底率
-- 采装/运输衍生(初设无设计值):电铲循环时间/工作面长度(初设只有工作线长度,概念不同)/
--   单循环分钟/装满铲数/推荐配车数
-- 排土:充填率
-- 经济单价(非工艺设计参数,初设仅有投资估算):柴油/电/炸药/外委单价
-- 安全环保监测(初设无设计阈值):振动速度/飞石距离/粉尘浓度
DELETE FROM parameter_definition WHERE code IN (
    'hole_count', 'charge_density', 'stemming_length', 'charge_total_kg', 'delay_ms',
    'blast_volume', 'oversized_rate_pct', 'toe_rate_pct',
    'shovel_cycle_time', 'face_length', 'cycle_time_min',
    'bucket_loads_per_truck', 'recommended_truck_cnt', 'dump_fill_rate',
    'diesel_unit_price', 'electric_unit_price', 'explosive_unit_price', 'outsource_unit_price',
    'vibration_velocity', 'flying_rock_distance', 'dust_concentration'
);
