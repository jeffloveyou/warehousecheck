#!/usr/bin/env bash
# M7-02 Linux 每日備份（與 scripts/windows/backup.ps1 相同規則，systemd timer 以 root 執行；docs/13）：
#   pg_dump -Fc 每個資料庫（pg_restore -l 檢查）→ /var/backups/warehouse/dump/daily 保留 30 份、每月第一份留 7 年
#   附件 rsync（只增不刪）；有 EXTERNAL_COPY 時再同步過去；結果寫 /var/lib/warehouse/status/backup-status.json
# 設定：/etc/warehouse/backup.conf（shell 變數）：DATABASES="warehouse_prod" EXTERNAL_COPY="" KEEP_DAILY=30 KEEP_MONTHLY_YEARS=7
# 帳號：warehouse_backup，密碼在 /etc/warehouse/secrets/backup.pgpass（init-prod-db.sh 產生）。
# 需要 GNU coreutils（head -n -N、date -d）與 python3、rsync；BusyBox 型 NAS 要先確認（docs/13）。
# 時間點還原（WAL 封存＋pg_basebackup）依 NAS 的 PostgreSQL 安裝方式設定，見 docs/13；本腳本只做每日邏輯備份。
set -euo pipefail

BACKUP_ROOT=/var/backups/warehouse
DATA=/var/lib/warehouse/data
STATUS=/var/lib/warehouse/status
DATABASES="warehouse_prod"; EXTERNAL_COPY=""; KEEP_DAILY=30; KEEP_MONTHLY_YEARS=7; PORT=5432
[[ -f /etc/warehouse/backup.conf ]] && source /etc/warehouse/backup.conf
export PGPASSFILE=/etc/warehouse/secrets/backup.pgpass PGCLIENTENCODING=UTF8
install -d -m 0755 "$STATUS"   # 網站帳號要能讀狀態檔（內容不含秘密）；只有 root 能寫
umask 077
install -d -m 0700 "$BACKUP_ROOT" "$BACKUP_ROOT/dump/daily" "$BACKUP_ROOT/dump/monthly" "$BACKUP_ROOT/attachments"

started=$(date +%s); stamp=$(date +%Y%m%d-%H%M%S); month=$(date +%Y%m)
FILES_JSON="[]"; MESSAGE=""; OK=true; EXTERNAL_OK=""; EXTERNAL_AT=""
add_file() {
  FILES_JSON=$(KIND="$1" FILE="$2" FILES="$FILES_JSON" python3 -c '
import json, os
f = json.loads(os.environ["FILES"])
f.append({"kind": os.environ["KIND"], "name": os.path.basename(os.environ["FILE"]), "sizeMb": round(os.path.getsize(os.environ["FILE"]) / 1048576, 1)})
print(json.dumps(f))')
}

# 每一步都明確檢查：函式在 run || … 的左邊時 set -e 不生效（審查 S9）
run() {
  local db file cutoff m ym
  for db in $DATABASES; do
    file="$BACKUP_ROOT/dump/daily/$db-$stamp.dump"
    pg_dump -h localhost -p "$PORT" -U warehouse_backup -w -d "$db" -Fc -f "$file" || { MESSAGE="pg_dump $db 失敗"; return 1; }
    pg_restore -l "$file" >/dev/null || { MESSAGE="$file 無法讀取"; return 1; }
    add_file dump "$file" || { MESSAGE="寫入檔案清單失敗"; return 1; }
    if [[ ! -f "$BACKUP_ROOT/dump/monthly/$db-$month.dump" ]]; then
      cp "$file" "$BACKUP_ROOT/dump/monthly/$db-$month.dump" || { MESSAGE="每月備份複製失敗"; return 1; }
    fi
    # 只認 <db>-yyyymmdd-hhmmss.dump，共同前綴的資料庫不會互相輪替
    find "$BACKUP_ROOT/dump/daily" -maxdepth 1 -regextype posix-extended -regex ".*/$db-[0-9]{8}-[0-9]{6}\.dump" -printf '%f\n' \
      | sort | head -n -"$KEEP_DAILY" | sed "s|^|$BACKUP_ROOT/dump/daily/|" | xargs -r rm -f || { MESSAGE="輪替每日備份失敗"; return 1; }
    cutoff=$(date -d "-$KEEP_MONTHLY_YEARS years" +%Y%m) || { MESSAGE="date -d 不支援"; return 1; }
    for m in "$BACKUP_ROOT/dump/monthly/$db-"[0-9][0-9][0-9][0-9][0-9][0-9].dump; do
      [[ -e "$m" ]] || continue
      ym=${m##*-}; ym=${ym%.dump}
      if [[ "$ym" < "$cutoff" ]]; then rm -f "$m" || { MESSAGE="刪除過期每月備份失敗"; return 1; }; fi
    done
  done
  if [[ -d "$DATA/attachments" ]]; then
    rsync -a "$DATA/attachments/" "$BACKUP_ROOT/attachments/" || { MESSAGE="附件備份失敗"; return 1; }
  fi
  if [[ -n "$EXTERNAL_COPY" ]]; then
    EXTERNAL_OK=false
    if rsync -a --delete "$BACKUP_ROOT/dump/" "$EXTERNAL_COPY/dump/" && rsync -a "$BACKUP_ROOT/attachments/" "$EXTERNAL_COPY/attachments/"; then
      EXTERNAL_OK=true; EXTERNAL_AT=$(date -Iseconds)
    else
      MESSAGE="外部複本失敗"
    fi
  fi
}
run || OK=false

FREE_GB=$(df -P "$BACKUP_ROOT" | awk 'NR==2 {printf "%.1f", $4/1048576}' || echo "")
# 值一律經環境變數交給 Python（路徑、訊息含引號或反斜線也不會破壞程式，審查 S9）
OK="$OK" MESSAGE="$MESSAGE" FILES="$FILES_JSON" FREE_GB="$FREE_GB" EXTERNAL_COPY="$EXTERNAL_COPY" EXTERNAL_OK="$EXTERNAL_OK" \
EXTERNAL_AT="$EXTERNAL_AT" DURATION=$(( $(date +%s) - started )) BACKUP_ROOT="$BACKUP_ROOT" STATUS_FILE="$STATUS/backup-status.json" \
python3 -c '
import json, os, datetime
e = os.environ
status = {
    "finishedAt": datetime.datetime.now().astimezone().isoformat(),
    "succeeded": e["OK"] == "true",
    "message": e["MESSAGE"] or None,
    "durationSeconds": int(e["DURATION"]),
    "files": json.loads(e["FILES"]),
    "backupRoot": e["BACKUP_ROOT"],
    "freeGb": float(e["FREE_GB"]) if e["FREE_GB"] else None,
    "externalCopyPath": e["EXTERNAL_COPY"],
    "externalCopiedAt": e["EXTERNAL_AT"] or None,
    "externalCopySucceeded": None if not e["EXTERNAL_OK"] else e["EXTERNAL_OK"] == "true",
}
path = e["STATUS_FILE"]
with open(path + ".tmp", "w") as f:
    json.dump(status, f, ensure_ascii=False)
os.chmod(path + ".tmp", 0o644)
os.replace(path + ".tmp", path)
'
echo "$(date -Iseconds) 備份$([[ $OK == true ]] && echo 完成 || echo 失敗) $MESSAGE" | tee -a "$BACKUP_ROOT/backup.log"
[[ $OK == true ]]
