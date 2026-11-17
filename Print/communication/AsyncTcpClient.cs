using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SqlTypes;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MHG_BoxPrint.Print.communication
{  
    public enum enumClient
    {
        CONNECTED,
        DISCONNECTED,
        RECEIVED
    }
    public class AsyncTcpClient
    {
        private readonly AutoResetEvent _receiveEvent = new AutoResetEvent(false);
        private readonly object _receiveLock = new object();
        private readonly StringBuilder _receiveBuffer = new StringBuilder();

        DataTimeoutMonitor timeoutMonitor = new DataTimeoutMonitor(TimeSpan.FromSeconds(30));

        BackgroundWorker worker = new BackgroundWorker();

        bool STATUP = true;
        public void LOAD()
        {
            if (STATUP)
            {
                STATUP = false;
                worker.WorkerSupportsCancellation = true;
                worker.DoWork += Worker_DoWork;
                worker.RunWorkerAsync();

                timeoutMonitor.Timeout += () =>
                {
                    SafeInvoke(enumClient.DISCONNECTED, "Máy in không phản hồi");
                    Disconnect();
                    Connected = false;
                    if (!worker.IsBusy)
                    {
                        worker.RunWorkerAsync();
                    }
                };

            }
        }
        public void UNLOAD()
        {
            if (worker!= null && worker.IsBusy) worker.CancelAsync();
            if (client != null)
            {
                client.Close();
            }
        }

        string last = "";
        public void SafeInvoke(enumClient eAE, string _strData)
        {
            if (ClientCallBack == null) return;
            string invokeData = eAE.ToString() +_strData;
            //if(invokeData!=last)
            {
                ClientCallBack.Invoke(eAE, _strData);
                last = invokeData;
            }
            
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            while (!worker.CancellationPending)
            {
                if (!re || !Connected)
                {
                    //Disconnect();
                    Connect();
                }
                Thread.Sleep(10000);
            }
        }

        public delegate void EventForClient(enumClient eAE, string _strData);
        public event EventForClient ClientCallBack;
        public string IP { get; set; } = "192.168.250.33";
        public int Port { get; set; } = 2030;

        byte[] m_DataBuffer = new byte[512];
        IAsyncResult m_asynResult;
        private AsyncCallback pfnCallBack;
        private Socket client;
        public bool Connected { get; private set; }
        private bool statup = false;
        public void Connect()
        {
            LOAD();
            try
            {
                Connected = false;
                Ping pinger = new Ping();
                PingReply reply = pinger.Send(IP);
                if (reply.Status == IPStatus.Success)
                {

                    client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    IPAddress Ip = IPAddress.Parse(IP);
                    int iPortNo = System.Convert.ToInt32(Port);
                    IPEndPoint ipEnd = new IPEndPoint(Ip, iPortNo);
                    client.Connect(ipEnd);

                    Connected = client.Connected;

                    if (client.Connected)
                    {
                        re = true;
                        tr = 5;
                        WaitForData();
                        SafeInvoke(enumClient.CONNECTED, "");
                        timeoutMonitor.Start();
                    }
                }
                else
                {
                    SafeInvoke(enumClient.DISCONNECTED, "Kiểm tra đường dây"); statup = true;
                    Connected = false;
                }
            }
            catch //(Exception)
            {
                //throw;
            }
            if (!Connected && !worker.IsBusy)
            {
                worker.RunWorkerAsync();
            }
        }
        private void WaitForData()
        {
            try
            {
                if (pfnCallBack == null)
                {
                    pfnCallBack = new AsyncCallback(OnDataReceived);
                }
                CSocketPacket theSocPkt = new CSocketPacket();
                theSocPkt.thisSocket = client;
                m_asynResult = client.BeginReceive(theSocPkt.dataBuffer, 0, theSocPkt.dataBuffer.Length, SocketFlags.None, pfnCallBack, theSocPkt);
            }
            catch
            {
            }
        }
        private class CSocketPacket
        {
            public System.Net.Sockets.Socket thisSocket;
            public byte[] dataBuffer = new byte[512];
        }
        public void Disconnect()
        {
            try
            {
                if (Connected)
                {
                    if (client != null)
                    {
                        client.Disconnect(true);
                        Connected = false;
                        SafeInvoke(enumClient.DISCONNECTED, "Kiểm tra đường dây");
                        if (!Connected && !worker.IsBusy)
                        {
                            worker.RunWorkerAsync();
                        }
                    }
                }
            }
            catch
            {
                Connected = false;
            }
        }
        public void Send(string data)
        {
            if (client != null && data != null)
            {
                try
                {
                    Object objData = data;
                    //byte[] byData = System.Text.Encoding.ASCII.GetBytes('\x002'+objData.ToString() + '\x03');
                    byte[] byData = System.Text.Encoding.ASCII.GetBytes(objData.ToString());
                    client.Send(byData);
                }
                catch (SocketException)
                {
                    client = null;

                    SafeInvoke(enumClient.DISCONNECTED, "Kiểm tra đường dây");
                    Connected = false;
                }
            }
        }
   
        int tr = 0;
        bool re = false;
        private void OnDataReceived(IAsyncResult asyn)
        {
            try
            {
                string szData = "";
                CSocketPacket theSockId = (CSocketPacket)asyn.AsyncState;
                int iRx = theSockId.thisSocket.EndReceive(asyn);

                if (iRx > 0) // Chỉ xử lý khi có dữ liệu nhận được
                {
                    timeoutMonitor.Feed();
                    Encoding encoding = Encoding.GetEncoding(28591);
                    szData = encoding.GetString(theSockId.dataBuffer, 0, iRx);

                    lock (_receiveLock)
                    {
                        _receiveBuffer.Append(szData);
                    }

                    _receiveEvent.Set();

                    re = true;
                    tr = 5;
                    Connected = true;

                    SafeInvoke(enumClient.RECEIVED, szData);

                    WaitForData();     
                }


            }
            catch
            {
                re = false;
            }
        }

        public string SendAndRead(string cmd,
                          string endMark = "\r\n",
                          int timeout = 5000)
        {
            if (!Connected || client == null)
                throw new Exception("Socket not connected.");

            lock (_receiveLock)
            {
                _receiveBuffer.Clear();
            }

            _receiveEvent.Reset();

            byte[] data = Encoding.ASCII.GetBytes(cmd);
            try
            {
                if (client != null && client.Connected)
                {
                    client.Send(data);
                }
            }
            catch (ObjectDisposedException)
            {
                // socket đã bị dispose
            }
            catch (SocketException ex)
            {
                // lỗi mạng
            }

            DateTime start = DateTime.Now;

            while ((DateTime.Now - start).TotalMilliseconds < timeout)
            {
                int remain = timeout - (int)(DateTime.Now - start).TotalMilliseconds;
                if (remain <= 0)
                    break;

                _receiveEvent.WaitOne(remain);

                lock (_receiveLock)
                {
                    string result = _receiveBuffer.ToString();

                    if (result.Contains(endMark))
                    {
                        return result;
                    }
                }
            }
            return string.Empty;

            //throw new TimeoutException("Receive timeout.");
        }
    }
}