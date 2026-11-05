using Avalonia.Controls;
using Avalonia.Controls.Templates;
using TanTienQR.ViewModels;
using TanTienQR.Views;

namespace TanTienQR;

/// <summary>
/// Ánh xạ ViewModel sang View tương ứng bằng <c>switch</c> tĩnh.
///
/// KHÔNG dùng <c>Type.GetType</c> + <c>Activator.CreateInstance</c> như bản
/// mẫu mặc định của Avalonia: hai API đó dùng reflection nên khi publish
/// Native AOT sẽ sinh cảnh báo IL2026/IL3050 và view có thể bị trim mất.
/// Bản này chỉ tham chiếu trực tiếp tới các lớp View nên AOT-safe.
/// </summary>
public class ViewLocator : IDataTemplate
{
    /// <summary>Tạo View tương ứng với ViewModel, hoặc <c>null</c> nếu không có.</summary>
    public Control? Build(object? param) => param switch
    {
        MainViewModel => new InfoView(),
        _ => null,
    };

    /// <summary>True nếu dữ liệu là ViewModel mà locator này hỗ trợ.</summary>
    public bool Match(object? data) => data is MainViewModel;
}
