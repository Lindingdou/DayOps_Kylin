#!/bin/sh
# 麒麟服务器一键安装达梦 DM8 中心库（对应 README.md 路线 A 的全部步骤）
#
#   sudo ./install-dm8-kylin.sh --media ~/dm8_20240712_x86_rh6_64.iso \
#        --sysdba-password 'Pitmine@2026dm' --client-cidr 192.168.114.0/24
#
# 为什么要脚本：达梦的安装分成**三个身份**——安装要用 dmdba(装不成 root)、注册服务要回 root、
# 建库要用 SYSDBA；顺序错了会卡在很难看懂的地方。更要紧的是 dminit 那几个参数
# (CHARSET / CASE_SENSITIVE / LENGTH_IN_CHAR) **建库后改不了**，装错只能推倒重来。
# 这两件事由脚本把住。
#
# 可重复执行：每步先探测是否已完成，已完成就跳过，中途失败修好后再跑一遍即可。
# 纯 sh，不依赖 bash 特性（麒麟的 /bin/sh 是 dash）。
set -eu

# ── 默认值 ─────────────────────────────────────────────────────────────
MEDIA=""
SYSDBA_PWD=""
APP_PWD=""
CLIENT_CIDR=""
PORT=5236
DB_NAME=PITMINE
DB_USER=PITMINE
INSTANCE=DMSERVER
INSTALL_PATH=/opt/dmdbms
DATA_PATH=""
COMPAT_MODE=""
PAGE_SIZE=16
OS_USER=dmdba
OS_GROUP=dinstall
ASSUME_YES=0
SKIP_DEPS=0

RED=''; GRN=''; YEL=''; RST=''
if [ -t 1 ]; then RED=$(printf '\033[31m'); GRN=$(printf '\033[32m'); YEL=$(printf '\033[33m'); RST=$(printf '\033[0m'); fi
say()  { echo "${GRN}==>${RST} $*"; }
warn() { echo "${YEL}[注意]${RST} $*" >&2; }
die()  { echo "${RED}[失败]${RST} $*" >&2; exit 1; }

usage() {
    cat <<'USAGE'
用法: sudo ./install-dm8-kylin.sh --media <DM8安装介质> --sysdba-password <口令> [选项]

必填:
  --media PATH             DM8 安装介质。支持 .iso / .zip / .tar.gz，
                           也可直接指向解开后的 DMInstall.bin
  --sysdba-password PASS   SYSDBA 口令。DM8 默认口令策略要求 ≥9 位

可选:
  --app-password PASS      应用账号口令，不给则与 SYSDBA 同一个
  --client-cidr CIDR       放行的客户端网段，如 192.168.114.0/24。不给则只开端口不限源
  --port PORT              监听端口，默认 5236
  --db-name NAME           数据库名，默认 PITMINE
  --db-user NAME           应用账号(达梦里 用户=模式)，默认 PITMINE
  --instance NAME          实例名，决定服务名 DmService<实例名>，默认 DMSERVER
  --install-path PATH      软件安装目录，默认 /opt/dmdbms
  --data-path PATH         数据目录，默认 <install-path>/data
  --compatible-mode N      兼容模式取值(见下)，不给则用达梦原生语法
  --page-size N            页大小 KB，默认 16。建库后不可改
  --os-user NAME           运行数据库的系统账号，默认 dmdba（不能用 root）
  --os-group NAME          该账号的组，默认 dinstall
  --skip-deps              跳过装依赖
  -y, --yes                不交互确认
  -h, --help               显示本帮助

关于 --compatible-mode:
  各版本取值不一样(MySQL/Oracle 等的编号会变)。脚本会在初始化前把**你这个版本**的
  dminit 帮助里 COMPATIBLE_MODE 那几行打出来，照着填。不填就是达梦原生 ——
  迁移脚本本来就要为达梦单独生成一套，原生模式反而最可预期。

装完会打印客户端连接串与自检结果。
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --media)            MEDIA="${2:?--media 需要参数}"; shift 2 ;;
        --sysdba-password)  SYSDBA_PWD="${2:?--sysdba-password 需要参数}"; shift 2 ;;
        --app-password)     APP_PWD="${2:?--app-password 需要参数}"; shift 2 ;;
        --client-cidr)      CLIENT_CIDR="${2:?--client-cidr 需要参数}"; shift 2 ;;
        --port)             PORT="${2:?--port 需要参数}"; shift 2 ;;
        --db-name)          DB_NAME="${2:?--db-name 需要参数}"; shift 2 ;;
        --db-user)          DB_USER="${2:?--db-user 需要参数}"; shift 2 ;;
        --instance)         INSTANCE="${2:?--instance 需要参数}"; shift 2 ;;
        --install-path)     INSTALL_PATH="${2:?--install-path 需要参数}"; shift 2 ;;
        --data-path)        DATA_PATH="${2:?--data-path 需要参数}"; shift 2 ;;
        --compatible-mode)  COMPAT_MODE="${2:?--compatible-mode 需要参数}"; shift 2 ;;
        --page-size)        PAGE_SIZE="${2:?--page-size 需要参数}"; shift 2 ;;
        --os-user)          OS_USER="${2:?--os-user 需要参数}"; shift 2 ;;
        --os-group)         OS_GROUP="${2:?--os-group 需要参数}"; shift 2 ;;
        --skip-deps)        SKIP_DEPS=1; shift ;;
        -y|--yes)           ASSUME_YES=1; shift ;;
        -h|--help)          usage; exit 0 ;;
        *) die "未知参数: $1（--help 看用法）" ;;
    esac
