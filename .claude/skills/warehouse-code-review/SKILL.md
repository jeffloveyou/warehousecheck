---
name: warehouse-code-review
description: 倉庫管理系統的程式碼、安全性與效能審查清單。使用者要求審查、檢查程式、review diff／PR／commit，或 Agent 完成實作後自我檢查時使用；涵蓋分層依賴、庫存不變條件、冪等與併發、搜尋索引規則、授權與輸入驗證、SQL 注入、日誌洩密、效能（無限制查詢、N+1）、跨平台相容、Migration 相容性、線上更新相容、文件同步。Use for code review, security review and performance review in this warehouse project.
---

# 程式碼、安全性及效能審查

## 審查範圍與方式
- **以修改差異（diff）為主**；只為確認上下文才讀相關檔案，不全面重讀專案。
- 目前專案尚非 Git repository 時，請使用者指定檔案或提供變更範圍。
- 審查只提出發現，**不自行修改**；修正需使用者批准。
- 不重複輸出未修改的程式碼。

## 嚴重度分級
| 等級 | 定義 | 範例 |
|---|---|---|
| 🔴 阻擋 | 可能造成庫存錯誤、資料遺失、安全漏洞、無法回滾 | 結存繞過交易服務、缺冪等、SQL 注入、破壞性 Migration |
| 🟠 重要 | 正確性風險或明顯效能／相容問題 | 無分頁查詢、N+1、Windows-only API、缺 DB 約束 |
| 🟡 建議 | 可維護性、命名、測試補強 | 重複邏輯、錯誤訊息不清楚 |
| ⚪ 文件 | 文件未同步 | 手冊路徑過時、日誌缺條目 |

每項發現需寫：檔案:行號、問題、具體失敗情境（什麼輸入 → 什麼錯誤結果）、建議修正。沒有具體失敗情境的推測要標明「待確認」。

## 檢查清單
### 1. 架構與分層
- [ ] 依賴方向：Domain 不引用 EF Core／ASP.NET／HTTP；Api／Web 不直接操作 DbContext 寫庫存。
- [ ] DTO 與 Entity 分離；API 不回傳整個 Entity graph。
- [ ] 改動範圍符合已批准計畫，沒有不相關修改。

### 2. 庫存正確性（詳見 `warehouse-inventory-safety`）
- [ ] 只經 `InventoryPostingService` 過帳；Movement＋Balance＋冪等紀錄同一交易。
- [ ] `Idempotency-Key` 有 DB 唯一約束；同鍵不同 payload 被拒。
- [ ] 扣減使用條件式更新／列鎖／`Version`；預設不得負庫存。
- [ ] 序號、批號、儲位狀態、單位小數位驗證。
- [ ] 沒有刪除 Movement 或刪除已有交易的 Item。

### 3. 搜尋索引（詳見 `warehouse-search-index`）
- [ ] 共用關鍵字未被當作唯一識別；模糊結果不會自動成為交易對象。
- [ ] 寫入與查詢使用同一正規化函式；精確碼保留前導零與符號。
- [ ] 專屬別名唯一性有 DB 約束。

### 4. 安全性
- [ ] 授權在伺服器端（Policy／Role），不只隱藏前端按鈕。
- [ ] 所有輸入有驗證與長度上限；搜尋有 rate limit 考量。
- [ ] 參數化查詢；`FromSqlRaw`／字串串接 SQL 需特別檢查。
- [ ] 日誌與錯誤回應不洩漏連線字串、密碼、token、個資、stack trace。
- [ ] 秘密不在原始碼或提交的設定檔中。
- [ ] 敏感 token 不存在不安全的瀏覽器儲存；Cookie 設定（HttpOnly、Secure、SameSite）合理；POST 有 CSRF 防護（依認證方案）。
- [ ] 檔案上傳：大小、類型限制，存放路徑與發布目錄分離，不可路徑穿越。
- [ ] 新套件：授權可商用、維護狀態良好、經使用者核准。

### 5. 效能（詳見 `warehouse-performance`）
- [ ] 查詢有分頁與最大 page size；過濾排序在 DB。
- [ ] 唯讀查詢 `AsNoTracking`＋投影；無 N+1；必要索引存在。
- [ ] 前端無高頻輪詢、不載入全部資料；相機程式按需載入。
- [ ] 快取未參與可用量判定。

### 6. 跨平台與部署（詳見 `warehouse-deployment`）
- [ ] 無 Windows-only API、寫死路徑、寫死主機名稱；路徑大小寫一致。
- [ ] 設定／秘密／附件與發布目錄分離。
- [ ] 時間使用 UTC 儲存（`DateTimeOffset`／`timestamptz`），顯示時再轉換。

### 7. Migration 與線上更新相容（詳見 `warehouse-db-migration`）
- [ ] Migration 為 Expand 類，舊版 A 仍可運作；破壞性變更延到 Contract。
- [ ] 未在 App 啟動時自動套用正式 Migration。
- [ ] API 變更向後相容（不刪除舊頁面仍在使用的欄位）。
- [ ] PWA 不強制 reload 未提交表單。

### 8. 測試與文件
- [ ] 有對應測試（尤其併發、冪等、約束）；併發測試使用真實 PostgreSQL。
- [ ] 測試結果有真實輸出，否則標「待驗證」。
- [ ] `docs/04`、`docs/07`、`docs/05`、ADR 已依 `warehouse-documentation` 同步。
- [ ] 錯誤有結構化錯誤碼＋RequestId，便於追查。

## 輸出格式
1. 審查範圍（檔案／diff 範圍）。
2. 發現清單，依嚴重度由高到低。
3. 沒有發現問題的區塊簡短標示「已檢查，無發現」。
4. 建議的驗證指令（開發機由主對話執行；涉及正式環境者由使用者核准）。
