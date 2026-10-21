namespace Warehouse.Web.Pages.Dev;

/// <summary>/dev/camera：M0-05 相機掃碼可行性測試頁（HTTPS、權限、解碼、釋放相機）。</summary>
public class CameraModel(IWebHostEnvironment environment) : DevPageModel(environment)
{
    public void OnGet()
    {
    }
}
