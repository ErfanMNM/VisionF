using ClassLibraryMT.Licensing;
using ClassLibraryMT.Print;

namespace TanTienQR.Services;

internal sealed class PrinterLicenseGate : IPrintLicenseGate
{
    private readonly LicenseManager _license;

    public PrinterLicenseGate(LicenseManager license) => _license = license;

    public bool CanUse => LicenseEntitlements.HasFeature(_license.Current, LicenseFeatures.Printer)
        && _license.Current.Payload!.ExpiresUtc > DateTime.UtcNow;

    public string Message => CanUse
        ? "OK"
        : "License chưa mở quyền Máy in + gói bán kèm hoặc đã hết hạn. Hãy nhập license hợp lệ có entitlement printer.";
}
