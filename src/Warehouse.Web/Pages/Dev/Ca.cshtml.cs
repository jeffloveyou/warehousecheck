using Microsoft.AspNetCore.Mvc;

namespace Warehouse.Web.Pages.Dev;

/// <summary>/dev/ca：讓手機下載開發用根 CA（scripts/new-dev-certs.ps1 產生）。</summary>
public class CaModel(IWebHostEnvironment environment, IConfiguration configuration) : DevPageModel(environment)
{
    private string RootCaPath => configuration["DevCerts:RootCaPath"]
        ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Warehouse", "certs", "warehouse-dev-root-ca.cer");

    public bool RootCaExists { get; private set; }

    public void OnGet() => RootCaExists = System.IO.File.Exists(RootCaPath);

    // GET /dev/ca?handler=download
    public IActionResult OnGetDownload()
    {
        if (!System.IO.File.Exists(RootCaPath))
        {
            return NotFound();
        }
        // application/x-x509-ca-cert：iPhone Safari 會直接提示安裝描述檔，Android 會下載成檔案
        return PhysicalFile(RootCaPath, "application/x-x509-ca-cert", "warehouse-dev-root-ca.cer");
    }
}
