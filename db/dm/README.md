# 达梦 DM8 服务端安装（麒麟 · x86-64）

把达梦装在**麒麟机**上——既是换库开发的连测实例，也是未来的部署目标。
开发/移植阶段用**免费开发版**即可，零授权成本；上生产和集群再谈正式授权。

> **状态**：安装脚本已就绪但**尚未在真机上跑过**——本机没有 DM8 实例，麒麟机上也没有
> （`192.168.114.131:5236` 不通，那台跑的是 openGauss）。脚本里每一步的判据都写好了，
> 第一次跑出来的报错请贴回来，按现场修。

---

## 你要先做的一件事：拿到安装介质

到 **达梦官网/技术社区（dameng.com · eco.dameng.com）注册账号 → 下载中心 → DM8 开发版 →
Linux x86-64** ，接受授权后下载 `.iso`（或 `.zip`）。

这一步必须你本人做：要注册账号、要勾选接受授权协议，这两件事不能代办。
介质拿到后放到麒麟机上（`scp` 过去即可），剩下的交给脚本。

> 架构要与麒麟机一致。当前那台是 x86-64（VMware 虚机），别下成 aarch64 的包——
> 装上去会报 `cannot execute binary file`，脚本第 0 步会先替你拦下。

---

## 一键安装

把 `install-dm8-kylin.sh` 和 `01_bootstrap.sql` **一起**拷到麒麟机同一目录，然后：

```bash
chmod +x install-dm8-kylin.sh
sudo ./install-dm8-kylin.sh \
     --media ~/dm8_20240712_x86_rh6_64.iso \
     --sysdba-password 'Pitmine@2026dm' \
     --client-cidr 192.168.114.0/24
```

脚本做八件事，每步先探测是否已完成，**中途失败修好后再跑一遍即可**（不会重复建东西）：

| 步 | 做什么 | 为什么要脚本管 |
|---|---|---|
| 0 | 前置检查 | 架构/端口占用/口令位数/引号，**全在动手之前**拦下 |
| 1 | 装依赖 | 只补展开介质用的 `unzip`/`mount` |
| 2 | ulimit + 防火墙 | `nofile` 默认 1024，多客户端时报的错完全不像资源问题 |
| 3 | 建 `dmdba:dinstall` | 达梦**拒绝以 root 安装** |
| 4 | 静默安装 | 生成应答 XML，`DMInstall.bin -q`。`INIT_DB=0`——实例留给下一步自己建 |
| 5 | `dminit` 初始化 | **本脚本存在的最大理由**，见下表 |
| 6 | 注册服务并启动 | `dm_service_installer.sh` 要回 root 身份跑；起完等端口真在听才算数 |
| 7 | 建表空间/账号 | 跑 `01_bootstrap.sql`（占位符替换后） |
| 8 | 自检 | 建表→插中文→读回→删表，**这一步才是判据** |

### 关键初始化参数（务必设对，事后改不了）

| 参数 | 脚本取值 | 设错的后果 |
|------|------|--------|
| `CHARSET` | **1 = UTF-8** | 中文全变问号，**只能删库重来** |
| `LENGTH_IN_CHAR` | **1（按字符）** | `VARCHAR(10)` 只装得下 3 个汉字 |
| `CASE_SENSITIVE` | **0（不区分大小写）** | 全库小写表名要逐个加引号 |
| `PAGE_SIZE` | 16 KB（`--page-size` 可改） | 大行放不下 |
| `COMPATIBLE_MODE` | 默认不设（达梦原生） | 见下 |

**关于兼容模式**：各版本的取值编号不一样（MySQL/Oracle 对应几号会变），照抄别人的数字会踩坑。
脚本在初始化前会把**你这个版本**的 `dminit HELP` 里 `COMPATIBLE_MODE` 那几行打出来，
要用就 `--compatible-mode N` 传进去。不传就是原生——反正迁移脚本本来就要为达梦单独生成一套，
原生模式最可预期。

---

## 路线 B：Docker（若麒麟上装了 docker）

当前麒麟机上没有 docker，本机也没有。若你打算用容器：

```bash
docker load -i dm8_<版本>.tar
docker run -d --name dm8 --restart=always -p 5236:5236 \
   -e PAGE_SIZE=16 -e LOG_SIZE=1024 -e UNICODE_FLAG=1 -e INSTANCE_NAME=DMSERVER \
   <镜像名>:<标签>
docker logs -f dm8            # 看到实例 open 即就绪
```

环境变量以镜像自带 README 为准（`UNICODE_FLAG=1` 即 UTF-8）。
容器方便，但**不是部署形态**——生产还是走路线 A。

---

## 手工建库（不想用脚本时）

`01_bootstrap.sql` 里留了两个占位符，自己替换掉再跑：

```bash
sed -e 's/__DB_USER__/PITMINE/g' -e 's/__DB_PASSWORD__/Pitmine@2026dm/g' \
    01_bootstrap.sql > /tmp/boot.sql
/opt/dmdbms/bin/disql -S SYSDBA/'你的SYSDBA口令'@localhost:5236 < /tmp/boot.sql
```

它建：表空间 `PITMINE`、应用用户/模式 `PITMINE`、开发期授权。
**业务表不在这里建**——由迁移脚本 `V*.sql` 建（换库任务 P5-1）。

> 用 `< 文件` 喂标准输入，别用 disql 的反引号跑脚本语法：反引号穿过 `su -c` 的第二层 shell
> 会被当成命令替换，报出来的错完全不像引用问题。

## 连通冒烟

```sql
CREATE TABLE t_ping(id INT IDENTITY(1,1) PRIMARY KEY, name VARCHAR(50), ts TIMESTAMP DEFAULT CURRENT_TIMESTAMP);
INSERT INTO t_ping(name) VALUES('麒麟-达梦-连通正常');
SELECT id, name, ts FROM t_ping;    -- 中文正常 + id 自增 = 环境 OK
DROP TABLE t_ping;
```

脚本第 8 步已经自动跑过这一段，手工装才需要自己跑。

---

## 应用侧连接

驱动**已经在本机 NuGet 缓存里**（`DM.DmProvider 8.3.1.47463`，Apache-2.0，含 `net8.0` 目标），
离线也能编译，不必再找包。

```
Server=<麒麟机IP>;Port=5236;User Id=PITMINE;PWD=<口令>;Schema=PITMINE
```

已离线核实的两件事（反射 + IL 扫描，见换库评估）：

- `DmConnection` 直接继承 `System.Data.Common.DbConnection` → 能接进现有方言接缝
  （[`src/Data/GeoDbDialect.cs`](../../src/Data/GeoDbDialect.cs)），加一个子类即可。
- `DmParameterCollection` 的取参把 `@x` 和 `:x` **归一成同一个名字**，
  全库 671 处 `@` 占位符很可能一处不用改。
  ⚠ 但 SQL 文本里的占位符最终由服务端解析，这一条**必须连上真机再确认**。

其余待验项（多语句能否一次执行 / `MERGE INTO` 覆盖 73 处 upsert / `IDENTITY` 与种子显式主键共存
/ UTF-8 与 `LENGTH_IN_CHAR` 实际行为）都要等实例起来才能定。
