-- =============================================================================
-- V026: 用《东露天矿初步设计说明书》真实设计参数覆盖工艺参数占位值
--
-- 来源:东露天矿初步设计说明书(第2/5/8/9章)+ 单耗分析实测。
-- 覆盖 parameter_definition 的标准值/范围/报警阈,并同步 template_param_value 推荐值,
-- 使「工艺架构定义 / 参数模板库 / 平盘工艺地图 / 现场验收」均反映东露天真实设计。
-- 全部 UPDATE,幂等可重跑。
-- 范围/阈值同步放宽,保证真实设计值不被误判为 warning/fail。
-- =============================================================================

-- ── 穿爆系统 · 钻孔(第5章第2节 穿爆设备选型) ──────────────────────────────
-- 孔径:剥离牙轮钻机 Φ250mm(岩),采煤 Φ150mm
UPDATE parameter_definition SET standard_min=150, standard_max=310, standard_default=250,
       alarm_low=120, alarm_high=350, description='剥离牙轮钻机Φ250mm(岩)/采煤Φ150mm(初设5-2)'
WHERE code='hole_diameter';
-- 孔深:剥离钻孔深度 17.5m(台阶15m+超深)
UPDATE parameter_definition SET standard_min=10, standard_max=20, standard_default=17.5,
       alarm_low=8, alarm_high=22, description='剥离钻孔深度17.5m,台阶高度+超钻(初设5-2)'
WHERE code='hole_depth';

-- ── 穿爆系统 · 爆破效果验收 ────────────────────────────────────────────────
-- 炸药单耗:东露天实测 ≈0.48 kg/m³(单耗分析)
UPDATE parameter_definition SET standard_default=0.48,
       description='东露天炸药单耗实测≈0.48 kg/m³(2026单耗分析)'
WHERE code='unit_consumption';

-- ── 采装系统 · 工作面规划(第5章第4节 开采参数) ───────────────────────────
-- 台阶高度:黄土10m,基岩/半连续15m,采煤≤15m
UPDATE parameter_definition SET standard_min=10, standard_max=15, standard_default=15,
       alarm_low=8, alarm_high=18, description='黄土10m/基岩·半连续15m/采煤≤15m(初设5-4)'
WHERE code='bench_height';
-- 台阶坡面角:松散层60°,岩石65°,煤65°
UPDATE parameter_definition SET standard_min=60, standard_max=70, standard_default=65,
       alarm_low=55, alarm_high=80, description='松散层60°/岩石65°/煤65°(初设5-4 台阶坡面角α)'
WHERE code='bench_slope_angle';
-- 安全平台宽:岩石台阶6m,黄土台阶8m
UPDATE parameter_definition SET standard_min=6, standard_max=12, standard_default=8,
       description='岩石保安平台6m/黄土保安平台8m(初设5-4 端帮平台)'
WHERE code='safety_platform_width';
-- 工作平台宽:半连续工艺最小工作平盘宽度 80m
UPDATE parameter_definition SET standard_min=40, standard_max=90, standard_default=80,
       alarm_low=25, alarm_high=120, description='半连续工艺最小工作平盘宽度80m(初设5-4)'
WHERE code='working_platform_width';
-- 采宽(采掘带宽度):40m
UPDATE parameter_definition SET standard_min=30, standard_max=50, standard_default=40,
       description='采掘带宽度40m(初设5-4)'
WHERE code='mining_width';

-- ── 采装系统 · 剥离作业 ────────────────────────────────────────────────────
-- 月推进度:工作帮年推进度≈260 m/a → ≈22 m/月(半连续主导)
UPDATE parameter_definition SET standard_min=15, standard_max=60, standard_default=22,
       alarm_low=10, description='工作帮年推进度260m/a≈22m/月(半连续,初设5-4);单斗卡车29-43m/月'
WHERE code='advance_rate';

-- ── 运输系统 · 主道运输(第8章第2节 矿山道路技术条件,矿山二级道路·300t卡车) ─
-- 运距:东露天自营采剥实测综合运距≈3.0 km
UPDATE parameter_definition SET standard_default=3.0,
       description='东露天自营采剥综合运距≈3.0km(2026单耗分析)'
WHERE code='haul_distance_km';
-- 最大坡度:最大纵坡 8%
UPDATE parameter_definition SET standard_default=8,
       description='矿山道路最大纵坡8%(初设8-2)'
WHERE code='road_max_slope_pct';
-- 路宽:路面宽度25m,路基宽度30m
UPDATE parameter_definition SET standard_min=25, standard_max=40, standard_default=30,
       description='路面宽度25m/路基宽度30m(初设8-2)'
WHERE code='road_width';

-- ── 排土系统 · 外排作业(第9章第4节 排弃参数) ─────────────────────────────
-- 单层堆高(排土台阶高度):卡车-推土机 30~40m
UPDATE parameter_definition SET standard_min=20, standard_max=40, standard_default=30,
       alarm_low=15, alarm_high=50, description='内排卡车-推土机排土台阶高度30~40m(初设9-4)'
