using SkiaSharp;
using Warehouse.Domain;

namespace Warehouse.Infrastructure.Files;

/// <summary>重新編碼後的圖片（JPEG）。</summary>
public sealed record ProcessedImage(byte[] Large, int Width, int Height, byte[] Thumb);

/// <summary>
/// 用 SkiaSharp（MIT）在伺服器端產生大圖與縮圖（M3-08，使用者 2026-10-22 決定）：
/// 1. 依檔頭判斷格式，只收 JPEG、PNG、WebP（副檔名、Content-Type 都不信任）。
/// 2. 解碼前先看寬高，超過 4000 萬像素拒絕（避免「解壓縮炸彈」吃光記憶體）。
/// 3. 縮小取樣解碼：JPEG 可以直接解成 1/2、1/4、1/8 大小，手機 48MP 照片不必整張解開（審查 O1）。
/// 4. 依 EXIF 方向轉正（8 種都處理），縮到長邊 1600 與 320，鋪白底後重新編碼成 JPEG（品質 85／80）；
///    透明 PNG 不會變黑底，EXIF（含 GPS）也不會保留。
/// 解碼仍可能用到上百 MB 記憶體，呼叫端要限制同時處理的張數（<see cref="Warehousing.LocationImageService"/>）。
/// </summary>
public static class ImageProcessor
{
    public const int LargeEdge = 1600;
    public const int ThumbEdge = 320;
    private const long MaxPixels = 40_000_000;

    public static ProcessedImage Process(byte[] content)
    {
        if (!HasAllowedSignature(content))
        {
            throw new DomainRuleException("只接受 JPEG、PNG 或 WebP 圖片。");
        }
        using var codec = SKCodec.Create(new SKMemoryStream(content))
            ?? throw new DomainRuleException("圖片無法讀取，可能已損壞。");
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
        {
            throw new DomainRuleException("只接受 JPEG、PNG 或 WebP 圖片。");
        }
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            throw new DomainRuleException("圖片解析度太大（超過 4000 萬像素），請先縮小再上傳。");
        }

        using var decoded = Decode(codec);
        using var upright = ApplyOrientation(decoded, codec.EncodedOrigin);
        var source = upright ?? decoded;
        var (large, width, height) = Encode(source, LargeEdge, 85);
        var (thumb, _, _) = Encode(source, ThumbEdge, 80);
        return new ProcessedImage(large, width, height, thumb);
    }

    /// <summary>檔頭（magic bytes）：JPEG FF D8 FF、PNG 89 50 4E 47、WebP "RIFF....WEBP"。</summary>
    public static bool HasAllowedSignature(ReadOnlySpan<byte> b) =>
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        || (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        || (b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P');

    /// <summary>
    /// 解成 8 位元 RGBA（16 位元 PNG 也一樣，記憶體不會加倍）。格式支援時直接解成接近長邊 1600 的大小；
    /// 不支援縮小解碼的格式（PNG）會回傳原尺寸。
    /// </summary>
    private static SKBitmap Decode(SKCodec codec)
    {
        // 由小到大試 1/8、2/8 … 8/8，取第一個長邊仍 ≥ 1600 的尺寸（不支援的比例 SkiaSharp 會回傳原尺寸）
        var size = codec.Info.Size;
        for (var n = 1; n < 8; n++)
        {
            var scaled = codec.GetScaledDimensions(n / 8f);
            if (scaled.Width >= 1 && scaled.Height >= 1 && Math.Max(scaled.Width, scaled.Height) >= LargeEdge)
            {
                size = scaled;
                break;
            }
        }
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new DomainRuleException("圖片無法讀取，可能已損壞。");
        }
        return bitmap;
    }

    /// <summary>縮放後畫在白底上再編碼：透明區變白色，輸出一律不透明 JPEG。</summary>
    private static (byte[] Bytes, int Width, int Height) Encode(SKBitmap source, int maxEdge, int quality)
    {
        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new DomainRuleException("圖片縮放失敗。");
        using var opaque = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(opaque))
        {
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(resized);
            canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        }
        using var encoded = SKImage.FromBitmap(opaque);
        using var data = encoded.Encode(SKEncodedImageFormat.Jpeg, quality);
        return (data.ToArray(), width, height);
    }

    /// <summary>
    /// 手機照片常把方向記在 EXIF；依方向轉成正向，重新編碼後就不需要 EXIF。
    /// 正向（TopLeft）回傳 null，直接用原圖，不多複製一份（審查 O1）。
    /// 矩陣把原圖座標 (x, y) 對應到輸出座標；w、h 是原圖寬高。
    /// </summary>
    private static SKBitmap? ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        float w = bitmap.Width, h = bitmap.Height;
        SKMatrix? matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // 水平鏡像
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 轉 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // 垂直鏡像
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // 沿主對角線翻轉
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 順時針 90°
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // 沿副對角線翻轉
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // 逆時針 90°
            _ => null,
        };
        if (matrix is not { } m)
        {
            return null;
        }
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var rotated = new SKBitmap(new SKImageInfo(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        using var source = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(source, 0, 0, SKSamplingOptions.Default); // 90° 倍數旋轉與鏡像是逐像素對應，不需要插值
        return rotated;
    }
}