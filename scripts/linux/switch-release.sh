#!/usr/bin/env bash
# M0-12 Linux A/B 切換（精簡版，流程同 scripts/windows/switch-release.ps1）：
#   新版裝到待命槽位 → ready → 冒煙 → 改寫代理的 upstream.json → 經代理確認版本 → 排空 → 停止舊槽位（保留供回滾）
# 用法（需 root）：
#   sudo ./scripts/linux/switch-release.sh --release <發布資料夾> [--proxy-url http://localhost:5480] \
#        [--proxy-config /etc/warehouse-proxy] [--port-a 5080] [--port-b 5081] [--drain 10]
#   sudo ./scripts/linux/switch-release.sh --rollback [同上選項]
# 槽位：A＝systemd 服務 warehouse-a、B＝warehouse-b。
set -euo pipefail

RELEASE=""
ROLLBACK="false"
PROXY_URL="http://localhost:5480"
PROXY_CONFIG="/etc/warehouse-proxy"
PORT_A="5080"
PORT_B="5081"
DRAIN="10"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) RELEASE="$2"; shift 2 ;;
    --rollback) ROLLBACK="true"; shift ;;
    --proxy-url) PROXY_URL="$2"; shift 2 ;;
    --proxy-config) PROXY_CONFIG="$2"; shift 2 ;;
    --port-a) PORT_A="$2"; shift 2 ;;
    --port-b) PORT_B="$2"; shift 2 ;;
    --drain) DRAIN="$2"; shift 2 ;;
    *) echo "未知參數：$1" >&2; exit 2 ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "請以 root 執行（sudo）。" >&2; exit 1; }
[[ "$ROLLBACK" == "true" || -n "$RELEASE" ]] || { echo "請指定 --release 或 --rollback。" >&2; exit 2; }

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
UPSTREAM="$PROXY_CONFIG/upstream.json"
LOG=/var/log/warehouse-deploy.log
log() { echo "$(date -Iseconds) $*" | tee -a "$LOG"; }
port_of() { [[ "$1" == "A" ]] && echo "$PORT_A" || echo "$PORT_B"; }
service_of() { echo "warehouse-${1,,}"; }
json_field() { python3 -c 'import json,sys; print(json.load(sys.stdin).get(sys.argv[1], ""))' "$1"; }
version_at() { curl -sf --max-time 3 "$1/health/ready" | json_field version || true; }

wait_version() {  # $1=URL $2=期望版本（空字串＝只要 200）$3=秒數
  local end=$((SECONDS + $3)) v
  while (( SECONDS < end )); do
    v=$(version_at "$1")
    if [[ -n "$v" && ( -z "$2" || "$v" == "$2" ) ]]; then return 0; fi
    sleep 0.3
  done
  return 1
}

write_upstream() {  # 暫存檔＋mv（同一檔案系統內為原子置換），代理不會讀到寫一半的內容
  python3 -c 'import json,sys; print(json.dumps({"ActiveSlot": sys.argv[1], "ReverseProxy": {"Clusters": {"app": {"Destinations": {"active": {"Address": f"http://localhost:{sys.argv[2]}/"}}}}}}, indent=2))' \
    "$1" "$(port_of "$1")" > "$UPSTREAM.tmp"
  chown root:warehouse "$UPSTREAM.tmp"; chmod 0640 "$UPSTREAM.tmp"
  mv -f "$UPSTREAM.tmp" "$UPSTREAM"
}

[[ -f "$UPSTREAM" ]] || { echo "找不到 $UPSTREAM，請先以 install-service.sh 安裝代理。" >&2; exit 1; }
current=$(json_field ActiveSlot < "$UPSTREAM")
target=$([[ "$current" == "A" ]] && echo B || echo A)
current_url="http://localhost:$(port_of "$current")"
target_url="http://localhost:$(port_of "$target")"
current_version=$(version_at "$current_url")

if [[ "$ROLLBACK" == "true" ]]; then
  log "開始回滾：$current（$current_version）→ $target"
  systemctl start "$(service_of "$target")"
  wait_version "$target_url" "" 30 || { systemctl stop "$(service_of "$target")"; log "失敗：槽位 $target 未就緒，流量維持 $current"; exit 1; }
  target_version=$(version_at "$target_url")
else
  target_version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$RELEASE/release.json")
  log "開始更新：$current（$current_version）→ $target（$target_version）"
  "$SCRIPT_DIR/uninstall-service.sh" --name "$(service_of "$target")" >/dev/null
  if ! "$SCRIPT_DIR/install-service.sh" --release "$RELEASE" --name "$(service_of "$target")" --port "$(port_of "$target")"; then
    systemctl stop "$(service_of "$target")" 2>/dev/null || true
    log "失敗：槽位 $target 新版未就緒，流量維持 $current"; exit 1
  fi
  connected=$(curl -sf --max-time 5 "$target_url/api/system/info" | json_field databaseConnected)
  [[ "$connected" == "True" ]] || { systemctl stop "$(service_of "$target")"; log "失敗：冒煙測試資料庫未連線，流量維持 $current"; exit 1; }
fi

write_upstream "$target"
if ! wait_version "$PROXY_URL" "$target_version" 15; then
  write_upstream "$current"
  log "失敗：代理未在 15 秒內轉到 $target，已改回 $current"; exit 1
fi
log "流量已切換：$current → $target"

echo "排空 $DRAIN 秒…"
sleep "$DRAIN"
systemctl stop "$(service_of "$current")" 2>/dev/null || true
systemctl disable "$(service_of "$current")" 2>/dev/null || true
systemctl enable "$(service_of "$target")" 2>/dev/null
log "完成：目前槽位 $target，版本 $target_version（$(service_of "$current") 已停止，保留供回滾）"