done

[ -n "$DATA_PATH" ] || DATA_PATH="$INSTALL_PATH/data"
[ -n "$APP_PWD" ] || APP_PWD="$SYSDBA_PWD"
DB_DIR="$DATA_PATH/$DB_NAME"
SERVICE="DmService$INSTANCE"

# 全程要用的临时目录，退出时收拾干净（含可能挂上的 ISO）
WORKDIR=""
MOUNTED=""
cleanup() {
    [ -n "$MOUNTED" ] && umount "$MOUNTED" 2>/dev/null || true
    [ -n "$WORKDIR" ] && rm -rf "$WORKDIR" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# ── 0. 前置检查（这几条不过，后面全白做）────────────────────────────────
say "0/8 前置检查"

[ -n "$MEDIA" ] || { usage; die "缺 --media"; }
[ -e "$MEDIA" ] || die "找不到安装介质: $MEDIA"
[ -n "$SYSDBA_PWD" ] || { usage; die "缺 --sysdba-password"; }

# 口令规则先在本地卡住 —— 达梦默认 PWD_POLICY 要求长度 ≥9，
# 等 dminit 跑到一半才报错的话，前面建的目录还得手工清。
[ "${#SYSDBA_PWD}" -ge 9 ] || die "SYSDBA 口令不足 9 位。DM8 默认口令策略(PWD_POLICY)要求 ≥9 位。"
[ "${#APP_PWD}"    -ge 9 ] || die "应用账号口令不足 9 位。"
echo "$SYSDBA_PWD" | grep -q '[A-Za-z]' || warn "  SYSDBA 口令没有字母，部分策略下会被拒"
echo "$SYSDBA_PWD" | grep -q '[0-9]'    || warn "  SYSDBA 口令没有数字，部分策略下会被拒"

# 口令要经过 shell 引用和 SQL 的双引号字面量两道手，含 ' 或 " 时必然被截断，
# 而截断后的报错是"口令错误"，查半天也想不到是引号的事。这里直接拦下。
case "$SYSDBA_PWD$APP_PWD" in
    *\'*|*\"*) die "口令里不能含单引号或双引号（会被 shell/SQL 的引用截断）。请换个特殊符号，如 @ # % _ - +" ;;
esac

ARCH=$(uname -m)
say "  本机架构: $ARCH"
if [ -r /etc/kylin-release ]; then say "  系统: $(head -1 /etc/kylin-release)"; else warn "  非麒麟系统(继续, 但步骤可能有出入)"; fi

# 架构对不上是最常见的第一个坑：达梦的包名里带 x86 / aarch64 / arm
MEDIA_BASE=$(basename "$MEDIA")
case "$MEDIA_BASE" in
    *aarch64*|*arm64*|*arm*) MEDIA_ARCH=aarch64 ;;
    *x86*|*X86*)             MEDIA_ARCH=x86_64 ;;
    *)                       MEDIA_ARCH="" ;;
