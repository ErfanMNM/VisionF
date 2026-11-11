using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;

namespace UiAuditHarness.Audit;

/// <summary>
/// Truy cập các type / thành viên <c>internal</c> của assembly TanTienQR
/// (MainViewModel, LoginViewModel, ...) qua reflection.
///
/// Lý do: các ViewModel đó được khai báo <c>internal</c> trong app, còn
/// harness là assembly riêng và KHÔNG được sửa file nguồn của app.
/// </summary>
internal static class Reflect
{
    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Lấy type theo tên đầy đủ từ assembly TanTienQR.</summary>
    public static Type? GetType(string fullName)
    {
        var asm = typeof(TanTienQR.App).Assembly;
        return asm.GetType(fullName, throwOnError: false);
    }

    /// <summary>Lấy giá trị property (kể cả internal) theo tên.</summary>
    public static object? GetProperty(object? instance, string name)
    {
        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        var prop = type.GetProperty(name, All)
            ?? throw new MissingMemberException(type.FullName, name);

        return prop.GetValue(instance);
    }

    /// <summary>Đọc property bool (mặc định false khi lỗi).</summary>
    public static bool GetBool(object? instance, string name)
    {
        try
        {
            return GetProperty(instance, name) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Thực thi một <c>ICommand.Execute</c> lấy từ property.</summary>
    public static void ExecuteCommand(object? instance, string propertyName)
    {
        var cmd = GetCommand(instance, propertyName);
        cmd.Execute(null);
    }

    /// <summary>Thực thi một lệnh (kể cả bản async) lấy từ property.</summary>
    public static Task ExecuteCommandAsync(object? instance, string propertyName)
    {
        var cmd = GetCommand(instance, propertyName);

        // CommunityToolkit.Mvvm: IAsyncRelayCommand nằm trong namespace
        // CommunityToolkit.Mvvm.Input — dùng duck-typing để không cần reference.
        var asyncProp = cmd.GetType().GetProperty("ExecutionTask");
        if (asyncProp is not null && cmd.GetType().GetMethod("ExecuteAsync") is not null)
        {
            cmd.Execute(null);

            var task = asyncProp.GetValue(cmd) as Task;
            if (task is not null)
            {
                return task;
            }

            return Task.CompletedTask;
        }

        cmd.Execute(null);
        return Task.CompletedTask;
    }

    private static ICommand GetCommand(object? instance, string propertyName)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        var cmd = GetProperty(instance, propertyName) as ICommand;
        if (cmd is null)
        {
            throw new MissingMemberException(
                instance.GetType().FullName, propertyName + " (ICommand)");
        }

        return cmd;
    }

    /// <summary>Gọi một method không tham số trên instance.</summary>
    public static object? Invoke(object? instance, string methodName, params object?[] args)
    {
        if (instance is null)
        {
            return null;
        }

        var type = instance.GetType();
        var method = type.GetMethod(methodName, All)
            ?? throw new MissingMethodException(type.FullName, methodName);

        return method.Invoke(instance, args);
    }
}
