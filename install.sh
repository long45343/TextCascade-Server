#!/bin/sh
# TextCascade Server — Linux 一键安装脚本（systemd + 静态单二进制）
#
# 从 GitHub Release 下载发行包。先下载脚本再执行（不要用 curl | sh：
# 脚本需要用标准输入接收密码）：
#
#   curl -fsSL https://raw.githubusercontent.com/long45343/TextCascade-Server/go/install.sh \
#     -o /tmp/tc-install.sh && sudo sh /tmp/tc-install.sh
#
#   sudo sh /tmp/tc-install.sh install --version 1.0.0 \
#        --cert fullchain.pem --key privkey.pem --username alice
#   sudo sh /tmp/tc-install.sh upgrade            # 升级到最新正式版
#   sudo sh /tmp/tc-install.sh upgrade --version 1.0.0-Alpha
#   sudo sh /tmp/tc-install.sh status
#   sudo sh /tmp/tc-install.sh uninstall [--purge [--delete-user]]
#
# 数据来源（默认 GitHub 官方仓库）：
#   https://github.com/<repo>/releases/download/v<version>/
#     TextCascade.Server-<version>-linux-<arch>.tar.gz
#     checksums-sha256.txt
#
# 只支持 systemd（>= 235）的 Linux；二进制为 CGO_ENABLED=0 静态构建，
# 目标机不需要任何运行时。

set -eu

APP=TextCascade.Server
SERVICE=textcascade-server
SERVICE_USER=textcascade
SERVICE_GROUP=textcascade
DEFAULT_REPO=long45343/TextCascade-Server
TAB=$(printf '\t')

BIN_DIR=/opt/textcascade-server
CONFIG_DIR=/etc/textcascade
DATA_DIR=/var/lib/textcascade
CONFIG_FILE=$CONFIG_DIR/textcascade.toml
ENV_FILE=$CONFIG_DIR/textcascade.env
USERS_FILE=$CONFIG_DIR/users.json
CERT_DIR=$CONFIG_DIR/certs
UNIT_FILE=/etc/systemd/system/$SERVICE.service

# 数据源
REPO=${TEXTCASCADE_REPO:-$DEFAULT_REPO}
MIRROR=${TEXTCASCADE_GITHUB_MIRROR:-}
BASE_URL=          # 可选：纯文件服务器布局（离线/自建镜像）
TOKEN=${TEXTCASCADE_GITHUB_TOKEN:-${GITHUB_TOKEN:-}}
INCLUDE_PRERELEASE=0

ACTION=install
VERSION=
TAG=
PORT=8443
BIND=0.0.0.0
CERT_SRC=
KEY_SRC=
SELF_SIGNED=0
CN=
USERNAME=
PASSWORD_STDIN=0
PASSWORD=
ASSUME_YES=0
NO_START=0
NO_VERIFY=0
FORCE=0
ROTATE_SECRET=0
FROM_FILE=
FROM_SHA=
PURGE=0
DELETE_USER=0
LIST_ONLY=0
COUNT=0
TAG=
WIZARD=1

TTY_IN=
if [ -r /dev/tty ]; then
    TTY_IN=/dev/tty
fi

WORK=
PAYLOAD=
BIN_SRC=
UNIT_SRC=
CERT_NAME=
ARCH_TAG=
HEALTH_URL=
RELEASES_TSV=

say() { printf '%s\n' "$*"; }
warn() { printf '警告：%s\n' "$*" >&2; }
die() { printf '错误：%s\n' "$*" >&2; exit 1; }
has() { command -v "$1" >/dev/null 2>&1; }
have_tty() { [ -n "$TTY_IN" ]; }
step() { printf '\n==> %s\n' "$*"; }

cleanup() {
    if [ -n "$WORK" ] && [ -d "$WORK" ]; then
        rm -rf "$WORK"
    fi
}
trap cleanup EXIT
trap 'cleanup; exit 130' INT TERM

usage() {
    cat <<'EOF'
用法：sh install.sh [动作] [选项]

直接运行 `sh install.sh` 会进入交互向导：逐步询问版本、端口、证书与首个用户，
最后汇总确认后才改动系统。已用选项指定的项目不会再问；加 -y 则全部采用默认值、
不进行任何提问（适合脚本与 CI）。

动作（默认 install）：
  install                              安装或修复现有安装（保留配置、密钥、用户）
  upgrade                              用新版二进制替换并重启（保留全部数据）
  status                               查看版本、服务状态、健康检查与用户数
  uninstall                            停止并移除服务与程序（默认保留配置与数据）
  list                                 列出仓库可用版本（同 --list）

版本与来源：
  --version X                          指定版本（如 1.0.0、1.0.0-Alpha）；
                                       交互终端下缺省弹出选择菜单，非交互取最新正式版
  --list                               列出仓库可用版本后退出
  --prerelease                         版本列表与"最新"都包含预发布版
  --repo OWNER/REPO                    从指定 GitHub 仓库取包（默认 long45343/TextCascade-Server）
  --mirror PREFIX                      GitHub 下载前缀（如 https://ghfast.top/），用于加速
  --base-url URL                       改用纯文件服务器布局，不使用 GitHub
  --from-file PATH                     离线安装：直接使用本地 tar.gz，不联网
  --sha256 HEX                         配合 --from-file 校验包完整性

证书（三选一；已有安装可省略以沿用现状）：
  --cert PATH                          证书文件（.pem/.crt/.pfx/.p12）
  --key PATH                           .pem/.crt 对应的私钥（缺省查找同目录同名 .key）
  --self-signed [CN]                   生成自签证书（测试用；CN 缺省为主机名）

其他：
  --port N                             监听端口（默认 8443）
  --bind ADDR                          监听地址（默认 0.0.0.0）
  --username NAME                      安装后创建首个用户
  --password-stdin                     从标准输入读取该用户的密码（自动 --yes）
  --rotate-secret                      重新生成 token secret（所有已登录设备失效）
  --force                              覆盖现有配置、环境文件与 systemd 单元
  --no-verify                          跳过 SHA-256 校验
  --no-start                           只落地文件，不启动服务
  --purge                              uninstall 时同时删除配置与数据
  --delete-user                        uninstall --purge 时同时删除系统用户
  -y, --yes                            不做任何提问，采用默认值（隐式 --no-wizard）
  --wizard                             强制进入交互向导
  --no-wizard                          跳过向导，仅用命令行参数与默认值
  -h, --help                           显示本帮助

环境变量：
  TEXTCASCADE_REPO                     同 --repo
  TEXTCASCADE_GITHUB_MIRROR            同 --mirror
  TEXTCASCADE_GITHUB_TOKEN             GitHub token（私有仓库或 API 限流时使用）
EOF
}

# ---------------------------------------------------------------- 参数解析

