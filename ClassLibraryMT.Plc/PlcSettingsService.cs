using System.Globalization;
using ClassLibraryMT.Communications.Plc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Plc;

/// <summary>
/// Cấu hình thư viện quản lý tham số PLC (section <c>PlcSettings</c> trong appsettings.json).
/// </summary>
public sealed class PlcSettingsOptions
{
    /// <summary>
    /// Đường dẫn file database tham số PLC và recipe.
    ///
    /// Nếu là đường dẫn tương đối thì được hiểu là tương đối so với thư mục
    /// ứng dụng (<see cref="AppContext.BaseDirectory"/>). Mặc định
    /// <c>logs/plc.db</c>.
    /// </summary>
    public string DbPath { get; set; } = "logs/plc.db";

    /// <summary>
    /// Đường dẫn file database đã phân giải thành đường dẫn tuyệt đối.
    /// </summary>
    public string ResolveFullPath()
    {
        if (string.IsNullOrWhiteSpace(DbPath))
        {
            return Path.Combine(AppContext.BaseDirectory, "logs", "plc.db");
        }

        return Path.IsPathRooted(DbPath)
            ? DbPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DbPath));
    }
}

/// <summary>
/// Client đọc/ghi ô nhớ PLC cho thao tác thủ công.
///
/// Tách interface khỏi <see cref="OmronPlc"/> để:
/// - Test được không cần PLC thật.
/// - Tạo client riêng cho tab "Chỉnh PLC", tránh tranh chấp với
///   instance singleton đang dùng cho heartbeat.
///
/// <see cref="OmronPlc"/> không thread-safe nên mọi lệnh phải được serialize.
/// </summary>
public interface IPlcMemoryClient : IDisposable
{
    /// <summary>Đọc 1 word (short).</summary>
    PlcResult<short> ReadInt16(string address);

    /// <summary>Đọc 2 word (int).</summary>
    PlcResult<int> ReadInt32(string address);

    /// <summary>Đọc 2 word (float).</summary>
    PlcResult<float> ReadFloat(string address);

    /// <summary>Đọc 1 bit trong word (địa chỉ dạng "D12.05").</summary>
    PlcResult<bool> ReadBool(string address);

    /// <summary>Ghi 1 word (short).</summary>
    PlcResult WriteInt16(string address, short value);

    /// <summary>Ghi 2 word (int).</summary>
    PlcResult WriteInt32(string address, int value);

    /// <summary>Ghi 2 word (float).</summary>
    PlcResult WriteFloat(string address, float value);

    /// <summary>Ghi 1 bit trong word (địa chỉ dạng "D12.05").</summary>
    PlcResult WriteBool(string address, bool value);
}

/// <summary>
/// Tạo client đọc/ghi ô nhớ PLC từ cấu hình.
/// </summary>
public interface IPlcMemoryClientFactory
{
    /// <summary>
    /// Tạo một client mới. Caller chịu trách nhiệm dispose.
    /// </summary>
    /// <returns>Client đọc/ghi ô nhớ.</returns>
    IPlcMemoryClient Create();
}

/// <summary>
/// Cấu hình kết nối PLC dùng cho client thủ công.
/// </summary>
internal sealed class PlcClientOptions
{
    /// <summary>Địa chỉ IP PLC.</summary>
    public string Ip { get; set; } = "127.0.0.1";

    /// <summary>Cổng UDP FINS.</summary>
    public int Port { get; set; } = 9600;

    /// <summary>Node PLC (DA1).</summary>
    public byte RemoteNode { get; set; }

    /// <summary>Node PC (SA1).</summary>
    public byte LocalNode { get; set; } = 1;

    /// <summary>Timeout mỗi request (ms).</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>Số lần thử lại.</summary>
    public int Retries { get; set; }
}

/// <summary>
/// Bọc <see cref="OmronPlc"/> thành <see cref="IPlcMemoryClient"/>.
/// </summary>
internal sealed class OmronMemoryClient : IPlcMemoryClient
{
    private readonly OmronPlc _plc;

