-- =============================================================================
-- V035: 去向扩展档案(sink_profile) + 库容盘点流水(sink_stocktake)
--       + 作业面去向档案(working_face_routing)
--
-- 由来:TaskLib 的「去向台账」与「作业面台账」两个窗口要真写回,但 SinkNode / FaceInput
-- 上有一批字段在现有表里没有列 —— dump_site 与 working_face 都另有消费者(排土场管理、
-- 工艺几何、参数验收),往里塞列会影响它们,故三张表全部新建、只做「扩展」不做「改造」。
--
-- 权威归属(避免双主口径,读回时按此规则合并):
--   · 排土场的 容量/已堆/台阶高/台阶坡角/状态/内外排 → 仍以 dump_site 为准;
--   · 卸载点的 名称/通过能力/坐标/子类           → 仍以 load_unload_point 为准;
--   · 本表只存两张表都放不下的:可接物料白名单、工作线长、当前排弃层、兜底运距、
--     开放时窗、启用期次,以及无法用 dump_type 表达的细分类型(表土堆场)与
--     卸载点的状态(load_unload_point 无 status 列)。
--
-- 单位:sink_profile 一律用【m³ / m / km / t·h⁻¹ / 小时】的工程原单位,不用万 m³ ——
--       万 m³ 只是 dump_site 的历史口径,换算(×1e4 / ÷1e4)只发生在 dump_site 边界上。
--
-- 幂等:全部 IF NOT EXISTS;本脚本重复执行不产生副作用(迁移器按 checksum 只跑一次,
--       但手工重放/合库场景也必须安全)。
-- =============================================================================

