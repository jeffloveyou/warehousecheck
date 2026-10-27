# Linux 與 NAS 部署手冊 v1.0

> **狀態：這份手冊只在 WSL2 Ubuntu 24.04 上驗證過（M0-11、M0-12），還沒有在實體 NAS 上驗證。**
> - 使用者的 QNAP 型號、CPU、RAM、QTS 版本、是否有 Container Station 都還沒確認（D-17～19）。
> - V1 正式伺服器是 Windows（ADR-0022）。NAS 的第一階段只當外部備份目的地，目前暫緩。
> - 依 AGENTS.md 第四節，不宣稱任何 NAS 型號一定能執行。
>
> 程式與腳本細節見 `docs/04` §11、§12.7；備份與還原規則與 Windows 相同（ADR-0021、`docs/12`）。

## 1. 先確認 NAS 條件（評估清單）
| 項目 | 怎麼查 | 需要的條件 |
|---|---|---|
| CPU 架構 | `uname -m` | `x86_64` 用 `linux-x64`；`aarch64` 用 `linux-arm64`。arm64 只用 QEMU 模擬過（M0-11），要實機確認縮圖用的 SkiaSharp 能不能執行 |
| RAM | `free -m` | 平時「目前槽位＋代理＋PostgreSQL」約 0.5 GB；換版時 A、B 同時執行約多 0.15 GB（M0-13）。建議可用記憶體 ≥ 2 GB |
| 執行方式 | 能不能裝 .NET 10 Runtime；或有沒有 Container Station／Docker | 原生 systemd 最省記憶體（約 47 MB）；容器約 50 MiB |
| systemd | `systemctl --version` | 原生安裝腳本需要 systemd。QTS 一般沒有 systemd，可能只能用容器 |
| GNU 工具 | `head --version`、`date --version`、`rsync`、`python3` | `backup.sh` 需要 GNU coreutils、rsync、python3；BusyBox 型 NAS 要另外處理 |
| PostgreSQL | NAS 套件或容器 | 傾向不放在 NAS（D-18）。要放的話，資料目錄要放在持久磁碟並納入備份 |

> **建議順序**（ADR-0009）：
> 1. NAS 能裝 .NET Runtime 並有 systemd：用原生服務。
> 2. 能跑容器：用容器。
> 3. 都不行：NAS 只當備份與附件的儲存空間，程式留在 PC。

## 2. 原生安裝（systemd）
```bash
# 在開發機發布（PowerShell），把資料夾複製到 NAS
.\scripts\publish.ps1 -Runtime linux-x64            # 或 linux-arm64
.\scripts\publish.ps1 -Project Proxy -Runtime linux-x64

# NAS 上（root）
sudo ./scripts/linux/init-prod-db.sh --database warehouse_prod     # 帳號分離，密碼寫入 /etc/warehouse/secrets
./scripts/linux/migrate-db.sh --release <主程式資料夾> --database warehouse_prod   # 輸入 owner 密碼、YES
sudo ./scripts/linux/install-service.sh --release <主程式資料夾> --name warehouse-a --port 5080 \
     --connection-string-file /etc/warehouse/secrets/warehouse_prod.connection.txt
sudo ./scripts/linux/install-service.sh --release <代理資料夾> --name warehouse-proxy --port 5480 \
     --config-dir /etc/warehouse-proxy --upstream-port 5080
```
- 目錄：
  - `/opt/warehouse/releases/<版本>`：發布檔。
  - `/etc/warehouse`：設定，權限 0640 root:warehouse。
  - `/var/lib/warehouse/{data,keys}`：資料與金鑰。
  - `/etc/warehouse-proxy`：代理設定，由獨立使用者 `warehouse-proxy` 執行，讀不到連線字串。
