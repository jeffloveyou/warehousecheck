using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Warehouse.Application.Errors;
using Warehouse.Application.MasterData;
using Warehouse.Domain;

namespace Warehouse.Web.Pages.MasterData;

/// <summary>分類維護（物料管理員）：清單＋同頁新增／修改表單（?edit=分類ID）＋停用／啟用。</summary>
public class CategoriesModel(IMasterDataService masterData) : PageModel
{
    public IReadOnlyList<CategoryRow> Categories { get; private set; } = [];

    [BindProperty(SupportsGet = true, Name = "edit")]
    public Guid? EditId { get; set; }

    [BindProperty]
    public CategoryForm Input { get; set; } = new();

    [BindProperty]
    public uint Version { get; set; }

    public string? Error { get; private set; }

    public sealed class CategoryForm
    {
        [Display(Name = "上層分類")]
        public Guid? ParentId { get; set; }

        [Required(ErrorMessage = "請輸入代碼。")]
        [Display(Name = "代碼")]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "請輸入名稱。")]
        [Display(Name = "名稱")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "料號前綴")]
        public string? ItemCodePrefix { get; set; }

        [Display(Name = "排序")]
        public int SortOrder { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Categories = await masterData.ListCategoriesAsync(cancellationToken);
        if (EditId is { } id && Categories.FirstOrDefault(c => c.Id == id) is { } row)
        {
            Input = new CategoryForm
            {
                ParentId = row.ParentId, Code = row.Code, Name = row.Name, ItemCodePrefix = row.ItemCodePrefix, SortOrder = row.SortOrder,
            };
            Version = row.Version;
        }
    }

    public Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            if (!ModelState.IsValid)
            {
                return false;
            }
            var input = new CategoryInput(Input.ParentId, Input.Code, Input.Name, Input.ItemCodePrefix, Input.SortOrder);
            if (EditId is { } id)
            {
                await masterData.UpdateCategoryAsync(id, Version, input, cancellationToken);
                TempData["Message"] = $"已修改分類 {Input.Code}。";
            }
            else
            {
                await masterData.CreateCategoryAsync(input, cancellationToken);
                TempData["Message"] = $"已新增分類 {Input.Code}。";
            }
            return true;
        }, cancellationToken);

    public Task<IActionResult> OnPostToggleAsync(Guid id, uint version, bool isActive, CancellationToken cancellationToken) =>
        RunAsync(async () =>
        {
            await masterData.SetCategoryActiveAsync(id, version, isActive, cancellationToken);
            TempData["Message"] = isActive ? "已啟用分類。" : "已停用分類。";
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
        Categories = await masterData.ListCategoriesAsync(cancellationToken);
        return Page();
    }
}