-- ── ① 去向扩展档案 ───────────────────────────────────────────────────────────
-- sink_id = SinkNode.Id:排土场为 dump_site.dump_id;卸载点为 'LUP-{load_unload_point.id}'。
-- 一个去向至多一行;没有行 = 该去向从未在「去向台账」里编辑过,读回时用工程缺省值。
CREATE TABLE IF NOT EXISTS sink_profile (
    sink_id             TEXT PRIMARY KEY,                  -- dump_site.dump_id 或 'LUP-{id}'
    sink_kind           TEXT    NOT NULL DEFAULT '',       -- SinkKind 枚举名;细化 dump_type 表达不了的类型(TopsoilYard)
    status              TEXT    NOT NULL DEFAULT 'active', -- active/full/closed;仅卸载点侧权威(dump_site 自带 status)
    accept_tph          REAL    NOT NULL DEFAULT 0,        -- 通过能力 t/h,0=不限;仅排土场侧权威(卸载点见 throughput_tph)
    accepted_materials  TEXT    NOT NULL DEFAULT '',       -- 可接物料码白名单,逗号分隔;空=按物料自身 AllowedSinks 判定
    work_line_length_m  REAL    NOT NULL DEFAULT 0,        -- 排土工作线长 m;推进距离 d = V容/(L×h)
    active_bench_level  INTEGER NOT NULL DEFAULT 1,        -- 当前可排台阶层(自下而上,1 起)
    fallback_haul_km    REAL    NOT NULL DEFAULT 0,        -- 路网不可解时的兜底运距 km(三层兜底最后一层)
    open_from_hour      REAL    NOT NULL DEFAULT 0,        -- 当日开放时窗起(0..24)
    open_to_hour        REAL    NOT NULL DEFAULT 24,       -- 当日开放时窗止(0..24);0/24=全天
    open_from_period    TEXT,                              -- 启用期次(内排土场须等采空区形成;空=已启用)
    note                TEXT,
    created_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TRIGGER IF NOT EXISTS trg_sink_profile_updated_at
AFTER UPDATE ON sink_profile FOR EACH ROW BEGIN
    UPDATE sink_profile SET updated_at = CURRENT_TIMESTAMP WHERE sink_id = NEW.sink_id;
END;

-- ── ② 库容盘点流水 ───────────────────────────────────────────────────────────
-- 「已填」是实绩逐日累计出来的,不允许在普通编辑里随手改 —— 改了账就对不上。
-- 确需修正(实测扫描、历史补录、口径纠偏)时走盘点:留下改前/改后/差额/原因,只增不改。
-- 单位一律【占容方 m³】,与 SinkNode.FilledM3 同口径(不是 dump_site 的万 m³)。
CREATE TABLE IF NOT EXISTS sink_stocktake (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    sink_id         TEXT    NOT NULL DEFAULT '',
    sink_name       TEXT    NOT NULL DEFAULT '',
    before_filled_m3 REAL   NOT NULL DEFAULT 0,           -- 盘点前已填(占容方 m³)
    after_filled_m3  REAL   NOT NULL DEFAULT 0,           -- 盘点后已填(占容方 m³)
    delta_m3        REAL    NOT NULL DEFAULT 0,           -- 差额 = after - before(正=补记,负=冲回)
    reason          TEXT    NOT NULL DEFAULT '',          -- 修正原因(必填,无原因不许改账)
    operator        TEXT    NOT NULL DEFAULT '',
    created_at      TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_sink_stocktake_sink ON sink_stocktake(sink_id, created_at);

-- ── ③ 作业面去向档案 ─────────────────────────────────────────────────────────
-- face_code 对齐 working_face.face_code(TaskLib 侧即 FaceInput.Zone)。
-- working_face 存的是「台阶几何」(台阶高/采宽/面长/推进度),本表存的是「当日怎么干」
-- (去向、运距、日目标、混采分项),两者互不覆盖:
--   · 能落 working_face 的(equipment_id / material / status)由服务层写回那张表;
--   · 台阶【标高】BenchElevationM 存本表 bench_elevation_m —— 它不是 working_face.bench_height_m
--     (那是台阶【高度】),两者量纲相同但口径完全不同,混写会污染工艺几何与参数验收的消费者。
-- splits_json = MaterialDestination[] 的 JSON(混采面一条任务多个去向),GeoDataBase 不解析。
CREATE TABLE IF NOT EXISTS working_face_routing (
    face_code               TEXT PRIMARY KEY,             -- = working_face.face_code / FaceInput.Zone
    process                 TEXT    NOT NULL DEFAULT 'Load',  -- Load(采装) | Dump(排土) …ProcessType 枚举名
    engineering_position_id TEXT    NOT NULL DEFAULT '',  -- 工程位置(EP-xx);≠ working_face.location_code(平盘编码)
    bench_elevation_m       REAL    NOT NULL DEFAULT 0,   -- 台阶标高 m(不是台阶高度!)
    material_code           TEXT    NOT NULL DEFAULT '',  -- MaterialCatalog 物料码
    material_mix            TEXT    NOT NULL DEFAULT '',  -- 混采构成原文("煤6∶岩4");空=单一物料
    destination_id          TEXT    NOT NULL DEFAULT '',  -- 主去向 SinkNode.Id
    destination_name        TEXT    NOT NULL DEFAULT '',
    destination_kind        TEXT    NOT NULL DEFAULT '',  -- SinkKind 枚举名
    haul_distance_km        REAL    NOT NULL DEFAULT 0,   -- 运距 km
    equiv_haul_km           REAL    NOT NULL DEFAULT 0,   -- 等效运距 km(含坡度折算)
    day_target_m3           REAL    NOT NULL DEFAULT 0,   -- 当日目标 m³ 实方
    derived_from_inbound    INTEGER NOT NULL DEFAULT 0,   -- 1=排土面,日目标由入方推导,不手工填
    shovel_model_pref       TEXT    NOT NULL DEFAULT '',  -- 主铲型号偏好(编组求解用)
    main_equipment          TEXT    NOT NULL DEFAULT '',  -- 主设备(同时写回 working_face.equipment_id)
    splits_json             TEXT    NOT NULL DEFAULT '[]',-- MaterialDestination[] JSON,opaque
    note                    TEXT,
    created_at              TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at              TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TRIGGER IF NOT EXISTS trg_working_face_routing_updated_at
AFTER UPDATE ON working_face_routing FOR EACH ROW BEGIN
    UPDATE working_face_routing SET updated_at = CURRENT_TIMESTAMP WHERE face_code = NEW.face_code;
END;
