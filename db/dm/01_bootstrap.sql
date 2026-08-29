-- ============================================================
--  达梦 DM8 · PitMine3D 建库脚本（以 SYSDBA 执行，一次性）
--  用法: disql SYSDBA/SYSDBA@localhost:5236 01_bootstrap.sql
--  作用: 建表空间 + 应用用户/模式 PITMINE + 开发期授权
--  说明: 业务表由移植后的 MigrationRunner 跑 V*.sql 自动建，不在此文件
-- ============================================================

-- 1) 表空间（初始 256MB，自动扩展）
CREATE TABLESPACE "PITMINE"
    DATAFILE 'PITMINE01.DBF' SIZE 256 AUTOEXTEND ON NEXT 64 MAXSIZE 8192;

-- 2) 应用用户（达梦里 用户 = 模式 schema）
--    注意 DM8 默认口令策略较严(长度/复杂度)，如被拒可调整口令或实例口令策略
CREATE USER "PITMINE" IDENTIFIED BY "PitMine_2026#dm"
    DEFAULT TABLESPACE "PITMINE";

-- 3) 开发期授权（够建表/增删改查/建序列视图存过）—— 生产环境请按最小权限收紧
GRANT "RESOURCE","PUBLIC" TO "PITMINE";
GRANT CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE, SELECT TABLE TO "PITMINE";

-- 4) 回显确认
SELECT USERNAME, DEFAULT_TABLESPACE, ACCOUNT_STATUS
FROM   DBA_USERS WHERE USERNAME = 'PITMINE';

COMMIT;
-- 完成后用应用用户登录跑冒烟(见 README「连通冒烟」)