while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help) usage; exit 0 ;;
        install|upgrade|status|uninstall|list)
            ACTION=$1; shift ;;
        -y|--yes) ASSUME_YES=1; shift ;;
        --list) ACTION=list; shift ;;
        --wizard) WIZARD=1; shift ;;
        --no-wizard) WIZARD=0; shift ;;
        --no-start) NO_START=1; shift ;;
        --no-verify) NO_VERIFY=1; shift ;;
        --force) FORCE=1; shift ;;
        --rotate-secret) ROTATE_SECRET=1; shift ;;
        --purge) PURGE=1; shift ;;
        --delete-user) DELETE_USER=1; shift ;;
        --password-stdin) PASSWORD_STDIN=1; shift ;;
        --prerelease) INCLUDE_PRERELEASE=1; shift ;;
        --version) [ $# -ge 2 ] || die "--version 需要一个版本号。"; VERSION=$2; shift 2 ;;
        --version=*) VERSION=${1#*=}; shift ;;
        --repo) [ $# -ge 2 ] || die "--repo 需要 OWNER/REPO。"; REPO=$2; shift 2 ;;
        --repo=*) REPO=${1#*=}; shift ;;
        --mirror) [ $# -ge 2 ] || die "--mirror 需要一个前缀 URL。"; MIRROR=$2; shift 2 ;;
        --mirror=*) MIRROR=${1#*=}; shift ;;
        --base-url) [ $# -ge 2 ] || die "--base-url 需要一个 URL。"; BASE_URL=$2; shift 2 ;;
        --base-url=*) BASE_URL=${1#*=}; shift ;;
        --port) [ $# -ge 2 ] || die "--port 需要一个端口号。"; PORT=$2; shift 2 ;;
        --port=*) PORT=${1#*=}; shift ;;
        --bind) [ $# -ge 2 ] || die "--bind 需要一个地址。"; BIND=$2; shift 2 ;;
        --bind=*) BIND=${1#*=}; shift ;;
        --cert) [ $# -ge 2 ] || die "--cert 需要一个路径。"; CERT_SRC=$2; shift 2 ;;
        --cert=*) CERT_SRC=${1#*=}; shift ;;
        --key) [ $# -ge 2 ] || die "--key 需要一个路径。"; KEY_SRC=$2; shift 2 ;;
        --key=*) KEY_SRC=${1#*=}; shift ;;
        --self-signed)
            SELF_SIGNED=1; shift
            case "${1:-}" in
                ""|-*) ;;
                *) CN=$1; shift ;;
            esac
            ;;
        --username) [ $# -ge 2 ] || die "--username 需要一个用户名。"; USERNAME=$2; shift 2 ;;
        --username=*) USERNAME=${1#*=}; shift ;;
        --from-file) [ $# -ge 2 ] || die "--from-file 需要一个路径。"; FROM_FILE=$2; shift 2 ;;
        --from-file=*) FROM_FILE=${1#*=}; shift ;;
        --sha256) [ $# -ge 2 ] || die "--sha256 需要一个十六进制摘要。"; FROM_SHA=$2; shift 2 ;;
        --sha256=*) FROM_SHA=${1#*=}; shift ;;
        -*) die "未知选项：$1（见 --help）" ;;
        *) die "无法识别的参数：$1（动作仅支持 install / upgrade / status / list / uninstall，见 --help）" ;;
    esac
done

case "$BASE_URL" in
    */) BASE_URL=${BASE_URL%/} ;;
esac
case "$MIRROR" in
    */) MIRROR=${MIRROR%/} ;;
esac

# ---------------------------------------------------------------- 前置检查

require_root() {
    [ "$(id -u)" = 0 ] || die "需要 root 权限运行（sudo sh $0 ...）。"
}

require_systemd() {
    has systemctl || die "未找到 systemctl：本脚本只支持 systemd 系统。"
    [ -d /run/systemd/system ] || die "systemd 未作为 PID 1 运行（容器或非 systemd 环境请改用 Docker 部署）。"

    _ver=$(systemctl --version | sed -n '1s/^systemd \([0-9][0-9]*\).*/\1/p')
    [ -n "$_ver" ] || die "无法识别 systemd 版本。"
    if [ "$_ver" -lt 235 ]; then
        die "systemd $_ver 过旧：本单元使用 ConfigurationDirectory=（需 235+），请升级系统或改用 Docker 部署。"
    fi
}

detect_arch() {
    _m=$(uname -m 2>/dev/null || true)
    case "$_m" in
        x86_64|amd64) ARCH_TAG=x64 ;;
        aarch64|arm64) ARCH_TAG=arm64 ;;
        "") die "无法识别本机架构：uname -m 没有输出。" ;;
        *) die "不支持的架构：$_m（发行包仅提供 x64 与 arm64）。" ;;
    esac
}

fetch() {
    _url=$1
    _dst=$2
    if has curl; then
        if [ -n "$TOKEN" ]; then
            curl -fsSL --retry 2 --connect-timeout 15 \
                -H "Authorization: Bearer $TOKEN" -o "$_dst" "$_url"
        else
            curl -fsSL --retry 2 --connect-timeout 15 -o "$_dst" "$_url"
        fi
    elif has wget; then
        if [ -n "$TOKEN" ]; then
            wget -q -T 30 --header="Authorization: Bearer $TOKEN" -O "$_dst" "$_url"
        else
            wget -q -T 30 -O "$_dst" "$_url"
        fi
    else
        die "需要 curl 或 wget 来下载发行包。"
    fi
}

# GitHub API 主机；自建企业版可用 TEXTCASCADE_GITHUB_API 覆盖。
github_api_host() {
    printf '%s' "${TEXTCASCADE_GITHUB_API:-https://api.github.com}"
}

sha256_of() {
    if has sha256sum; then
        sha256sum "$1" | { read -r _h _; printf '%s\n' "$_h"; }
    elif has shasum; then
        shasum -a 256 "$1" | { read -r _h _; printf '%s\n' "$_h"; }
    elif has openssl; then
        openssl dgst -sha256 "$1" | sed 's/.*= *//'
    else
        die "找不到 sha256sum / shasum / openssl，无法校验完整性。"
    fi
}

need_openssl() {
    has openssl || die "生成自签证书需要 openssl。"
}

# 以服务用户身份执行（CLI 写出的 users.json 属主必须与 systemd 单元一致）
run_as_service() {
    if has runuser; then
        runuser -u "$SERVICE_USER" -- "$@"
    elif has setpriv; then
        setpriv --reuid="$SERVICE_USER" --regid="$SERVICE_GROUP" --init-groups "$@"
    else
        _cmd=
        for _a in "$@"; do
            _q=$(printf '%s' "$_a" | sed "s/'/'\\\\''/g")
            _cmd="$_cmd '$_q'"
        done
        su -s /bin/sh "$SERVICE_USER" -c "$_cmd"
    fi
}

confirm() {
    [ "$ASSUME_YES" = 1 ] && return 0
    have_tty || return 1
    printf '%s [y/N] ' "$1"
    read -r _ans < "$TTY_IN" || return 1
    case "$_ans" in
        y|Y|yes|YES|1) return 0 ;;
        *) return 1 ;;
    esac
}

# 读一行输入到 REPLY；无 TTY 或读失败时返回非零。
ask() {
    have_tty || return 1
    printf '%s' "$1"
    if ! read -r REPLY < "$TTY_IN"; then
        printf '\n'
        return 1
    fi
    REPLY=$(printf '%s' "$REPLY" | tr -d '\r')
    return 0
}

# ask 的带默认值版本：直接回车采用 $2。
ask_default() {
    _prompt=$1
    _def=$2
    if [ -n "$_def" ]; then
        ask "$_prompt [$_def]: "
    else
        ask "$_prompt: "
    fi || return 1
    if [ -z "$REPLY" ]; then
        REPLY=$_def
    fi
    return 0
}

# ask 的是/否版本；返回 0 表示是。
ask_yes() {
    _prompt=$1
    _def=${2:-n}
    if [ "$_def" = y ]; then
        ask "$_prompt [Y/n]: " || return 1
    else
        ask "$_prompt [y/N]: " || return 1
    fi
    case "$REPLY" in
        "") [ "$_def" = y ] ;;
        y|Y|yes|YES) return 0 ;;
        *) return 1 ;;
    esac
}

# ---------------------------------------------------------------- 版本与载荷

