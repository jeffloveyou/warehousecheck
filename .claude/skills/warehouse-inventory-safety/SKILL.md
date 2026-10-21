---
name: warehouse-inventory-safety
description: 庫存交易正確性與資料保護規則。凡涉及入庫、出庫、移庫、期初建帳、盤點調整、預留、StockBalances、StockMovements、StockDocuments、InventoryPostingService、Idempotency-Key、ProcessedCommands、併發扣帳、負庫存、批號、序號、儲位狀態，或物料刪除／改基本單位／改追蹤模式時使用。Use for any inventory posting, stock balance, idempotency, or concurrency work.
---

# 庫存交易正確性與資料保護

> 原則：**庫存正確性優先於效能與便利**。詳細資料表與交易範例以 `docs/02-架構與資料庫.md` 為準，本 Skill 只列檢查重點。

## 先讀文件（按需）
- `docs/02-架構與資料庫.md`：「重要一致性設計」、「入庫／出庫／移庫交易範例」、「基本 API 草案」。
- `docs/01-需求規格.md`：入庫／出庫／移庫操作流程。
- `docs/05-驗收測試.md`：庫存與併發相關測試 ID。

## 不可破壞的不變條件
1. **單一入口**：只有 Application 層的 `InventoryPostingService`（或經 ADR 核准的同等業務交易服務）可以改變結存。Web、Api、Repository、背景工作、匯入腳本都不得直接 `UPDATE StockBalances`。
2. **同一交易**：`StockDocuments`／`StockDocumentLines`、`StockMovements`、`StockBalances`、`ProcessedCommands` 在同一個 PostgreSQL Transaction 提交；Commit 後才回成功。
3. **冪等**：所有庫存 POST 需 `Idempotency-Key`。
   - 同鍵＋同 payload（以 RequestHash 比對）→ 回傳前次結果，不再過帳。
   - 同鍵＋不同 payload → 拒絕（明確錯誤碼）。
   - 去重由 DB 唯一約束保證，不能只靠「先查再寫」。
4. **防超賣**：可用量檢查與扣減必須在同一交易中以條件式更新（如 `WHERE Quantity >= @qty`）或列鎖／`Version` 樂觀併發完成；「先查到有 10 件」不足以防止兩人同時出庫。
5. **預設不得負庫存**；例外須獨立 ADR 並經使用者批准。
6. **移庫數量守恆**：來源負向 Movement＋目的正向 Movement 在同一交易；跨倉在途為 V2 雙階段流程，不以單次移庫假裝已送達。
7. **Movement 不可任意刪改**：更正用反向／調整單據，保留稽核軌跡。
8. **結存維度**：`ItemId + LocationId + LotId + SerialId + StockStatus`；NULL 維度的唯一性需以適當 DB 約束（例如部分索引或 `NULLS NOT DISTINCT`）處理。
9. **序號**：同一序號不得同時在兩個儲位為在庫。
10. **數量**：使用 `decimal`；依單位 `DecimalPlaces` 限制小數位（`PCS` 通常整數）。
11. **物料主檔**：`ItemId` 永久；已有交易的物料只能停用，不可刪除、改基本單位或改追蹤模式。
12. **出庫對象必須明確**：模糊搜尋結果不得自動成為出庫物料（見 `warehouse-search-index`）。
13. **V1 不支援離線庫存交易**：離線或未收到伺服器成功回應時，UI 不可顯示為已提交。
14. **快取不可決定可用量**：最終判定一律以 DB 交易為準。

## 實作檢查清單
- [ ] 權限檢查在 Api／Application（伺服器端），不只在前端。
- [ ] 輸入驗證：單位、數量小數位、批號／序號依 `TrackingMode`、儲位 `Status` 可用。
- [ ] 冪等鍵查詢與寫入在交易內，具唯一約束。
- [ ] 交易邊界清楚（`BeginTransaction` / `SaveChanges` / `Commit`），失敗時 Rollback 並回傳可理解錯誤碼＋RequestId。
- [ ] 併發衝突（`DbUpdateConcurrencyException`、唯一約束衝突）有明確處理與錯誤碼，不吞例外。
- [ ] 代理或客戶端重試不會盲目重發非冪等寫入。
- [ ] 寫入 `AuditLogs`（誰、何時、前後值），日誌不含密碼或個資。

## 必要測試（先寫測試再實作）
- 同一 `Idempotency-Key` 重送 → 只有一筆 Movement，結存只變一次。
- 同鍵不同 payload → 拒絕。
- 兩個並行出庫合計超過可用量 → 只有一筆成功，結存不為負。
- 交易中途失敗（模擬例外） → Movement 與 Balance 都沒有寫入。
- 移庫 → 來源減、目的加，總量不變。
- 序號重複入庫至另一儲位 → 拒絕。
- 已有交易的物料嘗試刪除／改基本單位 → 拒絕。
- API 成功但客戶端逾時重試 → 不重複過帳（亦屬 T-UPDATE-04）。

> 併發測試需真實 PostgreSQL（如測試用資料庫或容器）；EF Core InMemory 無法驗證交易與鎖行為。測試由使用者執行，未取得輸出前標記「待驗證」。

## 常見陷阱
- 在 Controller 或 Razor Page 直接改 `StockBalance.Quantity`。
- 先 `SELECT` 可用量再 `UPDATE`，中間沒有鎖或條件。
- 把冪等紀錄寫在交易外，造成「過帳成功但冪等紀錄沒寫入」或相反。
- 用快取的可用量決定能否出庫。
- 以刪除 Movement 修正錯帳。
