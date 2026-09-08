-- 由 build/sqlite2dm.py 从 V037_face_source_xyz.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V037: 作业面源端坐标(working_face_routing.source_x/y/z)
--
-- 由来:V036 把【汇】端的坐标补齐了(sink_profile.x/y/z + load_unload_point.x/y/z),
--   但路网求运距要求**源汇两端都能定位到 RoadNode**,而【源】端(作业面/铲位)一直没有
--   坐标可存 —— working_face 存的是台阶几何(台阶高/采宽/面长/推进度),没有平面坐标列。
--   源端没坐标 ⇒ HaulResolver 只能拿作业面名 / 工程位置号去路网做名称匹配,匹配不上
--   就整条链落到第三层「兜底运距」。而运距 L_eq 是循环时间的输入:
--       T_c = t_装 + 60·L_eq/v_重 + t_卸 + 60·L_eq/v_空 + t_调,  n* = T_c/τ_L,
--   n* 又决定编组班产、编组班产又是编制裂解装箱的 bin —— 源端缺坐标,下游一串数全偏。
--
-- 为什么加在 working_face_routing 而不是 working_face:与 V035/V036 同一条纪律 ——
--   working_face 另有消费者(工艺几何、参数验收、工作面管理),往里塞列会影响它们。
--   本表是「当日怎么干」的家,源端代表点属于「这个面从哪装车」,正是本表的职责。
--
-- 权威归属:
--   · 本表的 source_x/y/z 是【人工录入】的源代表点,权威最高 —— 台账里录了就以它为准。
--   · 没录(x、y 均为 0)时由 TaskLib 侧的 HaulResolver 从 mineable_region.points_json
--     自动推导区域质心顶上,那是**系统推的**、不入库(库里仍是 0),这样区域边界改了
--     推导值会跟着改,而不会被一份陈旧的猜测值钉死。
--
-- 单位口径:
--   · source_x / source_y —— 矿区平面坐标系,与 sink_profile.x/y、load_unload_point.x/y、
--     mineable_region.points_json、road_network 节点同一坐标系,单位【m】。
--     ★ x、y 均为 0 = 未录坐标,不是原点。判据与 HaulResolver.HasPosition、
--       FaceInput.HasSourcePosition 严格一致 —— 绝不能把 (0,0) 当真实位置去吸附
--       路网节点,那会吸到离原点最近的节点上,算出一个「看着很正常」的假运距。
--   · source_z —— 源端标高【m】,即该面所在台阶的标高。Z=0 是合法标高,不参与
--     「有没有坐标」的判定(与 V036 的 sink_profile.z 完全同一口径)。
--
-- 幂等:SQLite 的 ALTER TABLE ADD COLUMN 没有 IF NOT EXISTS,重复执行会报
--   duplicate column name。迁移器按 (module, version) 主键只跑一次,且按 checksum 校验
--   已应用脚本 —— 正常路径不会重复执行。写法照抄 V036(裸 ALTER TABLE ADD COLUMN),
--   不自创 guard:自创的 guard 会让本脚本与既有加列脚本的形态不一致,反而更难核对。
--
-- ★ 不改 V035/V036:迁移器按 checksum 校验已应用脚本,改动一个字都会抛
--   MigrationChecksumMismatchException,让用户既有的库直接起不来。加列一律新开脚本。
-- =============================================================================

-- ── 作业面源端代表点(铲位/装载点);x、y 均为 0 = 未录,由区域质心自动推导 ───────────
ALTER TABLE working_face_routing ADD COLUMN source_x DOUBLE NOT NULL DEFAULT 0;  -- 源代表点 X(m);x、y 均为 0 = 未录坐标
ALTER TABLE working_face_routing ADD COLUMN source_y DOUBLE NOT NULL DEFAULT 0;  -- 源代表点 Y(m)
ALTER TABLE working_face_routing ADD COLUMN source_z DOUBLE NOT NULL DEFAULT 0;  -- 源代表点 Z 标高(m);Z=0 是合法标高,不参与"有没有坐标"的判定
