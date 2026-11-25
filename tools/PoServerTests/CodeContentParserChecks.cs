using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClassLibraryMT.PoServer;

internal static class CodeContentParserChecks
{
    internal static async Task RunAsync(string workDirectory, Action<string, bool, string> check,
        CancellationToken ct = default)
    {
        var root = Path.Combine(workDirectory, "codecontent-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sequence = 0;

        async Task VerifyTextAsync(string format, string contents, string[] expected, string name)
        {
            var path = Path.Combine(root, (++sequence).ToString() + "." + format);
            await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), ct);
            var actual = format == "csv" ? await PoCodeFileParser.ReadCsvAsync(path, ct)
                : await PoCodeFileParser.ReadJsonAsync(path, ct);
            check(name, actual.SequenceEqual(expected, StringComparer.Ordinal), "CodeContent changed or rows were lost.");
        }

        async Task RejectTextAsync(string format, string contents, string name)
        {
            var path = Path.Combine(root, (++sequence).ToString() + "." + format);
            await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), ct);
            await RejectAsync(() => format == "csv" ? PoCodeFileParser.ReadCsvAsync(path, ct)
                : PoCodeFileParser.ReadJsonAsync(path, ct), name);
        }

        async Task RejectAsync(Func<Task<IReadOnlyList<string>>> action, string name)
        {
            try
            {
                await action();
                check(name, false, "Invalid table was accepted.");
            }
            catch (PoFileFormatException)
            {
                check(name, true, "");
            }
        }

        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        string[] csvCodes = ["  opaque raw value  ", "https://a/21/SAME?x=1,2", "quote \"inside\"\r\nnext line", "Case", "case", "CodeContent"];
        await VerifyTextAsync("csv", "\uFEFF\r\n\"CodeContent\"\r\n" + string.Join("\r\n", csvCodes.Select(Quote)) + "\r\n\r\n",
            csvCodes, "CSV keeps quoted commas, quotes, line endings, whitespace and case exactly");
        await VerifyTextAsync("csv", "CodeContent\n  raw text  \n", ["  raw text  "],
            "CSV keeps unquoted leading and trailing whitespace");
        var boundaryCode = new string('a', 16 * 1024 - 30) + "\",tail\r\nnext";
        await VerifyTextAsync("csv", "CodeContent\r\n" + Quote(boundaryCode), [boundaryCode],
            "CSV quote and newline state survives input-buffer boundaries");

        foreach (var (text, name) in new (string, string)[]
        {
            ("serial\nA", "CSV rejects legacy serial header"),
            ("codecontent\nA", "CSV requires exact CodeContent header spelling"),
            ("CodeContent,Serial\nA,S", "CSV rejects extra header columns"),
            ("CodeContent\nA,B", "CSV rejects additional data cells"),
            ("CodeContent\nA,", "CSV rejects an additional empty column"),
            ("CodeContent\n\"\"", "CSV rejects an explicitly empty cell"),
            ("CodeContent\n   \n", "CSV rejects a whitespace-only value"),
            ("CodeContent\n\"unfinished", "CSV rejects unterminated quotes"),
            ("CodeContent\n\"A\"unexpected", "CSV rejects trailing text after a quoted cell"),
            ("CodeContent\nA\"B", "CSV rejects unescaped quotes in an unquoted cell"),
        }) await RejectTextAsync("csv", text, name);

        await VerifyTextAsync("json", """[{"CodeContent":"  opaque|full,Value  "},{"CodeContent":"Case"},{"CodeContent":"case"},{"CodeContent":"CodeContent"}]""",
            ["  opaque|full,Value  ", "Case", "case", "CodeContent"], "JSON keeps whole CodeContent values without header guessing");
        await VerifyTextAsync("json", "\uFEFF" + """[{"CodeContent":"Unicode-Đ/21/SAME"}]""",
            ["Unicode-Đ/21/SAME"], "JSON accepts a UTF-8 file BOM without changing Unicode values");
        foreach (var (text, name) in new (string, string)[]
        {
            ("[\"A\"]", "JSON rejects a bare string array"),
            ("[1]", "JSON rejects bare numbers"),
            ("[null]", "JSON rejects null rows"),
            ("[{}]", "JSON rejects a missing CodeContent"),
            ("""[{"codecontent":"A"}]""", "JSON requires exact CodeContent property spelling"),
            ("""[{"CodeContent":1}]""", "JSON rejects a non-string CodeContent"),
            ("""[{"CodeContent":null}]""", "JSON rejects a null CodeContent"),
            ("""[{"CodeContent":"A","Serial":"S"}]""", "JSON rejects additional identity fields"),
            ("""[{"CodeContent":"A","CodeContent":"B"}]""", "JSON rejects duplicate CodeContent properties"),
            ("""[{"CodeContent":" "}]""", "JSON rejects whitespace-only code content"),
            ("""{"poName":"PO","codes":["A"]}""", "JSON rejects inline PO envelopes"),
            ("""[{"CodeContent":"A"},{"CodeContent":""}]""", "JSON rejects the complete file when a later row is empty"),
        }) await RejectTextAsync("json", text, name);

        static string Cell(string reference, string content) => new XElement("c", new XAttribute("r", reference),
            new XAttribute("t", "inlineStr"), new XElement("is", new XElement("t", new XAttribute(XNamespace.Xml + "space", "preserve"), content)))
            .ToString(SaveOptions.DisableFormatting);
        static string Row(int number, params string[] cells) => $"<row r=\"{number}\">{string.Concat(cells)}</row>";

        async Task<string> XlsxAsync(string rows, string? sharedStrings = null)
        {
            var path = Path.Combine(root, (++sequence).ToString() + ".xlsx");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            async Task EntryAsync(string name, string text)
            {
                await using var stream = archive.CreateEntry(name).Open();
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(text.AsMemory(), ct);
            }
            await EntryAsync("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="codes" sheetId="1" r:id="rId1" /></sheets></workbook>""");
            await EntryAsync("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/codes.xml" /></Relationships>""");
            await EntryAsync("xl/worksheets/codes.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + rows + "</sheetData></worksheet>");
            if (sharedStrings is not null) await EntryAsync("xl/sharedStrings.xml", sharedStrings);
            return path;
        }

        var validXlsx = await XlsxAsync("<row r=\"1\"/>" + Row(2, Cell("A2", "CodeContent"))
            + Row(3, Cell("A3", "  full|opaque,code  "), "<c r=\"B3\" s=\"1\"/>")
            + Row(4, Cell("A4", "Case")) + Row(5, Cell("A5", "case")));
        var excelCodes = await PoCodeFileParser.ReadXlsxAsync(validXlsx, ct);
        check("XLSX preserves text and case; empty formatted cells are not extra data columns",
            excelCodes.SequenceEqual(["  full|opaque,code  ", "Case", "case"], StringComparer.Ordinal), "XLSX values changed.");

        var sharedXlsx = await XlsxAsync(Row(1, "<c r=\"A1\" t=\"s\"><v>0</v></c>")
            + Row(2, "<c r=\"A2\" t=\"s\"><v>1</v></c>"),
            """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>CodeContent</t></si><si><r><t xml:space="preserve">  opaque</t></r><rPh><t>phonetic annotation</t></rPh><r><t xml:space="preserve"> Value  </t></r></si></sst>""");
        var sharedCodes = await PoCodeFileParser.ReadXlsxAsync(sharedXlsx, ct);
        check("XLSX rich shared strings retain code text and omit phonetic annotations",
            sharedCodes.SequenceEqual(["  opaque Value  "], StringComparer.Ordinal), "Shared-string identity changed.");

        var escapedXlsx = await XlsxAsync(Row(1, Cell("A1", "CodeContent"))
            + Row(2, Cell("A2", "left_x000D_right_x005F_x0041_")));
        var escapedCodes = await PoCodeFileParser.ReadXlsxAsync(escapedXlsx, ct);
        check("XLSX decodes escaped control characters and preserves escaped literal sequences",
            escapedCodes.SequenceEqual(["left\rright_x0041_"], StringComparer.Ordinal), "Excel text escapes changed code identity.");

        foreach (var (rows, name) in new (string, string)[]
        {
            (Row(1, Cell("A1", "Serial")) + Row(2, Cell("A2", "A")), "XLSX rejects legacy headers"),
            (Row(1, Cell("A1", "CodeContent"), Cell("B1", "Serial")), "XLSX rejects additional header columns"),
            (Row(1, Cell("A1", "CodeContent")) + Row(2, Cell("A2", "A"), Cell("B2", "B")), "XLSX rejects additional data columns"),
            (Row(1, Cell("A1", "CodeContent")) + Row(2, Cell("A2", "")), "XLSX rejects an explicit empty value"),
            (Row(1, Cell("A1", "CodeContent")) + Row(2, "<c r=\"A2\"><v>123</v></c>"), "XLSX requires codes stored as text"),
            (Row(1, Cell("A1", "CodeContent")) + Row(2, "<c r=\"A2\" t=\"str\"><f>CONCAT(1,2)</f><v>12</v></c>"), "XLSX rejects formula identities"),
        })
        {
            var invalidXlsx = await XlsxAsync(rows);
            await RejectAsync(() => PoCodeFileParser.ReadXlsxAsync(invalidXlsx, ct), name);
        }
    }
}
