# Warehouse AI Co-Development｜混合型倉庫管理系統

**整合規劃基線 v0.2｜2026-10-21**  
狀態：僅為規劃／規格文件；尚未包含可執行的庫存管理系統，未對 NAS/PC、舊電腦效能或線上更新作實機測試。

## 專案目標
建立支援零件、原料、半成品、成品和耗材的倉庫管理系統，使用者可自行增加多種搜尋俗稱／關鍵字；支援手機、電腦條碼槍與工業 PDA。AI Agent 協作開發，同步留下能讓人自主理解、修改、測試及發布的繁體中文開發手冊。

**新增不可忽略的條件**：舊電腦必須能輕量使用；同一程式碼必須支援 Windows PC 與符合條件的 Linux NAS 作伺服器；應用程式更新以不妨礙正常倉庫作業為目標（單機硬體故障不等於零停機）。

## 開始方式
1. 閱讀 `docs/00-專案總計畫.md`、`docs/01-需求規格.md`。
2. 將 `AGENTS.md` 放在專案根目錄，讓 AI Agent 先依規範提出計畫、等待核准，之後才實作。
3. M0 **先做**低規格電腦效能、Web 前端、Windows/NAS 部署、雙實例更新技術驗證；切勿直接把 Blazor WebAssembly 或 NAS 容器能力視為已批准。
4. 每次實作後由使用者在 PowerShell 執行必要指令，Agent 依真實結果修正；文件與程式同版本維護。
5. **不提供練習題或要求獨立完成課後作業**；開發手冊重點是清楚說明「怎麼運作、去哪裡改、如何驗證、如何排錯」。

## 文件索引
| 路徑 | 用途 |
|---|---|
| `AGENTS.md` | AI Agent 規劃／修改權限、效能、部署與線上更新規範 |
| `docs/00-專案總計畫.md` | 正式目標、技術方向、時程階段、交付物 |
| `docs/01-需求規格.md` | 物料、搜尋、庫存、權限、低負載與部署需求 |
| `docs/02-架構與資料庫.md` | 分層、資料表、交易、併發、基礎系統設計 |
| `docs/03-索引搜尋規格.md` | 任意別名與共用關鍵字搜尋的詳細行為 |
| `docs/04-開發手冊.md` | 長期更新的繁體中文實作／除錯／修改參考手冊 |
| `docs/05-驗收測試.md` | 功能、現場設備、效能、NAS/PC、更新與回滾測試 |
| `docs/06-學習與交接.md` | 依模組查閱知識、維護交接（無練習題） |
| `docs/07-開發日誌.md` | 功能與設計變更紀錄、實測證據 |
| `docs/08-待決事項.md` | 尚待確認的規格、硬體及部署條件 |
| `docs/09-任務清單.md` | M0–M7 開發順序、完成條件 |
| `docs/10-低負載與跨平台部署.md` | 客戶端效能策略、Windows 與 Linux NAS 部署準則 |
| `docs/11-線上更新與回滾.md` | Blue-Green、反向代理、DB 相容與回滾設計 |
| `docs/adr/0001-技術基線.md` | 暫定技術選型與 M0 決策門檻 |
| `docs/adr/0002-效能與部署策略.md` | 效能／雙平台／不中斷更新的架構決策提案 |
| `docs/adr/0003-V1業務規則與範圍.md` | 料號、建檔核准、別名、追蹤模式、條碼選配、V1 範圍 |
| `docs/adr/0004-負庫存與預留庫存.md` | 負庫存例外與預留庫存的規則、併發與測試 |
| `docs/adr/0005-開發環境M0範圍與AI角色.md` | 開發工具、規模基線、M0 範圍、更新策略、AI 角色 |
| `docs/adr/0006-M0開工決策與Win7相容.md` | 必填欄位、料號前綴、儲位與單位、專案結構、備份、更新相容期、Win7 支援 |
| `docs/adr/0007-前端技術選型.md` | M0-08 Razor Pages vs Blazor SSR 量測結果與建議（提案） |
| `poc/` | M0-08 前端比較 PoC 與 Playwright 量測工具（不屬於正式程式） |
| `scripts/check-env.ps1` | M0-01 開發環境檢查（唯讀） |
| `scripts/bootstrap-solution.ps1` | M0-02 建立 solution 與分層專案（執行一次） |
| `scripts/init-dev-db.ps1` | M0-04 建立開發／測試資料庫與 `warehouse_app` 帳號 |
| `scripts/new-dev-certs.ps1` | M0-05 開發用內部 CA 與 HTTPS 憑證（`-Remove` 移除） |
| `scripts/open-dev-firewall.ps1` | M0-05 開放手機連線用的防火牆連接埠 5443（`-Remove` 移除） |
| `scripts/publish.ps1` | M0-10 發布（win-x64／linux-x64／linux-arm64，framework-dependent 或 self-contained） |
| `scripts/windows/install-service.ps1`、`uninstall-service.ps1` | M0-10 安裝／移除 Windows 服務 |
| `docs/adr/0008-Windows部署與設定分離.md` | 部署目錄、外部設定、服務帳號、FDD／SC 決策 |
| `templates/agent-task.md` | 每次交給 AI Agent 的需求任務模板 |
| `templates/feature-handoff.md` | 每項功能的修改與驗證交接卡 |

