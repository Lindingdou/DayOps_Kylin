# 在银河麒麟上装 openGauss（中心服务器）

> 用途：给局域网内的多客户端做中心库。装完按 [LOCAL-TEST.md](LOCAL-TEST.md) 第 5 步跑探针验证。
>
> 说明：下面的步骤综合自鲲鹏社区和 openGauss 社区的麒麟适配实践，**我没有在真机上跑过**，
> 具体包名和路径随麒麟版本（V10 SP1/SP2/SP3）和 openGauss 版本会有出入。
> 真正的判据是最后一步的探针测试。

---

## 0. 先确认架构（**弄错这步后面全白做**）

```bash
uname -m          # x86_64 或 aarch64
cat /etc/kylin-release
```

包必须和架构对上。**拿 aarch64 的包在 x86 机器上跑会报 `not a binary file, executable format error`** —— 这是麒麟上最常见的第一个坑。

到 [openGauss 下载页](https://opengauss.org/zh/download/) 取对应架构的包。

## 1. 选哪个版本

| | 适用 | 装法 |
|---|---|---|
| **极简版 simpleInstall** | 开发测试、单机验证 | 一条 `install.sh`，最快 |
| **轻量版 lite** | 单机生产，资源受限 | `install.sh --mode single`，无 OM/CM 组件 |
| 企业版 | 集群、主备高可用 | `gs_preinstall` + `gs_install`，要写 XML 配置 |

**你这个场景（几十张表、4 万行数据、十几个客户端）用极简版或轻量版就够**，不需要企业版那套。数据量小到不构成任何压力。

## 2. 装依赖

麒麟 V10 服务器版（yum 系）：

```bash
sudo yum install -y net-tools bzip2 bison python3 python3-devel libaio-devel \
    flex ncurses-devel pam-devel libffi-devel patch autoconf automake byacc cmake diffutils
```

桌面版若是 apt 系，换成对应的 `-dev` 包名。

## 3. 系统配置

内核信号量：

```bash
echo 'kernel.sem = 250 32000 100 999' | sudo tee -a /etc/sysctl.conf
sudo sysctl -p
```

关防火墙和 SELinux（**测试环境**这样最省事；生产环境应改为只放行 5432 给客户端网段）：

```bash
sudo systemctl stop firewalld && sudo systemctl disable firewalld
sudo setenforce 0
```

字符集要是 UTF-8：

```bash
echo 'export LANG=en_US.UTF-8' >> ~/.bashrc && source ~/.bashrc
```

内存小于 4G 的机器，安装期间先关 swap，装完再开。

## 4. 建专用用户（**不能用 root 装**）

openGauss 拒绝以 root 身份运行数据库进程：

```bash
sudo groupadd dbgrp
sudo useradd -g dbgrp -m omm
sudo passwd omm
```

## 5. 安装

把包传到机器上，解压后切到 `omm` 用户执行。轻量版单机：

```bash
su - omm
tar -zxvf openGauss-*-Lite-*.tar.gz -C ~/openGauss
cd ~/openGauss
echo 'Pitmine@2026' | sh ./install.sh --mode single -D ~/openGauss/data -R ~/openGauss/install --start
```

极简版（simpleInstall 目录下）：

```bash
sh ./install.sh -w 'Pitmine@2026' -p 5432
```

密码规则：至少 8 位，含大写、小写、数字、特殊符号。

验证起来了：

```bash
gs_ctl query -D ~/openGauss/data
gsql -d postgres -p 5432 -c "SELECT version();"
```

## 6. 打开 MD5 认证（**不做这步 .NET 客户端连不上**）

openGauss 默认 SHA256 认证，标准 Npgsql 握不了手。

```bash
gs_guc reload -N all -I all -c "password_encryption_type=1"
gs_guc reload -N all -I all -c "listen_addresses='*'"
gs_guc reload -N all -I all -h "host all all 192.168.1.0/24 md5"     # 换成你的客户端网段
```

## 7. 建库和账号（**必须在第 6 步之后**）

`password_encryption_type` 只对**之后新建或重设密码**的用户生效，顺序反了这一步白做。

```bash
gsql -d postgres -p 5432
```

```sql
CREATE USER pitmine WITH PASSWORD 'Pitmine@2026';
CREATE DATABASE pitmine OWNER pitmine;
```

**别用超级用户 omm 给应用连**，就用这个 `pitmine` 低权限账号。

## 8. 验证（这一步才是判据）

回到开发机：

```bash
PITMINE_TEST_PG_CONN='Host=<麒麟机IP>;Port=5432;Database=pitmine;Username=pitmine;Password=Pitmine@2026' dotnet test tests/PitMine3D.Kylin.Tests --filter OpenGaussIntegrationTests
```

5 条探针全绿，说明认证、`ON CONFLICT`、`SERIAL` 序列、触发器都成立。然后建库：

```bash
PITMINE_TEST_PG_CONN='...' PITMINE_TEST_PG_MIGRATE=1 dotnet test tests/PitMine3D.Kylin.Tests --filter 全部迁移能在真实openGauss上建起库
```

---

## 常见坑

| 现象 | 原因 |
|---|---|
| `not a binary file, executable format error` | 包的架构和机器不符（第 0 步） |
| `GAUSS_ENV` 环境变量报错 | `omm` 用户的 `.bashrc` 没 source，重新登录一次 |
| 客户端连接被拒 | `listen_addresses` 没开 `*`，或 pg_hba 没放行客户端网段 |
| 认证失败但密码没错 | 用户是在改 `password_encryption_type` **之前**建的，重设一次密码即可 |
| 装到一半提示不能用 root | 切到 `omm` 用户（第 4 步） |
