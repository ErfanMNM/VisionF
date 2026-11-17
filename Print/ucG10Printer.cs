using MHG_BoxPrint.Print.command;
using MHG_BoxPrint.Print.communication;
using MHG_BoxPrint.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;

namespace MHG_BoxPrint.Print
{
    public partial class ucG10Printer : UserControl
    {
        public delegate void ucG10PrinterCallback(PrinterStatus status, string s);
        public delegate void ucG10PrinterucG10PrinterFile(List<PrinterFile> files, string file);
        public delegate void FileChangeEvent(PrinterFile file);

        public event ucG10PrinterCallback Callback;
        public event ucG10PrinterucG10PrinterFile CallbackFile;
        public event FileChangeEvent OnFileChanged;

        private AsyncTcpClient client;
        private Rs232Client rs232Client;

        public bool IsConnected { get; set; }
        public bool IsPrinting { get; set; }
        public string Job { get; set; } = "";

        public string PrinterIp { get; set; } = "192.168.0.3";
        public int PrinterPort { get; set; } = 6007;
        public string PrinterComPort { get; set; } = "COM7";

        private bool _lastStt;
        private bool _stt;
        private bool _lock;
        private bool laststatus1;
        private bool statup1;

        private const int MaxCount = 100;
        private const int MaxData = 1000000;

        private string DataGlobal = "";
        private int Index = 0;

        private readonly CommandQueueManager commandQueueManagerTCP = new CommandQueueManager();
        private readonly CommandQueueManager commandQueueManagerRS232 = new CommandQueueManager();

        private readonly PrinterData printerData = new PrinterData();

        // Chỉ cho phép một thao tác điều khiển máy in tại một thời điểm.
        private readonly SemaphoreSlim _operationSemaphore = new SemaphoreSlim(1, 1);

        // Quản lý việc tạo danh sách dữ liệu.
        private readonly object _generationSync = new object();
        private CancellationTokenSource _cts;

        // _unloaded: đã yêu cầu dừng (chặn các callback/gửi lệnh mới).
        // _unloadCompleted: tất cả thành phần đã xác nhận dừng xong.
        private volatile bool _unloaded;
        private volatile bool _unloadCompleted;
        private bool _tcpQueueStarted;

        private static readonly object _logSync = new object();
        private static string _currentLogPath;
        private static DateTime _currentLogDate = DateTime.MinValue;

        public string FileLog
        {
            get
            {
                lock (_logSync)
                {
                    DateTime today = DateTime.Today;

                    if (!string.IsNullOrEmpty(_currentLogPath) &&
                        _currentLogDate == today)
                    {
                        return _currentLogPath;
                    }

                    string todayStr = DateTime.Now.ToString("yyyy-MM-dd");

                    string directory = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "Log",
                        todayStr);

                    Directory.CreateDirectory(directory);

                    string filePath = Path.Combine(
                        directory,
                        $"PrinterLog_{todayStr}.txt");

                    if (File.Exists(filePath))
                    {
                        int count = 2;

                        while (File.Exists(Path.Combine(
                            directory,
                            $"PrinterLog_{todayStr}_{count}.txt")))
                        {
                            count++;
                        }

                        filePath = Path.Combine(
                            directory,
                            $"PrinterLog_{todayStr}_{count}.txt");
                    }

                    using (File.Create(filePath)) { }

                    _currentLogPath = filePath;
                    _currentLogDate = today;

                    return _currentLogPath;
                }
            }
        }

        public ucG10Printer()
        {
            InitializeComponent();
        }

        // =========================================================
        // CALLBACK
        // =========================================================

