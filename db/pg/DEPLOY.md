# 部署指南（麒麟 + openGauss，局域网一库多客户端）

> 2026-09-08。**现在还不能直接部署到 openGauss** —— 迁移和方言层一次都没在真实实例上跑过。
> 下面按阶段给路径，阶段 1 才是你现在该做的事。

---

## 阶段 0：现在就能交付的形态（SQLite 单机）

这条路是通的、验过的，可先让用户用起来或做验收演示。

```bash
./build/publish-linux.sh linux-x64        # 自包含发布, 目标机不用装 .NET
./build/fetch-deps.sh                     # 取 app-local ICU(麒麟上 .NET 最常缺的依赖)
./build/make-deb.sh linux-x64 0.1.0       # 出包: dist/pitmine3d_0.1.0_amd64.deb
```

装到 `/opt/pitmine3d`，数据落 `~/.local/share/PitMine3D.Kylin/geo.db`，一人一个库，零运维。
ARM 机器把 RID 换成 `linux-arm64`。

---

## 阶段 1：在麒麟测试机把 openGauss 跑通（**你现在该做的**）

目的不是上线，是用几十分钟一次性验掉一堆猜测。

### 1.1 装库

openGauss 支持银河麒麟 V10 全架构（ARM64/x86_64），单机和集群都行。
**轻量版**砍掉 OM/CM 组件，单机安装一条命令：

```bash
echo <密码> | sh ./install.sh --mode single -D ~/openGauss/data -R ~/openGauss/install --start
```

注意别把 aarch64 的包装到 x86 机器上（会报 "not a binary file"）。

### 1.2 打开 MD5 认证（否则标准 Npgsql 连不上）

openGauss 默认用自己的 SHA256 认证，PG 生态的驱动握不了手。

```sql
-- postgresql.conf / gs_guc
password_encryption_type = 1        -- 0=仅MD5  1=MD5+SHA256  2=仅SHA256
```

**改完必须重设一次用户密码**，配置才对该用户生效：

```sql
CREATE USER pitmine PASSWORD '***';   -- 或 ALTER USER pitmine PASSWORD '***';
CREATE DATABASE pitmine OWNER pitmine;
```

`pg_hba.conf` 里给客户端网段放行 md5，并开放端口（默认 5432，openGauss 有时用 15400）。

> 若现场安全要求不允许开 MD5，把 `OpenGaussDialect.CreateConnection` 里的
> `NpgsqlConnection` 换成 `OpenGauss.NET` 的连接类型 —— 方言层就是为这种替换留的。

### 1.3 跑一次迁移

```bash
PITMINE_DB=opengauss \
PITMINE_DB_CONN='Host=127.0.0.1;Port=5432;Database=pitmine;Username=pitmine;Password=***' \
PITMINE_DB_MIGRATE=1 \
/opt/pitmine3d/PitMine3D.Kylin
```

**这一条命令会一次性证实或推翻 5 条假设**（见 [PORTING.md](PORTING.md)）：
`ON CONFLICT` 可不可用、认证通不通、Npgsql 8 接不接受 PG 9.2 版本号、
`SERIAL` 与显式主键共存、`session_replication_role` 权限。

跑通的标志：

```sql
SELECT COUNT(*) FROM _schema_migration;   -- 应为 52
```

### 1.4 用起来

迁移过了不等于程序能用。去点各功能页，把报错清单记下来。
按实测改比盲猜省事得多。

---

## 阶段 2：补齐三件

1. **按实测报错修**
2. **乐观锁接线** —— 21 处 UPDATE 带 `AND row_version=@seen` + 界面冲突提示。
   机制已就绪（V051 + `RowVersionTests` 验过），只差接线。
3. **连接保活重连** —— 现在连接开一次用到底，远程库网络一抖就全挂。

### 硬前提：连接串怎么下发（**还没做**）

现状只能靠环境变量 `PITMINE_DB_CONN`，里面带密码。十几台客户端不可能挨个设，
明文密码散在每台机器上也不合适。现有配置都落在 `~/.local/share/PitMine3D.Kylin/*.json`，
那是**每用户**位置，而连接串是**站点级**部署设置。

要做成：

- 站点配置 `/etc/pitmine3d/db.conf`，权限 `root:pitmine 0640`，**部署时放置，不进 .deb**
- 读取优先级：环境变量 > `/etc/pitmine3d/db.conf` > 默认 SQLite
- 数据库账号用**专用低权限账号**，只授本库增删改查，不要用超级用户

---

## 阶段 3：局域网正式部署

**服务器（一台）**

1. 装 openGauss、按 1.2 配好认证与账号
2. 开放端口给客户端网段
3. **跑一次迁移**（`PITMINE_DB_MIGRATE=1`），只在这台上跑

**客户端（每台）**

1. `dpkg -i pitmine3d_<版本>_<架构>.deb`
2. 放站点配置 `/etc/pitmine3d/db.conf`
3. 启动 —— 程序只**校验** schema 版本，不执行迁移

客户端若报"数据库 schema 落后于本程序，缺 N 个迁移"，说明库还没升级，**先别用**。
这是刻意设计的：带着不匹配的 schema 跑会写坏数据。

**升级顺序不能反**

```
1. 先在服务器上跑迁移升级库    PITMINE_DB_MIGRATE=1
2. 再分发新版客户端 .deb
```

> **旧客户端 + 新库**这个组合程序不拦（库里版本比程序新不算"缺"）。
> 靠发版纪律管：要么全升，要么确认旧客户端不写改过的表。

---

## 部署前检查清单

- [ ] openGauss 认证：`password_encryption_type=1` 且已重设用户密码
- [ ] `pg_hba.conf` 放行客户端网段，端口已开
- [ ] 52 个迁移全部登记
- [ ] 应用层报错已按实测处理
- [ ] 乐观锁已接线（否则多人同时改会静默覆盖）
- [ ] 连接重连已做（否则网络抖动即崩）
- [ ] `/etc/pitmine3d/db.conf` 机制已实现，账号非超级用户
- [ ] 授权：openGauss 本身免费（木兰宽松许可，可商用），但**社区版没有安全可靠测评资质**；
      若招标要求进名单，须换成通过测评的商业发行版（华为 GaussDB / 海量数据 Vastbase /
      云和恩墨 MogDB）—— 它们都是 openGauss 内核，本套迁移可直接复用