# 把 GitHub releases JSON 转成 TSV：tag<TAB>prerelease<TAB>published_at<TAB>assets
# 不依赖 jq/python，只按 GitHub 固定字段顺序逐块读取（assets 在 published_at 之后）。
json_releases_table() {
    tr -d '\r' < "$1" | awk '
        function field(line, key,    v) {
            v = line
            sub(".*\"" key "\"[[:space:]]*:[[:space:]]*", "", v)
            sub(/^"/, "", v)
            sub(/".*/, "", v)
            sub(/,.*/, "", v)
            return v
        }
        /"tag_name"[[:space:]]*:/ && tag == "" { tag = field($0, "tag_name") }
        /"prerelease"[[:space:]]*:/ && pre == "" {
            pre = $0; sub(/.*"prerelease"[[:space:]]*:[[:space:]]*/, "", pre)
            sub(/,.*/, "", pre); gsub(/[ \t]/, "", pre)
        }
        /"published_at"[[:space:]]*:/ && pub == "" { pub = field($0, "published_at") }
        /"assets"[[:space:]]*:[[:space:]]*\[/ { inassets = 1 }
        inassets && /"name"[[:space:]]*:/ {
            a = field($0, "name")
            if (a != "") assets = (assets == "" ? a : assets "," a)
        }
        inassets && /^[[:space:]]*\][[:space:]]*,?[[:space:]]*$/ {
            inassets = 0
            if (tag != "") { print tag "\t" pre "\t" pub "\t" assets; tag = ""; pre = ""; pub = ""; assets = "" }
        }
        END {
            if (tag != "") print tag "\t" pre "\t" pub "\t" assets
        }
    '
}

api_releases() {
    _dst=$1
    _query=$2
    fetch "$(github_api_host)/repos/$REPO/releases$_query" "$_dst"
}

# 拉取发布列表到 $WORK/releases.tsv（已按 GitHub 返回顺序，新的在前）。
load_releases() {
    [ -n "$RELEASES_TSV" ] && [ -s "$RELEASES_TSV" ] && return 0

    RELEASES_TSV=$WORK/releases.tsv
    _json=$WORK/releases.json
    api_releases "$_json" "?per_page=30" \
        || die "无法访问 GitHub API：$(github_api_host)/repos/$REPO/releases"

    json_releases_table "$_json" > "$RELEASES_TSV"
    [ -s "$RELEASES_TSV" ] || die "仓库 $REPO 没有可用的 Release。"
}

# 0.x 系列是 C#/.NET 实现，1.0.0 起才是 Go 静态二进制；0.5.5 曾是误发的倒退版本，已撤回。
# 版本号已在 _check_version_shape 里限制字符集，这里只取主版本号。
is_installable_tag() {
    _v=${1#v}
    _major=${_v%%.*}
    case "$_major" in
        ""|*[!0-9]*) return 1 ;;
    esac
    [ "$_major" -ge 1 ]
}

# 输出 "tag<TAB>prerelease<TAB>note" 供菜单使用；预发布、缺资产与 .NET 版本分别标注。
release_entries() {
    while IFS="$TAB" read -r _tag _pre _date _assets; do
        [ -n "$_tag" ] || continue
        if [ "$_pre" = "true" ] && [ "$INCLUDE_PRERELEASE" = 0 ]; then
            continue
        fi

        _note=
        case "$_date" in
            ""|null) _note="无发布日期" ;;
            *) _note=${_date%%T*} ;;
        esac
        if [ "$_pre" = "true" ]; then
            _note="$_note · 预发布"
        fi
        if is_installable_tag "$_tag"; then
            case "$_assets" in
                *"-linux-$ARCH_TAG.tar.gz"*) ;;
                *) _note="$_note · 无 linux-$ARCH_TAG 资产" ;;
            esac
        else
            _note="$_note · .NET 版本，本脚本不可安装"
        fi
        printf '%s\t%s\t%s\n' "$_tag" "$_pre" "$_note"
    done < "$RELEASES_TSV"
}

# 最新的可安装版本（跳过 .NET 包）。只在 RELEASES_TSV 上判断，不受预发布过滤影响。
newest_installable_tag() {
    while IFS="$TAB" read -r _tag _pre _date _assets; do
        [ -n "$_tag" ] || continue
        is_installable_tag "$_tag" || continue
        case "$_assets" in
            *"-linux-$ARCH_TAG.tar.gz"*) ;;
            *) continue ;;
        esac
        printf '%s\n' "$_tag"
        return 0
    done < "$RELEASES_TSV"
    return 1
}

have_installable_stable() {
    while IFS="$TAB" read -r _tag _pre _date _assets; do
        [ -n "$_tag" ] || continue
        [ "$_pre" = "true" ] && continue
        is_installable_tag "$_tag" || continue
        case "$_assets" in
            *"-linux-$ARCH_TAG.tar.gz"*) return 0 ;;
        esac
    done < "$RELEASES_TSV"
    return 1
}

print_release_list() {
    load_releases
    say "仓库 $REPO 的可用版本（新 → 旧）："
    say ""
    _n=0
    while IFS="$TAB" read -r _tag _pre _note; do
        _n=$((_n + 1))
        printf '  %2d) %-24s %s\n' "$_n" "$_tag" "$_note"
    done <<EOF
$(release_entries)
EOF
    [ "$_n" -gt 0 ] || die "没有可显示的版本（试加 --prerelease）。"
    say ""
    say "0.x 版本为 .NET 程序，需要 .NET 运行时，本脚本不可安装；请选 1.0.0 起的 Go 版本。"
}

# 交互式选择；无 TTY 或 --yes 时退回自动选最新可安装的正式版。
choose_version() {
    load_releases

    _default=
    while IFS="$TAB" read -r _tag _pre _note; do
        case "$_note" in *".NET 版本"*) continue ;; esac
        if [ "$_pre" != "true" ]; then
            _default=$_tag
            break
        fi
    done <<EOF
$(release_entries)
EOF
    if [ -z "$_default" ]; then
        _default=$(release_entries | awk -F'\t' '$3 !~ /\.NET 版本/ {print $1; exit}')
    fi
    [ -n "$_default" ] || die "仓库 $REPO 没有可安装的版本（需要 1.0.0 起的 Go 静态包）。"

    if [ "$ASSUME_YES" = 1 ] || ! have_tty; then
        TAG=$_default
        say "选用最新可安装版本：${TAG#v}"
        return 0
    fi

    say ""
    say "仓库 $REPO 的可用版本（新 → 旧）："
    say ""
    _n=0
    _tags=
    while IFS="$TAB" read -r _tag _pre _note; do
        _n=$((_n + 1))
        _tags="$_tags $_tag"
        _mark=
        [ "$_tag" = "$_default" ] && _mark=" ← 默认"
        printf '  %2d) %-24s %s%s\n' "$_n" "$_tag" "$_note" "$_mark"
    done <<EOF
$(release_entries)
EOF
    say ""
    say "  0) 取消"
    say ""
    say "0.x 版本为 .NET 程序，需要 .NET 运行时，本脚本不可安装；请选 1.0.0 起的 Go 版本。"

    while :; do
        printf '\n选择版本 [回车=%s]: ' "${_default#v}"
        if ! read -r _ans < "$TTY_IN"; then
            TAG=$_default
            say ""
            say "未读到输入，使用默认：${TAG#v}"
            return 0
        fi
        _ans=$(printf '%s' "$_ans" | tr -d ' \r')
        case "$_ans" in
            "") TAG=$_default; return 0 ;;
            0) die "已取消。" ;;
            *[!0-9]*) say "请输入序号。" ; continue ;;
        esac
        if [ "$_ans" -ge 1 ] && [ "$_ans" -le "$_n" ]; then
            TAG=$(release_entries | sed -n "${_ans}p" | cut -f1)
            return 0
        fi
        say "序号超出范围（1-$_n）。"
    done
}

resolve_latest_tag() {
    _api=$WORK/latest.json
    _tag=

    # /releases/latest 由 GitHub 排除 draft 与 prerelease，但它可能指向 .NET 时代的
    # 0.x 版本（例如 0.5.5 被撤下后会回退到 0.5.0），因此仍需校验可安装性。
    if [ "$INCLUDE_PRERELEASE" = 0 ] && api_releases "$_api" "/latest" 2>/dev/null; then
        _tag=$(json_releases_table "$_api" | head -n 1 | cut -f1)
        if [ -n "$_tag" ] && ! is_installable_tag "$_tag"; then
            warn "$_tag 是 .NET 版本，不能直接安装；改取最新的 Go 版本。"
            _tag=
        fi
    fi

    if [ -z "$_tag" ]; then
        load_releases
        if [ "$INCLUDE_PRERELEASE" = 1 ]; then
            # 含预发布：取最新的可安装版本（newest_installable_tag 已跳过 .NET）
            _tag=$(newest_installable_tag || true)
        else
            while IFS="$TAB" read -r _tag _pre _note; do
                [ -n "$_tag" ] || continue
                case "$_note" in *".NET 版本"*) _tag=; continue ;; esac
                if [ "$_pre" != "true" ]; then
                    break
                fi
                _tag=
            done <<EOF
$(release_entries)
EOF
            [ -n "$_tag" ] || _tag=$(newest_installable_tag || true)
        fi
    fi

    [ -n "$_tag" ] || die "仓库 $REPO 没有可安装的 Release（需要 1.0.0 起的 Go 静态包）。"
    TAG=$_tag
    VERSION=${TAG#v}
}

