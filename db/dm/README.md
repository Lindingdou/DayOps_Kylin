# 达梦 DM8 环境配置（麒麟 · x86-64）

把达梦装在**麒麟测试机**上——它既是现在换库开发的连测实例，又是未来的部署目标。
开发/移植阶段**用免费开发版即可，零授权成本**；上生产再谈正式授权。

> 你只需先做一件事：到 **dameng.com（达梦技术社区）注册 → 下载 DM8 开发版**
> （Linux x86-64 安装包 **或** Docker 镜像 tar），接受授权。拿到后按下面走。

---

## 关键初始化参数（务必设对，事后难改）

无论装原生还是 Docker，实例初始化时这几个参数直接影响移植难度：

| 参数 | 取值 | 为什么 |
|------|------|--------|
| `CHARSET` | **1 = UTF-8** | 中文数据的命根子，建库后不可改 |
| `LENGTH_IN_CHAR` | **1（按字符）** | `VARCHAR(10)` = 10 个汉字而非 10 字节 |
| `CASE_SENSITIVE` | **0（不区分大小写）** | 贴近 SQLite 行为，少踩标识符大小写坑 |
| `COMPATIBLE_MODE` | **MySQL 兼容** | 让 `LIMIT/OFFSET`、自增等贴近 SQLite，**大幅减少方言翻译**（具体数值以你的 DM8 版本手册为准，通常 MySQL 模式） |

---

## 路线 A：原生安装包（推荐，就是部署形态）

```bash
# 1) 建专用用户（root 执行）
groupadd dinstall && useradd -g dinstall -m -d /home/dmdba dmdba
passwd dmdba

# 2) 挂载/解压安装包，以 dmdba 运行控制台安装（装到 /opt/dmdbms）
su - dmdba
./DMInstall.bin -i            # 按提示选择典型安装，路径填 /opt/dmdbms

# 3) 初始化实例（关键参数见上表）
cd /opt/dmdbms/bin
./dminit PATH=/opt/dmdbms/data DB_NAME=PITMINE INSTANCE_NAME=DMSERVER \
         PORT_NUM=5236 CHARSET=1 CASE_SENSITIVE=0 LENGTH_IN_CHAR=1

# 4) 注册为系统服务并启动（root 执行 script/root 下的脚本）
cd /opt/dmdbms/script/root
./dm_service_installer.sh -t dmserver -dm_ini /opt/dmdbms/data/PITMINE/dm.ini -p DMSERVER
systemctl start DmServiceDMSERVER
systemctl enable DmServiceDMSERVER
```

> 各版本安装器细节（是否交互、root 脚本名）可能略有差异，以你下载版本的《安装手册》为准。
> `COMPATIBLE_MODE` 若 `dminit` 未接受，可在 `dm.ini` 里设好后重启服务。

## 路线 B：Docker 镜像（若麒麟上有 docker）

```bash
docker load -i dm8_<版本>.tar          # 加载官方镜像 tar
docker run -d --name dm8 --restart=always -p 5236:5236 \
   -e PAGE_SIZE=16 -e LOG_SIZE=1024 -e UNICODE_FLAG=1 -e INSTANCE_NAME=DMSERVER \
   <镜像名>:<标签>
# 具体环境变量以镜像自带 README 为准（UNICODE_FLAG=1 即 UTF-8）
docker logs -f dm8                      # 看到实例 open 即就绪
```

---

## 建库建用户（实例起来后，一次性）

以 `SYSDBA` 连上，执行本目录的 `01_bootstrap.sql`：

```bash
/opt/dmdbms/bin/disql SYSDBA/SYSDBA@localhost:5236 `pwd`/01_bootstrap.sql
#           ↑ 若初始化时设了 SYSDBA_PWD，用你设的密码
```

它会建：表空间 `PITMINE`、应用用户/模式 `PITMINE`、开发期授权。
应用的 14 张业务表**不在这里建**——由（移植后的）`MigrationRunner` 跑 `V*.sql` 脚本自动建（对应任务 P5-1）。

## 连通冒烟（验证 UTF-8 + 自增 + 时间戳）

```sql
-- 以应用用户登录
-- /opt/dmdbms/bin/disql PITMINE/"PitMine_2026#dm"@localhost:5236
CREATE TABLE t_ping(id INT IDENTITY(1,1) PRIMARY KEY, name VARCHAR(50), ts TIMESTAMP DEFAULT CURRENT_TIMESTAMP);
INSERT INTO t_ping(name) VALUES('麒麟-达梦-连通正常');
SELECT id, name, ts FROM t_ping;    -- 应出中文且 id 自增
DROP TABLE t_ping;
```

中文正常显示、`id` 自增出值 = 环境 OK。

---

## 应用侧连接（供 P5-1 换 provider 用）

- **NuGet**：达梦官方 .NET 驱动 `DM.DMProvider`（`Dm` 命名空间；随 DM8 一起提供，或社区 nuget，版本对齐 .NET 8）。
- **连接串**（示例，键名以 DmProvider 文档为准）：
  ```
  Server=<麒麟机IP>;Port=5236;User Id=PITMINE;PWD=PitMine_2026#dm;Schema=PITMINE;
  ```
- Dapper 照常工作（`DmConnection` 实现 `IDbConnection`）；换库落点集中在 `SqlLib/Internal/ConnectionFactory.cs`（见任务 P5-1）。

> Windows 开发机连测：把连接串的 `Server` 指向麒麟机 IP，防火墙放行 5236 即可跨机连测。
