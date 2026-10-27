using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Warehouse.Application.Errors;
using Warehouse.Application.Imports;

namespace Warehouse.Infrastructure.Imports;

/// <summary>
/// 一列資料：工作表中的列號（標題是第 1 列）與「欄名 → 文字」。
/// <see cref="DateColumns"/>：xlsx 中被 Excel 存成日期的欄位（代碼欄若是日期，代表 Excel 自動轉換過，例如 1-2 → 1月2日）。
/// <see cref="NumberColumns"/>：xlsx 中存成數字的欄位（條碼存成數字時，前導零與第 16 位以後的數字已被 Excel 改掉，D-31 審查 🟠-3）。
/// </summary>
public sealed record ImportRow(int Row, Dictionary<string, string> Cells, List<string>? DateColumns = null, List<string>? NumberColumns = null)
{
    public string? Get(string column) =>
        Cells.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public bool IsDate(string column) => DateColumns?.Contains(column) == true;

    public bool IsNumber(string column) => NumberColumns?.Contains(column) == true;
}

/// <summary>
/// 讀取匯入檔（M6-03）：xlsx（ClosedXML，第一個工作表）或 UTF-8 CSV（RFC 4180：雙引號包住含逗號、換行的欄位）。
/// 第一列是標題，只讀到標題最後一個有值的欄（最多 <see cref="MaxColumns"/> 欄）；全空白的列略過；超過列數上限整個檔案拒絕。
/// 安全：xlsx 先檢查壓縮內容（項目數、實際解壓總量），避免小檔案解壓成數百 MB（M6-03b 審查 🟠-1）；
/// 公式儲存格要求貼上值；儲存格取 Excel 顯示的文字（保留 000123 這類前導零，審查 🟠-2）。
/// CSV 一律以嚴格 UTF-8 解碼：台灣 Excel「另存 CSV」預設是 Big5，遇到無效位元組就拒絕並提示改存「CSV UTF-8」。
/// </summary>
public static class ImportFileReader
{
    public const int MaxColumns = 64;
    private const int MaxZipEntries = 100;
    private const long MaxUncompressedBytes = 50L * 1024 * 1024;

