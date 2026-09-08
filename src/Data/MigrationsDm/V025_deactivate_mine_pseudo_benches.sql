-- 由 build/sqlite2dm.py 从 V025_deactivate_mine_pseudo_benches.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V025: 将矿级 location 代码标记为非活动平盘
--
-- V020 为满足 equipment.operating_area 外键，向 mine_location 插入了 4 个矿级代码
-- (安太堡矿/安家岭矿/东露天矿/其他单位)。但「平盘工艺地图」的平盘下拉走
-- IMineLocationService.All(activeOnly:true)，这些矿级代码会以空绑定的"伪平盘"
-- 混进下拉并成为默认项，显示 0 个工艺环节。
--
-- 这里把它们置为 is_active=0：既从平盘下拉中剔除，又不影响 equipment.operating_area
-- 外键(外键只校验代码存在，与 is_active 无关)与设备信息管理中矿名的文本显示。
-- =============================================================================

UPDATE mine_location SET is_active = 0
WHERE location_code IN ('安太堡矿', '安家岭矿', '东露天矿', '其他单位');
