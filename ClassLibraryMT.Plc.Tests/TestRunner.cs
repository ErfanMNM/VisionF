namespace ClassLibraryMT.Plc.Tests;

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

    public static void AreClose(double expected, double actual, double tolerance, string what = "")
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new Exception($"{what} mong doi <{expected}> +/- {tolerance} nhung nhan <{actual}>.");
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
