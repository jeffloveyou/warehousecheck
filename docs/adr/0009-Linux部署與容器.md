# ADR-0009：Linux 部署方式（systemd 與容器）

- 狀態：已接受（Accepted），M0-11 計畫經使用者核准；實測 2026-10-21（WSL2 Ubuntu 24.04，非實體 NAS）
- 相關：ADR-0008、AGENTS.md 第四節、`docs/10` §6、`docs/04` §11

## 背景
AGENTS.md 要求可部署於 Windows PC 與符合條件的 Linux NAS，且不得宣稱所有 NAS 皆可執行。NAS 型號尚未決定，M0-11 先在開發機的 WSL2 Ubuntu 24.04 證明「程式與部署腳本在 Linux 上可運作」，ARM64 以 QEMU 模擬做初步確認。

## 決策
1. **沿用 ADR-0008 的設定分離**：`WAREHOUSE_CONFIG_DIR=/etc/warehouse`；不需另寫 Linux 專用的設定機制。
2. **原生部署＝systemd 服務**：`Microsoft.Extensions.Hosting.Systemd`（MIT）＋`AddSystemd()`，`Type=notify`（程式啟動完成才回報就緒）。腳本 `scripts/linux/install-service.sh`、`uninstall-service.sh`。
   - 目錄：`/opt/warehouse/releases/<版本>/`（root 擁有、唯讀）、`/etc/warehouse/`（0750 root:warehouse，設定檔 0640）、`/var/lib/warehouse/{data,keys}`（服務可寫）。
   - 服務使用者 `warehouse`（系統帳號、不能登入）；加固：`NoNewPrivileges`、`ProtectSystem=strict`、`ProtectHome`、`PrivateTmp`、只允許寫 `/var/lib/warehouse`。
3. **容器部署＝同一份發布檔**：`deploy/docker/Dockerfile` 以 `mcr.microsoft.com/dotnet/aspnet:10.0` 為基底，直接複製 `publish.ps1` 的 framework-dependent 輸出，不在容器內編譯；非 root（`$APP_UID`）執行；設定以唯讀掛載 `/config`，並以 `--group-add <warehouse gid>` 取得讀取權，不放寬檔案權限。
4. **NAS 選擇順序**：可裝 .NET Runtime 且有 systemd → 原生服務（記憶體最少）；否則有 Docker/OCI → 容器；兩者皆無 → 不支援，App 改放 PC。

## 實測（M0-11，WSL2 Ubuntu 24.04、32 執行緒、7.4 GB RAM）
| 項目 | 結果 |
|---|---|
| Linux 上 build／test | 0 警告；14/14 通過（整合測試經 mirrored 網路連 Windows PostgreSQL） |
| systemd 服務（linux-x64 FDD，7.6 MB／37 檔） | ready 200（含 DB 檢查）、`restart` 0.7 秒恢復就緒；cgroup 記憶體約 47 MB（RSS 約 127 MB，含共用函式庫） |
| Docker x64 | 啟動 1.26 秒就緒、重啟 1.28 秒；`docker stats` 約 50 MiB；映像下載大小 98.7 MB、解壓後 351 MB |
| Docker arm64（**QEMU 模擬**） | 可執行、ready 200；啟動 10.9 秒、記憶體約 210 MiB——為模擬開銷，**不代表實機效能** |

## 後果
- Linux 部署腳本需要 `bash`、`systemd`、`curl`、`bc`、`python3`（產生 JSON 設定）；部分 NAS 系統不一定具備，取得型號後需逐項確認。
- Data Protection 金鑰：Linux 服務使用者沒有家目錄，金鑰只存在記憶體，**每次重啟都會換金鑰**（Windows 虛擬帳號則存在設定檔）。目前尚無登入與表單，影響有限；M6 前依 ADR-0008 改為 `/var/lib/warehouse/keys` 並在 A/B 間共用（目錄已建立）。
- WSL2 與實體 NAS 的差異（核心、檔案系統、CPU 等級、可用記憶體）尚未驗證；ARM64 實機效能需取得設備後補測。
