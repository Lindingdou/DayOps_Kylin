# 在本地（Windows）测 openGauss

> 目的：不用麒麟服务器，就把 [PORTING.md](PORTING.md) 里标着"必须在真机上验证"的 5 条假设
> 一次性验掉。这些是静态检查证明不了的东西，只能连上去跑。

**别用 PostgreSQL 冒充。** 恰恰是那三个不确定项——SHA256 认证、`ON CONFLICT` 支持、
PG 9.2 版本号——用标准 PG 测会全部通过，给你假信心。要测就测真镜像。

---

## 1. 装 Docker Desktop

本机目前**没有 Docker 也没有 WSL**。装 Docker Desktop for Windows（会自动启用 WSL2 后端）。

## 2. 起 openGauss 容器

```bash
docker run --name opengauss --privileged=true -d -e GS_PASSWORD='Pitmine@2026' -p 15432:5432 enmotech/opengauss:latest
```

- `GS_PASSWORD` **必填**，规则是至少 8 位且包含大写、小写、数字、特殊符号
- `--privileged=true` 这个镜像要求要加
- 宿主机端口用 15432，避开本机可能已有的 5432

看日志确认起来了：

```bash
docker logs -f opengauss
```

## 3. 打开 MD5 认证（**关键，不做这步 Npgsql 连不上**）

openGauss 默认 SHA256，标准 PG 驱动握不了手。

```bash
docker exec -it opengauss bash
su - omm
gs_guc reload -N all -I all -c "password_encryption_type=1"
gs_guc reload -N all -I all -h "host all all 0.0.0.0/0 md5"
```

## 4. 建库建账号（**必须在改完认证之后**）

改 `password_encryption_type` 只对**之后新建或重设密码**的用户生效，顺序反了就白做。

```bash
gsql -d postgres -p 5432
```

```sql
CREATE USER pitmine WITH PASSWORD 'Pitmine@2026';
CREATE DATABASE pitmine OWNER pitmine;
```

## 5. 跑探针测试（几秒钟出结果）

回到 Windows 这边：

```bash
PITMINE_TEST_PG_CONN='Host=localhost;Port=15432;Database=pitmine;Username=pitmine;Password=Pitmine@2026' dotnet test tests/PitMine3D.Kylin.Tests --filter OpenGaussIntegrationTests
```

这一条命令逐条验：

| 用例 | 验的是 |
|---|---|
| `假设1和3_Npgsql能连上openGauss并报出服务器版本` | 认证通不通、Npgsql 8 接不接受 PG 9.2 版本号 |
| `假设2_ON_CONFLICT_DO_NOTHING可用` | 38 处 `INSERT OR IGNORE` 的翻译成不成立 |
| `假设2_ON_CONFLICT_DO_UPDATE可用` | 35 处 `INSERT OR REPLACE` 的翻译成不成立 |
| `假设4_SERIAL列显式插值后setval能把序列接上` | 种子带显式主键会不会把序列搞坏 |
| `假设5_共用触发器函数能自增行版本号` | 触发器不递归、乐观锁真能拦住过期版本 |

**不设 `PITMINE_TEST_PG_CONN` 时这些用例直接返回**，不影响日常全量跑。

## 6. 真跑一遍 52 份迁移

确认探针都过了再做这步——它会往库里建 50 多张表、灌 3 万多行种子：

```bash
PITMINE_TEST_PG_CONN='Host=localhost;Port=15432;Database=pitmine;Username=pitmine;Password=Pitmine@2026' PITMINE_TEST_PG_MIGRATE=1 dotnet test tests/PitMine3D.Kylin.Tests --filter 全部迁移能在真实openGauss上建起库
```

多加一道 `PITMINE_TEST_PG_MIGRATE` 开关是因为这是破坏性操作，**别指向生产库**。

跑通就说明整套移植在真实 openGauss 上成立。想重来：

```bash
docker rm -f opengauss
```

## 7. 用程序连

```bash
PITMINE_DB=opengauss PITMINE_DB_CONN='Host=localhost;Port=15432;Database=pitmine;Username=pitmine;Password=Pitmine@2026' dotnet run --project src/PitMine3D.Kylin.csproj
```

这时程序**只校验** schema 版本、不执行迁移（共享库上迁移是部署动作）。
然后去点各功能页，把应用层的报错记下来。

---

## 说明

上面 `gs_guc` 的具体参数在不同镜像版本可能略有出入，以 `docker logs` 和镜像自带文档为准。
第 5 步的探针测试是可靠的判据——它跑通了，就是真的通了。
