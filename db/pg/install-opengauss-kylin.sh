#!/bin/sh
# 麒麟服务器一键安装 openGauss 中心库（对应 INSTALL-KYLIN.md 的 0~8 步）
#
#   sudo ./install-opengauss-kylin.sh --package ~/openGauss-Lite-x86_64.tar.gz \
#        --password 'Pitmine@2026' --client-cidr 192.168.1.0/24
#
# 为什么要脚本：手册里第 6 步(改认证方式)必须在第 7 步(建账号)**之前**，顺序反了这一步白做，
# 而且现场很难看出错在哪；架构对不上会报 "executable format error"，是麒麟上最常见的第一个坑。
# 这两处都由脚本把住。
#
# 可重复执行：每步先探测是否已完成，已完成就跳过，中途失败修好后再跑一遍即可。
# 纯 sh，不依赖 bash 特性（麒麟的 /bin/sh 是 dash）。
set -eu

# ── 默认值 ─────────────────────────────────────────────────────────────
PKG=""
PASSWORD=""
CLIENT_CIDR=""
PORT=5432
DB_NAME=pitmine
DB_USER=pitmine
OS_USER=omm
OS_GROUP=dbgrp
INSTALL_ROOT=""
ASSUME_YES=0
SKIP_DEPS=0

RED=''; GRN=''; YEL=''; RST=''
if [ -t 1 ]; then RED=$(printf '\033[31m'); GRN=$(printf '\033[32m'); YEL=$(printf '\033[33m'); RST=$(printf '\033[0m'); fi
say()  { echo "${GRN}==>${RST} $*"; }
warn() { echo "${YEL}[注意]${RST} $*" >&2; }
die()  { echo "${RED}[失败]${RST} $*" >&2; exit 1; }

usage() {
    cat <<'USAGE'
用法: sudo ./install-opengauss-kylin.sh --package <openGauss 安装包> --password <密码> [选项]

必填:
  --package PATH        openGauss 安装包(.tar.gz)，架构必须与本机一致
  --password PASS       应用账号密码。规则: ≥8 位，含大写/小写/数字/特殊符号

可选:
  --client-cidr CIDR    放行的客户端网段，如 192.168.1.0/24。不给则只放行本机
  --port PORT           监听端口，默认 5432
  --db-name NAME        数据库名，默认 pitmine
  --db-user NAME        应用账号，默认 pitmine
  --os-user NAME        运行数据库的系统账号，默认 omm（不能用 root）
  --skip-deps           跳过装依赖（已经装过时用）
  -y, --yes             不交互确认
  -h, --help            显示本帮助

装完会打印客户端连接串。验证判据见 INSTALL-KYLIN.md 第 8 步的探针测试。
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --package)      PKG="${2:?--package 需要参数}"; shift 2 ;;
        --password)     PASSWORD="${2:?--password 需要参数}"; shift 2 ;;
        --client-cidr)  CLIENT_CIDR="${2:?--client-cidr 需要参数}"; shift 2 ;;
        --port)         PORT="${2:?--port 需要参数}"; shift 2 ;;
        --db-name)      DB_NAME="${2:?--db-name 需要参数}"; shift 2 ;;
        --db-user)      DB_USER="${2:?--db-user 需要参数}"; shift 2 ;;
        --os-user)      OS_USER="${2:?--os-user 需要参数}"; shift 2 ;;
        --skip-deps)    SKIP_DEPS=1; shift ;;
        -y|--yes)       ASSUME_YES=1; shift ;;
        -h|--help)      usage; exit 0 ;;
        *) die "未知参数: $1（--help 看用法）" ;;
    esac
done

# ── 0. 前置检查（这几条不过，后面全白做）────────────────────────────────
say "0/8 前置检查"

# 先校验参数本身 —— 敲错命令不该等到 sudo 之后才发现
[ -n "$PKG" ] || { usage; die "缺 --package"; }
[ -f "$PKG" ] || die "找不到安装包: $PKG"
[ -n "$PASSWORD" ] || { usage; die "缺 --password"; }

