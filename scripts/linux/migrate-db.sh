#!/usr/bin/env bash
# M7-01 以擁有者帳號套用發布資料夾的 migrations.sql（與 scripts/windows/migrate-db.ps1 相同流程）：
#   列出待套用 → pg_dump 備份到 <backup-root>/pre-migrate → 輸入 YES → psql 套用 → 補授權 → 再確認
# 用法：./scripts/linux/migrate-db.sh --release <發布資料夾> [--database warehouse_prod] [--port 5432] [--backup-root /var/backups/warehouse] [--skip-backup]
set -euo pipefail

RELEASE=""; DATABASE="warehouse_prod"; PORT="5432"; BACKUP_ROOT="/var/backups/warehouse"; SKIP_BACKUP="false"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --release) RELEASE="$2"; shift 2 ;;
    --database) DATABASE="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --backup-root) BACKUP_ROOT="$2"; shift 2 ;;
    --skip-backup) SKIP_BACKUP="true"; shift ;;
    *) echo "未知參數：$1" >&2; exit 2 ;;
  esac
done
SCRIPT="$RELEASE/migrations.sql"
[[ -f "$SCRIPT" ]] || { echo "找不到 $SCRIPT（M7-01 以後的 publish.ps1 才會產生）。" >&2; exit 1; }
OWNER="${DATABASE}_owner"; APP="${DATABASE}_app"

mapfile -t ALL < <(grep -oE "VALUES \('[0-9]{14}_[A-Za-z0-9_]+'" "$SCRIPT" | sed -E "s/VALUES \('//; s/'$//" | awk '!seen[$0]++')
(( ${#ALL[@]} > 0 )) || { echo "$SCRIPT 裡找不到任何 Migration。" >&2; exit 1; }

read -r -s -p "$OWNER 密碼：" PGPASSWORD; echo
export PGPASSWORD PGCLIENTENCODING=UTF8
trap 'unset PGPASSWORD' EXIT
run_psql() { psql -X -q -h localhost -p "$PORT" -U "$OWNER" -d "$DATABASE" -v ON_ERROR_STOP=1 "$@"; }
applied() {
  if [[ "$(run_psql -A -t -c "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL")" == "t" ]]; then
    run_psql -A -t -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory"'
  fi
}
pending() { local done; done=$(applied); for m in "${ALL[@]}"; do grep -qxF "$m" <<<"$done" || echo "$m"; done; }

# 先確認連得上：下面的 process substitution 失敗不會觸發 set -e，會被誤判成「全部待套用」（審查 M6）
run_psql -c 'SELECT 1' >/dev/null || { echo "無法連線 $DATABASE（帳號 $OWNER）。" >&2; exit 1; }
declare -A KNOWN=(); for m in "${ALL[@]}"; do KNOWN[$m]=1; done
mapfile -t APPLIED < <(applied)
for m in "${APPLIED[@]}"; do
  [[ -z "$m" ]] && continue
  [[ -n "${KNOWN[$m]:-}" ]] || { echo "資料庫有這個版本不認得的 Migration：$m（資料庫比這個發布版本新）。" >&2; exit 1; }
done
mapfile -t PENDING < <(pending)
if (( ${#PENDING[@]} == 0 )); then echo "$DATABASE 已是最新結構（${#ALL[@]} 個 Migration）。"; exit 0; fi
echo "待套用的 Migration（${#PENDING[@]} 個）："; printf '  %s\n' "${PENDING[@]}"

if [[ "$SKIP_BACKUP" != "true" ]]; then
  install -d -m 0700 "$BACKUP_ROOT" "$BACKUP_ROOT/pre-migrate"   # 完整資料庫（含密碼雜湊）只給 root（審查 M2）
  umask 077
  file="$BACKUP_ROOT/pre-migrate/$DATABASE-$(date +%Y%m%d-%H%M%S).dump"
  pg_dump -h localhost -p "$PORT" -U "$OWNER" -d "$DATABASE" -Fc -f "$file" || { echo "備份失敗，未套用任何 Migration。" >&2; exit 1; }
  echo "備份完成：$file"
fi
read -r -p "確認套用到 $DATABASE？輸入 YES 繼續：" answer
[[ "$answer" == "YES" ]] || { echo "已取消，資料庫未變更。"; exit 1; }

PGOPTIONS='-c lock_timeout=5s' run_psql -f "$SCRIPT" || { echo "套用失敗；若是等鎖逾時，等長時間作業結束後重跑即可。" >&2; exit 1; }
run_psql -c "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO $APP; GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO $APP;"
mapfile -t LEFT < <(pending)
(( ${#LEFT[@]} == 0 )) || { echo "套用後仍有待套用的 Migration：${LEFT[*]}" >&2; exit 1; }
echo "完成：$DATABASE 已套用到 ${ALL[-1]}。"
