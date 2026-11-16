using System.Text;
using Microsoft.Playwright;

internal static class ImportDuplicateCheck
{
    public static async Task RunAsync(IPage page, string baseUrl, Guid warehouse, string itemCode, string suffix, Action<bool, string> check)
    {
        const string locations = "儲位碼,名稱\nA-01,新名稱\nB-01,新儲位";
        await Upload("Locations", locations, "ReportError");
        var batchUrl = page.Url;
        check(await page.Locator("#import-commit").CountAsync() == 0, "儲位：預設標記異常會擋住同碼");
        await Recalculate("SkipExact");
        check((await page.InnerTextAsync("#import-item-summary")).Contains("略過 1"), "儲位：同批重算可略過同碼");
        await Recalculate("UpdateExisting");
        check((await page.InnerTextAsync("#import-item-summary")).Contains("更新 1"), "儲位：可改選更新既有儲位");
        if (Environment.GetEnvironmentVariable("WAREHOUSE_LEGACY_IMPORT_URL") is { Length: > 0 } legacy)
            await CheckLegacy(legacy, batchUrl);
        await page.ClickAsync("#import-commit");
        check((await page.InnerTextAsync("#page-message")).Contains("已匯入 2 列"), "儲位：確認新增與更新");
        await Upload("Locations", locations, "SkipExact");
        check((await page.InnerTextAsync("#import-previous")).Contains("上次實際處理 2 列")
            && await page.IsVisibleAsync("#import-commit"), "重傳：顯示同一檔案上次結果，仍可確認");

        var indexes = $"料號,類型,文字\n{itemCode},關鍵字,kw-{suffix}\n{itemCode},關鍵字,kw-{suffix}";
        await Upload("Indexes", indexes, "ReportError");
        check(await page.Locator("#import-commit").CountAsync() == 0, "索引：同檔重複預設為異常");
        await Recalculate("SkipExact");
        var summary = await page.InnerTextAsync("#import-item-summary");
        check(summary.Contains("新增 1") && summary.Contains("略過 1"), "索引：略過同檔相同索引");
        var report = await ReadCsv();
        check(report.Contains("本檔第 2 列") && report.Contains(itemCode), "索引：CSV 包含來源列及對應料號");
        await page.ClickAsync("#import-commit");

        await page.GotoAsync(baseUrl + "/Opening");
        await page.SelectOptionAsync("#opening-create-form select[name='WarehouseId']", warehouse.ToString());
        await page.ClickAsync("#opening-create");
        await page.ClickAsync("#opening-import-link");
        await SetFile($"儲位,料號,數量\nA-01,{itemCode},5\nA-01,{itemCode},7");
        await page.ClickAsync("#import-upload");
        await page.WaitForSelectorAsync("#import-summary");
        var opening = await ReadCsv();
        check(opening.Contains("草稿第 1 行數量 5，本列數量 7") && opening.Contains("本批較早的試算列")
            && await page.Locator("#import-commit").CountAsync() == 0, "期初：CSV 顯示衝突數量且禁止確認");

        async Task Upload(string kind, string data, string mode)
        {
            await page.GotoAsync(baseUrl + "/Import?Kind=" + kind);
            if (kind == "Locations") await page.SelectOptionAsync("#Target", warehouse.ToString());
            await page.SelectOptionAsync("#DuplicateMode", mode);
            await SetFile(data);
            await page.ClickAsync("#import-upload");
            await page.WaitForSelectorAsync("#import-item-summary");
        }

        Task SetFile(string data) => page.Locator("#Upload").SetInputFilesAsync(new FilePayload
        {
            Name = "duplicates.csv", MimeType = "text/csv", Buffer = Encoding.UTF8.GetBytes(data),
        });

        async Task Recalculate(string mode)
        {
            await page.SelectOptionAsync("#DuplicateMode", mode);
            await page.ClickAsync("#import-recalculate");
            await page.WaitForSelectorAsync("#import-item-summary");
        }

        async Task<string> ReadCsv()
        {
            var url = await page.EvalOnSelectorAsync<string>("#import-issues-download", "a => a.href");
            var response = await page.Context.APIRequest.GetAsync(url);
            return await response.TextAsync();
        }

        async Task CheckLegacy(string origin, string current)
        {
            var old = await page.Context.NewPageAsync();
            try
            {
                await old.GotoAsync(origin + new Uri(current).AbsolutePath);
                var rejected = await old.EvaluateAsync<bool>("""
                    async () => {
                        const token = document.querySelector('[name=__RequestVerificationToken]');
                        const body = new URLSearchParams({ __RequestVerificationToken: token.value });
                        const result = await fetch(location.pathname + '?handler=Commit', { method: 'POST', body });
                        return !result.redirected && (await result.text()).includes('這個檔案有錯誤，不能匯入');
                    }
                    """);
                check(rejected, "相容性：舊版直接確認新版儲位批次仍被阻擋");
                await old.GotoAsync(origin + "/Import?Kind=Locations&Target=" + warehouse);
                var uploadBlocked = await old.EvaluateAsync<bool>("""
                    async data => {
                        const form = new FormData(document.getElementById('import-form'));
                        form.set('DuplicateMode', 'UpdateExisting');
                        form.set('Upload', new File([data], 'legacy-locations.csv', { type: 'text/csv' }));
                        const response = await fetch('/Import/DuplicatePlan?handler=ItemPlan', { method: 'POST', body: form });
                        return response.status === 404 && !response.redirected;
                    }
                    """, locations);
                check(uploadBlocked, "相容性：舊版不存在新版儲位與索引上傳路徑");
            }
            finally { await old.CloseAsync(); }
        }
    }
}
