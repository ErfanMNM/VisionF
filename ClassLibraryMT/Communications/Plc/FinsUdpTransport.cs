using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ClassLibraryMT.Communications.Plc;

/// <summary>Ket qua cua mot lan goi lenh FINS.</summary>
internal sealed class FinsTransportResult
{
    public bool IsSuccess { get; init; }
    public string ErrorCode { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ushort[] Words { get; init; } = [];
    public TimeSpan ProcessTime { get; init; }
}

/// <summary>
/// Transport FINS/UDP dong bo: gui frame va cho phan hoi trong timeout, co retry.
///
/// Khong dung thread nen, khong heartbeat, khong event.
/// </summary>
internal sealed class FinsUdpTransport : IDisposable
{
    private const int ReceiveBufferSize = 2048;

    private readonly UdpClient _client;
    private readonly IPEndPoint _remote;
    private readonly int _timeoutMs;
    private readonly int _retries;
    private readonly byte[] _buffer = new byte[ReceiveBufferSize];
    private byte _sid = Fins.SidStart;
    private bool _disposed;

    public FinsUdpTransport(string ip, int port, int timeoutMs, int retries)
    {
        _remote = new IPEndPoint(IPAddress.Parse(ip), port);
        _timeoutMs = Math.Max(1, timeoutMs);
        _retries = Math.Max(0, retries);
        _client = new UdpClient();
        _client.Client.ReceiveTimeout = _timeoutMs;
        _client.Client.SendTimeout = _timeoutMs;

        // Connect de chi nhan phan hoi tu PLC va bo qua frame cua may khac.
        _client.Connect(_remote);
    }

    /// <summary>
    /// Gui lenh doc word va nhan du lieu.
    /// </summary>
    public FinsTransportResult Read(
        byte remoteNode, byte localNode, int wordAddress, int wordCount)
    {
        var sw = Stopwatch.StartNew();

        var sid = NextSid();
        var frame = FinsCodec.BuildReadCommand(sid, remoteNode, localNode, wordAddress, wordCount);

        var lastCode = "TIMEOUT";
        var lastMessage = "Khong nhan duoc phan hoi tu PLC.";
        var totalAttempts = _retries + 1;

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            var response = Exchange(frame);
            if (response is null)
            {
                lastMessage = attempt < totalAttempts - 1
                    ? $"Khong nhan duoc phan hoi (lan {attempt + 1}/{totalAttempts}), thu lai..."
                    : "Khong nhan duoc phan hoi tu PLC.";
                continue;
            }

            if (!FinsCodec.IsValidResponseHeader(response, out var responseSid))
            {
                lastCode = "BADRESP";
                lastMessage = "Phan hoi khong phai frame FINS hop le.";
                continue;
            }

            if (responseSid != sid)
            {
                // Phan hoi cua lenh truoc -> bo qua, cho tiep.
                lastCode = "SID";
                lastMessage = $"SID khong khop (mong doi {sid}, nhan {responseSid}).";
                continue;
            }

            if (!FinsCodec.TryReadEndCode(response, out var endCode))
            {
                lastCode = "BADRESP";
                lastMessage = "Phan hoi khong doc duoc end code.";
                continue;
            }

            // Phai echo dung MRC/SRC cua lenh da gui.
            if (!FinsCodec.HasEchoedCommand(response, Fins.MrcMemoryArea, Fins.SrcRead))
            {
                lastCode = "BADRESP";
                lastMessage = "Phan hoi khong echo dung MRC/SRC.";
                continue;
            }

            if (endCode != 0)
            {
                return new FinsTransportResult
                {
                    IsSuccess = false,
                    ErrorCode = endCode.ToString("X4"),
                    Message = Fins.DescribeEndCode(endCode),
                    ProcessTime = sw.Elapsed,
                };
            }

            if (!FinsCodec.TryReadData(response, wordCount, out var words))
            {
                return new FinsTransportResult
                {
                    IsSuccess = false,
                    ErrorCode = "BADRESP",
                    Message = "Phan hoi thieu du lieu.",
                    ProcessTime = sw.Elapsed,
                };
            }

            return new FinsTransportResult
            {
                IsSuccess = true,
                ErrorCode = "0000",
                Message = "OK",
                Words = words,
                ProcessTime = sw.Elapsed,
            };
        }

