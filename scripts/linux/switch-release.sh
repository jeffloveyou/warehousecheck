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
command -v python3 >/dev/null || { echo "需要 python3（讀寫 JSON 設定）。" >&2; exit 1; }
[[ "$ROLLBACK" == "true" || -n "$RELEASE" ]] || { echo "請指定 --release 或 --rollback。" >&2; exit 2; }

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
UPSTREAM="$PROXY_CONFIG/upstream.json"
LOG=/var/log/warehouse-deploy.log
STATE="$PROXY_CONFIG/deploy-state.json"
# 同時只能有一個切換（M7-10）
exec 9>/run/warehouse-switch-release.lock
flock -n 9 || { echo "另一個 switch-release 正在執行，請等它完成後再試。" >&2; exit 1; }
log() { echo "$(date -Iseconds) $*" | tee -a "$LOG"; }
port_of() { [[ "$1" == "A" ]] && echo "$PORT_A" || echo "$PORT_B"; }
service_of() { echo "warehouse-${1,,}"; }
# 讀不到或不是 JSON 時輸出空字串（輪詢期間服務尚未回應是正常情況，不印 Traceback）
json_field() { python3 -c '
import json,sys
try: print(json.load(sys.stdin).get(sys.argv[1], ""))
except Exception: print("")' "$1"; }
version_at() { curl -sf --max-time 3 "$1/health/ready" | json_field version || true; }
# 代理回應的 X-Warehouse-Slot（M7-10）；舊版代理沒有時輸出空字串
slot_at() { curl -sf --max-time 3 -o /dev/null -D - "$1/health/ready" | tr -d "\r" | awk -F": " "tolower(\$1)==\"x-warehouse-slot\"{print \$2}" || true; }
# 部署狀態：每個槽位的版本與是否曾成功上線（回滾只回到成功上線過的版本）
# state_get 槽位 欄位 → 輸出值（good 為 true／false；沒有紀錄時空字串）
state_get() { [[ -f "$STATE" ]] || { echo ""; return; }; python3 -c '
import json,sys
v=(json.load(open(sys.argv[1])).get(sys.argv[2]) or {}).get(sys.argv[3], "")
print(str(v).lower() if isinstance(v, bool) else v)' "$STATE" "$1" "$2"; }
# state_set 槽位 版本 true|false
state_set() { python3 -c '
import json,sys,os,datetime
p=sys.argv[1]; s=json.load(open(p)) if os.path.exists(p) else {}
s[sys.argv[2]]={"version": sys.argv[3], "good": sys.argv[4]=="true", "updatedAt": datetime.datetime.now().astimezone().isoformat()}
open(p+".tmp","w").write(json.dumps(s, indent=2)); os.replace(p+".tmp", p)' "$STATE" "$1" "$2" "$3"; }

wait_version() {  # $1=URL $2=期望版本（空字串＝只要 200）$3=秒數 [$4=期望槽位：代理有回 X-Warehouse-Slot 時必須相符]
  local end=$((SECONDS + $3)) v s
  while (( SECONDS < end )); do
    v=$(version_at "$1")
    if [[ -n "$v" && ( -z "$2" || "$v" == "$2" ) ]]; then
      if [[ -z "${4:-}" ]]; then return 0; fi
      s=$(slot_at "$1")
      if [[ -z "$s" || "$s" == "$4" ]]; then return 0; fi
    fi
    sleep 0.3
  done
  return 1
}

smoke() {  # $1=槽位 URL；curl 失敗（逾時、500）時不能讓 set -e 直接結束，要走「停止目標槽位＋記錄」
  local connected
  connected=$(curl -sf --max-time 5 "$1/api/system/info" | json_field databaseConnected || true)
  [[ "$connected" == "True" ]]
}

write_upstream() {  # 暫存檔＋mv（同一檔案系統內為原子置換），代理不會讀到寫一半的內容
  python3 -c 'import json,sys; print(json.dumps({"ActiveSlot": sys.argv[1], "ReverseProxy": {"Clusters": {"app": {"Destinations": {"active": {"Address": f"http://localhost:{sys.argv[2]}/"}}}}}}, indent=2))' \
    "$1" "$(port_of "$1")" > "$UPSTREAM.tmp"
  chown --reference="$UPSTREAM" "$UPSTREAM.tmp"; chmod 0640 "$UPSTREAM.tmp"   # 沿用原檔擁有者（root:warehouse-proxy）
  mv -f "$UPSTREAM.tmp" "$UPSTREAM"
}

[[ -f "$UPSTREAM" ]] || { echo "找不到 $UPSTREAM，請先以 install-service.sh 安裝代理。" >&2; exit 1; }
current=$(json_field ActiveSlot < "$UPSTREAM")
target=$([[ "$current" == "A" ]] && echo B || echo A)
current_url="http://localhost:$(port_of "$current")"
target_url="http://localhost:$(port_of "$target")"
current_version=$(version_at "$current_url")

if [[ "$ROLLBACK" == "true" ]]; then
  systemctl cat "$(service_of "$target")" >/dev/null 2>&1 || { log "失敗：待命槽位 $target 沒有安裝服務，無法回滾"; exit 1; }
  # 只回到曾成功上線的版本：更新失敗留在待命槽位的新版不能被切過去
  [[ "$(state_get "$target" good)" != "false" ]] || { log "失敗：待命槽位 $target 的版本 $(state_get "$target" version) 未曾成功上線，拒絕回滾"; exit 1; }
  log "開始回滾：$current（$current_version）→ $target"
  systemctl start "$(service_of "$target")" || { log "失敗：槽位 $target 無法啟動，流量維持 $current"; exit 1; }
  wait_version "$target_url" "" 30 || { systemctl stop "$(service_of "$target")"; log "失敗：槽位 $target 未就緒，流量維持 $current"; exit 1; }
  target_version=$(version_at "$target_url")
  smoke "$target_url" || { systemctl stop "$(service_of "$target")"; log "失敗：冒煙測試資料庫未連線，流量維持 $current"; exit 1; }
else
  entry=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("entry", "Warehouse.Web"))' "$RELEASE/release.json")
  [[ "$entry" == "Warehouse.Web" ]] || { echo "$RELEASE 不是主程式版本（entry=$entry）。" >&2; exit 1; }
  target_version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$RELEASE/release.json")
  log "開始更新：$current（$current_version）→ $target（$target_version）"
  if [[ -n "$current_version" && -z "$(state_get "$current" version)" ]]; then state_set "$current" "$current_version" true; fi
  state_set "$target" "$target_version" false
  "$SCRIPT_DIR/uninstall-service.sh" --name "$(service_of "$target")" >/dev/null
  if ! "$SCRIPT_DIR/install-service.sh" --release "$RELEASE" --name "$(service_of "$target")" --port "$(port_of "$target")"; then
    systemctl stop "$(service_of "$target")" 2>/dev/null || true
    log "失敗：槽位 $target 新版未就緒，流量維持 $current"; exit 1
  fi
  smoke "$target_url" || { systemctl stop "$(service_of "$target")"; log "失敗：冒煙測試資料庫未連線，流量維持 $current"; exit 1; }
fi

write_upstream "$target"
if ! wait_version "$PROXY_URL" "$target_version" 15 "$target"; then
  write_upstream "$current" || { log "嚴重：無法改回 upstream.json，目前指向 $target；請檢查 $UPSTREAM 與兩個槽位的服務"; exit 1; }
  log "失敗：代理未在 15 秒內轉到 $target，已改回 $current"; exit 1
fi
state_set "$target" "$target_version" true
log "流量已切換：$current → $target"

echo "排空 $DRAIN 秒…"
sleep "$DRAIN"
systemctl stop "$(service_of "$current")" 2>/dev/null || true
systemctl disable "$(service_of "$current")" 2>/dev/null || true
systemctl enable "$(service_of "$target")" 2>/dev/null || log "警告：無法設定 $(service_of "$target") 開機啟動"
log "完成：目前槽位 $target，版本 $target_version（$(service_of "$current") 已停止，保留供回滾）"
