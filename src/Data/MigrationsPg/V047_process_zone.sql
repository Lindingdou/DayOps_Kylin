-- 由 build/sqlite2pg.py 从 V047_process_zone.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V047: 工序作业区(process_zone) —— 一个作业面的五道工序，五块位置不同的地
--
-- 【为什么必须另起一张表，不进 mineable_region】
-- mineable_region 的类别判定是**白名单**，而它的兜底是采场：
--     ZoneRecord.IsPit => !IsDump && !IsWorkingSlope        (TaskLib/Zoning/ZoneStore.cs)
--     SimRegion.IsPit  同一条                               (TaskLib/Simulation/SimRegions.cs)
-- 往里塞一个新 category（drill / blast_guard / dump_tip …），这些行会被
-- **静默当成采场**进三维推演的推进轮廓（SimRegionLoader.Load）与路网中心线的裁剪范围
-- (RoadLibPlugin.LoadClipRegions)。一个字的红字都不会有 —— 只是推演里凭空多出几块
-- 朝着推进方向动的地，而每一块看着都挺合理。全项目有 19 个文件在按那张表的 category 筛，
-- 逐个改掉的风险远大于另起一张表。
--
-- 【与 mineable_region 的分工】
--   mineable_region：**长期存在的地**（采场/排土场/工作帮）。一个采场一条，推进了改边界，
--                    不按期次新建。三维推演、路网裁剪、单元归属都读它。
--   process_zone   ：**某一期某一道工序在哪儿干**。按期次新建，过期即历史。
--                    下游是任务编制/派工/清场，**不参与推演的推进极性**。
-- 两张表的行不许互相顶替：拿工序区当采场进推演，推进方向会按工序区的形状走；
-- 拿采场当工序区去清场，会把整个采场都圈成爆破警戒区。
--
-- 【为什么按 期次 + 工序 + 名字 唯一】
-- 同一个作业面在同一期里，穿孔一块、爆破警戒一块、采装一块 —— 名字相同、工序不同，
-- 三块地的位置不一样（沿推进方向错开）。少了 process 进唯一键，后生成的会把前面那块覆盖掉，
-- 而覆盖是 upsert，不报错。少了 period，上个月的穿孔区会被这个月的顶掉，历史查不回来。
--
-- 【z_source 不许空】同 mineable_region 的 z 陷阱：points_json 里写 0 会被下游当成实测高程
-- （ParseXyz 只有零顶点才返回 NaN），层体整体摆到 0m 而界面正常。所以高程解不出就**拒绝入库**，
-- 出处原样写进 z_source。这一列 NOT NULL 且不给默认值，就是为了让"忘了填"变成写不进去。
--
-- 【cell_m 必须存】轮廓是栅格描边出来的，格距就是轮廓精度。不存的话，三个月后有人
-- 拿这条边界去量工程量，而没有任何东西告诉他这是 ±2m 的东西。
--
-- 【clipped_at_border】爆破警戒区膨胀时顶到了格网边界 = 区域被截断。
-- 截断后的轮廓仍是一条规规矩矩的闭合环，图上看不出来 —— 所以必须在库里留证据。
-- =============================================================================

CREATE TABLE IF NOT EXISTS process_zone (
    id                 BIGSERIAL PRIMARY KEY,
    period             TEXT NOT NULL,                  -- 期次 yyyy-MM(工序区是按期的)
    process            TEXT NOT NULL,                  -- 工序码:drill/blast_guard/load/dump_tip/dump_doze
    name               TEXT NOT NULL,                  -- 区域名(作业面名/炮次号;分块带 -2 -3 后缀)
    group_key          TEXT NOT NULL DEFAULT '',       -- 分组键(作业面名;警戒区按炮次)
    piece_index        BIGINT NOT NULL DEFAULT 1,     -- 本组第几块(不相连时分块出,Z6)
    piece_count        BIGINT NOT NULL DEFAULT 1,

    points_json        TEXT NOT NULL,                  -- 扁平 [x0,y0,z0,x1,y1,z1,...]，与 mineable_region 同口径
    z_source           TEXT NOT NULL,                  -- 高程出处(空 = 不许入库)
    cell_m             DOUBLE PRECISION NOT NULL,                  -- 栅格格距 m = 轮廓精度
    area_m2            DOUBLE PRECISION NOT NULL DEFAULT 0,        -- 栅格面积(不是描边环面积)
    ring_area_m2       DOUBLE PRECISION NOT NULL DEFAULT 0,        -- 描边环面积(与上面差得多 = 描边不可信)
    clipped_at_border  BIGINT NOT NULL DEFAULT 0,     -- 1 = 膨胀顶到格网边界，本区被截断

    unit_ids           TEXT NOT NULL DEFAULT '',       -- 覆盖的采掘单元号，顿号分隔
    unit_count         BIGINT NOT NULL DEFAULT 0,
    volume_m3          DOUBLE PRECISION,                           -- 本区的量(NULL=不适用,如警戒区)
    volume_basis       TEXT NOT NULL DEFAULT '',       -- 量口径:原位实方/控制方量/排弃占容(三者绝不许并成一列)
    equip_role         TEXT NOT NULL DEFAULT '',       -- 该区的设备角色:钻机/电铲/卡车/推土机

    lead_days          DOUBLE PRECISION,                           -- 相对采装的超前(+)/滞后(-)工日;NULL=不适用
    guard_radius_m     DOUBLE PRECISION,                           -- 警戒半径 m(仅 blast_guard)
    band_width_m       DOUBLE PRECISION,                           -- 带宽 m(仅 dump_tip/dump_doze)

    active             BIGINT NOT NULL DEFAULT 1,     -- 本期算不算数(与 visible 是两件事)
    visible            BIGINT NOT NULL DEFAULT 1,     -- 画不画它
    note               TEXT NOT NULL DEFAULT '',       -- 记账:期次+分组+单元清单+格距+Z出处
    created_at         TEXT DEFAULT CURRENT_TIMESTAMP,
    updated_at         TEXT DEFAULT CURRENT_TIMESTAMP
);

-- upsert 键：同一期同一工序同一名字只留一条(换几何,不新增)
CREATE UNIQUE INDEX ux_process_zone_key
    ON process_zone(period, process, name);

-- 按期取一整期(界面每次开窗问一次)
CREATE INDEX idx_process_zone_period ON process_zone(period);
-- 按工序回查(派工:今天钻机该去哪几块地)
CREATE INDEX idx_process_zone_process ON process_zone(process, period);
-- 按作业面回查(一个面的五道工序摆在一起看)
CREATE INDEX idx_process_zone_group ON process_zone(group_key);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_process_zone_updated_at ON process_zone;
CREATE TRIGGER trg_process_zone_updated_at
BEFORE UPDATE ON process_zone
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();
