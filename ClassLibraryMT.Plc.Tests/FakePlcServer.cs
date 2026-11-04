using System.Net;
using System.Net.Sockets;

namespace ClassLibraryMT.Plc.Tests;

/// <summary>
/// Fake PLC FINS/UDP dung cho test: nhan frame request, ghi lai,
/// va tra ve response theo kich ban (thanh cong hoac end code loi).
/// </summary>
internal sealed class FakePlcServer : IDisposable
{
    private readonly UdpClient _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public List<byte[]> Requests { get; } = [];

    /// <summary>End code tra ve. 0 = thanh cong.</summary>
    public ushort EndCode { get; set; }

    /// <summary>Neu true, response se co SID khac voi request (test sai SID).</summary>
    public bool UseWrongSid { get; set; }

    /// <summary>So word tra ve trong response doc.</summary>
    public int ResponseWordCount { get; set; }

    /// <summary>Dung khi can response co byte thong bao (MRC/SRC = 0x00 0x00).</summary>
    public bool UseNotificationBytes { get; set; }

    /// <summary>Neu true, server khong phan hoi (test timeout).</summary>
    public bool Silent { get; set; }

    /// <summary>Gia tri word thu i tra ve (mac dinh 0).</summary>
    public Func<int, ushort> WordProvider { get; set; } = _ => 0;

    public FakePlcServer()
    {
        _listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_listener.Client.LocalEndPoint!).Port;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var received = await _listener.ReceiveAsync(cts.Token);

                lock (Requests) Requests.Add(received.Buffer!);

                if (Silent) continue;

                var response = BuildResponse(received.Buffer);
                if (response is not null)
                    await _listener.SendAsync(response, received.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private byte[]? BuildResponse(byte[] request)
    {
        // Than lenh doc: [10]=MRC [11]=SRC [12]=area [13..14]=dia chi [15]=bit [16..17]=so word
        var isRead = request.Length >= 18 && request[10] == 0x01 && request[11] == 0x01;
        var wordCount = isRead
            ? (request[16] << 8) | request[17]
            : 0;

        if (EndCode != 0 || !isRead)
        {
            // Response khong co du lieu (ghi, hoac loi).
            var resp = new byte[14];
            resp[0] = 0xC0;
            resp[2] = request[2];
            resp[9] = UseWrongSid ? (byte)(request[9] ^ 0xFF) : request[9];
            resp[10] = request.Length > 10 ? request[10] : (byte)0x01;
            resp[11] = request.Length > 11 ? request[11] : (byte)0x01;
            resp[12] = (byte)(EndCode >> 8);
            resp[13] = (byte)(EndCode & 0xFF);
            return resp;
        }

        var count = ResponseWordCount > 0 ? ResponseWordCount : wordCount;

        // End code luon nam o offset [12..13], du lieu bat dau tu [14..]
        // (giong cach HslCommunication doc response FINS).
        var size = 14 + count * 2;

        var response = new byte[size];
        response[0] = 0xC0;
        response[2] = request[2];
        response[9] = UseWrongSid ? (byte)(request[9] ^ 0xFF) : request[9];

        response[10] = 0x01;       // MRC
        response[11] = 0x01;       // SRC
        response[12] = (byte)(EndCode >> 8);
        response[13] = (byte)(EndCode & 0xFF);

        // Byte thong bao (RTS): thay the MRC/SRC bang 0x00 de test phai bao loi.
        if (UseNotificationBytes)
        {
            response[10] = 0x00;
            response[11] = 0x00;
        }

        var p = 14;
        for (var i = 0; i < count; i++)
        {
            var w = WordProvider(i);
            response[p++] = (byte)(w >> 8);
            response[p++] = (byte)(w & 0xFF);
        }

        return response;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
