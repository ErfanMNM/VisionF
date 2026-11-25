using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ClassLibraryMT.PoServer;

/// <summary>Reads an exact one-column CodeContent table without projecting or trimming code values.</summary>
public static class PoCodeFileParser
{
    private const string ColumnName = "CodeContent";

    /// <summary>
    /// Reads CSV with exactly one CodeContent header and one string cell per record.
    /// Quoted commas, escaped quotes and embedded line endings are preserved.
    /// Empty physical lines are ignored; an empty or whitespace-only cell is invalid.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadCsvAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = new List<string>();
        var value = new StringBuilder();
        var state = CsvState.Start;
        var headerRead = false;
        var recordHasContent = false;
        var skipLineFeed = false;
        var recordNumber = 0;

        void FinishRecord()
        {
            if (!recordHasContent) return;
            recordNumber++;
            var text = value.ToString();
            if (!headerRead)
            {
                RequireHeader(text, "CSV");
                headerRead = true;
            }
            else
            {
                RequireCode(text, $"CSV record {recordNumber}");
                result.Add(text);
            }
            value.Clear();
            state = CsvState.Start;
            recordHasContent = false;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true);
            var buffer = new char[16 * 1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var character = buffer[i];
                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (character == '\n') continue;
                    }

                    if (state == CsvState.Quoted)
                    {
                        if (character == '"') state = CsvState.QuoteClosed;
                        else value.Append(character);
                        continue;
                    }

                    if (character is '\r' or '\n')
                    {
                        FinishRecord();
                        skipLineFeed = character == '\r';
                        continue;
                    }

                    if (character == ',')
                        throw new PoFileFormatException("CSV must contain exactly one CodeContent column; quote commas inside code values.");

