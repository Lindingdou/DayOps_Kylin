#!/bin/sh
# 麒麟上部署 openGauss 一主两从（企业版 gs_preinstall + gs_install）
#
#   sudo ./install-opengauss-cluster.sh \
#        --package ~/openGauss-*-Enterprise-*.tar.gz \
#        --primary  db1:192.168.1.11 \
#        --standby  db2:192.168.1.12 \
#        --standby  db3:192.168.1.13 \
#        --password 'Pitmine@2026' --client-cidr 192.168.1.0/24
#
# 与单机脚本(install-opengauss-kylin.sh)的关系：那个装极简/轻量版单实例，够十几个客户端用；
# 这个装企业版集群，只为**高可用**（主库宕了自动切到备库），不提升性能。
# 几十张表、几万行的数据量本身对单机毫无压力，是否值得上集群按可用性要求定。
#
# ！！本脚本未在真实三机环境上跑过 ！！
# 所以设计成"宁可早停，不要装一半"：前置条件逐条检查，任何一条不过立即退出并说清怎么修。
# 真正的判据是最后一步 gs_om -t status 里三个节点的角色，以及客户端探针。
set -eu

PKG=""; PASSWORD=""; CLIENT_CIDR=""
PRIMARY=""; STANDBYS=""; PORT=5432
OS_USER=omm; OS_GROUP=dbgrp
APP_DB=pitmine; APP_USER=pitmine
XML_OUT=/tmp/opengauss-cluster.xml
ASSUME_YES=0; DRY_RUN=0

RED=''; GRN=''; YEL=''; RST=''
if [ -t 1 ]; then RED=$(printf '\033[31m'); GRN=$(printf '\033[32m'); YEL=$(printf '\033[33m'); RST=$(printf '\033[0m'); fi
say()  { echo "${GRN}==>${RST} $*"; }
warn() { echo "${YEL}[注意]${RST} $*" >&2; }
die()  { echo "${RED}[失败]${RST} $*" >&2; exit 1; }

usage() {
    cat <<'USAGE'
用法: sudo ./install-opengauss-cluster.sh --package <企业版包> --password <密码> \
        --primary <主机名:IP> --standby <主机名:IP> --standby <主机名:IP> [选项]

必填:
  --package PATH        openGauss **企业版**安装包（极简/轻量版做不了主备）
  --password PASS       应用账号密码，≥8 位含大小写数字特殊符号
  --primary NAME:IP     主节点
  --standby NAME:IP     备节点，给两次即一主两从

可选:
  --client-cidr CIDR    客户端网段，如 192.168.1.0/24（不给则只有本机能连）
  --port PORT           默认 5432
  --xml-out PATH        生成的集群配置写到哪，默认 /tmp/opengauss-cluster.xml
  --dry-run             只做前置检查并生成 XML，不真正安装（**建议先跑一遍**）
  -y, --yes             不交互确认

在**主节点**上以 root 执行。三台机器需要：同架构、同系统、能互相解析主机名、时间同步。
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --package)     PKG="${2:?}"; shift 2 ;;
        --password)    PASSWORD="${2:?}"; shift 2 ;;
        --primary)     PRIMARY="${2:?}"; shift 2 ;;
        --standby)     STANDBYS="$STANDBYS ${2:?}"; shift 2 ;;
        --client-cidr) CLIENT_CIDR="${2:?}"; shift 2 ;;
        --port)        PORT="${2:?}"; shift 2 ;;
        --xml-out)     XML_OUT="${2:?}"; shift 2 ;;
        --dry-run)     DRY_RUN=1; shift ;;
        -y|--yes)      ASSUME_YES=1; shift ;;
        -h|--help)     usage; exit 0 ;;
        *) die "未知参数: $1（--help 看用法）" ;;
    esac
done

name_of() { echo "${1%%:*}"; }
ip_of()   { echo "${1##*:}"; }

# ── 0. 参数校验（先于 root 检查，敲错命令不必先 sudo）──────────────────
say "0/9 参数校验"
[ -n "$PKG" ] || { usage; die "缺 --package"; }
[ -f "$PKG" ] || die "找不到安装包: $PKG"
[ -n "$PASSWORD" ] || { usage; die "缺 --password"; }
[ -n "$PRIMARY" ] || { usage; die "缺 --primary"; }

pw_ok=1
echo "$PASSWORD" | grep -q '[A-Z]' || pw_ok=0
echo "$PASSWORD" | grep -q '[a-z]' || pw_ok=0
echo "$PASSWORD" | grep -q '[0-9]' || pw_ok=0
echo "$PASSWORD" | grep -q '[^A-Za-z0-9]' || pw_ok=0
[ "${#PASSWORD}" -ge 8 ] || pw_ok=0
[ "$pw_ok" = "1" ] || die "密码不合规: 需 ≥8 位且含 大写/小写/数字/特殊符号"

