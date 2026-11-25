using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TanTienQR.ViewModels;

namespace TanTienQR.Controls;

/// <summary>Vùng HWND thật; tỷ lệ và kích thước được panel ngoài tính bằng layout.</summary>
public sealed partial class PrinterRemoteHost : NativeControlHost
{
    public static readonly StyledProperty<PrinterRemoteViewModel?> RemoteProperty = AvaloniaProperty.Register<PrinterRemoteHost, PrinterRemoteViewModel?>(nameof(Remote));
    public PrinterRemoteViewModel? Remote { get => GetValue(RemoteProperty); set => SetValue(RemoteProperty, value); }
    private nint _handle;

    public PrinterRemoteHost() { IsVisible = false; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != RemoteProperty) return;
        if (change.OldValue is PrinterRemoteViewModel previous)
        {
            previous.PropertyChanged -= OnRemoteChanged;
            if (_handle != 0) previous.DetachSurface(_handle);
        }
        if (Remote is { } current)
        {
            current.PropertyChanged += OnRemoteChanged;
            if (_handle != 0) current.AttachSurface(_handle);
        }
        UpdateVisibility();
    }

    private void OnRemoteChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(PrinterRemoteViewModel.IsSurfaceVisible)) UpdateVisibility(); }
    private void UpdateVisibility()
    {
        IsVisible = Remote?.IsSurfaceVisible == true;
        // Ẩn ngay HWND trước khi Avalonia hoàn tất vòng layout cho overlay.
        if (_handle != 0 && !IsVisible && OperatingSystem.IsWindows()) ShowWindow(_handle, 0);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows() || parent.HandleDescriptor != "HWND") return base.CreateNativeControlCore(parent);
        _handle = CreateWindowEx(0, "STATIC", "", 0x40000000 | 0x02000000 | 0x04000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không tạo được khung màn hình máy in.");
        Remote?.AttachSurface(_handle);
        UpdateVisibility();
        return new PlatformHandle(_handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (!OperatingSystem.IsWindows() || _handle == 0 || control.Handle != _handle) { base.DestroyNativeControlCore(control); return; }
        Remote?.DetachSurface(control.Handle);
        DestroyWindow(control.Handle);
        _handle = 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int command);
}
