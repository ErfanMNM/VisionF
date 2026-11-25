using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TanTienQR.Services.PrinterRemote;

/// <summary>Lưu mật khẩu trong Windows Credential Manager của người dùng hiện tại.</summary>
public sealed partial class WindowsPrinterCredentialStore : IPrinterCredentialStore
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool Exists(string target)
    {
        if (!IsSupported || !CredRead(target, 1, 0, out var pointer)) return false;
        CredFree(pointer);
        return true;
    }

    public string? ReadPassword(string target, string username)
    {
        if (!IsSupported || !CredRead(target, 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (!string.Equals(Marshal.PtrToStringUni(credential.UserName), username, StringComparison.OrdinalIgnoreCase)) return null;
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    public void Save(string target, string username, string password)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Kho thông tin xác thực yêu cầu Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var bytes = Encoding.Unicode.GetBytes(password);
        if (bytes.Length > 2560) throw new ArgumentException("Mật khẩu vượt quá dung lượng của Windows Credential Manager.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var name = Marshal.StringToHGlobalUni(username);
        var targetPointer = Marshal.StringToHGlobalUni(target);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = targetPointer, BlobSize = (uint)bytes.Length, Blob = blob, Persist = 2, UserName = name };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không lưu được thông tin đăng nhập máy in.");
        }
        finally
        {
            Array.Clear(bytes);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(targetPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags, Type;
        public nint TargetName, Comment;
        public long LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes, TargetAlias, UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredRead(string target, uint type, uint flags, out nint credential);
    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredWrite(ref Credential credential, uint flags);
    [LibraryImport("advapi32.dll")] private static partial void CredFree(nint credential);
}
