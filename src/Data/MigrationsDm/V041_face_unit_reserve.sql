-- 由 build/sqlite2dm.py 从 V041_face_unit_reserve.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V041: 作业面的「空间身份」与「备采家底」——为任务编制补齐盘子
--
-- 补的是四个一直缺、而装箱一直在拿别的东西凑的量：
--
--  ① unit_id —— 采掘单元号，对回采矿模型导出的那张表(MiningUnitLedger.Row.UnitId)，
--     也就是对回图上的那个体。此前一条日任务只有 zone(自由文本作业面名)，
--     点一行**定位不到图上任何东西**，备采量核销不了、期次覆盖率算不出来、
--     影像底图上没有可画的边界。UnitId 是这几件事共同的钥匙。
--     格式与生成端一致：层-B带号-P幅号（如 3煤-B12-P03）。空 = 该面还没绑到单元。
--
--  ② available_reserve_m3 —— 本面备采储量(m³ 原位实方)。
--     装箱里"备采用尽 → 该班空闲(OreShortage)"这句话，此前判的是**当日目标**用尽，
--     不是储量见底 —— 于是"这个面还能采几天"、"采准断不断档"根本无从判起(BR-V6 三量)。
--     0 = 未录(不是"没有储量")：判据一律先看 >0 再用，不拿 0 当"采空"。
--
--  ③ advance_azimuth_deg —— 推进方位(°)。作业区布置要画推进箭头、
--     动画要让设备沿正确方向走，此前只能从上游 PlanFace 猜。NULL = 未录。
--
--  ④ mining_width_m —— 推进宽/采宽(m)。与 ③ 配对，一个给方向一个给步长。NULL = 未录。
--
-- 【为什么不放 working_face】那张表存的是「台阶几何」(台阶高/坡角/平盘宽/面长/月推进度)，
-- 是设计口径、按月变；本表存的是「当日怎么干」，按天变。备采量与单元号跟着"当日怎么干"走：
-- 同一个台阶今天在 B12 幅、明天推到 B13 幅，单元号会变而台阶几何不变。
-- (mining_width_m 与 working_face.mining_width_m 同名不同义：那边是设计采宽，这边是本面当前实际推进宽。)
--
-- 写法照抄仓库既有加列脚本(V036/V037)：ALTER TABLE ... ADD COLUMN，不自创 guard。
-- =============================================================================

ALTER TABLE working_face_routing ADD COLUMN unit_id VARCHAR(255) NOT NULL DEFAULT '';  -- 采掘单元号(= 采矿模型 UnitId);空=未绑
ALTER TABLE working_face_routing ADD COLUMN available_reserve_m3 DOUBLE NOT NULL DEFAULT 0;   -- 备采储量(m³ 原位实方);0=未录,非"采空"
ALTER TABLE working_face_routing ADD COLUMN advance_azimuth_deg DOUBLE;                      -- 推进方位(°,正北起顺时针);NULL=未录
ALTER TABLE working_face_routing ADD COLUMN mining_width_m DOUBLE;                      -- 本面当前推进宽(m);NULL=未录

-- 按单元号回查作业面（点图上的体 → 找到今天是哪个面在采它）
CREATE INDEX idx_working_face_routing_unit
    ON working_face_routing(unit_id);
