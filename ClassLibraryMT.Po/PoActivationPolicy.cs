namespace ClassLibraryMT.Po;

/// <summary>
/// Quyết định <c>PoActivationWorker</c> có nên chạy vòng lặp gửi kích hoạt hay không.
///
/// <para>
/// Vòng lặp chỉ vô nghĩa khi mọi request chắc chắn thất bại: HTTP server nhận PO
/// trên <b>cùng máy</b> đang tắt (<c>PoServer:Enabled = false</c>) mà
/// <see cref="PoOptions.BaseUrl"/> lại trỏ về loopback (127.0.0.1 / localhost / ::1).
/// Khi đó retry mỗi 2,5 giây chỉ tạo log rác, không chờ được server "sống lại".
/// </para>
///
/// <para>
/// Lớp này thuần logic (không phụ thuộc DI / UI) để test được từ
/// <c>tools/PoTests</c>. Việc bật/tắt theo <c>PoServer:Enabled</c> do nơi gọi
/// truyền vào — <c>ClassLibraryMT.Po</c> không tham chiếu
/// <c>ClassLibraryMT.PoServer</c>.
/// </para>
/// </summary>
public static class PoActivationPolicy
{
    /// <summary>
    /// Kiểm tra <paramref name="baseUrl"/> có trỏ về chính máy đang chạy hay không
    /// (localhost, dải 127.0.0.0/8, ::1).
    ///
    /// <para>
    /// URL rỗng hoặc không parse được trả về <c>false</c> — KHÔNG chặn worker,
    /// để lỗi cấu hình vẫn hiện ra trong log thay vì bị "im lặng".
    /// </para>
    /// </summary>
    /// <param name="baseUrl">Địa chỉ base của server PO (có thể null/rỗng).</param>
    /// <returns>True nếu địa chỉ là loopback.</returns>
    public static bool IsLoopbackBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return false;

        // Uri.IsLoopback xử lý sẵn localhost, 127.x.x.x và [::1].
        return uri.IsLoopback;
    }

    /// <summary>
    /// True nếu worker nên chạy vòng lặp gửi kích hoạt.
    ///
    /// <para>
    /// False khi <see cref="PoOptions.AutoActivate"/> = false, hoặc khi PoServer
    /// cùng máy đang tắt và <see cref="PoOptions.BaseUrl"/> là loopback.
    /// </para>
    /// </summary>
    /// <param name="options">Cấu hình section <c>Po</c>.</param>
    /// <param name="poServerEnabled">Giá trị <c>PoServer:Enabled</c>.</param>
    /// <returns>True nếu nên chạy worker.</returns>
    public static bool ShouldRun(PoOptions options, bool poServerEnabled)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.AutoActivate)
            return false;

        // Có server cùng máy đang bật → chạy bình thường (kể cả BaseUrl loopback).
        // Server tắt nhưng BaseUrl trỏ máy khác (MES thật) → vẫn chạy.
        return poServerEnabled || !IsLoopbackBaseUrl(options.BaseUrl);
    }
}
