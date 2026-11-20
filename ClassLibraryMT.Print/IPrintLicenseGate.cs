namespace ClassLibraryMT.Print;

public interface IPrintLicenseGate
{
    bool CanUse { get; }
    string Message { get; }
}
