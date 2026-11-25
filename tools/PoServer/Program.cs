namespace PoServer;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var prefix = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0] : "http://127.0.0.1:5080/";
        var dbPath = args.Length > 1 ? Path.GetFullPath(args[1])
            : Path.Combine(AppContext.BaseDirectory, "logs", "po-server.db");
        await using var server = new PoConsoleServer(prefix, dbPath);
        server.Start();
        Console.WriteLine($"PO test server: {prefix}");
        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine("Import a CSV/JSON/XLSX file with one CodeContent column at POST /api/po.");
        Console.WriteLine("GET /api/po, GET /api/po/{orderNo}/codes, POST /api/activate; Ctrl+C to stop.");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopped.TrySetResult(); };
        await stopped.Task;
    }
}