## AI Agent Skills（`.claude/skills/`）
Skill 只放流程、檢查清單與「該讀哪份文件」，詳細規格仍以 `docs/` 為準。

| Skill | 用途 |
|---|---|
| `warehouse-feature-development` | 功能規劃、修改、交付流程；依任務指引搭配其他 Skill |
| `warehouse-inventory-safety` | 庫存交易正確性、冪等、併發與資料保護 |
| `warehouse-search-index` | 自訂物料索引、別名、關鍵字、正規化與模糊搜尋 |
| `warehouse-performance` | 舊電腦低負載與效能調校、量測紀錄 |
| `warehouse-documentation` | 開發手冊及文件同步規則 |
| `warehouse-db-migration` | 資料庫結構更新、Expand → Migrate → Contract 相容性 |
| `warehouse-scanner` | 手機相機、USB HID 條碼槍、PDA Keyboard Wedge 掃描 |
| `warehouse-deployment` | Windows／NAS 發布、Blue-Green 更新與回滾 |
| `warehouse-code-review` | 程式碼、安全性及效能審查清單 |

## AI 角色（`.claude/agents/`）
依任務選用模型以節省 token；分派規則見 `AGENTS.md` 第十節。

| 角色 | 模型 | 用途 |
|---|---|---|
| `warehouse-architect` | Opus | 方案比較、ADR 草稿、架構風險（唯讀） |
| `warehouse-reviewer` | Opus | 庫存／併發／安全／Migration 審查（唯讀） |
| `warehouse-implementer` | Sonnet | 依已批准計畫實作 |
| `warehouse-test-writer` | Sonnet | 單元、整合、併發測試 |
| `warehouse-doc-writer` | Haiku | 開發手冊、日誌、驗收文件同步 |
| `warehouse-explorer` | Haiku | 檔案、符號、文件段落搜尋 |

## 邊界與警語
- NAS 支援必須依實際品牌、型號、CPU 架構、RAM、OS、容器執行環境驗證；並非所有 NAS 都能執行容器或 PostgreSQL。
- 藍綠部署需要兩個可同時運作的 App 實例及相容的資料庫 schema；單一主機故障、資料庫故障與作業系統更新並不在「不中斷」承諾內。
- 相機掃碼與 PWA 安裝行為受瀏覽器及 HTTPS 安全限制；正式上線需以實機驗證。
- 本包規劃文件中的範例埠號、效能數值和資料表皆非已執行或已驗收成果。
## AI Agent 文件讀取指引

一般任務不需要載入全部文件。

任務類型與建議閱讀文件：

- 專案初始化：
  00、01、02、08、09

- 物料及資料庫：
  01、02、09

- 搜尋索引：
  03、02、09

- 入出庫及庫存交易：
  01、02、05

- 手機與 PDA：
  01、05

- 效能最佳化：
  10、05

- Windows / NAS 部署：
  10、02

- 線上更新：
  11、10、05

- 架構變更：
  對應 ADR 及相關規格

- 程式維護：
  04 及相關程式碼

上述文件僅為建議起點。
實際讀取範圍應依任務調整。

禁止無必要地一次讀取整個 docs/ 資料夾。
