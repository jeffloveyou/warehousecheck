#!/usr/bin/env bash
# M7-02 Linux 還原（與 scripts/windows/restore.ps1 的 -Dump 相同規則；docs/13）：一律還原到「新」資料庫，不覆蓋現有資料。
# 用法（root）：sudo ./scripts/linux/restore.sh --dump <檔案> --target <新資料庫名> [--compare-with <資料庫>] [--port 5432]
#   擁有者：<新名稱>_owner 若存在，否則 postgres；有 <新名稱>_app 時授權給它。--compare-with 逐表比對筆數（還原演練）。
# 附件：rsync -a /var/backups/warehouse/attachments/ <目的地>/
set -euo pipefail
DUMP=""; TARGET=""; COMPARE=""; PORT=5432
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dump) DUMP="$2"; shift 2 ;;
    --target) TARGET="$2"; shift 2 ;;
    --compare-with) COMPARE="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    *) echo "未知參數：$1" >&2; exit 2 ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "請以 root 執行（sudo）。" >&2; exit 1; }
[[ -f "$DUMP" ]] || { echo "找不到 $DUMP。" >&2; exit 1; }
[[ "$TARGET" =~ ^[a-z][a-z0-9_]{2,40}$ ]] || { echo "--target 只能是小寫英數與底線。" >&2; exit 2; }
export PGCLIENTENCODING=UTF8
pg() { sudo -u postgres psql -X -q -p "$PORT" -v ON_ERROR_STOP=1 "$@"; }

[[ -z "$(pg -A -t -d postgres -c "SELECT 1 FROM pg_database WHERE datname = '$TARGET'")" ]] \
  || { echo "資料庫 $TARGET 已存在；還原一律到新的資料庫，請換名稱。" >&2; exit 1; }
OWNER="${TARGET}_owner"; APP="${TARGET}_app"
[[ -n "$(pg -A -t -d postgres -c "SELECT 1 FROM pg_roles WHERE rolname = '$OWNER'")" ]] || OWNER=postgres
pg -d postgres -c "CREATE DATABASE $TARGET OWNER $OWNER ENCODING 'UTF8' TEMPLATE template0"
start=$(date +%s)
# 備份檔在 0700 目錄：以 root 讀出、經標準輸入交給 postgres 帳號的 pg_restore
sudo -u postgres pg_restore -p "$PORT" -d "$TARGET" --no-owner --role="$OWNER" --exit-on-error < "$DUMP"
if [[ -n "$(pg -A -t -d postgres -c "SELECT 1 FROM pg_roles WHERE rolname = '$APP'")" ]]; then
  pg -d "$TARGET" <<SQL
REVOKE ALL ON DATABASE $TARGET FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE $TARGET TO $OWNER, $APP;
GRANT USAGE ON SCHEMA public TO $APP;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $APP;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $APP;
SQL
fi
if [[ -n "$COMPARE" ]]; then
  counts() { echo "SELECT format('SELECT %L, count(*) FROM %I', tablename, tablename) FROM pg_tables WHERE schemaname = 'public' ORDER BY 1 \gexec" \
    | sudo -u postgres psql -X -A -t -F '|' -p "$PORT" -d "$1"; }
  if diff <(counts "$COMPARE") <(counts "$TARGET"); then echo "每張資料表筆數都相同。"; else echo "上面是筆數不同的資料表（< 來源、> 還原）。"; fi
fi
echo "完成：$TARGET（$(( $(date +%s) - start )) 秒）。"