resolve_version() {
    if [ -n "$FROM_FILE" ]; then
        # 离线包：从文件名 TextCascade.Server-<version>-linux-<arch>.tar.gz 推断
        if [ -z "$VERSION" ]; then
            VERSION=$(basename "$FROM_FILE" \
                | sed -n 's/^TextCascade\.Server-\(.*\)-linux-[^-]*\.tar\.gz$/\1/p')
            [ -n "$VERSION" ] || die "无法从文件名识别版本，请用 --version 指定。"
            say "从文件名推断版本：$VERSION"
        fi
        case "$VERSION" in
            v[0-9]*) VERSION=${VERSION#v} ;;
        esac
        _check_version_shape
        return 0
    fi

    if [ -z "$VERSION" ]; then
        if [ -n "$BASE_URL" ]; then
            _latest=$WORK/latest.txt
            fetch "$BASE_URL/latest.txt" "$_latest" 2>/dev/null \
                && VERSION=$(tr -d ' \t\r\n' < "$_latest")
            [ -n "$VERSION" ] || die "无法确定版本：请加 --version，或在 $BASE_URL/latest.txt 写入版本号。"
        else
            # 交互终端让用户挑版本；非交互（-y / 无 TTY）取最新正式版。
            if [ "$ASSUME_YES" = 0 ] && have_tty; then
                choose_version
                VERSION=${TAG#v}
            else
                resolve_latest_tag
                say "解析到最新版本：$VERSION"
            fi
        fi
    fi

    case "$VERSION" in
        v[0-9]*) VERSION=${VERSION#v} ;;
    esac
    _check_version_shape
}

# 版本号会拼进 URL 与文件名，严格限制字符集，避免路径注入。
_check_version_shape() {
    case "$VERSION" in
        "") die "版本号为空。" ;;
        *[!0-9A-Za-z._-]*) die "版本号含非法字符：$VERSION（只允许字母、数字、点、下划线、连字符）。" ;;
    esac
}

payload_name() {
    printf 'TextCascade.Server-%s-linux-%s.tar.gz' "$VERSION" "$ARCH_TAG"
}

release_download_prefix() {
    if [ -n "$MIRROR" ]; then
        printf '%s/github.com/%s/releases/download/v%s' "$MIRROR" "$REPO" "$VERSION"
    else
        printf 'https://github.com/%s/releases/download/v%s' "$REPO" "$VERSION"
    fi
}

download_payload() {
    _name=$(payload_name)
    PAYLOAD=$WORK/$_name
    _checks=$WORK/checksums-sha256.txt
    _got_checks=0

    if [ -n "$BASE_URL" ]; then
        # 纯文件服务器布局：优先 <base>/<version>/，回退到平铺。
        if fetch "$BASE_URL/$VERSION/$_name" "$PAYLOAD" 2>/dev/null; then
            fetch "$BASE_URL/$VERSION/checksums-sha256.txt" "$_checks" 2>/dev/null && _got_checks=1
        elif fetch "$BASE_URL/$_name" "$PAYLOAD" 2>/dev/null; then
            fetch "$BASE_URL/checksums-sha256.txt" "$_checks" 2>/dev/null && _got_checks=1
        else
            die "下载失败：$BASE_URL/$VERSION/$_name（也未在根目录找到）。"
        fi

        case "$BASE_URL" in
            http://*) warn "base-url 为明文 HTTP：SHA-256 校验只能防传输损坏，不能防中间人篡改。" ;;
        esac
    else
        _prefix=$(release_download_prefix)
        say "下载：$_prefix/$_name"
        fetch "$_prefix/$_name" "$PAYLOAD" \
            || die "下载失败：$_prefix/$_name（检查版本 ${VERSION} 是否存在该架构的资产）。"
        fetch "$_prefix/checksums-sha256.txt" "$_checks" 2>/dev/null && _got_checks=1
    fi

    if [ "$NO_VERIFY" = 0 ]; then
        if [ "$_got_checks" = 1 ]; then
            _want=
            while read -r _h _f; do
                _f=${_f#\*}
                case "$_f" in
                    "$_name"|*"/$_name") _want=$_h; break ;;
                esac
            done < "$_checks"

            if [ -n "$_want" ]; then
                _actual=$(sha256_of "$PAYLOAD")
                [ "$_want" = "$_actual" ] || die "SHA-256 校验失败：期望 $_want，实际 $_actual。"
                say "SHA-256 校验通过（$_want）。"
            else
                warn "校验文件中没有 $_name 的记录，跳过校验。"
            fi
        else
            warn "该 Release 未提供 checksums-sha256.txt，跳过校验。"
        fi
    fi
}

acquire_payload() {
    if [ -n "$FROM_FILE" ]; then
        [ -f "$FROM_FILE" ] || die "找不到文件：$FROM_FILE"
        PAYLOAD=$FROM_FILE
        if [ -n "$FROM_SHA" ]; then
            _actual=$(sha256_of "$PAYLOAD")
            case "$_actual" in
                "$FROM_SHA") say "SHA-256 校验通过（$_actual）。" ;;
                *) die "SHA-256 校验失败：期望 $FROM_SHA，实际 $_actual。" ;;
            esac
        elif [ "$NO_VERIFY" = 0 ]; then
            warn "离线安装未提供 --sha256，跳过完整性校验。"
        fi
    else
        download_payload
    fi

    PAYLOAD_DIR=$WORK/payload
    mkdir -p "$PAYLOAD_DIR"
    case "$PAYLOAD" in
        *.tar.gz|*.tgz) tar -xzf "$PAYLOAD" -C "$PAYLOAD_DIR" ;;
        *.tar) tar -xf "$PAYLOAD" -C "$PAYLOAD_DIR" ;;
        *) die "不支持的包格式：$PAYLOAD（需要 .tar.gz）。" ;;
    esac

    BIN_SRC=$(find "$PAYLOAD_DIR" -type f -name "$APP" | head -n 1)
    [ -n "$BIN_SRC" ] || die "发行包内未找到 $APP 可执行文件。"
    UNIT_SRC=$(find "$PAYLOAD_DIR" -type f -name "$SERVICE.service" | head -n 1)
}

# ---------------------------------------------------------------- 证书

prepare_certificate() {
    _existing=
    if [ -f "$CONFIG_FILE" ]; then
        _existing=$(sed -n 's/^[[:space:]]*certificate_path[[:space:]]*=[[:space:]]*"\(.*\)".*/\1/p' "$CONFIG_FILE" | head -n 1)
    fi

    if [ "$SELF_SIGNED" = 1 ]; then
        need_openssl
        [ -n "$CN" ] || CN=$(hostname -f 2>/dev/null || hostname 2>/dev/null || echo localhost)
        CERT_NAME=server.pem
        warn "将生成自签证书（CN=$CN）。生产环境请改用可信证书，并把文件放到 $CERT_DIR。"
        _cert=$CERT_DIR/$CERT_NAME
        _key=$CERT_DIR/server.key
        if ! openssl req -x509 -newkey rsa:2048 -days 365 -nodes \
                -keyout "$_key" -out "$_cert" \
                -subj "/CN=$CN" \
                -addext "subjectAltName=DNS:$CN" >/dev/null 2>&1; then
            # OpenSSL < 1.1.1 没有 -addext
            openssl req -x509 -newkey rsa:2048 -days 365 -nodes \
                -keyout "$_key" -out "$_cert" -subj "/CN=$CN" >/dev/null 2>&1 \
                || die "生成自签证书失败。"
        fi
        chmod 0644 "$_cert"
        chmod 0600 "$_key"
        chown "$SERVICE_USER:$SERVICE_GROUP" "$_cert" "$_key"
        CERT_READY=1
        return 0
    fi

    if [ -n "$CERT_SRC" ]; then
        [ -f "$CERT_SRC" ] || die "找不到证书文件：$CERT_SRC"

        _base=$(basename "$CERT_SRC")
        _stem=${_base%.*}
        case "$_base" in
            *.pfx|*.p12)
                [ -z "$KEY_SRC" ] || die "PFX/P12 是单文件格式，不能配合 --key 使用。"
                CERT_NAME=$_base
                cp -f "$CERT_SRC" "$CERT_DIR/$CERT_NAME"
                ;;
            *)
                CERT_NAME=$_base
                cp -f "$CERT_SRC" "$CERT_DIR/$CERT_NAME"
                _key_dst=$CERT_DIR/$_stem.key
                if [ -n "$KEY_SRC" ]; then
                    [ -f "$KEY_SRC" ] || die "找不到私钥文件：$KEY_SRC"
                    cp -f "$KEY_SRC" "$_key_dst"
                elif [ -f "${CERT_SRC%.*}.key" ]; then
                    cp -f "${CERT_SRC%.*}.key" "$_key_dst"
                elif [ -f "$CERT_SRC" ] && grep -q 'BEGIN .*PRIVATE KEY' "$CERT_SRC" 2>/dev/null; then
                    : # 证书与私钥合包，无需边车
                else
                    die "缺少私钥：请用 --key 指定，或把私钥放在 ${CERT_SRC%.*}.key。"
                fi
                ;;
        esac

        chmod 0644 "$CERT_DIR/$CERT_NAME"
        if [ -f "$CERT_DIR/$_stem.key" ]; then
            chmod 0600 "$CERT_DIR/$_stem.key"
        fi
        chown -R "$SERVICE_USER:$SERVICE_GROUP" "$CERT_DIR"
        CERT_READY=1
        return 0
    fi

    if [ -n "$_existing" ] && [ -f "$_existing" ]; then
        CERT_NAME=$(basename "$_existing")
        say "沿用现有证书：$_existing"
        CERT_READY=1
        return 0
    fi

    die "未提供 TLS 证书：请用 --cert/--key 指定，或加 --self-signed 生成测试证书。"
}

