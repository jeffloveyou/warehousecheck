---
name: warehouse-deployment
description: Windows PC 與 Linux NAS 發布、部署、線上更新與回滾規則：dotnet publish、RID（win-x64、linux-x64、linux-arm64）、Framework-dependent／Self-contained、scripts/publish.ps1、Windows Service、容器、反向代理、HTTPS 憑證、設定與秘密分離、Blue-Green A/B 切換、/health/live、/health/ready、版本識別、請求排空、PWA 更新、回滾、備份還原、NAS 資源驗證。凡處理發布、安裝、伺服器設定、版本更新時使用。Use for publish, deployment, NAS/PC hosting, blue-green update and rollback work.
---

# Windows／NAS 發布與線上更新

> 「不中斷更新」指**應用程式升版期間正常作業盡可能不中斷**，不是單機硬體高可用。反向代理、App、DB、NAS/PC 單點故障不在保證範圍內。

## 先讀文件（按需）
- `docs/10-低負載與跨平台部署.md`：§5 Windows、§6 NAS、§7 發布與組態、§8 上線前條件。
- `docs/11-線上更新與回滾.md`：全文（更新任務必讀）。
- `docs/adr/0002-效能與部署策略.md`：已提案的決策。
- Schema 變更交給 `warehouse-db-migration`。

## 硬性規則
1. **開發機**（含 WSL）的發布、服務安裝與啟停、容器操作由主對話執行，並在 `docs/07` 記錄指令、實際結果與學習重點；**正式環境**部署、正式資料庫 Migration 必須先經使用者核准（AGENTS.md 第一節）。子角色不執行任何指令。
2. 不得宣稱所有 NAS 皆可執行；NAS 品牌、型號、CPU 架構、RAM、OS、容器能力須實機確認。M0 前 Linux x64 PoC 只證明 Linux 相容性。
3. 核心業務不得依賴 Windows-only API、寫死路徑或主機名稱；路徑用設定與 `Path.Combine`，注意 Linux 大小寫敏感。
4. 設定、憑證、秘密、資料庫檔案、上傳附件**與發布目錄分離**；秘密不提交 Git。
5. 發布產物不提交 Git（`publish/`、`artifacts/` 列入 `.gitignore`）。
6. 未驗證單一 NAS/PC 能同時跑 A/B 兩個實例前，不得宣稱零停機；資源不足要回報限制、備案與維護窗口。
7. 未經核准不得開通付費服務或依賴特定雲端。

## 發布矩陣
| RID | 用途 | 狀態 |
|---|---|---|
| `win-x64` | Windows PC 服務 | 必評估 |
| `linux-x64` | x86-64 NAS／Linux PC／容器 | 必評估 |
| `linux-arm64` | ARM64 NAS | 目標 NAS 為 ARM 時驗證 |

每個 RID 都需分別測試 Framework-dependent 與 Self-contained。

`scripts/publish.ps1` 預定參數：`-RuntimeIdentifier`、`-SelfContained`、`-Configuration`、`-OutputRoot`、版本號；輸出到不受 Git 追蹤的目錄，並寫入版本資訊（Git commit、建置時間）。

## Blue-Green 更新流程（A → B）
1. 從已審查 commit 建立發布包，**不覆蓋 A 的目錄**。
2. 確認 schema 差異、相容窗口、回滾限制；完成並驗證備份。
3. 若有 Migration：獨立、經核准地套用 Expand 類變更。
4. 在獨立路徑／埠／服務名稱／容器啟動 B，連同一個相容 DB。
5. `/health/live`、`/health/ready`＋冒煙測試（不得任意變動真實庫存）。
6. 反向代理把**新請求**切到 B；記錄切換時間與版本。
7. 排空 A 的進行中請求（SSE／WebSocket／Blazor Server circuit 需另行處理重連）。
8. 停止 A，保留其發布檔供回滾。
9. 安全時機提示客戶端更新；相容期結束後才安排 Contract。

## 健康檢查與可觀測性
- `GET /health/live`：只確認行程存活，不做昂貴依賴探測。
- `GET /health/ready`：啟動完成、DB 可連線（設超時）。
- 受控的版本資訊端點或回應標頭（不洩漏機密）。
- 結構化日誌含 RequestId、部署版本；不記錄連線密碼、帳密或個資。

## PWA／前端更新
- 靜態資源使用含版本的檔名或資源清單。
- **不得**強制 `skipWaiting`／自動 reload 清掉使用者未送出的表單或連續掃碼狀態；完成作業後再提示更新。
- 舊頁面與新 API 在相容窗口內必須可共同工作。

## 回滾
1. 確認 B 異常，判斷 A 與目前 schema／資料格式是否相容。
2. 反向代理切回 A（必要時先啟動 A 並 readiness）。
3. 保留 B 的日誌；核對交易筆數、冪等鍵、結存無遺失或重複。
4. 已執行不可逆 Migration 或新資料格式 → 不可盲目回滾，啟動事先核准的資料修復計畫。
5. 記錄原因、執行者、時間、commit、DB 版本、預防措施。

## 必要測試（`docs/05` T-UPDATE-01~09）
B ready 失敗不切流量；切換後錯誤可切回 A；切換期間併發出庫與同鍵重送無重複／負庫存；未提交表單不被清除；A/B 共用 schema 正確；NAS/PC 可容納兩個實例（或明載限制）；可查詢目前版本與部署日誌。備份必須實際還原測試過。

## 交付與文件
- `docs/04-開發手冊.md` §10（Windows）、§11（NAS）、§12（線上更新）：實測指令、預期輸出、錯誤與回復方法。
- `docs/10`、`docs/11` 對應段落；選型（反向代理、服務方式、容器）以 ADR 記錄。

## 常見陷阱
- `dotnet publish` 直接輸出到正在運行的目錄。
- `appsettings.Production.json` 內含密碼並被提交。
- 用 `C:\` 或 `\\` 寫死路徑，Linux 上失敗。
- App 啟動時自動 `Database.Migrate()` 套到正式 DB。
- 代理層對 POST 自動重試造成重複扣帳。
