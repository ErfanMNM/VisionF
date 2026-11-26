namespace TanTienQR.Services;

/// <summary>Manual intake and always-on PDA listener (section ManualActivation).</summary>
internal sealed class ManualActivationOptions
{
    public int QueueCapacity { get; set; } = 64;
    public string HistoryPath { get; set; } = "logs/manual-activation.db";
    public string HttpPrefix { get; set; } = "http://+:18080/";
    public int MaxCodeLength { get; set; } = 4096;
    public int MaxBodyBytes { get; set; } = 16384;
    public int MaxConcurrentRequests { get; set; } = 16;
    public int RequestTimeoutSeconds { get; set; } = 15;

    public string ResolveHistoryPath() => Path.GetFullPath(Path.IsPathRooted(HistoryPath)
        ? HistoryPath : Path.Combine(AppContext.BaseDirectory, HistoryPath));
}
