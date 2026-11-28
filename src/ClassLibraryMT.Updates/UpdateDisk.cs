using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClassLibraryMT.Updates;

internal static partial class UpdateDisk
{
    internal static bool Exists(string path, bool directory)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is not null && !Exists(parent, directory: true)) return false;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Thư mục cập nhật không được dùng liên kết.");
        if (((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Tệp cập nhật gặp đường dẫn không đúng loại.");
        return true;
    }

    internal static void EnsureDirectory(string path)
    {
        if (Exists(path, directory: true)) return;
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) EnsureDirectory(parent);
        Directory.CreateDirectory(path);
        Exists(path, directory: true);
        if (parent is not null) SyncDirectory(parent);
    }

    internal static byte[] ReadSmall(string path, int limit)
    {
        Exists(path, directory: false);
        using var file = File.OpenRead(path);
        if (file.Length > limit) throw new IOException("Tệp trạng thái cập nhật quá lớn.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new IOException("Tệp cập nhật đã thay đổi khi đang đọc.");
        return bytes;
    }

    internal static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        var folder = Path.GetDirectoryName(path)!;
        Exists(folder, directory: true);
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            Replace(temp, path);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    internal static void Replace(string source, string destination, bool overwrite = true)
    {
        if (OperatingSystem.IsWindows())
        {
            const int writeThrough = 8;
            const int replaceExisting = 1;
            if (!MoveFileEx(source, destination, writeThrough | (overwrite ? replaceExisting : 0)))
                throw new IOException("Không thể di chuyển tệp cập nhật.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        else
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination, overwrite);
            var srcFolder = Path.GetDirectoryName(source)!;
            var destFolder = Path.GetDirectoryName(destination)!;
            SyncDirectory(destFolder);
            if (srcFolder != destFolder) SyncDirectory(srcFolder);
        }
    }

    internal static void DeleteFile(string path)
    {
        if (!Exists(path, directory: false)) return;
        File.Delete(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    internal static void DeleteDirectory(string path)
    {
        if (!Exists(path, directory: true)) return;
        // Từ chối cây có reparse point trước khi xóa, kể cả junction trên Windows.
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Không xóa staging có liên kết.");
            if ((attributes & FileAttributes.Directory) != 0) DeleteDirectory(entry);
            else DeleteFile(entry);
        }
        Directory.Delete(path);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    internal static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        const int oDirectory = 0x10000;
        const int oCloexec = 0x80000;
        var handle = Open(path, oDirectory | oCloexec);
        if (handle < 0)
            throw new IOException("Không mở được thư mục cập nhật để đồng bộ.", new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (Fsync(handle) != 0)
                throw new IOException("Không đồng bộ được thư mục cập nhật.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally
        {
            _ = Close(handle);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string source, string destination, int flags);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int handle);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int handle);
}