                    recordHasContent = true;
                    if (state == CsvState.QuoteClosed)
                    {
                        if (character != '"')
                            throw new PoFileFormatException("CSV contains characters after a closing quote.");
                        value.Append('"');
                        state = CsvState.Quoted;
                    }
                    else if (character == '"')
                    {
                        if (state != CsvState.Start)
                            throw new PoFileFormatException("CSV quotes must enclose the entire CodeContent cell.");
                        state = CsvState.Quoted;
                    }
                    else
                    {
                        value.Append(character);
                        state = CsvState.Unquoted;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (state == CsvState.Quoted)
                throw new PoFileFormatException("CSV contains an unterminated quoted CodeContent cell.");
            FinishRecord();
            if (!headerRead) throw new PoFileFormatException("CSV is missing its CodeContent header.");
            return result;
        }
        catch (DecoderFallbackException exception)
        {
            throw new PoFileFormatException("CSV contains invalid text encoding.", exception);
        }
    }

    /// <summary>Reads a JSON array of objects, each with exactly one CodeContent string property.</summary>
    public static async Task<IReadOnlyList<string>> ReadJsonAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new PoFileFormatException("JSON must be an array of objects with exactly one CodeContent string property.");

            var result = new List<string>();
            var row = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                row++;
                if (item.ValueKind != JsonValueKind.Object)
                    throw new PoFileFormatException($"JSON row {row} must be an object containing only CodeContent.");
                var properties = item.EnumerateObject();
                if (!properties.MoveNext())
                    throw new PoFileFormatException($"JSON row {row} is missing CodeContent.");
                var property = properties.Current;
                if (!string.Equals(property.Name, ColumnName, StringComparison.Ordinal)
                    || properties.MoveNext() || property.Value.ValueKind != JsonValueKind.String)
                    throw new PoFileFormatException($"JSON row {row} must contain exactly one string property named CodeContent.");
                var value = property.Value.GetString()!;
                RequireCode(value, $"JSON row {row}");
                result.Add(value);
            }
            return result;
        }
        catch (JsonException exception)
        {
            throw new PoFileFormatException("Invalid JSON code file: " + exception.Message, exception);
        }
    }

    /// <summary>
    /// Reads the first XLSX worksheet as a single text column headed CodeContent.
    /// Numeric and formula cells are rejected because their displayed value can change code identity.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadXlsxAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var workbook = await ReadXmlAsync(RequireEntry(archive, "xl/workbook.xml"), cancellationToken)
                .ConfigureAwait(false);
            var relationships = await ReadXmlAsync(RequireEntry(archive, "xl/_rels/workbook.xml.rels"), cancellationToken)
                .ConfigureAwait(false);
            var firstSheet = workbook.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "sheets")?
                .Elements().FirstOrDefault(element => element.Name.LocalName == "sheet")
                ?? throw new PoFileFormatException("XLSX has no worksheet.");
            var relationId = firstSheet.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "id" && attribute.Name.NamespaceName.Length > 0)?.Value;
            var relation = relationships.Root?.Elements().FirstOrDefault(element =>
                element.Name.LocalName == "Relationship" && (string?)element.Attribute("Id") == relationId);
            if (relation is null || relationId is null
                || !((string?)relation.Attribute("Type") ?? string.Empty).EndsWith("/worksheet", StringComparison.Ordinal))
                throw new PoFileFormatException("The first XLSX sheet is not a valid worksheet.");

            var sharedStrings = new List<string>();
            var sharedRelation = relationships.Root?.Elements().FirstOrDefault(element =>
                ((string?)element.Attribute("Type") ?? string.Empty).EndsWith("/sharedStrings", StringComparison.Ordinal));
            var sharedPath = sharedRelation is null ? "xl/sharedStrings.xml" : ResolvePartPath(sharedRelation);
            if (archive.GetEntry(sharedPath) is { } sharedEntry)
            {
                var strings = await ReadXmlAsync(sharedEntry, cancellationToken).ConfigureAwait(false);
                foreach (var item in strings.Root?.Elements().Where(element => element.Name.LocalName == "si") ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sharedStrings.Add(ReadStringText(item));
                }
            }

            var result = new List<string>();
            var headerRead = false;
            await using var sheetStream = RequireEntry(archive, ResolvePartPath(relation)).Open();
            using var reader = XmlReader.Create(sheetStream, XmlSettings());
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
                using var subtree = reader.ReadSubtree();
                var row = await XElement.LoadAsync(subtree, LoadOptions.PreserveWhitespace, cancellationToken)
                    .ConfigureAwait(false);
                var previousColumn = 0;
                string? value = null;
                foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    var reference = (string?)cell.Attribute("r");
                    var column = reference is null ? previousColumn + 1 : ReadColumn(reference);
                    if (column <= previousColumn)
                        throw new PoFileFormatException("XLSX cells must use distinct column positions in each row.");
                    previousColumn = column;
                    var hasValue = cell.Elements().Any(element => element.Name.LocalName is "v" or "is" or "f");
                    if (!hasValue) continue; // Empty cells carrying formatting are not additional data columns.
                    if (column != 1)
                        throw new PoFileFormatException("XLSX must contain exactly one CodeContent data column in column A.");
                    value = ReadCodeCell(cell, sharedStrings);
                }
                if (value is null) continue; // Empty worksheet rows are not code records.
                if (!headerRead)
                {
                    RequireHeader(value, "XLSX");
                    headerRead = true;
                }
                else
                {
                    RequireCode(value, "XLSX row " + ((string?)row.Attribute("r") ?? "?"));
                    result.Add(value);
                }
            }
            if (!headerRead) throw new PoFileFormatException("XLSX is missing its CodeContent header.");
            return result;
        }
        catch (InvalidDataException exception)
        {
            throw new PoFileFormatException("Invalid XLSX file: " + exception.Message, exception);
        }
        catch (XmlException exception)
        {
            throw new PoFileFormatException("Invalid XLSX XML: " + exception.Message, exception);
        }
    }

    /// <summary>True only for the exact single CodeContent header.</summary>
    public static bool IsHeaderLine(string line) => string.Equals(line, ColumnName, StringComparison.Ordinal);

    private static void RequireHeader(string value, string format)
    {
        if (!IsHeaderLine(value))
            throw new PoFileFormatException($"{format} must have exactly one column named CodeContent.");
    }

    private static void RequireCode(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PoFileFormatException(location + " has an empty CodeContent value.");
    }

    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string path) => archive.GetEntry(path)
        ?? throw new PoFileFormatException("XLSX is missing " + path + ".");

    private static XmlReaderSettings XmlSettings() => new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
    };

    private static async Task<XDocument> ReadXmlAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, XmlSettings());
        return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, ct).ConfigureAwait(false);
    }

    private static string ResolvePartPath(XElement relationship)
    {
        var target = (string?)relationship.Attribute("Target");
        if (string.IsNullOrWhiteSpace(target)
            || string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(new Uri("https://xlsx.invalid/xl/workbook.xml"), target, out var resolved)
            || resolved.Scheme != "https" || resolved.Host != "xlsx.invalid"
            || resolved.Query.Length > 0 || resolved.Fragment.Length > 0)
            throw new PoFileFormatException("XLSX contains an invalid or external worksheet relationship.");
        return resolved.GetComponents(UriComponents.Path, UriFormat.Unescaped);
    }

    private static int ReadColumn(string reference)
    {
        var column = 0;
        var index = 0;
        while (index < reference.Length && reference[index] is >= 'A' and <= 'Z')
        {
            column = checked(column * 26 + reference[index] - 'A' + 1);
            index++;
            if (column > 16384) throw new PoFileFormatException("XLSX has an invalid cell reference.");
        }
        if (index == 0 || index == reference.Length
            || !int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row <= 0)
            throw new PoFileFormatException("XLSX has an invalid cell reference.");
        return column;
    }

    private static string ReadCodeCell(XElement cell, List<string> sharedStrings)
    {
        if (cell.Elements().Any(element => element.Name.LocalName == "f"))
            throw new PoFileFormatException("XLSX CodeContent must be text, not a formula.");
        var type = (string?)cell.Attribute("t") ?? string.Empty;
        var raw = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
        if (type == "inlineStr")
        {
            var inline = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "is")
                ?? throw new PoFileFormatException("XLSX contains an invalid inline text cell.");
            return ReadStringText(inline);
        }
        if (type == "str") return XmlConvert.DecodeName(raw);
        if (type == "s" && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < sharedStrings.Count)
            return sharedStrings[index];
        throw new PoFileFormatException("XLSX CodeContent cells must be stored as text; numbers, formulas and errors are not supported.");
    }

    private static string ReadStringText(XElement element)
    {
        var text = new StringBuilder();
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "t") text.Append(XmlConvert.DecodeName(child.Value));
            else if (child.Name.LocalName == "r")
                foreach (var run in child.Elements().Where(part => part.Name.LocalName == "t"))
                    text.Append(XmlConvert.DecodeName(run.Value));
        }
        return text.ToString();
    }

    private enum CsvState { Start, Unquoted, Quoted, QuoteClosed }
}
