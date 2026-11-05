using System.Text;
using ClassLibraryMT.Communications.Checkee;
using ClassLibraryMT.DeviceStatus;
using ClassLibraryMT.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace QRDemoCodeBackend;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Any(a => a.Equals("--show-hwid", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("HWID (gui ma nay cho nha cung cap de lay file license.lic):");
            Console.WriteLine("  " + HardwareId.GetGrouped());
            return 0;
        }

        // ---- Check license truoc khi chay service ----
        if (!TryCheckLicense(args, out var exitCode))
            return exitCode;

#if DEVELOPMENT
        // Chay Debug: dung appsettings.Development.json (co ClientId/ApiKey mau).
        // Phai dat TRUOC khi tao Host builder vi no doc environment luc khoi tao.
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
#endif

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddAppServices(builder.Configuration);

        using var host = builder.Build();

        // Kiem tra cau hinh TRUOC khi chay host, de loi ra ro rang
        // thay vi lan host lai in stack trace.
        if (!TryValidateOptions(host.Services, out exitCode))
            return exitCode;

        await host.RunAsync();
        return 0;
    }

    // Kiem tra cac option bat buoc. Tra false neu sai (da in thong bao lenh).
    private static bool TryValidateOptions(IServiceProvider services, out int exitCode)
    {
        exitCode = 0;

        try
        {
            _ = services.GetRequiredService<IOptions<CameraOptions>>().Value;
            _ = services.GetRequiredService<IOptions<CheckeeOptions>>().Value;
            _ = services.GetRequiredService<IOptions<PlcOptions>>().Value;
            return true;
        }
        catch (OptionsValidationException ex)
        {
            Console.Error.WriteLine("CAU HINH SAI:");
            foreach (var failure in ex.Failures)
                Console.Error.WriteLine("  - " + failure);
            Console.Error.WriteLine("Sua file appsettings.json canh ben file .exe roi chay lai.");
            exitCode = 4;
            return false;
        }
    }

    // Kiem tra license. Tra false neu khong hop le (da in thong bao lenh).
    private static bool TryCheckLicense(string[] args, out int exitCode)
    {
        exitCode = 0;

        var licPath = LicenseArg(args) ?? LicenseValidator.DefaultLicensePath;
        var lic = LicenseValidator.Validate(licPath);
        if (lic.Ok)
        {
            Console.WriteLine($"License OK (het han {lic.Payload!.ExpiresUtc:yyyy-MM-dd}, con {lic.DaysLeft} ngay).");
            if (lic.DaysLeft <= 30)
                Console.WriteLine("CANH BAO: license sap het han, lien he nha cung cap de gia han.");

            // AppState: license hop le -> app co the Ready (neu camera + PLC da ket noi).
            AppStateManager.SetLicenseValid(true);
            return true;
        }

        Console.WriteLine("GIAY PHEP KHONG HOP LE: " + lic.Message);
        Console.WriteLine("Ma may (HWID) cua may nay:");
        Console.WriteLine("  " + HardwareId.GetGrouped());
        Console.WriteLine("Chep file license.lic vao thu muc chua file .exe roi chay lai,");
        Console.WriteLine("hoac chay: DemoQR.exe --license <duong-dan-license.lic>");
        try
        {
            Console.Write("Nhan phim bat ky de thoat...");
            Console.ReadKey(true);
            Console.WriteLine();
        }
        catch { }
        exitCode = 3;
        return false;
    }

    private static string? LicenseArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i].Equals("--license", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        return null;
    }
}