esac
if [ -n "$MEDIA_ARCH" ] && [ "$MEDIA_ARCH" != "$ARCH" ]; then
    die "架构不匹配: 本机 $ARCH, 介质看着是 $MEDIA_ARCH($MEDIA_BASE)。装上去会报 'cannot execute binary file'。"
fi
[ -n "$MEDIA_ARCH" ] || warn "  从文件名看不出架构($MEDIA_BASE), 请自行确认与 $ARCH 一致"

# 达梦装完约 1.5G，数据目录另算；留 5G 是保守值
PARENT=$(dirname "$INSTALL_PATH")
FREE_KB=$(df -Pk "$PARENT" 2>/dev/null | awk 'NR==2 {print $4}' || echo 0)
[ "${FREE_KB:-0}" -ge 5242880 ] || warn "  $PARENT 可用空间不足 5G(当前 $((FREE_KB/1024))M)"

# 端口占用先看一眼 —— 5236 被占时服务能注册上但起不来，报错在日志深处
if command -v ss >/dev/null 2>&1 && ss -lnt 2>/dev/null | awk '{print $4}' | grep -q ":$PORT\$"; then
    die "端口 $PORT 已被占用。换 --port 或先停掉占用者(ss -lntp | grep $PORT)。"
fi

[ "$(id -u)" = "0" ] || die "请用 root 运行(sudo)。建用户、写 limits、注册服务都需要 root。"

if [ "$ASSUME_YES" != "1" ]; then
    echo
    echo "将执行: 装依赖 → 调 ulimit/放行端口 → 建系统账号 $OS_USER → 安装到 $INSTALL_PATH"
    echo "        → 初始化实例 $DB_NAME(端口 $PORT) → 注册服务 $SERVICE → 建账号 $DB_USER → 自检"
    printf "确认继续? [y/N] "
    read -r ans
    case "$ans" in y|Y|yes|YES) ;; *) die "已取消" ;; esac
fi

# ── 1. 依赖 ────────────────────────────────────────────────────────────
# 达梦的安装器是自解压二进制，本身依赖极少；这里只补展开介质要用的工具。
if [ "$SKIP_DEPS" = "1" ]; then
    say "1/8 装依赖(已按 --skip-deps 跳过)"
elif command -v yum >/dev/null 2>&1; then
    say "1/8 装依赖(yum)"
    yum install -y unzip tar util-linux glibc >/dev/null || die "依赖安装失败, 请检查 yum 源"
elif command -v apt-get >/dev/null 2>&1; then
    say "1/8 装依赖(apt)"
    apt-get update -qq || true
    apt-get install -y unzip tar util-linux >/dev/null || die "依赖安装失败, 请检查 apt 源"
else
    warn "1/8 既没有 yum 也没有 apt-get, 跳过装依赖(unzip/mount 需自行确保可用)"
fi

# ── 2. 系统配置 ────────────────────────────────────────────────────────
say "2/8 系统配置(文件句柄上限 / 端口放行)"
# 达梦对 open files 要求高，默认 1024 在并发多客户端时会报 "too many open files"，
# 而那时的报错长得完全不像资源问题，很难往这里想。
if ! grep -q "^$OS_USER .*nofile" /etc/security/limits.conf 2>/dev/null; then
    cat >> /etc/security/limits.conf <<LIMITS
