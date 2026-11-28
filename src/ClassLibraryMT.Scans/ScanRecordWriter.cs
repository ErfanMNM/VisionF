using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ClassLibraryMT.Scans;

/// <summary>
/// Worker nền ghi bản ghi quét mã vào SQLite.
///
/// Mọi lệnh ghi enqueue vào <see cref="Channel{T}"/> và chờ ACK commit SQLite.
/// Worker gom batch rồi insert trong một transaction.
///
/// Tính năng:
/// - WAL mode + busy_timeout để đọc không bị chặn bởi worker ghi.
/// - Flush theo thời gian (<see cref="ScanStoreOptions.FlushIntervalMs"/>) hoặc
///   khi batch đủ (<see cref="ScanStoreOptions.BatchSize"/>).
/// - Prune theo retention mỗi <see cref="ScanStoreOptions.PruneIntervalMs"/>.
/// - Queue đầy hoặc database tạm lỗi tạo backpressure/retry, không bỏ record.
/// - Khi <see cref="Dispose"/> thì flush toàn bộ bản ghi còn trong queue trước khi đóng.
///
/// Lỗi ghi ra stderr qua <see cref="ScanDiagnostics"/> — cố ý KHÔNG nhận
/// <c>ILogger</c> để tránh phụ thuộc vòng tròn trong DI.
/// </summary>
internal sealed class ScanRecordWriter : IScanRecordWriter, IDisposable
{
    private readonly ScanStoreOptions _opt;
    private readonly ScanStore _store;
    private readonly Channel<object> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private volatile bool _disposed;
    private Exception? _writeFailure;

    /// <summary>Số bản ghi đã ghi thành công (dùng cho test/thống kê).</summary>
    private long _writtenCount;

