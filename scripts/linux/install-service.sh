#!/usr/bin/env bash
# M0-11 把 publish.ps1 產生的 linux-x64／linux-arm64 版本安裝成 systemd 服務（設定、資料與發布目錄分離）。
#   /opt/warehouse/releases/<版本>/              發布檔（已存在則不覆寫）
#   /etc/warehouse/appsettings.Production.json   0640 root:warehouse（只有 root 與服務使用者可讀）
#   /var/lib/warehouse/{data,keys}               服務使用者 warehouse 可寫
# 用法（需 root）：
#   sudo ./scripts/linux/install-service.sh --release <發布資料夾> [--name warehouse-a] [--port 5081] \
#        [--connection-string-file <檔案>] [--verify-restart]
# --connection-string-file：第一次安裝、設定檔不存在時使用；讀取後立即刪除，避免密碼出現在命令列。
set -euo pipefail

NAME="warehouse-a"
PORT="5081"
RELEASE=""
CS_FILE=""
VERIFY_RESTART="false"
APP_USER="warehouse"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) RELEASE="$2"; shift 2 ;;
    --name) NAME="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --connection-string-file) CS_FILE="$2"; shift 2 ;;
    --verify-restart) VERIFY_RESTART="true"; shift ;;
    *) echo "未知參數：$1" >&2; exit 2 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "請以 root 執行（sudo）。" >&2; exit 1; }
[[ -f "$RELEASE/release.json" ]] || { echo "找不到 $RELEASE/release.json，請指定 publish.ps1 的輸出資料夾。" >&2; exit 1; }
systemctl list-unit-files "$NAME.service" --no-legend | grep -q . && { echo "服務 $NAME 已存在，請先執行 uninstall-service.sh。" >&2; exit 1; }

release_name=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["name"])' "$RELEASE/release.json")
deployment=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["deployment"])' "$RELEASE/release.json")
release_dir="/opt/warehouse/releases/$release_name"

# 1. 服務專用系統使用者（不能登入、沒有家目錄）
id "$APP_USER" &>/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin "$APP_USER"

# 2. 目錄與發布檔（每個版本獨立，不覆寫）
mkdir -p /opt/warehouse/releases /etc/warehouse /var/lib/warehouse/data /var/lib/warehouse/keys
if [[ -d "$release_dir" ]]; then
  echo "版本目錄已存在，沿用：$release_dir"
else
  cp -r "$RELEASE" "$release_dir"
  echo "已複製版本：$release_dir"
fi
chown -R root:root /opt/warehouse
chmod -R a+rX /opt/warehouse
[[ -f "$release_dir/Warehouse.Web" ]] && chmod 755 "$release_dir/Warehouse.Web"

# 3. 設定檔（只在第一次建立；用 python3 產生 JSON，避免特殊字元破壞格式）
config="/etc/warehouse/appsettings.Production.json"
if [[ ! -f "$config" ]]; then
  if [[ -z "$CS_FILE" ]]; then
    python3 -c 'import json; print(json.dumps({"ConnectionStrings": {"Warehouse": "Host=localhost;Port=5432;Database=warehouse;Username=warehouse_app;Password=<請填入>;Timeout=3"}}, indent=2))' > "$config"
    chown root:"$APP_USER" "$config"; chmod 0640 "$config"
    echo "已建立設定範本 $config，請填入連線字串後重新執行。" >&2
    exit 1
  fi
  python3 -c 'import json,sys; print(json.dumps({"ConnectionStrings": {"Warehouse": open(sys.argv[1]).read().strip()}}, indent=2))' "$CS_FILE" > "$config"
  rm -f "$CS_FILE"
  echo "已建立設定檔（連線字串不顯示）。"
fi
chown root:"$APP_USER" /etc/warehouse "$config"
chmod 0750 /etc/warehouse
chmod 0640 "$config"
chown -R "$APP_USER":"$APP_USER" /var/lib/warehouse
chmod -R 0750 /var/lib/warehouse

# 4. systemd unit：framework-dependent 用 dotnet 執行 dll；self-contained 直接執行檔
if [[ "$deployment" == "self-contained" ]]; then
  exec_start="$release_dir/Warehouse.Web"
else
  dotnet_path=$(command -v dotnet) || { echo "找不到 dotnet，framework-dependent 版本需要先安裝 ASP.NET Core Runtime 10。" >&2; exit 1; }
  exec_start="$dotnet_path $release_dir/Warehouse.Web.dll"
fi

cat > "/etc/systemd/system/$NAME.service" <<EOF
[Unit]
Description=Warehouse 倉庫管理系統 ($NAME)
After=network-online.target
Wants=network-online.target

[Service]
# Type=notify：程式完成啟動後才通知 systemd 就緒（由 AddSystemd() 提供）
Type=notify
User=$APP_USER
Group=$APP_USER
WorkingDirectory=$release_dir
ExecStart=$exec_start
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:$PORT
Environment=WAREHOUSE_CONFIG_DIR=/etc/warehouse
Environment=DOTNET_NOLOGO=1
Restart=on-failure
RestartSec=5
TimeoutStartSec=60
# 加固：不能提權、系統目錄唯讀、看不到 /home、獨立 /tmp，只能寫 /var/lib/warehouse
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/warehouse

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "$NAME.service"
echo "已建立並啟動服務 $NAME（使用者 $APP_USER，埠 $PORT）"

wait_ready() {
  for _ in $(seq 1 60); do
    if [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT/health/ready")" == "200" ]]; then return 0; fi
    sleep 0.5
  done
  return 1
}

wait_ready || { echo "服務已啟動但 /health/ready 未在 30 秒內回 200，請看 journalctl -u $NAME" >&2; exit 1; }
echo "服務已就緒：http://localhost:$PORT/health/ready = 200"

if [[ "$VERIFY_RESTART" == "true" ]]; then
  start=$(date +%s.%N)
  systemctl restart "$NAME.service"
  wait_ready || { echo "重新啟動後未就緒。" >&2; exit 1; }
  printf '重新啟動驗證：%.1f 秒後恢復就緒\n' "$(echo "$(date +%s.%N) - $start" | bc)"
fi

systemctl show "$NAME.service" -p ActiveState -p SubState -p MainPID -p MemoryCurrent --no-pager
