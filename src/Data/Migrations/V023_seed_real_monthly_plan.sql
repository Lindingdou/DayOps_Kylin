-- AUTO-GENERATED from 平朔 7.1 dataset (露天设备资产/分布台账 + 设备能力/单耗分析). Do not hand-edit.
-- V023: 月度计划(东露天矿 2026年1-5月,源自单耗分析:自营采剥/运距/提升高度)

DELETE FROM monthly_plan WHERE year=2026;
INSERT INTO monthly_plan (year, month, plan_strip_wan_m3, plan_coal_wan_t, plan_outsource_strip_wan_m3, ratio_strip_coal, avg_distance_km, avg_height_m, team1_distance_km, team2_distance_km) VALUES
  (2026, 1, 1041.6667, 0, 0, 0, 3.31, 68.7974, 3.31, 3.31),
  (2026, 2, 1041.6667, 0, 0, 0, 3.046, 74.22, 3.046, 3.046),
  (2026, 3, 1041.6667, 0, 0, 0, 3.0019, 64.1033, 3.0019, 3.0019),
  (2026, 4, 1041.6667, 0, 0, 0, 2.6858, 46.3321, 2.6858, 2.6858),
  (2026, 5, 1041.6667, 0, 0, 0, 2.9137, 57.8124, 2.9137, 2.9137);