    /// <summary>
    /// Khởi tạo worker ghi bản ghi quét mã.
    /// </summary>
    /// <param name="options">Cấu hình lưu trữ.</param>
    /// <param name="store">Lớp lưu trữ (mở connection + insert).</param>
    public ScanRecordWriter(IOptions<ScanStoreOptions> options, ScanStore store)
    {
        _opt = options.Value;
        _store = store;

        // Bounded channel tạo backpressure thay vì bỏ lịch sử. Enqueue có thể
        // chờ khi writer chậm, nhưng mỗi bản ghi camera vẫn được giữ nguyên.
        _queue = Channel.CreateBounded<object>(
            new BoundedChannelOptions(Math.Max(1, _opt.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        _worker = Task.Run(() => RunAsync(_cts.Token));

        // Tạo file DB + schema ngay khi khởi động (nền, không chặn startup).
        // Nếu không, file chỉ được tạo lúc có bản ghi đầu tiên -> người dùng
        // mở thư mục logs lúc app đang chạy mà không thấy file scans.db.
        _ = Task.Run(() => EnsureSchemaAsync(_cts.Token));
    }

    /// <summary>
    /// Tạo file DB và schema (nếu chưa có). Lỗi thì chỉ ghi ra stderr —
    /// không được làm app không khởi động được.
    /// </summary>
    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            ScanDiagnostics.Error("Khong tao duoc file DB ban ghi quet ma.", ex);
        }
    }

    /// <summary>Đường dẫn file database đang dùng.</summary>
    public string DatabasePath => _store.DatabasePath;

    /// <summary>Số bản ghi đã ghi thành công.</summary>
    public long WrittenCount => Interlocked.Read(ref _writtenCount);

    /// <summary>
    /// Đưa một bản ghi vào hàng đợi và chờ tới khi SQLite commit.
    /// </summary>
    /// <param name="record">Bản ghi cần lưu.</param>
    public void Enqueue(ScanRecordEntity record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (_disposed)
        {
            var error = new ObjectDisposedException(nameof(ScanRecordWriter));
            RecordFailure(error);
            throw error;
        }

        var pending = new PendingRecord(record);
        try
        {
            // Chờ chỗ trong hàng đợi lẫn ACK commit SQLite. Enqueue thành công
            // nghĩa là lịch sử đã nằm bền vững trong DB.
            _queue.Writer.WriteAsync(pending).AsTask().GetAwaiter().GetResult();
            pending.Wait();
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            throw;
        }
    }

    /// <summary>
    /// Chờ cho tới khi hàng đợi rỗng và dữ liệu đã commit vào database.
    /// </summary>
    /// <param name="cancellationToken">Token hủy.</param>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        // Yêu cầu flush có phản hồi: worker chỉ hoàn thành yêu cầu sau khi đã
        // ghi xong batch vào database. Nếu chỉ chờ "channel rỗng" thì sẽ bị
        // race: worker đã gom bản ghi vào danh sách cục bộ nhưng chưa kịp insert.
        var request = new FlushRequest();

        await _queue.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await request.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task VerifyHealthyAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _store.OpenAsync(cancellationToken).ConfigureAwait(false);
            // BEGIN IMMEDIATE xác nhận quyền ghi cả khi SQLite mở fallback read-only.
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _writeFailure, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordFailure(ex);
            throw new IOException("Không xác minh được database bản ghi quét mã.", ex);
        }
        if (_worker.IsCompleted)
            throw new IOException("Writer bản ghi quét mã đã dừng.");
    }

    private void RecordFailure(Exception cause) => Interlocked.CompareExchange(ref _writeFailure, cause, null);

    private async Task RunAsync(CancellationToken ct)
    {
        var pending = new List<PendingRecord>(capacity: _opt.BatchSize);
        var lastFlush = Environment.TickCount64;
        var lastPrune = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Chờ có phần tử mới, nhưng không lâu hơn 1/4 chu kỳ flush
                // để batch luôn được đẩy xuống đúng nhịp.
                var waitMs = Math.Max(10, _opt.FlushIntervalMs / 4);
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(waitMs);

                bool hasItem;
                try
                {
                    hasItem = await _queue.Reader.WaitToReadAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Hết thời gian chờ -> vòng lặp tiếp theo kiểm tra nhịp flush.
                    hasItem = false;
                }

                if (!hasItem && _queue.Reader.Completion.IsCompleted)
                    break;

                if (hasItem)
                {
                    // Gom tối đa BatchSize phần tử đang có sẵn (không chặn).
                    while (pending.Count < _opt.BatchSize && _queue.Reader.TryRead(out var item))
                    {
                        if (item is FlushRequest request)
                        {
                            // Yêu cầu flush: ghi nốt phần đang dở rồi báo hoàn thành.
                            await FlushBatchAsync(pending, ct).ConfigureAwait(false);
                            request.Complete();
                            continue;
                        }

                        if (item is PendingRecord record)
                        {
                            pending.Add(record);
                        }
                    }
                }

                var now = Environment.TickCount64;

                // Đủ batch -> flush ngay.
                if (pending.Count >= _opt.BatchSize)
                {
                    await FlushBatchAsync(pending, ct).ConfigureAwait(false);
                    lastFlush = now;
                }
                // Hoặc đã quá chu kỳ flush -> flush phần đang dở.
                else if (pending.Count > 0 && now - lastFlush >= _opt.FlushIntervalMs)
                {
                    await FlushBatchAsync(pending, ct).ConfigureAwait(false);
                    lastFlush = now;
                }

                // Chu kỳ prune: xoá bản ghi cũ hơn retention.
                if (_opt.RetentionDays > 0 && now - lastPrune >= _opt.PruneIntervalMs)
                {
                    await PruneAsync(ct).ConfigureAwait(false);
                    lastPrune = now;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                // Worker không được chết vì lỗi ghi DB. Log ra stderr và tiếp tục.
                ScanDiagnostics.Error("Loi trong worker ghi ban ghi quet ma SQLite.", ex);
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Tắt: flush nốt phần đang dở + hoàn thành barrier sau khi đã commit.
        var pendingFlushRequests = new List<FlushRequest>();
        try
        {
            while (_queue.Reader.TryRead(out var item))
            {
                if (item is FlushRequest request)
                {
                    pendingFlushRequests.Add(request);
                    continue;
                }

                if (item is PendingRecord record)
                {
                    pending.Add(record);
                }
            }

            if (pending.Count > 0)
            {
                await FlushBatchAsync(pending, ct).ConfigureAwait(false);
            }
            foreach (var request in pendingFlushRequests) request.Complete();
            pendingFlushRequests.Clear();
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
            foreach (var record in pending) record.Fail(ex);
            foreach (var request in pendingFlushRequests) request.Fail(ex);
            ScanDiagnostics.Error("Khong flush duoc ban ghi con lai khi dong worker.", ex);
        }
    }

    private async Task FlushBatchAsync(List<PendingRecord> pending, CancellationToken ct)
    {
        if (pending.Count == 0)
        {
            return;
        }

        // Giữ batch trong bộ nhớ cho tới khi SQLite xác nhận transaction đã
        // commit. Lỗi tạm thời được retry; Flush chỉ được hoàn tất sau khi ghi.
        while (pending.Count > 0)
        {
            var batch = new List<PendingRecord>(pending);
            try
            {
                await using var connection = await _store.OpenAsync(ct).ConfigureAwait(false);
                await _store.InsertBatchAsync(connection, batch.Select(item => item.Record).ToArray(), ct)
                    .ConfigureAwait(false);

                pending.Clear();
                Interlocked.Add(ref _writtenCount, batch.Count);
                Interlocked.Exchange(ref _writeFailure, null);
                foreach (var item in batch) item.Complete();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                ScanDiagnostics.Error(
                    $"Ghi batch ban ghi quet ma that bai ({batch.Count} ban ghi); se thu lai.", ex);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_opt.RetentionDays);
            var removed = await _store.PruneAsync(cutoff, ct).ConfigureAwait(false);

            if (removed > 0)
            {
                ScanDiagnostics.Info(
                    $"Da don {removed} ban ghi quet ma cu hon {_opt.RetentionDays} ngay.");
            }
        }
        catch (Exception ex)
        {
            ScanDiagnostics.Error("Khong prune duoc ban ghi quet ma cu.", ex);
        }
    }

    /// <summary>
    /// Dừng worker và flush toàn bộ bản ghi còn trong hàng đợi.
    ///
    /// Idempotent — có thể được gọi nhiều lần (DI container + thao tác tắt app).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // Đóng channel để worker biết hết việc -> flush nốt rồi thoát.
            _queue.Writer.TryComplete();
            if (!_worker.Wait(TimeSpan.FromSeconds(5)))
            {
                _cts.Cancel();
                if (!_worker.Wait(TimeSpan.FromSeconds(5)))
                {
                    var error = new IOException("Writer dừng trước khi xác nhận commit một số bản ghi scan.");
                    FailQueuedItems(error);
                    ScanDiagnostics.Error("Writer scan chưa dừng sau timeout; các producer còn chờ được báo lỗi.", error);
                }
            }
        }
        catch (Exception ex)
        {
            ScanDiagnostics.Error("Loi khi dong worker ghi ban ghi quet ma SQLite.", ex);
        }
        finally
        {
            // Nếu worker đã thoát bất thường, đánh lỗi cho producer đang chờ;
            // không giả báo các bản ghi chưa commit là hoàn tất.
            _cts.Cancel();
            if (_worker.IsCompleted)
                FailQueuedItems(new ObjectDisposedException(nameof(ScanRecordWriter),
                    "Writer stopped before this record was committed."));
            else
                _ = _worker.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
        }
    }

    private void FailQueuedItems(Exception error)
    {
        while (_queue.Reader.TryRead(out var item))
        {
            if (item is FlushRequest request) request.Fail(error);
            if (item is PendingRecord record) record.Fail(error);
        }
    }

    /// <summary>
    /// Yêu cầu flush kèm phản hồi: worker chỉ gọi <see cref="Complete"/> sau khi
    /// đã ghi xong batch vào database, nên <see cref="FlushAsync"/> chỉ trả về
    /// khi dữ liệu thực sự nằm trong file.
    /// </summary>
    private sealed class FlushRequest
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Chờ worker hoàn tất việc ghi.</summary>
        public Task WaitAsync(CancellationToken cancellationToken) =>
            _tcs.Task.WaitAsync(cancellationToken);

        /// <summary>Báo cho bên chờ biết đã ghi xong.</summary>
        public void Complete() => _tcs.TrySetResult();
        public void Fail(Exception error) => _tcs.TrySetException(error);
    }

    private sealed class PendingRecord(ScanRecordEntity record)
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScanRecordEntity Record { get; } = record;
        public void Complete() => _tcs.TrySetResult();
        public void Fail(Exception error) => _tcs.TrySetException(error);
        public void Wait() => _tcs.Task.GetAwaiter().GetResult();
    }
}
