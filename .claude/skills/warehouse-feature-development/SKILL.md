---
name: warehouse-feature-development
description: 倉庫管理系統的功能規劃、修改與交付流程（規劃 → 使用者批准 → 實作 → 使用者驗證 → 文件同步）。凡是新增功能、修改既有功能、修 bug、建立專案結構、處理 M0–M7 任務、填寫任務卡或功能交接卡時使用；並依任務類型指引應搭配的其他 warehouse-* Skill。Use for any feature planning, implementation, bug fix, or task delivery in this warehouse project.
---

# 功能開發與交付流程

## 適用時機
- 任何會改動程式或文件的任務：新功能、修改、修 bug、重構、M0–M7 任務清單項目。
- 使用者貼上 `templates/agent-task.md` 任務卡，或要求填寫 `templates/feature-handoff.md` 交接卡。

## 先讀文件（按需，不要一次讀完整個 docs/）
- 一定要遵守：`AGENTS.md`（已在上下文中就不必重讀）。
- 依任務類型挑選：參考 `README.md`「AI Agent 文件讀取指引」。
- 進度與完成條件：`docs/09-任務清單.md` 中對應任務 ID 那一段。
- 尚未決定的事項：`docs/08-待決事項.md`（遇到未定規格時先查，不要自行假設）。

## 硬性規則
1. **規劃階段不得修改任何專案檔案**；計畫經使用者明確批准後才實作。
2. 只修改已批准範圍；不改其他模組、不大量重構、不無故重寫整個檔案。
3. 開發機的 `dotnet restore/build/test/ef/publish`、開發 DB Migration、已核准套件安裝、服務啟停 → **由主對話直接執行**，並在 `docs/07` 記錄「指令、實際結果、學習重點」；新套件、合併 PR、正式環境部署與正式 DB Migration、破壞性資料操作 → 先經使用者同意（AGENTS.md 第一節）。子角色不執行指令。
4. 不得宣稱已執行或測試通過；沒有實際輸出就標記「待驗證」。
5. 說明、UI 文字、commit 訊息使用繁體中文；程式識別字、資料表、API 用一致的英文命名。
6. 不提供練習題或作業；改為提供操作解說、資料流、除錯方式與日後查找索引。
7. 未經核准不得：安裝套件、動正式資料、推送遠端、改系統環境、開通付費服務。

## 作業步驟
### 階段 1：規劃（不改檔）
1. 需求理解：使用者會怎麼操作？明確**不做**的事？未知限制與風險？
2. 搜尋相關符號與檔案位置（Grep/Glob），只讀直接相關內容。
3. 必要時比較 2–3 個方案並給出建議（不要列出不會採用的冗長選項）。
4. 檔案級修改計畫：哪些檔案／類別／資料表／API，為什麼。
5. 先列測試案例（Given/When/Then，參考 `docs/05-驗收測試.md` 格式）。
6. 標出是否影響效能、NAS/PC 部署、線上更新、資料庫 schema。
7. 等待使用者批准。

### 階段 2：實作（批准後）
- 局部修改；每個新類別／方法說明用途。
- 遵守分層：`Web → Api → Application → Domain`，`Infrastructure` 實作 Application 介面；Domain 不依賴 UI/DB/HTTP。

### 階段 3：交付（AGENTS.md 第七節格式）
1. 修改檔案清單、核心資料流、設計理由。
2. 測試案例、PowerShell 驗證指令、預期結果、**實際結果或「待驗證」**。
3. 回滾策略、文件更新、必要 ADR、Git 變更摘要（繁體中文 commit 訊息）。
4. 效能／NAS/PC／線上更新影響與對應測試（若適用）。
5. 填寫 `templates/feature-handoff.md` 交接卡內容（大型功能）。

### 階段 4：文件同步
- 交給 `warehouse-documentation` Skill 的規則處理（04 開發手冊、07 開發日誌、05 驗收測試、ADR）。

## 搭配其他 Skill 的路由表
| 任務涉及 | 同時使用 |
|---|---|
| 入庫、出庫、移庫、盤點、結存、冪等、併發 | `warehouse-inventory-safety` |
| 料號、別名、關鍵字、搜尋、正規化 | `warehouse-search-index` |
| 頁面、查詢、列表、圖片、前端載入 | `warehouse-performance` |
| 新增／修改資料表、欄位、索引、約束 | `warehouse-db-migration` |
| 相機、條碼槍、PDA、掃碼解析 | `warehouse-scanner` |
| 發布、服務、NAS、反向代理、版本切換 | `warehouse-deployment` |
| 完成後自我審查或使用者要求審查 | `warehouse-code-review` |
| 每次交付 | `warehouse-documentation` |

## 大型任務
- 拆成可獨立驗證的小任務，每次只處理已批准範圍。
- 跨工作階段時留下精簡交接摘要：已完成、待辦、相關檔案。

## 常見陷阱
- 把「計畫」當成「已批准」而直接動手。
- 回報「建置成功」「測試通過」但其實沒有使用者提供的輸出。
- 為了小功能順手改大量不相關程式。
- 前端技術、NAS 型號、反向代理在 M0 前尚未定案，不可當作既定事實寫進程式或文件。
