#!/usr/bin/env bash
# M7-01 建立正式（或演練用）資料庫，擁有者帳號與網站帳號分開（與 scripts/windows/init-prod-db.ps1 相同規則）。
# 用法（需 root；以 postgres 系統帳號經 peer 驗證連線，不需要 postgres 密碼）：
#   sudo ./scripts/linux/init-prod-db.sh [--database warehouse_prod] [--port 5432]
#   <db>_owner：擁有全部資料表，只用來套用 Migration（密碼由你輸入、自行保管）
#   <db>_app：網站使用，只有 DML／序列／函式權限，不能停用 trigger、不能 TRUNCATE、不能改結構
#   warehouse_backup：備份用（pg_read_all_data＋REPLICATION）
# 隨機密碼寫到 /etc/warehouse/secrets（root:root 0700）：<db>.connection.txt、backup.pgpass；重跑時沿用。
set -euo pipefail

DATABASE="warehouse_prod"
PORT="5432"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --database) DATABASE="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    *) echo "未知參數：$1" >&2; exit 2 ;;
  esac
done
[[ $EUID -eq 0 ]] || { echo "請以 root 執行（sudo）。" >&2; exit 1; }
[[ "$DATABASE" =~ ^[a-z][a-z0-9_]{2,40}$ ]] || { echo "資料庫名稱只能是小寫英數與底線。" >&2; exit 2; }

OWNER="${DATABASE}_owner"; APP="${DATABASE}_app"; BACKUP="warehouse_backup"
SECRETS=/etc/warehouse/secrets
install -d -m 0700 -o root -g root "$SECRETS"
CONN="$SECRETS/$DATABASE.connection.txt"; PGPASS="$SECRETS/backup.pgpass"
random_secret() { tr -dc 'A-HJ-NP-Za-km-z2-9' < /dev/urandom | head -c 32 || true; }
APP_PW=$([[ -f "$CONN" ]] && sed -n 's/.*Password=\([^;]*\).*/\1/p' "$CONN" || random_secret)
BACKUP_PW=$([[ -f "$PGPASS" ]] && head -n1 "$PGPASS" | awk -F: '{print $NF}' || random_secret)
[[ -n "$APP_PW" && -n "$BACKUP_PW" ]] || { echo "$SECRETS 的檔案格式不對，請刪除後重跑。" >&2; exit 1; }

read -r -s -p "$OWNER 密碼（新帳號才會設定；至少 12 字）：" OWNER_PW; echo
read -r -s -p "再輸入一次：" OWNER_PW2; echo
[[ "$OWNER_PW" == "$OWNER_PW2" ]] || { echo "兩次輸入的密碼不一致。" >&2; exit 1; }
(( ${#OWNER_PW} >= 12 )) || { echo "擁有者密碼至少 12 字。" >&2; exit 1; }

q() { printf "'%s'" "${1//\'/\'\'}"; }   # SQL 字串常值
psql_as_postgres() { sudo -u postgres psql -X -q -p "$PORT" -v ON_ERROR_STOP=1 "$@"; }

# SQL 經標準輸入：密碼不出現在命令列
psql_as_postgres -d postgres <<SQL
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', '$OWNER', $(q "$OWNER_PW")) WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$OWNER') \gexec
SELECT format('CREATE ROLE %I LOGIN', '$APP') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$APP') \gexec
ALTER ROLE $APP LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD $(q "$APP_PW");
SELECT format('CREATE ROLE %I LOGIN REPLICATION', '$BACKUP') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$BACKUP') \gexec
ALTER ROLE $BACKUP LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE REPLICATION PASSWORD $(q "$BACKUP_PW");
GRANT pg_read_all_data TO $BACKUP;
SELECT format('CREATE DATABASE %I OWNER %I ENCODING %L TEMPLATE template0', '$DATABASE', '$OWNER', 'UTF8') WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$DATABASE') \gexec
SQL
psql_as_postgres -d "$DATABASE" <<SQL
REVOKE ALL ON DATABASE $DATABASE FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE $DATABASE TO $OWNER, $APP;
GRANT CONNECT ON DATABASE $DATABASE TO $BACKUP;
ALTER SCHEMA public OWNER TO $OWNER;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO $APP, $BACKUP;
ALTER DEFAULT PRIVILEGES FOR ROLE $OWNER IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $APP;
ALTER DEFAULT PRIVILEGES FOR ROLE $OWNER IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $APP;
ALTER DEFAULT PRIVILEGES FOR ROLE $OWNER IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO $APP;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $APP;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $APP;
SQL

umask 077
printf 'Host=localhost;Port=%s;Database=%s;Username=%s;Password=%s;Timeout=3' "$PORT" "$DATABASE" "$APP" "$APP_PW" > "$CONN"
printf 'localhost:%s:*:%s:%s\n' "$PORT" "$BACKUP" "$BACKUP_PW" > "$PGPASS"
echo "完成：$DATABASE（擁有者 $OWNER、網站帳號 $APP、備份帳號 $BACKUP）；連線字串在 $CONN"
echo "下一步：sudo ./scripts/linux/migrate-db.sh --release <發布資料夾> --database $DATABASE"
