using System.Net;

namespace DraftOptimizer.Core;

public static class AutomaticRetry
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        for (int retry = 0; ; retry++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await operation(ct); }
            catch (Exception ex) when (retry < 3 && !ct.IsCancellationRequested && IsTransient(ex))
            {
                ErrorLog.Write($"Reintento automático {retry + 1}/3", ex);
                await Task.Delay(TimeSpan.FromSeconds(1 << retry), ct);
            }
        }
    }

    public static Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct) =>
        RunAsync(async token => { await operation(token); return true; }, ct);

    private static bool IsTransient(Exception error) => error switch
    {
        HttpRequestException request => request.StatusCode is null or HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests || (int)request.StatusCode.Value >= 500,
        OperationCanceledException => true,
        IOException when error is not InvalidDataException => true,
        _ => false
    };
}