    public static (IReadOnlyList<string> Headers, IReadOnlyList<ImportRow> Rows) Read(string fileName, byte[] content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var (table, dates, numbers) = extension switch
        {
            ".xlsx" => ReadXlsx(content),
            ".csv" => (ReadCsv(content), new Dictionary<int, List<int>>(), new Dictionary<int, List<int>>()),
            _ => throw BadFile("只接受 .xlsx 或 .csv 檔。"),
        };
        if (table.Count == 0)
        {
            throw BadFile("檔案是空的（第一列要是標題）。");
        }
        var headers = table[0].Select(h => h.Trim()).ToList();
        var duplicate = headers.Where(h => h.Length > 0).GroupBy(h => h).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw BadFile($"標題「{duplicate.Key}」重複。");
        }
        var rows = new List<ImportRow>();
        for (var i = 1; i < table.Count; i++)
        {
            var cells = table[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            if (rows.Count >= IImportService.MaxRows)
            {
                throw TooLarge();
            }
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < headers.Count && c < cells.Count; c++)
            {
                if (headers[c].Length > 0)
                {
                    dict[headers[c]] = cells[c];
                }
            }
            rows.Add(new ImportRow(i + 1, dict, ColumnsOf(dates, i, headers), ColumnsOf(numbers, i, headers)));
        }
        return (headers, rows);
    }

    private static List<string>? ColumnsOf(Dictionary<int, List<int>> cells, int row, List<string> headers) =>
        cells.TryGetValue(row, out var list) && list.Where(c => c < headers.Count).Select(c => headers[c]).ToList() is { Count: > 0 } names
            ? names : null;

    /// <summary>xlsx 是 zip：逐項實際解壓計數（不只相信宣告的長度），超過上限就拒絕，再交給 ClosedXML。</summary>
    private static void CheckZip(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries)
            {
                throw BadFile("Excel 檔內容太複雜，請只保留一個工作表、另存新檔後再上傳。");
            }
            long total = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxUncompressedBytes)
                    {
                        throw BadFile("Excel 檔解壓後太大（超過 50 MB），請分成多個檔案。");
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            throw BadFile("無法讀取這個 Excel 檔，請確認是 .xlsx 格式（不是 .xls），且沒有設定密碼。");
        }
    }

    private static (List<List<string>> Table, Dictionary<int, List<int>> Dates, Dictionary<int, List<int>> Numbers) ReadXlsx(byte[] content)
    {
        CheckZip(content);
        try
        {
            using var stream = new MemoryStream(content);
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.First();
            var used = sheet.RangeUsed();
            if (used is null)
            {
                return ([], [], []);
            }
            var lastRow = used.LastRow().RowNumber();
            if (lastRow > IImportService.MaxRows + 1000)
            {
                throw TooLarge();
            }
            // 只讀到標題列最後一個有值的欄：A1 與 XFD1 都有值時，不會變成 16384 欄 × 每列的巨大表格
            var lastColumn = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
            if (lastColumn > MaxColumns)
            {
                throw BadFile($"標題超過 {MaxColumns} 欄，請使用範本的欄位。");
            }
            var table = new List<List<string>>(lastRow);
            var dates = new Dictionary<int, List<int>>();
            var numbers = new Dictionary<int, List<int>>();
            for (var r = 1; r <= lastRow; r++)
            {
                var row = new List<string>(lastColumn);
                for (var c = 1; c <= lastColumn; c++)
                {
                    var cell = sheet.Cell(r, c);
                    if (cell.HasFormula)
                    {
                        throw BadFile($"第 {r} 列第 {c} 欄是公式，請複製後「貼上值」再上傳。");
                    }
                    if (cell.DataType == XLDataType.DateTime)
                    {
                        row.Add(cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        (dates.TryGetValue(r - 1, out var list) ? list : dates[r - 1] = []).Add(c - 1);
                    }
                    else
                    {
                        if (cell.DataType == XLDataType.Number)
                        {
                            (numbers.TryGetValue(r - 1, out var list) ? list : numbers[r - 1] = []).Add(c - 1);
                        }
                        row.Add(cell.GetFormattedString());
                    }
                }
                table.Add(row);
            }
            return (table, dates, numbers);
        }
        catch (Exception ex) when (ex is not AppException)
        {
            throw BadFile("無法讀取這個 Excel 檔，請確認是 .xlsx 格式（不是 .xls），且沒有設定密碼。");
        }
    }

    private static List<List<string>> ReadCsv(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw BadFile("CSV 不是 UTF-8 編碼（台灣 Excel 預設另存的 CSV 是 Big5）。請在 Excel 選「另存新檔 → CSV UTF-8（逗號分隔）」，或直接上傳 .xlsx。");
        }
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }
        var table = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch < ' ' && ch is not ('\t' or '\r' or '\n'))
            {
                throw BadFile($"CSV 第 {table.Count + 1} 列含有控制字元，請檢查檔案。");
            }
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }
            switch (ch)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Count > MaxColumns)
                    {
                        throw BadFile($"CSV 第 {table.Count + 1} 列超過 {MaxColumns} 欄。");
                    }
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    table.Add(row);
                    row = [];
                    if (table.Count > IImportService.MaxRows + 1000)
                    {
                        throw TooLarge();
                    }
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }
        if (quoted)
        {
            throw BadFile("CSV 有未結束的雙引號，請檢查檔案。");
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            table.Add(row);
        }
        return table;
    }

    private static ConflictException TooLarge() =>
        new("import.too_large", $"資料超過 {IImportService.MaxRows:N0} 列，請分成多個檔案。");

    private static ConflictException BadFile(string message) => new("import.bad_file", message);
}