$OS_USER soft nofile 65536
$OS_USER hard nofile 65536
$OS_USER soft nproc  65536
$OS_USER hard nproc  65536
LIMITS
    say "  已写入 $OS_USER 的 nofile/nproc 上限(65536)"
else
    say "  limits 已配过, 跳过"
fi

if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    if [ -n "$CLIENT_CIDR" ]; then
        firewall-cmd --permanent --add-rich-rule="rule family=ipv4 source address=$CLIENT_CIDR port port=$PORT protocol=tcp accept" >/dev/null 2>&1 || true
        say "  已放行 $CLIENT_CIDR → $PORT/tcp"
    else
        firewall-cmd --permanent --add-port="$PORT/tcp" >/dev/null 2>&1 || true
        say "  已放行 $PORT/tcp(未限源, 建议用 --client-cidr 收紧)"
    fi
    firewall-cmd --reload >/dev/null 2>&1 || true
elif command -v ufw >/dev/null 2>&1; then
    if [ -n "$CLIENT_CIDR" ]; then
        ufw allow from "$CLIENT_CIDR" to any port "$PORT" proto tcp >/dev/null 2>&1 || true
    else
        ufw allow "$PORT/tcp" >/dev/null 2>&1 || true
    fi
    say "  已放行 $PORT/tcp"
fi

# ── 3. 系统账号（达梦拒绝以 root 安装）─────────────────────────────────
say "3/8 系统账号 $OS_USER"
getent group "$OS_GROUP" >/dev/null 2>&1 || groupadd "$OS_GROUP"
if getent passwd "$OS_USER" >/dev/null 2>&1; then
    say "  $OS_USER 已存在, 跳过"
else
    useradd -g "$OS_GROUP" -m -d "/home/$OS_USER" "$OS_USER"
    say "  已建 $OS_USER"
fi
mkdir -p "$INSTALL_PATH" "$DATA_PATH"
chown -R "$OS_USER:$OS_GROUP" "$INSTALL_PATH" "$DATA_PATH"

# ── 4. 安装 ────────────────────────────────────────────────────────────
say "4/8 安装达梦 → $INSTALL_PATH"
if [ -x "$INSTALL_PATH/bin/dminit" ]; then
    say "  检测到已安装($INSTALL_PATH/bin/dminit), 跳过"
else
    WORKDIR=$(mktemp -d /tmp/dm8install.XXXXXX)
    chmod 755 "$WORKDIR"

    # 介质三种形态各自展开，最终都要落到一个 DMInstall.bin
    case "$MEDIA" in
        *.iso|*.ISO)
            MOUNTED="$WORKDIR/iso"
            mkdir -p "$MOUNTED"
            mount -o loop,ro "$MEDIA" "$MOUNTED" || die "挂载 ISO 失败: $MEDIA"
            say "  已挂载 ISO"
            ;;
        *.zip|*.ZIP)
            unzip -q "$MEDIA" -d "$WORKDIR/unzip" || die "解压 zip 失败"
            MOUNTED="" ;;
        *.tar.gz|*.tgz|*.tar)
            mkdir -p "$WORKDIR/untar"
            tar -xf "$MEDIA" -C "$WORKDIR/untar" || die "解压 tar 失败"
            MOUNTED="" ;;
        *DMInstall.bin|*.bin)
            mkdir -p "$WORKDIR/bin"
            cp "$MEDIA" "$WORKDIR/bin/DMInstall.bin" || die "拷贝安装器失败"
            MOUNTED="" ;;
        *)
            die "认不出介质类型: $MEDIA（支持 .iso / .zip / .tar.gz / DMInstall.bin）" ;;
    esac

    INSTALLER=$(find "$WORKDIR" -maxdepth 4 -name 'DMInstall.bin' -type f 2>/dev/null | head -1)
    [ -n "$INSTALLER" ] || die "介质里找不到 DMInstall.bin。请确认下载的是 DM8 安装包而不是客户端/驱动包。"
    say "  安装器: $INSTALLER"

    # ISO 是只读的，安装器要可执行，先复制出来
    RUNDIR="$WORKDIR/run"
    mkdir -p "$RUNDIR"
    cp "$INSTALLER" "$RUNDIR/DMInstall.bin"
    chmod +x "$RUNDIR/DMInstall.bin"

    # 静默安装应答文件。KEY 留空 = 试用/开发授权(有效期以你下载的版本为准)；
    # 拿到正式 lic 后填其路径即可。INIT_DB=0: 这里只装软件，实例由下一步 dminit 建 ——
    # 让安装器顺手建库的话，那几个"事后改不了"的参数就用不上我们指定的值了。
    cat > "$RUNDIR/dm_install.xml" <<XML
