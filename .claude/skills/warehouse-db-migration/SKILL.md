---
name: warehouse-db-migration
description: 資料庫結構更新與版本相容規則：EF Core Migration、PostgreSQL/Npgsql schema 變更、新增或修改資料表／欄位／索引／唯一約束、欄位改名或刪除、資料回填、Expand → Migrate → Contract、Blue-Green A/B 相容窗口、正式資料庫 Migration 審核、備份與回滾限制。凡會產生或修改 Migration、DbContext 模型設定時使用。Use for any EF Core migration or schema change.
---

# 資料庫結構更新與相容性

## 先讀文件（按需）
- `docs/02-架構與資料庫.md`：核心資料表、「重要一致性設計」、「藍綠部署與資料庫相容」。
- `docs/11-線上更新與回滾.md`：§3 發布流程、§4 API 與資料庫相容策略、§7 回滾限制。

## 硬性規則
1. **開發／測試資料庫**（`warehouse_dev`、`warehouse_test`）的 `dotnet ef migrations add`、`database update`、產生 SQL script 由主對話執行，並在 `docs/07` 留歷程（AGENTS.md 第一節）；**正式資料庫**一律見第 2 條。子角色不執行。
2. **正式資料庫 Migration 獨立執行並經使用者審核**；新版 App 啟動時**不得**自動套用（不在 `Program.cs` 呼叫 `Database.Migrate()` 對正式環境）。
3. 執行前先完成並驗證備份。
4. 部署及回滾過程不允許直接清除資料。
5. 已有交易資料的物料不可透過 Migration 變更基本單位或追蹤模式。
6. `StockMovements` 為不可任意刪改的帳；Migration 不得重寫或刪除歷史異動。

## Expand → Migrate → Contract
| 階段 | 內容 | A（舊版）可用？ |
|---|---|---|
| Expand | 只新增：新表、可為 NULL 或有預設值的新欄位、新索引（大表考慮 `CREATE INDEX CONCURRENTLY`） | 是 |
| Migrate | 部署新版 B；雙寫或背景回填資料；確認新舊資料一致 | 是（相容窗口） |
| Contract | 相容窗口結束、A 停止且確認可不回滾後，才刪除舊欄位／舊表、加上 NOT NULL | 否 → 需另行核准 |

範例：欄位改名 `Name` → `DisplayName`
1. Expand：新增 `DisplayName`（nullable）。
2. Migrate：B 同時寫 `Name` 與 `DisplayName`，回填舊資料。
3. Contract（另一個版本）：移除 `Name`，`DisplayName` 改 NOT NULL。

## 設計檢查清單
- [ ] 這個 Migration 套用後，**正在運行的舊版 A 仍能正常讀寫**嗎？不能 → 拆分或需維護窗口。
- [ ] 有無破壞性操作（DROP、RENAME、型別縮小、加 NOT NULL 無預設值）？有 → 移到 Contract 階段。
- [ ] 唯一約束：`ItemCode`、`ExactIdentifierRegistry`、冪等鍵 `ProcessedCommands.IdempotencyKey`、有效別名 `NormalizedText`（部分索引 `WHERE IsActive`）。
- [ ] NULL 維度唯一性（如 `StockBalances` 的 `LotId?`、`SerialId?`）：使用 `NULLS NOT DISTINCT` 或部分索引，明確寫在 Migration。
- [ ] 數量欄位 `decimal`（`numeric(p,s)`）精度足夠。
- [ ] 大表索引／回填的鎖與耗時評估；必要時分批。
- [ ] `Down()` 方法是否安全？若不可逆，明確說明並提供資料修復計畫，不宣稱可無條件回滾。
- [ ] 產生的 Migration 檔案經人工閱讀（尤其是自動推斷的 rename／drop）。

## 提供給使用者的指令範本（實際專案路徑於 M0 後確認，未執行前標記待驗證）
```powershell
# 新增 Migration（開發環境）
dotnet ef migrations add <MigrationName> --project .\src\Warehouse.Infrastructure --startup-project .\src\Warehouse.Web

# 產生可審核的冪等 SQL（正式環境用此 SQL 經審核後執行）
dotnet ef migrations script <FromMigration> <ToMigration> --idempotent --output .\artifacts\migrations\<name>.sql --project .\src\Warehouse.Infrastructure --startup-project .\src\Warehouse.Web

# 開發資料庫套用
dotnet ef database update --project .\src\Warehouse.Infrastructure --startup-project .\src\Warehouse.Web
```
說明每個指令預期輸出與常見錯誤（如找不到 DbContext、連線字串、`dotnet-ef` 工具未安裝）。`--startup-project` 用 `Warehouse.Web`（實際啟動的主程式；`Warehouse.Api` 是類別庫）。安裝 `dotnet-ef` 工具屬於新增工具，需先經使用者核准，核准後由主對話安裝。

## 必要測試
- 對空資料庫從頭套用所有 Migration 成功。
- 對含測試資料的上一版 schema 套用，資料保留正確。
- A 版程式碼對新 schema 的讀寫冒煙測試（T-UPDATE-06）。
- 約束測試：重複料號、重複專屬別名、重複冪等鍵被 DB 拒絕。

## 交付與文件
- `docs/04-開發手冊.md`：該功能章節的「資料庫表、約束及 Migration」段落。
- 涉及部署流程時更新 `docs/11`；設計改變需 ADR。
- 回滾說明：哪個版本可回、資料是否需修復、備份位置。
