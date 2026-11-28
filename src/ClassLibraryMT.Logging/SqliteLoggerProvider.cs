using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> ghi mọi log của <c>ILogger&lt;T&gt;</c> xuống SQLite.
///
/// Nhờ provider này, các log kỹ thuật đang có (camera worker, PLC heartbeat,
/// license manager...) tự động chảy vào database mà không phải sửa callsite.
/// </summary>
[ProviderAlias("Sqlite")]
public sealed class SqliteLoggerProvider : ILoggerProvider
{
    private readonly SqliteLogWriter _writer;
    private readonly LogLevel _minLevel;

    /// <summary>
    /// Khởi tạo provider.
    /// </summary>
    /// <param name="writer">Worker ghi xuống SQLite.</param>
    /// <param name="options">Cấu hình (xác định mức log tối thiểu).</param>
    public SqliteLoggerProvider(SqliteLogWriter writer, IOptions<SqliteLoggingOptions> options)
    {
        _writer = writer;
        _minLevel = options.Value.MinLevel;
    }

    /// <summary>
    /// Tạo logger cho một category.
    /// </summary>
    /// <param name="categoryName">Tên category.</param>
    public ILogger CreateLogger(string categoryName) => new SqliteLogger(_writer, categoryName, _minLevel);

    /// <summary>Giải phóng tài nguyên (không cần làm gì thêm).</summary>
    public void Dispose()
    {
        // Worker được dispose bởi DI container (đăng ký singleton).
    }

    /// <summary>
    /// Logger ghi từng entry xuống hàng đợi của writer.
    /// </summary>
    private sealed class SqliteLogger : ILogger
    {
        private readonly SqliteLogWriter _writer;
        private readonly string _category;
        private readonly LogLevel _minLevel;

        public SqliteLogger(SqliteLogWriter writer, string category, LogLevel minLevel)
        {
            _writer = writer;
            _category = category;
            _minLevel = minLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
        {
            // None luôn tắt; các mức khác so với ngưỡng cấu hình.
            return logLevel != LogLevel.None && logLevel >= _minLevel;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter is not null
                ? formatter(state, exception)
                : state?.ToString() ?? string.Empty;

            _writer.Enqueue(new AppLogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Level = logLevel.ToString(),
                Category = _category,
                EventId = eventId.Id,
                Message = message,
                Exception = exception?.ToString(),
                PropertiesJson = BuildPropertiesJson(state),
            });
        }

        /// <summary>
        /// Serialize các thuộc tính bổ sung của log state (ngoài message) thành JSON.
        /// </summary>
        private static string? BuildPropertiesJson<TState>(TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.Append('{');
            var first = true;

            foreach (var (key, value) in pairs)
            {
                if (string.Equals(key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(LogJsonOptions.SerializeString(key));
                builder.Append(':');
                builder.Append(LogJsonOptions.SerializeString(value?.ToString() ?? string.Empty));
            }

            builder.Append('}');

            var json = builder.ToString();
            return json == "{}" ? null : json;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        private NullScope()
        {
        }

        public void Dispose()
        {
        }
    }
}