# 在动任何系统状态之前先校验证书参数，避免失败时留下半成品目录。
check_cert_args() {
    if [ "$SELF_SIGNED" = 1 ]; then
        need_openssl
        [ -z "$CERT_SRC" ] || die "--self-signed 与 --cert 不能同时使用。"
        return 0
    fi

    if [ -n "$CERT_SRC" ]; then
        [ -f "$CERT_SRC" ] || die "找不到证书文件：$CERT_SRC"
        case "$(printf '%s' "$CERT_SRC" | tr 'A-Z' 'a-z')" in
            *.pem|*.crt|*.pfx|*.p12) ;;
            *) die "证书扩展名不受支持（仅 .pem/.crt/.pfx/.p12）：$CERT_SRC" ;;
        esac
        case "$CERT_SRC" in
            *.pfx|*.p12|*.PFX|*.P12)
                [ -z "$KEY_SRC" ] || die "PFX/P12 是单文件格式，不能配合 --key 使用。"
                ;;
            *)
                if [ -n "$KEY_SRC" ]; then
                    [ -f "$KEY_SRC" ] || die "找不到私钥文件：$KEY_SRC"
                elif [ -f "${CERT_SRC%.*}.key" ]; then
                    :
                elif grep -q 'BEGIN .*PRIVATE KEY' "$CERT_SRC" 2>/dev/null; then
                    :
                else
                    die "缺少私钥：请用 --key 指定，或把私钥放在 ${CERT_SRC%.*}.key。"
                fi
                ;;
        esac
        return 0
    fi

    # 未显式指定：已有安装沿用其配置里的证书
    if [ -f "$CONFIG_FILE" ]; then
        _existing=$(sed -n 's/^[[:space:]]*certificate_path[[:space:]]*=[[:space:]]*"\(.*\)".*/\1/p' "$CONFIG_FILE" | head -n 1)
        if [ -n "$_existing" ] && [ -f "$_existing" ]; then
            return 0
        fi
    fi

    die "未提供 TLS 证书：请用 --cert/--key 指定，或加 --self-signed 生成测试证书。"
}

# ---------------------------------------------------------------- 系统布局

ensure_user_and_dirs() {
    if ! id "$SERVICE_USER" >/dev/null 2>&1; then
        useradd --system --home-dir "$BIN_DIR" --shell /usr/sbin/nologin "$SERVICE_USER" \
            || die "创建系统用户 $SERVICE_USER 失败。"
        say "已创建系统用户 $SERVICE_USER。"
    fi

    mkdir -p "$BIN_DIR" "$CONFIG_DIR" "$CERT_DIR" "$DATA_DIR"
    chown root:root "$BIN_DIR"
    chmod 0755 "$BIN_DIR"
    chown "$SERVICE_USER:$SERVICE_GROUP" "$CONFIG_DIR" "$CERT_DIR" "$DATA_DIR"
    chmod 0755 "$CONFIG_DIR"
    chmod 0750 "$CERT_DIR"
    chmod 0750 "$DATA_DIR"
}

generate_secret() {
    if has openssl; then
        openssl rand -base64 48 | tr -d '\r\n'
    elif [ -r /dev/urandom ] && has base64; then
        head -c 48 /dev/urandom | base64 | tr -d '\r\n'
    else
        die "无法生成 token secret：需要 openssl 或 base64。"
    fi
}

write_env() {
    if [ -f "$ENV_FILE" ] && [ "$ROTATE_SECRET" = 0 ]; then
        say "保留现有 token secret：$ENV_FILE"
        return 0
    fi

    if [ -f "$ENV_FILE" ] && [ "$ROTATE_SECRET" = 1 ]; then
        warn "重新生成 token secret：所有已签发的登录令牌立即失效，全部设备需要重新登录。"
    fi

    _secret=$(generate_secret)
    _tmp=$CONFIG_DIR/.textcascade.env.new
    ( umask 077; printf 'TEXTCASCADE_TOKEN_SECRET=%s\n' "$_secret" > "$_tmp" )
    chown "$SERVICE_USER:$SERVICE_GROUP" "$_tmp"
    chmod 0600 "$_tmp"
    mv -f "$_tmp" "$ENV_FILE"
    say "已写入 token secret：$ENV_FILE"
}

write_config() {
    CONFIG_CHANGED=0
    _existing_port=
    if [ -f "$CONFIG_FILE" ]; then
        _existing_port=$(sed -n 's/^[[:space:]]*port[[:space:]]*=[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$CONFIG_FILE" | head -n 1)
    fi

    if [ -f "$CONFIG_FILE" ] && [ "$FORCE" = 0 ]; then
        say "保留现有配置：$CONFIG_FILE"
        if [ -n "$_existing_port" ]; then
            if [ "$PORT" != "$_existing_port" ] && [ "$PORT" != 8443 ]; then
                warn "--port 对已有安装不生效（需 --force 重写配置）。"
            fi
            PORT=$_existing_port
        fi
        return 0
    fi

    cat > "$CONFIG_FILE" <<EOF
# 由 install.sh 生成。其余键（auth / limits / rate_limit）省略即取内置安全默认值。
[server]
bind = "$BIND"
port = $PORT
certificate_path = "$CERT_DIR/$CERT_NAME"

[files]
users_file = "$USERS_FILE"
state_file = "$DATA_DIR/textcascade.state.json"
EOF

    chown "$SERVICE_USER:$SERVICE_GROUP" "$CONFIG_FILE"
    chmod 0640 "$CONFIG_FILE"
    CONFIG_CHANGED=1
    say "已写入配置：$CONFIG_FILE（端口 $PORT）"
}

write_users_if_missing() {
    [ -f "$USERS_FILE" ] && return 0
    ( umask 077; printf '{\n  "nextTokenVersion": 1,\n  "users": []\n}\n' > "$USERS_FILE" )
    chown "$SERVICE_USER:$SERVICE_GROUP" "$USERS_FILE"
    chmod 0600 "$USERS_FILE"
}

# 可执行性探测：不给参数运行。能执行时它会因缺少 token secret 而在配置校验处
# 以非 126/127 退出；架构不符或文件损坏则是 126（EACCES/ENOEXEC）或
# 127（ELIBBAD 等）。不能用 --version 探测：1.0.0-Alpha 及更早版本没有该参数。
binary_is_runnable() {
    _code=0
    "$1" >/dev/null 2>&1 || _code=$?
    case "$_code" in
        126|127) return 1 ;;
        *) return 0 ;;
    esac
}

# 0.x 的 Release 是 .NET 单文件程序（需 .NET 运行时），1.0.0 起才是 Go 静态二进制。
# 用 ELF PT_INTERP/依赖库区分，缺 file 命令时跳过检查。
binary_is_self_contained() {
    if has file; then
        _desc=$(file -b "$1" 2>/dev/null || true)
        case "$_desc" in
            *"statically linked"*) return 0 ;;
            *"dynamically linked"*)
                case "$_desc" in
                    *"Go BuildID"*) return 0 ;;   # Go 动态构建（异常但可运行）
                esac
                return 1
                ;;
        esac
        return 0
    fi
    return 0
}