        return new FinsTransportResult
        {
            IsSuccess = false,
            ErrorCode = lastCode,
            Message = lastMessage,
            ProcessTime = sw.Elapsed,
        };
    }

    /// <summary>
    /// Gui lenh ghi word.
    /// </summary>
    public FinsTransportResult Write(
        byte remoteNode, byte localNode, int wordAddress, ReadOnlySpan<ushort> words)
    {
        var sw = Stopwatch.StartNew();

        var sid = NextSid();
        var frame = FinsCodec.BuildWriteCommand(sid, remoteNode, localNode, wordAddress, words);

        var lastCode = "TIMEOUT";
        var lastMessage = "Khong nhan duoc phan hoi tu PLC.";
        var totalAttempts = _retries + 1;

        for (var attempt = 0; attempt < totalAttempts; attempt++)
        {
            var response = Exchange(frame);
            if (response is null)
            {
                lastMessage = attempt < totalAttempts - 1
                    ? $"Khong nhan duoc phan hoi (lan {attempt + 1}/{totalAttempts}), thu lai..."
                    : "Khong nhan duoc phan hoi tu PLC.";
                continue;
            }

            if (!FinsCodec.IsValidResponseHeader(response, out var responseSid))
            {
                lastCode = "BADRESP";
                lastMessage = "Phan hoi khong phai frame FINS hop le.";
                continue;
            }

            if (responseSid != sid)
            {
                lastCode = "SID";
                lastMessage = $"SID khong khop (mong doi {sid}, nhan {responseSid}).";
                continue;
            }

            if (!FinsCodec.TryReadEndCode(response, out var endCode))
            {
                lastCode = "BADRESP";
                lastMessage = "Phan hoi khong doc duoc end code.";
                continue;
            }

            // Phai echo dung MRC/SRC cua lenh da gui.
            if (!FinsCodec.HasEchoedCommand(response, Fins.MrcMemoryArea, Fins.SrcWrite))
            {
                lastCode = "BADRESP";
                lastMessage = "Phan hoi khong echo dung MRC/SRC.";
                continue;
            }

            if (endCode != 0)
            {
                return new FinsTransportResult
                {
                    IsSuccess = false,
                    ErrorCode = endCode.ToString("X4"),
                    Message = Fins.DescribeEndCode(endCode),
                    ProcessTime = sw.Elapsed,
                };
            }

            return new FinsTransportResult
            {
                IsSuccess = true,
                ErrorCode = "0000",
                Message = "OK",
                ProcessTime = sw.Elapsed,
            };
        }

        return new FinsTransportResult
        {
            IsSuccess = false,
            ErrorCode = lastCode,
            Message = lastMessage,
            ProcessTime = sw.Elapsed,
        };
    }

    /// <summary>
    /// Gui frame va nhan phan hoi. Tra null neu khong co phan hoi (timeout).
    ///
    /// Dung receive dong bo tren socket da connect de:
    /// - chi nhan frame tu dung dia chi PLC,
    /// - khong tao task receive "treo" bi lan sang request sau.
    /// </summary>
    private byte[]? Exchange(byte[] frame)
    {
        try
        {
            _client.Send(frame, frame.Length);

            var received = _client.Client.Receive(_buffer);
            if (received <= 0) return null;

            var response = new byte[received];
            Array.Copy(_buffer, response, received);
            return response;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>SID tang dan, wrap ve 0 khi tran 255.</summary>
    private byte NextSid()
    {
        var sid = _sid;
        _sid = (byte)((_sid + 1) & 0xFF);
        return sid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