WHERE code='dump_bench_height';
-- 最大堆高(总排弃高度):120~150m
UPDATE parameter_definition SET standard_min=50, standard_max=150, standard_default=120,
       alarm_low=40, alarm_high=180, description='总排弃高度120~130m,外排边坡最大150m(初设9)'
WHERE code='dump_max_height';
-- 排土场坡角(排土台阶坡面角):35°
UPDATE parameter_definition SET standard_min=30, standard_max=35, standard_default=35,
       description='内排土场排土台阶坡面角35°,最终帮坡角22°(初设9-4)'
WHERE code='dump_slope_angle';

-- ── 边坡管理 · 工作帮设计(第5章第4节 帮坡角 / 第2章边坡稳定) ────────────────
-- 工作帮坡角:正常生产≈10°(半连续宽平盘)
UPDATE parameter_definition SET standard_min=8, standard_max=15, standard_default=10,
       alarm_low=5, alarm_high=20, description='正常生产工作帮坡角约10°(半连续宽平盘,初设5-4)'
WHERE code='working_slope_angle';
-- 安全系数F:边坡稳定系数要求 >1.1~1.2(最终边坡>1.2)
UPDATE parameter_definition SET standard_min=1.1, standard_default=1.2,
       description='边坡稳定系数要求>1.1~1.2,最终边坡>1.2(初设2-3 瑞典条分法)'
WHERE code='safety_factor_F';

-- ── 边坡管理 · 最终帮设计 ──────────────────────────────────────────────────
-- 最终帮坡角:采掘场最终边坡角35°(端帮35°),稳定系数>1.2
UPDATE parameter_definition SET standard_min=35, standard_max=45, standard_default=35,
       description='采掘场最终边坡角35°(端帮帮坡角35°),稳定系数>1.2(初设2-3/5-4)'
WHERE code='final_slope_angle';

-- =============================================================================
-- 同步 template_param_value 推荐值(硬岩/4煤/煤层三模板)
-- 与材料无关的台阶/边坡/运输/排土参数:所有覆盖模板统一取设计值。
-- =============================================================================
-- 孔径:硬岩(t1)/4煤剥采(t2)=250(岩剥离);煤层(t3)=150。1001 仅在 t1,t2。
UPDATE template_param_value SET recommended_value=250, min_value=220, max_value=310 WHERE param_id=1001;
-- 孔深 17.5
UPDATE template_param_value SET recommended_value=17.5, min_value=15, max_value=20 WHERE param_id=1002;
-- 炸药单耗 0.48
UPDATE template_param_value SET recommended_value=0.48 WHERE param_id=1301;
-- 台阶高度 15
UPDATE template_param_value SET recommended_value=15, min_value=10, max_value=15 WHERE param_id=2001;
-- 台阶坡面角 65
UPDATE template_param_value SET recommended_value=65, min_value=60, max_value=65 WHERE param_id=2002;
-- 安全平台宽 8(岩6/黄土8)
UPDATE template_param_value SET recommended_value=8, min_value=6, max_value=8 WHERE param_id=2003;
-- 工作平台宽 80
UPDATE template_param_value SET recommended_value=80, min_value=40, max_value=90 WHERE param_id=2004;
-- 采宽 40
UPDATE template_param_value SET recommended_value=40, min_value=35, max_value=50 WHERE param_id=2005;
-- 月推进度 22
UPDATE template_param_value SET recommended_value=22, min_value=15, max_value=45 WHERE param_id=2101;
-- 运距 3.0
UPDATE template_param_value SET recommended_value=3.0, min_value=2.5, max_value=3.5 WHERE param_id=3001;
-- 路宽 30
UPDATE template_param_value SET recommended_value=30, min_value=25, max_value=40 WHERE param_id=3003;
-- 排土最大堆高 120
UPDATE template_param_value SET recommended_value=120, min_value=50, max_value=150 WHERE param_id=4001;
-- 排土单层堆高 30
UPDATE template_param_value SET recommended_value=30, min_value=20, max_value=40 WHERE param_id=4002;
-- 排土坡面角 35
UPDATE template_param_value SET recommended_value=35, min_value=30, max_value=35 WHERE param_id=4003;
-- 工作帮坡角 10
UPDATE template_param_value SET recommended_value=10, min_value=8, max_value=15 WHERE param_id=5001;
-- 安全系数F 1.2
UPDATE template_param_value SET recommended_value=1.2, min_value=1.1, max_value=2.0 WHERE param_id=5002;
-- 最终帮坡角 35
UPDATE template_param_value SET recommended_value=35, min_value=35, max_value=45 WHERE param_id=5101;

-- 煤层钻机孔径 Φ150(煤层标准 t3);若 t3 未覆盖 1001 则不影响。
UPDATE template_param_value SET recommended_value=150, min_value=120, max_value=170
WHERE param_id=1001 AND template_id IN (SELECT template_id FROM process_template WHERE applicable_hardness='soft');
