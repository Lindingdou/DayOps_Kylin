-- =============================================================================
-- V019: 路网存档 — 开拓运输系统某一时期的道路网络图(节点+边)持久化
--
-- 每条记录 = 一份命名的路网，对应"什么时候的路网"(captured_at)。
-- 图本身(节点/边/中线/属性)由 RoadLib 序列化成 JSON 存 graph_json(GeoDataBase 不解析,opaque)。
-- node_count/edge_count/length_km 冗余存,供管理列表免反序列化直接显示。
-- 纯数据 + 元数据，自包含；载入即设为会话图，亦可作两期演化对比的快照来源。
-- =============================================================================

CREATE TABLE IF NOT EXISTS road_network (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NOT NULL DEFAULT '',          -- 路网名称(可含时期含义,如"2026-06 现状")
    captured_at TEXT    NOT NULL DEFAULT '',          -- 所属/采集时刻(app 写,"什么时候的路网")
    graph_json  TEXT    NOT NULL DEFAULT '{}',        -- RoadLib 序列化的图(节点+边),opaque
    node_count  INTEGER NOT NULL DEFAULT 0,
    edge_count  INTEGER NOT NULL DEFAULT 0,
    length_km   REAL    NOT NULL DEFAULT 0,           -- 总里程 km(冗余,列表显示用)
    note        TEXT,
    created_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TRIGGER IF NOT EXISTS trg_road_network_updated_at
AFTER UPDATE ON road_network FOR EACH ROW BEGIN
    UPDATE road_network SET updated_at = CURRENT_TIMESTAMP WHERE id = NEW.id;
END;