# 密码规则先在本地卡住，别等 openGauss 装到一半才报
pw_ok=1
echo "$PASSWORD" | grep -q '[A-Z]' || pw_ok=0
echo "$PASSWORD" | grep -q '[a-z]' || pw_ok=0
echo "$PASSWORD" | grep -q '[0-9]' || pw_ok=0
echo "$PASSWORD" | grep -q '[^A-Za-z0-9]' || pw_ok=0
[ "${#PASSWORD}" -ge 8 ] || pw_ok=0
[ "$pw_ok" = "1" ] || die "密码不合规: 需 ≥8 位且同时含 大写/小写/数字/特殊符号"

ARCH=$(uname -m)
say "  本机架构: $ARCH"
[ -r /etc/kylin-release ] && say "  系统: $(head -1 /etc/kylin-release)" || warn "  非麒麟系统(继续, 但步骤可能有出入)"

# 架构对不上是麒麟上最常见的坑，包名里通常带 x86_64 / aarch64
PKG_BASE=$(basename "$PKG")
case "$PKG_BASE" in
    *x86_64*|*X86_64*) PKG_ARCH=x86_64 ;;
    *aarch64*|*arm64*) PKG_ARCH=aarch64 ;;
    *)                 PKG_ARCH="" ;;
esac
if [ -n "$PKG_ARCH" ] && [ "$PKG_ARCH" != "$ARCH" ]; then
    die "架构不匹配: 本机 $ARCH, 包是 $PKG_ARCH。拿错架构的包会报 'executable format error'。"
fi
[ -n "$PKG_ARCH" ] || warn "  从包名看不出架构($PKG_BASE), 请自行确认与 $ARCH 一致"

FREE_KB=$(df -Pk /home 2>/dev/null | awk 'NR==2 {print $4}' || echo 0)
[ "${FREE_KB:-0}" -ge 5242880 ] || warn "  /home 可用空间不足 5G(当前 $((FREE_KB/1024))M), openGauss 可能装不下"

# 参数与架构都对了, 再要 root
[ "$(id -u)" = "0" ] || die "请用 root 运行(sudo)。装依赖、建用户、写 sysctl 都需要 root。"

if [ "$ASSUME_YES" != "1" ]; then
    echo
    echo "将执行: 装依赖 → 调内核参数 → 建系统账号 $OS_USER → 安装 openGauss → 开 MD5 认证 → 建库 $DB_NAME/账号 $DB_USER → 验证"
    printf "确认继续? [y/N] "
    read -r ans
    case "$ans" in y|Y|yes|YES) ;; *) die "已取消" ;; esac
fi

# ── 1. 依赖 ────────────────────────────────────────────────────────────
if [ "$SKIP_DEPS" = "1" ]; then
    say "1/8 装依赖(已按 --skip-deps 跳过)"
elif command -v yum >/dev/null 2>&1; then
    say "1/8 装依赖(yum)"
    yum install -y net-tools bzip2 bison python3 python3-devel libaio-devel \
        flex ncurses-devel pam-devel libffi-devel patch autoconf automake byacc cmake diffutils \
        >/dev/null || die "依赖安装失败, 请检查 yum 源"
elif command -v apt-get >/dev/null 2>&1; then
    say "1/8 装依赖(apt)"
    apt-get update -qq || true
    apt-get install -y net-tools bzip2 bison python3 python3-dev libaio-dev \
        flex libncurses-dev libpam0g-dev libffi-dev patch autoconf automake byacc cmake diffutils \
        >/dev/null || die "依赖安装失败, 请检查 apt 源"
else
    die "既没有 yum 也没有 apt-get, 无法自动装依赖。请手动装好后加 --skip-deps 重跑。"
fi

# ── 2. 系统配置 ────────────────────────────────────────────────────────
say "2/8 系统配置(内核信号量 / 字符集 / 端口放行)"
if ! grep -q '^kernel.sem' /etc/sysctl.conf 2>/dev/null; then
    echo 'kernel.sem = 250 32000 100 999' >> /etc/sysctl.conf
fi
sysctl -p >/dev/null 2>&1 || warn "  sysctl -p 有告警(通常不影响)"

# 只放行需要的端口，不整个关防火墙（手册里关防火墙是图省事，生产上不该那么做）
if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="$PORT/tcp" >/dev/null 2>&1 || true
    firewall-cmd --reload >/dev/null 2>&1 || true
    say "  已放行 $PORT/tcp（未整个关闭防火墙）"
