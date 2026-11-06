using System.Diagnostics;

namespace TanTienQR.Services;

/// <summary>
/// Bộ đếm số liệu runtime của app (singleton).
///
/// Dùng cho tab "Dev" để theo dõi hoạt động thực tế: bao nhiêu lần ghi
/// heartbeat (D16), bao nhiêu lần ghi kết quả (D12), bao nhiêu mã đã quét.
///
/// Dùng <see cref="Interlocked"/> vì các số này được tăng từ nhiều luồng:
/// camera worker (đọc mã), PLC writer (ghi xuống PLC) và heartbeat service.
///
/// KHÔNG lưu lịch sử — chỉ giữ số đếm tích luỹ từ lúc app khởi động.
/// </summary>
internal sealed class RuntimeCounters
{
    // ---------- Ghi PLC ----------

    private long _heartbeatWrites;
    private long _heartbeatFailures;
    private long _resultWrites;
    private long _resultFailures;

    // ---------- Quét mã ----------

    private long _scansTotal;
    private long _scansPass;
    private long _scansFail;

    // ---------- Mốc thời gian ----------

    /// <summary>Thời điểm app khởi động (dùng để tính uptime).</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Thời điểm app khởi động theo giờ địa phương.</summary>
    public DateTime StartedLocal => StartedUtc.ToLocalTime();

    // ---------- Heartbeat (D16) ----------

    /// <summary>Số lần đã ghi trạng thái Ready xuống PLC (D16).</summary>
    public long HeartbeatWrites => Interlocked.Read(ref _heartbeatWrites);

    /// <summary>Số lần ghi D16 thất bại (PLC không phản hồi / timeout).</summary>
    public long HeartbeatFailures => Interlocked.Read(ref _heartbeatFailures);

    // ---------- Kết quả quét (D12) ----------

    /// <summary>Số lần đã ghi kết quả xuống PLC (D12).</summary>
    public long ResultWrites => Interlocked.Read(ref _resultWrites);

    /// <summary>Số lần ghi D12 thất bại.</summary>
    public long ResultFailures => Interlocked.Read(ref _resultFailures);

    // ---------- Quét mã ----------

    /// <summary>Tổng số mã đã xử lý.</summary>
    public long ScansTotal => Interlocked.Read(ref _scansTotal);

    /// <summary>Số mã hợp lệ (Pass).</summary>
    public long ScansPass => Interlocked.Read(ref _scansPass);

    /// <summary>Số mã lỗi (NoRead / sai định dạng / trùng).</summary>
    public long ScansFail => Interlocked.Read(ref _scansFail);

    // ---------- Ghi từ các service ----------

    /// <summary>Ghi nhận một lần ghi heartbeat (D16).</summary>
    /// <param name="success">True nếu ghi thành công.</param>
    public void AddHeartbeat(bool success)
    {
        Interlocked.Increment(ref _heartbeatWrites);

        if (!success)
        {
            Interlocked.Increment(ref _heartbeatFailures);
        }
    }

    /// <summary>Ghi nhận một lần ghi kết quả (D12).</summary>
    /// <param name="success">True nếu ghi thành công.</param>
    public void AddResult(bool success)
    {
        Interlocked.Increment(ref _resultWrites);

        if (!success)
        {
            Interlocked.Increment(ref _resultFailures);
        }
    }

    /// <summary>Ghi nhận một mã đã quét.</summary>
    /// <param name="pass">True nếu mã hợp lệ (Pass).</param>
    public void AddScan(bool pass)
    {
        Interlocked.Increment(ref _scansTotal);

        if (pass)
        {
            Interlocked.Increment(ref _scansPass);
        }
        else
        {
            Interlocked.Increment(ref _scansFail);
        }
    }

    /// <summary>Thời gian app đã chạy (định dạng "1h 02m 03s").</summary>
    public string UptimeText
    {
        get
        {
            var elapsed = DateTime.UtcNow - StartedUtc;

            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s"
                : $"{elapsed.Minutes}m {elapsed.Seconds}s";
        }
    }

    /// <summary>
    /// Thời gian CPU đã dùng (đọc từ process hiện tại).
    ///
    /// Dùng <c>Process.GetCurrentProcess()</c> — nhớ <c>using</c> vì nó giữ
    /// handle của process, không dispose thì rò handle mỗi lần gọi.
    /// </summary>
    public static TimeSpan GetCpuTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (InvalidOperationException)
        {
            // Process đã kết thúc -> không lấy được.
            return TimeSpan.Zero;
        }
        catch (PlatformNotSupportedException)
        {
            // Nền tảng không hỗ trợ -> bỏ qua.
            return TimeSpan.Zero;
        }
    }
}
