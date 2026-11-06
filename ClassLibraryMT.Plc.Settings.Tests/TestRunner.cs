namespace ClassLibraryMT.Plc.Settings.Tests;

/// <summary>Bo test don gian, khong can package ngoai.</summary>
internal static class TestRunner
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = [];

    public static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            var message = $"{name}: {ex.Message}";
            Failures.Add(message);
            Console.WriteLine($"  [FAIL] {message}");
        }
    }

    /// <summary>Chạy test bất đồng bộ (bọc lỗi theo từng test).</summary>
    public static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            var message = $"{name}: {ex.Message}";
            Failures.Add(message);
            Console.WriteLine($"  [FAIL] {message}");
        }
    }

    public static void AreEqual<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} mong doi <{expected}> nhung nhan <{actual}>.");
    }

    public static void IsTrue(bool condition, string what = "")
    {
        if (!condition) throw new Exception($"{what} mong doi true.");
    }

    public static void IsFalse(bool condition, string what = "")
    {
        if (condition) throw new Exception($"{what} mong doi false.");
    }

    public static void IsNotNull(object? value, string what = "")
    {
        if (value is null) throw new Exception($"{what} khong duoc null.");
    }

    public static void IsNull(object? value, string what = "")
    {
        if (value is not null) throw new Exception($"{what} phai null.");
    }

    public static void Contains(string haystack, string needle, string what = "")
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new Exception($"{what} chua chua '{needle}'. Noi dung: {haystack}");
    }

    public static void DoesNotContain(string haystack, string needle, string what = "")
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
            throw new Exception($"{what} khong duoc chua '{needle}'. Noi dung: {haystack}");
    }

    public static int Summary()
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        if (_failed == 0)
        {
            Console.WriteLine($"TAT CA PASS: {_passed} test.");
            return 0;
        }

        Console.WriteLine($"{_passed} pass, {_failed} FAIL:");
        foreach (var f in Failures) Console.WriteLine($"  - {f}");
        return 1;
    }
}