elif command -v ufw >/dev/null 2>&1; then
    ufw allow "$PORT/tcp" >/dev/null 2>&1 || true
    say "  已放行 $PORT/tcp"
fi

# ── 3. 系统账号（openGauss 拒绝以 root 跑）──────────────────────────────
say "3/8 系统账号 $OS_USER"
getent group "$OS_GROUP" >/dev/null 2>&1 || groupadd "$OS_GROUP"
if getent passwd "$OS_USER" >/dev/null 2>&1; then
    say "  $OS_USER 已存在, 跳过"
else
    useradd -g "$OS_GROUP" -m "$OS_USER"
    say "  已建 $OS_USER"
fi
OS_HOME=$(getent passwd "$OS_USER" | cut -d: -f6)
[ -n "$OS_HOME" ] && [ -d "$OS_HOME" ] || die "取不到 $OS_USER 的家目录"
[ -n "$INSTALL_ROOT" ] || INSTALL_ROOT="$OS_HOME/openGauss"
DATA_DIR="$INSTALL_ROOT/data"

# ── 4. 安装 ────────────────────────────────────────────────────────────
say "4/8 安装 openGauss → $INSTALL_ROOT"
if [ -d "$DATA_DIR" ] && [ -f "$DATA_DIR/postgresql.conf" ]; then
    say "  检测到已有实例($DATA_DIR), 跳过安装"
else
    mkdir -p "$INSTALL_ROOT"
    cp "$PKG" "$INSTALL_ROOT/" || die "拷贝安装包失败"
    chown -R "$OS_USER:$OS_GROUP" "$INSTALL_ROOT"
    PKG_IN=$(basename "$PKG")

    su - "$OS_USER" -c "cd '$INSTALL_ROOT' && tar -zxf '$PKG_IN'" || die "解压失败"

    # 极简版(simpleInstall/install.sh)与轻量版(install.sh --mode single)入口不同，自动认
    INST=$(su - "$OS_USER" -c "find '$INSTALL_ROOT' -maxdepth 3 -name install.sh -type f 2>/dev/null | head -1")
    [ -n "$INST" ] || die "解压后找不到 install.sh, 请确认包是否完整"
    say "  安装脚本: $INST"

    if echo "$INST" | grep -q simpleInstall; then
        su - "$OS_USER" -c "cd \"\$(dirname '$INST')\" && sh ./install.sh -w '$PASSWORD' -p $PORT" \
            || die "极简版安装失败, 详见上方输出"
    else
        su - "$OS_USER" -c "cd \"\$(dirname '$INST')\" && echo '$PASSWORD' | sh ./install.sh --mode single -D '$DATA_DIR' -R '$INSTALL_ROOT/install' --start" \
            || die "轻量版安装失败, 详见上方输出"
    fi
fi

# 后续 gs_* / gsql 都要 omm 的环境变量，统一走登录 shell
ommsh() { su - "$OS_USER" -c "$1"; }

say "  实例状态:"
ommsh "gs_ctl query -D '$DATA_DIR'" 2>/dev/null | sed 's/^/    /' || warn "  gs_ctl 查不到状态(下一步会再验)"

# ── 5. 认证方式（**必须在建账号之前**）─────────────────────────────────
say "5/8 打开 MD5 认证与远程监听"
# openGauss 默认 SHA256，标准 Npgsql 握不了手；且此设置只对之后新建/重设密码的用户生效，
# 所以顺序绝不能和下一步调换。
ommsh "gs_guc reload -N all -I all -c \"password_encryption_type=1\"" >/dev/null 2>&1 \
    || ommsh "gs_guc set -D '$DATA_DIR' -c \"password_encryption_type=1\"" >/dev/null 2>&1 \
    || die "设置 password_encryption_type 失败"
ommsh "gs_guc reload -N all -I all -c \"listen_addresses='*'\"" >/dev/null 2>&1 \
    || ommsh "gs_guc set -D '$DATA_DIR' -c \"listen_addresses='*'\"" >/dev/null 2>&1 \
    || warn "  设置 listen_addresses 失败, 远程可能连不上"
ommsh "gs_guc set -D '$DATA_DIR' -c \"port=$PORT\"" >/dev/null 2>&1 || true

