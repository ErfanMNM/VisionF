namespace TanTienQR.Services;

// Đồng bộ việc nhận lượt quét mới với lúc bắt đầu bảo trì.
public sealed class ScanIntakeGate
{
    private readonly Lock _gate = new();
    private volatile bool _suspended;
    private bool _maintenanceSuspended;
    private int _suspensionLeases;

    public bool IsSuspended => _suspended;

    internal void SetSuspended(bool suspended)
    {
        lock (_gate)
        {
            _maintenanceSuspended = suspended;
            _suspended = _maintenanceSuspended || _suspensionLeases > 0;
        }
    }

    // Mỗi thao tác giữ lease riêng để không mở lại intake của một tác vụ khác.
    internal IDisposable Suspend()
    {
        lock (_gate)
        {
            _suspensionLeases++;
            _suspended = true;
        }
        return new Suspension(this);
    }

    internal IDisposable? TrySuspendForDeviceApply(int retainedSuspensionLeases)
    {
        lock (_gate)
        {
            if (_maintenanceSuspended || _suspensionLeases != retainedSuspensionLeases)
                return null;

            _suspensionLeases++;
            _suspended = true;
            return new Suspension(this);
        }
    }

    private void ReleaseSuspension()
    {
        lock (_gate)
        {
            _suspensionLeases--;
            _suspended = _maintenanceSuspended || _suspensionLeases > 0;
        }
    }

    private sealed class Suspension(ScanIntakeGate owner) : IDisposable
    {
        private ScanIntakeGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseSuspension();
    }

    internal void Dispatch(Action admit)
    {
        lock (_gate)
            if (!_suspended)
                admit();
    }
}
