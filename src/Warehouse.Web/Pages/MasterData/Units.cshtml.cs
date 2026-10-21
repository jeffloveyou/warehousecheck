using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Errors;
using Warehouse.Application.MasterData;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.MasterData;

/// <summary>計量單位維護（物料管理員）：清單＋同頁新增／修改表單（?edit=單位ID）＋停用／啟用。</summary>
public class UnitsModel(IMasterDataService masterData) : PageModel
{
    public IReadOnlyList<UnitRow> Units { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = "edit")]
    public Guid? EditId { get; set; }

    [BindProperty]
    public UnitForm Input { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    public bool EditingInUse { get; private set; }

    public string? Error { get; private set; }

    public sealed class UnitForm
    {
        [Required(ErrorMessage = "請輸入代碼。")]
        [Display(Name = "代碼")]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入名稱。")]
        [Display(Name = "名稱")]
        public string Name { get; set; } = string.Empty;

        [Range(0, 4, ErrorMessage = "小數位數必須介於 0～4。")]
        [Display(Name = "小數位數")]
        public int DecimalPlaces { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Units = await masterData.ListUnitsAsync(cancellationToken);
        if (EditId is { } id && Units.FirstOrDefault(u => u.Id == id) is { } row)
        {
            Input = new UnitForm { Code = row.Code, Name = row.Name, DecimalPlaces = row.DecimalPlaces };
            Version = row.Version;
            EditingInUse = row.IsInUse;
        }
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            if (!ModelState.IsValid)
            {
                return false;
            }
            var input = new UnitInput(Input.Code, Input.Name, Input.DecimalPlaces);
            if (EditId is { } id)
            {
                await masterData.UpdateUnitAsync(id, Version, input, cancellationToken);
                TempData["Message"] = $"已修改單位 {Input.Code}。";
            }
            else
            {
                await masterData.CreateUnitAsync(input, cancellationToken);
                TempData["Message"] = $"已新增單位 {Input.Code}。";
            }
            return true;
        }, cancellationToken);

    public Task<IActionResult> OnPostToggleAsync(Guid id, uint version, bool isActive, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await masterData.SetUnitActiveAsync(id, version, isActive, cancellationToken);
            TempData["Message"] = isActive ? "已啟用單位。" : "已停用單位。";
            return true;
        }, cancellationToken);

    private async Task<IActionResult> RunAsync(Func<Task<bool>> action, CancellationToken cancellationToken)
    {
        try
        {
            if (await action())
            {
                return RedirectToPage();
            }
        }
        catch (Exception ex) when (ex is DomainRuleException or AppException)
        {
            Error = ex.Message;
        }
        Units = await masterData.ListUnitsAsync(cancellationToken);
        return Page();
    }
}
