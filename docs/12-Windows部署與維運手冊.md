# Windows 部署與維運手冊 v1.0

> 適用：V1 正式伺服器（Windows 11、PostgreSQL 17、網站、代理同機，ADR-0022）。2026-10-27 依實際環境撰寫。
> 設計理由見 ADR-0008、0010、0021、0022；程式位置見 `docs/04` §12、§24；Linux／NAS 版本見 `docs/13`。
> **所有需要密碼的步驟都由你在自己的 PowerShell 視窗輸入，不要貼到任何對話或文件裡。**

## 1. 環境一覽
| 項目 | 值 |
|---|---|
| 使用者網址 | `https://192.168.0.86:5443`（D-27：V1 用 IP） |
| 主機 | `DESKTOP-FMD51FK`，Windows 11 |
| 服務 | `WarehouseProxy`（埠 5443）、`WarehouseA`（5080）、`WarehouseB`（5081，只聽 localhost）、`postgresql-x64-17`（5432） |
| 資料庫 | `warehouse_prod`。網站帳號 `warehouse_prod_app`，擁有者 `warehouse_prod_owner`，備份帳號 `warehouse_backup`（ADR-0021） |
| 程式與設定 | `C:\Warehouse\`：<br>・`releases\` 各版本<br>・`config\` 網站設定<br>・`proxy-config\` 代理設定與 `upstream.json`<br>・`certs\` 伺服器憑證<br>・`data\keys` A/B 共用金鑰<br>・`data\attachments` 附件<br>・`secrets\` 連線字串、pgpass<br>・`logs\`、`status\` |
| 備份 | `C:\WarehouseBackup\`：<br>・`wal\`、`base\`<br>・`dump\daily`、`dump\monthly`<br>・`attachments\`<br>・`pre-migrate\`<br>・`backup.log`、`backup-settings.json` |
| PostgreSQL 工具 | `C:\Program Files\PostgreSQL\17\bin\`（`psql.exe`、`pg_dump.exe`、`pg_ctl.exe`）。不在 PATH 裡，本文寫 `psql` 的地方請用完整路徑，例如 `& 'C:\Program Files\PostgreSQL\17\bin\psql.exe' -h localhost -U postgres -d postgres` |
| 伺服器憑證 | 由內部根 CA「Warehouse Dev Root CA」簽發，**2027-11-22 到期**，見 §8.4 |
| 權限 | `C:\Warehouse`、`C:\WarehouseBackup` 只有 Administrators、SYSTEM 可讀，執行腳本會跳 UAC |

> 腳本都在專案目錄 `C:\CodeProject\物料管理系統` 執行。需要系統管理員權限的腳本會自己跳出 UAC，輸出在結束後顯示。

## 2. 每天、每週、每月要看的東西
| 頻率 | 動作 | 正常狀態 |
|---|---|---|
| 每天上班 | 用系統管理員帳號開 `/Admin/System`（選單「系統狀態」） | 上方顯示「目前沒有需要處理的項目」。V1 在設定外部複本前，會一直有「外部複本未設定」警告 |
| 每週 | 事件檢視器 → Windows 記錄 → 應用程式，篩選來源 `Warehouse` | 沒有 1001（異常），或每個 1001 後面都有 1002（恢復） |
| 每週 | `Get-Content C:\WarehouseBackup\backup.log -Tail 7` | 每天一行「備份完成（N 秒）」；目前約 8～15 秒 |
| 每月 | 檢查 `C:` 與備份磁碟的可用空間 | 至少 10 GB（低於這個值時健康檢查會警告） |
| 每季 | 還原演練（§5.4），依 D-13 | 筆數比對全部一致 |

## 3. 換版（更新程式）
### 3.1 沒有 Migration 的版本
```powershell
cd C:\CodeProject\物料管理系統
git switch main; git pull
$env:WAREHOUSE_TEST_DB = [Environment]::GetEnvironmentVariable('WAREHOUSE_TEST_DB','User')
.\scripts\publish.ps1 -Runtime win-x64          # 會跑全部測試，並檢查工作目錄是乾淨的
.\scripts\windows\switch-release.ps1 -ReleasePath artifacts\publish\0.1.0-<commit>-win-x64-fdd
```
- `switch-release.ps1` 的流程：
  1. 把新版裝到待命槽位。
  2. ready 檢查＋冒煙測試。
  3. 切換 `upstream.json`，並經代理確認新版已接手。
  4. 排空 10 秒後停止舊槽位，舊槽位保留供回滾。
- 正常結尾：`完成：目前槽位 X，版本 0.1.0+<commit>`。
- 確認：
  - `Invoke-RestMethod https://localhost:5443/api/system/info` 的版本是新的。
  - 系統狀態頁正常。

