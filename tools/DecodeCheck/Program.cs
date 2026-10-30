using System.Text.Json;
using Microsoft.Playwright;

// 相機解碼實測（docs/04 §6.2、docs/05 T-CAM-1D）：不需要資料庫與網站。
// 用 Playwright 攔截 https://decode.test/ 的請求，直接回傳 src/Warehouse.Web/wwwroot 的檔案（與正式網站同一份 camera-decoder.js、zxing-wasm）。
// 用法：
//   dotnet run --project tools/DecodeCheck -- generate <zxing-wasm 套件資料夾>   用 zxing-wasm writer（不進專案）產生 barcodes/*.png 測試圖（只需做一次，圖已進 Git）
//   dotnet run --project tools/DecodeCheck                                        Chromium 109 解碼各碼制、前導零、旋轉、多碼；列出耗時
var root = FindRepoRoot();
var wwwroot = Path.Combine(root, "src", "Warehouse.Web", "wwwroot");
var barcodeDir = Path.Combine(root, "tools", "DecodeCheck", "barcodes");
var chrome109 = Path.Combine(root, "poc", ".browsers", "chromium-109", "chrome-win", "chrome.exe");
const string Origin = "https://decode.test";

// 承諾的第一批碼制（計畫 §4.2）；文字含前導零，比對時一律當字串
var cases = new (string Name, string Format, string Text)[]
{
    ("qr", "QRCode", "WH:ITEM:EL-000125"),
    ("qr-location", "QRCode", "WH:LOC:MAIN:A-01-02"),
    ("ean13", "EAN13", WithCheckDigit("471234567890")),
    ("ean13-leading-zero", "EAN13", WithCheckDigit("003600029145")),
    ("ean8", "EAN8", WithCheckDigit("9638507")),
    ("upca", "UPCA", WithCheckDigit("03600029145")),
    ("upce", "UPCE", "01234565"),
    ("code128", "Code128", "B12-abc-0009"),
    ("code39", "Code39", "LOC-A01"),
    ("code93", "Code93", "CODE93-TEST"),
    ("itf", "ITF", "12345678901231"),
    ("codabar", "Codabar", "A40156B"),
};

using var pw = await Playwright.CreateAsync();
if (args.Length >= 2 && args[0] == "generate")
{
    var package = args[1];
    Directory.CreateDirectory(barcodeDir);
    await using var modern = await pw.Chromium.LaunchAsync(new() { Headless = true });
    var page = await OpenAsync(modern, new Dictionary<string, string>
    {
        ["/writer/index.js"] = Path.Combine(package, "dist", "iife", "writer", "index.js"),
        ["/writer/zxing_writer.wasm"] = Path.Combine(package, "dist", "writer", "zxing_writer.wasm"),
    });
    var written = await page.EvaluateAsync<JsonElement>("""
        async (cases) => {
          await new Promise((res, rej) => { const s = document.createElement('script'); s.src = '/writer/index.js'; s.onload = res; s.onerror = rej; document.head.appendChild(s); });
          await ZXingWASM.prepareZXingModule({ overrides: { locateFile: (p, pre) => p.endsWith('.wasm') ? '/writer/' + p : pre + p }, fireImmediately: true });
          const out = [];
          for (const c of cases) {
            const r = await ZXingWASM.writeBarcode(c.text, { format: c.format, scale: 3 });
            if (r.error) { out.push({ name: c.name, error: r.error }); continue; }
            const bytes = new Uint8Array(await r.image.arrayBuffer());
            let bin = ''; for (const b of bytes) bin += String.fromCharCode(b);
            out.push({ name: c.name, png: btoa(bin) });
          }
          return out;
        }
        """, cases.Select(c => new { name = c.Name, format = c.Format, text = c.Text }).ToArray());
    foreach (var w in written.EnumerateArray())
    {
        var name = w.GetProperty("name").GetString()!;
        if (w.TryGetProperty("error", out var error)) { Console.WriteLine($"  ✘ {name}：{error}"); continue; }
        await File.WriteAllBytesAsync(Path.Combine(barcodeDir, name + ".png"), Convert.FromBase64String(w.GetProperty("png").GetString()!));
        Console.WriteLine($"  ✔ {name}.png");
    }
    return 0;
}

