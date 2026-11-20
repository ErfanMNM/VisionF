using ClassLibraryMT.Licensing;
using ClassLibraryMT.Print;

namespace TanTienQR.Services;

internal sealed class PrinterLicenseGate : IPrintLicenseGate
{
    private readonly LicenseManager _license;

    public PrinterLicenseGate(LicenseManager license) => _license = license;

    public bool CanUse => LicenseEntitlements.HasFeature(_license.Current, LicenseFeatures.Printer);

    public string Message => CanUse
        ? "OK"
        : "License hiện tại chưa mở quyền Máy in + gói bán kèm. Hãy nhập license có entitlement printer.";
}
