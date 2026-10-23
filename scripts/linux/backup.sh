#!/usr/bin/env bash
# M7-02 Linux 每日備份（與 scripts/windows/backup.ps1 相同規則，systemd timer 以 root 執行；docs/13）：
#   pg_dump -Fc 每個資料庫（pg_restore -l 檢查）→ /var/backups/warehouse/dump/daily 保留 30 份、每月第一份留 7 年
#   附件 rsync（只增不刪）；有 EXTERNAL_COPY 時再同步過去；結果寫 /var/lib/warehouse/data/backup-status.json
# 設定：/etc/warehouse/backup.conf（shell 變數）：DATABASES="warehouse_prod" EXTERNAL_COPY="" KEEP_DAILY=30 KEEP_MONTHLY_YEARS=7
# 帳號：warehouse_backup，密碼在 /etc/warehouse/secrets/backup.pgpass（init-prod-db.sh 產生）。
# 時間點還原（WAL 封存＋pg_basebackup）依 NAS 的 PostgreSQL 安裝方式設定，見 docs/13；本腳本只做每日邏輯備份。
set -euo pipefail

BACKUP_ROOT=/var/backups/warehouse
DATA=/var/lib/warehouse/data
DATABASES="warehouse_prod"; EXTERNAL_COPY=""; KEEP_DAILY=30; KEEP_MONTHLY_YEARS=7; PORT=5432
[[ -f /etc/warehouse/backup.conf ]] && source /etc/warehouse/backup.conf
export PGPASSFILE=/etc/warehouse/secrets/backup.pgpass PGCLIENTENCODING=UTF8
umask 077
install -d -m 0700 "$BACKUP_ROOT" "$BACKUP_ROOT/dump/daily" "$BACKUP_ROOT/dump/monthly" "$BACKUP_ROOT/attachments"

started=$(date +%s); stamp=$(date +%Y%m%d-%H%M%S); month=$(date +%Y%m)
files="[]"; message=""; ok=true; external_ok=null; external_at=null
add_file() { files=$(python3 -c 'import json,sys,os; f=json.loads(sys.argv[1]); f.append({"kind": sys.argv[2], "name": os.path.basename(sys.argv[3]), "sizeMb": round(os.path.getsize(sys.argv[3])/1048576, 1)}); print(json.dumps(f))' "$files" "$1" "$2"); }

run() {
  for db in $DATABASES; do
    file="$BACKUP_ROOT/dump/daily/$db-$stamp.dump"
    pg_dump -h localhost -p "$PORT" -U warehouse_backup -w -d "$db" -Fc -f "$file" || { message="pg_dump $db 失敗"; return 1; }
    pg_restore -l "$file" >/dev/null || { message="$file 無法讀取"; return 1; }
    add_file dump "$file"
    [[ -f "$BACKUP_ROOT/dump/monthly/$db-$month.dump" ]] || cp "$file" "$BACKUP_ROOT/dump/monthly/$db-$month.dump"
    ls -1 "$BACKUP_ROOT/dump/daily/$db-"*.dump | sort | head -n -"$KEEP_DAILY" | xargs -r rm -f
    cutoff=$(date -d "-$KEEP_MONTHLY_YEARS years" +%Y%m)
    for m in "$BACKUP_ROOT/dump/monthly/$db-"*.dump; do
      [[ -e "$m" ]] || continue
      ym=${m##*-}; ym=${ym%.dump}
      [[ "$ym" < "$cutoff" ]] && rm -f "$m"
    done
  done
  [[ -d "$DATA/attachments" ]] && { rsync -a "$DATA/attachments/" "$BACKUP_ROOT/attachments/" || { message="附件備份失敗"; return 1; }; }
  if [[ -n "$EXTERNAL_COPY" ]]; then
    external_ok=false
    if rsync -a --delete "$BACKUP_ROOT/dump/" "$EXTERNAL_COPY/dump/" && rsync -a "$BACKUP_ROOT/attachments/" "$EXTERNAL_COPY/attachments/"; then
      external_ok=true; external_at="\"$(date -Iseconds)\""
    else
      message="外部複本失敗"
    fi
  fi
}
run || ok=false

free_gb=$(df -P "$BACKUP_ROOT" | awk 'NR==2 {printf "%.1f", $4/1048576}')
python3 - "$DATA/backup-status.json" <<PY
import json, sys, os
status = {"finishedAt": "$(date -Iseconds)", "succeeded": $([[ $ok == true ]] && echo True || echo False),
          "message": "$message" or None, "durationSeconds": $(( $(date +%s) - started )), "files": json.loads('$files'),
          "backupRoot": "$BACKUP_ROOT", "freeGb": $free_gb, "externalCopyPath": "$EXTERNAL_COPY",
          "externalCopiedAt": $( [[ $external_at == null ]] && echo None || echo "$external_at" ),
          "externalCopySucceeded": $( [[ $external_ok == null ]] && echo None || { [[ $external_ok == true ]] && echo True || echo False; } )}
path = sys.argv[1]
os.makedirs(os.path.dirname(path), exist_ok=True)
with open(path + ".tmp", "w") as f: json.dump(status, f, ensure_ascii=False)
os.chmod(path + ".tmp", 0o644)   # 網站帳號要能讀（內容不含秘密）
os.replace(path + ".tmp", path)
PY
echo "$(date -Iseconds) 備份$([[ $ok == true ]] && echo 完成 || echo 失敗) $message" | tee -a "$BACKUP_ROOT/backup.log"
[[ $ok == true ]]
