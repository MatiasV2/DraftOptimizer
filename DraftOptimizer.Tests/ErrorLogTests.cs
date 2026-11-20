using DraftOptimizer.Core;

static class ErrorLogTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var error = new InvalidOperationException("Fallo de carga de prueba", new IOException("Detalle interno"));
        var paths = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => ErrorLog.Write("Build de prueba", error))));
        check(paths.All(path => path != null && File.Exists(path)) && paths.Distinct().Count() == 2,
            "Dos errores simultáneos generan TXT distintos sin sobrescribirse");
        if (paths[0] is { } path)
        {
            string text = await File.ReadAllTextAsync(path);
            check(text.Contains("Build de prueba") && text.Contains("Detalle interno") && text.Contains("Fecha UTC") && text.Contains("Versión"),
                "El TXT incluye operación, fecha, versión y excepción interna");
        }
        foreach (var logPath in paths.Where(value => value != null)) File.Delete(logPath!);
    }
}