<?xml version="1.0" encoding="UTF-8"?>
<DATABASE>
    <LANGUAGE>zh</LANGUAGE>
    <TIME_ZONE>+08:00</TIME_ZONE>
    <KEY></KEY>
    <INSTALL_TYPE>0</INSTALL_TYPE>
    <INSTALL_PATH>$INSTALL_PATH</INSTALL_PATH>
    <INIT_DB>0</INIT_DB>
</DATABASE>
XML
    chown -R "$OS_USER:$OS_GROUP" "$RUNDIR"

    say "  静默安装中(约 1~3 分钟)…"
    su - "$OS_USER" -c "cd '$RUNDIR' && ./DMInstall.bin -q dm_install.xml" \
        || die "安装失败。常见原因: 介质架构不符、$INSTALL_PATH 权限不对、磁盘不足。
  可改用交互安装排查: su - $OS_USER -c \"cd '$RUNDIR' && ./DMInstall.bin -i\""

    [ -x "$INSTALL_PATH/bin/dminit" ] || die "安装器跑完了但 $INSTALL_PATH/bin/dminit 不在, 请看上方输出"
    say "  安装完成"
fi

DM_BIN="$INSTALL_PATH/bin"
dmsh() { su - "$OS_USER" -c "export LD_LIBRARY_PATH='$DM_BIN:\${LD_LIBRARY_PATH:-}'; $1"; }

# ── 5. 初始化实例（**这几个参数建库后改不了**）─────────────────────────
say "5/8 初始化实例 $DB_NAME"
if [ -f "$DB_DIR/dm.ini" ]; then
    say "  检测到已有实例($DB_DIR/dm.ini), 跳过初始化"
    warn "  若上次的初始化参数不对(字符集/大小写敏感), 只能删掉 $DB_DIR 重来"
else
    if [ -n "$COMPAT_MODE" ]; then
        COMPAT_ARG="COMPATIBLE_MODE=$COMPAT_MODE"
    else
        COMPAT_ARG=""
        # 把这个版本自己的取值表打出来 —— 各版本编号不一致，照抄别人的数字会踩坑
        say "  本版本 dminit 支持的 COMPATIBLE_MODE 取值:"
        dmsh "'$DM_BIN/dminit' HELP" 2>/dev/null | grep -i -A2 'COMPATIBLE_MODE' | sed 's/^/    /' || true
        say "  未指定 --compatible-mode, 按达梦原生语法初始化"
    fi

    # CHARSET=1(UTF-8) / CASE_SENSITIVE=0(标识符不分大小写) / LENGTH_IN_CHAR=1(VARCHAR 按字符计)
    # 这三个**建库后不可改**：字符集错了中文全乱，大小写敏感错了全库表名要改，
    # LENGTH_IN_CHAR 错了 VARCHAR(10) 只装得下 3 个汉字。
    dmsh "'$DM_BIN/dminit' PATH='$DATA_PATH' DB_NAME=$DB_NAME INSTANCE_NAME=$INSTANCE \
          PORT_NUM=$PORT CHARSET=1 CASE_SENSITIVE=0 LENGTH_IN_CHAR=1 \
          PAGE_SIZE=$PAGE_SIZE SYSDBA_PWD='$SYSDBA_PWD' $COMPAT_ARG" \
        || die "dminit 失败, 详见上方输出"
    [ -f "$DB_DIR/dm.ini" ] || die "dminit 跑完了但 $DB_DIR/dm.ini 不在"
    say "  已初始化: UTF-8 / 不区分大小写 / VARCHAR 按字符 / 页 ${PAGE_SIZE}K"
