# M0-08 前端 PoC 共用規格

兩個 PoC（`Poc.RazorPages` port 5101、`Poc.BlazorSsr` port 5102）必須實作完全相同的網址與 DOM id，`Poc.Bench` 用同一套腳本量測。資料一律來自 `Poc.Shared.FakeItemCatalog`。

## 共通要求
- 只用 http（`launchSettings.json` 的 http profile 設定固定 port；移除 `UseHttpsRedirection`／HSTS）。
- 介面文字繁體中文；**不使用 Bootstrap、jQuery 或任何前端框架**；兩邊使用同一份極簡 CSS（`wwwroot/app.css`，內容相同）。
- 相容 Chrome 109：不使用 CSS nesting、`:has()` 以外的新 CSS 也盡量避免；JS 只用原生 API。
- 未登入（沒有 cookie `poc_user`）時，`/`、`/search`、`/receipt`、`/scan` 一律導向 `/login`。
- 每頁有導覽列：`<nav id="nav">`，連結 `a#nav-search`、`a#nav-receipt`、`a#nav-scan`。

## 1. 登入 `/login`
- `input#username`（name `username`）、`button#login-submit`；POST 後設定 cookie `poc_user`（HttpOnly，值為帳號），302 導向 `/`。
- 帳號空白 → 顯示 `.field-error`，不導向。
- `/` 首頁顯示 `#welcome`（含帳號名稱）與導覽列。

## 2. 查料 `/search?q=&page=`
- `form#search-form`（method GET），`input#q`（name `q`），按 Enter 送出。
- 結果：`#total-count`（只有數字），`table#results` 內每筆 `tr.result-row`，含 `td.item-code`、`td.item-name`、`td.location`、`td.hit-reason`。
- 分頁：有下一頁時顯示 `a#next-page`；有上一頁時 `a#prev-page`。
- 查詢字串為空或超過 50 字：不查詢，顯示提示 `#search-hint`。

## 3. 入庫表單 `/receipt`
- `input#itemCode`（name `itemCode`）、`input#quantity`（name `quantity`，數字）、`input#location`（name `location`）、隱藏欄位 `idempotencyKey`（GET 時產生 GUID）、`button#receipt-submit`。
- 伺服器驗證：料號必須存在於 `FakeItemCatalog.FindByCode`；數量 1～99999 整數；儲位必填。錯誤顯示在對應欄位下方的 `.field-error`，保留使用者已輸入的值。
- 成功：PRG，302 導向 `/receipt?done=<料號>`，頁面顯示 `#receipt-success`（含料號與數量）及新的空白表單。（不實際寫入任何資料。）

## 4. 掃碼 `/scan`
- `input#scan-input`：頁面載入即取得焦點；處理完每筆後焦點必須回到此欄位且內容清空。
- 按 Enter = 一筆掃碼：依 `FindByCode` 查詢，於 `ul#scan-list` **最上方**新增 `li.scan-row`：找到時含 `span.item-code`、`span.item-name`；找不到時含 `span.scan-error`（文字含原始輸入）。
- `#scan-count`（只有數字）＝目前清單筆數。
- 連續快速掃碼（每字元間隔約 10 ms、每筆以 Enter 結尾、筆與筆之間不等待）時，**不得遺失字元或整筆**；請以各框架最自然的方式實作並在檔案頂端註解說明做法。

## 量測用輔助
- `GET /healthz` 回 200 `ok`（Bench 用來等待啟動）。