### 3.2 有 Migration 的版本（先 Migration，再換版）
```powershell
# 1. 看有沒有待套用的 Migration：publish 產生的 migrations.sql 會列在發布資料夾
.\scripts\windows\migrate-db.ps1 -ReleasePath artifacts\publish\0.1.0-<commit>-win-x64-fdd -Database warehouse_prod
#    → 列出待套用的項目 → 自動備份到 C:\WarehouseBackup\pre-migrate\ → 輸入 warehouse_prod_owner 密碼 → 輸入 YES
# 2. 換版
.\scripts\windows\switch-release.ps1 -ReleasePath artifacts\publish\0.1.0-<commit>-win-x64-fdd
```
- Migration 都是 Expand（只新增），舊版可以在新結構上執行。所以**一定要先 Migration、再換版**。
- 「等鎖超過 5 秒就放棄」：表示有長交易擋住，稍後再跑一次即可。腳本可以重複執行。
- 各版本的相容限制看 `docs/11` §7。

### 3.3 回滾
```powershell
.\scripts\windows\switch-release.ps1 -Rollback
```
- 切回待命槽位的版本。只能回到曾經成功上線的版本（`C:\Warehouse\deploy-state.json` 有記錄）。
- 資料庫不會回退。新增的表留著，舊版不會讀它們（`docs/11` §10「資料庫 Migration 的回滾」）。
- 「待命槽位沒有安裝服務」或待命槽位是壞版本時，改用 `-ReleasePath <要的版本>` 重新部署。

### 3.4 換版時機
- 換版時不需要停工（A/B 切換）。即使如此，**最好選在沒有人正在送出單據的時候**。
- 有 Migration 時，選下班後執行。
- `docs/11` §7 列出的版本，在舊槽位停止前有特定操作要避開（例如 M6 不能設定倉庫範圍）。

## 4. 備份
### 4.1 排程（`install-backup-tasks.ps1` 已安裝，以 SYSTEM 執行）
| 工作排程器名稱 | 時間 | 內容 |
|---|---|---|
| Warehouse 每日備份 | 02:00 | 基底備份（保留 7 份）、`pg_dump`（每日 30 份；每月第一份保留 7 年）、附件、外部複本 |
| Warehouse 附件備份 | 每 15 分鐘 | `data\attachments` → `C:\WarehouseBackup\attachments`（只增不刪） |
| Warehouse 健康檢查 | 每 5 分鐘 | ready、備份是否太舊、磁碟空間、WAL 封存，異常時寫事件記錄 |

- WAL 封存每 15 分鐘至少一個檔，加上附件備份，構成 15 分鐘 RPO。
- 手動補做一次每日備份（系統管理員 PowerShell）：
  ```powershell
  Start-ScheduledTask -TaskName 'Warehouse 每日備份'
  ```