    /// <summary>
    /// Khởi tạo client từ cấu hình.
    /// </summary>
    /// <param name="options">Cấu hình kết nối.</param>
    public OmronMemoryClient(PlcClientOptions options)
    {
        _plc = new OmronPlc(
            options.Ip,
            options.Port,
            options.RemoteNode,
            options.LocalNode,
            options.TimeoutMs,
            options.Retries);
    }

    /// <inheritdoc />
    public PlcResult<short> ReadInt16(string address) => _plc.ReadInt16(address);

    /// <inheritdoc />
    public PlcResult<int> ReadInt32(string address) => _plc.ReadInt32(address);

    /// <inheritdoc />
    public PlcResult<float> ReadFloat(string address) => _plc.ReadFloat(address);

    /// <inheritdoc />
    public PlcResult<bool> ReadBool(string address) => _plc.ReadBool(address);

    /// <inheritdoc />
    public PlcResult WriteInt16(string address, short value) => _plc.WriteInt16(address, value);

    /// <inheritdoc />
    public PlcResult WriteInt32(string address, int value) => _plc.WriteInt32(address, value);

    /// <inheritdoc />
    public PlcResult WriteFloat(string address, float value) => _plc.WriteFloat(address, value);

    /// <inheritdoc />
    public PlcResult WriteBool(string address, bool value) => _plc.WriteBool(address, value);

    /// <inheritdoc />
    public void Dispose() => _plc.Dispose();
}

/// <summary>
/// Tạo client đọc/ghi ô nhớ từ cấu hình.
/// </summary>
internal sealed class PlcMemoryClientFactory : IPlcMemoryClientFactory
{
    private readonly IOptions<PlcClientOptions> _options;

    /// <summary>
    /// Khởi tạo factory.
    /// </summary>
    /// <param name="options">Cấu hình kết nối.</param>
    public PlcMemoryClientFactory(IOptions<PlcClientOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IPlcMemoryClient Create() => new OmronMemoryClient(_options.Value);
}

/// <summary>
/// Triển khai <see cref="IPlcSettingsService"/>: CRUD tham số/recipe trên SQLite
/// và đọc/ghi PLC thật qua <see cref="IPlcMemoryClient"/>.
/// </summary>
internal sealed class PlcSettingsService : IPlcSettingsService
{
    private readonly PlcStore _store;
    private readonly IPlcMemoryClientFactory _clientFactory;
    private readonly ILogger<PlcSettingsService> _logger;

