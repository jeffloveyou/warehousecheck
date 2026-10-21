#!/usr/bin/env bash
# M0-11 停止並移除 systemd 服務。預設保留 /opt/warehouse、/etc/warehouse、/var/lib/warehouse（可重新安裝或回滾）。
# 用法：sudo ./scripts/linux/uninstall-service.sh [--name warehouse-a] [--remove-data]
#   --remove-data：連同發布檔、設定、資料與 warehouse 使用者一起刪除（無法復原）。
set -euo pipefail

NAME="warehouse-a"
REMOVE_DATA="false"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --name) NAME="$2"; shift 2 ;;
    --remove-data) REMOVE_DATA="true"; shift ;;
    *) echo "未知參數：$1" >&2; exit 2 ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "請以 root 執行（sudo）。" >&2; exit 1; }

if [[ -f "/etc/systemd/system/$NAME.service" ]]; then
  systemctl disable --now "$NAME.service" || true
  rm -f "/etc/systemd/system/$NAME.service"
  systemctl daemon-reload
  echo "已移除服務 $NAME"
else
  echo "服務 $NAME 不存在"
fi

if [[ "$REMOVE_DATA" == "true" ]]; then
  rm -rf /opt/warehouse /etc/warehouse /var/lib/warehouse
  id warehouse &>/dev/null && userdel warehouse
  echo "已刪除 /opt/warehouse、/etc/warehouse、/var/lib/warehouse 與 warehouse 使用者"
else
  echo "保留 /opt/warehouse、/etc/warehouse、/var/lib/warehouse"
fi
