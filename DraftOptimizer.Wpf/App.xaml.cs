using System;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace DraftOptimizer;

public partial class App : Application
{
    internal PortraitImageLoader Portraits { get; } = new(
        new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer", "cache", "portraits"));

    protected override void OnExit(ExitEventArgs e)
    {
        Portraits.Dispose();
        base.OnExit(e);
    }

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "ui-errors.log"), $"{DateTimeOffset.UtcNow:O} {e.Exception}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Preserve the original dispatcher exception.
            }
        };
    }
}
