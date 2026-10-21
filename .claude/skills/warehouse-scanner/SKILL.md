---
name: warehouse-scanner
description: 掃碼輸入規則：手機 iOS/Android 相機掃碼（HTTPS、權限、延遲載入、釋放攝影機）、Windows USB HID 條碼槍、Android 工業 PDA Keyboard Wedge（DataWedge、Enter/Tab 尾碼、連掃、焦點、漏字、防重複提交）、統一掃碼 Parser、條碼碼制與物料／儲位／批號／序號識別、PWA。凡處理掃碼頁面、掃碼解析、裝置相容性或 M5 任務時使用。Use for barcode scanning, camera, HID scanner, and PDA keyboard wedge work.
---

# 手機、條碼槍與 PDA 掃描

## 先讀文件（按需）
- `docs/01-需求規格.md`：FR-03 倉庫、儲位與掃碼；入／出／移庫操作流程；設備相容性需求。
- `docs/05-驗收測試.md`：`T-DEVICE-01~03`、`T-PERF-05`。
- `docs/09-任務清單.md`：M0-05、M5 段落。
- 搜尋／條碼識別規則交由 `warehouse-search-index`。

## 架構原則
```text
相機解碼器 ─┐
HID 條碼槍 ─┼─> 原始字串 ─> 統一 Scan Parser ─> 掃描結果（類型＋值）─> 同一後端 API
PDA Wedge ─┘
```
- 三類設備共用**同一個文字解析介面**與同一後端；不同設備只是輸入來源不同。
- Parser 判斷掃到的是：物料條碼、儲位碼、批號、序號或未知；不認得 → 顯示原始值與明確錯誤，不猜測。
- 最終識別由伺服器以精確識別（`ItemCode`／`ItemBarcodes`／`ExactIdentifierRegistry`）判定；**掃碼只走精確比對**，不走模糊搜尋或共用關鍵字。
- 條碼與俗稱分離；GS1 複合條碼完整解析不在 V1 範圍（僅預留擴充點）。

## 鍵盤模擬掃碼（HID／PDA Keyboard Wedge）
- 掃碼槍等同快速鍵盤輸入，尾碼通常為 Enter 或 Tab（依設備設定，需記錄）。
- [ ] 掃碼欄位在頁面載入、送出後、錯誤後都能**自動回到焦點**；焦點遺失時有明顯提示。
- [ ] Enter 觸發「解析＋查詢」，不直接觸發庫存提交；提交需另一個明確確認動作（或規格批准的流程）。
- [ ] **防重複提交**：送出期間鎖定按鈕／忽略重複 Enter；庫存提交仍依賴 `Idempotency-Key`（見 `warehouse-inventory-safety`）。
- [ ] 快速連掃：每次掃描以尾碼切分，排隊處理，不互相覆蓋；漏字或過短字串要拒絕。
- [ ] 不在每個 keypress 呼叫 API；等尾碼或明確送出。
- [ ] **不載入相機解碼程式**（T-PERF-05）。
- 參考：Zebra DataWedge Keystroke Output（`docs/00-專案總計畫.md` 參考連結）。

## 手機相機掃碼
- 需 HTTPS 安全內容環境才可使用 `getUserMedia`；內網憑證方案於 M0 驗證。
- [ ] 相機模組**僅在使用者按下相機掃碼時動態載入**。
- [ ] 離開畫面、掃到結果、切到背景時停止 `MediaStream` 所有 track，釋放攝影機。
- [ ] 權限被拒絕、無相機、瀏覽器不支援 → 可理解的提示，並提供手動輸入替代。
- [ ] 解碼函式庫須查證可商用授權與維護狀態，引入前需使用者核准。
- [ ] 解碼頻率節制，避免舊手機過熱或耗電。

## PWA 注意事項
- PWA ≠ 支援離線庫存交易；離線時不可顯示為已提交。
- Service Worker 更新不得強制 reload 正在連續掃碼或填寫中的表單（見 `warehouse-deployment`）。

## 測試
- 自動化：Parser 單元測試（各碼制範例、前後空白、Tab/Enter 尾碼、未知格式、過短字串、前導零保留）。
- 現場實機（無法自動化，需記錄設備型號、OS、瀏覽器、設定、步驟、結果）：
  - T-DEVICE-01 Windows 11 + USB HID 條碼槍完成查料、入庫、出庫。
  - T-DEVICE-02 iPhone Safari／Android Chrome 相機授權與拒絕流程、HTTPS PWA 安裝。
  - T-DEVICE-03 Android PDA 帶 Enter/Tab 不重複提交、不遺失焦點、不漏字。
- 未取得設備時列為「待測」，不得宣稱已相容。

## 交付與文件
- `docs/04-開發手冊.md` §6：掃碼資料流、Parser 位置、焦點處理程式位置、常見故障（焦點跑掉、尾碼設定錯、相機黑畫面、權限被拒）。
- 設備相容性表（型號／OS／瀏覽器／結果）記錄於 `docs/05-驗收測試.md`。

## 常見陷阱
- 用模糊搜尋處理掃碼結果，導致掃到錯的料號。
- 每個頁面一載入就啟動相機或載入解碼器。
- Enter 直接送出庫存交易，連掃時重複過帳。
- 只在開發機用鍵盤模擬測試，就宣稱 PDA 可用。