// 除錯：dotnet run --project tools/DecodeCheck -- raw <圖檔名> [ReaderOptions JSON]，印出 zxing-wasm 原始結果（原圖大小，不經 camera-decoder.js）
if (args.Length >= 2 && args[0] == "raw")
{
    await using var browser = await pw.Chromium.LaunchAsync(new() { ExecutablePath = chrome109, Headless = true });
    var page = await OpenAsync(browser, new());
    var raw = await page.EvaluateAsync<string>("""
        async ({ file, options }) => {
          await new Promise((res, rej) => { const s = document.createElement('script'); s.src = '/lib/zxing-wasm/zxing-reader.js'; s.onload = res; s.onerror = rej; document.head.appendChild(s); });
          await ZXingWASM.prepareZXingModule({ overrides: { locateFile: (p, pre) => p.endsWith('.wasm') ? '/lib/zxing-wasm/' + p : pre + p }, fireImmediately: true });
          const blob = await (await fetch('/barcodes/' + file)).blob();
          const results = await ZXingWASM.readBarcodes(blob, JSON.parse(options));
          return JSON.stringify(results.map(r => ({ text: r.text, format: r.format, symbology: r.symbology, isValid: r.isValid, error: r.error })));
        }
        """, new { file = args[1], options = args.Length > 2 ? args[2] : "{}" });
    Console.WriteLine(raw);
    return 0;
}

// 解碼：每張圖放到 1280×720 白底（條碼約占畫面寬 45%，模擬手機鏡頭距離），再交給 camera-decoder.js（縮到 640 px）
var decodeCases = new List<object>();
foreach (var c in cases)
{
    decodeCases.Add(new { name = c.Name, files = new[] { c.Name + ".png" }, rotate = 0, expect = ZxingText(c.Name, c.Text) });
}
decodeCases.Add(new { name = "ean13 旋轉 90°", files = new[] { "ean13.png" }, rotate = 90, expect = cases.Single(c => c.Name == "ean13").Text });
decodeCases.Add(new { name = "code128 旋轉 180°", files = new[] { "code128.png" }, rotate = 180, expect = cases.Single(c => c.Name == "code128").Text });
decodeCases.Add(new { name = "qr 傾斜 8°", files = new[] { "qr.png" }, rotate = 8, expect = cases.Single(c => c.Name == "qr").Text });
decodeCases.Add(new { name = "多碼：ean13＋code39", files = new[] { "ean13.png", "code39.png" }, rotate = 0, expect = "(multiple)" });
decodeCases.Add(new { name = "空白畫面", files = Array.Empty<string>(), rotate = 0, expect = "(none)" });