        public void InvokeCallback(PrinterStatus status, string s)
        {
            if (IsDisposed || Disposing)
                return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        Callback?.Invoke(status, s);
                    }));
                }
                catch { }

                return;
            }

            Callback?.Invoke(status, s);
        }

        private void InvokeCallbackFile(List<PrinterFile> files, string file)
        {
            if (IsDisposed || Disposing)
                return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        CallbackFile?.Invoke(files, file);
                    }));
                }
                catch { }

                return;
            }

            CallbackFile?.Invoke(files, file);
        }

        // =========================================================
        // LOG
        // =========================================================

        private void Logs(string message)
        {
            string log = $"{DateTime.Now:dd-MM-yyyy HH:mm:ss} - {message}";

            try
            {
                lock (_logSync)
                {
                    File.AppendAllText(
                        FileLog,
                        log + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }

            if (IsDisposed || Disposing)
                return;

            listBox1.SafeInvoke(() =>
            {
                if (listBox1.Items.Count >= 100)
                    listBox1.Items.RemoveAt(listBox1.Items.Count - 1);

                listBox1.Items.Insert(0, log);
            });
        }

        // =========================================================
        // LOAD
        // =========================================================

        public void LOAD()
        {
            _unloaded = false;
            _unloadCompleted = false;

            try
            {
                if (File.Exists(FileLog))
                {
                    string[] lines = File.ReadAllLines(FileLog);

                    listBox1.Items.AddRange(
                        lines.Take(100).Reverse().ToArray());
                }

                Logs("------- Mở ứng dụng -------");
            }
            catch (Exception ex)
            {
                Logs($"Lỗi đọc log: {ex.Message}");
            }

            InitTCP();

            commandQueueManagerTCP.AddNormal(CommandString.Counter, 2000);
            commandQueueManagerTCP.AddNormal(CommandString.Status, 2000);
            commandQueueManagerTCP.AddNormal(CommandString.GFile, 2000);
            commandQueueManagerTCP.AddNormal(CommandString.Ink, 5000);
            commandQueueManagerTCP.AddNormal(CommandString.MFDLEN, 2000);

            commandQueueManagerTCP.CommandSent += (sender, cmd) =>
            {
                if (client != null && client.Connected && !_unloaded)
                {
                    client.Send(cmd);
                }
            };

            StartTcpQueue();

            // Nếu sử dụng RS232, gọi InitRS232() tại đây.
        }

        private void InitTCP()
        {
            client = new AsyncTcpClient
            {
                IP = PrinterIp,
                Port = PrinterPort
            };

            client.ClientCallBack += (aa, bb) =>
            {
                if (_unloaded)
                    return;

                switch (aa)
                {
                    case enumClient.CONNECTED:

                        commandQueueManagerTCP.AddPriority(CommandString.Counter, 500);
                        commandQueueManagerTCP.AddPriority(CommandString.Status, 500);
                        commandQueueManagerTCP.AddPriority(CommandString.GFile, 500);
                        commandQueueManagerTCP.AddPriority(CommandString.MFDLEN, 500);

                        _stt = true;
                        break;

                    case enumClient.DISCONNECTED:

                        IsConnected = false;
                        _stt = false;

                        if (_lastStt)
                        {
                            _lastStt = false;

                            Logs("TCP: Đã ngắt kết nối - " + bb);

                            label1.SafeInvoke(() =>
                            {
                                label1.ForeColor = Color.Red;
                            });

                            InvokeCallback(PrinterStatus.Tcp_Disconnected, bb);
                        }

                        break;

                    case enumClient.RECEIVED:

                        IsConnected = true;

                        ClientCallBack(bb, true);

                        if (_stt && !_lastStt)
                        {
                            _lastStt = true;

                            label1.SafeInvoke(() =>
                            {
                                label1.ForeColor = Color.Green;
                            });

                            Logs("TCP: Đã kết nối");

                            InvokeCallback(PrinterStatus.Tcp_Connected, "");
                        }

                        break;
                }
            };

            client.LOAD();
        }

        private void InitRS232()
        {
            rs232Client = new Rs232Client
            {
                PortName = PrinterComPort
            };

            rs232Client.OnDataReceived += data =>
            {
                ClientCallBack(data, false);
            };

            rs232Client.OnLog += (connected, log) =>
            {
                Logs($"RS232: {log}");

                label2.SafeInvoke(() =>
                {
                    label2.ForeColor = connected ? Color.Green : Color.Red;
                });

                InvokeCallback(
                    connected
                        ? PrinterStatus.Rs232_Connected
                        : PrinterStatus.Rs232_Disconnected,
                    "");
            };

            rs232Client.LOAD();
        }

        // =========================================================
        // QUEUE CONTROL
        // =========================================================

        private void StartTcpQueue()
        {
            if (_unloaded || _tcpQueueStarted)
                return;

            commandQueueManagerTCP.Start();
            _tcpQueueStarted = true;
        }

        // Trả về true nếu queue đã dừng hẳn.
        private bool StopTcpQueue()
        {
            if (!_tcpQueueStarted)
                return true;

            bool stopped = commandQueueManagerTCP.Stop();

            if (stopped)
                _tcpQueueStarted = false;
            else
                Logs("TCP Queue chưa dừng hẳn (hết thời gian chờ).");

            return stopped;
        }

        // =========================================================
        // PRINTER CALLBACK
        // =========================================================

        private void ClientCallBack(string rawData, bool TCP)
        {
            if (string.IsNullOrEmpty(rawData))
                return;

            char separator = TCP ? '|' : ':';

            string[] parts = rawData.Split(separator);

            string cmd = parts[0];

            string data = parts.Length > 1
                ? string.Join(separator.ToString(), parts.Skip(1))
                : "";

            switch (cmd)
            {
                case "RESET":

                    if (data.StartsWith("OK"))
                    {
                        printerData.PrintedCount = 0;
                        UpdateCounter();
                        Logs("Xóa số đếm thành công.");
                    }

                    break;

                case "CLR":

                    if (data.StartsWith("OK"))
                        Logs("Xóa dữ liệu cũ thành công.");

                    break;

                case "BACK":

                    if (_lock)
                        break;

                    if (TryParseInt(data, out int backVal))
                    {
                        Console.WriteLine($"BACK: {backVal}");

                        printerData.Pinted();

                        Send1Data();

                        UpdateStatus();
                        UpdateCounter();
                    }

                    break;

                case "MFD":

                    if (_lock)
                        break;

                    if (data.StartsWith("OK"))
                    {
                        Console.WriteLine($"MFD: {data}");
                        printerData.SendSuccess();
                    }
                    else
                    {
                        Console.WriteLine($"MFD: {data}");

                        printerData.SendFailure();

                        if (printerData.ConsecutiveFailure == false)
                            Send1Data();
                    }

                    UpdateStatus();
                    break;

                case "MFDLEN":

                    if (_lock)
                        break;

                    if (TryParseInt(data, out int mfdLength))
                    {
                        printerData.MFDLEN = mfdLength;

                        if (mfdLength < MaxCount &&
                            printerData.DataList.Count > 0 &&
                            IsPrinting)
                        {
                            int count = mfdLength;

                            while (count < MaxCount)
                            {
                                Send1Data();
                                count++;
                            }
                        }

                        UpdateStatus();
                    }

                    break;

                case "CSTATUS":

                    if (TryParseInt(data, out int counter))
                    {
                        printerData.PrintedCount = counter;
                        Console.WriteLine($"CSTATUS: {counter}");
                        UpdateCounter();
                    }

                    break;

                case "STATUS":

                    if (TryParseInt(data, out int status))
                        UpdatePrinterStatus(status == 1);

                    break;

                case "INKV":

                    if (TryParseDouble(data, out double ink1, out double ink2))
                    {
                        lblInk.SafeInvoke(() =>
                        {
                            lblInk.Text = $"Mực: {ink1:F2}%, {ink2:F2}%";
                        });

                        InvokeCallback(PrinterStatus.Ink1, ink1.ToString());
                        //InvokeCallback(PrinterStatus.Ink2, ink2.ToString());

                        if (ink1 < 10)
                            InvokeCallback(PrinterStatus.Ink1_Low, $"{ink1:F2}%");

                        //if (ink2 < 10)
                        //    InvokeCallback(PrinterStatus.Ink2_Low, $"{ink2:F2}%");
                    }

                    break;

                case "START":

                    if (data.StartsWith("OK"))
                        UpdatePrinterStatus(true);

                    break;

                case "STOP":

                    if (data.StartsWith("OK"))
                        UpdatePrinterStatus(false);

                    break;

                case "GFILE":

                    ProcessGFile(data);
                    break;
            }
        }

        private void ProcessGFile(string data)
        {
            string currentId = "";

            List<PrinterFile> files = new List<PrinterFile>();

            string[] items = data.Split(',');

            foreach (string item in items)
            {
                if (item.StartsWith("CU:"))
                {
                    currentId = item.Substring(3);
                }
                else
                {
                    string[] p = item.Split(new char[] { ':' }, 2);

                    if (p.Length == 2)
                    {
                        files.Add(new PrinterFile
                        {
                            Id = p[0].Trim(),
                            Name = p[1].Trim('\r', '\n', ' ')
                                .Replace("\\r", "")
                                .Replace("\\n", "")
                        });
                    }
                }
            }

            var currentFile = files.FirstOrDefault(x => x.Id == currentId);

            Job = currentFile != null ? currentFile.Name : string.Empty;

            if (!string.IsNullOrEmpty(Job))
            {
                LoadFile(files, Job);
                InvokeCallbackFile(files, Job);
            }

            UpdateJob();
        }

        private void UpdatePrinterStatus(bool isPrinting)
        {
            IsPrinting = isPrinting;

            btnPrint.SafeInvoke(() =>
            {
                btnPrint.BackColor = isPrinting ? Color.Red : Color.Green;
                btnPrint.Text = isPrinting ? "■ DỪNG IN" : "▶ BẬT IN";
            });

            UpdateJob();

            if (laststatus1 != isPrinting || !statup1)
            {
                statup1 = true;
                laststatus1 = isPrinting;

                Logs(isPrinting ? "Máy in đang in." : "Máy in đã dừng.");

                InvokeCallback(
                    isPrinting
                        ? PrinterStatus.Printer_Printing
                        : PrinterStatus.Printer_Stopped,
                    "");
            }
        }

        private bool TryParseInt(string input, out int value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.Trim('\r', '\n', ' ')
                .Replace("\\r", "")
                .Replace("\\n", "");

            string[] parts = input.Split(',');

            return int.TryParse(parts[0], out value);
        }

        private bool TryParseDouble(string input, out double value1, out double value2)
        {
            value1 = 0;
            value2 = 0;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            string[] parts = input.Split(',');

            if (parts.Length < 1 || parts.Length > 2)
                return false;

            if (!double.TryParse(parts[0].Trim(), out value1))
                return false;

            if (parts.Length == 2 && !double.TryParse(parts[1].Trim(), out value2))
                return false;

            return true;
        }

        // =========================================================
        // START / STOP PRINTER
        // =========================================================

        public void StartPrinter(bool sync = false)
        {
            if (!sync)
            {
                commandQueueManagerTCP.AddPriority(CommandString.Start, 50);
                commandQueueManagerRS232.AddPriority(CommandString.Start, 50);
            }
            else
            {
                string response = client.SendAndRead(
                    $"{CommandString.Start}", "\r\n", 10000);

                if (response == null || !response.StartsWith("START|OK"))
                    Logs($"Không nhận được xác nhận START: {response ?? "Timeout"}");
            }

            statup1 = false;
        }

        public void StopPrinter(bool sync = false)
        {
            if (!sync)
            {
                commandQueueManagerTCP.AddPriority(CommandString.Stop, 50);
                //  commandQueueManagerRS232.AddPriority(CommandString.Stop, 50);
            }
            else
            {
                string response = client.SendAndRead(
                    $"{CommandString.Stop}", "\r\n", 5000);

                if (response == null || !response.StartsWith("STOP|OK"))
                    Logs($"Không nhận được xác nhận STOP: {response ?? "Timeout"}");
            }

            statup1 = false;
        }

        // =========================================================
        // ADD DATA - GENERATE
        // =========================================================

        public void AddData(string data, int index = 0)
        {
            if (string.IsNullOrEmpty(data))
            {
                InvokeCallback(
                    PrinterStatus.AddData_Failure,
                    "Lỗi gửi dữ liệu: Dữ liệu rỗng.");

                return;
            }

            if (_unloaded)
                return;

            DataGlobal = data;
            Index = index;

            CancellationTokenSource current;

            lock (_generationSync)
            {
                current = new CancellationTokenSource();

                if (_cts != null)
                    _cts.Cancel();

                _cts = current;
            }

            _ = RunAsync(data, index + 1, MaxData, current);
        }

        private async Task RunAsync(
            string data,
            int index,
            int maxData,
            CancellationTokenSource source)
        {
            CancellationToken token = source.Token;

            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);

                List<string> lsdata = await Task.Run(() =>
                {
                    List<string> result = new List<string>();

                    for (int i = index; i <= maxData; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        result.Add(data + i.ToString());
                    }

                    return result;
                }, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                // Khi đã vào AddDataAsync, không hủy ngang quá trình gửi máy in.
                await AddDataAsync(lsdata, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Yêu cầu tạo dữ liệu mới thay thế yêu cầu cũ.
            }
            catch (Exception ex)
            {
                Logs($"Lỗi tạo dữ liệu: {ex.Message}");
                InvokeCallback(PrinterStatus.AddData_Failure, ex.Message);
            }
            finally
            {
                lock (_generationSync)
                {
                    if (ReferenceEquals(_cts, source))
                        _cts = null;
                }

                source.Dispose();
            }
        }

        // =========================================================
        // ADD DATA - PUBLIC LIST
        // =========================================================

        public void AddData(List<string> data)
        {
            _ = AddDataAsync(data, CancellationToken.None);
        }

        private async Task AddDataAsync(
            List<string> data,
            CancellationToken token)
        {
            if (data == null || data.Count == 0)
            {
                InvokeCallback(
                    PrinterStatus.AddData_Failure,
                    "Lỗi gửi dữ liệu: Dữ liệu rỗng.");

                return;
            }

            if (!IsConnected || client == null || !client.Connected)
            {
                InvokeCallback(
                    PrinterStatus.AddData_Failure,
                    "Lỗi gửi dữ liệu: Máy in chưa kết nối.");

                return;
            }

            bool acquired = false;
            bool queueStopped = false;
            bool operationStarted = false;

            try
            {
                await _operationSemaphore.WaitAsync(token).ConfigureAwait(false);
                acquired = true;

                if (_unloaded || !IsConnected || client == null || !client.Connected)
                    throw new InvalidOperationException("Máy in đã ngắt kết nối.");

                btnPrint.SafeInvoke(() => btnPrint.Enabled = false);

                // Dừng queue trước khi giao tiếp đồng bộ.
                await Task.Run(() =>
                {
                    StopTcpQueue();
                }).ConfigureAwait(false);

                queueStopped = true;
                operationStarted = true;

                await Task.Run(() =>
                {
                    AddDataInternal(data);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Chỉ hủy khi chưa bắt đầu chu trình gửi.
            }
            catch (Exception ex)
            {
                Logs($"Lỗi gửi dữ liệu: {ex.Message}");

                InvokeCallback(
                    PrinterStatus.AddData_Failure,
                    ex.Message);
            }
            finally
            {
                _lock = false;

                if (queueStopped && !_unloaded)
                {
                    try
                    {
                        StartTcpQueue();
                    }
                    catch (Exception ex)
                    {
                        Logs($"Lỗi khởi động TCP Queue: {ex.Message}");
                    }
                }

                if (acquired)
                {
                    btnPrint.SafeInvoke(() => btnPrint.Enabled = true);
                    _operationSemaphore.Release();
                }
            }
        }

        // =========================================================
        // ADD DATA - PRINTER PROCESS
        // Chạy trên Background Thread
        // =========================================================

        private void AddDataInternal(List<string> data)
        {
            Logs($"Đã nhận {data.Count} dữ liệu.");

            printerData.AddData(data, Index);

            Thread.Sleep(1000);

            Logs("Đang tắt máy in.");

            StopPrinter(true);

            Thread.Sleep(1000);

            string clearResponse = client.SendAndRead(
                $"{CommandString.Clear}", "\r\n", 1000);

            string resetResponse = client.SendAndRead(
                $"{CommandString.ResetPrintout}", "\r\n", 1000);

            Logs($"Clear response: {clearResponse ?? "Timeout"}");
            Logs($"Reset response: {resetResponse ?? "Timeout"}");

            Thread.Sleep(1000);

            Logs("Đang gửi dữ liệu đến máy in.");

            _lock = true;

            try
            {
                for (int i = 0; i < MaxCount && i < data.Count; i++)
                {
                    if (_unloaded || !IsConnected || client == null || !client.Connected)
                    {
                        Logs("Mất kết nối máy in khi gửi dữ liệu.");
                        return;
                    }

                    string str = printerData.Send1Data();

                    if (string.IsNullOrEmpty(str))
                        break;

                    string response = client.SendAndRead(
                        $"{CommandString.MFD}{str}",
                        "\r\n",
                        1000);

                    if (response != null && response.StartsWith("MFD|OK"))
                    {
                        printerData.SendSuccess();
                    }
                    else
                    {
                        printerData.SendFailure();

                        Logs($"Gửi dữ liệu thất bại tại vị trí {i + 1}: {response ?? "Timeout"}");
                    }

                    UpdateStatus();

                    Thread.Sleep(10);
                }
            }
            finally
            {
                _lock = false;
            }

            client.SendAndRead(
                $"{CommandString.MFDLEN}", "\r\n", 1000);

            Logs($"Đã gửi thành công {printerData.SentSuccessCount} dữ liệu đến máy in.");

            InvokeCallback(
                PrinterStatus.AddData_Success,
                $"Đã gửi thành công {printerData.SentSuccessCount} dữ liệu đến máy in.");

            Logs("Đang bật máy in.");

            StartPrinter(true);

            Thread.Sleep(5000);

            UpdateStatus();
        }

        // =========================================================
        // SEND DATA FROM QUEUE
        // =========================================================

        private void Send1Data()
        {
            string str = printerData.Send1Data();

            if (string.IsNullOrEmpty(str))
            {
                Logs("Hết dữ liệu.");
                return;
            }

            commandQueueManagerTCP.AddMid(
                $"{CommandString.MFD}{str}", 500);
        }

        // =========================================================
        // UPDATE UI
        // =========================================================

        private void UpdateStatus()
        {
            lblCountPrinter.SafeInvoke(() =>
            {
                lblCountPrinter.Text = $"{printerData.PinterDataCount}";
            });

            lblTotal.SafeInvoke(() =>
            {
                lblTotal.Text =
                    (printerData.DataList.Count - printerData.SentCount).ToString();
            });

            lblBuffer.SafeInvoke(() =>
            {
                lblBuffer.Text = printerData.ToString();
            });
        }

        private void UpdateCounter()
        {
            lblCounter.SafeInvoke(() =>
            {
                lblCounter.Text = printerData.PrintedCount.ToString();
            });
        }

        private string lastJob = "";

        private void UpdateJob()
        {
            lblJob.SafeInvoke(() =>
            {
                lblJob.Text = IsPrinting
                    ? $"Máy in đang in ({Job})"
                    : $"Máy in đã dừng ({Job})";
            });

            if (!string.IsNullOrEmpty(Job) && lastJob != Job)
            {
                lastJob = Job;
                InvokeCallback(PrinterStatus.Printer_Job, Job);
            }
        }

        // =========================================================
        // SET FILE
        // =========================================================

        public async Task<bool> SetFileAsync(PrinterFile file)
        {
            if (file == null)
                return false;

            bool acquired = false;
            bool queueStopped = false;
            bool result = false;

            try
            {
                await _operationSemaphore.WaitAsync().ConfigureAwait(false);
                acquired = true;

                Logs($"Đang đổi bản tin {file.Name}...");

                await Task.Run(() =>
                {
                    if (IsPrinting)
                    {
                        Logs("Đang tắt máy in.");

                        StopPrinter(true);
                        Thread.Sleep(1000);
                    }

                    StopTcpQueue();
                    queueStopped = true;

                    string response = client.SendAndRead(
                        $"{CommandString.SFile}{file.Id}",
                        "\r\n",
                        5000);

                    if (response != null && response.StartsWith("SFILE|OK"))
                    {
                        Logs($"Đã đổi bản tin {file.Name} thành công.");
                        result = true;
                    }
                    else
                    {
                        Logs($"Lỗi khi đổi bản tin: {response ?? "Timeout"}");
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logs($"Lỗi đổi bản tin: {ex.Message}");
            }
            finally
            {
                if (queueStopped && !_unloaded)
                    StartTcpQueue();

                if (acquired)
                    _operationSemaphore.Release();
            }

            if (result && !_unloaded)
            {
                await Task.Run(() =>
                {
                    Logs("Đang bật máy in.");
                    StartPrinter(true);
                    Thread.Sleep(1000);
                }).ConfigureAwait(false);
            }

            return result;
        }

        // Giữ tương thích với những nơi đang gọi SetFile đồng bộ.
        // Trong sự kiện UI nên sử dụng await SetFileAsync(file).
        public bool SetFile(PrinterFile file)
        {
            return SetFileAsync(file).GetAwaiter().GetResult();
        }

        // =========================================================
        // BUTTON EVENTS
        // =========================================================

        private async void btnPrint_Click(object sender, EventArgs e)
        {
            btnPrint.Enabled = false;

            try
            {
                if (client != null && client.Connected)
                {
                    if (btnPrint.BackColor == Color.Green)
                    {
                        commandQueueManagerTCP.AddPriority(
                            CommandString.Start, 50);
                    }
                    else
                    {
                        commandQueueManagerTCP.AddPriority(
                            CommandString.Stop, 50);
                    }

                    await Task.Delay(8000);
                }
            }
            catch (Exception ex)
            {
                Logs($"Lỗi điều khiển máy in: {ex.Message}");
            }
            finally
            {
                if (!_unloaded)
                    btnPrint.Enabled = true;
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Logs("----------------------------------------------");
        }

        private void button2_Click(object sender, EventArgs e)
        {
            printerData.ConsecutiveFailure = false;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            commandQueueManagerTCP.AddPriority(CommandString.Counter, 500);
            commandQueueManagerTCP.AddPriority(CommandString.ResetPrintout, 500);
        }

        private void button4_Click(object sender, EventArgs e)
        {
            G10Monitor monitor = new G10Monitor();
            monitor.IP = PrinterIp;
            monitor.Show();
        }

        private void button5_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(DataGlobal))
            {
                MessageBox.Show("Chưa có dữ liệu in.");
                return;
            }

            using (var form = new StartWith())
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    Logs($"Bật máy in từ dữ liệu số {form.Index}");
                    AddData(DataGlobal, form.Index);
                }
            }
        }

        private async void button6_Click(object sender, EventArgs e)
        {
            if (comboBox2.SelectedIndex < 0)
                return;

            var item = comboBox2.SelectedItem as PrinterFile;

            if (item == null)
                return;

            button6.Enabled = false;

            try
            {
                // Event được gọi trên Background Thread.
                await Task.Run(() =>
                {
                    OnFileChanged?.Invoke(item);
                });

                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                Logs($"Lỗi đổi bản tin: {ex.Message}");
            }
            finally
            {
                if (!_unloaded)
                    button6.Enabled = true;
            }
        }

        // =========================================================
        // LOAD FILE
        // =========================================================

        public void LoadFile(List<PrinterFile> files, string file)
        {
            if (files == null)
                return;

            comboBox2.SafeInvoke(() =>
            {
                comboBox2.DataSource = null;

                comboBox2.DisplayMember = nameof(PrinterFile.Name);
                comboBox2.ValueMember = nameof(PrinterFile.Id);

                comboBox2.DataSource = files;

                comboBox2.SelectedIndex =
                    files.FindIndex(x => x.Name == file);
            });
        }

        // =========================================================
        // UNLOAD
        // Trả về true nếu mọi thành phần đã xác nhận dừng.
        // Trả về false nếu còn thành phần chưa dừng (có thể gọi lại).
        // =========================================================

        public bool UNLOAD()
        {
            if (_unloadCompleted)
                return true;

            // Đặt ngay từ đầu để chặn callback/gửi lệnh mới (giống bản gốc).
            _unloaded = true;

            lock (_generationSync)
            {
                if (_cts != null)
                    _cts.Cancel();

                _cts = null;
            }

            List<string> failed = new List<string>();

            if (!SafeStop("TCP queue", () => StopTcpQueue()))
                failed.Add("TCP queue");

            if (!SafeStop("RS232 queue", () => commandQueueManagerRS232.Stop()))
                failed.Add("RS232 queue");

            // AsyncTcpClient.UNLOAD() / Rs232Client.UNLOAD() hiện trả về void
            // nên chỉ biết là không ném lỗi. Khi các lớp đó trả về bool
            // thì đổi thành: client == null || client.UNLOAD()
            if (!SafeStop("TCP client", () =>
            {
                if (client != null)
                    client.UNLOAD();

                return true;
            }))
                failed.Add("TCP client");

            if (!SafeStop("RS232 client", () =>
            {
                if (rs232Client != null)
                    rs232Client.UNLOAD();

                return true;
            }))
                failed.Add("RS232 client");

            IsConnected = false;
            IsPrinting = false;

            if (failed.Count > 0)
            {
                Logs("Chưa dừng hết: " + string.Join(", ", failed));
                return false;
            }

            _unloadCompleted = true;
            return true;
        }

        private bool SafeStop(string name, Func<bool> stop)
        {
            try
            {
                return stop();
            }
            catch (Exception ex)
            {
                Logs($"Lỗi dừng {name}: {ex.Message}");
                return false;
            }
        }
    }
}