- `--connection-string-file` 讀取後會刪除這個檔案。如果還要保留，先複製一份再給腳本。
- **HTTPS**：Linux 簡測的代理只聽 HTTP。實機要在 `/etc/warehouse-proxy/appsettings.Production.json` 設定 `Kestrel:Endpoints` 與憑證，並在實機上驗證。
- 代理與網站如果不在同一台主機或同一個容器網路，要設定 `ForwardedHeaders` 的 `KnownProxies`，否則登入限流會把所有人算成同一個 IP（`docs/10` §7）。

## 3. 換版與回滾
```bash
./scripts/linux/migrate-db.sh --release <新版>                       # 有 Migration 時先做
sudo ./scripts/linux/switch-release.sh --release <新版> --proxy-url http://localhost:5480
sudo ./scripts/linux/switch-release.sh --rollback --proxy-url http://localhost:5480
```
- 槽位：A 是 `warehouse-a`，B 是 `warehouse-b`。紀錄在 `/var/log/warehouse-deploy.log`。
- 同時只能執行一個切換（`flock`）。

## 4. 容器
- 映像直接使用 `publish.ps1` 的輸出：`deploy/docker/Dockerfile`。檔頭有 build／run 範例，見 `docs/04` §11.3。
- `/config` 唯讀掛載設定，`/data/attachments` 掛持久目錄，這個目錄要讓 UID 1654 可寫，並納入備份。
- QNAP Container Station 上的實際設定（網路模式、volume、開機自動啟動），等取得設備後再補。

## 5. 備份與還原
```bash
sudo cp scripts/linux/systemd/warehouse-backup.* /etc/systemd/system/
sudo systemctl enable --now warehouse-backup.timer           # 每天 02:00；Persistent=true，關機錯過的會補做
sudo ./scripts/linux/restore.sh --dump <檔案> --target warehouse_restore --compare-with warehouse_prod
```
- 設定檔是 `/etc/warehouse/backup.conf`：
  - `DATABASES="warehouse_prod"`
  - `EXTERNAL_COPY=""`
  - `KEEP_DAILY=30`
  - `KEEP_MONTHLY_YEARS=7`
- `backup.sh` 只做每日邏輯備份（`pg_dump`）和附件。**時間點還原（WAL 封存＋pg_basebackup）要依 NAS 上 PostgreSQL 的安裝方式另外設定**，在實機上設定完成之前，RPO 是 24 小時，不是 15 分鐘。
- 狀態檔在 `/var/lib/warehouse/status/backup-status.json`。
  - Windows 的安裝腳本會自動設定 `Backup__StatusDirectory`，`install-service.sh` 還沒有。
  - 所以 Linux 要在 `/etc/warehouse/appsettings.Production.json` 加上 `"Backup": { "StatusDirectory": "/var/lib/warehouse/status" }`，系統狀態頁才看得到備份結果（未實機驗證）。

## 6. 第一階段：QNAP 當外部備份目的地（暫緩中）
使用者 2026-10-26 指示「NAS 的部分先跳過」。恢復時的步驟：
1. **使用者在 QNAP 操作**：
   - 建立共用資料夾 `WarehouseBackup`。
   - 建立專用帳號 `wh-backup`，只給這個資料夾讀寫權限。
   - 回報 NAS 的 IP、型號、CPU、RAM、QTS 版本、有沒有 Container Station。
2. Claude 合併分支 `feature/M8-04-nas-external-backup`：
   - 內含 `set-backup-target.ps1`。
   - 帳密存在 `C:\Warehouse\secrets\nas-backup.txt`，只有系統管理員可讀。
   - 備份時用 `New-PSDrive -Credential` 連線。
3. 在正式伺服器上執行：
   ```powershell
   .\scripts\windows\set-backup-target.ps1 -Path \\<NAS IP>\WarehouseBackup -User wh-backup
   ```
   - 密碼由你自己輸入兩次。
   - 腳本會立刻跑一次備份並驗證。
4. 系統狀態頁的「外部複本未設定」警告消失。