if [ -n "$CLIENT_CIDR" ]; then
    ommsh "gs_guc reload -N all -I all -h \"host all all $CLIENT_CIDR md5\"" >/dev/null 2>&1 \
        || ommsh "gs_guc set -D '$DATA_DIR' -h \"host all all $CLIENT_CIDR md5\"" >/dev/null 2>&1 \
        || warn "  写 pg_hba 规则失败, 请手动加: host all all $CLIENT_CIDR md5"
    say "  已放行网段 $CLIENT_CIDR (md5)"
else
    warn "  未给 --client-cidr, 只有本机能连。远程客户端需另加 pg_hba 规则。"
fi

ommsh "gs_ctl restart -D '$DATA_DIR'" >/dev/null 2>&1 || warn "  重启实例失败, 配置可能未生效"

# ── 6. 建库与应用账号（必须在第 5 步之后）──────────────────────────────
say "6/8 建数据库 $DB_NAME 与账号 $DB_USER"
gsql_root() { ommsh "gsql -d postgres -p $PORT -t -A -c \"$1\""; }

if gsql_root "SELECT 1 FROM pg_roles WHERE rolname='$DB_USER'" 2>/dev/null | grep -q 1; then
    say "  账号 $DB_USER 已存在, 重设密码(确保用 MD5 存储)"
    gsql_root "ALTER USER $DB_USER WITH PASSWORD '$PASSWORD'" >/dev/null || die "重设密码失败"
else
    gsql_root "CREATE USER $DB_USER WITH PASSWORD '$PASSWORD'" >/dev/null || die "建账号失败"
fi

if gsql_root "SELECT 1 FROM pg_database WHERE datname='$DB_NAME'" 2>/dev/null | grep -q 1; then
    say "  数据库 $DB_NAME 已存在, 跳过"
else
    gsql_root "CREATE DATABASE $DB_NAME OWNER $DB_USER" >/dev/null || die "建库失败"
fi

# ── 7. 自检（这一步才是判据）────────────────────────────────────────────
say "7/8 自检: 用应用账号连一次并做一次读写往返"
PROBE_SQL="CREATE TABLE IF NOT EXISTS _pitmine_probe(id serial primary key, note text);
INSERT INTO _pitmine_probe(note) VALUES ('ok');
SELECT count(*) FROM _pitmine_probe;
DROP TABLE _pitmine_probe;"
if ommsh "PGPASSWORD='$PASSWORD' gsql -h 127.0.0.1 -p $PORT -d $DB_NAME -U $DB_USER -t -A -c \"$PROBE_SQL\"" >/dev/null 2>&1; then
    say "  ${GRN}读写往返通过${RST}(建表/插入/查询/删表)"
else
    die "应用账号连不上或没有建表权限。
  常见原因: 第 5 步的 MD5 认证没生效(顺序反了)、pg_hba 未放行、端口不对。
  手工复现: su - $OS_USER -c \"gsql -h 127.0.0.1 -p $PORT -d $DB_NAME -U $DB_USER\""
fi

# ── 8. 收尾 ────────────────────────────────────────────────────────────
IP=$(hostname -I 2>/dev/null | awk '{print $1}')
[ -n "${IP:-}" ] || IP="<本机IP>"
say "8/8 完成"
cat <<EOF

  ${GRN}安装完成${RST}

  客户端连接串:
    Host=$IP;Port=$PORT;Database=$DB_NAME;Username=$DB_USER;Password=<你设的密码>

  下一步(在开发机上跑探针, 这才是最终判据):
    PITMINE_TEST_PG_CONN='Host=$IP;Port=$PORT;Database=$DB_NAME;Username=$DB_USER;Password=<密码>' \\
      dotnet test tests/PitMine3D.Kylin.Tests --filter OpenGaussIntegrationTests

  建库(套用全部迁移):
    PITMINE_TEST_PG_CONN='...' PITMINE_TEST_PG_MIGRATE=1 \\
      dotnet test tests/PitMine3D.Kylin.Tests --filter 全部迁移能在真实openGauss上建起库

  实例管理(以 $OS_USER 身份):
    su - $OS_USER -c "gs_ctl query -D $DATA_DIR"     # 看状态
    su - $OS_USER -c "gs_ctl stop  -D $DATA_DIR"     # 停
    su - $OS_USER -c "gs_ctl start -D $DATA_DIR"     # 起

EOF