    // OmronPlc không thread-safe -> serialize mọi lệnh đọc/ghi.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Khởi tạo service.
    /// </summary>
    /// <param name="store">Lớp lưu trữ SQLite.</param>
    /// <param name="clientFactory">Factory tạo client đọc/ghi ô nhớ.</param>
    /// <param name="logger">Logger.</param>
    public PlcSettingsService(
        PlcStore store,
        IPlcMemoryClientFactory clientFactory,
        ILogger<PlcSettingsService> logger)
    {
        _store = store;
        _clientFactory = clientFactory;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Tham so
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<IReadOnlyList<PlcParameter>> GetParametersAsync(CancellationToken ct = default) =>
        _store.GetParametersAsync(ct);

    /// <inheritdoc />
    public Task<PlcParameter?> GetParameterByIdAsync(long id, CancellationToken ct = default) =>
        _store.GetParameterByIdAsync(id, ct);

    /// <inheritdoc />
    public async Task<Result> SaveParameterAsync(
        PlcParameter parameter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        var validation = ValidateParameter(parameter);
        if (!validation.Ok)
        {
            return validation;
        }

        return await _store.SaveParameterAsync(parameter, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Result> DeleteParameterAsync(long id, CancellationToken ct = default) =>
        _store.DeleteParameterAsync(id, ct);

    // ------------------------------------------------------------------
    // Recipe
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public Task<IReadOnlyList<PlcRecipe>> GetRecipesAsync(CancellationToken ct = default) =>
        _store.GetRecipesAsync(ct);

    /// <inheritdoc />
    public Task<Result> CreateRecipeAsync(string name, CancellationToken ct = default) =>
        _store.CreateRecipeAsync(name, ct);

    /// <inheritdoc />
    public Task<Result> DeleteRecipeAsync(long id, CancellationToken ct = default) =>
        _store.DeleteRecipeAsync(id, ct);

    /// <inheritdoc />
    public Task<Result> SaveRecipeValuesAsync(
        long recipeId,
        IReadOnlyDictionary<long, string> values,
        CancellationToken ct = default) =>
        _store.SaveRecipeValuesAsync(recipeId, values, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, string>> GetRecipeValuesAsync(
        long recipeId,
        CancellationToken ct = default) =>
        _store.GetRecipeValuesAsync(recipeId, ct);

    // ------------------------------------------------------------------
    // Doc / ghi PLC
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<PlcReadResult> ReadParameterAsync(
        PlcParameter parameter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        if (!parameter.CanRead)
        {
            return PlcReadResult.Fail(
                parameter.Id,
                "ARG",
                $"Tham so '{parameter.Name}' chi ghi, khong doc duoc.");
        }

        var validation = ValidateParameter(parameter);
        if (!validation.Ok)
        {
            return PlcReadResult.Fail(parameter.Id, "ARG", validation.Message);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var client = _clientFactory.Create();

            var result = parameter.ValueType switch
            {
                PlcValueType.Int16 => ToShortResult(client.ReadInt16(parameter.Address)),
                PlcValueType.Int32 => ToIntResult(client.ReadInt32(parameter.Address)),
                PlcValueType.Float => ToFloatResult(client.ReadFloat(parameter.Address)),
                PlcValueType.Bool => ToBoolResult(client.ReadBool(parameter.Address)),
                _ => PlcResult<string>.Fail("ARG", "Kieu du lieu khong ho tro.", default),
            };

            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "Doc o nho PLC that bai ({Name} = {Address}): {Code} - {Message}",
                    parameter.Name, parameter.Address, result.ErrorCode, result.Message);
                return PlcReadResult.Fail(
                    parameter.Id,
                    result.ErrorCode,
                    result.Message,
                    result.ProcessTime);
            }

            return PlcReadResult.Ok(
                parameter.Id,
                result.Value ?? string.Empty,
                "OK",
                result.ProcessTime);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlcReadResult>> ReadAllAsync(CancellationToken ct = default)
    {
        var parameters = await _store.GetParametersAsync(ct).ConfigureAwait(false);

        var results = new List<PlcReadResult>(parameters.Count);
        foreach (var parameter in parameters)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ReadParameterAsync(parameter, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<PlcWriteResult> WriteParameterAsync(
        PlcParameter parameter,
        string value,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var client = _clientFactory.Create();
            return WriteToClient(client, parameter, value);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlcWriteResult>> ApplyRecipeAsync(
        long recipeId,
        CancellationToken ct = default)
    {
        var recipe = await _store.GetRecipeByIdAsync(recipeId, ct).ConfigureAwait(false);
        if (recipe is null)
        {
            return [PlcWriteResult.Fail("ARG", "Khong tim thay recipe.")];
        }

        var parameters = await _store.GetParametersAsync(ct).ConfigureAwait(false);
        var values = await _store.GetRecipeValuesAsync(recipeId, ct).ConfigureAwait(false);

        var results = new List<PlcWriteResult>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Dung MOT client cho ca recipe: tranh tao/ket noi lai cho tung o nho.
            using var client = _clientFactory.Create();

            foreach (var parameter in parameters)
            {
                ct.ThrowIfCancellationRequested();

                // Chi ghi tham so co quyen ghi va co gia tri trong recipe.
                if (!parameter.CanWrite)
                {
                    continue;
                }

                if (!values.TryGetValue(parameter.Id, out var value))
                {
                    results.Add(PlcWriteResult.Fail(
                        "ARG", $"Tham so '{parameter.Name}' khong co gia tri trong recipe."));
                    continue;
                }

                results.Add(WriteToClient(client, parameter, value));
            }
        }
        finally
        {
            _gate.Release();
        }

        return results;
    }

    /// <summary>
    /// Ghi mot gia tri xuong PLC bang client da co (khong acquire lai gate).
    /// </summary>
    /// <param name="client">Client de ghi.</param>
    /// <param name="parameter">Tham so can ghi.</param>
    /// <param name="value">Gia tri (chuoi).</param>
    /// <returns>Ket qua ghi.</returns>
    private PlcWriteResult WriteToClient(
        IPlcMemoryClient client,
        PlcParameter parameter,
        string value)
    {
        if (!parameter.CanWrite)
        {
            return PlcWriteResult.Fail("ARG", $"Tham so '{parameter.Name}' chi doc, khong ghi duoc.");
        }

        var validation = ValidateParameter(parameter);
        if (!validation.Ok)
        {
            return PlcWriteResult.Fail("ARG", validation.Message);
        }

        if (!TryParseValue(parameter.ValueType, value, out var parsed, out var parseError))
        {
            return PlcWriteResult.Fail("ARG", parseError);
        }

        var result = parameter.ValueType switch
        {
            PlcValueType.Int16 => client.WriteInt16(parameter.Address, (short)parsed),
            PlcValueType.Int32 => client.WriteInt32(parameter.Address, (int)parsed),
            PlcValueType.Float => client.WriteFloat(parameter.Address, (float)parsed),
            PlcValueType.Bool => client.WriteBool(parameter.Address, parsed != 0),
            _ => PlcResult.Fail("ARG", "Kieu du lieu khong ho tro.", default),
        };

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Ghi o nho PLC that bai ({Name} = {Address}): {Code} - {Message}",
                parameter.Name, parameter.Address, result.ErrorCode, result.Message);
            return PlcWriteResult.Fail(result.ErrorCode, result.Message, result.ProcessTime);
        }

        return PlcWriteResult.Ok(NormalizeValue(parameter.ValueType, parsed), "OK", result.ProcessTime);
    }

    // ------------------------------------------------------------------
    // Validation & parsing
    // ------------------------------------------------------------------

    /// <summary>
    /// Kiểm tra tính hợp lệ của một tham số (tên, địa chỉ, kiểu dữ liệu).
    /// </summary>
    private static Result ValidateParameter(PlcParameter parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter.Name))
        {
            return Result.Fail("Ten tham so khong duoc de trong.");
        }

        if (parameter.Name.Trim().Length > 100)
        {
            return Result.Fail("Ten tham so toi da 100 ky tu.");
        }

        if (!IsValidAddress(parameter.Address))
        {
            return Result.Fail(
                $"Dia chi '{parameter.Address}' khong hop le. "
                + "Dinh dang: D12, DM12 hoac D12.05 (bit 0-15).");
        }

        if (parameter.ValueType == PlcValueType.Bool &&
            !parameter.Address.Contains('.', StringComparison.Ordinal))
        {
            return Result.Fail(
                $"Tham so Bool '{parameter.Name}' phai dung dia chi dang 'D12.05'.");
        }

        if (parameter.ValueType != PlcValueType.Bool &&
            parameter.Address.Contains('.', StringComparison.Ordinal))
        {
            return Result.Fail(
                $"Tham so '{parameter.Name}' dang dung dia chi bit "
                + "nhung kieu du lieu khong phai Bool.");
        }

        if (!Enum.IsDefined(parameter.AccessMode))
        {
            return Result.Fail("Che do truy cap khong hop le.");
        }

        if (!Enum.IsDefined(parameter.ValueType))
        {
            return Result.Fail("Kieu du lieu khong hop le.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Kiểm tra địa chỉ ô nhớ vùng D (theo cùng luật với <c>FinsAddress</c>).
    /// </summary>
    /// <param name="address">Địa chỉ cần kiểm tra.</param>
    /// <returns>True nếu hợp lệ.</returns>
    private static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var text = address.Trim();

        if (text.StartsWith("DM", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        else if (text.StartsWith("D", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }
        else
        {
            return false;
        }

        if (text.Length == 0)
        {
            return false;
        }

        int? bit = null;
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var bitPart = text[(dot + 1)..];
            text = text[..dot];

            if (!int.TryParse(
                    bitPart,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var bitValue))
            {
                return false;
            }

            if (bitValue < 0 || bitValue > 15)
            {
                return false;
            }

            bit = bitValue;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var word))
        {
            return false;
        }

        _ = bit;
        return word is >= 0 and <= 32767;
    }

    /// <summary>
    /// Parse chuỗi thành giá trị số theo kiểu dữ liệu.
    /// </summary>
    private static bool TryParseValue(
        PlcValueType type,
        string? raw,
        out double value,
        out string error)
    {
        value = 0;
        error = string.Empty;

        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Gia tri khong duoc de trong.";
            return false;
        }

        switch (type)
        {
            case PlcValueType.Int16:
                if (!short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    error = $"'{text}' khong phai so nguyen 16 bit.";
                    return false;
                }

                value = s;
                return true;

            case PlcValueType.Int32:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    error = $"'{text}' khong phai so nguyen 32 bit.";
                    return false;
                }

                value = i;
                return true;

            case PlcValueType.Float:
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                    || float.IsNaN(f)
                    || float.IsInfinity(f))
                {
                    error = $"'{text}' khong phai so thuc hop le.";
                    return false;
                }

                value = f;
                return true;

            case PlcValueType.Bool:
                if (TryParseBool(text, out var b))
                {
                    value = b ? 1 : 0;
                    return true;
                }

                error = $"'{text}' khong phai gia tri boolean (true/false, 1/0, on/off).";
                return false;

            default:
                error = "Kieu du lieu khong ho tro.";
                return false;
        }
    }

    /// <summary>
    /// Parse chuỗi thành boolean (chấp nhận true/false, 1/0, on/off, co/khong).
    /// </summary>
    private static bool TryParseBool(string text, out bool value)
    {
        value = false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "on" or "yes" or "co":
                value = true;
                return true;
            case "0" or "false" or "off" or "no" or "khong":
                value = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Chuẩn hoá giá trị để hiển thị/lưu trữ.
    /// </summary>
    private static string NormalizeValue(PlcValueType type, double value) => type switch
    {
        PlcValueType.Int16 => ((short)value).ToString(CultureInfo.InvariantCulture),
        PlcValueType.Int32 => ((int)value).ToString(CultureInfo.InvariantCulture),
        PlcValueType.Float => ((float)value).ToString("0.####", CultureInfo.InvariantCulture),
        PlcValueType.Bool => value != 0 ? "true" : "false",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    // ------------------------------------------------------------------
    // Chuyen doi ket qua
    // ------------------------------------------------------------------

    private static PlcResult<string> ToShortResult(PlcResult<short> result) =>
        result.IsSuccess
            ? PlcResult<string>.Ok(
                result.Value!.ToString(CultureInfo.InvariantCulture),
                result.Message,
                result.ProcessTime)
            : PlcResult<string>.Fail(result.ErrorCode, result.Message, result.ProcessTime);

    private static PlcResult<string> ToIntResult(PlcResult<int> result) =>
        result.IsSuccess
            ? PlcResult<string>.Ok(
                result.Value!.ToString(CultureInfo.InvariantCulture),
                result.Message,
                result.ProcessTime)
            : PlcResult<string>.Fail(result.ErrorCode, result.Message, result.ProcessTime);

    private static PlcResult<string> ToFloatResult(PlcResult<float> result) =>
        result.IsSuccess
            ? PlcResult<string>.Ok(
                result.Value!.ToString("0.####", CultureInfo.InvariantCulture),
                result.Message,
                result.ProcessTime)
            : PlcResult<string>.Fail(result.ErrorCode, result.Message, result.ProcessTime);

    private static PlcResult<string> ToBoolResult(PlcResult<bool> result) =>
        result.IsSuccess
            ? PlcResult<string>.Ok(
                result.Value!.ToString().ToLowerInvariant(),
                result.Message,
                result.ProcessTime)
            : PlcResult<string>.Fail(result.ErrorCode, result.Message, result.ProcessTime);
}
