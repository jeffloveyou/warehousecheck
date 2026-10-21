---
name: warehouse-search-index
description: 自訂物料搜尋索引規則：正式料號 ItemCode、條碼 ItemBarcodes、專屬別名 ExclusiveAlias（ItemAliases）、共用關鍵字 SharedKeyword（ItemKeywords）、ExactIdentifierRegistry、文字正規化 NormalizedText、搜尋排序與命中原因、模糊搜尋 ILIKE／pg_trgm、防止重複建檔。凡修改物料搜尋、新增俗稱／別名／關鍵字、物料建檔相似候選時使用。Use for item search, alias, keyword, normalization and fuzzy matching work.
---

# 物料搜尋索引、別名與模糊搜尋

> 規格唯一來源：`docs/03-索引搜尋規格.md`。本 Skill 為實作與審查用的檢查清單。

## 先讀文件（按需）
- `docs/03-索引搜尋規格.md`：全文（約 60 行，搜尋任務必讀）。
- `docs/02-架構與資料庫.md`：`Items`、`ItemBarcodes`、`ItemAliases`、`ItemKeywords` 及「重要一致性設計」中 `ExactIdentifierRegistry` 段落。
- `docs/09-任務清單.md`：M2 段落。

## 語意分層（不可混用）
| 類型 | 對應 | 唯一性 |
|---|---|---|
| `ItemCode` | 正式料號 | 全域唯一，不可重複 |
| `ItemBarcodes` | 實體條碼／包裝碼 | 精確識別；與俗稱分離 |
| `ExclusiveAlias` | 專屬別名 | 有效集合中 `NormalizedText` 只能對應一個 `ItemId` |
| `SharedKeyword` | 俗稱、用途、特徵 | `(ItemId, NormalizedText)` 唯一；可跨多物料 |

- 料號、條碼、專屬別名的精確識別衝突，建議由 `ExactIdentifierRegistry (NormalizedIdentifier, Namespace, ItemId)` 加 **DB 唯一約束**統一保證，避免跨表「先查再寫」的競態。
- 共用關鍵字**絕不可**被當作唯一識別或掃碼精確命中。

## 正規化規則
- 保留原文欄位（`Text`／`RawValue`），另存搜尋用 `NormalizedText`／`NormalizedValue`，不破壞原始值。
- 自然語言搜尋：Unicode NFKC、大小寫不敏感、整理前後與重複空白。
- 精確代碼（料號、GTIN、型號）：連字號、斜線、前導零可能有意義 → 使用獨立、較保守的正規化規則；不可套用「忽略『的』」這類寬鬆處理。
- 全半形、繁簡、中英混合需以測試資料驗證；不假設能自動理解同義詞。
- 正規化函式集中在一處（Domain 或 Application），寫入與查詢共用同一實作，並有單元測試。

## 搜尋排序（由高到低）
1. 正式料號／可唯一解析的識別碼精確命中
2. 專屬別名精確命中
3. 型號／名稱精確命中
4. 共用關鍵字精確命中
5. 多詞篩選、部分符合
6. 模糊相似度（僅為建議候選）

結果 DTO 必須包含：**命中欄位／命中原因**、料號、完整品名規格；畫面可視需要顯示可用量、儲位、縮圖、品牌。

## 業務規則
- 模糊結果、排序第一名都**不得自動成為出庫或交易對象**；需人工選擇確認。
- 員工可新增／撤銷自己建立的共用關鍵字（留稽核）；專屬別名可能需管理員核准。
- 新增關鍵字時顯示「此關鍵字已被 N 個料號使用」；不自動合併物料。
- 搜尋不到時流程：選既有料號 → 確認產品 → 新增索引。**不得從搜尋框直接建立新物料**；無建檔權限者不能繞過審核。
- 索引採停用（`IsActive = false`）不硬刪除；停用者不再命中但可稽核。
- 正式建檔前顯示相似物料候選（料號、原廠型號、條碼、別名），由人判斷是否重複。

## 效能與安全
- 一律參數化查詢；搜尋字串設最少／最多長度；必要時 rate limit。
- 後端分頁＋最大回傳筆數；即使關鍵字對應大量料號也要分頁。
- UI 使用 debounce 或按 Enter 送出；不下載全部料號／索引到瀏覽器。
- 演進：M2 先用精確比對 + B-tree 索引 + `ILIKE`／多詞；之後依實測決定是否導入 `pg_trgm` GIN/GiST，並附 `EXPLAIN (ANALYZE, BUFFERS)` 證據。V1 不依賴雲端 AI 語意搜尋。

## 必要測試（對應規格驗收情境 A–H）
- A 多個別名都命中同一料號；B 共用關鍵字顯示多個料號；C 重複新增同料號索引被提示且不建立重複；D 已被專屬占用的別名不可掛到其他料號；E `MR J4 40B` 只顯示 `MR-J4-40B` 為相似候選，不當精確條碼；F 新增關鍵字後可查得；G 停用索引不命中但可稽核；H 無權限不能經搜尋頁建立 Item。
- 正規化單元測試：全半形、大小寫、空白、連字號、前導零、中英混合。
- 併發測試：兩人同時建立相同專屬別名 → 只有一筆成功（DB 約束）。

## 常見陷阱
- 把俗稱存進 `ItemBarcodes`，造成掃碼誤判。
- 寫入與查詢用不同的正規化邏輯，導致「存得進去卻搜不到」。
- 只在應用層檢查別名唯一，缺 DB 約束。
- `ILIKE '%x%'` 無上限查詢拖垮 DB。