fi

# ── 6. 注册服务并启动 ──────────────────────────────────────────────────
say "6/8 注册系统服务 $SERVICE"
if systemctl list-unit-files 2>/dev/null | grep -q "^$SERVICE"; then
    say "  服务已注册, 跳过"
else
    INSTALLER_SH="$INSTALL_PATH/script/root/dm_service_installer.sh"
    [ -x "$INSTALLER_SH" ] || die "找不到 $INSTALLER_SH（安装是否完整?）"
    "$INSTALLER_SH" -t dmserver -dm_ini "$DB_DIR/dm.ini" -p "$INSTANCE" >/dev/null \
        || die "注册服务失败"
    say "  已注册"
fi

systemctl enable "$SERVICE" >/dev/null 2>&1 || warn "  设置开机自启失败"
systemctl start "$SERVICE" >/dev/null 2>&1 || true

# 达梦起来要几秒，等它把库 open 了再往下走，否则下一步连不上会误判成装错了
i=0
while [ $i -lt 30 ]; do
    if ss -lnt 2>/dev/null | awk '{print $4}' | grep -q ":$PORT\$"; then break; fi
    i=$((i + 1)); sleep 1
done
if [ $i -ge 30 ]; then
    systemctl status "$SERVICE" --no-pager 2>&1 | sed 's/^/    /' || true
    die "服务起来了但 $PORT 端口 30 秒内没监听。日志: $DB_DIR/log/ 下的 dm_${INSTANCE}_*.log"
fi
say "  服务已启动, $PORT 端口在听"

# ── 7. 建表空间与应用账号 ──────────────────────────────────────────────
say "7/8 建表空间/账号 $DB_USER"
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
BOOTSTRAP="$SCRIPT_DIR/01_bootstrap.sql"
[ -f "$BOOTSTRAP" ] || die "找不到 $BOOTSTRAP（请把本脚本与 01_bootstrap.sql 一起拷到服务器）"

# 单一来源: 建库 DDL 只写在 01_bootstrap.sql 里，这里只做占位符替换。
# 手工执行那条路径(见 README)用的是同一个文件，两边不会漂。
WORKDIR2=$(mktemp -d /tmp/dmboot.XXXXXX)
chmod 755 "$WORKDIR2"
# 口令里常有 / & 这类 sed 的元字符，不转义的话替换出来的是另一个口令，
# 后面自检会以"口令错误"收场 —— 排查方向完全错。
esc_sed() { printf '%s' "$1" | sed -e 's/[\\/&]/\\&/g'; }
sed -e "s/__DB_USER__/$(esc_sed "$DB_USER")/g" \
    -e "s/__DB_PASSWORD__/$(esc_sed "$APP_PWD")/g" \
    "$BOOTSTRAP" > "$WORKDIR2/bootstrap.sql"
chown -R "$OS_USER:$OS_GROUP" "$WORKDIR2"

# SQL 一律走标准输入喂给 disql，不用它的反引号跑脚本语法 ——
# 那个反引号要穿过 su -c 的第二层 shell，会被当成命令替换，且报错完全不像引用问题。
# 重定向由 root 这层做，dmdba 读不读得到临时文件也就无所谓了。
run_sql() {   # $1 = 登录串(如 SYSDBA/'口令')  $2 = SQL 文件
    su - "$OS_USER" -c "export LD_LIBRARY_PATH='$DM_BIN:\${LD_LIBRARY_PATH:-}'; '$DM_BIN/disql' -S $1@localhost:$PORT" < "$2"
}