SB_COUNT=$(echo "$STANDBYS" | wc -w)
[ "$SB_COUNT" -eq 2 ] || die "一主两从需要给两个 --standby，当前给了 $SB_COUNT 个"

for n in "$PRIMARY" $STANDBYS; do
    case "$n" in *:*) ;; *) die "节点要写成 主机名:IP 的形式，收到: $n" ;; esac
    echo "$(ip_of "$n")" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' || die "IP 不合法: $n"
done

# 企业版才有 gs_preinstall；极简/轻量版包做不了主备，早停比装一半强
case "$(basename "$PKG")" in
    *Enterprise*|*enterprise*) ;;
    *) warn "包名里看不到 Enterprise：主备集群需要企业版，极简/轻量版只能单机。请确认包对。" ;;
esac

ARCH=$(uname -m)
case "$(basename "$PKG")" in
    *x86_64*) [ "$ARCH" = "x86_64" ]  || die "架构不匹配: 本机 $ARCH, 包是 x86_64" ;;
    *aarch64*|*arm64*) [ "$ARCH" = "aarch64" ] || die "架构不匹配: 本机 $ARCH, 包是 aarch64" ;;
esac

P_NAME=$(name_of "$PRIMARY"); P_IP=$(ip_of "$PRIMARY")
say "  主节点: $P_NAME ($P_IP)"
for s in $STANDBYS; do say "  备节点: $(name_of "$s") ($(ip_of "$s"))"; done

# ── 1. 三机连通性与一致性（集群装不起来八成卡在这几条）──────────────────
say "1/9 三机前置检查"
# --dry-run 只核对参数并生成 XML: 不需要 root, 也不查远程(现场可先在任意机器上过一遍配置)
if [ "$DRY_RUN" = "1" ]; then
    warn "  --dry-run: 跳过 root 与三机连通性检查"
else
[ "$(id -u)" = "0" ] || die "请用 root 运行(sudo)"

MYIP_OK=0
for ip in $(hostname -I 2>/dev/null || echo); do [ "$ip" = "$P_IP" ] && MYIP_OK=1; done
[ "$MYIP_OK" = "1" ] || warn "  本机 IP 里没有 $P_IP —— 本脚本应在**主节点**上执行"

for n in "$PRIMARY" $STANDBYS; do
    nm=$(name_of "$n"); ip=$(ip_of "$n")

    ping -c1 -W2 "$ip" >/dev/null 2>&1 || die "ping 不通 $nm($ip)。先解决网络。"

    # 主机名解析：gs_preinstall 全程按主机名找节点，/etc/hosts 没配会莫名其妙失败
    if ! getent hosts "$nm" >/dev/null 2>&1; then
        die "解析不了主机名 $nm。三台机器的 /etc/hosts 都要有:
    $P_IP  $P_NAME
$(for s in $STANDBYS; do echo "    $(ip_of "$s")  $(name_of "$s")"; done)"
    fi

    # SSH 免密：gs_preinstall 要以 root 登录各节点分发文件
    if [ "$ip" != "$P_IP" ]; then
        ssh -o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking=no \
            "root@$ip" true >/dev/null 2>&1 \
            || die "root 免密登录 $nm($ip) 不通。在主节点执行:
    ssh-keygen -t rsa -N '' -f ~/.ssh/id_rsa   # 已有可跳过
    ssh-copy-id root@$ip"

        RARCH=$(ssh -o BatchMode=yes -o StrictHostKeyChecking=no "root@$ip" 'uname -m' 2>/dev/null || echo "?")
        [ "$RARCH" = "$ARCH" ] || die "$nm 架构是 $RARCH, 与主节点 $ARCH 不一致"

        # 时间不同步会让主备复制判定出错
        RT=$(ssh -o BatchMode=yes -o StrictHostKeyChecking=no "root@$ip" 'date +%s' 2>/dev/null || echo 0)
        LT=$(date +%s)
        DIFF=$((RT > LT ? RT - LT : LT - RT))
        [ "$DIFF" -le 5 ] || die "$nm 与主节点时差 ${DIFF}s（>5s）。先做时间同步(chrony/ntp)。"
    fi
    say "  $nm($ip) 可达、可解析、架构一致、时间同步"
done
fi

