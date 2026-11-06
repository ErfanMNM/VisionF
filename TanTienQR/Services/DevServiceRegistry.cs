using Microsoft.Extensions.DependencyInjection;

namespace TanTienQR.Services;

/// <summary>
/// Ảnh chụp danh sách service đã đăng ký trong DI container.
///
/// Dùng cho tab "Dev" để xem toàn bộ service mà app đang chạy, phục vụ việc
/// chẩn đoán: service nào bị đăng ký 2 lần, lifetime gì, implementation ra sao.
///
/// Danh sách được chụp từ <see cref="ServiceDescriptor"/> — KHÔNG dùng
/// reflection scan assembly nên vẫn an toàn với Native AOT.
/// </summary>
internal sealed class DevServiceRegistry
{
    /// <summary>
    /// Danh sách descriptor của các service đã đăng ký.
    /// </summary>
    public IReadOnlyList<ServiceDescriptor> Descriptors { get; }

    /// <summary>
    /// Khởi tạo registry với danh sách descriptor đã chụp.
    /// </summary>
    /// <param name="descriptors">Danh sách service đã đăng ký.</param>
    public DevServiceRegistry(IReadOnlyList<ServiceDescriptor> descriptors)
    {
        Descriptors = descriptors;
    }

    /// <summary>
    /// Tên hiển thị của lớp implementation, không dùng reflection sinh động:
    /// đọc thẳng từ descriptor đã đăng ký.
    /// </summary>
    /// <param name="descriptor">Descriptor cần mô tả.</param>
    /// <returns>Tên implementation hoặc chuỗi mô tả dạng factory.</returns>
    public static string DescribeImplementation(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is not null)
        {
            return descriptor.ImplementationType.Name;
        }

        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance.GetType().Name;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return "factory";
        }

        return "—";
    }
}
