-- 由 build/sqlite2pg.py 从 V038_seam_bench_param.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2pg.py

-- =============================================================================
-- pmgeo.db 逐煤层台阶参数(煤的采矿模型·倾斜分层)
-- 版本: V038
-- 说明: V034 已把【煤台阶】那套指标立进 parameter_definition(coal_bench_height /
--       coal_bench_slope_angle / coal_platform_width,phase_id=201),但那是【全矿一套】。
--       实际各煤层厚度、顶底板岩性、可采下限都不同,4 号煤和 11 号煤没道理共用一个台阶高。
--
--       所以这里【不另起一套指标】,只做「逐煤层覆盖值」:
--         · 取值区间 / 报警上下限 / 默认值 —— 仍归 parameter_definition(V034 已校准好)
--         · 某层煤要偏离全矿默认  —— 在本表写一行覆盖
--       字段留 NULL = 该项不覆盖,回落 parameter_definition.standard_default。
--       读取顺序:本表 → parameter_definition 默认 → 代码兜底。
--
-- 关联: seam_code → coal_seam_def.code(全库煤层主键:borehole_seam_result /
--       coal_sample / current_state_point / virtual_drill_surface.seam_name 同一套取值)。
--
-- 分层口径: datum='floor' = 从底板起算往上切分层(煤采到底板为止,从底往上分才贴实际)。
--       留成字段而非写死,便于将来按顶板起算。
-- 多方案: design_version 允许同一层煤存多套参数做比选;is_active=1 的那套参与建模。
-- =============================================================================

CREATE TABLE IF NOT EXISTS seam_bench_param (
    id                      SERIAL PRIMARY KEY,
    seam_code               TEXT    NOT NULL,               -- → coal_seam_def.code (4 / 9 / 11 ...)

    -- 覆盖 parameter_definition 的煤台阶三项(NULL = 不覆盖,回落全矿默认)
    bench_height_m          DOUBLE PRECISION,                           -- 覆盖 coal_bench_height (2007)
    bench_slope_angle_deg   DOUBLE PRECISION,                           -- 覆盖 coal_bench_slope_angle (2008)
    berm_width_m            DOUBLE PRECISION,                           -- 覆盖 coal_platform_width (2009)

    -- 煤的采矿模型专有(parameter_definition 里没有对应项)
    strip_width_m           DOUBLE PRECISION,                           -- 采掘带宽度 W(往高墙里推进的水平深度)
    layering_mode           TEXT    NOT NULL DEFAULT 'inclined',  -- 'inclined' 倾斜分层 / 'horizontal' 顶底水平
    min_mineable_thick_m    DOUBLE PRECISION,                           -- 最小可采厚:薄于此不出煤体
    datum                   TEXT    NOT NULL DEFAULT 'floor',     -- 'floor' 从底板起算 / 'roof' 从顶板起算

    design_version          TEXT,
    notes                   TEXT,
    is_active               INTEGER NOT NULL DEFAULT 1,
    created_at              TEXT    DEFAULT CURRENT_TIMESTAMP,
    updated_at              TEXT    DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY (seam_code) REFERENCES coal_seam_def(code) ON DELETE CASCADE ON UPDATE CASCADE
);

-- 一层煤在一个方案下只能有一行(方案为空视作默认方案)
CREATE UNIQUE INDEX idx_seam_bench_param_key
    ON seam_bench_param(seam_code, COALESCE(design_version, ''));

CREATE INDEX idx_seam_bench_param_active
    ON seam_bench_param(is_active);

CREATE OR REPLACE FUNCTION fn_touch_updated_at() RETURNS trigger AS $BODY$
BEGIN
    NEW.updated_at := CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$BODY$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_seam_bench_param_updated_at ON seam_bench_param;
CREATE TRIGGER trg_seam_bench_param_updated_at
BEFORE UPDATE ON seam_bench_param
FOR EACH ROW
EXECUTE PROCEDURE fn_touch_updated_at();

-- 为已在册的煤层各建一行空覆盖(全部 NULL = 全走全矿默认),
-- 这样参数分组窗口一打开就能按煤层列全,用户只改要偏离的那几项。
-- strip_width / min_mineable_thick 在 parameter_definition 里没有对口项,给出初值。
INSERT INTO seam_bench_param
    (seam_code, strip_width_m, layering_mode, min_mineable_thick_m, datum, notes)
SELECT code, 20.0, 'inclined', 0.8, 'floor', '自动建行:参数全部回落全矿默认,按需逐项覆盖'
FROM coal_seam_def;