# 读取二进制自报版本。老版本不支持 --version 时输出为空，返回非零。
binary_reports_version() {
    _out=$("$1" --version 2>/dev/null || true)
    case "$_out" in
        "$APP "[0-9]*) printf '%s\n' "${_out#"$APP "}" ; return 0 ;;
        *) return 1 ;;
    esac
}

install_binary() {
    BINARY_CHANGED=0
    _tmp=$BIN_DIR/.$APP.new.$$
    cp -f "$BIN_SRC" "$_tmp"
    chown root:root "$_tmp"
    chmod 0755 "$_tmp"

    # 0.x 是 .NET 单文件程序，装上会因缺 .NET 运行时起不来。
    if ! binary_is_self_contained "$_tmp"; then
        rm -f "$_tmp"
        die "该版本（$VERSION）是 .NET 程序，需要 .NET 运行时；本脚本只支持 1.0.0 起的 Go 静态版本（含 1.0.0-Alpha）。"
    fi

    if ! binary_is_runnable "$_tmp"; then
        rm -f "$_tmp"
        die "下载的二进制无法在本机执行（架构不匹配或文件损坏）。"
    fi

    _reported=$(binary_reports_version "$_tmp" || true)
    if [ -n "$_reported" ]; then
        case "$_reported" in
            "$VERSION") ;;
            *) warn "二进制自报版本为「$_reported」，与请求的 $VERSION 不一致。" ;;
        esac
    fi

    if [ -f "$BIN_DIR/$APP" ] && cmp -s "$_tmp" "$BIN_DIR/$APP"; then
        rm -f "$_tmp"
        _record_version
        say "二进制已是最新内容，无需替换：$BIN_DIR/$APP"
        return 0
    fi

    if [ -f "$BIN_DIR/$APP" ]; then
        cp -p "$BIN_DIR/$APP" "$BIN_DIR/$APP.previous" 2>/dev/null || true
    fi
    mv -f "$_tmp" "$BIN_DIR/$APP"
    BINARY_CHANGED=1
    _record_version
    say "已安装二进制：$BIN_DIR/$APP（$VERSION）"
}

# 1.0.0-Alpha 及更早版本没有 --version，靠这个文件记录已安装版本。
_record_version() {
    printf '%s\n' "$VERSION" > "$BIN_DIR/.installed-version"
    chmod 0644 "$BIN_DIR/.installed-version"
}

# 已安装版本：优先二进制自报，其次安装时写入的记录文件。
installed_version() {
    _v=$(binary_reports_version "$BIN_DIR/$APP" 2>/dev/null || true)
    if [ -n "$_v" ]; then
        printf '%s\n' "$_v"
        return 0
    fi
    if [ -r "$BIN_DIR/.installed-version" ]; then
        _v=$(tr -d ' \r\n' < "$BIN_DIR/.installed-version")
        if [ -n "$_v" ]; then
            printf '%s（记录值，该二进制不支持 --version）\n' "$_v"
            return 0
        fi
    fi
    return 1
}

install_unit() {
    UNIT_CHANGED=0
    if [ -f "$UNIT_FILE" ]; then
        if [ -n "$UNIT_SRC" ] && cmp -s "$UNIT_SRC" "$UNIT_FILE"; then
            say "systemd 单元内容一致，无需更新：$UNIT_FILE"
            return 0
        fi
        if [ "$FORCE" = 0 ]; then
            say "保留现有 systemd 单元：$UNIT_FILE"
            return 0
        fi
    fi

    if [ -n "$UNIT_SRC" ]; then
        install -m 0644 -o root -g root "$UNIT_SRC" "$UNIT_FILE"
    else
        cat > "$UNIT_FILE" <<'EOF'
[Unit]
Description=TextCascade lightweight text synchronization server
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=textcascade
Group=textcascade
WorkingDirectory=/opt/textcascade-server
EnvironmentFile=/etc/textcascade/textcascade.env
ExecStart=/opt/textcascade-server/TextCascade.Server serve --config /etc/textcascade/textcascade.toml
StateDirectory=textcascade
ConfigurationDirectory=textcascade
Restart=on-failure
RestartSec=5s
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
LimitNOFILE=65536
StandardOutput=journal
StandardError=journal
SyslogIdentifier=textcascade-server

[Install]
WantedBy=multi-user.target
EOF
    fi
    UNIT_CHANGED=1
    say "已安装 systemd 单元：$UNIT_FILE"
}

# ---------------------------------------------------------------- 运行与验收

# 健康检查候选地址：通配绑定时依次尝试 127.0.0.1、::1（双栈监听的系统上
# IPv4 回环可能不可达），再回退到显式 bind 地址。
health_candidates() {
    case "$BIND" in
        0.0.0.0|::|"[::]")
            printf '%s\n' "127.0.0.1" "::1"
            ;;
        127.0.0.1|localhost)
            printf '%s\n' "127.0.0.1" "::1"
            ;;
        *)
            printf '%s\n' "$BIND"
            ;;
    esac
}

format_host() {
    case "$1" in
        *:*) printf '[%s]' "$1" ;;
        *) printf '%s' "$1" ;;
    esac
}

health_url() {
    # 供展示用：返回第一个候选地址的 URL。
    _first=$(health_candidates | head -n 1)
    printf 'https://%s:%s/health' "$(format_host "$_first")" "$PORT"
}

health_ok() {
    for _h in $(health_candidates); do
        _url="https://$(format_host "$_h"):$PORT/health"
        if has curl; then
            if curl -fsSk --max-time 3 "$_url" >/dev/null 2>&1; then
                HEALTH_URL=$_url
                return 0
            fi
        elif has wget; then
            if wget -q --no-check-certificate -T 3 -O /dev/null "$_url" >/dev/null 2>&1; then
                HEALTH_URL=$_url
                return 0
            fi
        else
            return 1
        fi
    done
    return 1
}

start_service() {
    systemctl daemon-reload
    systemctl enable "$SERVICE" >/dev/null 2>&1 || true
    systemctl restart "$SERVICE"

    _i=0
    while [ "$_i" -lt 20 ]; do
        if systemctl is-active --quiet "$SERVICE" && health_ok; then
            say "服务已启动，健康检查通过（$HEALTH_URL）。"
            return 0
        fi
        _i=$((_i + 1))
        sleep 1
    done

    printf '\n服务未能在 20 秒内通过健康检查，最近日志：\n' >&2
    journalctl -u "$SERVICE" -n 40 --no-pager >&2 2>/dev/null || true
    die "启动失败（配置：$CONFIG_FILE）。"
}

# 仅确保服务在运行；已健康则不动它（供幂等重装使用）。
ensure_service_running() {
    systemctl daemon-reload
    systemctl enable "$SERVICE" >/dev/null 2>&1 || true

    if systemctl is-active --quiet "$SERVICE" && health_ok; then
        say "服务已在运行，无需重启（$HEALTH_URL）。"
        return 0
    fi

    systemctl restart "$SERVICE"
    _i=0
    while [ "$_i" -lt 20 ]; do
        if systemctl is-active --quiet "$SERVICE" && health_ok; then
            say "服务已启动，健康检查通过（$HEALTH_URL）。"
            return 0
        fi
        _i=$((_i + 1))
        sleep 1
    done

    printf '\n服务未能在 20 秒内通过健康检查，最近日志：\n' >&2
    journalctl -u "$SERVICE" -n 40 --no-pager >&2 2>/dev/null || true
    die "启动失败（配置：$CONFIG_FILE）。"
}

add_first_user() {
    [ -n "$USERNAME" ] || return 0

    # 幂等：用户已存在时不算失败（重装/修复场景）。
    if run_as_service "$BIN_DIR/$APP" user list --config "$CONFIG_FILE" 2>/dev/null \
            | awk -F, 'NR>1 {print $1}' | grep -Fxq "$USERNAME"; then
        say "用户 $USERNAME 已存在，跳过创建。"
        return 0
    fi

    if [ "$PASSWORD_STDIN" = 1 ]; then
        if printf '%s\n' "$PASSWORD" | run_as_service "$BIN_DIR/$APP" user add \
                --config "$CONFIG_FILE" --username "$USERNAME" --password-stdin; then
            say "已创建用户 $USERNAME。"
        else
            die "创建用户 $USERNAME 失败。"
        fi
    elif have_tty; then
        say "为 $USERNAME 设置密码："
        if run_as_service "$BIN_DIR/$APP" user add --config "$CONFIG_FILE" --username "$USERNAME"; then
            say "已创建用户 $USERNAME。"
        else
            die "创建用户 $USERNAME 失败。"
        fi
    else
        die "创建用户需要交互终端；无终端时请加 --password-stdin 从标准输入提供密码。"
    fi
}

