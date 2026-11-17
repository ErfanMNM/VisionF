using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MHG_BoxPrint.Print.communication
{
    public class DataTimeoutMonitor : IDisposable
    {
        private readonly TimeSpan _timeout;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _lock = new object();

        public event Action Timeout;

        public DataTimeoutMonitor(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        public void Start()
        {
            Reset();
        }

        public void Feed()
        {
            Reset();
        }

        private void Reset()
        {
            CancellationTokenSource oldCts;

            lock (_lock)
            {
                oldCts = _cts;
                _cts = new CancellationTokenSource();

                _ = WaitTimeoutAsync(_cts);
            }

            oldCts.Cancel();
            oldCts.Dispose();
        }

        private async Task WaitTimeoutAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(_timeout, cts.Token);

                // Chỉ timeout nếu đây vẫn là CTS hiện tại
                lock (_lock)
                {
                    if (ReferenceEquals(cts, _cts) && !cts.IsCancellationRequested)
                    {
                        Timeout?.Invoke();
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }
}