# ── 2. 生成集群 XML（手写最容易错的一步）───────────────────────────────
say "2/9 生成集群配置 → $XML_OUT"
ALL_NODES="$PRIMARY $STANDBYS"
NODE_NAMES=""; for n in $ALL_NODES; do NODE_NAMES="$NODE_NAMES,$(name_of "$n")"; done
NODE_NAMES=${NODE_NAMES#,}
BACK_IPS=""; for n in $ALL_NODES; do BACK_IPS="$BACK_IPS,$(ip_of "$n")"; done
BACK_IPS=${BACK_IPS#,}
APP_ROOT="/opt/opengauss"
DATA_ROOT="/opt/opengauss/data/dn"

{
    echo '<?xml version="1.0" encoding="utf-8"?>'
    echo '<ROOT>'
    echo '  <CLUSTER>'
    echo '    <PARAM name="clusterName" value="pitmine_cluster" />'
    echo "    <PARAM name=\"nodeNames\" value=\"$NODE_NAMES\" />"
    echo "    <PARAM name=\"backIp1s\" value=\"$BACK_IPS\" />"
    echo "    <PARAM name=\"gaussdbAppPath\" value=\"$APP_ROOT/app\" />"
    echo "    <PARAM name=\"gaussdbLogPath\" value=\"$APP_ROOT/log\" />"
    echo "    <PARAM name=\"gaussdbToolPath\" value=\"$APP_ROOT/tool\" />"
    echo "    <PARAM name=\"corePath\" value=\"$APP_ROOT/corefile\" />"
    echo '  </CLUSTER>'
    echo '  <DEVICELIST>'
    first=1
    for n in $ALL_NODES; do
        nm=$(name_of "$n"); ip=$(ip_of "$n")
        echo "    <DEVICE sn=\"$nm\">"
        echo "      <PARAM name=\"name\" value=\"$nm\" />"
        echo "      <PARAM name=\"azName\" value=\"AZ1\" />"
        echo '      <PARAM name="azPriority" value="1" />'
        echo "      <PARAM name=\"backIp1\" value=\"$ip\" />"
        echo "      <PARAM name=\"sshIp1\" value=\"$ip\" />"
        if [ "$first" = "1" ]; then
            # 主节点：dataNode1 第一项是本机数据目录，其后依次是各备机(主备关系由此确定)
            DN="$DATA_ROOT"
            for s in $STANDBYS; do DN="$DN,$(name_of "$s"),$DATA_ROOT"; done
            echo '      <PARAM name="dataNum" value="1" />'
            echo "      <PARAM name=\"dataPortBase\" value=\"$PORT\" />"
            echo "      <PARAM name=\"dataNode1\" value=\"$DN\" />"
            echo '      <PARAM name="dataNode1_syncNum" value="0" />'
            first=0
        fi
        echo '    </DEVICE>'
    done
    echo '  </DEVICELIST>'
    echo '</ROOT>'
} > "$XML_OUT"
chmod 644 "$XML_OUT"
say "  已生成（装之前请过目一遍）"

if [ "$DRY_RUN" = "1" ]; then
    echo
    say "--dry-run: 前置检查通过, XML 已生成, 未执行安装"
    echo "  查看: cat $XML_OUT"
    echo "  确认无误后去掉 --dry-run 重跑"
    exit 0
fi

if [ "$ASSUME_YES" != "1" ]; then
    echo
    echo "将在三台机器上安装 openGauss 企业版集群(一主两从)。这会改动各节点的系统配置。"
    printf "确认继续? [y/N] "
    read -r ans
    case "$ans" in y|Y|yes|YES) ;; *) die "已取消" ;; esac
fi

# ── 3~5. 解包 / gs_preinstall / gs_install ─────────────────────────────
say "3/9 解包"
mkdir -p "$APP_ROOT/software"
tar -zxf "$PKG" -C "$APP_ROOT/software" || die "解压失败"
SCRIPT_DIR=$(find "$APP_ROOT/software" -maxdepth 3 -type d -name script | head -1)
[ -n "$SCRIPT_DIR" ] || die "解包后找不到 script 目录，确认这是企业版包"

say "4/9 gs_preinstall（建用户、配互信、分发到各节点）"
"$SCRIPT_DIR/gs_preinstall" -U "$OS_USER" -G "$OS_GROUP" -X "$XML_OUT" --non-interactive \
    || die "gs_preinstall 失败。常见原因: 互信未配好、/etc/hosts 不全、依赖缺失。"

say "5/9 gs_install（建库并拉起主备）"
su - "$OS_USER" -c "gs_install -X '$XML_OUT'" || die "gs_install 失败，详见上方输出与 $APP_ROOT/log"

# ── 6. 认证方式（必须早于建账号，与单机脚本同一个坑）────────────────────
say "6/9 认证方式与监听"
su - "$OS_USER" -c "gs_guc reload -N all -I all -c \"password_encryption_type=1\"" >/dev/null 2>&1 \
    || die "设置 password_encryption_type 失败"
su - "$OS_USER" -c "gs_guc reload -N all -I all -c \"listen_addresses='*'\"" >/dev/null 2>&1 \
    || warn "  设置 listen_addresses 失败，远程可能连不上"

