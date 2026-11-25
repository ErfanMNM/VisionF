using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ClassLibraryMT.Licensing;

// HWID v2 chi dua tren MachineGuid, khong phu thuoc trang thai adapter hay volume.
public static class HardwareId
{
    private const string AlgorithmPrefix = "VisionF-HWID-v2|";

    public static string Get() => Compute(ReadMachineIdentifier());

    // Dang de nhin, de doc qua dien thoai: XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX
    public static string GetGrouped()
    {
        var h = Get();
        return string.Join("-", Enumerable.Range(0, 8).Select(i => h.Substring(i * 4, 4)));
    }

    // Chuan hoa ma nguoi dung nhap: bo gach/cach, doi chu hoa, chi giu 0-9 A-F.
    public static string Normalize(string? hwid)
    {
        if (string.IsNullOrWhiteSpace(hwid)) return "";
        var sb = new StringBuilder(32);
        foreach (var c in hwid.ToUpperInvariant())
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))
                sb.Append(c);
        return sb.ToString();
    }

    internal static string Compute(string machineIdentifier)
    {
        var normalized = NormalizeMachineIdentifier(machineIdentifier);
        var input = Encoding.UTF8.GetBytes(AlgorithmPrefix + normalized);
        return Convert.ToHexString(SHA256.HashData(input))[..32];
    }

    private static string ReadMachineIdentifier()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var value = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "") as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            else if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
            {
                var value = File.ReadAllText("/etc/machine-id");
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Khong doc duoc dinh danh he dieu hanh de tao HWID.", ex);
        }

        throw new InvalidOperationException("Khong tim thay MachineGuid hoac machine-id de tao HWID.");
    }

    private static string NormalizeMachineIdentifier(string machineIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineIdentifier);

        var normalized = machineIdentifier.Trim()
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("{", "", StringComparison.Ordinal)
            .Replace("}", "", StringComparison.Ordinal)
            .ToUpperInvariant();

        if (normalized.Length != 32 || normalized.Any(c =>
                !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))))
            throw new ArgumentException("Machine identifier phai la UUID 32 ky tu hex.", nameof(machineIdentifier));

        return normalized;
    }
}
