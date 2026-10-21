using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Poc.Shared;

namespace Poc.RazorPages.Pages;

public class ReceiptModel : PageModel
{
    // 數量以字串繫結，才能自行判斷「非整數」並給中文錯誤訊息。
    [BindProperty(Name = "itemCode")]
    public string? ItemCode { get; set; }

    [BindProperty(Name = "quantity")]
    public string? Quantity { get; set; }

    [BindProperty(Name = "location")]
    public string? Location { get; set; }

    [BindProperty(Name = "idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    public string? DoneItemCode { get; private set; }
    public string? DoneQuantity { get; private set; }

    public void OnGet(string? done, string? qty)
    {
        IdempotencyKey = Guid.NewGuid().ToString();
        if (!string.IsNullOrEmpty(done))
        {
            DoneItemCode = done;
            DoneQuantity = qty;
        }
    }

    public IActionResult OnPost()
    {
        var code = (ItemCode ?? string.Empty).Trim();
        var item = FakeItemCatalog.FindByCode(code);
        if (item is null)
        {
            ModelState.AddModelError("itemCode", code.Length == 0 ? "請輸入料號" : "料號不存在");
        }

        var quantity = 0;
        if (!int.TryParse((Quantity ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out quantity)
            || quantity < 1 || quantity > 99999)
        {
            ModelState.AddModelError("quantity", "數量必須是 1～99999 的整數");
        }

        if (string.IsNullOrWhiteSpace(Location))
        {
            ModelState.AddModelError("location", "請輸入儲位");
        }

        if (!ModelState.IsValid || item is null)
        {
            // 保留使用者輸入；冪等鍵缺漏時補一個。
            IdempotencyKey = string.IsNullOrEmpty(IdempotencyKey) ? Guid.NewGuid().ToString() : IdempotencyKey;
            return Page();
        }

        // PoC 不寫入任何資料。PRG：成功後 302 回 GET。
        return Redirect($"/receipt?done={Uri.EscapeDataString(item.ItemCode)}&qty={quantity}");
    }

    public string? ErrorFor(string key)
        => ModelState.TryGetValue(key, out var entry) && entry.Errors.Count > 0
            ? entry.Errors[0].ErrorMessage
            : null;
}
