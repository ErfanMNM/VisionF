using ClassLibraryMT.Communications.Plc;
using ClassLibraryMT.Plc;

namespace TanTienQR.Services;

/// <summary>Tab tham số/recipe dùng cùng PLC runtime đã hot-apply với heartbeat.</summary>
internal sealed class RuntimePlcMemoryClientFactory(OmronPlc plc) : IPlcMemoryClientFactory
{
    public IPlcMemoryClient Create() => plc.TryAcquireTransactionLease() is { } lease
        ? new BorrowedClient(plc, lease) : BusyClient.Instance;

    // PlcSettingsService dispose client sau mỗi thao tác. Wrapper mượn không
    // sở hữu socket singleton. Lease giữ endpoint ổn định qua mọi lệnh của
    // cùng recipe; Dispose chỉ trả lease, không đóng PLC của heartbeat.
    private sealed class BorrowedClient(OmronPlc client, IDisposable lease) : IPlcMemoryClient
    {
        private IDisposable? _lease = lease;
        private bool Disposed => Volatile.Read(ref _lease) is null;
        private static PlcResult<T> Closed<T>() => PlcResult<T>.Fail("DISPOSED", "Phiên thao tác PLC đã đóng.", default);
        private static PlcResult Closed() => PlcResult.Fail("DISPOSED", "Phiên thao tác PLC đã đóng.", default);
        public PlcResult<short> ReadInt16(string address) => Disposed ? Closed<short>() : client.ReadInt16(address);
        public PlcResult<int> ReadInt32(string address) => Disposed ? Closed<int>() : client.ReadInt32(address);
        public PlcResult<float> ReadFloat(string address) => Disposed ? Closed<float>() : client.ReadFloat(address);
        public PlcResult<bool> ReadBool(string address) => Disposed ? Closed<bool>() : client.ReadBool(address);
        public PlcResult WriteInt16(string address, short value) => Disposed ? Closed() : client.WriteInt16(address, value);
        public PlcResult WriteInt32(string address, int value) => Disposed ? Closed() : client.WriteInt32(address, value);
        public PlcResult WriteFloat(string address, float value) => Disposed ? Closed() : client.WriteFloat(address, value);
        public PlcResult WriteBool(string address, bool value) => Disposed ? Closed() : client.WriteBool(address, value);
        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    private sealed class BusyClient : IPlcMemoryClient
    {
        public static BusyClient Instance { get; } = new();
        private const string Message = "PLC đang áp dụng cấu hình hoặc thực hiện thao tác khác. Vui lòng thử lại.";
        private static PlcResult<T> Busy<T>() => PlcResult<T>.Fail("BUSY", Message, default);
        private static PlcResult Busy() => PlcResult.Fail("BUSY", Message, default);
        public PlcResult<short> ReadInt16(string address) => Busy<short>();
        public PlcResult<int> ReadInt32(string address) => Busy<int>();
        public PlcResult<float> ReadFloat(string address) => Busy<float>();
        public PlcResult<bool> ReadBool(string address) => Busy<bool>();
        public PlcResult WriteInt16(string address, short value) => Busy();
        public PlcResult WriteInt32(string address, int value) => Busy();
        public PlcResult WriteFloat(string address, float value) => Busy();
        public PlcResult WriteBool(string address, bool value) => Busy();
        public void Dispose() { }
    }
}
