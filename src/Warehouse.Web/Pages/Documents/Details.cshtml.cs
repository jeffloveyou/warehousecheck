using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Api.Authorization;
using Warehouse.Application.Abstractions;
using Warehouse.Application.Errors;
using Warehouse.Application.Inventory;
using Warehouse.Application.MasterData;
using Warehouse.Application.Scanning;
using Warehouse.Application.Search;
using Warehouse.Application.Settings;
using Warehouse.Application.Warehousing;
using Warehouse.Domain;
using Warehouse.Domain.Inventory;
using Warehouse.Domain.Scanning;
using Warehouse.Domain.Text;
using Warehouse.Web.Pages.Shared;

namespace Warehouse.Web.Pages.Documents;

/// <summary>
/// 進出倉單明細頁：草稿時編輯表頭、加／刪明細；依狀態、權限與「單據審核」設定顯示確認、送審、核准、退回、取消、沖銷。
/// 確認／送審／核准按鈕帶開頁時產生的冪等鍵（連點、重新整理後重送只會執行一次）與 Version（xmin，併發檢查）。
/// 權限一律由服務再檢查；這裡的 IAuthorizationService 只決定 handler 能不能執行、按鈕要不要顯示。
/// 料號搜尋用 GET 重新顯示頁面（?ItemQuery=），不做逐字即時建議（低負載原則）。
/// </summary>
[RequestSizeLimit(11 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
public class DetailsModel(
    IStockDocumentService documents, IDocumentAttachmentService attachments, IDocumentMasterDataService master,
    ISettingsService settings, IItemSearchService search, IItemLocationService itemLocations, IStockQueryService stock,
    IAuthorizationService authorization, IStockReservationService reservations) : PageModel
{
    /// <summary>料號搜尋最多列幾筆候選。</summary>
    private const int CandidateLimit = 20;

    private Guid? _redirectId;
    private bool _keepHeaderInput;

    public DocumentDetails Doc { get; private set; } = null!;

    public SystemSettings Settings { get; private set; } = null!;

    public DocumentHeaderOptions? HeaderOptions { get; private set; }

    public IReadOnlyList<DocumentAttachmentRow> Attachments { get; private set; } = [];

    public bool CanEdit { get; private set; }

    public bool CanConfirm { get; private set; }

    public bool CanSubmit { get; private set; }

    public bool CanApprove { get; private set; }

    public bool CanCancel { get; private set; }

    public bool CanReverse { get; private set; }

    public bool CanUpload { get; private set; }

    public string? Error { get; private set; }

    /// <summary>料號搜尋的提示（找不到、結果過多等）。</summary>
    public string? SearchNotice { get; private set; }

    /// <summary>精確命中的料號（已帶入料號欄）。</summary>
    public ItemSearchHit? Resolved { get; private set; }

    /// <summary>沒有精確命中時的候選清單，由人點選。</summary>
    public IReadOnlyList<ItemSearchHit> Candidates { get; private set; } = [];

    /// <summary>進倉：建議儲位（預設、常用、目前有庫存）。</summary>
    public IReadOnlyList<LocationSuggestion>? Suggestions { get; private set; }

    /// <summary>出倉：該料號在本倉仍有效的手動預留（不含出倉單送審自動建立的），供明細選擇依預留出庫。</summary>
    public IReadOnlyList<ReservationRow> ReservationOptions { get; private set; } = [];

    /// <summary>出倉、移庫：該料號在本倉的結存維度（儲位、批號、序號、數量）。</summary>
    public PagedResult<BalanceRow>? Balances { get; private set; }

    [BindProperty]
    public uint Version { get; set; }

    [BindProperty]
    public string? IdempotencyKey { get; set; }

    [BindProperty]
    public string? Comment { get; set; }

    [BindProperty]
    public DocumentHeaderForm Header { get; set; } = new();

    /// <summary>料號搜尋輸入（可掃碼；GET ?ItemQuery=）。精確命中時帶入明細表單的料號欄。</summary>
    [BindProperty(SupportsGet = true)]
    public string? ItemQuery { get; set; }

    [BindProperty]
    public LineForm Line { get; set; } = new();

    [BindProperty]
    public IFormFile? Attachment { get; set; }

    public sealed class LineForm
    {
        public string? LocationCode { get; set; }

        public string? ItemCode { get; set; }

        public string? LotNo { get; set; }

        public string? SerialNo { get; set; }

        public DateOnly? ExpiryDate { get; set; }

        public decimal Quantity { get; set; }

        /// <summary>移庫：目的倉庫碼（空白＝同倉；目的儲位是別倉 QR 時用 QR 上的倉庫）。</summary>
        public string? TargetWarehouseCode { get; set; }

        public string? TargetLocationCode { get; set; }

        /// <summary>出倉：選填的預留 ID（文字，伺服器端解析成 Guid）。</summary>
        public string? ReservationText { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, fillHeaderFromDoc: true, cancellationToken))
        {
            return NotFound();
        }
        IdempotencyKey = Guid.NewGuid().ToString();
        return Page();
    }

    public Task<IActionResult> OnPostUpdateHeaderAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            _keepHeaderInput = true;
            var header = await DocumentFormSupport.ToHeaderAsync(Header, master, cancellationToken);
            await documents.UpdateHeaderAsync(id, Version, header, actor, cancellationToken);
            return "已儲存表頭。";
        }, cancellationToken);

    public Task<IActionResult> OnPostAddLineAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            var doc = await documents.GetAsync(id, cancellationToken)
                ?? throw new NotFoundException("document.not_found", "找不到單據。");
            Guid? reservationId = null;
            if (doc.DocType == StockDocumentType.Outbound && !string.IsNullOrWhiteSpace(Line.ReservationText))
            {
                reservationId = Guid.TryParse(Line.ReservationText.Trim(), out var parsed)
                    ? parsed
                    : throw new DomainRuleException("預留 ID 格式不正確。");
            }
            var isTransfer = doc.DocType == StockDocumentType.Transfer;
            var input = new DocumentLineInput(
                Line.LocationCode ?? string.Empty, Line.ItemCode ?? string.Empty, Line.Quantity,
                Line.LotNo, Line.SerialNo,
                doc.DocType == StockDocumentType.Inbound ? Line.ExpiryDate : null,
                isTransfer ? Line.TargetWarehouseCode : null,
                isTransfer ? Line.TargetLocationCode : null,
                reservationId);
            await documents.AddLineAsync(id, input, actor, cancellationToken);
            return "已新增明細。";
        }, cancellationToken);

    public Task<IActionResult> OnPostRemoveLineAsync(Guid id, Guid lineId, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            await documents.RemoveLineAsync(id, lineId, actor, cancellationToken);
            return "已刪除明細。";
        }, cancellationToken);

    public Task<IActionResult> OnPostConfirmAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            await documents.ConfirmAsync(id, Version, IdempotencyKey ?? string.Empty, actor, cancellationToken);
            return "已確認並過帳。";
        }, cancellationToken);

    public Task<IActionResult> OnPostSubmitAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            await documents.SubmitAsync(id, Version, IdempotencyKey ?? string.Empty, actor, cancellationToken);
            return "已送審，等待倉庫主管核准。";
        }, cancellationToken);

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentApprove, async actor =>
        {
            await documents.ApproveAsync(id, Version, IdempotencyKey ?? string.Empty, actor, cancellationToken);
            return "已核准並過帳。";
        }, cancellationToken);

    public Task<IActionResult> OnPostReturnAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentApprove, async actor =>
        {
            await documents.ReturnAsync(id, Version, Comment ?? string.Empty, actor, cancellationToken);
            return "已退回給建立者修改。";
        }, cancellationToken);

    public Task<IActionResult> OnPostCancelAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            await documents.CancelAsync(id, Version, actor, cancellationToken);
            return "已取消。";
        }, cancellationToken);

    public Task<IActionResult> OnPostReverseAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            _redirectId = await documents.CreateReversalAsync(id, actor, cancellationToken);
            return "已建立沖銷單草稿，請檢查明細後確認過帳。";
        }, cancellationToken);

    public Task<IActionResult> OnPostUploadAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            if (Attachment is null || Attachment.Length == 0)
            {
                throw new DomainRuleException("請選擇要上傳的檔案。");
            }
            await using var stream = Attachment.OpenReadStream();
            await attachments.UploadAsync(id, Attachment.FileName, stream, Attachment.Length, actor, cancellationToken);
            return "已上傳附件。";
        }, cancellationToken);

    public Task<IActionResult> OnPostRemoveAttachmentAsync(Guid id, Guid attachmentId, CancellationToken cancellationToken) =>
        RunAsync(id, Policies.DocumentEdit, async actor =>
        {
            await attachments.RemoveAsync(id, attachmentId, actor, cancellationToken);
            return "已移除附件。";
        }, cancellationToken);

    /// <summary>連續掃碼面板（M5-03）的設定：步驟依單據類型；解析與加入明細都打本頁的 handler。</summary>
    public ScanPanelModel ScanPanel => new(
        Doc.DocType switch
        {
            StockDocumentType.Inbound => "inbound",
            StockDocumentType.Outbound => "outbound",
            _ => "transfer",
        },
        Doc.WarehouseId,
        Url.Page("/Documents/Details", "ScanResolve", new { id = Doc.Id })!,
        Url.Page("/Documents/Details", "ScanAddLine", new { id = Doc.Id })!);

    /// <summary>
    /// 掃碼面板解析一筆掃碼（GET，回傳 JSON）。只做精確比對（<see cref="IScanResolver"/>），物料另附本倉結存或建議儲位、可選的手動預留。
    /// <paramref name="target"/>：移庫單正在等目的儲位，此時允許其他倉庫的儲位 QR。
    /// </summary>
    public async Task<IActionResult> OnGetScanResolveAsync(
        Guid id, string? code, bool target, [FromServices] IScanResolver resolver, CancellationToken cancellationToken)
    {
        if (await documents.GetAsync(id, cancellationToken) is not { } doc)
        {
            return NotFound();
        }
        var anyWarehouse = target && doc.DocType == StockDocumentType.Transfer && ScanCodeParser.Parse(code).Kind == ScanCodeKind.Location;
        var result = await resolver.ResolveAsync(code, anyWarehouse ? null : doc.WarehouseId, cancellationToken);
        object? extra = null;
        if (result.Item is { } item)
        {
            if (doc.DocType == StockDocumentType.Inbound)
            {
                var suggestions = await itemLocations.SuggestAsync(item.Id, doc.WarehouseId, cancellationToken);
                extra = new { suggestions = suggestions.Select(s => new { location = s.LocationCode, reason = s.Reason.ToString(), quantity = s.QuantityHere }) };
            }
            else
            {
                var balances = await stock.GetBalancesAsync(doc.WarehouseId, null, item.Id, 1, cancellationToken);
                var open = doc.DocType == StockDocumentType.Outbound
                    ? (await reservations.ListAsync(item.ItemCode, doc.WarehouseId, ReservationStatus.Open, 1, cancellationToken, selectableOnly: true))
                        .Items.Where(r => r.ReferenceType != "StockDocument").ToList()
                    : [];
                extra = new
                {
                    balances = balances.Items.Select(b => new
                    {
                        location = b.LocationCode, lot = b.LotNo, serial = b.SerialNo, quantity = b.Quantity, available = b.LocationAvailable,
                    }),
                    balanceTotal = balances.TotalCount,
                    reservations = open.Select(r => new
                    {
                        id = r.Id, remaining = r.RemainingQuantity, by = r.CreatedByName, expiresAt = r.ExpiresAt is { } ex ? Display.Time(ex) : null,
                    }),
                };
            }
        }
        return new JsonResult(ScanJson.From(result, extra));
    }

    /// <summary>
    /// 掃碼面板加入明細（POST JSON，antiforgery 由 RequestVerificationToken 標頭驗證）。
    /// 成功回傳重繪的明細表（HTML）並在標頭帶新的單據版本；規則錯誤回 400 JSON。網路錯誤時畫面不自動重送（避免累加兩次）。
    /// </summary>
    public async Task<IActionResult> OnPostScanAddLineAsync(Guid id, [FromBody] ScanLineRequest request, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, Policies.DocumentEdit)).Succeeded)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        Guid lineId;
        try
        {
            var doc = await documents.GetAsync(id, cancellationToken)
                ?? throw new NotFoundException("document.not_found", "找不到單據。");
            var isTransfer = doc.DocType == StockDocumentType.Transfer;
            lineId = await documents.AddLineAsync(id, new DocumentLineInput(
                request.LocationCode ?? string.Empty, request.ItemCode ?? string.Empty, request.Quantity,
                request.LotNo, request.SerialNo,
                doc.DocType == StockDocumentType.Inbound ? request.ExpiryDate : null,
                isTransfer ? request.TargetWarehouseCode : null,
                isTransfer ? request.TargetLocationCode : null,
                doc.DocType == StockDocumentType.Outbound ? request.ReservationId : null,
                request.Merge), User.ToActor(), cancellationToken);
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            // 400 只代表「寫入前就被拒絕」：畫面據此判斷這一筆沒有加入
            return new JsonResult(new { message = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
        }
        // 已寫入：之後的重繪若出錯會是 500，畫面當成「不確定」並重新讀取明細表，不會讓人以為失敗而重掃（PR 2 審查 S2）。
        // 只讀明細表需要的資料，不載入表頭選項、附件（PR 2 審查 S3）
        if (await documents.GetAsync(id, cancellationToken) is not { } current)
        {
            return NotFound();
        }
        Doc = current;
        Version = current.Version;
        var actor = User.ToActor();
        CanEdit = current.Status == StockDocumentStatus.Draft && (actor.IsWarehouseManager || current.CreatedById == actor.Id);
        Response.Headers["X-Line-Id"] = lineId.ToString();
        return Partial("_DocumentLines", this);
    }

    /// <summary>掃碼面板送出的明細（JSON）。</summary>
    public sealed class ScanLineRequest
    {
        public string? LocationCode { get; set; }

        public string? ItemCode { get; set; }

        public decimal Quantity { get; set; }

        public string? LotNo { get; set; }

        public string? SerialNo { get; set; }

        public DateOnly? ExpiryDate { get; set; }

        public string? TargetWarehouseCode { get; set; }

        public string? TargetLocationCode { get; set; }

        public Guid? ReservationId { get; set; }

        /// <summary>逐件累加：相同的草稿行累加數量。</summary>
        public bool Merge { get; set; }
    }

    /// <summary>候選料號的連結：以該料號的正式料號重新搜尋，會精確命中並帶入。</summary>
    public string CandidateUrl(string itemCode) => Url.Page("/Documents/Details", new { id = Doc.Id, ItemQuery = itemCode })!;

    /// <summary>執行動作：成功轉址（PRG）；已知錯誤重新載入並顯示訊息，保留使用者輸入。</summary>
    private async Task<IActionResult> RunAsync(
        Guid id, string policy, Func<Actor, Task<string>> action, CancellationToken cancellationToken)
    {
        if (!(await authorization.AuthorizeAsync(User, policy)).Succeeded)
        {
            return Forbid();
        }
        try
        {
            TempData["Message"] = await action(User.ToActor());
            return RedirectToPage(new { id = _redirectId ?? id });
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        if (!await LoadAsync(id, fillHeaderFromDoc: !_keepHeaderInput, cancellationToken))
        {
            return NotFound();
        }
        IdempotencyKey = Guid.NewGuid().ToString();
        return Page();
    }

    private async Task<bool> LoadAsync(Guid id, bool fillHeaderFromDoc, CancellationToken cancellationToken)
    {
        if (await documents.GetAsync(id, cancellationToken) is not { } doc)
        {
            return false;
        }
        Doc = doc;
        Version = doc.Version;
        Settings = await settings.GetAsync(cancellationToken);
        var actor = User.ToActor();
        var canDocumentEdit = (await authorization.AuthorizeAsync(User, Policies.DocumentEdit)).Succeeded;
        var owner = actor.IsWarehouseManager || doc.CreatedById == actor.Id;
        var draft = doc.Status == StockDocumentStatus.Draft;
        CanEdit = draft && canDocumentEdit && owner;
        CanConfirm = CanEdit && !Settings.DocumentApprovalEnabled;
        CanSubmit = CanEdit && Settings.DocumentApprovalEnabled;
        CanCancel = (draft || doc.Status == StockDocumentStatus.Submitted) && canDocumentEdit && owner;
        CanApprove = doc.Status == StockDocumentStatus.Submitted
            && (await authorization.AuthorizeAsync(User, Policies.DocumentApprove)).Succeeded;
        CanReverse = doc.Status == StockDocumentStatus.Posted && canDocumentEdit && doc.DocType != StockDocumentType.Adjustment;
        CanUpload = Settings.DocumentAttachmentsEnabled && doc.Status != StockDocumentStatus.Cancelled && canDocumentEdit && owner;
        // 關閉附件功能也要讀：既有附件仍可檢視（M6-05）
        Attachments = await attachments.ListAsync(id, cancellationToken);
        if (CanEdit)
        {
            var current = new DocumentHeaderForm
            {
                PurposeId = doc.PurposeId,
                PartnerId = doc.PartnerId,
                ExternalNo = doc.ExternalNo,
                Note = doc.Note,
            };
            if (fillHeaderFromDoc)
            {
                Header = current;
            }
            HeaderOptions = await DocumentFormSupport.LoadOptionsAsync(
                master, settings, current, doc.PurposeName, doc.PartnerName, cancellationToken);
            await SearchItemAsync(doc, cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// 料號輸入（可掃碼）後的搜尋：精確命中一筆就帶入料號並載入建議儲位或結存；否則列出候選（最多 20 筆）給人點選。
    /// 模糊結果不會自動當成單據對象。
    /// </summary>
    private async Task SearchItemAsync(DocumentDetails doc, CancellationToken cancellationToken)
    {
        // GET 用搜尋框的輸入；POST 失敗重新顯示時，用剛送出的料號欄
        var raw = HttpMethods.IsGet(Request.Method) ? ItemQuery : Line.ItemCode;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        var query = raw.Trim();
        var parsed = ScanCodeParser.Parse(query);
        var text = parsed.Kind == ScanCodeKind.Item && parsed.ItemCode is not null
            ? parsed.ItemCode
            : TextNormalizer.Normalize(query);
        var result = await search.SearchAsync(new ItemSearchRequest(text, false, 1, CandidateLimit), cancellationToken);
        var hits = result.Items.Where(h => h.ItemCode is not null).ToList();
        var exact = hits.Where(h => h.Reason == HitReason.ItemCode).ToList();
        // 原廠條碼命中只列為候選、不自動帶入：條碼可能是外箱（包裝數量），由人確認後輸入基本單位數量（M5-01 審查 M1）
        if (exact.Count == 0 && hits.Count == 1 && hits[0].Reason == HitReason.Alias)
        {
            exact = hits;
        }
        if (exact.Count == 1)
        {
            Resolved = exact[0];
            Line.ItemCode = Resolved.ItemCode;
            if (doc.DocType == StockDocumentType.Inbound)
            {
                Suggestions = await itemLocations.SuggestAsync(Resolved.Id, doc.WarehouseId, cancellationToken);
            }
            else
            {
                Balances = await stock.GetBalancesAsync(doc.WarehouseId, null, Resolved.Id, 1, cancellationToken);
            }
            if (doc.DocType == StockDocumentType.Outbound)
            {
                var open = await reservations.ListAsync(Resolved.ItemCode, doc.WarehouseId, ReservationStatus.Open, 1, cancellationToken, selectableOnly: true);
                ReservationOptions = open.Items.Where(r => r.ReferenceType != "StockDocument").ToList();
            }
            return;
        }
        Candidates = hits;
        SearchNotice = hits.Count == 0
            ? $"找不到符合「{query}」的有效物料。"
            : result.TotalCount > hits.Count
                ? $"符合的物料超過 {hits.Count} 筆，只列前 {hits.Count} 筆，請輸入更精確的料號或關鍵字。"
                : null;
    }
}
