-- 由 build/sqlite2dm.py 从 V011_parameter_acceptance.sql 自动生成, 请勿手改。
-- 要改请改源脚本或生成器, 然后重跑: python build/sqlite2dm.py

-- =============================================================================
-- V011: 现场验收 — 实测参数录入
--
-- 每一条记录 = 某平盘某工艺环节某参数在某天的实测值
-- 与 phase_location_binding 当时绑定的模板对照计算偏差
-- =============================================================================

CREATE TABLE parameter_acceptance (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    param_id            INT NOT NULL,
    location_code       VARCHAR(255) NOT NULL,
    phase_id            INT NOT NULL,
    measure_date        VARCHAR(255) NOT NULL,
    measured_value      DOUBLE,                          -- 数值型实测
    measured_text       VARCHAR(255),                          -- 文本/枚举型实测
    template_value      DOUBLE,                          -- 验收时模板规定值(快照)
    deviation_pct       DOUBLE,                          -- 偏差 % = (measured - template) / template * 100
    "status"              VARCHAR(255) NOT NULL DEFAULT 'pass' CHECK ("status" IN ('pass','warning','fail','pending')),
    equipment_id        VARCHAR(255),                          -- 关联设备(可空)
    accepted_by         VARCHAR(255),                          -- 验收人
    acceptance_date     VARCHAR(255),                          -- 验收日期
    conclusion          VARCHAR(255),                          -- 合格/整改后合格/不合格
    scope_code          VARCHAR(255),                          -- 炮区/工作面具体编号(自由文本)
    notes               VARCHAR(2000),
    created_at          VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          VARCHAR(255) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (param_id)      REFERENCES parameter_definition(param_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (location_code) REFERENCES mine_location(location_code) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (phase_id)      REFERENCES process_phase(phase_id) ON DELETE CASCADE ON UPDATE CASCADE,
    FOREIGN KEY (equipment_id)  REFERENCES equipment(equipment_id) ON DELETE SET NULL ON UPDATE CASCADE
);

CREATE INDEX idx_accept_loc_phase ON parameter_acceptance(location_code, phase_id);
CREATE INDEX idx_accept_param     ON parameter_acceptance(param_id);
CREATE INDEX idx_accept_date      ON parameter_acceptance(measure_date);
CREATE INDEX idx_accept_status    ON parameter_acceptance("status");

CREATE TRIGGER trg_parameter_acceptance_updated_at
BEFORE UPDATE ON parameter_acceptance
FOR EACH ROW
BEGIN
    :NEW.updated_at := CURRENT_TIMESTAMP;
END;