count_users() {
    [ -f "$USERS_FILE" ] || { echo 0; return 0; }
    grep -c '"username"' "$USERS_FILE" 2>/dev/null || echo 0
}

json_escape() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

# 服务通过 fsnotify + 250ms 防抖监听 users.json；CLI 写完到服务可见之间
# 有一个短暂窗口，期间登录会返回 401。用一次真实登录做端到端验收，
# 既覆盖该窗口，也验证 TLS、用户与密码确实可用。
verify_login() {
    [ -n "$USERNAME" ] && [ "$PASSWORD_STDIN" = 1 ] || return 0

    _body=$(printf '{"username":"%s","password":"%s"}' \
        "$(json_escape "$USERNAME")" "$(json_escape "$PASSWORD")")

    for _h in $(health_candidates); do
        _url="https://$(format_host "$_h"):$PORT/api/v1/login"
        _i=0
        while [ "$_i" -lt 20 ]; do
            _code=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 3 \
                -X POST "$_url" \
                -H 'Content-Type: application/json' \
                -d "$_body" 2>/dev/null || echo 000)
            case "$_code" in
                200)
                    say "登录验收通过（HTTP 200，$USERNAME）。"
                    return 0
                    ;;
                429)
                    warn "登录验收遇到限流（429），跳过。"
                    return 0
                    ;;
            esac
            _i=$((_i + 1))
            sleep 0.5
        done
    done

    warn "登录验收未在 10 秒内返回 200（最后状态 $_code）；请检查用户与密码，或稍后重试。"
    return 0
}

print_summary() {
    _shown=${HEALTH_URL:-$(health_url)}
    cat <<EOF

--------------------------------------------------------------------
安装完成
  版本      : $VERSION
  程序      : $BIN_DIR/$APP
  配置      : $CONFIG_FILE
  secret    : $ENV_FILE
  用户表    : $USERS_FILE
  状态文件  : $DATA_DIR/textcascade.state.json
  证书      : $CERT_DIR/$CERT_NAME
  监听      : $BIND:$PORT（仅 HTTPS）

  健康检查  : curl -sk $_shown
  查看日志  : journalctl -u $SERVICE -f
  用户管理  : runuser -u $SERVICE_USER -- $BIN_DIR/$APP user add \\
                --config $CONFIG_FILE --username <名字>
--------------------------------------------------------------------
EOF
}

# ---------------------------------------------------------------- 交互向导

# 记住哪些参数是用户显式给出的。向导只询问未指定的项，
# 这样 "sh install.sh" 是全程问答，而 "sh install.sh --port 9443" 只问其余。
M_VERSION=0; M_PORT=0; M_CERT=0; M_USER=0

mark_explicit() {
    if [ -n "$VERSION" ] || [ -n "$FROM_FILE" ] || [ "$INCLUDE_PRERELEASE" = 1 ]; then
        M_VERSION=1
    fi
    if [ "$PORT" != 8443 ]; then
        M_PORT=1
    fi
    if [ -n "$CERT_SRC" ] || [ "$SELF_SIGNED" = 1 ]; then
        M_CERT=1
    fi
    if [ -n "$USERNAME" ]; then
        M_USER=1
    fi
    # 显式给了证书参数就说明想覆盖证书；密码/用户已定时不再追问首个用户
    if [ "$PASSWORD_STDIN" = 1 ]; then
        M_USER=1
    fi
}

wizard_enabled() {
    [ "$WIZARD" = 1 ] && have_tty && [ "$ASSUME_YES" = 0 ]
}

wizard_intro() {
    cat <<EOF

====================================================================
 TextCascade Server 安装向导
====================================================================
 每一步都给出默认值，直接回车即采用。全部参数会在最后汇总确认，
 确认前不会改动系统。若想跳过问答，用 -y 或直接带命令行参数。
====================================================================
EOF
}

# 1) 版本
wizard_version() {
    [ "$M_VERSION" = 1 ] && return 0

    load_releases
    _default=
    while IFS="$TAB" read -r _tag _pre _note; do
        case "$_note" in *".NET 版本"*) continue ;; esac
        [ "$_pre" != "true" ] || continue
        _default=$_tag
        break
    done <<EOF
$(release_entries)
EOF
    [ -n "$_default" ] || _default=$(release_entries | awk -F'\t' '$3 !~ /\.NET 版本/ {print $1; exit}')
    [ -n "$_default" ] || die "仓库 $REPO 没有可安装的版本。"

    say ""
    say "可用版本（新 → 旧）："
    _n=0
    while IFS="$TAB" read -r _tag _pre _note; do
        _n=$((_n + 1))
        _mark=
        [ "$_tag" = "$_default" ] && _mark=" ← 推荐"
        printf '  %2d) %-22s %s%s\n' "$_n" "$_tag" "$_note" "$_mark"
    done <<EOF
$(release_entries)
EOF

    while :; do
        ask "选择版本序号（回车用 ${_default#v}）: " || { TAG=$_default; VERSION=${TAG#v}; return 0; }
        case "$REPLY" in
            "") TAG=$_default; VERSION=${TAG#v}; return 0 ;;
            *[!0-9]*) say "请输入序号。" ; continue ;;
        esac
        [ "$REPLY" -ge 1 ] && [ "$REPLY" -le "$_n" ] \
            || { say "序号超出范围（1-$_n）。"; continue; }
        TAG=$(release_entries | sed -n "${REPLY}p" | cut -f1)
        VERSION=${TAG#v}
        return 0
    done
}

# 2) 监听端口
wizard_port() {
    [ "$M_PORT" = 1 ] && return 0
    ask_default "监听端口" "$PORT" || return 0
    case "$REPLY" in
        ""|*[!0-9]*) return 0 ;;
    esac
    if [ "$REPLY" -ge 1 ] && [ "$REPLY" -le 65535 ]; then
        PORT=$REPLY
    else
        warn "端口必须是 1-65535，保持 $PORT。"
    fi
}

# 3) 证书
wizard_cert() {
    [ "$M_CERT" = 1 ] && return 0

    # 已有安装且证书可用时默认沿用
    _existing=
    if [ -f "$CONFIG_FILE" ]; then
        _existing=$(sed -n 's/^[[:space:]]*certificate_path[[:space:]]*=[[:space:]]*"\(.*\)".*/\1/p' "$CONFIG_FILE" | head -n 1)
    fi

    if [ -n "$_existing" ] && [ -f "$_existing" ]; then
        ask_yes "沿用现有证书 $_existing" y || return 0
        if [ "$REPLY" != "" ] && [ "$REPLY" != "n" ] && [ "$REPLY" != "N" ]; then
            return 0
        fi
    fi

    say ""
    say "TLS 证书是必需项（服务只提供 HTTPS）。"
    say "  1) 我有证书文件（PEM 证书 + 同目录 .key，或单文件 PFX/P12）"
    say "  2) 生成自签证书（仅测试用；客户端需要跳过校验或手动信任）"
    while :; do
        ask "选择 [1]: " || return 0
        case "${REPLY:-1}" in
            1)
                ask "证书文件路径（.pem/.crt/.pfx/.p12）: " || return 0
                [ -n "$REPLY" ] || { say "路径不能为空。"; continue; }
                CERT_SRC=$REPLY
                case "$CERT_SRC" in
                    *.pfx|*.p12|*.PFX|*.P12) return 0 ;;
                esac
                if [ -f "${CERT_SRC%.*}.key" ] || grep -q 'BEGIN .*PRIVATE KEY' "$CERT_SRC" 2>/dev/null; then
                    return 0
                fi
                ask "私钥路径（回车沿用 ${CERT_SRC%.*}.key）: " || return 0
                [ -n "$REPLY" ] && KEY_SRC=$REPLY
                return 0
                ;;
            2)
                ask_default "自签证书的域名或主机名" "$(hostname 2>/dev/null || echo localhost)" || return 0
                SELF_SIGNED=1
                CN=$REPLY
                warn "自签证书仅适合内网测试。"
                return 0
                ;;
            *) say "请输入 1 或 2。" ;;
        esac
    done
}

