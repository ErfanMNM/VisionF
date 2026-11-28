namespace ClassLibraryMT.Plc;

/// <summary>
/// API quản lý tham số PLC và recipe.
///
/// Xem <see cref="PlcSettingsService"/> cho phần triển khai.
/// </summary>
public interface IPlcSettingsService
{
    /// <summary>Lấy toàn bộ tham số PLC.</summary>
    /// <param name="ct">Token hủy.</param>
    Task<IReadOnlyList<PlcParameter>> GetParametersAsync(CancellationToken ct = default);

    /// <summary>Lấy một tham số theo Id.</summary>
    /// <param name="id">Id tham số.</param>
    /// <param name="ct">Token hủy.</param>
    Task<PlcParameter?> GetParameterByIdAsync(long id, CancellationToken ct = default);

    /// <summary>Thêm hoặc cập nhật một tham số (Id = 0 thì thêm mới).</summary>
    /// <param name="parameter">Tham số cần lưu.</param>
    /// <param name="ct">Token hủy.</param>
    Task<Result> SaveParameterAsync(PlcParameter parameter, CancellationToken ct = default);

    /// <summary>Xoá một tham số.</summary>
    /// <param name="id">Id tham số.</param>
    /// <param name="ct">Token hủy.</param>
    Task<Result> DeleteParameterAsync(long id, CancellationToken ct = default);

    /// <summary>Lấy toàn bộ recipe.</summary>
    /// <param name="ct">Token hủy.</param>
    Task<IReadOnlyList<PlcRecipe>> GetRecipesAsync(CancellationToken ct = default);

    /// <summary>Tạo recipe mới.</summary>
    /// <param name="name">Tên recipe.</param>
    /// <param name="ct">Token hủy.</param>
    Task<Result> CreateRecipeAsync(string name, CancellationToken ct = default);

    /// <summary>Xoá một recipe (không xoá recipe mặc định).</summary>
    /// <param name="id">Id recipe.</param>
    /// <param name="ct">Token hủy.</param>
    Task<Result> DeleteRecipeAsync(long id, CancellationToken ct = default);

    /// <summary>Lưu giá trị các tham số cho một recipe (ghi đè toàn bộ).</summary>
    /// <param name="recipeId">Id recipe.</param>
    /// <param name="values">Danh sách giá trị theo ParameterId.</param>
    /// <param name="ct">Token hủy.</param>
    Task<Result> SaveRecipeValuesAsync(
        long recipeId,
        IReadOnlyDictionary<long, string> values,
        CancellationToken ct = default);

    /// <summary>Đọc giá trị các tham số của một recipe.</summary>
    /// <param name="recipeId">Id recipe.</param>
    /// <param name="ct">Token hủy.</param>
    Task<IReadOnlyDictionary<long, string>> GetRecipeValuesAsync(
        long recipeId,
        CancellationToken ct = default);

    /// <summary>Đọc giá trị hiện tại của một tham số từ PLC.</summary>
    /// <param name="parameter">Tham số cần đọc.</param>
    /// <param name="ct">Token hủy.</param>
    Task<PlcReadResult> ReadParameterAsync(PlcParameter parameter, CancellationToken ct = default);

    /// <summary>Đọc giá trị hiện tại của tất cả tham số từ PLC.</summary>
    /// <param name="ct">Token hủy.</param>
    Task<IReadOnlyList<PlcReadResult>> ReadAllAsync(CancellationToken ct = default);

    /// <summary>Ghi một giá trị xuống PLC.</summary>
    /// <param name="parameter">Tham số cần ghi.</param>
    /// <param name="value">Giá trị (chuỗi, parse theo kiểu của tham số).</param>
    /// <param name="ct">Token hủy.</param>
    Task<PlcWriteResult> WriteParameterAsync(
        PlcParameter parameter,
        string value,
        CancellationToken ct = default);

    /// <summary>Áp dụng toàn bộ giá trị của một recipe xuống PLC.</summary>
    /// <param name="recipeId">Id recipe.</param>
    /// <param name="ct">Token hủy.</param>
    Task<IReadOnlyList<PlcWriteResult>> ApplyRecipeAsync(
        long recipeId,
        CancellationToken ct = default);
}
