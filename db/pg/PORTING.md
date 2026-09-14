# openGauss 移植现状

> 2026-09-08。目标形态：**局域网内一个共享 openGauss 库 + 多台麒麟客户端**，替代原来的嵌入式 SQLite。

## 一句话现状

数据层已经**能**指向 openGauss，但**还没在真实实例上跑过一次**。
默认仍是 SQLite，行为与移植前逐字一致（全量测试 2072 项通过）。

## 怎么切过去

```bash
export PITMINE_DB=opengauss
export PITMINE_DB_CONN='Host=192.168.1.10;Port=5432;Database=pitmine;Username=pitmine;Password=***'
```

不设 `PITMINE_DB` 就是 SQLite。方言选择在 [`src/Data/GeoDbDialect.cs`](../../src/Data/GeoDbDialect.cs)。

## 已完成

| 层 | 做了什么 |
|---|---|
| 类型解耦 | 269 处 `SqliteConnection` → `DbConnection`，24 个文件 |
| 参数 | 129 处 `AddWithValue` 收敛到 `src/Data/DbCompat.cs` 单一入口 |
| 方言 | `SqliteDialect` / `OpenGaussDialect`，封装建连、建表、外键开关 |
| 驱动 | `Npgsql 8.0.9` |
| 迁移翻译 | 52 份全部生成到 `src/Data/MigrationsPg/`，由 `build/sqlite2pg.py` 产出 |
| 共享库守卫 | 迁移改为部署动作，客户端启动只校验版本 |
| 并发 | V051 行版本号 `row_version`（乐观锁基础） |
| 性能 | V052 补 12 个外键索引；13 个 CSV 导入方法从每行 2~4 次往返降到 1 次 |

## 比达梦省下来的（原以为要改，实际不用）

之前评估达梦时列的几项大工程，在 openGauss 上直接消失：

| | 达梦 | openGauss |
|---|---|---|
| `@` 参数（407 处） | 要全改成 `:` | **Npgsql 原生认 `@`，零改动** |
| 保留字（应用层 87 处 + 迁移 291 处） | 全要加引号 | **`date`/`status`/`value`/`year` 在 PG 都是非保留字，零改动** |
| `TEXT` / `INTEGER` | 要换成 `VARCHAR(n)` / `INT`，还要担心宽度和行宽 | **PG 原生类型，照搬** |
| 迁移脚本执行 | 一次只吃一条，要写语句拆分器 | **Npgsql 支持一个命令多条语句**，拆分器已删 |

## 翻译规则（`build/sqlite2pg.py`）

**openGauss 内核衍生自 PostgreSQL 9.2**，不能按现代 PG 写。已避开：
`GENERATED AS IDENTITY`（PG 10+）、`EXECUTE FUNCTION`（PG 11+）、`CREATE INDEX IF NOT EXISTS`（PG 9.5+）。

| SQLite | openGauss | 处数 |
|---|---|---|
| `TEXT` | `TEXT`（不变） | 365 |
| `REAL` | `DOUBLE PRECISION` | 243 |
| `INTEGER` | `INTEGER`（不变） | 125 |
| `INTEGER PRIMARY KEY AUTOINCREMENT` | `SERIAL PRIMARY KEY` | 39 |
| `CREATE INDEX IF NOT EXISTS` | 去掉 IF NOT EXISTS | 94 |
| `INSERT OR IGNORE` | `… ON CONFLICT DO NOTHING` | 38 |
| `INSERT OR REPLACE` | `… ON CONFLICT (键) DO UPDATE SET …=EXCLUDED.…` | 35 |
| `INSERT OR REPLACE`（无可用唯一键） | 普通 `INSERT` | 9 |
| `IFNULL()` | `COALESCE()` | 3 |
| `date('now')` | `CURRENT_DATE` | 3 |

**两处非机械改写：**

1. **触发器（31 个）** —— PG 的触发器体必须放在函数里。原写法 `AFTER UPDATE` 里回头
   `UPDATE` 自己，在 PG 下会**递归触发**（PG 触发器默认递归）。改成共用函数 + `BEFORE`：

   ```sql
   CREATE OR REPLACE FUNCTION fn_touch_row() RETURNS trigger AS $BODY$
   BEGIN
       NEW.updated_at := CURRENT_TIMESTAMP;
       NEW.row_version := OLD.row_version + 1;
       RETURN NEW;
   END;
   $BODY$ LANGUAGE plpgsql;
   ```
   全库时间列都叫 `updated_at`、版本列都叫 `row_version`，所以一个共用函数够用。

2. **序列对齐** —— 种子数据带显式主键插进 `SERIAL` 列，序列不会跟着前进；不对齐的话
   之后第一条自增插入就撞主键。最后一个迁移末尾对全部 39 个 `SERIAL` 表补 `setval`。

重新生成：`python build/sqlite2pg.py`。生成器遇到没见过的构造直接报错退出。
`PgMigrationTests` 保证两套迁移不漂移、没有 PG 9.2 之后的语法、触发器形式正确、序列已对齐。

## ⚠ 必须在真机上验证的假设

没有实例，这些都没验过：

1. **`ON CONFLICT` 是否可用。** openGauss 官方文档说支持，但社区有人反映 6.0 轻量版不支持。
   若不支持，那 73 处要改写成 `WHERE NOT EXISTS` / 先 `DELETE` 再 `INSERT`。
2. **认证。** openGauss 默认 SHA256，标准 Npgsql 握不了手。需在服务端设
   `password_encryption_type=1` 并**重设一次用户密码**启用 MD5；或把
   `OpenGaussDialect.CreateConnection` 换成 `OpenGauss.NET` 的连接类型。
3. **Npgsql 8 能否接受 openGauss 报出的 PG 9.2 版本号。** 若拒连，降到 Npgsql 6.x 或改用
   `OpenGauss.NET`（方言层就是为这种替换准备的）。
4. **`SERIAL` 与显式主键插入共存**是否有其他副作用。
5. **`session_replication_role`** 需要相应权限；拿不到就跳过，那时种子的跨表插入顺序必须自洽。

## 还没做

- **乐观锁接线**：21 处 UPDATE 带上 `AND row_version=@seen` + 界面冲突提示（方案 B）
- **连接保活重连**：现在连接开一次用到底，网络一抖全挂
- **站点配置机制** `/etc/pitmine3d/db.conf`：见 [DEPLOY.md](DEPLOY.md)
- **界面数据陈旧**：别人改了你的列表不会自动刷新，需定时或手动刷新（产品决定）