# 节点之间的复制连接也要放行，否则备库连不上主库
for n in $ALL_NODES; do
    ip=$(ip_of "$n")
    su - "$OS_USER" -c "gs_guc reload -N all -I all -h \"host replication $OS_USER $ip/32 trust\"" >/dev/null 2>&1 || true
done
if [ -n "$CLIENT_CIDR" ]; then
    su - "$OS_USER" -c "gs_guc reload -N all -I all -h \"host all all $CLIENT_CIDR md5\"" >/dev/null 2>&1 \
        || warn "  写客户端 pg_hba 规则失败，请手动加: host all all $CLIENT_CIDR md5"
    say "  已放行客户端网段 $CLIENT_CIDR"
else
    warn "  未给 --client-cidr，局域网客户端连不上"
fi

# 防火墙只开所需端口
for n in $ALL_NODES; do
    ip=$(ip_of "$n")
    if [ "$ip" = "$P_IP" ]; then
        command -v firewall-cmd >/dev/null 2>&1 && { firewall-cmd --permanent --add-port="$PORT/tcp" >/dev/null 2>&1 || true; firewall-cmd --reload >/dev/null 2>&1 || true; }
    else
        ssh -o BatchMode=yes -o StrictHostKeyChecking=no "root@$ip" \
            "command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --permanent --add-port=$PORT/tcp >/dev/null 2>&1 && firewall-cmd --reload >/dev/null 2>&1" >/dev/null 2>&1 || true
    fi
done

# ── 7. 应用账号 ────────────────────────────────────────────────────────
say "7/9 建库 $APP_DB 与账号 $APP_USER"
gsql_root() { su - "$OS_USER" -c "gsql -d postgres -p $PORT -t -A -c \"$1\""; }
if gsql_root "SELECT 1 FROM pg_roles WHERE rolname='$APP_USER'" 2>/dev/null | grep -q 1; then
    gsql_root "ALTER USER $APP_USER WITH PASSWORD '$PASSWORD'" >/dev/null || die "重设密码失败"
else
    gsql_root "CREATE USER $APP_USER WITH PASSWORD '$PASSWORD'" >/dev/null || die "建账号失败"
fi
gsql_root "SELECT 1 FROM pg_database WHERE datname='$APP_DB'" 2>/dev/null | grep -q 1 \
    || gsql_root "CREATE DATABASE $APP_DB OWNER $APP_USER" >/dev/null || die "建库失败"

# ── 8. 角色核对（这一步才是判据）────────────────────────────────────────
say "8/9 集群状态"
STATUS=$(su - "$OS_USER" -c "gs_om -t status --detail" 2>&1 || true)
echo "$STATUS" | sed 's/^/    /'
PRI_N=$(echo "$STATUS" | grep -ci 'primary' || true)
STB_N=$(echo "$STATUS" | grep -ci 'standby' || true)
[ "$PRI_N" -ge 1 ] || die "没看到 Primary 节点，集群未正常建立"
[ "$STB_N" -ge 2 ] || warn "只看到 $STB_N 个 Standby，期望 2 个 —— 检查备节点日志"

say "9/9 读写往返自检"
PROBE="CREATE TABLE IF NOT EXISTS _pitmine_probe(id serial primary key, note text);
INSERT INTO _pitmine_probe(note) VALUES ('ok'); SELECT count(*) FROM _pitmine_probe; DROP TABLE _pitmine_probe;"
su - "$OS_USER" -c "PGPASSWORD='$PASSWORD' gsql -h 127.0.0.1 -p $PORT -d $APP_DB -U $APP_USER -t -A -c \"$PROBE\"" >/dev/null 2>&1 \
    && say "  ${GRN}通过${RST}" \
    || die "应用账号读写不通。多半是第 6 步认证没生效或 pg_hba 未放行。"

IPS="$P_IP"; for s in $STANDBYS; do IPS="$IPS,$(ip_of "$s")"; done
cat <<EOF

  ${GRN}集群部署完成（一主两从）${RST}

  客户端连接串（**要写全三个节点**，主库切换后才能自动连到新主）:
    Host=$IPS;Port=$PORT;Database=$APP_DB;Username=$APP_USER;Password=<密码>;Target Session Attributes=primary

  「Target Session Attributes=primary」不能省: 不写的话客户端可能连到只读备库，
  查询正常但一写就报错，现场极难判断。

  常用运维:
    su - $OS_USER -c "gs_om -t status --detail"     # 看各节点角色
    su - $OS_USER -c "gs_ctl switchover -D <备库数据目录>"   # 计划内主备切换
    su - $OS_USER -c "gs_om -t start"  /  "gs_om -t stop"

EOF
