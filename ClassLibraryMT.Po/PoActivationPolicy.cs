namespace ClassLibraryMT.Po;

/// <summary>
/// Quyết định <c>PoActivationWorker</c> có nên chạy vòng lặp gửi kích hoạt hay không.
///
/// <para>This policy is independent of the in-app PO ingestion listener. Activation
/// can target a separate MES endpoint on loopback or a remote host.</para>
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
    /// True when activation is enabled. Server liveness is verified by each request.
    /// </summary>
    /// <param name="options">Cấu hình section <c>Po</c>.</param>
    /// <returns>True nếu nên chạy worker.</returns>
    public static bool ShouldRun(PoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AutoActivate;
    }

    /// <summary>Compatibility overload; ingestion listener state does not control activation.</summary>
    public static bool ShouldRun(PoOptions options, bool poServerEnabled) => ShouldRun(options);
}
