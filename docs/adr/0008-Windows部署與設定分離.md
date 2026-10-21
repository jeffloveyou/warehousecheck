# ADR-0008：Windows 部署方式與設定分離

- 狀態：已接受（Accepted），M0-10 計畫經使用者核准；實測 2026-10-21
- 相關：AGENTS.md 第四節、`docs/10` §5、§7、`docs/04` §10

## 背景
AGENTS.md 要求設定、憑證、秘密、資料與附件和發布目錄分離，發布產物不進 Git，並保留 Framework-dependent 與 Self-contained 兩種發布方式。M0-12 的 A/B 切換也需要「同一台主機上並存多個版本」。

## 決策
1. **外部設定目錄**：環境變數 `WAREHOUSE_CONFIG_DIR` 指向發布目錄以外的資料夾，程式載入其中的 `appsettings.json` 與 `appsettings.{環境}.json`，順序在環境變數之前（`src/Warehouse.Web/ExternalConfiguration.cs`）。Windows 與 Linux 共用同一機制。
2. **版本目錄不覆寫**：`<Root>\releases\<版本>-<commit>-<RID>-<fdd|sc>\`；回滾與 A/B 都是「讓服務指向另一個版本目錄」。
3. **Windows 服務**：`Microsoft.Extensions.Hosting.WindowsServices`（MIT）＋`AddWindowsService()`；服務帳號使用虛擬帳號 `NT SERVICE\<服務名>`；延遲自動啟動；失敗自動重啟；服務專屬環境變數寫在服務的登錄機碼。
4. **預設 Framework-dependent**：在 Windows PC 安裝 ASP.NET Core Runtime 10，發布 7.7 MB／37 檔；self-contained 為 112.9 MB／367 檔。Runtime 安全性更新由 Windows Update／安裝程式處理。Self-contained 保留給無法安裝 Runtime 的主機（例如部分 NAS）。
5. **權限**：`config` 只給 Administrators、SYSTEM、服務帳號讀；`data`、`logs` 服務帳號可修改；`releases` 服務帳號可讀與執行。

## 實測（M0-10）
- 安裝後 `/health/ready` 200；`/api/system/info` 版本 `0.1.0+e2a5f49…` 與 `release.json` 一致；Production 環境 `/dev/*` 回 404。
- `Restart-Service` 後 1.2 秒恢復就緒；服務程序記憶體約 94 MB。
- config 檔 ACL 只有 `NT SERVICE\WarehouseA (R)`、SYSTEM、Administrators。

## 後果
- 部署需系統管理員權限（安裝、重啟、移除）。
- 兩個實例（A/B）須使用不同服務名與埠，共用同一個 `config` 目錄與資料庫。
- Data Protection 金鑰需在 M6 前改為共用目錄，否則 A/B 切換時 Cookie 會失效。
- 連線字串以檔案保存，依賴 NTFS 權限保護；若需更高等級（如 DPAPI 加密）於 M7 再評估。
