using System.Globalization;
using System.Text;
using QRCoder;
using Warehouse.Application.Abstractions;

namespace Warehouse.Infrastructure.Labels;

/// <summary>
/// <see cref="IQrCodeRenderer"/> 實作：QRCoder 1.6.0（MIT；net6.0 以上沒有相依套件）只用來編碼，
/// SVG 由模組矩陣自己組（同一列連續的黑格合併成一個矩形），不經過 System.Drawing，Windows／Linux 結果相同。
/// 實測 SVG 1.8～2.4 KB（小標籤一頁 96 個約 230 KB）。
/// 頁面以 Html.Raw 輸出：SVG 只能含數字座標，**不可加入任何未編碼的文字**（例如 &lt;title&gt;儲位碼），否則會變成 XSS。
/// </summary>
public sealed class QrSvgRenderer : IQrCodeRenderer
{
    public string RenderSvg(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix; // 已含四邊各 4 格靜區
        var size = matrix.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            var row = matrix[y];
            for (var x = 0; x < size; x++)
            {
                if (!row[x])
                {
                    continue;
                }

                var start = x;
                while (x + 1 < size && row[x + 1])
                {
                    x++;
                }

                path.Append(CultureInfo.InvariantCulture, $"M{start} {y}h{x - start + 1}v1h-{x - start + 1}z");
            }
        }

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" width=\"100%\" height=\"100%\" "
            + $"shape-rendering=\"crispEdges\" role=\"img\"><rect width=\"{size}\" height=\"{size}\" fill=\"#fff\"/>"
            + $"<path d=\"{path}\" fill=\"#000\"/></svg>";
    }
}
