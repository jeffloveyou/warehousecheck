using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Warehouse.Infrastructure.Exports;

/// <summary>
/// 逐列寫出單一工作表的 xlsx（M6-03）。直接以 <see cref="ZipArchiveMode.Create"/> 串流壓縮、XmlWriter 逐列寫入，
/// 記憶體不隨列數增加（OpenXml SDK 需要可讀寫的串流，5 萬筆大型操作日誌時峰值多約 400 MB，M6-03a 審查）。
/// 只寫 Excel 必要的五個檔案（內容類型、關聯、活頁簿、工作表）。文字一律是 inline string（不會被當公式執行），
/// 數字寫成數值，時間轉成台灣時間的文字；第一列是標題並凍結窗格。
/// </summary>
public static class XlsxWriter
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>Excel 單一儲存格最多 32,767 字元，超過時截斷並標記（例如很長的稽核 JSON）。</summary>
    private const int MaxCellLength = 32_767;

    private const string TruncatedMark = "…(截斷)";

    private static readonly TimeZoneInfo Taipei = ResolveTaipei();

    public static async Task WriteAsync(
        Stream output, string sheetName, IReadOnlyList<string> headers, IAsyncEnumerable<object?[]> rows,
        CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Static(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        Static(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Static(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        var workbook = new StringBuilder();
        using (var w = XmlWriter.Create(workbook, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            w.WriteStartElement("workbook", Main);
            w.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            w.WriteStartElement("sheets", Main);
            w.WriteStartElement("sheet", Main);
            w.WriteAttributeString("name", Clean(sheetName));
            w.WriteAttributeString("sheetId", "1");
            w.WriteAttributeString("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId1");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }
        Static(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + workbook);

        var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false) });
        await writer.WriteStartDocumentAsync(true);
        writer.WriteStartElement("worksheet", Main);
        writer.WriteRaw("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        writer.WriteRaw($"""<cols><col min="1" max="{headers.Count}" width="16" customWidth="1"/></cols>""");
        writer.WriteStartElement("sheetData", Main);
        WriteRow(writer, headers.Cast<object?>().ToArray());
        var count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            WriteRow(writer, row);
            if (++count % 1000 == 0)
            {
                await writer.FlushAsync(); // 每 1000 列送進壓縮串流，緩衝不累積
            }
        }
        writer.WriteEndElement();
        writer.WriteEndElement();
        await writer.WriteEndDocumentAsync();
        await writer.FlushAsync();
    }

    private static void Static(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private static void WriteRow(XmlWriter writer, object?[] values)
    {
        writer.WriteStartElement("row", Main);
        foreach (var value in values)
        {
            writer.WriteStartElement("c", Main);
            switch (value)
            {
                case null:
                    break;
                case decimal d:
                    Number(writer, d.ToString(CultureInfo.InvariantCulture));
                    break;
                case int i:
                    Number(writer, i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    Number(writer, l.ToString(CultureInfo.InvariantCulture));
                    break;
                case bool b:
                    Text(writer, b ? "是" : "否");
                    break;
                case DateTimeOffset t:
                    Text(writer, TimeZoneInfo.ConvertTime(t, Taipei).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    break;
                case DateOnly date:
                    Text(writer, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    break;
                default:
                    Text(writer, value.ToString() ?? string.Empty);
                    break;
            }
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void Number(XmlWriter writer, string value) => writer.WriteElementString("v", Main, value);

    private static void Text(XmlWriter writer, string text)
    {
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", Main);
        writer.WriteStartElement("t", Main);
        writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(Clean(text));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>去掉 XML 不允許的字元（控制字元、U+FFFE／U+FFFF、不成對的 surrogate），超過儲存格上限時截斷並標記。</summary>
    public static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxCellLength));
        var i = 0;
        for (; i < text.Length && builder.Length < MaxCellLength; i++)
        {
            if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                builder.Append(text, i, 2);
                i++;
            }
            else if (XmlConvert.IsXmlChar(text[i]))
            {
                builder.Append(text[i]);
            }
        }
        return i < text.Length
            ? builder.ToString(0, Math.Min(builder.Length, MaxCellLength - TruncatedMark.Length)) + TruncatedMark
            : builder.ToString();
    }

    private static TimeZoneInfo ResolveTaipei()
    {
        foreach (var id in new[] { "Asia/Taipei", "Taipei Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Taipei", TimeSpan.FromHours(8), "Taipei", "Taipei");
    }
}
