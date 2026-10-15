using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using static ThreadsToxic.Web.Services.Export.PyUnicode;

namespace ThreadsToxic.Web.Services.Export;

/// <summary>
/// Ghi file Excel/CSV giống exporter của crawler (export_curated_excel / export_curated_csv):
/// cùng tên file, tên sheet, độ rộng cột, font, màu, viền, wrap text và chiều cao hàng.
/// Khác biệt có chủ đích: ô bắt đầu bằng "=" (vd "=)))") được ghi là văn bản; bên crawler openpyxl
/// biến chúng thành công thức hỏng khiến Excel báo lỗi khi mở.
/// </summary>
public static class CuratedFiles
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string CsvContentType = "text/csv";
    public const string SheetName = "Dữ liệu đánh giá";

    public static string FileName(string status, string extension, DateTime now) =>
        $"threads_danh_gia_4_cot_{status}_{now:yyyyMMdd_HHmmss}.{extension}";

    private sealed record ColumnStyle(double Width, string Align, double FontSize, string Color, bool IsUser);

    // COLUMN_CONFIG, đúng thứ tự khai báo (thứ tự quyết định khi tên cột khớp nhiều mục)
    private static readonly (string Key, ColumnStyle Style)[] ColumnConfig =
    [
        ("Mã ID", new(16, "center", 9.5, "64748B", false)),
        ("Bài viết", new(48, "left", 10.5, "000000", false)),
        ("f0", new(42, "left", 10.5, "000000", false)),
        ("f1", new(42, "left", 10.5, "000000", false)),
        ("f2", new(42, "left", 10.5, "000000", false)),
        ("f3", new(42, "left", 10.5, "000000", false)),
        ("Nội dung", new(48, "left", 10.5, "000000", false)),
        ("Điểm đánh giá", new(22, "center", 10.5, "000000", false)),
        ("Chủ đề bài viết", new(24, "center", 10.5, "000000", false)),
        ("Tự đánh giá của bạn (Xấu luôn / Chưa rõ / Trong sạch)", new(30, "center", 10, "854D0E", true)),
        ("Từ lóng mới bổ sung (nếu có)", new(28, "center", 10, "854D0E", true)),
    ];

    private static readonly ColumnStyle DefaultStyle = new(25, "left", 10.5, "000000", false);

    // Cột có thể dài, dùng để tính chiều cao hàng
    private static readonly string[] LongTextKeywords = ["bài viết", "f0", "f1", "f2", "f3", "nội dung"];

    private const double LineHeightPt = 14.4;
    private const double MinRowHeight = 18.0;
    private const double MaxRowHeight = 400.0;
    private const double CharWidthCorrection = 1.8;

    // Khớp theo tên cột: bằng nhau, hoặc chuỗi này nằm trong chuỗi kia (vd "Chủ đề bài viết" khớp "Bài viết")
    private static ColumnStyle StyleFor(string header)
    {
        var h = Lower(Strip(header));
        foreach (var (key, style) in ColumnConfig)
        {
            var k = Lower(key);
            if (k == h || h.Contains(k, StringComparison.Ordinal) || k.Contains(h, StringComparison.Ordinal))
                return style;
        }
        return DefaultStyle;
    }

    private static bool IsLongText(string header)
    {
        var h = Lower(Strip(header));
        return LongTextKeywords.Any(k => h.Contains(k, StringComparison.Ordinal));
    }

    // Số ký tự mỗi dòng của từng cột dài khi wrap; 0 = cột không dùng để tính chiều cao
    private static int[] CharsPerLine(IReadOnlyList<string> headers) =>
        headers.Select(h => IsLongText(h) ? Math.Max(1, (int)(StyleFor(h).Width / CharWidthCorrection)) : 0).ToArray();

    private static double RowHeight(IReadOnlyList<string> row, int[] charsPerLineByColumn)
    {
        var maxLines = 1;
        for (var i = 0; i < charsPerLineByColumn.Length; i++)
        {
            var charsPerLine = charsPerLineByColumn[i];
            if (charsPerLine == 0 || string.IsNullOrEmpty(row[i])) continue;
            var paragraphs = row[i].Split('\n');
            var wrapLines = paragraphs.Sum(p => Math.Max(1, (Length(p) + charsPerLine - 1) / charsPerLine));
            maxLines = Math.Max(maxLines, Math.Max(paragraphs.Length, wrapLines));
        }
        return Math.Min(MaxRowHeight, Math.Max(MinRowHeight, maxLines * LineHeightPt + 4));
    }

    public static byte[] WriteCsv(IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        // pandas.to_csv(encoding="utf-8-sig") trên Windows: BOM, xuống dòng \r\n, csv.QUOTE_MINIMAL
        var sb = new StringBuilder();
        AppendCsvRecord(sb, headers);
        foreach (var row in rows)
            AppendCsvRecord(sb, row);
        var body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. body.GetPreamble(), .. body.GetBytes(sb.ToString())];
    }

    private static void AppendCsvRecord(StringBuilder sb, IReadOnlyList<string> fields)
    {
        // Module csv của Python: một bản ghi chỉ có một trường rỗng được ghi là ""
        if (fields.Count == 1 && fields[0].Length == 0)
        {
            sb.Append("\"\"\r\n");
            return;
        }
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var field = CsvSafe(fields[i]);
            if (field.AsSpan().IndexOfAny(",\"\r\n") >= 0)
                sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            else
                sb.Append(field);
        }
        sb.Append("\r\n");
    }

    // Chống CSV injection: nội dung bình luận đến từ internet, ô bắt đầu bằng = + - @ có thể bị Excel chạy như công thức
    // khi mở CSV. Thêm dấu ' đứng đầu để Excel coi là chữ. Khác crawler ở đúng các ô này.
    public static string CsvSafe(string field) =>
        field.Length > 0 && field[0] is '=' or '+' or '-' or '@' ? "'" + field : field;

    public static byte[] WriteXlsx(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var styles = headers.Select(StyleFor).ToArray();

        // Mỗi kiểu font/nền khác nhau là một mục trong styles.xml; xf 0 mặc định, 1 header, từ 2 trở đi là cột dữ liệu
        var fonts = new List<string> { FontXml("Calibri", 11, "000000", bold: false), FontXml("Segoe UI", 11, "FFFFFF", bold: true) };
        var cellXfs = new List<string>
        {
            """<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>""",
            """<xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>""",
        };
        var columnXf = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            var s = styles[i];
            var font = s.IsUser ? FontXml("Segoe UI", 10, "854D0E", bold: true) : FontXml("Segoe UI", s.FontSize, s.Color, bold: false);
            var fontId = fonts.IndexOf(font);
            if (fontId < 0)
            {
                fonts.Add(font);
                fontId = fonts.Count - 1;
            }
            var fillId = s.IsUser ? 3 : 0;
            var xf = $"""<xf numFmtId="0" fontId="{fontId}" fillId="{fillId}" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="{s.Align}" vertical="top" wrapText="1"/></xf>""";
            var xfId = cellXfs.IndexOf(xf);
            if (xfId < 0)
            {
                cellXfs.Add(xf);
                xfId = cellXfs.Count - 1;
            }
            columnXf[i] = xfId;
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            WriteEntry(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            WriteEntry(zip, "xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><bookViews><workbookView activeTab="0"/></bookViews><sheets><sheet name="{SheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            WriteEntry(zip, "xl/styles.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="{fonts.Count}">{string.Concat(fonts)}</fonts><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0F172A"/><bgColor rgb="FF0F172A"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFEF9C3"/><bgColor rgb="FFFEF9C3"/></patternFill></fill></fills><borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="thin"><color rgb="FFCBD5E1"/></left><right style="thin"><color rgb="FFCBD5E1"/></right><top style="thin"><color rgb="FFCBD5E1"/></top><bottom style="thin"><color rgb="FFCBD5E1"/></bottom><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="{cellXfs.Count}">{string.Concat(cellXfs)}</cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");

            var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
            using var stream = sheet.Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            WriteSheet(xml, headers, rows, styles, columnXf);
        }
        return buffer.ToArray();
    }

    private static void WriteSheet(XmlWriter xml, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
        ColumnStyle[] styles, int[] columnXf)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        xml.WriteStartDocument(standalone: true);
        xml.WriteStartElement("worksheet", ns);

        xml.WriteStartElement("dimension", ns);
        xml.WriteAttributeString("ref", $"A1:{ColumnName(Math.Max(1, headers.Count) - 1)}{rows.Count + 1}");
        xml.WriteEndElement();

        xml.WriteStartElement("sheetFormatPr", ns);
        xml.WriteAttributeString("baseColWidth", "8");
        xml.WriteAttributeString("defaultRowHeight", "15");
        xml.WriteEndElement();

        if (headers.Count > 0)
        {
            xml.WriteStartElement("cols", ns);
            for (var i = 0; i < headers.Count; i++)
            {
                xml.WriteStartElement("col", ns);
                xml.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("width", Number(styles[i].Width));
                xml.WriteAttributeString("customWidth", "1");
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetData", ns);
        WriteRow(xml, 1, headers, 28, _ => 1);
        var charsPerLine = CharsPerLine(headers);
        foreach (var (row, index) in rows.Select((row, index) => (row, index)))
        {
            // Bên crawler, mọi ô văn bản dữ liệu được gộp khoảng trắng thêm một lần trước khi tính chiều cao
            var values = row.Select(CollapseSpaces).ToArray();
            WriteRow(xml, index + 2, values, RowHeight(values, charsPerLine), i => columnXf[i]);
        }
        xml.WriteEndElement();

        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter xml, int rowNumber, IReadOnlyList<string> values, double height,
        Func<int, int> styleOf)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        xml.WriteStartElement("row", ns);
        xml.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("ht", Number(height));
        xml.WriteAttributeString("customHeight", "1");
        for (var i = 0; i < values.Count; i++)
        {
            var text = SanitizeXml(values[i]);
            xml.WriteStartElement("c", ns);
            xml.WriteAttributeString("r", $"{ColumnName(i)}{rowNumber}");
            xml.WriteAttributeString("s", styleOf(i).ToString(CultureInfo.InvariantCulture));
            if (text.Length > 0)
            {
                xml.WriteAttributeString("t", "inlineStr");
                xml.WriteStartElement("is", ns);
                xml.WriteStartElement("t", ns);
                if (text != text.Trim(' ', '\t', '\n', '\r'))
                    xml.WriteAttributeString("xml", "space", null, "preserve");
                xml.WriteString(text);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static string FontXml(string name, double size, string color, bool bold) =>
        $"""<font>{(bold ? "<b/>" : "")}<sz val="{Number(size)}"/><color rgb="FF{color}"/><name val="{name}"/></font>""";

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // openpyxl ghi số bằng "%.16g" (vd 13 * 14.4 + 4 = 191.20000000000002 được ghi là 191.2)
    private static string Number(double value) => value.ToString("G16", CultureInfo.InvariantCulture);

    public static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    // Ký tự điều khiển không hợp lệ trong XML 1.0 bị bỏ (openpyxl sẽ báo lỗi và không tạo được file)
    private static string SanitizeXml(string text)
    {
        if (!text.Any(c => c < 0x20 && c is not ('\t' or '\n' or '\r') || c is '\uFFFE' or '\uFFFF' || char.IsSurrogate(c)))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
                continue;
            }
            if (char.IsSurrogate(c) || c is '\uFFFE' or '\uFFFF' || c < 0x20 && c is not ('\t' or '\n' or '\r'))
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
