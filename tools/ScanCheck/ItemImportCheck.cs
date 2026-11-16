using System.Text;
using Microsoft.Playwright;

internal static class ItemImportCheck
{
    public static async Task RunAsync(IPage page, string baseUrl, string suffix, Action<bool, string> check)
    {
        var name = "瀏覽器匯入" + suffix;
        var header = "品名,分類,基本單位,追蹤模式,規格";
        await UploadAsync($"{header}\n{name},EL,PCS,不追蹤,原規格", "KeepPending", autoApprove: true);
        await page.ClickAsync("#import-commit");
        var code = (await page.Locator("#import-item-results tbody tr").First.Locator("td").Nth(2).InnerTextAsync()).Trim();
        check(code.StartsWith("EL-", StringComparison.Ordinal), "物料匯入：自動核准後逐列顯示正式料號");

        await UploadAsync($"{header}\n{name},EL,PCS,不追蹤,原規格\n{name},EL,PCS,不追蹤,另一規格",
            "ReportError", autoApprove: false);
        var url = page.Url;
        check(await page.Locator("#import-commit").CountAsync() == 0, "物料匯入：標記異常時不可確認");
        await RecalculateAsync("UpdateExisting");
        check(await page.Locator("#import-commit").CountAsync() == 0, "物料匯入：疑似重複須指定正式料號才可更新");
        await RecalculateAsync("KeepPending");
        check((await page.InnerTextAsync("#import-item-summary")).Contains("新增 2"), "物料匯入：改選保留待核准後兩列皆可新增");
        await RecalculateAsync("SkipExact");
        var summary = await page.InnerTextAsync("#import-item-summary");
        check(page.Url == url && summary.Contains("新增 1") && summary.Contains("略過 1"), "物料匯入：同批改選後只略過完全相同列");
        var response = await page.Context.APIRequest.GetAsync(await page.EvalOnSelectorAsync<string>("#import-issues-download", "a => a.href"));
        var csv = await response.TextAsync();
        check(response.Ok && csv.Contains("來源列,處理結果") && csv.Contains("略過") && csv.Contains(code),
            "物料匯入：CSV 包含來源列、處理結果及料號");
        if (Environment.GetEnvironmentVariable("WAREHOUSE_LEGACY_IMPORT_URL") is { Length: > 0 } legacy)
            await CheckLegacyAsync(legacy, url);
        await page.ClickAsync("#import-commit");
        check((await page.InnerTextAsync("#page-message")).Contains("已匯入 1 列"), "物料匯入：確認後只新增未略過列");

        await UploadAsync($"正式料號,規格\n{code},調整後規格", "UpdateExisting", autoApprove: false);
        check((await page.InnerTextAsync("#import-item-results")).Contains("原規格") && (await page.InnerTextAsync("#import-item-results")).Contains("調整後規格"),
            "物料匯入：更新先顯示欄位變更前後值");
        await page.ClickAsync("#import-commit");
        check((await page.InnerTextAsync("#page-message")).Contains("已匯入 1 列"), "物料匯入：可按正式料號確認更新");

        async Task UploadAsync(string data, string mode, bool autoApprove)
        {
            await page.GotoAsync(baseUrl + "/Import?Kind=Items");
            await page.SelectOptionAsync("#DuplicateMode", mode);
            if (autoApprove) await page.CheckAsync("#import-auto-approve");
            await page.Locator("#Upload").SetInputFilesAsync(new FilePayload
            {
                Name = "items.csv", MimeType = "text/csv", Buffer = Encoding.UTF8.GetBytes(data),
            });
            await page.ClickAsync("#import-upload");
            await page.WaitForSelectorAsync("#import-item-summary");
        }

        async Task RecalculateAsync(string mode)
        {
            var form = page.Locator("form").Filter(new() { Has = page.Locator("#import-recalculate") });
            var token = form.Locator("[name=PreviewVersion]");
            var original = await token.InputValueAsync();
            await page.SelectOptionAsync("#DuplicateMode", mode);
            await page.ClickAsync("#import-recalculate");
            await page.WaitForFunctionAsync("old => document.querySelector('input[name=PreviewVersion]')?.value !== old", original);
            await token.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        }

        async Task CheckLegacyAsync(string legacy, string currentBatchUrl)
        {
            var old = await page.Context.NewPageAsync();
            try
            {
                await old.GotoAsync(legacy + new Uri(currentBatchUrl).AbsolutePath);
                check(await old.Locator("#import-summary").CountAsync() == 1 && await old.Locator("#import-commit").CountAsync() == 0,
                    "相容性：舊版試算頁阻擋新版待確認批次");
                var rejected = await old.EvaluateAsync<bool>("""
                    async () => {
                        const token = document.querySelector('[name=__RequestVerificationToken]');
                        const form = new URLSearchParams({ __RequestVerificationToken: token.value });
                        const response = await fetch(location.pathname + '?handler=Commit', { method: 'POST', body: form });
                        return !response.redirected && (await response.text()).includes('這個檔案有錯誤，不能匯入');
                    }
                    """);
                check(rejected, "相容性：直接向舊版送出確認也不會匯入新版批次");
                await old.GotoAsync(legacy + "/Import?Kind=Items");
                var ignored = await old.EvaluateAsync<bool>("""
                    async data => {
                        const form = new FormData(document.getElementById('import-form'));
                        form.set('DuplicateMode', 'SkipExact');
                        form.set('Upload', new File([data], 'legacy-guard.csv', { type: 'text/csv' }));
                        const response = await fetch('/Import/ItemPlan?handler=ItemPlan', { method: 'POST', body: form });
                        return response.status === 404 && !response.redirected;
                    }
                    """, $"{header}\n{name},EL,PCS,不追蹤,原規格");
                check(ignored, "相容性：新版上傳入口落到舊版時不會降級成舊匯入");
            }
            finally { await old.CloseAsync(); }
        }
    }
}
