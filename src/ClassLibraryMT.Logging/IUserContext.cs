namespace ClassLibraryMT.Logging;

/// <summary>
/// Thông tin người dùng đang thao tác, dùng để ghi vào nhật ký thao tác.
///
/// Interface này nằm trong lib Logging (không phải lib Auth) để lib Logging
/// không phụ thuộc vào lib Auth — tránh phụ thuộc vòng. Ứng dụng muốn ghi
/// tên người dùng vào log chỉ cần implement interface này và đăng ký vào DI.
///
/// Khi không có ai đăng nhập, service có thể bỏ qua interface này (đăng ký
/// nullable) — các bản ghi sẽ có <c>Username</c> rỗng.
/// </summary>
public interface IUserContext
{
    /// <summary>Tên đăng nhập của người dùng hiện tại, null nếu chưa đăng nhập.</summary>
    string? Username { get; }

    /// <summary>Khoá chính của người dùng hiện tại, null nếu chưa đăng nhập.</summary>
    long? UserId { get; }
}
