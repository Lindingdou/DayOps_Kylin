-- 由 build/sqlite2pg.py 从 V030_virtual_drill.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- V030: 虚拟钻孔 — 地质模型三角网(地表 / 煤层顶底板)节点 + 连接顺序持久化
--
-- 首次使用「虚拟钻孔」时,用户在场景中指定地表面 + 各煤层顶/底板三角网,程序读取其
-- 节点(顶点 xyz)与连接顺序(三角索引),打包序列化后随本表存库(geometry_b64,opaque)。
-- 之后任意位置的虚拟钻孔层位计算,直接从库里取几何做竖直求交算顶/底板高程,不再依赖
-- 场景当前是否仍加载该地质模型。每行 = 一张已捕获的面:
--   role='surface' 地表(seam_name 空) | role='roof' 煤层顶板 | role='floor' 煤层底板
-- 顶/底板按 seam_name 配对成一层煤;seam_order 决定自上而下排列;color_hex = 层位显示色(可配置)。
-- vertex_count / triangle_count / bbox 冗余存,供管理列表显示与快速排除,免反序列化。
-- =============================================================================

CREATE TABLE IF NOT EXISTS virtual_drill_surface (
    id             SERIAL PRIMARY KEY,
    role           TEXT    NOT NULL DEFAULT 'roof',      -- 'surface' | 'roof' | 'floor'
    seam_name      TEXT    NOT NULL DEFAULT '',          -- 地表为空;顶/底板为煤层编号(如 4 / 7-1 / 9 / 11)
    seam_order     INTEGER NOT NULL DEFAULT 0,           -- 地质排序(自上而下递增,0 在最上)
    color_hex      TEXT    NOT NULL DEFAULT '#3C3C3C',   -- 层位显示色(可配置;#RRGGBB)
    source_layer   TEXT    NOT NULL DEFAULT '',          -- 来源:原场景三角网图层名(溯源)
    vertex_count   INTEGER NOT NULL DEFAULT 0,
    triangle_count INTEGER NOT NULL DEFAULT 0,
    min_x DOUBLE PRECISION NOT NULL DEFAULT 0,
    min_y DOUBLE PRECISION NOT NULL DEFAULT 0,
    min_z DOUBLE PRECISION NOT NULL DEFAULT 0,
    max_x DOUBLE PRECISION NOT NULL DEFAULT 0,
    max_y DOUBLE PRECISION NOT NULL DEFAULT 0,
    max_z DOUBLE PRECISION NOT NULL DEFAULT 0,
    geometry_b64   TEXT    NOT NULL DEFAULT '',          -- 打包的顶点 + 三角索引(base64,opaque)
    created_at     TEXT    NOT NULL DEFAULT '',
    updated_at     TEXT    NOT NULL DEFAULT ''
);

-- 一个 (role, seam_name) 只存一张面:重复捕获即覆盖,服务层据此按业务键 upsert。
CREATE UNIQUE INDEX ux_virtual_drill_surface_role_seam
    ON virtual_drill_surface(role, seam_name);
