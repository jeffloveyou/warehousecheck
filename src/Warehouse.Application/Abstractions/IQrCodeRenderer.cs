namespace Warehouse.Application.Abstractions;

/// <summary>
/// QR Code 轉成 SVG（M7-03 儲位標籤）。伺服器產生、直接嵌在頁面：瀏覽器不必載入任何程式，列印時是向量圖不會糊。
/// </summary>
public interface IQrCodeRenderer
{
    /// <summary>
    /// 回傳完整的 &lt;svg&gt; 元素（含 4 格靜區；寬高 100%，由外層決定實際尺寸）。
    /// 容錯等級 M（約 15%，標籤小污損仍可讀）。
    /// </summary>
    string RenderSvg(string content);
}
