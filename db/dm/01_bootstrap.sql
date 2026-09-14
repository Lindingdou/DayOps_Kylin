-- ============================================================
--  达梦 DM8 · PitMine3D 建库脚本（以 SYSDBA 执行，一次性）
--
--  两条路径共用这一个文件，别再各写一份：
--    · 自动: install-dm8-kylin.sh 第 7 步会替换下面的占位符后执行
--    · 手工: 自己把 __DB_USER__ / __DB_PASSWORD__ 换掉，再
--            disql SYSDBA/<SYSDBA口令>@localhost:5236 `pwd`/01_bootstrap.sql
--
--  作用: 建表空间 + 应用用户/模式 + 开发期授权
--  说明: 业务表**不在这里建** —— 由迁移脚本 V*.sql 建（换库任务 P5-1）
-- ============================================================

-- 1) 表空间（初始 256MB，自动扩展，上限 8G）
--    达梦不像 PG 有"数据库"这层，一个实例一个库；隔离靠表空间 + 用户/模式。
CREATE TABLESPACE "__DB_USER__"
    DATAFILE '__DB_USER__01.DBF' SIZE 256 AUTOEXTEND ON NEXT 64 MAXSIZE 8192;

-- 2) 应用用户（达梦里 用户 = 模式 schema，建完用户就有了同名模式）
--    口令策略默认要求 ≥9 位；被拒的话要么改口令，要么调实例的 PWD_POLICY。
CREATE USER "__DB_USER__" IDENTIFIED BY "__DB_PASSWORD__"
    DEFAULT TABLESPACE "__DB_USER__";

-- 3) 开发期授权（够建表/增删改查/建序列视图存过）—— 生产环境请按最小权限收紧
GRANT "RESOURCE","PUBLIC" TO "__DB_USER__";
GRANT CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE, SELECT TABLE TO "__DB_USER__";

-- 4) 回显确认
SELECT USERNAME, DEFAULT_TABLESPACE, ACCOUNT_STATUS
FROM   DBA_USERS WHERE USERNAME = '__DB_USER__';

COMMIT;
-- 完成后用应用用户登录跑冒烟(见 README「连通冒烟」)；install-dm8-kylin.sh 第 8 步已自动跑过。
