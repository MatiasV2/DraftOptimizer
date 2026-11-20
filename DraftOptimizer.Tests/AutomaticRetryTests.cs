using DraftOptimizer.Core;
using System.Net;

static class AutomaticRetryTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        int attempts = 0;
        var result = await AutomaticRetry.RunAsync(_ =>
        {
            if (++attempts < 2) throw new HttpRequestException("Temporal", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(42);
        }, default);
        check(attempts == 2 && result == 42, "Una carga temporalmente fallida se recupera con un reintento automático");

        attempts = 0;
        try
        {
            await AutomaticRetry.RunAsync<int>(_ =>
            {
                attempts++;
                throw new HttpRequestException("Sin servicio", null, HttpStatusCode.ServiceUnavailable);
            }, default);
        }
        catch (HttpRequestException) { check(attempts == 4, "Tras tres reintentos se entrega el error para mostrar el popup"); }

        attempts = 0;
        try
        {
            await AutomaticRetry.RunAsync<int>(_ => { attempts++; throw new InvalidDataException("Perfil inválido"); }, default);
        }
        catch (InvalidDataException) { check(attempts == 1, "Un perfil inválido no provoca reintentos de red"); }

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(100);
        attempts = 0;
        try
        {
            await AutomaticRetry.RunAsync<int>(_ => { attempts++; throw new HttpRequestException("Sin conexión"); }, cancellation.Token);
        }
        catch (OperationCanceledException) { check(attempts == 1, "Cambiar de campeón o cerrar la app cancela los reintentos pendientes"); }
    }
}
