using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DraftOptimizer.Core;

public static class ErrorLog
{
    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer", "logs");

    public static string? Write(string operation, Exception error, [CallerMemberName] string member = "")
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var timestamp = DateTimeOffset.UtcNow;
            var path = Path.Combine(DirectoryPath, $"error-{timestamp:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
            var text = new StringBuilder()
                .AppendLine($"Fecha UTC: {timestamp:O}")
                .AppendLine($"Fecha local: {DateTimeOffset.Now:O}")
                .AppendLine($"Operación: {operation}")
                .AppendLine($"Método: {member}")
                .AppendLine($"Versión: {typeof(ErrorLog).Assembly.GetName().Version}")
                .AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}")
                .AppendLine($"Arquitectura: {RuntimeInformation.ProcessArchitecture}")
                .AppendLine()
                .AppendLine(error.ToString())
                .ToString();
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Debug.WriteLine($"No se pudo escribir el log: {failure.Message}");
            return null;
        }
    }
}