# 4) 首个用户
wizard_user() {
    [ "$M_USER" = 1 ] && return 0

    if [ -f "$USERS_FILE" ] && [ "$(count_users)" != 0 ]; then
        say ""
        say "已有用户，跳过创建（可用 runuser 命令另行增删）。"
        return 0
    fi

    say ""
    if ask_yes "现在创建第一个用户" y; then
        while :; do
            ask "用户名: " || return 0
            [ -n "$REPLY" ] || { say "用户名不能为空。"; continue; }
            USERNAME=$REPLY
            break
        done
        PASSWORD_STDIN=0
        PASSWORD=
        return 0
    fi
}

wizard_review() {
    _shown_cert=${CERT_SRC:-$CERT_DIR/${CERT_NAME:-server.pem}}
    [ "$SELF_SIGNED" = 1 ] && _shown_cert="自签（CN=$CN）"
    cat <<EOF

--------------------------------------------------------------------
即将执行的操作
  版本      : ${VERSION:-（待解析）}
  架构      : linux-$ARCH_TAG
  监听      : $BIND:$PORT（仅 HTTPS）
  证书      : $_shown_cert
  程序      : $BIN_DIR/$APP
  配置      : $CONFIG_FILE
  secret    : $ENV_FILE
  用户表    : $USERS_FILE
  状态文件  : $DATA_DIR/textcascade.state.json
  首个用户  : ${USERNAME:-（不创建）}
  已有数据  : $(if [ -f "$CONFIG_FILE" ]; then echo "检测到现有安装，将保留配置/secret/用户，仅更新程序"; else echo "全新安装"; fi)
--------------------------------------------------------------------
EOF
    confirm "确认开始安装？" || die "已取消，未做任何改动。"
}

run_wizard() {
    wizard_intro
    wizard_version
    wizard_port
    wizard_cert
    wizard_user

    # 版本可能由向导选定（离线包时可能仍为空，留给 resolve_version 从文件名推断）
    [ -n "$VERSION" ] && _check_version_shape
    check_cert_args
    wizard_review
}

# ---------------------------------------------------------------- 动作

preflight() {
    require_root
    require_systemd
    detect_arch

    WORK=$(mktemp -d "${TMPDIR:-/tmp}/textcascade-install.XXXXXX") || die "无法创建临时目录。"
    chmod 700 "$WORK"
}

action_install() {
    preflight

    if wizard_enabled; then
        run_wizard
        if [ -z "$VERSION" ]; then
            step "解析版本"
            resolve_version
        fi
        step "获取发行包"
        acquire_payload
    else
        step "解析版本"
        resolve_version
        say "目标版本：$VERSION（linux-$ARCH_TAG）"

        step "检查证书参数"
        check_cert_args

        step "获取发行包"
        acquire_payload
    fi

    step "准备系统用户与目录"
    ensure_user_and_dirs

    step "准备证书"
    prepare_certificate

    step "写入 token secret"
    write_env

    step "写入配置"
    write_config
    write_users_if_missing

    step "安装程序与 systemd 单元"
    install_binary
    install_unit

    if [ "$NO_START" = 1 ]; then
        say "已按 --no-start 跳过启动。"
        print_summary
        return 0
    fi

    step "启动服务"
    if [ "$BINARY_CHANGED" = 1 ] || [ "$UNIT_CHANGED" = 1 ] || [ "$CONFIG_CHANGED" = 1 ]; then
        start_service
    else
        ensure_service_running
    fi

    step "创建用户"
    add_first_user

    step "验收"
    verify_login

    _n=$(count_users)
    print_summary
    if [ "$_n" = 0 ]; then
        warn "当前没有任何用户，客户端无法登录。用上面的「用户管理」命令创建第一个用户。"
    fi
}

action_upgrade() {
    [ -f "$CONFIG_FILE" ] || die "未发现现有安装（$CONFIG_FILE）；请先执行 install。"
    preflight
    step "解析版本"
    resolve_version

    _current=$(installed_version 2>/dev/null | sed 's/（记录值，该二进制不支持 --version）//' || true)
    if [ -n "$_current" ] && [ "$_current" = "$VERSION" ]; then
        say "当前已是 $VERSION，检查服务状态后结束。"
        ensure_service_running
        return 0
    fi
    if [ -n "$_current" ]; then
        say "升级：$_current → $VERSION（linux-$ARCH_TAG）"
    else
        say "升级到：$VERSION（linux-$ARCH_TAG）"
    fi

    step "获取发行包"
    acquire_payload

    step "替换二进制"
    install_binary

    step "启动服务"
    start_service

    print_summary
}

action_list() {
    detect_arch
    WORK=$(mktemp -d "${TMPDIR:-/tmp}/textcascade-list.XXXXXX") || die "无法创建临时目录。"
    chmod 700 "$WORK"
    print_release_list
}

action_status() {
    require_root
    detect_arch

    if [ ! -x "$BIN_DIR/$APP" ]; then
        die "未安装：$BIN_DIR/$APP 不存在。"
    fi

    _ver=$(installed_version || true)
    say "版本      : ${_ver:-未知}"
    say "服务状态  : $(systemctl is-active "$SERVICE" 2>/dev/null || true)"
    say "开机自启  : $(systemctl is-enabled "$SERVICE" 2>/dev/null || true)"

    if [ -f "$CONFIG_FILE" ]; then
        _port=$(sed -n 's/^[[:space:]]*port[[:space:]]*=[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$CONFIG_FILE" | head -n 1)
        _bind=$(sed -n 's/^[[:space:]]*bind[[:space:]]*=[[:space:]]*"\(.*\)".*/\1/p' "$CONFIG_FILE" | head -n 1)
        [ -n "$_port" ] && PORT=$_port
        [ -n "$_bind" ] && BIND=$_bind
    fi

    if health_ok; then
        say "健康检查  : OK（$(health_url)）"
    else
        say "健康检查  : 失败（$(health_url)）"
    fi
    say "用户数    : $(count_users)"
}

action_uninstall() {
    require_root

    if [ -f "$UNIT_FILE" ]; then
        systemctl disable --now "$SERVICE" >/dev/null 2>&1 || true
        rm -f "$UNIT_FILE"
        systemctl daemon-reload
        say "已停止并移除服务单元。"
    else
        say "服务单元不存在，跳过。"
    fi

    rm -rf "$BIN_DIR"
    say "已删除程序目录 $BIN_DIR。"

    if [ "$PURGE" = 1 ]; then
        if [ "$ASSUME_YES" = 0 ] && ! confirm "将删除 $CONFIG_DIR 与 $DATA_DIR（含用户表与 token secret），继续？"; then
            die "已取消。"
        fi
        rm -rf "$CONFIG_DIR" "$DATA_DIR"
        say "已删除配置与数据目录。"
        if [ "$DELETE_USER" = 1 ] && id "$SERVICE_USER" >/dev/null 2>&1; then
            userdel "$SERVICE_USER" 2>/dev/null || warn "删除系统用户 $SERVICE_USER 失败（可能仍被占用）。"
            say "已删除系统用户 $SERVICE_USER。"
        fi
    else
        say "配置与数据保留在 $CONFIG_DIR 与 $DATA_DIR（加 --purge 可一并删除）。"
    fi
}

# ---------------------------------------------------------------- 主流程

if [ "$PASSWORD_STDIN" = 1 ]; then
    IFS= read -r PASSWORD || PASSWORD=
    PASSWORD=$(printf '%s' "$PASSWORD" | tr -d '\r')
    [ -n "$PASSWORD" ] || die "标准输入为空：--password-stdin 需要一行非空密码。"
    ASSUME_YES=1
fi

mark_explicit

case "$ACTION" in
    install) action_install ;;
    upgrade) action_upgrade ;;
    status) action_status ;;
    uninstall) action_uninstall ;;
    list) action_list ;;
    *) die "未知动作：$ACTION" ;;
esac
