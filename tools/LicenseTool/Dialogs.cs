using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace LicenseTool;

// Helper mo hop thoai chon file/thu muc tu ViewModel ma khong phu thuoc MainWindow cu the.
internal static class FileDialogs
{
    private static TopLevel? TopLevel =>
        (App.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static async Task<string?> OpenFileAsync(string title, string filter)
    {
        var tl = TopLevel;
        if (tl?.StorageProvider is null) return null;
        var files = await tl.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilter(filter),
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    public static async Task<string?> PickFolderAsync(string title)
    {
        var tl = TopLevel;
        if (tl?.StorageProvider is null) return null;
        var folders = await tl.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private static IReadOnlyList<Avalonia.Platform.Storage.FilePickerFileType> ParseFilter(string filter)
    {
        // "Mo ta (*.a;*.b);*.a;*.b|Mo ta 2..."
        var list = new List<Avalonia.Platform.Storage.FilePickerFileType>();
        foreach (var part in filter.Split('|'))
        {
            var seg = part.Split(';');
            var name = seg[0];
            var patterns = seg.Skip(1).ToArray();
            list.Add(new Avalonia.Platform.Storage.FilePickerFileType(name)
            {
                Patterns = patterns.Length > 0 ? patterns : ["*.*"]
            });
        }
        return list;
    }
}

internal static class ClipboardHelper
{
    public static async Task SetTextAsync(string text)
    {
        var tl = (App.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (tl?.Clipboard is not null)
            await tl.Clipboard.SetTextAsync(text ?? "");
    }

    public static async Task<string?> GetTextAsync()
    {
        var tl = (App.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (tl?.Clipboard is null) return null;
        return await tl.Clipboard.TryGetTextAsync();
    }
}
