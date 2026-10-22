using SkiaSharp;

// PWA 圖示（M5-02）：深藍底、白色條碼線條與倉庫屋頂，不用字型（各平台都畫得一樣）。
// 用法：dotnet run --project tools/IconGen —— 覆寫 src/Warehouse.Web/wwwroot/icons/*.png。
// maskable 版本把圖案縮到中間 70%（Android 會裁成圓形或圓角方形，安全區域是中間 80%）。
var root = FindRepoRoot();
var output = Path.Combine(root, "src", "Warehouse.Web", "wwwroot", "icons");
Directory.CreateDirectory(output);

Draw("icon-192.png", 192, 0.86f, rounded: true);
Draw("icon-512.png", 512, 0.86f, rounded: true);
Draw("icon-maskable-512.png", 512, 0.70f, rounded: false);
Draw("apple-touch-icon.png", 180, 0.80f, rounded: false);
Draw("favicon-32.png", 32, 0.92f, rounded: true);
Console.WriteLine("已產生圖示到 " + output);

void Draw(string name, int size, float scale, bool rounded)
{
    using var bitmap = new SKBitmap(size, size);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    using var background = new SKPaint { Color = new SKColor(0x24, 0x34, 0x47), IsAntialias = true };
    if (rounded)
    {
        canvas.DrawRoundRect(new SKRect(0, 0, size, size), size * 0.18f, size * 0.18f, background);
    }
    else
    {
        canvas.DrawRect(new SKRect(0, 0, size, size), background);
    }

    // 圖案在 100×100 的座標系畫，再縮放到中間
    var unit = size * scale / 100f;
    var offset = size * (1 - scale) / 2f;
    canvas.Translate(offset, offset);
    canvas.Scale(unit);
    using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
    // 屋頂：兩條粗線（不用 SKPath，新版 SkiaSharp 已標為過時）
    using var roof = new SKPaint { Color = SKColors.White, IsAntialias = true, StrokeWidth = 8, StrokeCap = SKStrokeCap.Round, Style = SKPaintStyle.Stroke };
    canvas.DrawLine(14, 36, 50, 14, roof);
    canvas.DrawLine(50, 14, 86, 36, roof);
    // 條碼線條（寬窄交錯）
    var bars = new[] { (20f, 5f), (28f, 3f), (34f, 7f), (44f, 3f), (50f, 5f), (58f, 3f), (64f, 7f), (74f, 3f), (80f, 5f) };
    foreach (var (x, w) in bars)
    {
        canvas.DrawRect(new SKRect(x - 2, 46, x - 2 + w, 84), white);
    }

    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(Path.Combine(output, name));
    data.SaveTo(file);
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Warehouse.slnx")))
        {
            return dir.FullName;
        }
    }
    throw new InvalidOperationException("找不到專案根目錄（Warehouse.slnx）。");
}