- 修改腳本後要重跑 `install-backup-tasks.ps1`。排程執行的是複製到 `C:\Warehouse\scripts\` 的副本，所以要重新複製。

### 4.2 外部複本（**V1 目前未設定**）
- `C:\WarehouseBackup\backup-settings.json` 的 `externalCopyPath`：
  - JSON 裡的反斜線要寫兩個，例如 `"E:\\WarehouseBackup"`。
  - 也可以改用 `/`。
- 外接硬碟：直接填磁碟路徑。
- NAS：需要帳密，等 M8-04 的 `set-backup-target.ps1` 合併後再用（ADR-0022，目前暫緩）。
- 沒設定時，這台電腦或硬碟故障，備份會一起損失。**這是 V1 目前最大的風險。**

## 5. 還原
> 原則：**一律還原到新的位置**，確認無誤後才切換。不要直接覆蓋 `warehouse_prod`。

### 5.1 誤刪、誤改，只要查回某筆資料
1. 還原到新資料庫。需要 postgres 密碼。
   ```powershell
   $dump = (Get-ChildItem C:\WarehouseBackup\dump\daily\warehouse_prod-*.dump | Sort-Object Name | Select-Object -Last 1).FullName
   .\scripts\windows\restore.ps1 -Dump $dump -TargetDatabase warehouse_restore
   ```
2. 用 `psql -h localhost -U postgres -d warehouse_restore` 查詢。
3. 查完後刪掉：`DROP DATABASE warehouse_restore;`。

### 5.2 還原到某個時間點（PITR，例如誤操作發生在 14:30 之前）
```powershell
.\scripts\windows\restore.ps1 -Pitr -TargetTime '2026-10-24 14:25'
```
- 會在 `localhost:5499` 啟動暫時執行個體，資料目錄在 `C:\WarehouseRestore\pitr-<時間>`。
- 用 `psql -p 5499` 檢查。要把資料拿出來：
  1. 先 `pg_dump -p 5499 ... -Fc -f x.dump`。
  2. 再用 §5.1 還原成新資料庫。
- 指定的時間之後必須還有交易，PostgreSQL 才知道要停在哪裡。系統沒人使用時，改用不指定時間（還原到最新）。
- 結束時停止暫時執行個體：`pg_ctl stop -D <資料目錄>`。

### 5.3 硬碟或資料庫壞了，整個換過去（還原後切換）
1. 先準備新庫名（例如 `warehouse_prod2`）的帳號：
   - 執行 `init-prod-db.ps1 -Database warehouse_prod2`，建立 `warehouse_prod2_owner`、`_app` 與連線字串。這個腳本也會建立一個空庫。
   - 用 postgres 登入 psql，執行 `DROP DATABASE warehouse_prod2;`。這是因為 `restore.ps1` 只還原到不存在的資料庫。
2. 取得要還原的 dump：
   - 想還原到最新時，先執行 `restore.ps1 -Pitr`（不指定時間），再用 `pg_dump -p 5499 -d warehouse_prod -Fc -f <檔案>` 取出。
   - 也可以直接用最近一份每日 dump。
3. 還原：`restore.ps1 -Dump <檔案> -TargetDatabase warehouse_prod2`。
   - 因為擁有者帳號已經存在，資料表會歸 `warehouse_prod2_owner` 所有，並授權給 `_app`。
4. 再執行一次 `init-prod-db.ps1 -Database warehouse_prod2`。
   - 這一步會補上預設權限，之後 Migration 新增的表才會自動授權。
   - 庫已存在，所以不會重建。
5. 讓網站改連新庫：`set-connection.ps1 -Database warehouse_prod2`。
   - 執行時不能有人在作業。
   - 會作廢所有登入，大家要重新登入。
   - 原本的 `data\attachments` 會被改名保存，並建立一個空目錄。
6. 還原附件：
   - 執行 `restore.ps1 -Attachments -Destination C:\Warehouse\data\attachments`。
   - 也可以把輸出目錄的內容複製進 `data\attachments`。
7. 確認系統狀態頁、抽查幾張單據與結存。
8. 修改 `backup-settings.json` 的 `databases`，改成新庫名，再重跑 `install-backup-tasks.ps1`。

### 5.4 每季還原演練
```powershell
.\scripts\windows\restore.ps1 -Dump $dump -TargetDatabase warehouse_restore_check -CompareWith warehouse_prod
```
- 結果應為「比對 N 張資料表，共 M 筆，全部一致」。
- 做完後刪除：
  ```sql
  DROP DATABASE warehouse_restore_check WITH (FORCE);
  ```
  用 postgres 登入 psql 執行。
- 2026-10-24 的實測：39 張表、286,059 筆全部一致，10.6 秒；PITR 12.4 秒。

## 6. 故障排查
### 6.1 先看哪裡
| 順序 | 位置 | 看什麼 |
|---|---|---|
| 1 | `/Admin/System` | 版本、資料庫連線、WAL 封存、備份 |
| 2 | 事件檢視器 → 應用程式 → 來源 `Warehouse`、`WarehouseA`、`WarehouseB`、`WarehouseProxy` | 服務啟動失敗、健康檢查異常 |
| 3 | `C:\Warehouse\logs\Warehouse<A或B>-yyyyMMdd.log` | 一行一筆 JSON，包含時間、等級、訊息、`requestId`、`path`、`version`。保留 30 天 |
| 4 | `C:\Warehouse\logs\deploy.log` | 每次換版、回滾的紀錄 |
| 5 | `C:\Warehouse\status\health.log`、`backup-status.json` | 健康檢查與備份結果 |
| 6 | 網站「操作日誌」`/Admin/Audit` | 誰在什麼時間做了什麼。永久紀錄，保留 7 年 |

找某個錯誤請求的完整經過：
```powershell
Select-String -Path C:\Warehouse\logs\WarehouseB-*.log -Pattern '<requestId>' | Select-Object -Last 20
```

### 6.2 常見狀況
| 現象 | 原因 | 處理 |
|---|---|---|
| 所有人都連不上（瀏覽器顯示無法連線） | `WarehouseProxy` 停止，或電腦 IP 變了 | `Get-Service WarehouseProxy`，必要時 `Start-Service WarehouseProxy`；`ipconfig` 確認 IP 還是 192.168.0.86 |
| 502 Bad Gateway | 目前槽位的服務停止 | `Get-Service WarehouseA, WarehouseB`，啟動目前槽位（`upstream.json` 的 `ActiveSlot`）或 `-Rollback` |
| ready 失敗或頁面 500，狀態頁顯示資料庫未連線 | PostgreSQL 停止或磁碟滿 | `Get-Service postgresql-x64-17`；檢查磁碟空間 |
| 手機顯示「不安全的連線」、相機不能用 | 手機沒有信任根 CA，或憑證過期 | 重新安裝根 CA（`docs/04` §6.1）；憑證過期見 §8.4 |
| 狀態頁「備份過舊」 | 02:00 時電腦關機或睡眠，或備份失敗 | 看 `backup.log` 最後幾行；手動執行 §4.1；設定電源選項，讓電腦不睡眠 |
| 「WAL 封存失敗」 | `wal` 目錄權限或磁碟滿 | 事件記錄的錯誤訊息；`icacls C:\WarehouseBackup\wal` 要包含 `NETWORK SERVICE` |
| 表單送出 400（防偽權杖無效） | 金鑰目錄被更動，或換了資料庫（`set-connection` 會作廢登入） | 重新整理頁面並重新登入 |
| 單據確認時「請稍後再試」 | 同一個儲位或物料剛好有其他人在過帳，或偵測到死結後自動放棄 | 再按一次。冪等鍵保證不會重複扣帳 |
| 換版中止「新版未就緒」 | 新版啟動失敗，例如設定檔、Migration 沒套用 | 流量沒有切換，系統照常運作。查事件記錄，修正後重跑 |

### 6.3 不要做的事
- 不要手動編輯 `upstream.json`，一律用 `switch-release.ps1`。
- 不要用 `Stop-Process -Name Warehouse.Web`。這會同時殺掉 A、B 兩個服務，要用 `Stop-Service`。
- 不要用 `warehouse_prod_owner` 或 `postgres` 直接修改庫存資料表。庫存只能經由單據過帳（AGENTS.md 第六節）。
- 不要對 `warehouse_prod` 執行 `check-nonowner.ps1`，也不要在正式庫跑演練。腳本本身也會拒絕。

## 7. 帳號
- 新增帳號、改角色、停用、重設密碼：網站「帳號」`/Admin/Users`（系統管理員）。
  - 停用帳號或改角色後，就算對方勾了「記住我」，最慢 5 分鐘後也會失效。
- 系統管理員全部遺失時，在伺服器建立一個新的：
  ```powershell
  .\scripts\windows\create-admin.ps1 -Database warehouse_prod -User <帳號> -DisplayName <名稱>
  ```
  初始密碼只顯示一次，第一次登入時必須更改。
- 「記住我」的天數：系統設定 → 登入。預設 14 天，設 0 表示關閉。

## 8. 一次性或少見的維護
### 8.1 附件孤兒檔清理
```powershell
.\scripts\windows\clean-attachments.ps1          # 只檢查
.\scripts\windows\clean-attachments.ps1 -Apply   # 搬到 <附件目錄>-orphans\<時間>\，觀察一段時間沒問題後再自行刪除
```
### 8.2 資料保留
- 網站每天自動清理一次（ADR-0021）。
- 保留期限在「系統設定 → 資料保留」調整，不能低於下限。
- 庫存單據、異動、結存永久保留。

### 8.3 PostgreSQL 超級使用者密碼
- 只有建庫、開 WAL 封存、還原時才會用到，排程與網站都不用。
- 更換方法：`psql -h localhost -U postgres -d postgres` 登入後執行 `\password postgres`。

### 8.4 伺服器憑證到期（2027-11-22）或 IP 改變
> 效期維持 397 天（瀏覽器對伺服器憑證的上限，使用者 2026-10-27 決定），大約每年換一次。建議在行事曆設提醒：到期前一個月更換。

1. 用建立根 CA 的同一個 Windows 使用者（jeff）開 PowerShell 執行：
   ```powershell
   .\scripts\new-dev-certs.ps1 -IpAddress 192.168.0.86
   ```
   - 根 CA 沿用，只重新簽發伺服器憑證。
   - 手機不必重新安裝根 CA。
2. 把新的 pfx 密碼寫進暫存檔，執行：
   ```powershell
   .\scripts\windows\install-proxy.ps1 -ReleasePath <代理發布資料夾> -PfxPath <新 pfx> -PfxPasswordFile <暫存檔>
   ```
   - 代理會重新啟動，約有幾秒連不上。
3. 用瀏覽器確認憑證的到期日。
- 建議在到期前一個月處理。

### 8.5 重新安裝 PostgreSQL 之前
- 先把 `C:\WarehouseBackup\wal` 改名保存，否則新叢集的 WAL 檔名會和舊的衝突（ADR-0021）。

## 9. 第一次建置正式環境（紀錄，日後重建用）
2026-10-24 實際走過的順序，詳細結果見 `docs/07`：
1. `init-prod-db.ps1 -Database warehouse_prod`：輸入 postgres 密碼與自訂的擁有者密碼。
2. `migrate-db.ps1 -ReleasePath <版本> -Database warehouse_prod`：輸入擁有者密碼與 YES。
3. `enable-wal-archive.ps1`：會重新啟動 PostgreSQL。
4. `install-backup-tasks.ps1 -Databases warehouse_prod`。
5. `set-connection.ps1 -Database warehouse_prod`：網站改連正式庫，並以 A/B 切換套用。
6. `create-admin.ps1 -Database warehouse_prod -User <帳號>`。
7. 演練用的 `warehouse_staging`：`check-nonowner.ps1`（10 項 SQL 探測與畫面冒煙）、`drill.ps1`（A/B 演練）。
- 代理與服務的第一次安裝見 `docs/04` §12.3。
