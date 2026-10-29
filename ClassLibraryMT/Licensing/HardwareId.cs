using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ClassLibraryMT.Licensing;

// Ma dinh danh may: ket hop MachineGuid + serial o C + MAC + CPU,
// bam SHA256, lay 32 ky tu hex dau (128-bit). On dinh qua cac lan cai lai app.
public static partial class HardwareId
{
    public static string Get()
    {
        var raw = Collect();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Normalize(Convert.ToHexString(hash))[..32];
    }

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

    private static string Collect()
    {
        var machineGuid = "";
        try
        {
            if (OperatingSystem.IsWindows())
                machineGuid = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", "") as string ?? "";
        }
        catch { /* bo qua, dung thanh phan khac */ }

        var volume = "";
        try
        {
            if (OperatingSystem.IsWindows() &&
                GetVolumeInformationW(@"C:\", null, 0, out var serial, out _, out _, null, 0) != 0)
                volume = serial.ToString("X8", CultureInfo.InvariantCulture);
        }
        catch { /* bo qua */ }

        var mac = "";
        try
        {
            mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.Speed)
                .Select(n => n.GetPhysicalAddress()?.ToString())
                .FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? "";
        }
        catch { /* bo qua */ }

        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        return $"{machineGuid}|{volume}|{mac}|{cpu}";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);
}
