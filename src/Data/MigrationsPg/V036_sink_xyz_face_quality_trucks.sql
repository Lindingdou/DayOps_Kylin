-- 由 build/sqlite2pg.py 从 V036_sink_xyz_face_quality_trucks.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V036: 去向坐标(sink_profile.x/y/z) + 作业面煤质目标与实配车号(working_face_routing)
--
-- 由来:V035 把两个台账窗口接上了写回,但还剩四类字段「无处可存」——
--   ① 排土场的 X/Y/Z:dump_site 表没有坐标列(load_unload_point 有),于是排土场进了
--      SinkRegistry 之后 X/Y/Z 恒为 0。HaulResolver 求运距时先按 RefEntityId/Id 去路网
--      精确匹配节点,匹配不上才按坐标吸附;排土场两条路都走不通,只能落到第三层兜底运距。
--      给它一个坐标,最近节点吸附这条路才通得了(也供三维定位)。
--   ② 作业面的煤质目标四项:配煤约束的输入,此前只能来自 SampleTaskBoard 的样例。
--   ③ 作业面的实配车号:现场实际配的车。与建议车数之差正是引擎报「运力不足」的判据
--      (TaskExploder:配 < 荐 ⇒ 铲将待车),故必须是真数据而不是样例。
--   (RecommendedTrucks / GroupCapacityM3PerH 是 FleetMatcher 的求解派生值,故意不存。)
--
-- 为什么加在这两张扩展表上而不是本体表:dump_site 与 working_face 都另有消费者
-- (排土场管理、工艺几何、参数验收),往里塞列会影响它们 —— 与 V035 同一条纪律。
--
-- 权威归属(读回时按此合并,避免双主口径):
--   · 坐标:【卸载点】以 load_unload_point.x/y/z 为准 —— 那张表自带坐标列,且
--     「装卸点设置」也在改它;本表的 x/y/z 只对【排土场】(dump_site 来源)生效。
--     写回时两侧都落一份(与 accept_tph / status 同做法:写全、读按权威挑),
--     免得档案行上留一串 0 看着像数据丢了。
--   · 煤质目标 / 实配车号:working_face 没有这些列,本表是它们唯一的家。
--
-- 单位口径:
--   · x / y / z —— 矿区平面坐标系,与 load_unload_point.x/y/z、road_network 节点同一坐标系,
--     单位【m】。全 0(x、y 均为 0)= 未录坐标,不是原点 —— HaulResolver.HasPosition
--     就是这么判的,绝不能把 (0,0) 当成真实位置去吸附路网节点。
--   · quality_ash_pct / quality_sulfur_pct / quality_moisture_pct —— 百分数【%】,填 12.5 不是 0.125;
--     quality_cv_mjkg —— 热值【MJ/kg】(不是 kcal/kg,换算 1 MJ/kg ≈ 239 kcal/kg)。
--   · assigned_trucks —— 车号原文,逗号分隔(如 'T-01,T-02');空串 = 未配车。
--
-- ★ 煤质四项必须允许 NULL:CoalQuality 在契约里本就是可空的(纯量矿场景没有煤质目标),
--   用 0 冒充「没有目标」会让配煤约束把每个面都判成「灰分 0、硫 0,达标」——
--   一个恒真的约束比没有约束更危险。故这四列不给 NOT NULL、不给 DEFAULT。
--   四列各自可空:允许「只填了灰分,热值还没定」这种录入中间态如实存下来;
--   是否构成一个【有效的煤质目标】由调用方判(TaskLib 侧:四项齐全才组装 CoalQuality)。
--
-- 幂等:SQLite 的 ALTER TABLE ADD COLUMN 没有 IF NOT EXISTS,重复执行会报
--   duplicate column name。迁移器按 (module, version) 主键只跑一次,且按 checksum 校验
--   已应用脚本 —— 正常路径不会重复执行。写法照抄仓库既有加列脚本
--   (V016_mineable_region_category / V018_mineable_region_color / V033_current_state_seam_horizon),
--   不自创 guard:自创的 guard 会让 checksum 与既有加列脚本的形态不一致,反而更难核对。
-- =============================================================================

-- ── ① 去向坐标(仅排土场侧权威;卸载点见 load_unload_point.x/y/z)────────────────
ALTER TABLE sink_profile ADD COLUMN x DOUBLE PRECISION NOT NULL DEFAULT 0;   -- 去向代表点 X(m);x、y 均为 0 = 未录坐标
ALTER TABLE sink_profile ADD COLUMN y DOUBLE PRECISION NOT NULL DEFAULT 0;   -- 去向代表点 Y(m)
ALTER TABLE sink_profile ADD COLUMN z DOUBLE PRECISION NOT NULL DEFAULT 0;   -- 去向代表点 Z 标高(m);Z=0 是合法标高,不参与"有没有坐标"的判定

-- ── ② 作业面煤质目标(配煤约束输入;NULL = 该面无煤质目标,即纯量矿)──────────────
ALTER TABLE working_face_routing ADD COLUMN quality_ash_pct DOUBLE PRECISION;   -- 灰分目标 %(NULL=无目标)
ALTER TABLE working_face_routing ADD COLUMN quality_cv_mjkg DOUBLE PRECISION;   -- 热值目标 MJ/kg(NULL=无目标)
ALTER TABLE working_face_routing ADD COLUMN quality_sulfur_pct DOUBLE PRECISION;   -- 硫分目标 %(NULL=无目标)
ALTER TABLE working_face_routing ADD COLUMN quality_moisture_pct DOUBLE PRECISION;   -- 水分目标 %(NULL=无目标)

-- ── ③ 作业面实配车号(现场实际配的车;与建议车数之差 = 运力不足的判据)────────────
ALTER TABLE working_face_routing ADD COLUMN assigned_trucks TEXT NOT NULL DEFAULT '';  -- 车号逗号分隔,如 'T-01,T-02';空=未配车
