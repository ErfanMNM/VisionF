namespace TanTienQR.Services;

// Đồng bộ việc nhận lượt quét mới với lúc bắt đầu bảo trì.
public sealed class ScanIntakeGate
{
    private readonly Lock _gate = new();
    private volatile bool _suspended;

    public bool IsSuspended => _suspended;

    internal void SetSuspended(bool suspended)
    {
        lock (_gate)
            _suspended = suspended;
    }

    internal void Dispatch(Action admit)
    {
        lock (_gate)
            if (!_suspended)
                admit();
    }
}
