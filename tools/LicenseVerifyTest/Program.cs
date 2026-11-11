using ClassLibraryMT.Licensing;

// Kiem tra chu ky license duoc cap tu Cloudflare License Portal.
// Dung:  dotnet run --project tools/LicenseVerifyTest -- <duong-dan-license.lic> [product]
//
// Tool nay dung CHINH code cua app (LicenseValidator) nen ket qua
// xac nhan app se chay duoc tren may thuc te.

var path = args.Length > 0 ? args[0] : "license.lic";
var product = args.Length > 1 ? args[1] : "TanTienQR";

if (!File.Exists(path))
{
    Console.Error.WriteLine($"Khong tim thay file: {path}");
    return 2;
}

Console.WriteLine($"File    : {Path.GetFullPath(path)}");
Console.WriteLine($"Product : {product}");
Console.WriteLine($"HWID may: {HardwareId.GetGrouped()}");
Console.WriteLine();

var check = LicenseValidator.Validate(path, product);

Console.WriteLine($"Ket qua  : {(check.Ok ? "HOP LE" : "KHONG HOP LE")}");
Console.WriteLine($"Thong bao: {check.Message}");

if (check.Payload is not null)
{
    Console.WriteLine();
    Console.WriteLine("--- Payload ---");
    Console.WriteLine($"  Customer : {check.Payload.Customer}");
    Console.WriteLine($"  HWID     : {HardwareId.Normalize(check.Payload.Hwid)}");
    Console.WriteLine($"  Issued   : {check.Payload.IssuedUtc:yyyy-MM-dd HH:mm:ss} UTC");
    Console.WriteLine($"  Expires  : {check.Payload.ExpiresUtc:yyyy-MM-dd HH:mm:ss} UTC");
    Console.WriteLine($"  Product  : {check.Payload.Product}");
    Console.WriteLine($"  Key ID   : {check.Payload.KeyId}");
    Console.WriteLine($"  Con lai  : {check.DaysLeft} ngay");
}

return check.Ok ? 0 : 1;
