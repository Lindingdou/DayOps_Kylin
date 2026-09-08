-- 由 build/sqlite2pg.py 从 V045_road_centerline_set.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V045: 中心线存档(road_centerline_set) —— 「中心线管理」的持久化落点
--
-- 【为什么要这张表】道路中心线此前**只活在图纸图层上**（点云_道路中心线）：
-- 提取一次、手动补几条、删掉几条画错的，全部结果只是一堆多段线实体。
-- 于是三件事做不到：
--   ① 换个工程/重开图纸，上一轮"提取 + 人工整理"的成果拿不回来，只能重跑一遍提取；
--   ② 想留一版"整理干净的底本"再去试着删，删错了只能靠 Ctrl+Z 一步步倒；
--   ③ 两个时期的中线没法各存一份对照（路网存档 road_network 存的是**建完网的图**，
--      不是中线本身；从图反推不回原始中线：noding 已经把线切碎、桥接还凭空加过段）。
--
-- 【与 road_network(V019) 的分工】
--   road_centerline_set = **进料**（图纸上那一批中线折线，几何原样）
--   road_network        = **成品**（吸附/打断/桥接之后的节点+边图）
-- 建网是单向的，所以两张表都要留：改口径重建网要回到进料，寻径/运距只认成品。
--
-- 【几何为什么是 base64 而不是 JSON】一次提取上千条、每条上百顶点，JSON 文本是几十 MB
-- 的数字串。这里存 GZip 过的二进制（布局见 RoadLib.Network.CenterlineSetCodec，
-- magic 'RCL1'），GeoDataBase 不解析（opaque），同 virtual_drill_surface.geometry_b64。
--
-- 【冗余列】line_count / vertex_count / length_km / 包围盒都由 app 写入，
-- 供管理窗列表**免解包**直接显示 —— 存档列表一开就解十来份几十 MB 的几何是没必要的。
-- =============================================================================

CREATE TABLE IF NOT EXISTS road_centerline_set (
    id           SERIAL PRIMARY KEY,
    name         TEXT    NOT NULL DEFAULT '',       -- 存档名(可含时期,如"2026-06 提取+人工整理")
    captured_at  TEXT    NOT NULL DEFAULT '',       -- 存档时刻(app 写,"什么时候的中线")
    source       TEXT    NOT NULL DEFAULT '',       -- 来路标注:提取/手动/管理整理…(纯留痕,不参与逻辑)
    geometry_b64 TEXT    NOT NULL DEFAULT '',       -- GZip 二进制 base64(RCL1),opaque
    line_count   INTEGER NOT NULL DEFAULT 0,
    vertex_count INTEGER NOT NULL DEFAULT 0,
    length_km    DOUBLE PRECISION    NOT NULL DEFAULT 0,        -- 三维总长 km(冗余,列表显示用)
    min_x        DOUBLE PRECISION    NOT NULL DEFAULT 0,
    min_y        DOUBLE PRECISION    NOT NULL DEFAULT 0,
    min_z        DOUBLE PRECISION    NOT NULL DEFAULT 0,
    max_x        DOUBLE PRECISION    NOT NULL DEFAULT 0,
    max_y        DOUBLE PRECISION    NOT NULL DEFAULT 0,
    max_z        DOUBLE PRECISION    NOT NULL DEFAULT 0,
    note         TEXT,
    created_at   TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at   TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_road_centerline_set_updated_at ON road_centerline_set;
CREATE TRIGGER trg_road_centerline_set_updated_at
BEFORE UPDATE ON road_centerline_set
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();
