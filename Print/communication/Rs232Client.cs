using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MHG_BoxPrint.Print.communication
{
    public class Rs232Client : IDisposable
    {
        private SerialPort _serial;
        private readonly object _lock = new object();
        private string _buffer = "";

        public string PortName = "COM9";
        public bool IsOpen => _serial?.IsOpen == true;
        public bool IsConnected = false;

        // Event nhận dữ liệu dạng dòng
        public event Action<string> OnDataReceived;
        public event Action<bool,string> OnLog;

        BackgroundWorker _worker = new BackgroundWorker();

        public void LOAD()
        {
            return;
            _worker.DoWork += _worker_DoWork;
            _worker.WorkerSupportsCancellation = true;
            if (!_worker.IsBusy)
            {
                _worker.RunWorkerAsync();
            }
        }

        public void UNLOAD()
        {
            if (_worker.IsBusy)
            {
                _worker.CancelAsync();
            }
            Disconnect();
        }

        private void _worker_DoWork(object sender, DoWorkEventArgs e)
        {
            while (!_worker.CancellationPending)
            {
                if (!IsOpen || !IsConnected)
                {
                    Log(false, "Cổng COM bị ngắt kết nối, đang thử kết nối lại...");
                    Connect(PortName);
                }
                else
                {

                }
                Thread.Sleep(10000);
            }
        }

        private bool ComPortExists(string portName)
        {
            return SerialPort.GetPortNames()
                             .Any(p => p.Equals(portName, StringComparison.OrdinalIgnoreCase));
        }

        public void Connect(string portName, int baudRate = 9600)
        {
            if (IsOpen)
            {
                Log(false, "Cổng COM đã được mở.");
                return;
            }

            if (!ComPortExists(portName))
            {
                Log(false, $"{portName} không tồn tại.");
                return;
            }
            try
            {
                _serial = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
                _serial.Encoding = Encoding.ASCII;
                _serial.DataReceived += Serial_DataReceived;

                _serial.Open();
                Log(true, $"Đã kết nối: {portName}");
                IsConnected = true;

            }
            catch (Exception ex)
            {
                Log(false, $"Không thể mở cổng {portName}: {ex.Message}");
                IsConnected = false;
            }
        }

        public void Disconnect()
        {
            if (!IsOpen)
            {
                Log(false, "Cổng COM chưa được mở.");
                return;
            }
            try
            {

                _serial.DataReceived -= Serial_DataReceived;
                _serial.Close();
                _serial.Dispose();
                _serial = null;
            }
            catch (Exception ex)
            {
                Log(false, $"Đóng cổng thất bại: {ex.Message}");
            }

            Log(false, "Ngắt kết nối.");
            IsConnected = false;
        }

        private void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string data = _serial.ReadExisting();

                lock (_lock)
                {
                    _buffer += data;

                    while (_buffer.Contains(";"))
                    {
                        int index = _buffer.IndexOf(";");
                        string line = _buffer.Substring(0, index);

                        _buffer = _buffer.Substring(index + 1);

                        OnDataReceived?.Invoke(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(false, $"Lỗi khi nhận dữ liệu: {ex.Message}");
            }
        }

        public void Send(string data)
        {
            if (!IsOpen) return;

            lock (_lock)
            {
                _serial.Write(data+";\n");
            }
        }

        public void SendLine(string data)
        {
            Send(data + "\r\n");
        }

        // Gửi và chờ phản hồi (request/response)
        public async Task<string> SendAndWaitAsync(string data, int timeoutMs = 5000)
        {
            var tcs = new TaskCompletionSource<string>();
            CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);

            void handler(string line)
            {
                tcs.TrySetResult(line);
            }

            OnDataReceived += handler;

            try
            {
                SendLine(data);

                using (cts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    return await tcs.Task;
                }
            }
            catch
            {
                throw new TimeoutException("No response");
            }
            finally
            {
                OnDataReceived -= handler;
            }
        }

        private void Log(bool b, string msg)
        {
            OnLog?.Invoke(b, msg);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