# 已经建过就跳过 —— 重复 CREATE USER 会报错，而那报错看着像口令策略问题，容易带偏。
# 用带前缀的字符串回显而不是裸的 COUNT(*): disql 的输出里还有别的数字，直接抓数字会误判。
printf "SELECT 'PROBE_USER_CNT_' || TO_CHAR(COUNT(*)) FROM DBA_USERS WHERE USERNAME='%s';\n" "$DB_USER" > "$WORKDIR2/exists.sql"
EXIST_OUT=$(run_sql "SYSDBA/'$SYSDBA_PWD'" "$WORKDIR2/exists.sql" 2>&1 || true)
if echo "$EXIST_OUT" | grep -q 'PROBE_USER_CNT_0'; then
    run_sql "SYSDBA/'$SYSDBA_PWD'" "$WORKDIR2/bootstrap.sql" 2>&1 | sed 's/^/    /' \
        || die "建库脚本执行失败, 详见上方输出"
    say "  表空间与账号已建"
elif echo "$EXIST_OUT" | grep -q 'PROBE_USER_CNT_'; then
    say "  账号 $DB_USER 已存在, 跳过建库脚本"
else
    echo "$EXIST_OUT" | sed 's/^/    /'
    die "以 SYSDBA 连不上实例。口令不对? 端口不对? 服务没起来?
  手工复现: su - $OS_USER -c \"$DM_BIN/disql SYSDBA/口令@localhost:$PORT\""
fi
rm -rf "$WORKDIR2"

# ── 8. 自检（这一步才是判据）────────────────────────────────────────────
say "8/8 自检: 用应用账号连一次, 验中文/自增/时间戳"
PROBE_OUT=$(dmsh "'$DM_BIN/disql' -S $DB_USER/'$APP_PWD'@localhost:$PORT <<'SQL'
CREATE TABLE t_pitmine_probe(id INT IDENTITY(1,1) PRIMARY KEY, note VARCHAR(100), ts TIMESTAMP DEFAULT CURRENT_TIMESTAMP);
INSERT INTO t_pitmine_probe(note) VALUES('麒麟-达梦-连通正常');
SELECT id || '|' || note FROM t_pitmine_probe;
DROP TABLE t_pitmine_probe;
SQL" 2>&1 || true)

echo "$PROBE_OUT" | sed 's/^/    /'
if echo "$PROBE_OUT" | grep -q '1|麒麟-达梦-连通正常'; then
    say "  ${GRN}读写往返通过${RST}(建表 / IDENTITY 自增 / UTF-8 中文 / 删表)"
else
    die "自检没通过。中文变问号 = 字符集初始化错了(要 CHARSET=1，只能删库重来);
  连不上 = 口令策略或端口; 权限报错 = 01_bootstrap.sql 的授权没跑到。"
fi

IP=$(hostname -I 2>/dev/null | awk '{print $1}')
[ -n "${IP:-}" ] || IP="<本机IP>"
cat <<EOF

  ${GRN}达梦 DM8 安装完成${RST}

  客户端连接串(DmProvider):
    Server=$IP;Port=$PORT;User Id=$DB_USER;PWD=<你设的应用口令>;Schema=$DB_USER

  下一步: 把上面这个连接串交给开发机, 跑换库探针
    (@ 占位符 / 多语句 / MERGE / IDENTITY 与显式主键共存 / UTF-8 —— 见 README「应用侧连接」)

  实例管理:
    systemctl status  $SERVICE
    systemctl restart $SERVICE
    su - $OS_USER -c "$DM_BIN/disql $DB_USER/口令@localhost:$PORT"

  日志: $DB_DIR/log/

EOF
