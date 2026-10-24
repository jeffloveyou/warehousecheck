using System.Globalization;
using System.Text;
using QRCoder;
using Warehouse.Application.Abstractions;

namespace Warehouse.Infrastructure.Labels;

/// <summary>
/// <see cref="IQrCodeRenderer"/> 實作：QRCoder 1.6.0（MIT；net6.0 以上沒有相依套件）只用來編碼，
/// SVG 由模組矩陣自己組（同一列連續的黑格合併成一個矩形），不經過 System.Drawing，Windows／Linux 結果相同。
/// 一個儲位碼約 25×25 格（含靜區 33×33），SVG 約 1～2 KB。
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
