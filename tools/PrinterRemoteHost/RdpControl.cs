using System.Runtime.InteropServices;

namespace TanTien.PrinterRemoteHost;

/// <summary>AxHost chỉ tồn tại trong helper JIT, không được đưa vào ứng dụng Native AOT.</summary>
internal sealed class RdpControl : AxHost
{
    private static readonly Guid EventsId = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    private readonly List<(int Id, Delegate Handler)> _events = [];
    public RdpControl() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD") { }
    public dynamic Client => GetOcx() ?? throw new InvalidOperationException("ActiveX chưa được tạo.");
    public void Observe(int id, Delegate handler)
    {
        ComEventsHelper.Combine(Client, EventsId, id, handler);
        _events.Add((id, handler));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (id, handler) in _events)
            {
                try { ComEventsHelper.Remove(Client, EventsId, id, handler); }
                catch (Exception) { }
            }
            _events.Clear();
        }
        base.Dispose(disposing);
    }
}