var failures = 0;
foreach (var (label, browserPath, prefer) in new[] { ("Chromium 109", chrome109, "zxing"), ("Chromium 109 自動選擇", chrome109, "") })
{
    if (!File.Exists(browserPath)) { Console.WriteLine($"✘ 找不到 {browserPath}（見 docs/04 §12 下載 Chromium 109 快照）"); return 1; }
    await using var browser = await pw.Chromium.LaunchAsync(new() { ExecutablePath = browserPath, Headless = true });
    var page = await OpenAsync(browser, new());
    var version = await page.EvaluateAsync<string>("() => navigator.userAgent");
    var transfer = new List<long>();
    page.RequestFinished += async (_, r) => { if (r.Url.Contains("/lib/zxing-wasm/")) transfer.Add((await r.SizesAsync()).ResponseBodySize); };
    var report = await page.EvaluateAsync<JsonElement>("""
        async ({ cases, prefer }) => {
          const mod = await import('/js/camera-decoder.js');
          const t0 = performance.now();
          const decoder = await mod.createDecoder(prefer ? { prefer } : undefined);
          const initMs = performance.now() - t0;
          const load = (f) => new Promise((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = '/barcodes/' + f; });
          const results = [];
          for (const c of cases) {
            const imgs = await Promise.all(c.files.map(load));
            const canvas = document.createElement('canvas'); canvas.width = 1280; canvas.height = 720;
            const ctx = canvas.getContext('2d'); ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, 1280, 720);
            imgs.forEach((img, i) => {
              const w = 1280 * (imgs.length > 1 ? 0.4 : 0.45), h = w * img.height / img.width;
              const cx = imgs.length > 1 ? 1280 * (i === 0 ? 0.27 : 0.73) : 640;
              ctx.save(); ctx.translate(cx, 360); ctx.rotate(c.rotate * Math.PI / 180);
              ctx.drawImage(img, -w / 2, -h / 2, w, h); ctx.restore();
            });
            const s = performance.now();
            let r; try { r = await decoder.decode(canvas); } catch (e) { r = { error: String(e) }; }
            results.push({ name: c.name, expect: c.expect, result: r, ms: Math.round(performance.now() - s) });
          }
          return { decoder: decoder.name, initMs: Math.round(initMs), native: 'BarcodeDetector' in window, results };
        }
        """, new { cases = decodeCases, prefer });
    Console.WriteLine($"== {label}（{version}）");
    Console.WriteLine($"   解碼器：{report.GetProperty("decoder")}；內建 BarcodeDetector：{report.GetProperty("native")}；初始化 {report.GetProperty("initMs")} ms；zxing 下載 {transfer.Sum() / 1024} KB");
    foreach (var r in report.GetProperty("results").EnumerateArray())
    {
        var expect = r.GetProperty("expect").GetString();
        var result = r.GetProperty("result");
        var got = result.ValueKind == JsonValueKind.Null ? "(none)"
            : result.TryGetProperty("multiple", out _) ? "(multiple)"
            : result.TryGetProperty("error", out var err) ? "錯誤 " + err
            : result.GetProperty("text").GetString() + "｜" + result.GetProperty("format").GetString();
        var ok = got == expect || got.StartsWith(expect + "｜", StringComparison.Ordinal);
        if (!ok) failures++;
        Console.WriteLine($"   {(ok ? "✔" : "✘")} {r.GetProperty("name")}：{got}（預期 {expect}，{r.GetProperty("ms")} ms）");
    }
}
Console.WriteLine(failures == 0 ? "全部通過" : $"失敗 {failures} 項");
return failures == 0 ? 0 : 1;

// 開一頁空白頁，https://decode.test/ 下的 /js、/lib 取自 wwwroot，/barcodes 取自測試圖，extra 指定其他檔案
async Task<IPage> OpenAsync(IBrowser browser, Dictionary<string, string> extra)
{
    var context = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
    var page = await context.NewPageAsync();
    page.PageError += (_, e) => Console.WriteLine("   頁面錯誤：" + e);
    await context.RouteAsync(Origin + "/**", async route =>
    {
        var path = new Uri(route.Request.Url).AbsolutePath;
        string? file = extra.TryGetValue(path, out var mapped) ? mapped
            : path == "/" ? null
            : path.StartsWith("/barcodes/", StringComparison.Ordinal) ? Path.Combine(barcodeDir, path["/barcodes/".Length..])
            : Path.Combine(wwwroot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (file is null) { await route.FulfillAsync(new() { ContentType = "text/html", Body = "<!doctype html><title>decode</title>" }); return; }
        if (!File.Exists(file)) { await route.FulfillAsync(new() { Status = 404 }); return; }
        var type = Path.GetExtension(file) switch { ".js" => "text/javascript", ".wasm" => "application/wasm", ".png" => "image/png", _ => "application/octet-stream" };
        await route.FulfillAsync(new() { ContentType = type, BodyBytes = await File.ReadAllBytesAsync(file) });
    });
    await page.GotoAsync(Origin + "/");
    return page;
}

// zxing-wasm 3.x 把 UPC-A、UPC-E 一律輸出成 13 碼（GTIN-13，前面補 0；UPC-E 先展開成 UPC-A）。
// Android 內建 BarcodeDetector 與多數條碼槍輸出 12 碼（UPC-A）／8 碼（UPC-E）。依使用者決定（計畫 4A）不在前端改寫，只記錄差異（docs/05）。
static string ZxingText(string name, string text) => name switch
{
    "upca" => "0" + text,
    "upce" => "0012345000065",
    _ => text,
};

// EAN／UPC 檢查碼（奇偶位權重 3／1，從右邊數）
static string WithCheckDigit(string digits)
{
    var sum = 0;
    for (var i = 0; i < digits.Length; i++)
    {
        var d = digits[digits.Length - 1 - i] - '0';
        sum += i % 2 == 0 ? d * 3 : d;
    }
    return digits + (10 - sum % 10) % 10;
}

/// <summary>往上找含 Warehouse.slnx 的資料夾（不寫死安裝路徑）。</summary>
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
